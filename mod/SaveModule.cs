using CasuMp.Protocol;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Collections.Concurrent;
using System.Reflection;
using HarmonyLib;
using Together;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace CasuMod;

public static class SaveModule
{
    internal static readonly ConcurrentDictionary<string, JObject> PendingData = new(); // RESUME/RESPONSE 데이터

    private static readonly ConcurrentDictionary<string, (float X, float Y, int Layer)> PendingPositions = new(); // 스폰 위치

    private static readonly ConcurrentDictionary<string, JArray> PendingDrops = new(); // 퇴장 시 드랍 아이템
    private static readonly ConcurrentDictionary<string, long> DropSeq = new();

    private static long _seq; // 도착 게이트용 단조 시퀀스
    private static readonly ConcurrentDictionary<string, long> PendingSeq = new();
    private static readonly ConcurrentDictionary<string, long> PositionSeq = new();
    private static readonly ConcurrentDictionary<string, long> ArrivalSeq = new();

    private const float FloorCheckDistance = 3f;

    internal static void OnPlayerArrival(string persistentId)
    {
        ArrivalSeq[persistentId] = Interlocked.Increment(ref _seq); // 직전 방문 데이터 무효화
        PendingData.TryRemove(persistentId, out _);
        PendingSeq.TryRemove(persistentId, out _);
        PositionSeq.TryRemove(persistentId, out _);
        PendingPositions.TryRemove(persistentId, out _);
        DropSeq.TryRemove(persistentId, out _);
        PendingDrops.TryRemove(persistentId, out _);
    }

    internal static void SetPending(string persistentId, JObject data)
    {
        PendingData[persistentId] = data;
        PendingSeq[persistentId] = Interlocked.Increment(ref _seq);
    }

    internal static void RemovePending(string persistentId)
    {
        PendingData.TryRemove(persistentId, out _);
        PendingSeq.TryRemove(persistentId, out _);
    }

    private static bool TryTakePending(string persistentId, out JObject data)
    {
        if (!ArrivalSeq.TryGetValue(persistentId, out long arrival)) arrival = long.MinValue;
        long seq = 0;
        bool has = PendingData.TryGetValue(persistentId, out data);
        bool fresh = has && PendingSeq.TryGetValue(persistentId, out seq) && seq > arrival; // 도착 이후 푸시만 소비
        if (fresh)
        {
            PendingData.TryRemove(persistentId, out _);
            PendingSeq.TryRemove(persistentId, out _);
            return true;
        }
        PendingData.TryRemove(persistentId, out _);
        PendingSeq.TryRemove(persistentId, out _);
        data = null;
        return false;
    }

    private static bool TryTakePendingPosition(string persistentId, out (float X, float Y, int Layer) pos)
    {
        if (!ArrivalSeq.TryGetValue(persistentId, out long arrival)) arrival = long.MinValue;
        long seq = 0;
        bool has = PendingPositions.TryGetValue(persistentId, out pos);
        bool fresh = has && PositionSeq.TryGetValue(persistentId, out seq) && seq > arrival; // 스테일 위치 스킵
        if (fresh)
        {
            PendingPositions.TryRemove(persistentId, out _);
            PositionSeq.TryRemove(persistentId, out _);
            return true;
        }
        PendingPositions.TryRemove(persistentId, out _);
        PositionSeq.TryRemove(persistentId, out _);
        return false;
    }

