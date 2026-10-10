using HarmonyLib;
using Together;

namespace CasuMod.Patch;

// 접속/퇴장 공지를 base 와 동일한 포맷/색상으로 모드가 직접 만들어 발신한다.
//   포맷: [플레이어색 이름]<parsebreak> [서버색(#FFC90E) <key:plr_joined|plr_left>]
//   - <key:> 는 클라이언트가 자기 로케일로 해석, <parsebreak> 도 클라가 처리한다
// - base 로컬 join/leave 는 JoinLeaveAnnounceSuppressPatch 가 억제하므로 중복 없음
// - 레이어 이동(마이그레이션) 시에는 발신하지 않는다:
//     도착 = MigrationArrivalTracker(게이트웨이 tail isMigratingArrival)
//     출발 = MigrationModule.IsFrozen(FREEZE 로 설정)
internal static class JoinLeaveAnnounceMessage
{
    internal static string Build(ScavPlayer plr, string localeKey)
    {
        return Chat.AddColorTags(plr.playerColor, plr.playerName)
            + "<parsebreak> "
            + Chat.AddColorTags(Chat.DefaultChatColors.Server, $"<key:{localeKey}>");
    }
}

// 접속 - base 가 로컬 공지하던 지점(ScavPlayer.Start, 서버/비로컬)에서 대신 발신
[HarmonyPatch(typeof(ScavPlayer), "Start")]
internal static class ScavPlayer_Start_AnnounceJoinPatch
{
    private static void Postfix(ScavPlayer __instance)
    {
        if (__instance == null || !Net.IsServer || __instance.IsLocal) return;

        // 마이그레이션 도착은 접속 공지 제외 (레이어 이동 시 표시 안 함)
        if (MigrationArrivalTracker.ClientIds.Contains(__instance.playerId)) return;

        AnnounceRelay.SendJoin(__instance, JoinLeaveAnnounceMessage.Build(__instance, "plr_joined"));
    }
}

// 퇴장 - base 가 로컬 공지하던 지점(ScavPlayer.OnDestroy, 서버)에서 대신 발신
[HarmonyPatch(typeof(ScavPlayer), nameof(ScavPlayer.OnDestroy))]
internal static class ScavPlayer_OnDestroy_AnnounceLeavePatch
{
    private static void Prefix(ScavPlayer __instance)
    {
        if (__instance == null || !Net.IsServer || !Net.IsRunning || __instance.IsLocal) return;

        // 마이그레이션(FREEZE) 중 퇴장은 제외 (레이어 이동 시 표시 안 함)
        if (MigrationModule.IsFrozen(__instance.GetPersistentId())) return;

        AnnounceRelay.SendLeave(__instance, JoinLeaveAnnounceMessage.Build(__instance, "plr_left"));
    }
}
