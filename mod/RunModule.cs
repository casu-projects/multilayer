using CasuMp.Protocol;
using System;
using System.Collections.Generic;
using HarmonyLib;
using Together;
using UnityEngine;

namespace CasuMod;

public static class RunModule
{
    private static bool _readyReported;
    private static bool _pendingStartRun;
    private static float _startRunRetryDeadline;
    private static bool _pendingReset;
    private static float _resetDeadline;

    internal static bool PreWarmGenerating { get; private set; }

    internal static void Tick()
    {
        if (_pendingStartRun)
        {
            if (Time.unscaledTime > _startRunRetryDeadline)
            {
                _pendingStartRun = false;
                Plugin.Log.LogWarning("[Run] START_RUN — 준비 대기 타임아웃 (90초).");
                return;
            }
            PreRunScript pre = UnityEngine.Object.FindObjectOfType<PreRunScript>();
            if (pre == null) return;
            if (!Net.IsRunning) return;
            _pendingStartRun = false;
            DoStartRun(pre); // 준비 완료 -> 실행
            return;
        }

        if (_pendingReset)
        {
            if (Time.unscaledTime > _resetDeadline)
            {
                _pendingReset = false;
                PreWarmGenerating = false;
                Plugin.Log.LogWarning("[Run] RESET — 준비 대기 타임아웃 (90초) — 재스폰 폴백."); // 재스폰 폴백
                return;
            }
            if (CasualtiesTogetherUtils.Util.IsWorldGenerated()) return;
            PreRunScript pre = UnityEngine.Object.FindObjectOfType<PreRunScript>();
            if (pre == null) return;
            if (!Net.IsRunning) return;
            _pendingReset = false;
            DoStartRun(pre);
        }
    }

    internal static void HandleStartRun()
    {
        if (CasualtiesTogetherUtils.Util.IsWorldGenerated()) // 이미 생성됨
        {
            return;
        }

        PreRunScript pre = UnityEngine.Object.FindObjectOfType<PreRunScript>();
        if (pre == null || !Net.IsRunning)
        {
            _pendingStartRun = true; // 서버 준비 대기 후 재시도
            _startRunRetryDeadline = Time.unscaledTime + 90f;
            return;
        }
        DoStartRun(pre);
    }

    private static void DoStartRun(PreRunScript pre)
    {
        try
        {
            PlayerPrefs.SetInt("didbasiccourse", 1);
            var traverse = HarmonyLib.Traverse.Create(pre);
            traverse.Method("StartRun").GetValue(); // 월드젠 시작
            PreWarmGenerating = true; // 이 구간 접속 거절
            Plugin.Log.LogInfo("[Run] START_RUN — 월드 생성 시작 (접속 거절 페이즈 진입).");
        }
        catch (System.Exception ex)
        {
            Plugin.Log.LogWarning($"[Run] START_RUN 실행 실패: {ex.Message}");
        }
    }

    internal static void HandleReset()
    {

        _pendingReset = true;
        _resetDeadline = Time.unscaledTime + 90f;
        _readyReported = false; // 재월드젠 READY 재보고 허용
        PreWarmGenerating = true;

        Plugin.Log.LogInfo("[Run] RESET — 레이어 초기화 시작 (ToMainMenu → 재월드젠).");
        HandleConsole("ToMainMenu");
    }

    internal static void HandleShutdown()
    {
        Plugin.Log.LogInfo("[Run] SHUTDOWN — 데이터 제출 후 종료.");
        foreach (ScavPlayer p in ScavPlayer.BodyToPlayerDict.Values) // 동결 제외 데이터 제출 후 종료
        {
            if (MigrationModule.IsFrozen(MigrationModule.PlayerKeyOf(p))) continue;
            SaveModule.SubmitPlayer(p);
        }
        OrchestratorClient.Instance?.WaitForOutboundFlush(1500);
        Application.Quit();
    }

    internal static void HandleConsole(string command)
    {
        if (string.IsNullOrEmpty(command)) return;
        try
        {
            var console = ConsoleScript.instance;
            if (console == null || console.logs == null)
            {
                Plugin.Log.LogWarning($"[Run] 콘솔 미준비 — 실행 불가: {command}");
                return;
            }

            int start = console.logs.Count;   // 실행 전 로그 위치
            var method = HarmonyLib.AccessTools.Method(typeof(ConsoleScript), "TryExecuteCommand");
            string[] args = command.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            method?.Invoke(console, new object[] { args, false });

            if (start <= console.logs.Count)
            {
                for (int i = start; i < console.logs.Count; i++)
                {
                    string line = System.Text.RegularExpressions.Regex.Replace(console.logs[i], "<[^>]*>", "");
                    Plugin.Log.LogInfo(line);
                }
            }
        }
        catch (System.Exception ex)
        {
            Plugin.Log.LogWarning($"[Run] 콘솔 명령 실패 ({command}): {ex.Message}");
        }
    }

    [HarmonyPatch(typeof(WorldGeneration), "FinishWorldGeneration")]
    internal static class FinishWorldGeneration_ConsumeSuppliesPatch
    {
        private static void Postfix()
        {
    
            if (WorldGeneration.world != null && WorldGeneration.world.totalTraveled <= 0)
            {
                WorldGeneration.world.totalTraveled = 1;
            }
        }
    }

    [HarmonyPatch(typeof(SharedMain), "CreatePlayerCharacters")]
    internal static class CreatePlayerCharacters_ReportReadyPatch
    {
        private static void Postfix()
        {
    
            PreWarmGenerating = false;

            if (_readyReported) return;
            if (OrchestratorClient.Instance == null || !OrchestratorClient.Instance.Connected) return;

            _readyReported = true;
            OrchestratorClient.Instance.SendEvent(MessageType.INSTANCE_READY,
                new { instanceKey = OrchestratorClient.Instance.InstanceKey });
            Plugin.Log.LogInfo($"[Run] INSTANCE_READY 보고 ({OrchestratorClient.Instance.InstanceKey}).");
        }
    }

    [HarmonyPatch(typeof(ServerMain), nameof(ServerMain.Server_AnnounceSeed))]
    internal static class Server_AnnounceSeed_StateSyncPatch
    {
        private static void Prefix(IReadOnlyList<knetid> to_who)
        {
                WorldGeneration world = WorldGeneration.world;
            if (world == null) return;
            WorldGeneration_GenerateWorld_MultiplayerPatch.firstworldgenparams.totalTraveled = world.totalTraveled;
            WorldGeneration_GenerateWorld_MultiplayerPatch.firstworldgenparams.biomeDepth = (byte)world.biomeDepth;
        }
    }

    [HarmonyPatch(typeof(Net), "ValidateNewClientHandshake")]
    internal static class ValidateNewClientHandshake_PreWarmRejectPatch
    {
        private static bool Prefix(ref string reject_reason, ref bool __result)
        {
            if (!PreWarmGenerating) return true;

            reject_reason = "Server is generating world, please try again.";
            __result = false;
            return false;
        }
    }
}
