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

/// <summary>총기 데미지 서버 권위 처리 — v4 하이브리드 (2026-08-11).
/// 문제 ①: 원본 Server_PlayerBodyShoot의 near-barrel SUS 검사가 Steam P2P 릴레이 경유
/// 환경에서 발사 거부를 유발한다 (프로덕션 로그 확정 — 클라 총구와 서버 총구가 릴레이
/// 지연 + Unreliable 위치 동기화 손실로 불일치 → SUS 오탐 → 롤백).
/// 문제 ②: 클라 리포트 게이트(BuildingHasActivePhysics — Dynamic rb 등)가 정적 식물/몬스터를
/// 리포트에서 누락 → 서버 데미지 0 → 롤백.
/// 문제 ③ (v4): 샷건 등 다탄 총이 같은 대상을 동시에 맞추면 리포트의 **중복 항목**(펠릿별
/// 히트 — 모드 설계: LimitMaxRepeats가 중복 유지, ApplyShootDamages가 항목별 데미지)을
/// 중복 제거로 압축해 1발분 데미지만 적용됐다. 블럭 데미지도 폴백 1회뿐이었고, 혼합
/// 히트(몬스터+블럭)에서는 리포트가 비어 있지 않아 서버 블럭 데미지가 아예 0이었다.
///
/// v4 설계 — 딜레이 면역 + 펠릿별 정확성:
///  ① 리포트 목록(limbs/buildings)은 **클라 발사 시점의 레이캐스트 결과** — 검증(PvP 게이트) 후
///     중복 항목을 그대로 보존하고 바닐라와 동일한 캡(LimitMaxRepeats)만 적용 → 펠릿별 데미지.
///  ② 목록이 비면(클라 게이트 누락 — 정적 식물 등) 서버 레이캐스트로 폴백 — 림브/빌딩 전용
///     (첫 Ground에서 정지 — 벽 관통 방지).
///  ③ 블럭 데미지는 **모든 사격에 별도 레이캐스트**(ApplyBlockDamage)로 적용 —
///     첫 Ground 히트에 `structureDamage × 펠릿 수` 합산. 클라 펠릿 관통 시뮬(TurretScript.Shoot:
///     림브/빌딩 관통 후 뒤 블럭까지 데미지)과 일치 — 혼합 히트에서도 블럭 데미지가 반영된다.
///  ④ near-barrel 검사 없음 — 릴레이 경유 환경에서 성립 불가 (협동 서버 안티치트 가치 낮음).
/// 플레이어간 사격은 CanAttackThisGuy(PVP) 게이트 유지 — PvP 차단.</summary>
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

            // 2) 검증 — near-barrel 제외 (릴레이 경유 환경에서 성립 불가)
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

            // 3) 리포트 목록 파싱 + 검증 — 클라 발사 시점 히트 (지연 면역 — 이동 타겟 정확).
            //    중복 항목(샷건 펠릿별 반복 히트)은 **보존**한다 — ApplyShootDamages가 항목별
            //    데미지를 적용하므로 다탄 총은 펠릿 수만큼 데미지가 들어가야 한다.
            //    (원본 검증과 동일: 플레이어는 CanAttackThisGuy(PvP), 몬스터는 항상 허용)
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

            // 4) 목록이 비면(클라 게이트 누락 — 정적 식물/몬스터 등) 서버 레이캐스트 폴백 —
            //    림브/빌딩 전용 (첫 Ground에서 정지 — 블럭 데미지는 7번 ApplyBlockDamage 담당).
            if (hitLimbs.Count == 0 && hitBuildings.Count == 0)
            {
                ServerLimbFallback(barrel, dir, gun, pb, hitLimbs, hitBuildings);
            }

            // 5) 캡 — 바닐라와 동일 (림브: min(shotsPerFire, 4), 빌딩: shotsPerFire).
            //    리포트는 클라가 이미 캡해 보내지만, 폴백 보충분까지 묶어 방어.
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

            // 7) 슈터 비주얼(서버측 총 상태 — 장전수/반동) + racked/gas — 원본과 동일 순서/동작.
            //    원본과 동일하게 ForceJam 래퍼 적용 — 언래킹 상태 사격에 서버측 강제 잼 부여
            //    (GunScript_JamChance Postfix: ForceJam=true면 잼 3, 아니면 원격 총은 -1).
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

            // 8) 블럭 데미지 — 모든 사격에 적용 (혼합 히트 포함). 클라 펠릿 관통 시뮬과 동일하게
            //    첫 Ground 히트에 펠릿 수 합산 — 스프레드 방향은 패킷에 없어 중앙 레이 근사.
            ApplyBlockDamage(barrel, dir, gun);

            // 9) 서버 권위 데미지 — 림브/빌딩 (중복 항목 보존 → 펠릿별 적용 + 3펠릿 절단 판정)
            TurretScript_Shoot_MultiplayerPatch.ApplyShootDamages(gst, hitLimbs, hitBuildings);

            return false;
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[GunFix] replay fail: {ex.Message}");
            return false;
        }
    }

    /// <summary>서버측 레이캐스트 — 권위 히트 재발견 (림브/빌딩 전용, 2026-08-11 v4).
    /// 정적 기하는 서버 시점과 클라 시점이 동일하므로 릴레이 지연과 무관하게 정확.
    /// 첫 Ground(layer 6)에서 레이를 정지한다 — 벽 뒤 타겟 오데미지 방지 (블럭 자체의
    /// 데미지는 ApplyBlockDamage가 별도 적용).</summary>
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
                break; // 벽 — 레이 정지 (블럭 데미지는 ApplyBlockDamage)
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

    /// <summary>블럭 데미지 — 첫 Ground 히트에 펠릿 수 합산 (v4).
    /// 바닐라 클라 펠릿 시뮬(TurretScript.Shoot)은 림브/빌딩을 관통해 뒤 블럭까지
    /// `structureDamage × 펠릿 수`를 적용하므로, 서버도 모든 사격에서 동일하게 반영한다.
    /// 개별 펠릿의 스프레드 방향은 패킷에 없어 중앙 레이 기준 근사.</summary>
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
