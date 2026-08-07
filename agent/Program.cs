using System.Diagnostics;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace CasuMpAgent;

internal static class Program
{
    /// <summary>시작 시 자동 탐지된 게이트웨이 직결용 호스트 IP (AGENT_HELLO address).</summary>
    private static string _localIp = "127.0.0.1";

    /// <summary>SHUTDOWN 수신 시 메인 루프 종료 요청 (Main에서 cts.Cancel과 연결).</summary>
    private static Action? _shutdownRequested;
    private static void Main(string[] args)
    {
        string configPath = args.Length > 0 ? args[0] : "agent.json";
        AgentConfig config = AgentConfig.Load(configPath);
        AgentLog.Init(config.MachineId);

        // 게이트웨이가 인스턴스에 직결할 호스트 IP — 자동 탐지 (config 필드 없음)
        string localIp = AgentConfig.DetectLocalIPv4();
        _localIp = localIp;

        AgentLog.Info($"구성 로드: {configPath}");
        // 휴리스틱 결과는 릴레이하지 않음 — 에이전트 콘솔 자체에만 (게이트웨이 부팅 로그와 동일 방식)
        Console.WriteLine($"머신 {config.MachineId} — 에이전트 서버 IP: {localIp}");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        _shutdownRequested = () => cts.Cancel();

        var instances = new Dictionary<string, InstanceProcess>();
        var inbound = new ConcurrentQueue<(ControlMessage Msg, Action<ControlMessage> Ack)>();

        CleanupOrphans(config);

        _ = RunControlChannelAsync(config, inbound, cts.Token);

        while (!cts.IsCancellationRequested)
        {
            // 수신 명령 처리
            while (inbound.TryDequeue(out var item))
            {
                try { HandleCommand(config, instances, item.Msg, item.Ack); }
                catch (Exception ex) { AgentLog.Info($"명령 처리 실패: {ex.Message}"); }
            }

            // 프로세스 감시
            foreach (InstanceProcess proc in instances.Values.ToList())
            {
                proc.Tick();
                if (proc.HasExited)
                {
                    instances.Remove(proc.Key);
                    AgentLog.Info($"{proc.Key} 종료 (code {proc.ExitCode}).");
                    // 오케스트레이터에 보고 (연결이 없다면 무시 — 재연결 후 재등록 대상)
                    outboundReports.Enqueue(ControlMessage.Create(0, "INSTANCE_EXITED",
                        new { instanceKey = proc.Key, code = proc.ExitCode ?? -1 }));
                }
            }

            Thread.Sleep(100);
        }

        AgentLog.Info("종료 중 — 인스턴스 정리.");
        foreach (InstanceProcess proc in instances.Values)
        {
            proc.Stop();
        }
    }

    /// <summary>오케스트레이터로 보낼 보고 메시지 (연결 수립 시 flush).</summary>
    private static readonly ConcurrentQueue<ControlMessage> outboundReports = new();

    // ── 명령 처리 ──

    private static void HandleCommand(AgentConfig config, Dictionary<string, InstanceProcess> instances,
        ControlMessage msg, Action<ControlMessage> ack)
    {
        switch (msg.Type)
        {
            case "VERBOSE":
            {
                var payload = msg.PayloadAs<VerbosePayload>();
                AgentLog.Verbose = payload?.On ?? false;
                AgentLog.Info($"verbose {(AgentLog.Verbose ? "켬" : "끔")} (오케스트레이터).");
                ack(ControlMessage.Create(msg.Seq, "ACK", new { ok = true }));
                return;
            }
            case "SPAWN":
            {
                var payload = msg.PayloadAs<SpawnPayload>();
                if (payload == null)
                {
                    ack(ControlMessage.Create(msg.Seq, "ACK", new { ok = false, reason = "payload 없음" }));
                    return;
                }
                if (instances.ContainsKey(payload.InstanceKey))
                {
                    ack(ControlMessage.Create(msg.Seq, "ACK", new { ok = false, reason = "이미 실행 중" }));
                    return;
                }
                if (!IsPortFreeOnOs(payload.Port))
                {
                    AgentLog.Debug($"{payload.InstanceKey} 포트 {payload.Port} 점유 중 — SPAWN 거부.");
                    ack(ControlMessage.Create(msg.Seq, "ACK", new { ok = false, reason = "port in use" }));
                    return;
                }

                InstanceProcess? proc = InstanceProcess.Spawn(config, payload);
                if (proc == null)
                {
                    ack(ControlMessage.Create(msg.Seq, "ACK", new { ok = false, reason = "spawn failed" }));
                    return;
                }
                instances[payload.InstanceKey] = proc;
                WritePid(config, payload.InstanceKey, proc);
                ack(ControlMessage.Create(msg.Seq, "ACK", new { ok = true }));
                break;
            }
            case "STOP":
            {
                var payload = msg.PayloadAs<StopPayload>();
                if (payload == null || !instances.Remove(payload.InstanceKey, out InstanceProcess? proc))
                {
                    ack(ControlMessage.Create(msg.Seq, "ACK", new { ok = false, reason = "인스턴스 없음" }));
                    return;
                }
                proc.Stop();
                proc.Cleanup();
                DeletePid(config, payload.InstanceKey);
                outboundReports.Enqueue(ControlMessage.Create(0, "INSTANCE_EXITED",
                    new { instanceKey = payload.InstanceKey, code = proc.ExitCode ?? 0 }));
                ack(ControlMessage.Create(msg.Seq, "ACK", new { ok = true }));
                break;
            }
            case "SHUTDOWN":
            {
                // 오케스트레이터 종료 신호 — 메인 루프 종료 → 전 인스턴스 graceful 정리 + 프로세스 종료.
                AgentLog.Info("종료 신호 수신 — 인스턴스 정리 후 종료.");
                ack(ControlMessage.Create(msg.Seq, "ACK", new { ok = true }));
                _shutdownRequested?.Invoke();
                break;
            }
            default:
                ack(ControlMessage.Create(msg.Seq, "ACK", new { ok = false, reason = $"unknown: {msg.Type}" }));
                break;
        }
    }