    public static JObject SerializePlayer(ScavPlayer plr)
    {
        Body body = plr.body;
        var root = new JObject // 바디/스킬/림브/컴포넌트/인벤토리/위치 전체 직렬화
        {
            ["version"] = 1,
            ["body"] = SerializeFields(body),
            ["skills"] = body.skills != null ? SerializeFields(body.skills) : new JObject(),
            ["limbs"] = new JArray(body.limbs.Select(SerializeFields)),
            ["bodyComponents"] = SerializeComponents(body.gameObject),
            ["limbComponents"] = new JArray(body.limbs.Select(l => l != null ? SerializeComponents(l.gameObject) : new JObject())),
            ["inventory"] = SerializeInventory(body),
            ["droppedItems"] = SerializeDroppedItems(body),
            ["savedRecipeData"] = SerializeRecipes(),
            ["charIdentity"] = SerializeCharIdentity(),
            ["lastHappiness"] = body.lastHappiness != null ? new JArray(body.lastHappiness) : new JArray(),
            ["caloriesConsumed"] = PlayerCamera.main != null ? PlayerCamera.main.caloriesConsumed : 0,
            ["position"] = new JObject
            {
                ["x"] = body.transform.position.x,
                ["y"] = body.transform.position.y,
                ["layer"] = WorldGeneration.world != null ? WorldGeneration.world.biomeDepth : 0,
            },
        };
        return root;
    }

    public static void SubmitPlayer(ScavPlayer plr)
    {
        if (plr == null || plr.body == null) return;
        try
        {
            JObject data = SerializePlayer(plr);
            OrchestratorClient.Instance?.SendEvent(MessageTypes.PlayerDataSubmit, // 직렬화 후 제출
                new { playerKey = MigrationModule.PlayerKeyOf(plr), payload = data });
        }
        catch (System.Exception ex)
        {
            Plugin.Log.LogWarning($"[Save] {plr.playerName} 직렬화 실패: {ex.Message}");
        }
    }

    internal static void HandlePlayerDataResponse(ControlMessage msg)
    {
        string playerKey = msg.PayloadAs<PlayerKeyPayload>()?.PlayerKey ?? "";
        JToken payload = msg.Inner("payload");
        if (playerKey == "")
        {
            Plugin.Log.LogWarning($"[Save] PLAYER_DATA_RESPONSE 형식 오류: {playerKey}");
            return;
        }
        if (payload == null || payload.Type != JTokenType.Object)
        {
            RemovePending(playerKey); // 데이터 없음 - 잔존 엔트리 정리
            return;
        }
        SetPending(playerKey, (JObject)payload);
    }

    internal static void RestorePlayer(Body body, ScavPlayer plr, JObject root)
    {
        try
        {
            RestoreBodyFields(body, root["body"]); // 바디/림브/인벤토리/위치 복원
            RestoreSkills(body, root["skills"]);
            RestoreLimbs(body, root["limbs"]);            ApplyComponents(body.gameObject, root["bodyComponents"]);
            ApplyLimbComponents(body, root["limbComponents"]);
            RestoreInventory(body, root["inventory"]);
            RestoreRecipes(plr, root["savedRecipeData"]);
            RestoreCharIdentity(root["charIdentity"]);
            RestoreMisc(root);
            QueuePosition(body, plr, root["position"]);
            QueueDroppedItems(plr, root["droppedItems"]);
        }
        catch (System.Exception ex)
        {
            Plugin.Log.LogWarning($"[Save] {plr.playerName} 복원 실패: {ex.Message}");
        }
    }

    private static readonly HashSet<string> TransientFieldBlacklist = new() // transient - 복원 시 제외
    {
        "tempCheckTime",
        "temperature",
        "wetness",
        "clothingTemperature",
    };

    private static bool IsSavableType(Type t) =>
        t == typeof(string) || t == typeof(float[]) || t == typeof(int[]) || t == typeof(bool[])
        || t == typeof(string[])
        || (t.IsValueType && !typeof(UnityEngine.Object).IsAssignableFrom(t));

    private static JObject SerializeFields(object obj)
    {
        var result = new JObject();
        foreach (FieldInfo f in obj.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            if (TransientFieldBlacklist.Contains(f.Name))
                continue;
            if (IsSavableType(f.FieldType))
            {
                try { result[f.Name] = JToken.FromObject(f.GetValue(obj)); }
                catch { }
            }
            else if (f.IsDefined(typeof(JsonPropertyAttribute), false)
                     && !typeof(UnityEngine.Object).IsAssignableFrom(f.FieldType))
            {
                try { result[f.Name] = JToken.FromObject(f.GetValue(obj)); }
                catch { }
            }
        }
        return result;
    }

