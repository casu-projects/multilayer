using System.Reflection;
using HarmonyLib;
using Together;

namespace CasuMod.Patch;

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
