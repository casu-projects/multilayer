using System.Diagnostics;

namespace CasuMpAgent;

/// <summary>단일 인스턴스 프로세스 — 스폰/감시/정지 (D3/G-3/G-4).</summary>
public sealed class InstanceProcess
{
    private readonly AgentConfig _config;
    private readonly Process _process;
    private readonly StreamWriter _stdin;

    public string Key { get; }
    public int Port { get; }
    public int Pid => _process.Id;
    public bool HasExited { get; private set; }
    public int? ExitCode { get; private set; }

    private InstanceProcess(AgentConfig config, string key, int port, Process process)
    {
        _config = config;
        Key = key;
        Port = port;
        _process = process;
        _stdin = process.StandardInput;
    }

    /// <summary>SPAWN 페이로드로 프로세스 실행 (HOME 격리 + 환경변수).</summary>
    public static InstanceProcess? Spawn(AgentConfig config, SpawnPayload payload)
    {
        // 게임 경로 미설정 — 명확한 실패 (Path.GetFullPath("") 예외/ack 타임아웃 대신).
        if (string.IsNullOrEmpty(config.GameExecutablePath))
        {
            AgentLog.Info($"SPAWN 거부 — agent.json의 GameExecutablePath가 설정되지 않았습니다.");
            return null;
        }

        string homeDir = Path.Combine(Path.GetFullPath(config.InstancesDir), Sanitize(payload.InstanceKey), "home");
        Directory.CreateDirectory(homeDir);

        string unityLogPath = Path.Combine(homeDir, "unity.log");
        string exeDir = Path.GetDirectoryName(Path.GetFullPath(config.GameExecutablePath))!;
        string gameExePath = Path.Combine(exeDir, "CasualtiesUnknown.x86_64");

        // 하드코딩된 시작 인자 (구 오케스트레이터와 동일 — template은 설정에 노출하지 않음):
        // 서버명 "dedicated" 고정, 최대 플레이어 200 (게임 콘솔 명령 — runcommand가 시작 시
        // 순서대로 실행: startserver → maxplayers), 비밀번호/포트/유니티 로그 경로만 동적.
        string args = $"{gameExePath} --ksmulti-servername \"dedicated\" "
            + $"--ksmulti-setpass \"{payload.ServerPassword}\" "
            + $"--ksmulti-runcommand \"startserver {payload.Port}\" "
            + $"--ksmulti-runcommand \"maxplayers 200\" "
            + (config.HeadlessMode ? "-batchmode -nographics " : "")
            + $"-logFile \"{unityLogPath}\"";

        var psi = new ProcessStartInfo
        {
            FileName = config.GameExecutablePath,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = homeDir,
        };
        psi.Environment["HOME"] = homeDir;
        psi.Environment["XDG_CONFIG_HOME"] = Path.Combine(homeDir, ".config");

        // 신규 모드(CasuAgent)용 — 인스턴스가 오케스트레이터에 직접 연결 (D3).
        // 주소는 SPAWN 페이로드가 아니라 에이전트 자신의 OrchestratorAddr을 주입
        // (인스턴스는 에이전트와 같은 머신에서 실행 — 동일 경로가 보장됨).
        psi.Environment["CASU_START_DEPTH"] = payload.Depth.ToString();
        psi.Environment["CASU_INSTANCE_KEY"] = payload.InstanceKey;
        psi.Environment["CASU_PORT"] = payload.Port.ToString();
        psi.Environment["CASU_ORCH_ADDR"] = config.OrchestratorAddr;

        try
        {
            Process process = Process.Start(psi)
                ?? throw new InvalidOperationException("Process.Start 반환 null");
            AgentLog.Debug($"{payload.InstanceKey} 스폰 (포트 {payload.Port}, HOME {homeDir})");

            // stdout은 진단 로그로만 (D3 — 프로토콜은 소켓)
            _ = DrainAsync(process, payload.InstanceKey);
            return new InstanceProcess(config, payload.InstanceKey, payload.Port, process);
        }
        catch (Exception ex)
        {
            AgentLog.Info($"{payload.InstanceKey} 스폰 실패: {ex.Message}");
            return null;
        }
    }

