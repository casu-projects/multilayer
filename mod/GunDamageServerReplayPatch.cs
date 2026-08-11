using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using KrokoshaCasualtiesMP;
using KrokoshaCasualtiesUtils;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace CasuMod;

// 총기 데미지 서버 권위 처리 — 원본 Server_PlayerBodyShoot의 near-barrel SUS 검사가 릴레이
// 경유 환경에서 오탐으로 발사를 거부하므로 대체한다.
// - 클라 리포트(limbs/buildings)를 중복 항목 보존(펠릿별 히트) + 바닐라 캡으로 그대로 적용.
// - 리포트가 비면(클라 게이트 누락 — 정적 식물 등) 서버 레이캐스트 폴백 (림브/빌딩 전용).
// - 블럭 데미지는 모든 사격에 별도 레이캐스트로 적용 — 첫 Ground 히트에 펠릿 수 합산
//   (클라 펠릿 관통 시뮬과 일치 — 혼합 히트에서도 블럭이 반영된다).
[HarmonyPatch(typeof(GunScript_Fire_MultiplayerPatch), "Server_PlayerBodyShoot")]
internal static class GunDamageServerReplayPatch
{
    private static readonly Type ItemSyncType = AccessTools.TypeByName("KrokoshaCasualtiesMP.ItemSync");
    private static readonly Type SyncInfoType = AccessTools.TypeByName("KrokoshaCasualtiesMP.SyncInfo");
    private static readonly MethodInfo TryGetItemSyncInfoMethod =
        AccessTools.Method(ItemSyncType, "TryGetItemSyncInfo",
            new[] { typeof(knetid), SyncInfoType.MakeByRefType() });
    private static readonly MethodInfo VisualsMethod =
        AccessTools.Method(typeof(GunScript_Fire_MultiplayerPatch), "PlayerBodyShoot_VisualsTypeShit");

