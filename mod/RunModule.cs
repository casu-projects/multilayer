using System;
using System.Collections.Generic;
using HarmonyLib;
using KrokoshaCasualtiesMP;
using UnityEngine;

namespace CasuMod;

// 런/인스턴스 수명주기 — START_RUN, SHUTDOWN, CONSOLE, INSTANCE_READY 보고.
public static class RunModule
{
    private static bool _readyReported;
    private static bool _pendingStartRun;
    private static float _startRunRetryDeadline;
    // Prewarm RESET 대기 (ToMainMenu → 월드젠 재시작).
    private static bool _pendingReset;
    private static float _resetDeadline;

    // 프리웜 월드젠 페이즈 플래그 — 이 구간 접속은 "Server is generating world"로 거절해
    // announce를 놓치고 로비에 고착되는 레이스를 차단한다.
    internal static bool PreWarmGenerating { get; private set; }

    // 매 프레임 호출 — 프리웜 START_RUN/RESET 재시도 (PreRunScript + 전송 계층 준비 대기).
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
            if (!Net.running) return;
            _pendingStartRun = false;
            DoStartRun(pre);
            return;
        }

        if (_pendingReset)
        {
            if (Time.unscaledTime > _resetDeadline)
            {
                _pendingReset = false;
                PreWarmGenerating = false;
                Plugin.Log.LogWarning("[Run] RESET — 준비 대기 타임아웃 (90초) — 재스폰 폴백.");
                return;
            }
            // ToMainMenu 완료(월드 언로드) 후 월드젠 재시작.
            if (KrokoshaCasualtiesUtils.Util.IsWorldGenerated()) return;
            PreRunScript pre = UnityEngine.Object.FindObjectOfType<PreRunScript>();
            if (pre == null) return;
            if (!Net.running) return;
            _pendingReset = false;
            DoStartRun(pre);
        }
    }

    // START_RUN — 월드 생성 트리거. 네트워크 없이 StartRun을 실행하면 월드젠이 조용히
    // 정지하므로 준비될 때까지 Update에서 재시도한다.
    internal static void HandleStartRun()
    {
        if (KrokoshaCasualtiesUtils.Util.IsWorldGenerated())
        {
            return;
        }

        PreRunScript pre = UnityEngine.Object.FindObjectOfType<PreRunScript>();
        if (pre == null || !Net.running)
        {
            _pendingStartRun = true;
            _startRunRetryDeadline = Time.unscaledTime + 90f;
            return;
        }
        DoStartRun(pre);
    }

    private static void DoStartRun(PreRunScript pre)
    {
        try
        {
            // didbasiccourse==0이면 바닐라가 튜토리얼 경고를 띄우고 중단한다.
            PlayerPrefs.SetInt("didbasiccourse", 1);
            var traverse = HarmonyLib.Traverse.Create(pre);
            traverse.Method("StartRun").GetValue();
            PreWarmGenerating = true;
            Plugin.Log.LogInfo("[Run] START_RUN — 월드 생성 시작 (접속 거절 페이즈 진입).");
        }
        catch (System.Exception ex)
        {
            Plugin.Log.LogWarning($"[Run] START_RUN 실행 실패: {ex.Message}");
        }
    }

    // RESET — Prewarm 인스턴스 레이어 초기화 (ToMainMenu → 월드젠 재시작 → READY 재보고).
    internal static void HandleReset()
    {
        if (!KrokoshaScavMultiplayer.is_dedicated_server) return;

        _pendingReset = true;
        _resetDeadline = Time.unscaledTime + 90f;
        _readyReported = false;
        PreWarmGenerating = true;

        Plugin.Log.LogInfo("[Run] RESET — 레이어 초기화 시작 (ToMainMenu → 재월드젠).");
        HandleConsole("ToMainMenu");
    }

    // SHUTDOWN — 전 플레이어 데이터 제출 후 종료 (outbound flush 대기).
    internal static void HandleShutdown()
    {
        Plugin.Log.LogInfo("[Run] SHUTDOWN — 데이터 제출 후 종료.");
        foreach (NetPlayer p in NetPlayer.BodyToPlayerDict.Values)
        {
            // 마이그레이션 동결 중 플레이어 제외 — 파괴된 인벤토리 상태로 덮어쓰지 않는다.
            if (MigrationModule.IsFrozen(p.GetPersistentId())) continue;
            SaveModule.SubmitPlayer(p);
        }
        OrchestratorClient.Instance?.WaitForOutboundFlush(1500);
        Application.Quit();
    }

    // CONSOLE — 오케스트레이터 명령을 게임 콘솔로 실행, 실행 결과 로그를 릴레이한다.
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

            // 실행 후 추가된 라인 = 실행 결과 — 리치 텍스트 태그만 제거해 로그로 캡처.
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

    // 월드젠 완료 시 시작 보급품 "소진"(totalTraveled=1) — 후발 조인 클라이언트의
    // 재지급을 방지. 코루틴 시작 시점에 발동하므로 실제 완료는 아니다.
    [HarmonyPatch(typeof(WorldGeneration), "FinishWorldGeneration")]
    internal static class FinishWorldGeneration_ConsumeSuppliesPatch
    {
        private static void Postfix()
        {
            if (!KrokoshaScavMultiplayer.is_dedicated_server) return;

            if (WorldGeneration.world != null && WorldGeneration.world.totalTraveled <= 0)
            {
                WorldGeneration.world.totalTraveled = 1;
            }
        }
    }

    // INSTANCE_READY 보고 — 실제 월드젠 완료 시점(CreatePlayerCharacters).
    [HarmonyPatch(typeof(SharedMain), "CreatePlayerCharacters")]
    internal static class CreatePlayerCharacters_ReportReadyPatch
    {
        private static void Postfix()
        {
            if (!KrokoshaScavMultiplayer.is_dedicated_server) return;

            // 접속 거절 페이즈 종료.
            PreWarmGenerating = false;

            if (_readyReported) return;
            if (OrchestratorClient.Instance == null || !OrchestratorClient.Instance.Connected) return;

            _readyReported = true;
            OrchestratorClient.Instance.SendEvent("INSTANCE_READY",
                new { instanceKey = OrchestratorClient.Instance.InstanceKey });
            Plugin.Log.LogInfo($"[Run] INSTANCE_READY 보고 ({OrchestratorClient.Instance.InstanceKey}).");
        }
    }

    // 월드젠 announce 파라미터에 현재 상태 반영 — 소진된 totalTraveled=1을 다시 넣어
    // 클라이언트가 라이프팟/보급품 장면을 재생성하지 않게 한다.
    [HarmonyPatch(typeof(ServerMain), nameof(ServerMain.Server_AnnounceSeed))]
    internal static class Server_AnnounceSeed_StateSyncPatch
    {
        private static void Prefix(IReadOnlyList<knetid> to_who)
        {
            if (!KrokoshaScavMultiplayer.is_dedicated_server) return;
            WorldGeneration world = WorldGeneration.world;
            if (world == null) return;
            WorldGeneration_GenerateWorld_MultiplayerPatch.firstworldgenparams.totalTraveled = world.totalTraveled;
            WorldGeneration_GenerateWorld_MultiplayerPatch.firstworldgenparams.biomeDepth = (byte)world.biomeDepth;
        }
    }

    // 프리웜 월드젠 페이즈의 접속 거절 — 게이트웨이가 "일시적 거절 → 재시도"로 처리하는
    // 사유 문자열을 그대로 사용한다.
    [HarmonyPatch(typeof(Net), "ValidateNewClientHandshake")]
    internal static class ValidateNewClientHandshake_PreWarmRejectPatch
    {
        private static bool Prefix(ref string reject_reason, ref bool __result)
        {
            if (!KrokoshaScavMultiplayer.is_dedicated_server || !PreWarmGenerating) return true;

            reject_reason = "Server is generating world, please try again.";
            __result = false;
            return false;
        }
    }
}
