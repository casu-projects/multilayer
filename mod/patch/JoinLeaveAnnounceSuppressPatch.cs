using System.Reflection;
using HarmonyLib;
using Together;

namespace CasuMod.Patch;

// base 로컬 접속/퇴장 공지(<key:plr_joined>/<key:plr_left>) 억제.
//
// 배경: base 는 ScavPlayer.Start / ScavPlayer.OnDestroy 에서 로컬(같은 레이어)에 접속/퇴장을
// 무조건 공지한다. 레이어 이동(마이그레이션)은 게이트웨이 투명 스왑이라 클라 연결은 유지되지만,
// 출발 인스턴스에서는 ScavPlayer 파괴(퇴장), 도착 인스턴스에서는 생성(접속)이 일어나므로
// 레이어 이동 때도 "left/joined"가 표시되는 노이즈가 발생한다.
//
// 처리: base 의 1-arg Chat.Server_ChatAnnouncement 에서 join/leave 문구만 억제한다.
// 접속/퇴장 공지는 오케스트레이터가 "게이트웨이 실제 연결 변화"에만 발신하는 크로스레이어
// ANNOUNCE(AnnounceRelay)로 일원화되므로, 레이어 이동 시에는 표시되지 않는다.
// (사망/기타 시스템 공지는 이 필터에 걸리지 않아 그대로 통과한다.)
[HarmonyPatch]
internal static class JoinLeaveAnnounceSuppressPatch
{
    [HarmonyTargetMethod]
    private static MethodBase TargetMethod()
    {
        return AccessTools.DeclaredMethod(typeof(Chat), nameof(Chat.Server_ChatAnnouncement),
            new[] { typeof(string).MakeByRefType() });
    }

    private static bool Prefix(ref string message)
    {
        if (message != null
            && (message.Contains("<key:plr_joined>") || message.Contains("<key:plr_left>")))
        {
            return false; // base 로컬 join/leave 억제 - 크로스레이어 공지가 담당
        }
        return true;
    }
}