    public void Tick()
    {
        if (HasExited) return;
        try
        {
            if (_process.HasExited)
            {
                HasExited = true;
                ExitCode = _process.ExitCode;
            }
        }
        catch (InvalidOperationException)
        {
            HasExited = true;
        }
    }

    /// <summary>G-4: stdin으로 quit 전달 → 유예 대기 → 강제 종료.</summary>
    public void Stop()
    {
        try
        {
            _stdin.WriteLine("quit");
            _stdin.Flush();
            // TimeSpan.Milliseconds는 '전체 밀리초'가 아니라 1초 미만 나머지라서, 5초도 0ms가 되버려요... 그래서 설정한 초를 실제 대기 시간으로 제대로 바꿔줍니다.
            int waitMilliseconds = checked(_config.StopGraceSeconds * 1000);
            _process.WaitForExit(waitMilliseconds);
        }
        catch { }

        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                AgentLog.Debug($"{Key} 강제 종료.");
            }
        }
        catch (InvalidOperationException) { }
    }

    /// <summary>종료 후 홈 디렉토리/로그 정리 (G-4). 진단용으로 unity.log는 보존한다.</summary>
    public void Cleanup()
    {
        try
        {
            string homeDir = Path.Combine(Path.GetFullPath(_config.InstancesDir), Sanitize(Key), "home");
            string? parent = Path.GetDirectoryName(homeDir);

            // 진단용: 유니티 로그 보존 (게임 인스턴스 옆 logs/ — {gameDir}/logs/{key}-unity.log,
            // 홈은 삭제됨). 경로는 GameExecutablePath에서 파생 — 인스턴스가 어디 있든 그 옆에 붙는다.
            try
            {
                string unityLog = Path.Combine(homeDir, "unity.log");
                if (File.Exists(unityLog))
                {
                    string? gameDir = Path.GetDirectoryName(_config.GameExecutablePath);
                    if (string.IsNullOrEmpty(gameDir))
                    {
                    }
                    else
                    {
                        string logsDir = Path.Combine(gameDir, "logs");
                        Directory.CreateDirectory(logsDir);
                        string preservedPath = Path.Combine(logsDir, $"{Sanitize(Key)}-unity.log");
                        File.Copy(unityLog, preservedPath, overwrite: true);
                    }
                }
            }
            catch (Exception ex)
            {
                AgentLog.Debug($"{Key} unity.log 보존 실패: {ex.Message}");
            }

            if (parent != null && Directory.Exists(parent))
            {
                Directory.Delete(parent, recursive: true);
            }
        }
        catch (Exception ex)
        {
            AgentLog.Debug($"{Key} 홈 정리 실패: {ex.Message}");
        }
    }

    private static async Task DrainAsync(Process process, string key)
    {
        try
        {
            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                // 인스턴스 키는 메시지가 아니라 소스에 부여 — 표시 "[agent:m1/depth-1]".
                AgentLog.Info(line, key);
            }
        }
        catch { }
    }

    private static string Sanitize(string s)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        return string.Create(s.Length, (s, invalid), (span, state) =>
        {
            for (int i = 0; i < state.s.Length; i++)
                span[i] = state.invalid.Contains(state.s[i]) ? '_' : state.s[i];
        });
    }
}

public sealed class SpawnPayload
{
    public string InstanceKey { get; set; } = "";
    public int Depth { get; set; }
    public int Port { get; set; }
    public string? ServerName { get; set; }
    public string? ServerPassword { get; set; }
}

/// <summary>디버그 로그 표시 상태 (오케스트레이터 VERBOSE 메시지).</summary>
public sealed class VerbosePayload
{
    public bool On { get; set; }
}