using System.Diagnostics;

namespace CasuMpAgent;

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

        psi.Environment["CASU_START_DEPTH"] = payload.Depth.ToString();
        psi.Environment["CASU_INSTANCE_KEY"] = payload.InstanceKey;
        psi.Environment["CASU_PORT"] = payload.Port.ToString();
        psi.Environment["CASU_ORCH_ADDR"] = config.OrchestratorAddr;

        try
        {
            Process process = Process.Start(psi)
                ?? throw new InvalidOperationException("Process.Start 반환 null");

            _ = DrainAsync(process, payload.InstanceKey);
            return new InstanceProcess(config, payload.InstanceKey, payload.Port, process);
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
                ExitCode = _process.ExitCode;
            }
        }
        catch (InvalidOperationException)
        {
            HasExited = true;
        }
    }

    public void Stop()
    {
        try
        {
            _stdin.WriteLine("quit");
            _stdin.Flush();
            int waitMilliseconds = checked(_config.StopGraceSeconds * 1000);
            _process.WaitForExit(waitMilliseconds);
        }
        catch { }

        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException) { }
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

    private static async Task DrainAsync(Process process, string key)
    {
        try
        {
            while (await process.StandardOutput.ReadLineAsync() is { } line)
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
