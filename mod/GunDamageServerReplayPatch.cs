using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using KrokoshaCasualtiesMP;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace CasuMod;

// 총기 데미지 서버 권위 처리 - 원본 Server_PlayerBodyShoot의 near-barrel SUS 검사가
// Steam P2P 릴레이 지연으로 오탐해 발사 거부(롤백)를 유발하므로 대체한다.
// 클라 리포트(클라 발사 시점 히트 - 지연 면역)를 PvP 게이트 검증 후 그대로 적용하고,
// 리포트가 비면(클라 게이트 누락 - 정적 식물 등) 서버 레이캐스트로 폴백한다.
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
 // 파싱 - 총 syncId, 총구 위치, 방향, racked
            MyLiteNetLibExtensions.Get(reader, out knetid gunId);
            MyLiteNetLibExtensions.Get(reader, out Vector2 barrel);
            MyLiteNetLibExtensions.Get(reader, out Vector2 dir);
            reader.Get(out bool racked);

 // 검증 (near-barrel 제외 - 릴레이 경유 환경에서 성립 불가)
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

 // 리포트 목록 파싱 + 검증 (플레이어는 CanAttackThisGuy(PvP), 몬스터는 항상 허용)
            var hitLimbs = new List<Limb>();
            reader.Get(out byte limbCount);
            for (int i = 0; i < limbCount; i++)
            {
                MyLiteNetLibExtensions.Get(reader, out LimbNetId limbId);
                if (!limbId.TryGetNetBodyAndLimbSafe(out NetBody nb, out Limb limb)) continue;
                if (!nb.is_player || pb.CanAttackThisGuy(nb.body))
                {
 // 샷건 다탄은 같은 림브에 중복 히트가 리포트됨 - 중복 제거 시 화면의 다발 데미지와 불일치.
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

 // 목록이 비면(클라 게이트 누락 - 정적 식물 등) 서버 레이캐스트 폴백
            if (hitLimbs.Count == 0 && hitBuildings.Count == 0)
            {
                ServerRaycastFallback(barrel, dir, gun, pb, hitLimbs, hitBuildings);
            }

 // 10104 릴레이 - 다른 클라이언트들이 슈터의 사격 표시를 볼 수 있게
            NetDataWriter writer = Net.CreateWriter(10104);
            writer.Put((ushort)clientId);
            writer.Put((ushort)gunId);
            writer.Put(barrel);
            writer.Put(dir);
            Net.Server_SendToClients(DeliveryMethod.ReliableUnordered, in writer,
                (IEnumerable<knetid>)ServerMain.GetListOfClientIdsExceptThisAndHost(clientId));

 // 슈터 비주얼(서버측 총 상태 - 장전수/반동) + racked/gas - 원본과 동일 순서
            try { VisualsMethod?.Invoke(null, new object[] { clientId, siObj, barrel, dir }); }
            catch (Exception) { }
            gun.racked = racked;
            gun.lastRacked = false;
            if ((int)gun.firingMode != 0 && racked)
            {
                gst.gasTime = gun.desiredGasTime;
            }

 // 7) 서버 권위 데미지 적용
            TurretScript_Shoot_MultiplayerPatch.ApplyShootDamages(gst, hitLimbs, hitBuildings);

            return false;
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[GunFix] replay fail: {ex.Message}");
            return false;
        }
    }

 // 서버측 레이캐스트 - 권위 히트 재발견 (바닐라 TurretScript.Shoot과 동일 파라미터).
 // 정적 기하는 서버 시점과 클라 시점이 동일하므로 릴레이 지연과 무관하게 정확.
    private static void ServerRaycastFallback(Vector2 barrel, Vector2 dir, GunScript gun, NetBody pb,
        List<Limb> hitLimbs, List<SyncInfo> hitBuildings)
    {
        var hitBodies = new HashSet<Body>();
        float num = UnityEngine.Random.Range(0.85f, 1.15f);
        RaycastHit2D[] hits = Physics2D.RaycastAll(barrel, dir, 200f,
            LayerMask.GetMask("Ground", "Body", "Limb", "Descriptor"));
        foreach (RaycastHit2D hit in hits)
        {
            if (hit.collider == null) continue;

            if (hit.collider.TryGetComponent<BuildingEntity>(out BuildingEntity b) && !b.cantHit)
            {
                if (NetObjectRegistry.TryGetSyncInfo(hit.collider.gameObject, out SyncInfo si)
                    && !hitBuildings.Contains(si))
                {
                    hitBuildings.Add(si);
                }
            }
            else if (hit.collider.gameObject.layer == 6)
            {
                WorldGeneration.world.DamageBlock(hit.point + dir * 0.5f, gun.structureDamage * num);
                break;
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
}