    private static bool TryGetItemSyncInfo(knetid id, out object si)
    {
        si = null;
        try
        {
            var args = new object[] { id, null };
            bool ok = (bool)TryGetItemSyncInfoMethod.Invoke(null, args);
            si = args[1];
            return ok;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool Prefix(knetid clientId, ref NetDataReader reader)
    {
        if (!KrokoshaScavMultiplayer.is_dedicated_server) return true;
        try
        {
            // 1) 파싱 — 총 syncId, 총구 위치, 방향, racked
            MyLiteNetLibExtensions.Get(reader, out knetid gunId);
            MyLiteNetLibExtensions.Get(reader, out Vector2 barrel);
            MyLiteNetLibExtensions.Get(reader, out Vector2 dir);
            reader.Get(out bool racked);

            // 2) 검증
            if (!KrokoshaScavMultiplayer.IsInGameAndWorldGenerated()) return false;
            if (!NetPlayer.TryGetNetPlayerAndNetBodyFromClientId(clientId, out _, out NetBody pb)
                || pb.body == null || !pb.body.conscious) return false;
            if (!TryGetItemSyncInfo(gunId, out object siObj) || siObj == null) return false;
            GunScript gun = siObj.GetType().GetProperty("gun")?.GetValue(siObj) as GunScript;
            if (gun == null || gun.barrel == null) return false;
            dir.Normalize();
            if (dir == Vector2.zero) return false;

            KrokoshaGunScriptTrackerComponent gst =
                gun.gameObject.GetComponent<KrokoshaGunScriptTrackerComponent>()
                ?? gun.gameObject.AddComponent<KrokoshaGunScriptTrackerComponent>();

            // 3) 리포트 파싱 — 클라 발사 시점 히트. 중복 항목(샷건 펠릿별 반복 히트)은 보존한다.
            //    (ApplyShootDamages가 항목별 데미지를 적용 — 다탄 총은 펠릿 수만큼 들어간다)
            var hitLimbs = new List<Limb>();
            reader.Get(out byte limbCount);
            for (int i = 0; i < limbCount; i++)
            {
                MyLiteNetLibExtensions.Get(reader, out LimbNetId limbId);
                if (!limbId.TryGetNetBodyAndLimbSafe(out NetBody nb, out Limb limb)) continue;
                if (!nb.is_player || pb.CanAttackThisGuy(nb.body))
                {
                    hitLimbs.Add(limb);
                }
            }
            var hitBuildings = new List<SyncInfo>();
            reader.Get(out byte bldCount);
            for (int i = 0; i < bldCount; i++)
            {
                MyLiteNetLibExtensions.Get(reader, out knetid syncId);
                if (NetObjectRegistry.TryGetSyncInfo(syncId, out SyncInfo si2)
                    && si2.IsBuilding() && NetObjectRegistry.IsObjectDynamic(si2.go)
                    && !si2.building.cantHit)
                {
                    hitBuildings.Add(si2);
                }
            }

            // 4) 리포트가 비면 서버 레이캐스트 폴백 (림브/빌딩 전용 — 첫 벽에서 정지).
            if (hitLimbs.Count == 0 && hitBuildings.Count == 0)
            {
                ServerLimbFallback(barrel, dir, gun, pb, hitLimbs, hitBuildings);
            }

            // 5) 캡 — 바닐라와 동일 (림브: min(shotsPerFire, 4), 빌딩: shotsPerFire).
            hitLimbs = Util.LimitMaxRepeats(hitLimbs,
                Math.Min(gun.shotsPerFire, GunScript_Fire_MultiplayerPatch.MAX_HITS_PER_LIMB_AT_ONCE));
            hitBuildings = Util.LimitMaxRepeats(hitBuildings, gun.shotsPerFire);

            // 6) 10104 릴레이 — 다른 클라이언트들이 슈터의 사격 표시를 볼 수 있게
            NetDataWriter writer = Net.CreateWriter(10104);
            writer.Put((ushort)clientId);
            writer.Put((ushort)gunId);
            writer.Put(barrel);
            writer.Put(dir);
            Net.Server_SendToClients(DeliveryMethod.ReliableUnordered, in writer,
                (IEnumerable<knetid>)ServerMain.GetListOfClientIdsExceptThisAndHost(clientId));

            // 7) 슈터 비주얼 + racked/gas — 원본과 동일 순서. ForceJam 래퍼로 언래킹 사격에
            //    강제 잼을 부여한다.
            GunScript_JamChance_MultiplayerPatch.ForceJam = !gun.racked;
            try { VisualsMethod?.Invoke(null, new object[] { clientId, siObj, barrel, dir }); }
            catch (Exception) { }
            GunScript_JamChance_MultiplayerPatch.ForceJam = false;
            gun.racked = racked;
            gun.lastRacked = false;
            if ((int)gun.firingMode != 0 && racked)
            {
                gst.gasTime = gun.desiredGasTime;
            }

            // 8) 블럭 데미지 — 모든 사격에 적용. 개별 펠릿의 스프레드 방향은 패킷에 없어
            //    중앙 레이 기준 펠릿 수 합산 (근사).
            ApplyBlockDamage(barrel, dir, gun);

            // 9) 서버 권위 데미지 — 림브/빌딩 (중복 항목 → 펠릿별 적용 + 3펠릿 절단 판정)
            TurretScript_Shoot_MultiplayerPatch.ApplyShootDamages(gst, hitLimbs, hitBuildings);

            return false;
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[GunFix] replay fail: {ex.Message}");
            return false;
        }
    }

    // 서버측 레이캐스트 폴백 — 림브/빌딩 전용. 첫 Ground에서 정지 (블럭 데미지는 ApplyBlockDamage).
    private static void ServerLimbFallback(Vector2 barrel, Vector2 dir, GunScript gun, NetBody pb,
        List<Limb> hitLimbs, List<SyncInfo> hitBuildings)
    {
        var hitBodies = new HashSet<Body>();
        RaycastHit2D[] hits = Physics2D.RaycastAll(barrel, dir, 200f,
            LayerMask.GetMask("Ground", "Body", "Limb", "Descriptor"));
        foreach (RaycastHit2D hit in hits)
        {
            if (hit.collider == null) continue;

            if (hit.collider.gameObject.layer == 6)
            {
                break;
            }

            if (hit.collider.TryGetComponent<BuildingEntity>(out BuildingEntity b) && !b.cantHit)
            {
                if (NetObjectRegistry.TryGetSyncInfo(hit.collider.gameObject, out SyncInfo si)
                    && !hitBuildings.Contains(si))
                {
                    hitBuildings.Add(si);
                }
            }

            Limb limb = hit.collider.GetComponent<Limb>();
            Body body = limb != null ? limb.body : hit.collider.GetComponent<Body>();
            if (body != null && hitBodies.Add(body) && body.TryGetComponent<NetBody>(out NetBody nb))
            {
                bool allowed = !nb.is_player || pb.CanAttackThisGuy(body);
                if (allowed)
                {
                    Limb closest = body.GetClosestLimb(hit.point);
                    if (!hitLimbs.Contains(closest)) hitLimbs.Add(closest);
                }
            }
        }
    }

    // 블럭 데미지 — 첫 Ground 히트에 펠릿 수 합산.
    private static void ApplyBlockDamage(Vector2 barrel, Vector2 dir, GunScript gun)
    {
        float num = UnityEngine.Random.Range(0.85f, 1.15f);
        float total = gun.structureDamage * num * Math.Max(1, gun.shotsPerFire);
        RaycastHit2D[] hits = Physics2D.RaycastAll(barrel, dir, 200f,
            LayerMask.GetMask("Ground", "Body", "Limb", "Descriptor"));
        foreach (RaycastHit2D hit in hits)
        {
            if (hit.collider == null) continue;
            if (hit.collider.gameObject.layer == 6)
            {
                WorldGeneration.world.DamageBlock(hit.point + dir * 0.5f, total);
                break;
            }
        }
    }
}