    private static void RestoreFields(object target, JToken token)
    {
        if (token is not JObject fields) return;
        Type type = target.GetType();
        foreach (JProperty prop in fields.Properties())
        {
            if (TransientFieldBlacklist.Contains(prop.Name))
                continue; // 블랙리스트 필드가 남아 있는 저장 데이터는 복원 스킵
            FieldInfo f = type.GetField(prop.Name, BindingFlags.Public | BindingFlags.Instance);
            if (f == null) continue;
            if (IsSavableType(f.FieldType))
            {
                try { f.SetValue(target, prop.Value.ToObject(f.FieldType)); }
                catch { }
            }
            else if (f.IsDefined(typeof(JsonPropertyAttribute), false)
                     && !typeof(UnityEngine.Object).IsAssignableFrom(f.FieldType))
            {
                try { f.SetValue(target, prop.Value.ToObject(f.FieldType)); }
                catch { }
            }
        }
    }

    private static void RestoreBodyFields(Body body, JToken token) => RestoreFields(body, token);

    private static void RestoreSkills(Body body, JToken token)
    {
        if (body.skills == null) body.skills = new Skills();
        RestoreFields(body.skills, token);
    }

    private static void RestoreLimbs(Body body, JToken token)
    {
        if (token is not JArray arr || body.limbs == null) return;
        for (int i = 0; i < body.limbs.Length && i < arr.Count; i++)
        {
            if (body.limbs[i] != null)
                RestoreFields(body.limbs[i], arr[i]);
        }
    }

    private static JObject SerializeComponents(GameObject go)
    {
        var result = new JObject();
        foreach (MonoBehaviour mb in go.GetComponents<MonoBehaviour>())
        {
            if (mb == null) continue;
            Type t = mb.GetType();
            if (t.GetCustomAttributes(typeof(Saveable), false).Length == 0) continue;
            result[t.FullName] = SerializeFields(mb);
        }
        return result;
    }

    private static void ApplyComponents(GameObject go, JToken token)
    {
        if (token is not JObject comps) return;
        foreach (JProperty prop in comps.Properties())
        {
            Type t = ResolveType(prop.Name);
            if (t == null) continue;
            Component c = go.GetComponent(t);
            if (c == null)
            {
                if (!typeof(Component).IsAssignableFrom(t) || !t.IsSubclassOf(typeof(MonoBehaviour))) continue;
                try { c = go.AddComponent(t); }
                catch (System.Exception ex)
                {
                    Plugin.Log.LogWarning($"[Save] 컴포넌트 추가 실패 ({prop.Name}): {ex.Message}");
                    continue;
                }
            }
            RestoreFields(c, prop.Value);
        }
    }

    private static void ApplyLimbComponents(Body body, JToken token)
    {
        if (token is not JArray arr || body.limbs == null) return;
        for (int i = 0; i < body.limbs.Length && i < arr.Count; i++)
        {
            if (body.limbs[i] != null)
                ApplyComponents(body.limbs[i].gameObject, arr[i]);
        }
    }

    private static Type ResolveType(string name)
    {
        Type t = Type.GetType(name);
        if (t != null) return t;
        foreach (System.Reflection.Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            t = asm.GetType(name);
            if (t != null) return t;
        }
        return null;
    }

    private static JArray SerializeInventory(Body body)
    {
        var items = new JArray();
        if (body.slots != null)
        {
            for (int i = 0; i < body.slots.Length; i++)
            {
                Item item = body.GetItem(i);
                if (item == null) continue;
                items.Add(BuildItemEntry(item, i, null));
            }
        }
        foreach (Item w in body.GetAllWearables())
        {
            if (w == null) continue;
            string wearSlot = w.Stats != null ? w.Stats.wearSlotId : "";
            items.Add(BuildItemEntry(w, -1, wearSlot));
        }
        return items;
    }

