using HarmonyLib;
using KrokoshaCasualtiesMP;

namespace CasuMod;

// 2자 이하 닉네임 허용 — 베이스 모드의 "Name too short." 거부를 무효화한다.
[HarmonyPatch(typeof(KrokoshaScavMultiplayer),
    nameof(KrokoshaScavMultiplayer.ValidateClientConnectIntroductionPacket))]
internal static class AllowShortNamesPatch
{
    private static void Postfix(ref bool __result, ref string deny_reason)
    {
        if (!KrokoshaScavMultiplayer.is_dedicated_server) return;
        if (!__result && deny_reason == "Name too short.")
        {
            // deny_reason은 out 매개변수 — Harmony Postfix는 ref로 매칭된다.
            deny_reason = "Success";
            __result = true;
        }
    }
}
