using HarmonyLib;
using KrokoshaCasualtiesMP;

namespace CasuMod;

// 짧은 닉네임 허용 (구 프로젝트 AllowShortNamesPatch 이식) - 베이스 모드가
// 2자 이하 이름을 "Name too short."로 거부하므로, 데디 서버에서 허용으로 덮어쓴다
// deny_reason은 out 매개변수 - Harmony Postfix는 ref로 매칭된다
[HarmonyPatch(typeof(KrokoshaScavMultiplayer),
    nameof(KrokoshaScavMultiplayer.ValidateClientConnectIntroductionPacket))]
internal static class AllowShortNamesPatch
{
    private static void Postfix(ref bool __result, ref string deny_reason)
    {
        if (!KrokoshaScavMultiplayer.is_dedicated_server) return;
        if (!__result && deny_reason == "Name too short.")
        {
            deny_reason = "Success";
            __result = true;
        }
    }
}