    private static JObject BuildItemEntry(Item item, int slot, string wearSlot)
    {
        var e = new JObject
        {
            ["id"] = item.id,
            ["condition"] = item.condition,
            ["favourited"] = item.favourited,
            ["components"] = SerializeComponents(item.gameObject),
        };
        if (slot >= 0) e["slot"] = slot;
        else e["wearSlot"] = wearSlot ?? "";

        var contents = new JArray();
        foreach (Transform child in item.transform)
        {
            Item ci = child.GetComponent<Item>();
            if (ci == null) continue;
            contents.Add(BuildItemEntry(ci, -1, null));
        }
        if (contents.Count > 0) e["contents"] = contents;
        return e;
    }

    private const float DropItemCaptureRadius = 10f;

    private static JArray SerializeDroppedItems(Body body)
    {
        var items = new JArray();
        if (body == null) return items;

        Collider2D[] hits;
        try
        {
            hits = Physics2D.OverlapCircleAll(body.transform.position, DropItemCaptureRadius);
        }
        catch (System.Exception ex)
        {
            Plugin.Log.LogWarning($"[Save] 드랍 아이템 수집 실패: {ex.Message}");
            return items;
        }

        foreach (Collider2D hit in hits)
        {
            if (hit == null) continue;
            Item item = hit.GetComponent<Item>();
            if (item == null) continue;
            if (item.transform.parent != null) continue;

            try
            {
                var e = new JObject
                {
                    ["id"] = item.id,
                    ["condition"] = item.condition,
                    ["favourited"] = item.favourited,
                    ["components"] = SerializeComponents(item.gameObject),
                    ["x"] = item.transform.position.x,
                    ["y"] = item.transform.position.y,
                };

                var contents = new JArray();
                foreach (Transform child in item.transform)
                {
                    Item ci = child.GetComponent<Item>();
                    if (ci == null) continue;
                    contents.Add(BuildItemEntry(ci, -1, null));
                }
                if (contents.Count > 0) e["contents"] = contents;

                items.Add(e);

                NetObjectRegistry.SafeDestroyObject(item.gameObject);
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogWarning($"[Save] 드랍 아이템 '{item.id}' 직렬화 실패: {ex.Message}");
            }
        }
        return items;
    }

    private static void RestoreInventory(Body body, JToken token)
    {
        if (token is not JArray items) return;
        foreach (JObject entry in items.OfType<JObject>())
        {
            try
            {
                Item item = CreateItem(body, entry);
                if (item == null) continue;

                string wearSlot = entry.Value<string>("wearSlot");
                int slot = entry.Value<int?>("slot") ?? -1;

                if (slot >= 0)
                {
                    Item existing = body.GetItem(slot);
                    if (existing != null && existing.GetComponent<Container>() is Container c && c.CanHoldItem(item))
                    {
                        c.LoadItem(item);
                    }
                    else
                    {
                        body.PickUpItem(item, slot, force: true);
                    }
                }
                else if (wearSlot != null && wearSlot != "")
                {
                    body.AutoPickUpItem(item);
                }
                else
                {
                    body.AutoPickUpItem(item);
                }
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogWarning($"[Save] '{entry.Value<string>("id")}' 복원 실패: {ex.Message}");
            }
        }
    }

    private static Item CreateItem(Body body, JObject entry)
    {
        string id = entry.Value<string>("id");
        if (string.IsNullOrEmpty(id)) return null;
        GameObject obj = Utils.Create(id, body.transform.position, 0f);
        if (obj == null) return null;
        Item item = obj.GetComponent<Item>();
        if (item == null) { UnityEngine.Object.Destroy(obj); return null; }

        item.condition = entry.Value<float?>("condition") ?? 1f;
        item.favourited = entry.Value<bool?>("favourited") ?? false;
        ApplyComponents(obj, entry["components"]);

        if (entry["contents"] is JArray contents && contents.Count > 0
            && item.GetComponent<Container>() is Container container)
        {
            foreach (JObject childEntry in contents.OfType<JObject>())
            {
                Item child = CreateItem(body, childEntry);
                if (child != null) container.LoadItem(child);
            }
        }
        return item;
    }

