using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CasuMpAgent;

public sealed class InstanceProcess
{
    private const int SigCont = 18;
    private const int SigStop = 19;
    private const int StopTimeoutMilliseconds = 3000;

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int sig);

    private readonly AgentConfig _config;
    private readonly Process _process;
    private readonly StreamWriter _stdin;
    private readonly bool _usesProcessGroup;

    public string Key { get; }
    public int Depth { get; }
    public int Port { get; }
    public int Pid => _process.Id;
    public bool HasExited { get; private set; }
    public int? ExitCode { get; private set; }
    public bool IsSuspended { get; private set; }

    private InstanceProcess(AgentConfig config, string key, int depth, int port, Process process, bool usesProcessGroup)
    {
        _config = config;
        Key = key;
        Depth = depth;
        Port = port;
        _process = process;
        _stdin = process.StandardInput;
        _usesProcessGroup = usesProcessGroup;
    }

    public static InstanceProcess? Spawn(AgentConfig config, SpawnPayload payload)
    {
        if (string.IsNullOrEmpty(config.GameExecutablePath))
        {
            Logger.Info($"SPAWN 거부 — agent.json의 GameExecutablePath가 설정되지 않았습니다.");
            return null;
        }

        string homeDir = Path.Combine(Path.GetFullPath(config.InstancesDir), Sanitize(payload.InstanceKey), "home");
        Directory.CreateDirectory(homeDir);

        string unityLogPath = Path.Combine(homeDir, "unity.log");
        string exeDir = Path.GetDirectoryName(Path.GetFullPath(config.GameExecutablePath))!;
        string gameExePath = Path.Combine(exeDir, "CasualtiesUnknown.x86_64");

        string args = $"{Quote(gameExePath)} --ksmulti-servername \"dedicated\" "
            + $"--ksmulti-setpass \"{payload.ServerPassword}\" "
            + $"--ksmulti-runcommand \"startserver {payload.Port}\" "
            + $"--ksmulti-runcommand \"maxplayers 200\" "
            + $"-batchmode -nographics "
            + $"-logFile \"{unityLogPath}\"";

        string? setsid = FindSetsid();
        bool usesProcessGroup = setsid != null;
        string fileName = setsid ?? config.GameExecutablePath;
        string arguments = usesProcessGroup ? $"{Quote(config.GameExecutablePath)} {args}" : args;

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = homeDir,
        };
        psi.Environment["HOME"] = homeDir;
        psi.Environment["XDG_CONFIG_HOME"] = Path.Combine(homeDir, ".config");

        psi.Environment["CASU_START_DEPTH"] = payload.Depth.ToString();
        psi.Environment["CASU_INSTANCE_KEY"] = payload.InstanceKey;
        psi.Environment["CASU_PORT"] = payload.Port.ToString();
        psi.Environment["CASU_ORCH_ADDR"] = config.OrchestratorAddr;

        try
        {
            Process process = Process.Start(psi)
                ?? throw new InvalidOperationException("Process.Start 반환 null");

            _ = DrainAsync(process.StandardOutput, payload.InstanceKey);
            _ = DrainAsync(process.StandardError, payload.InstanceKey);
            return new InstanceProcess(config, payload.InstanceKey, payload.Depth, payload.Port, process, usesProcessGroup);
        }
        catch (Exception ex)
        {
            Logger.Info($"{payload.InstanceKey} 스폰 실패: {ex.Message}");
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
                IsSuspended = false;
                ExitCode = _process.ExitCode;
            }
        }
        catch (InvalidOperationException)
        {
            HasExited = true;
            IsSuspended = false;
        }
    }

    public bool Suspend()
    {
        if (HasExited) return false;
        if (IsSuspended) return true;
        if (!SendSignal(SigStop)) return false;
        IsSuspended = true;
        return true;
    }

    public bool Resume()
    {
        if (HasExited) return false;
        if (!IsSuspended) return true;
        if (!SendSignal(SigCont)) return false;
        IsSuspended = false;
        return true;
    }

    public async Task StopAsync()
    {
        if (IsSuspended) Resume();

        try
        {
            _stdin.WriteLine("quit");
            _stdin.Flush();
        }
        catch { }

        try
        {
            using var cts = new CancellationTokenSource(StopTimeoutMilliseconds);
            await _process.WaitForExitAsync(cts.Token);
            return;
        }
        catch (OperationCanceledException)
        {
        }
        catch (InvalidOperationException)
        {
            return;
        }
        catch (Exception ex)
        {
            Logger.Info($"{Key} 종료 대기 오류: {ex.Message}");
        }

        Kill();
    }

    public void Kill()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                Logger.Info($"{Key} 강제 종료.");
            }
        }
        catch (InvalidOperationException) { }
        catch (Exception ex)
        {
            Logger.Info($"{Key} 강제 종료 실패: {ex.Message}");
        }
    }

    public void Cleanup()
    {
        try
        {
            string homeDir = Path.Combine(Path.GetFullPath(_config.InstancesDir), Sanitize(Key), "home");
            string? parent = Path.GetDirectoryName(homeDir);

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
            catch (Exception)
            {
            }

            if (parent != null && Directory.Exists(parent))
            {
                Directory.Delete(parent, recursive: true);
            }
        }
        catch (Exception)
        {
        }
    }

    private bool SendSignal(int signal)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return false;
        try
        {
            int target = _usesProcessGroup ? -Pid : Pid;
            if (kill(target, signal) == 0) return true;
            if (_usesProcessGroup && kill(Pid, signal) == 0) return true;
            Logger.Info($"{Key} 시그널 {signal} 실패 (errno {Marshal.GetLastWin32Error()}).");
            return false;
        }
        catch (Exception ex)
        {
            Logger.Info($"{Key} 시그널 {signal} 예외: {ex.Message}");
            return false;
        }
    }

    private static string? FindSetsid()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return null;
        foreach (string path in new[] { "/usr/bin/setsid", "/bin/setsid" })
        {
            if (File.Exists(path)) return path;
        }
        return null;
    }

    private static string Quote(string value) => $"\"{value.Replace("\"", "\\\"")}\"";

    private static async Task DrainAsync(StreamReader reader, string key)
    {
        try
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                Logger.Info(line, key);
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
