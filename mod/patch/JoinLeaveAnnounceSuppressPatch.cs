using System.Reflection;
using HarmonyLib;
using Together;

namespace CasuMod.Patch;

// base 로컬 접속/퇴장 공지(<key:plr_joined>/<key:plr_left>)를 억제한다.
// 레이어 이동은 게이트웨이 투명 스왑이라 클라 연결이 유지되지만 출발지의 ScavPlayer 파괴/도착지의
// 생성으로 left/joined가 표시되는 노이즈가 생긴다. base의 1-arg Chat.Server_ChatAnnouncement에서
// join/leave 문구만 걸러내고, 실제 접속/퇴장 공지는 게이트웨이 연결 변화 기반 크로스레이어 ANNOUNCE로 일원화한다.
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
