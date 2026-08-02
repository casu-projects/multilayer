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

/// <summary>플레이어 데이터 세이브/로드 (S9) — 오케스트레이터 단일 소유자 모델.
/// 직렬화는 바닐라 SaveSystem과 동일한 메커니즘 (body/limbs 전체 상태 + [Saveable] 컴포넌트
/// + 계층 인벤토리 + 레시피 + 캐릭터 신원).</summary>
public static class SaveModule
{
    /// <summary>플레이어 데이터 수신 대기 큐 (PLAYER_DATA_RESPONSE / RESUME → Body.Start 소비).</summary>
    internal static readonly ConcurrentDictionary<string, JObject> PendingData = new();

    private static readonly ConcurrentDictionary<string, (float X, float Y, int Layer)> PendingPositions = new();

    // ── 직렬화 (S9-3) ──

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

    /// <summary>플레이어 데이터 제출 (퇴장/동결) → 오케스트레이터.</summary>
    public static void SubmitPlayer(NetPlayer plr)
    {
        if (plr == null || plr.body == null) return;
        try
        {
            JObject data = SerializePlayer(plr);
            OrchestratorClient.Instance?.SendEvent("PLAYER_DATA_SUBMIT",
                new { playerKey = plr.GetPersistentId(), payload = data });
            Plugin.Log.LogInfo($"[Save] {plr.playername} 데이터 제출.");
        }
        catch (System.Exception ex)
        {
            Plugin.Log.LogWarning($"[Save] {plr.playername} 직렬화 실패: {ex.Message}");
        }
    }