    // ── 제어 채널 (오케스트레이터에 연결) ──

    private static async Task RunControlChannelAsync(AgentConfig config,
        ConcurrentQueue<(ControlMessage, Action<ControlMessage>)> inbound, CancellationToken ct)
    {
        var (host, port) = SplitAddr(config.OrchestratorAddr);
        TimeSpan delay = TimeSpan.FromSeconds(config.ReconnectIntervalSeconds);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(host, port, ct);
                AgentLog.Info($"오케스트레이터 연결: {config.OrchestratorAddr}");

                using var stream = client.GetStream();
                using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
                using var reader = new StreamReader(stream, new UTF8Encoding(false));

                // HELLO 등록 (D3) — address는 시작 시 자동 탐지된 게이트웨이 직결용 IP
                writer.WriteLine(ControlMessage.Create(1, "AGENT_HELLO", new
                {
                    machineId = config.MachineId,
                    capacity = config.Capacity,
                    address = _localIp,
                }).Serialize());

                // 보류 보고 flush
                while (outboundReports.TryDequeue(out ControlMessage? report))
                {
                    await writer.WriteLineAsync(report.Serialize().AsMemory(), ct);
                }

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                Task readTask = ReadLoopAsync(reader, inbound, cts.Token);
                Task writeTask = WriteLoopAsync(writer, cts.Token);

                await Task.WhenAny(readTask, writeTask);
                cts.Cancel();
                try { await Task.WhenAll(readTask, writeTask); } catch { }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                // 연결 오류는 릴레이 없이 자체 stdout에만 — 재시도 루프가 오케스트레이터
                // 콘솔을 도배하지 않도록 한다 (정상 연결 로그는 릴레이됨).
                Console.WriteLine($"오케스트레이터 연결 오류: {ex.Message}");
            }
            await Task.Delay(delay, ct);
        }
    }

    private static async Task ReadLoopAsync(StreamReader reader,
        ConcurrentQueue<(ControlMessage, Action<ControlMessage>)> inbound, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            string? line = await reader.ReadLineAsync(ct);
            if (line == null) break;
            if (string.IsNullOrWhiteSpace(line)) continue;

            ControlMessage? msg = ControlMessage.Parse(line);
            if (msg == null) continue;
            if (msg.Type == "ACK") continue; // 우리가 보낸 보고의 ACK — 무시

            inbound.Enqueue((msg, ack => SendAck(reader.BaseStream, ack)));
        }
    }

    /// <summary>명령 처리 스레드가 아닌 곳에서도 쓸 수 있게 ACK를 스트림에 직접 기록.</summary>
    private static void SendAck(Stream stream, ControlMessage ack)
    {
        // 메인 스레드의 HandleCommand에서 호출됨 — 여기서는 outbound 전용 큐로 보낸다.
        outboundAcks.Enqueue(ack);
    }

    private static readonly ConcurrentQueue<ControlMessage> outboundAcks = new();

    private static async Task WriteLoopAsync(StreamWriter writer, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (outboundAcks.TryDequeue(out ControlMessage? ack))
            {
                await writer.WriteLineAsync(ack.Serialize().AsMemory(), ct);
            }
            else if (outboundReports.TryDequeue(out ControlMessage? report))
            {
                await writer.WriteLineAsync(report.Serialize().AsMemory(), ct);
            }
            else if (AgentLog.TryDequeue(out ControlMessage? log))
            {
                // 실시간 로그 릴레이 — 연결 수립 시 버퍼링된 로그가 자동 flush된다.
                await writer.WriteLineAsync(log!.Serialize().AsMemory(), ct);
            }
            else
            {
                await Task.Delay(5, ct);
            }
        }
    }

    // ── 유틸 ──

    private static (string Host, int Port) SplitAddr(string addr)
    {
        int idx = addr.LastIndexOf(':');
        return (addr[..idx], int.Parse(addr[(idx + 1)..]));
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

    // ── PID 추적 / orphan 정리 (G-3) ──

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
                        AgentLog.Info($"orphan 프로세스 정리: PID {pid} ({Path.GetFileName(dir)}).");
                        p.Kill(entireProcessTree: true);
                    }
                }
            }
            catch (ArgumentException) { } // 이미 없음
            catch (Exception ex)
            {
                AgentLog.Info($"orphan 정리 실패 ({Path.GetFileName(dir)}): {ex.Message}");
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