    private static JObject SerializeRecipes()
    {
        var saved = new JArray();
        if (Recipes.recipes != null)
        {
            foreach (var r in Recipes.recipes)
            {
                saved.Add(new JObject { ["hasMadeBefore"] = r.hasMadeBefore, ["INT"] = r.INT });
            }
        }
        return new JObject { ["saved"] = saved };
    }

    private static void RestoreRecipes(ScavPlayer plr, JToken token)
    {
        if (token?["saved"] is not JArray saved || Recipes.recipes == null) return;
        for (int i = 0; i < saved.Count && i < Recipes.recipes.Count; i++)
        {
            Recipes.recipes[i].hasMadeBefore = saved[i].Value<bool?>("hasMadeBefore") ?? false;
            Recipes.recipes[i].INT = saved[i].Value<int?>("INT") ?? 0;
        }
    }

    private static JObject SerializeCharIdentity()
    {
        int[] c = WoundView.view != null ? WoundView.view.cInfo : null;
        if (c == null)
        {
            Plugin.Log.LogWarning("[Save] WoundView.view 없음 — charIdentity를 기본값(0)으로 저장.");
            c = new int[4];
        }
        return new JObject
        {
            ["height"] = c.Length > 0 ? c[0] : 0,
            ["age"] = c.Length > 1 ? c[1] : 0,
            ["id"] = c.Length > 2 ? c[2] : 0,
            ["ver"] = c.Length > 3 ? c[3] : 0,
        };
    }

    private static void RestoreCharIdentity(JToken token)
    {
        if (token is not JObject id) return;
        if (WoundView.view == null)
        {
            return;
        }
        try
        {
            int h = id.Value<int?>("height") ?? 0;
            int a = id.Value<int?>("age") ?? 0;
            int cid = id.Value<int?>("id") ?? 0;
            int ver = id.Value<int?>("ver") ?? 0;
            WoundView.view.SetCharDetails(h, a, cid, ver);
        }
        catch { }
    }

    private static void RestoreMisc(JObject root)
    {
        if (root["lastHappiness"] is JArray lh)
        {
        }
        if (PlayerCamera.main != null && root["caloriesConsumed"] is JValue cc)
        {
            PlayerCamera.main.caloriesConsumed = cc.Value<int>();
        }
    }

    private static void QueuePosition(Body body, ScavPlayer plr, JToken token)
    {
        if (token is not JObject pos) return;
        string pid = MigrationModule.PlayerKeyOf(plr);
        PendingPositions[pid] = (
            pos.Value<float?>("x") ?? 0f,
            pos.Value<float?>("y") ?? 0f,
            pos.Value<int?>("layer") ?? 0);
        PositionSeq[pid] = Interlocked.Increment(ref _seq);
    }

    private static void QueueDroppedItems(ScavPlayer plr, JToken token)
    {
        if (token is not JArray drops) return;
        string pid = MigrationModule.PlayerKeyOf(plr);
        PendingDrops[pid] = new JArray(drops);
        DropSeq[pid] = Interlocked.Increment(ref _seq);
    }

    private static bool TryTakePendingDrops(string persistentId, out JArray drops)
    {
        drops = null;
        if (!ArrivalSeq.TryGetValue(persistentId, out long arrival)) arrival = long.MinValue;
        if (!PendingDrops.TryGetValue(persistentId, out var queued)) return false;
        if (!DropSeq.TryGetValue(persistentId, out long seq) || seq <= arrival)
        {
            PendingDrops.TryRemove(persistentId, out _);
            DropSeq.TryRemove(persistentId, out _);
            return false;
        }
        PendingDrops.TryRemove(persistentId, out _);
        DropSeq.TryRemove(persistentId, out _);
        drops = queued;
        return drops != null && drops.Count > 0;
    }

