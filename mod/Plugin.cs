using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using KrokoshaCasualtiesMP;
using System.Linq;

namespace CasuMod;

[BepInPlugin("dev.stdd.casumod", "CasuMod", "0.1.0")]
[BepInDependency("KrokoshaCasualtiesMP", "4.0.1")]
public class Plugin : BaseUnityPlugin
{
    public static ManualLogSource Log;
    internal static Harmony HarmonyInstance;

    /// <summary>디버그 로그 표시 여부 — 오케스트레이터 VERBOSE 메시지로 설정된다.
    /// false면 루틴 트레이스가 숨겨지고, 명령 결과/실패성 경고/오류는 항상 표시된다.</summary>
    internal static bool VerboseLogging;

    private void Awake()
    {
        Log = Logger;
        HarmonyInstance = new Harmony("dev.stdd.casumod");
        HarmonyInstance.PatchAll();

        gameObject.AddComponent<OrchestratorClient>();
        gameObject.AddComponent<MigrationModule>();
        ChatRelay.Init();

        Log.LogInfo($"CasuMod v0.1.0 loaded — {HarmonyInstance.GetPatchedMethods().Count()} method(s) patched.");
    }
}
