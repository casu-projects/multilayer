using System;
using System.Collections.Generic;
using HarmonyLib;
using Together;
using UnityEngine;

namespace CasuMod;

// 런/인스턴스 수명주기 - START_RUN, SHUTDOWN, CONSOLE, INSTANCE_READY 보고
// 프리웜: START_RUN은 모드 연결(MOD_HELLO) 직후 도착 - PreRunScript 로드 전이면
// Update에서 재시도한다 (월드젠은 플레이어 접속과 무관하게 부팅 직후 시작)
public static class RunModule
{
    private static bool _readyReported;
    private static bool _pendingStartRun;
    private static float _startRunRetryDeadline;
    // Prewarm 인스턴스 레이어 초기화(RESET) 대기 - ToMainMenu -> 월드젠 재시작
    // 오케스트레이터가 유휴 Prewarm 인스턴스에 발신 (프로세스 생존 유휴 대기)
    private static bool _pendingReset;
    private static float _resetDeadline;

    // 프리웜 월드젠 페이즈 플래그: StartRun 호출 ~ 월드젠 완료까지 true.
    // 이 구간 접속을 "Server is generating world"로 거절해 announce 유실/로비 고착 레이스를 차단한다.
    internal static bool PreWarmGenerating { get; private set; }

    // 매 프레임 호출 (OrchestratorClient.Update 경유) - 프리웜 START_RUN 재시도
    // 조건: PreRunScript 존재 + 전송 계층(전용 서버) 가동 - 순서 보장 (startserver 먼저)
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
            // ToMainMenu 완료(월드 언로드) + 전용 서버 가동 확인 후 월드젠 재시작
            if (CasualtiesTogetherUtils.Util.IsWorldGenerated()) return;
            PreRunScript pre = UnityEngine.Object.FindObjectOfType<PreRunScript>();
            if (pre == null) return;
            if (!Net.IsRunning) return;
            _pendingReset = false;
            DoStartRun(pre);
        }
    }

    // START_RUN - 월드 생성 트리거 (구 "map run" -> PreRunScript.StartRun)
    // 전용 서버는 튜토리얼 경고가 필요 없으므로 didbasiccourse를 1로 설정 후 시작한다
    internal static void HandleStartRun()
    {
        // 이미 세계가 생성된 인스턴스는 런 시작 불필요 (재접속/재수용 방어)
        if (CasualtiesTogetherUtils.Util.IsWorldGenerated())
        {
            return;
        }

        // 프리웜: MOD_HELLO 직후에는 PreRunScript/서버가 아직 준비되지 않을 수 있다.
        // 네트워크 없이 StartRun을 실행하면 월드젠이 조용히 정지하므로 둘 다 충족할 때까지 재시도한다.
        PreRunScript pre = UnityEngine.Object.FindObjectOfType<PreRunScript>();
        if (pre == null || !Net.IsRunning)
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
            // 바닐라 StartRun은 didbasiccourse==0이면 튜토리얼 경고를 띄우고 중단한다
            PlayerPrefs.SetInt("didbasiccourse", 1);
            var traverse = HarmonyLib.Traverse.Create(pre);
            traverse.Method("StartRun").GetValue();
            // 씬 로드~월드젠 완료까지 접속 거절 페이즈 시작 (핸드셰이크 패치와 연동)
            PreWarmGenerating = true;
            Plugin.Log.LogInfo("[Run] START_RUN — 월드 생성 시작 (접속 거절 페이즈 진입).");
        }
        catch (System.Exception ex)
        {
            Plugin.Log.LogWarning($"[Run] START_RUN 실행 실패: {ex.Message}");
        }
    }

    // RESET: 프로세스 생존 상태에서 ToMainMenu → 재월드젠 → INSTANCE_READY 재보고 → 유휴 대기.
    // 재생성 중 접속은 PreWarmGenerating으로 거절하고, 90초 타임아웃 시 오케스트레이터가 크래시 처리 후 재스폰한다.
    internal static void HandleReset()
    {

        _pendingReset = true;
        _resetDeadline = Time.unscaledTime + 90f;
        _readyReported = false;
        PreWarmGenerating = true;

        Plugin.Log.LogInfo("[Run] RESET — 레이어 초기화 시작 (ToMainMenu → 재월드젠).");
        HandleConsole("ToMainMenu");
    }

    // SHUTDOWN - 전 플레이어 데이터 제출 후 종료 (오케스트레이터 종료 캐스케이드)
    // 제출이 quit으로 유실되지 않도록 outbound flush를 대기한 뒤 종료한다
    internal static void HandleShutdown()
    {
        Plugin.Log.LogInfo("[Run] SHUTDOWN — 데이터 제출 후 종료.");
        foreach (ScavPlayer p in ScavPlayer.BodyToPlayerDict.Values)
        {
            // 마이그레이션 동결 중(FREEZE) 플레이어 제외 - 캡처 데이터를 파괴된 인벤토리
            // 상태로 덮어쓰지 않는다 (WAL/캡처가 재시작 복구 담당 - OnDestroy 패치 -5와 동일)
            if (MigrationModule.IsFrozen(MigrationModule.PlayerKeyOf(p))) continue;
            SaveModule.SubmitPlayer(p);
        }
        // fire-and-forget 제출(PLAYER_DATA_SUBMIT)이 소켓으로 전달될 시간을 확보
        OrchestratorClient.Instance?.WaitForOutboundFlush(1500);
        Application.Quit();
    }

    // CONSOLE - 오케스트레이터 명령을 게임 콘솔로 실행 (ConsoleScript.TryExecuteCommand)
    // 실행 결과(게임 콘솔 로그에 추가된 라인 - 성공/실패 메시지)를 일반 로그처럼 릴레이한다:
    // Plugin.Log -> 게임 stdout -> 에이전트 DrainAsync -> 오케스트레이터 표시. 출력이 없는
    // 명령은 자연히 로그에 남지 않는다 (실행 여부는 인스턴스 태그로 식별)
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

            // 실행 후 추가된 라인 = 실행 결과. 리치 텍스트 태그(<color> 등)만 제거하고
            // 게임 콘솔 타임스탬프([mm:ss])는 유지한 채 그대로 로그로 캡처한다
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

    // 월드젠 완료 시 시작 보급품 "소진" 처리 (totalTraveled=1) - 이후 조인하는
    // 클라이언트가 월드젠 파라미터로 1을 받아 라이프팟/보급품을 재지급받지 않게 한다
    // 주의: FinishWorldGeneration은 코루틴이므로 이 Postfix는 코루틴 시작 시점에 발동한다
    // (실제 완료 아님). INSTANCE_READY 보고는 CreatePlayerCharacters Postfix가 담당한다
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

    // INSTANCE_READY 보고 - 실제 월드젠 완료 시점.
    // CreatePlayerCharacters(전 바디 생성 + 오브젝트 등록 완료)에서 보고한다.
    // 코루틴 시작 시점에 보고하면 READY가 월드젠 완료보다 ~1초 앞서 발행되어 게이트웨이가 조기 연결/거절 폴링을 반복한다.
    [HarmonyPatch(typeof(SharedMain), "CreatePlayerCharacters")]
    internal static class CreatePlayerCharacters_ReportReadyPatch
    {
        private static void Postfix()
        {
    
            // 접속 거절 페이즈 종료 - 이후 접속은 announce(10021/10010)를 정상 수신한다
            PreWarmGenerating = false;

            if (_readyReported) return;
            if (OrchestratorClient.Instance == null || !OrchestratorClient.Instance.Connected) return;

            _readyReported = true;
            OrchestratorClient.Instance.SendEvent("INSTANCE_READY",
                new { instanceKey = OrchestratorClient.Instance.InstanceKey });
            Plugin.Log.LogInfo($"[Run] INSTANCE_READY 보고 ({OrchestratorClient.Instance.InstanceKey}).");
        }
    }

    // 재접속/후발 클라이언트가 받는 월드젠 파라미터에 현재 상태를 반영 (B)
    // firstworldgenparams는 월드젠 시작 시점에 캡처된 struct이므로, 소진된 totalTraveled=1을
    // 다시 넣어주지 않으면 클라이언트가 라이프팟/시작 보급품 장면을 재생성한다
    // 첫 월드젠 announce(생성 중)에서는 world.totalTraveled=0이라 캡처값과 동일해 영향 없음
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

    // 프리웜 월드젠 중 접속 거절: base 거절 조건(generatingWorld)만으로는 startserver~씬 로드 창의
    // 접속이 announce를 놓쳐 로비에 고착된다. PreWarmGenerating 구간에 동일 사유로 거절하면 게이트웨이가 재시도한다.
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