    internal static bool TryApplyPendingPosition(ScavPlayer plr, NetBody pb)
    {
        if (plr == null || pb == null || WorldGeneration.world == null) return false;
        if (!TryTakePendingPosition(MigrationModule.PlayerKeyOf(plr), out var p)) return false;

        if (p.Layer != WorldGeneration.world.biomeDepth) // 레이어 불일치(마이그레이션) -> 기본 스폰
        {
            return false;
        }

        Vector2 pos = new Vector2(
            Mathf.Clamp(p.X, -WorldGeneration.world.halfWidth, WorldGeneration.world.halfWidth),
            Mathf.Clamp(p.Y, -WorldGeneration.world.halfHeight, WorldGeneration.world.halfHeight));

        Vector2? safe = FindSafePosition(pos); // 막힌 위치면 주변 안전 위치 탐색
        if (safe == null)
        {
            return false;
        }

        pb.body.transform.position = safe.Value;
        plr.ServerPlayerState.did_give_spawn_location_from_a_save = true; // 바닐라 스폰 덮어쓰기 방지
        return true;
    }

    [HarmonyPatch(typeof(ServerMain), nameof(ServerMain.LateSpawnLocation))]
    internal static class ServerMain_LateSpawnLocation_SavedPositionPatch
    {
        private static bool Prefix(NetBody b)
        {
            if (b == null || b.plr == null) return true;
            if (TryApplyPendingPosition(b.plr, b))
            {
                return false; // 저장 위치 적용 -> 바닐라 계산 스킵
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(ServerMain), nameof(ServerMain.LateSpawnLocation))]
    internal static class ServerMain_LateSpawnLocation_DroppedItemsRestorePatch
    {
        private static void Postfix(NetBody b)
        {
            if (b == null || b.plr == null) return;
            if (!TryTakePendingDrops(MigrationModule.PlayerKeyOf(b.plr), out JArray drops)) return; // 스폰 확정 후 드랍 복원

            Vector2 spawnPos = b.body != null ? (Vector2)b.body.transform.position : Vector2.zero;
            foreach (JObject entry in drops.OfType<JObject>())
            {
                try
                {
                    SpawnDroppedItem(entry, spawnPos);
                }
                catch (System.Exception ex)
                {
                    Plugin.Log.LogWarning($"[Save] 드랍 아이템 '{entry.Value<string>("id")}' 복원 실패: {ex.Message}");
                }
            }
        }
    }

    private static void SpawnDroppedItem(JObject entry, Vector2 basePos)
    {
        string id = entry.Value<string>("id");
        if (string.IsNullOrEmpty(id)) return;

        Vector2 pos = basePos + UnityEngine.Random.insideUnitCircle * 0.5f;
        GameObject obj = Utils.Create(id, pos, 0f);
        if (obj == null) return;
        Item item = obj.GetComponent<Item>();
        if (item == null) { UnityEngine.Object.Destroy(obj); return; }

        if (item.rb != null)
        {
            item.rb.gravityScale = 0f;
            item.rb.velocity = Vector2.zero;
            item.rb.simulated = false;
        }

        item.condition = entry.Value<float?>("condition") ?? 1f;
        item.favourited = entry.Value<bool?>("favourited") ?? false;
        ApplyComponents(obj, entry["components"]);
        ApplyDroppedContents(item, entry["contents"]);
    }

    private static void ApplyDroppedContents(Item item, JToken token)
    {
        if (token is not JArray contents || contents.Count == 0) return;
        Container container = item.GetComponent<Container>();
        if (container == null) return;
        foreach (JObject entry in contents.OfType<JObject>())
        {
            string cid = entry.Value<string>("id");
            if (string.IsNullOrEmpty(cid)) continue;
            GameObject cobj = Utils.Create(cid, item.transform.position, 0f);
            if (cobj == null) continue;
            Item citem = cobj.GetComponent<Item>();
            if (citem == null) { UnityEngine.Object.Destroy(cobj); continue; }
            if (citem.rb != null)
            {
                citem.rb.gravityScale = 0f;
                citem.rb.simulated = false;
            }
            citem.condition = entry.Value<float?>("condition") ?? 1f;
            ApplyComponents(cobj, entry["components"]);
            container.LoadItem(citem);
        }
    }

