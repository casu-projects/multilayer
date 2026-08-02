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
