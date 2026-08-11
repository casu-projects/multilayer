using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Collections.Concurrent;
using System.Reflection;
using HarmonyLib;
using KrokoshaCasualtiesMP;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace CasuMod;

// 플레이어 데이터 세이브/로드 — 오케스트레이터 단일 소유자 모델. 직렬화는 바닐라
// SaveSystem과 동일 메커니즘 (body/limbs + [Saveable] 컴포넌트 + 계층 인벤토리 + 레시피 + 신원).
public static class SaveModule
{
    // 플레이어 데이터 수신 대기 큐 (PLAYER_DATA_RESPONSE / RESUME → Body.Start 소비).
    internal static readonly ConcurrentDictionary<string, JObject> PendingData = new();

    private static readonly ConcurrentDictionary<string, (float X, float Y, int Layer)> PendingPositions = new();

    // 스테일 잔존물 방지 — 모든 푸시/위치 큐 쓰기는 로컬 단조 시퀀스(_seq)를 스탬프하고,
    // 접속(OnPlayerArrival) 시점의 시퀀스보다 오래된 것은 소비하지 않는다. 늦게 도착한
    // 푸시도 저장만 되고 다음 방문의 도착 시퀀스보다 작아져 자동 무력화된다.
    private static long _seq;
    private static readonly ConcurrentDictionary<string, long> PendingSeq = new();
    private static readonly ConcurrentDictionary<string, long> PositionSeq = new();
    private static readonly ConcurrentDictionary<string, long> ArrivalSeq = new();

    private const float FloorCheckDistance = 3f;