    /// <summary>PLAYER_DATA_RESPONSE (접속 로드 응답) — pending 큐에 저장.
    /// payload가 없으면 "데이터 없음" — 잔존 엔트리를 정리하고 기본 상태로 진행.</summary>
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
            PendingData.TryRemove(playerKey, out _); // 데이터 없음 — 이전 잔존 엔트리 정리
            return;
        }
        PendingData[playerKey] = (JObject)payload;
    }

    // ── 복원 (S9-4 — Body.Start에서) ──

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
            Plugin.Log.LogInfo($"[Save] {plr.playername} 세이브 복원 완료.");
        }
        catch (System.Exception ex)
        {
            Plugin.Log.LogWarning($"[Save] {plr.playername} 복원 실패: {ex.Message}");
        }
    }

    // ── 필드 직렬화 헬퍼 ──

    private static bool IsSavableType(Type t) =>
        t == typeof(string) || t == typeof(float[]) || t == typeof(int[]) || t == typeof(bool[])
        || t == typeof(string[])
        || (t.IsValueType && !typeof(UnityEngine.Object).IsAssignableFrom(t));

    private static JObject SerializeFields(object obj)
    {
        var result = new JObject();
        foreach (FieldInfo f in obj.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            if (IsSavableType(f.FieldType))
            {
                try { result[f.Name] = JToken.FromObject(f.GetValue(obj)); }
                catch { }
            }
            else if (f.IsDefined(typeof(JsonPropertyAttribute), false)
                     && !typeof(UnityEngine.Object).IsAssignableFrom(f.FieldType))
            {
                // [JsonProperty] 클래스 타입 필드 (예: WaterContainerItem.stack — List<LiquidStack>,
                // NonDescriptCan.liquidIds — List<string>) — base SaveSystem은 Newtonsoft로
                // 직렬화하므로(액체 상태 등) 동일하게 처리한다. 화이트리스트(IsSavableType)가
                // 클래스 타입을 걸러 유실되던 문제 해결.
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
                // 클래스 타입 — JSON에서 새 인스턴스로 복원 (기존 참조 교체).
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

    // ── 컴포넌트 ([Saveable] — S9-1) ──

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
                // 런타임 추가 [Saveable] 컴포넌트 (S9-2) — GetComponent 실패 시 AddComponent 폴백.
                // 직렬화는 MonoBehaviour만 대상이므로 안전하다.
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

    /// <summary>타입 이름 → Type 해석. Type.GetType은 호출 어셈블리/mscorlib만 검색하므로,
    /// 게임 어셈블리(Assembly-CSharp)의 [Saveable] 컴포넌트(WaterContainerItem 등)는
    /// 이름만으로 조회하면 null이 되어 복원이 조용히 스킵된다 — 로드된 어셈블리를 전부
    /// 검색해 해석한다.</summary>
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

    // ── 인벤토리 (계층 — S9-1) ──

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

        // 재귀 내용물
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

    // ── 레시피 / 신원 / 기타 ──

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
            Plugin.Log.LogWarning("[Save] WoundView.view 없음 — charIdentity 복원 불가.");
            return;
        }
        try
        {
            int h = id.Value<int?>("height") ?? 0;
            int a = id.Value<int?>("age") ?? 0;
            int cid = id.Value<int?>("id") ?? 0;
            int ver = id.Value<int?>("ver") ?? 0;
            WoundView.view.SetCharDetails(h, a, cid, ver);
            Plugin.Log.LogInfo($"[Save] charIdentity 복원 (h={h}, a={a}, id={cid}, ver={ver}).");
        }
        catch { }
    }

    private static void RestoreMisc(JObject root)
    {
        if (root["lastHappiness"] is JArray lh)
        {
            // Body.lastHappiness는 SerializeFields에 포함되므로 여기서는 건너뜀 (중복 방지)
        }
        if (PlayerCamera.main != null && root["caloriesConsumed"] is JValue cc)
        {
            PlayerCamera.main.caloriesConsumed = cc.Value<int>();
        }
    }

    // ── 위치 (바닐라 스폰 위치 전송 시점에 적용 — LateSpawnLocation Prefix) ──

    private static void QueuePosition(Body body, NetPlayer plr, JToken token)
    {
        if (token is not JObject pos) return;
        PendingPositions[plr.GetPersistentId()] = (
            pos.Value<float?>("x") ?? 0f,
            pos.Value<float?>("y") ?? 0f,
            pos.Value<int?>("layer") ?? 0);
    }

    /// <summary>저장 위치 적용 — 바닐라 스폰 위치 전송(LateSpawnLocation) 시점에 호출되어
    /// 클라이언트가 처음 받는 스폰 위치가 저장 위치가 되게 한다 (기존 10168 지연 적용은
    /// 바닐라 스폰 전송 이후라 클라이언트에 반영되지 않았다). 성공 시 true — 호출부가
    /// 바닐라 스폰 계산을 스킵한다.</summary>
    internal static bool TryApplyPendingPosition(NetPlayer plr, NetBody pb)
    {
        if (plr == null || pb == null || WorldGeneration.world == null) return false;
        if (!PendingPositions.TryRemove(plr.GetPersistentId(), out var p)) return false;

        if (p.Layer != WorldGeneration.world.biomeDepth)
        {
            // 레이어 불일치 — 마이그레이션/레이어 전환: 위치 복원 없이 새 레이어 기본 스폰 사용
            return false;
        }

        Vector2 pos = new Vector2(
            Mathf.Clamp(p.X, -WorldGeneration.world.halfWidth, WorldGeneration.world.halfWidth),
            Mathf.Clamp(p.Y, -WorldGeneration.world.halfHeight, WorldGeneration.world.halfHeight));

        Vector2? safe = FindSafePosition(pos);
        if (safe == null)
        {
            // 안전 위치 탐색 실패 — 적용 포기, 바닐라 기본 스폰(LateSpawnLocation)으로 폴백.
            return false;
        }

        pb.body.transform.position = safe.Value;
        // 바닐라 스폰 플로우가 저장 위치를 덮어쓰지 않도록 플래그 설정 (PlayerSavedState.Apply와 동일 규약).
        plr.server_plrstate.did_give_spawn_location_from_a_save = true;
        Plugin.Log.LogInfo($"[Save] {plr.playername} 위치 적용 ({safe.Value.x:F1}, {safe.Value.y:F1}).");
        return true;
    }

    /// <summary>바닐라 스폰 위치 계산(LateSpawnLocation)을 저장 위치로 대체 — 저장된 위치가
    /// 있으면 그 위치를 적용하고 바닐라(기본 스폰/타인 근처) 계산을 스킵한다. 이후 바닐라가
    /// "Sending a spawn location"으로 현재 위치를 클라이언트에 전송하므로, 클라이언트가
    /// 처음 받는 스폰 위치가 저장 위치가 된다.</summary>
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

    /// <summary>저장 위치 walkability 검사 + 막혀 있으면 근처 안전 위치 탐색 (나선 반경 8 타일).
    /// 실패 시 null — 호출부가 기본 스폰으로 폴백한다.</summary>
    private static Vector2? FindSafePosition(Vector2 pos)
    {
        if (IsWalkable(pos)) return pos;

        Plugin.Log.LogInfo($"[Save] 저장 위치 ({pos.x:F1}, {pos.y:F1}) 막힘 — 근처 안전 위치 탐색.");
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
                        Plugin.Log.LogInfo($"[Save] 안전 위치 발견: ({candidate.x:F1}, {candidate.y:F1}).");
                        return candidate;
                    }
                }
            }
        }

        Plugin.Log.LogWarning($"[Save] 저장 위치 ({pos.x:F1}, {pos.y:F1}) 주변 안전 위치 없음 — 기본 스폰 폴백.");
        return null;
    }

    /// <summary>바디 콜라이더 크기 기준으로 Ground 블록과 겹치지 않는지 검사
    /// (바닐라 PlaceBody_FindSpawnLocation과 동일한 검사 원리).</summary>
    private static bool IsWalkable(Vector2 pos)
    {
        try
        {
            return !Physics2D.OverlapBox(pos, Body_Awake_MultiplayerPatch.origColSize, 0f,
                LayerMask.GetMask("Ground"));
        }
        catch
        {
            return true; // 검사 불가 시 통과 (기본 스폰 경로가 안전 보장)
        }
    }

    // ── 후킹 ──

    /// <summary>바디 생성 시 복원 (S9-4). 데이터 미수신 시 짧은 대기 후 기본 상태.</summary>
    [HarmonyPatch(typeof(Body), "Start")]
    internal static class Body_Start_RestorePatch
    {
        /// <summary>베이스 모드의 인메모리 저장(server_lastplayerstates) 이중 복원 차단 (P3).
        /// 우리 시스템(오케스트레이터 단일 소유자)이 정본이므로, 같은 인스턴스 재접속/롤백
        /// 시 베이스 PlayerSavedState.Apply가 아이템을 중복 생성하는 것을 방지한다.
        /// (구 모드 FreshInstanceReconnectPatch.cs:23 동일 규약)</summary>
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

            if (!PendingData.TryRemove(pid, out JObject data))
            {
                DateTime deadline = DateTime.UtcNow.AddSeconds(2);
                while (DateTime.UtcNow < deadline)
                {
                    // 응답 디스패치는 Update에서만 일어나는데, 여기서 메인 스레드를 블로킹하면
                    // Update가 실행될 수 없다 — 대기 중에도 큐를 직접 처리해 응답을 반영한다.
                    OrchestratorClient.Instance?.ProcessInbound();
                    if (PendingData.TryRemove(pid, out data)) break;
                    System.Threading.Thread.Sleep(25);
                }
            }
            if (data != null)
            {
                RestorePlayer(__instance, plr, data);
                // 위치 적용은 10168(월드젠 완료) Postfix로 지연 (2026-08-02 수정) — 바닐라
                // HeyPlayerJustJoined의 스폰 리로케이션(10019)이 저장 위치를 덮어쓰는 경합을
                // 피하기 위해, 바닐라 스폰 적용 이후에 우리 위치를 적용한다.
                // (QueuePosition이 PendingPositions에 큐잉 — MigrationModule의 10168 Postfix에서 소비)
            }
            else
            {
                Plugin.Log.LogWarning($"[Save] {plr.playername} 데이터 없음 — 기본 상태로 시작.");
                GrantStartingSupplies(__instance, plr);
            }
        }
    }

    /// <summary>세이브 데이터가 없는 신규 플레이어에게 시작 보급품 지급 (플레이어별 개별 판정 — B).
    /// 바닐라 지급은 totalTraveled=1 소진(FinishWorldGeneration)으로 전부 억제되어 있으므로,
    /// 여기서 바닐라 NetBody.CreateNewPlayerCharacter의 지급 로직을 대신 수행한다.
    /// 리스폰(!respawn — 인플레이스)에서도 재사용한다.</summary>
    internal static void GrantStartingSupplies(Body body, NetPlayer plr)
    {
        // 바닐라 조건 미러링 — 시작 보급품은 첫 레이어 신규 런 전용.
        // totalTraveled는 FinishWorldGeneration에서 1로 소진되므로 첫 레이어 판정에는 biomeDepth를 쓴다.
        if (WorldGeneration.world == null
            || WorldGeneration.world.biomeDepth != 0
            || (int)WorldGeneration.world.biomeOverride != 0
            || SaveSystem.loadedRun)
        {
            Plugin.Log.LogInfo($"[Save] {plr.playername} 기본 상태 — 보급품 조건 불일치로 미지급.");
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
                Plugin.Log.LogInfo($"[Save] {plr.playername} 기본 상태 — startingsupplies={WorldGeneration.GetRunSettingInt("startingsupplies")} 미지급.");
                return;
        }

        Plugin.Log.LogInfo($"[Save] {plr.playername} 신규 플레이어 — 시작 보급품 지급.");
    }

    /// <summary>퇴장 저장 (S9-5) — 마이그레이션 중(FREEZE 후)이면 건너뜀.</summary>
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
