using System.Diagnostics;
using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace CasuMpAgent;

internal static class Program
{
    // SHUTDOWN 수신 시 메인 루프 종료 요청
    private static Action? _shutdownRequested;
    private static void Main(string[] args)
    {
        string configPath = args.Length > 0 ? args[0] : "agent.json";
        AgentConfig config = AgentConfig.Load(configPath);
        Logger.Init($"agent:{config.MachineId}");

        string localIp = DetectLocalIPv4();

        Logger.Local($"에이전트 {config.MachineId} (IP: {localIp})");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        _shutdownRequested = () => cts.Cancel();

        var instances = new Dictionary<string, InstanceProcess>();
        var inbound = new ConcurrentQueue<(ControlMessage Msg, Action<ControlMessage> Ack)>();

        CleanupOrphans(config);

        var channel = new ControlChannel(config, localIp, inbound);
        _ = channel.RunAsync(cts.Token);

        while (!cts.IsCancellationRequested)
        {
            while (inbound.TryDequeue(out var item))
            {
                try { HandleCommand(config, instances, channel, item.Msg, item.Ack); }
                catch (Exception ex) { Logger.Info($"명령 처리 실패: {ex.Message}"); }
            }

            foreach (InstanceProcess proc in instances.Values.ToList())
            {
                proc.Tick();
                if (proc.HasExited)
                {
                    instances.Remove(proc.Key);
                    Logger.Info($"{proc.Key} 종료 (code {proc.ExitCode}).");
                    channel.Report(ControlMessage.Create(0, "INSTANCE_EXITED",
                        new { instanceKey = proc.Key, code = proc.ExitCode ?? -1 }));
                }
            }

            Thread.Sleep(100);
        }

        foreach (InstanceProcess proc in instances.Values)
        {
            proc.Stop();
        }
    }

    private static void HandleCommand(AgentConfig config, Dictionary<string, InstanceProcess> instances,
        ControlChannel channel, ControlMessage msg, Action<ControlMessage> ack)
    {
        switch (msg.Type)
        {
            case "VERBOSE":
            {
                var payload = msg.PayloadAs<VerbosePayload>();
                Logger.Verbose = payload?.On ?? false;
                Logger.Info($"verbose {(Logger.Verbose ? "켬" : "끔")} (오케스트레이터).");
                ack(ControlMessage.Ack(msg.Seq, true));
                return;
            }
            case "SPAWN":
            {
                var payload = msg.PayloadAs<SpawnPayload>();
                if (payload == null)
                {
                    ack(ControlMessage.Ack(msg.Seq, false, "payload 없음"));
                    return;
                }
                if (instances.ContainsKey(payload.InstanceKey))
                {
                    ack(ControlMessage.Ack(msg.Seq, false, "이미 실행 중"));
                    return;
                }
                if (!IsPortFreeOnOs(payload.Port))
                {
                    Logger.Debug($"{payload.InstanceKey} 포트 {payload.Port} 점유 중 — SPAWN 거부.");
                    ack(ControlMessage.Ack(msg.Seq, false, "port in use"));
                    return;
                }

                InstanceProcess? proc = InstanceProcess.Spawn(config, payload);
                if (proc == null)
                {
                    ack(ControlMessage.Ack(msg.Seq, false, "spawn failed"));
                    return;
                }
                instances[payload.InstanceKey] = proc;
                WritePid(config, payload.InstanceKey, proc);
                ack(ControlMessage.Ack(msg.Seq, true));
                break;
            }
            case "STOP":
            {
                var payload = msg.PayloadAs<StopPayload>();
                if (payload == null || !instances.Remove(payload.InstanceKey, out InstanceProcess? proc))
                {
                    ack(ControlMessage.Ack(msg.Seq, false, "인스턴스 없음"));
                    return;
                }
                proc.Stop();
                proc.Cleanup();
                DeletePid(config, payload.InstanceKey);
                channel.Report(ControlMessage.Create(0, "INSTANCE_EXITED",
                    new { instanceKey = payload.InstanceKey, code = proc.ExitCode ?? 0 }));
                ack(ControlMessage.Ack(msg.Seq, true));
                break;
            }
            case "SHUTDOWN":
            {
                Logger.Info("종료 신호 수신 — 인스턴스 정리 후 종료.");
                ack(ControlMessage.Ack(msg.Seq, true));
                _shutdownRequested?.Invoke();
                break;
            }
            default:
                ack(ControlMessage.Ack(msg.Seq, false, $"unknown: {msg.Type}"));
                break;
        }
    }

    // 호스트 IP 탐지
    private static string DetectLocalIPv4()
    {
        foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            string name = ni.Name;
            if (name == "lo" || name.StartsWith("docker") || name.StartsWith("veth")
                || name.StartsWith("br-") || name.StartsWith("tun") || name.StartsWith("virbr")) continue;
            foreach (UnicastIPAddressInformation ip in ni.GetIPProperties().UnicastAddresses)
            {
                if (ip.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (IPAddress.IsLoopback(ip.Address)) continue;
                byte[] b = ip.Address.GetAddressBytes();
                if (b[0] == 169 && b[1] == 254) continue;
                return ip.Address.ToString();
            }
        }
        return "127.0.0.1";
    }


    private static bool IsPortFreeOnOs(int port)
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Bind(new IPEndPoint(IPAddress.Any, port));
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static string PidPath(AgentConfig config, string key) =>
        Path.Combine(Path.GetFullPath(config.InstancesDir), Sanitize(key), "pid");

    private static void WritePid(AgentConfig config, string key, InstanceProcess proc)
    {
        try
        {
            string path = PidPath(config, key);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, proc.Pid.ToString());
        }
        catch { }
    }

    private static void DeletePid(AgentConfig config, string key)
    {
        try { File.Delete(PidPath(config, key)); } catch { }
    }

    private static void CleanupOrphans(AgentConfig config)
    {
        string root = Path.GetFullPath(config.InstancesDir);
        if (!Directory.Exists(root)) return;

        foreach (string dir in Directory.GetDirectories(root))
        {
            string pidFile = Path.Combine(dir, "pid");
            if (!File.Exists(pidFile)) continue;
            try
            {
                if (int.TryParse(File.ReadAllText(pidFile), out int pid))
                {
                    Process? p = Process.GetProcessById(pid);
                    if (p != null && !p.HasExited)
                    {
                        Logger.Info($"orphan 프로세스 정리: PID {pid} ({Path.GetFileName(dir)}).");
                        p.Kill(entireProcessTree: true);
                    }
                }
            }
            catch (ArgumentException) { } // 이미 없음
            catch (Exception ex)
            {
                Logger.Info($"orphan 정리 실패 ({Path.GetFileName(dir)}): {ex.Message}");
            }
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
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

public sealed class StopPayload
{
    public string InstanceKey { get; set; } = "";
}