    // 접속/재접속 시 도착 시퀀스 기록 + 직전 방문 잔존물 정리 (바디 스폰보다 먼저 호출).
    internal static void OnPlayerArrival(string persistentId)
    {
        ArrivalSeq[persistentId] = Interlocked.Increment(ref _seq);
        PendingData.TryRemove(persistentId, out _);
        PendingSeq.TryRemove(persistentId, out _);
        PositionSeq.TryRemove(persistentId, out _);
        PendingPositions.TryRemove(persistentId, out _);
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

    // 도착 시퀀스 게이트가 통과한 신선 푸시만 소비.
    private static bool TryTakePending(string persistentId, out JObject data)
    {
        if (!ArrivalSeq.TryGetValue(persistentId, out long arrival)) arrival = long.MinValue;
        long seq = 0;
        bool has = PendingData.TryGetValue(persistentId, out data);
        bool fresh = has && PendingSeq.TryGetValue(persistentId, out seq) && seq > arrival;
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

    // 위치 큐도 동일 게이트.
    private static bool TryTakePendingPosition(string persistentId, out (float X, float Y, int Layer) pos)
    {
        if (!ArrivalSeq.TryGetValue(persistentId, out long arrival)) arrival = long.MinValue;
        long seq = 0;
        bool has = PendingPositions.TryGetValue(persistentId, out pos);
        bool fresh = has && PositionSeq.TryGetValue(persistentId, out seq) && seq > arrival;
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

    public static JObject SerializePlayer(NetPlayer plr)
    {
        Body body = plr.body;
        var root = new JObject
        {
            ["version"] = 1,
            ["body"] = SerializeFields(body),
            ["skills"] = body.skills != null ? SerializeFields(body.skills) : new JObject(),
            ["limbs"] = new JArray(body.limbs.Select(SerializeFields)),
            ["bodyComponents"] = SerializeComponents(body.gameObject),
            ["limbComponents"] = new JArray(body.limbs.Select(l => l != null ? SerializeComponents(l.gameObject) : new JObject())),
            ["inventory"] = SerializeInventory(body),
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

    // 플레이어 데이터 제출 (퇴장/동결) → 오케스트레이터.
    public static void SubmitPlayer(NetPlayer plr)
    {
        if (plr == null || plr.body == null) return;
        try
        {
            JObject data = SerializePlayer(plr);
            OrchestratorClient.Instance?.SendEvent("PLAYER_DATA_SUBMIT",
                new { playerKey = plr.GetPersistentId(), payload = data });
        }
        catch (System.Exception ex)
        {
            Plugin.Log.LogWarning($"[Save] {plr.playername} 직렬화 실패: {ex.Message}");
        }
    }

    // PLAYER_DATA_RESPONSE — pending 큐에 저장. payload가 없으면 잔존 엔트리를 정리한다.
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
            RemovePending(playerKey); // 데이터 없음 — 이전 잔존 엔트리 정리
            return;
        }
        SetPending(playerKey, (JObject)payload);
    }

    // 복원 (Body.Start에서).

    internal static void RestorePlayer(Body body, NetPlayer plr, JObject root)
    {
        try
        {
            RestoreBodyFields(body, root["body"]);
            RestoreSkills(body, root["skills"]);
            RestoreLimbs(body, root["limbs"]);            ApplyComponents(body.gameObject, root["bodyComponents"]);
            ApplyLimbComponents(body, root["limbComponents"]);
            RestoreInventory(body, root["inventory"]);
            RestoreRecipes(plr, root["savedRecipeData"]);
            RestoreCharIdentity(root["charIdentity"]);
            RestoreMisc(root);
            QueuePosition(body, plr, root["position"]);
        }
        catch (System.Exception ex)
        {
            Plugin.Log.LogWarning($"[Save] {plr.playername} 복원 실패: {ex.Message}");
        }
    }

    // 직렬화 제외 필드 — 온도계통 transient 상태. 마이그레이션 목적지(새 프로세스)에서
    // 경과 시간/체온/땀/단열을 복원하면 냉각 게이트가 영구 스킵되고 체온 편차가 전이된다.
    private static readonly HashSet<string> TransientFieldBlacklist = new()
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
                // [JsonProperty] 클래스 타입 필드 (액체 스택 등) — base SaveSystem과 동일 처리.
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
                continue; // 구버전 저장 데이터 방어 — 복원 스킵.
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
                // 클래스 타입 — JSON에서 새 인스턴스로 복원.
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

    // 컴포넌트 ([Saveable]).

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
                // 런타임 추가 [Saveable] 컴포넌트 — GetComponent 실패 시 AddComponent 폴백.
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

    // 타입 이름 → Type 해석 — Type.GetType은 호출 어셈블리/mscorlib만 검색하므로
    // 게임 어셈블리의 [Saveable] 컴포넌트는 로드된 어셈블리를 전부 검색해 해석한다.
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

    // 인벤토리 (계층).

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

        // 재귀 내용물.
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

    // 레시피 / 신원 / 기타.

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

    private static void RestoreRecipes(NetPlayer plr, JToken token)
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
            // Body.lastHappiness는 SerializeFields에 포함되므로 여기서는 건너뜀.
        }
        if (PlayerCamera.main != null && root["caloriesConsumed"] is JValue cc)
        {
            PlayerCamera.main.caloriesConsumed = cc.Value<int>();
        }
    }

    // 위치 (바닐라 스폰 위치 전송 시점에 적용).

    private static void QueuePosition(Body body, NetPlayer plr, JToken token)
    {
        if (token is not JObject pos) return;
        PendingPositions[plr.GetPersistentId()] = (
            pos.Value<float?>("x") ?? 0f,
            pos.Value<float?>("y") ?? 0f,
            pos.Value<int?>("layer") ?? 0);
        PositionSeq[plr.GetPersistentId()] = Interlocked.Increment(ref _seq);
    }

    // 저장 위치 적용 — 바닐라 스폰 위치 전송(LateSpawnLocation) 시점에 호출되어 클라이언트가
    // 처음 받는 스폰 위치가 저장 위치가 되게 한다. 성공 시 true — 호출부가 바닐라 계산을 스킵.
    internal static bool TryApplyPendingPosition(NetPlayer plr, NetBody pb)
    {
        if (plr == null || pb == null || WorldGeneration.world == null) return false;
        if (!TryTakePendingPosition(plr.GetPersistentId(), out var p)) return false;

        if (p.Layer != WorldGeneration.world.biomeDepth)
        {
            // 레이어 불일치 — 마이그레이션/레이어 전환: 위치 복원 없이 기본 스폰 사용.
            return false;
        }

        Vector2 pos = new Vector2(
            Mathf.Clamp(p.X, -WorldGeneration.world.halfWidth, WorldGeneration.world.halfWidth),
            Mathf.Clamp(p.Y, -WorldGeneration.world.halfHeight, WorldGeneration.world.halfHeight));

        Vector2? safe = FindSafePosition(pos);
        if (safe == null)
        {
            // 안전 위치 탐색 실패 — 기본 스폰으로 폴백.
            return false;
        }

        pb.body.transform.position = safe.Value;
        plr.server_plrstate.did_give_spawn_location_from_a_save = true;
        return true;
    }

    // 저장 위치를 바닐라 스폰 위치 계산으로 대체 — 적용 성공 시 바닐라 계산을 스킵한다.
    [HarmonyPatch(typeof(ServerMain), nameof(ServerMain.LateSpawnLocation))]
    internal static class ServerMain_LateSpawnLocation_SavedPositionPatch
    {
        private static bool Prefix(NetBody b)
        {
            if (!KrokoshaScavMultiplayer.is_dedicated_server || b == null || b.plr == null) return true;
            if (TryApplyPendingPosition(b.plr, b))
            {
                return false;
            }
            return true;
        }
    }

    // 저장 위치 walkability 검사 + 막혀 있으면 근처 안전 위치 탐색 (나선 반경 8 타일).
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

    // Ground 블록과 겹치지 않고 발 아래 3f 내 바닥이 있는지 검사 (공중 스폰/낙사 방지).
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

    // 후킹.

    // 바디 생성 시 복원. 데이터 미수신 시 짧은 대기 후 기본 상태로 진행한다.
    [HarmonyPatch(typeof(Body), "Start")]
    internal static class Body_Start_RestorePatch
    {
        // 베이스 모드의 인메모리 저장(server_lastplayerstates) 이중 복원 차단 — 우리 시스템이
        // 정본이므로 재접속/롤백 시 베이스가 아이템을 중복 생성하지 않게 한다.
        [HarmonyPriority(Priority.First)]
        private static void Prefix(Body __instance)
        {
            if (!KrokoshaScavMultiplayer.is_dedicated_server || !KrokoshaScavMultiplayer.is_server) return;
            NetBody netBody = __instance.GetComponent<NetBody>();
            if (netBody == null || !netBody.is_player || netBody.plr == null) return;
            ServerMain.server_lastplayerstates.Remove(netBody.plr.GetPersistentId());
        }

        private static void Postfix(Body __instance)
        {
            if (!KrokoshaScavMultiplayer.is_dedicated_server) return;
            if (!NetPlayer.BodyToPlayerDict.TryGetValue(__instance, out NetPlayer plr)) return;
            string pid = plr.GetPersistentId();

            if (!TryTakePending(pid, out JObject data))
            {
                DateTime deadline = DateTime.UtcNow.AddSeconds(2);
                while (DateTime.UtcNow < deadline)
                {
                    // 메인 스레드 블로킹 중에는 Update가 실행되지 않으므로 여기서 큐를 직접 처리한다.
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
                if (Plugin.VerboseLogging) Plugin.Log.LogWarning($"[Save] {plr.playername} 데이터 없음 — 기본 상태로 시작.");
                GrantStartingSupplies(__instance, plr);
            }
        }
    }

    // 세이브 데이터가 없는 신규 플레이어에게 시작 보급품 지급 (바닐라 지급은 totalTraveled=1
    // 소진으로 억제되어 있으므로 대신 수행). 리스폰(!respawn — 인플레이스)에서도 재사용한다.
    internal static void GrantStartingSupplies(Body body, NetPlayer plr)
    {
        // 바닐라 조건 미러링 — 시작 보급품은 첫 레이어 신규 런 전용.
        if (WorldGeneration.world == null
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

    // 퇴장 저장 — 마이그레이션 동결 중이면 건너뛴다 (파괴된 인벤토리 상태로 덮어쓰지 않음).
    [HarmonyPatch(typeof(NetPlayer), nameof(NetPlayer.OnDestroy))]
    internal static class NetPlayer_OnDestroy_SubmitPatch
    {
        private static void Prefix(NetPlayer __instance)
        {
            if (!KrokoshaScavMultiplayer.is_dedicated_server || !KrokoshaScavMultiplayer.is_server) return;
            if (__instance == null) return;
            if (MigrationModule.IsFrozen(__instance.GetPersistentId())) return;
            if (__instance.body != null)
            {
                SubmitPlayer(__instance);
            }
        }
    }
}