    private static Vector2? FindSafePosition(Vector2 pos)
    {
        if (IsWalkable(pos)) return pos;

        for (int radius = 1; radius <= 8; radius++)
        {
            for (int dx = -radius; dx <= radius; dx++)
            {
                for (int dy = -radius; dy <= radius; dy++)
                {
                    if (Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dy)) != radius) continue;
                    Vector2 candidate = pos + new Vector2(dx, dy);
                    if (IsWalkable(candidate))
                    {
                        return candidate;
                    }
                }
            }
        }

        Plugin.Log.LogWarning(
            $"[Save] 저장 위치 주변(반경 8) 안전 위치 없음 — 기본 스폰으로 폴백 (x={pos.x:F0}, y={pos.y:F0})");
        return null;
    }

    private static bool IsWalkable(Vector2 pos)
    {
        try
        {
            if (Physics2D.OverlapBox(pos, Body_Awake_MultiplayerPatch.origColSize, 0f,
                LayerMask.GetMask("Ground")))
            {
                return false;
            }
            return Physics2D.Raycast(pos, Vector2.down, FloorCheckDistance,
                LayerMask.GetMask("Ground")).collider != null;
        }
        catch
        {
            return true; // 검사 불가 시 통과 (기본 스폰 경로가 안전 보장)
        }
    }

    [HarmonyPatch(typeof(Body), "Start")]
    internal static class Body_Start_RestorePatch
    {
        [HarmonyPriority(Priority.First)]
        private static void Prefix(Body __instance)
        {
            NetBody netBody = __instance.GetComponent<NetBody>();
            if (netBody == null || !netBody.IsPlayer || netBody.plr == null) return;
            ServerMain.Server_LastPlayerStates.Remove(netBody.plr.GetPersistentId());
        }

        private static void Postfix(Body __instance)
        {
            if (!ScavPlayer.BodyToPlayerDict.TryGetValue(__instance, out ScavPlayer plr)) return;
            string pid = MigrationModule.PlayerKeyOf(plr);

            if (!TryTakePending(pid, out JObject data))
            {
                DateTime deadline = DateTime.UtcNow.AddSeconds(2);
                while (DateTime.UtcNow < deadline)
                {
                    OrchestratorClient.Instance?.ProcessInbound();
                    if (TryTakePending(pid, out data)) break;
                    System.Threading.Thread.Sleep(25);
                }
            }
            if (data != null)
            {
                RestorePlayer(__instance, plr, data);
            }
            else
            {
                GrantStartingSupplies(__instance, plr); // 데이터 없음 -> 신규 보급품
            }
        }
    }

    internal static void GrantStartingSupplies(Body body, ScavPlayer plr)
    {
        if (WorldGeneration.world == null // 첫 레이어 신규 런만 지급
            || WorldGeneration.world.biomeDepth != 0
            || (int)WorldGeneration.world.biomeOverride != 0
            || SaveSystem.loadedRun)
        {
            return;
        }

        Vector2 pos = body.transform.position;
        switch (WorldGeneration.GetRunSettingInt("startingsupplies"))
        {
            case 1:
                body.PickUpItem(Utils.Create("emergencylight", pos, 0f).GetComponent<Item>(), 3, true);
                break;
            case 2:
                body.PickUpItem(Utils.Create("lantern", pos, 0f).GetComponent<Item>(), 3, true);
                body.PickUpItem(Utils.Create("dogfood", pos, 0f).GetComponent<Item>(), 4, true);
                body.PickUpItem(Utils.Create("waterbottle", pos, 0f).GetComponent<Item>(), 5, true);
                body.PickUpItem(Utils.Create("trashbag", pos, 0f).GetComponent<Item>(), 1, true);
                break;
            default:
                return;
        }

    }

    [HarmonyPatch(typeof(ScavPlayer), nameof(ScavPlayer.OnDestroy))]
    internal static class NetPlayer_OnDestroy_SubmitPatch
    {
        private static void Prefix(ScavPlayer __instance)
        {
            if (__instance == null) return;
            if (MigrationModule.IsFrozen(MigrationModule.PlayerKeyOf(__instance))) return; // 동결 중 = 캡처가 정본
            if (__instance.body != null)
            {
                SubmitPlayer(__instance);
            }
        }
    }
}
