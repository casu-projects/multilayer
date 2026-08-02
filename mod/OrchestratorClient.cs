using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using UnityEngine;

namespace CasuMod;

/// <summary>오케스트레이터 제어 채널 (D3) — TCP 소켓, MOD_HELLO 등록, 명령 수신/이벤트 전송.
/// 네트워크는 백그라운드 스레드, 메시지 처리는 메인 스레드(Update)에서.</summary>
public sealed class OrchestratorClient : MonoBehaviour
{
    public static OrchestratorClient Instance { get; private set; }

    private readonly ConcurrentQueue<ControlMessage> _inbound = new();
    private readonly ConcurrentQueue<ControlMessage> _outbound = new();

    private Thread _thread;
    private volatile bool _running;
    private volatile bool _connected;
    /// <summary>이번 연결에서 INSTANCE_READY를 보고했는지 — 재연결 시에만 리셋되어
    /// 재보고를 허용한다 (첫 연결은 FinishWorldGeneration 완료 보고가 담당).
    /// ROUTE-ON-READY 연동: 오케스트레이터 재시작 후 웜 인스턴스(이미 월드젠 완료)는
    /// FinishWorldGeneration Postfix의 1회 보고 가드(_readyReported)로 재보고되지 않으므로,
    /// 재연결 시점에 월드가 생성되어 있으면 READY를 재보고한다.</summary>
    private volatile bool _readyReportedOnConnection = true;
    /// <summary>이 인스턴스 프로세스에서 오케스트레이터에 한 번이라도 연결했는지.</summary>
    private volatile bool _everConnected;

    public string OrchAddr { get; private set; } = "";
    public string InstanceKey { get; private set; } = "depth-1";
    public int InstancePort { get; private set; }
    public int InstanceDepth { get; private set; } = 1;

    public bool Connected => _connected;

    private void Awake()
    {
        Instance = this;
        OrchAddr = Environment.GetEnvironmentVariable("CASU_ORCH_ADDR") ?? "";
        InstanceKey = Environment.GetEnvironmentVariable("CASU_INSTANCE_KEY") ?? "";
        InstanceDepth = int.TryParse(Environment.GetEnvironmentVariable("CASU_START_DEPTH"), out int d) ? d : 1;
        InstancePort = int.TryParse(Environment.GetEnvironmentVariable("CASU_PORT"), out int p) ? p : 0;
        if (InstanceKey == "")
        {
            InstanceKey = $"depth-{InstanceDepth}";
        }

        _running = true;
        _thread = new Thread(ThreadLoop) { IsBackground = true, Name = "CasuMod.OrchClient" };
        _thread.Start();
    }

    private void OnDestroy()
    {
        _running = false;
        try { _thread?.Join(1000); } catch { }
    }

    private void Update()
    {
        ProcessInbound();
        RunModule.Tick();
        TryReportReadyOnConnection();
    }

    /// <summary>연결 수립 + 월드 생성 완료 시 INSTANCE_READY 재보고 (ROUTE-ON-READY).
    /// 첫 부팅은 FinishWorldGeneration Postfix가 보고하고, 이 경로는 재연결(오케스트레이터
    /// 재시작) 후 웜 인스턴스의 READY 상태를 복원한다.</summary>
    private void TryReportReadyOnConnection()
    {
        if (!_connected || _readyReportedOnConnection) return;
        if (!KrokoshaCasualtiesUtils.Util.IsWorldGenerated()) return;
        _readyReportedOnConnection = true;
        SendEvent("INSTANCE_READY", new { instanceKey = InstanceKey });
        Plugin.Log.LogInfo($"[Orch] READY 재보고 (연결 재수립 — {InstanceKey}).");
    }

    /// <summary>인바운드 큐 처리 (메인 스레드 전용). Update에서 호출되며, Body.Start의
    /// 복원 대기 루프에서도 직접 호출해 응답을 즉시 반영한다 (디스패치 경쟁 제거).</summary>
    public void ProcessInbound()
    {
        while (_inbound.TryDequeue(out ControlMessage msg))
        {
            try { HandleIncoming(msg); }
            catch (System.Exception ex) { Plugin.Log.LogWarning($"[Orch] 명령 처리 실패 ({msg.Type}): {ex.Message}"); }
        }
    }

    /// <summary>이벤트/보고 전송 (메인 스레드에서 호출 — fire-and-forget).</summary>
    public void SendEvent(string type, object payload)
    {
        _outbound.Enqueue(ControlMessage.Create(0, type, payload));
    }

    /// <summary>outbound 큐가 비워질 때까지 대기 — 종료 전 제출(PLAYER_DATA_SUBMIT)이
    /// WriteLoop를 통해 소켓에 전달되도록 보장 (Application.Quit 전에 호출).</summary>
    public void WaitForOutboundFlush(int timeoutMs)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (_outbound.IsEmpty) return;
            Thread.Sleep(10);
        }
        Plugin.Log.LogWarning("[Orch] 종료 전 outbound flush 타임아웃 — 일부 보고가 유실될 수 있음.");
    }

    private void Ack(ControlMessage msg, bool ok, string reason = null)
    {
        _outbound.Enqueue(ControlMessage.Ack(msg.Seq, ok, reason));
    }

    private void HandleIncoming(ControlMessage msg)
    {
        switch (msg.Type)
        {
            case "START_RUN":
                RunModule.HandleStartRun();
                Ack(msg, true);
                break;
            case "SHUTDOWN":
                RunModule.HandleShutdown();
                Ack(msg, true);
                break;
            case "FREEZE":
                MigrationModule.HandleFreeze(msg);
                Ack(msg, true);
                break;
            case "UNFREEZE":
                MigrationModule.HandleUnfreeze(msg);
                Ack(msg, true);
                break;
            case "RESUME":
                MigrationModule.HandleResume(msg);
                Ack(msg, true);
                break;
            case "TRIGGER_WORLDGEN":
                MigrationModule.HandleTriggerWorldgen(msg.PayloadAs<PlayerKeyPayload>()?.PlayerKey ?? "");
                Ack(msg, true);
                break;
            case "RELEASE":
                MigrationModule.HandleRelease(msg.PayloadAs<PlayerKeyPayload>()?.PlayerKey ?? "",
                    msg.PayloadAs<PlayerKeyPayload>()?.Epoch ?? -1);
                Ack(msg, true);
                break;
            case "PLAYER_DATA_RESPONSE":
                SaveModule.HandlePlayerDataResponse(msg);
                break;
            case "MOD_HELLO_ACK":
                // 등록 확인 — no-op
                break;
            case "RUN_RULES_STATE":
                RunRuleState.Apply(msg.PayloadAs<RunRulesPayload>());
                break;
            case "CHAT":
                ChatRelay.Receive(msg.PayloadAs<ChatPayload>());
                break;
            case "ANNOUNCE":
                AnnounceRelay.Handle(msg.PayloadAs<AnnouncePayload>());
                break;
            case "LIST_RESULT":
                ChatCommands.HandleListResult(msg);
                break;
            case "CURRENT_RESULT":
                ChatCommands.HandleCurrentResult(msg);
                break;
            case "CONSOLE":
                RunModule.HandleConsole(msg.PayloadAs<ConsolePayload>()?.Command ?? "");
                Ack(msg, true);
                break;
            default:
                Plugin.Log.LogWarning($"[Orch] 알 수 없는 명령: {msg.Type}");
                Ack(msg, false, "unknown");
                break;
        }
    }

    // ── 네트워크 스레드 ──

    private void ThreadLoop()
    {
        var (host, port) = SplitAddr(OrchAddr);
        while (_running)
        {
            try
            {
                using var client = new TcpClient();
                client.Connect(host, port);
                using var stream = client.GetStream();
                using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
                using var reader = new StreamReader(stream, new UTF8Encoding(false));

                _connected = true;
                Plugin.Log.LogInfo($"[Orch] 오케스트레이터 연결: {OrchAddr}");

                // READY 재보고는 재연결 전용 (2026-08-02 수정): 첫 연결은 FinishWorldGeneration
                // 완료 시점의 INSTANCE_READY 보고가 담당한다 — 첫 연결에서도 재보고가 발동하면
                // ROUTE_UPDATE가 중복되어 게이트웨이가 백엔드를 이중 연결하고
                // "Player with this name already exists" 추방을 유발한다.
                if (_everConnected)
                {
                    _readyReportedOnConnection = false;
                }
                _everConnected = true;

                // MOD_HELLO 등록 (D3)
                writer.WriteLine(ControlMessage.Create(1, "MOD_HELLO", new
                {
                    instanceKey = InstanceKey,
                    port = InstancePort,
                    depth = InstanceDepth,
                }).Serialize());

                using var cts = new CancellationTokenSource();
                Task readTask = ReadLoopAsync(reader, cts.Token);
                Task writeTask = WriteLoopAsync(writer, cts.Token);
                Task.WaitAny(readTask, writeTask);
                cts.Cancel();
                try { Task.WaitAll(readTask, writeTask); } catch { }
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogWarning($"[Orch] 연결 오류: {ex.Message}");
            }
            finally
            {
                _connected = false;
                _readyReportedOnConnection = false;
            }
            Thread.Sleep(2000);
        }
    }

    private async Task ReadLoopAsync(StreamReader reader, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            string line = await reader.ReadLineAsync();
            if (line == null) break;
            if (string.IsNullOrWhiteSpace(line)) continue;
            ControlMessage msg = ControlMessage.Parse(line);
            if (msg == null) continue;
            _inbound.Enqueue(msg);
        }
    }

    private async Task WriteLoopAsync(StreamWriter writer, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (_outbound.TryDequeue(out ControlMessage msg))
            {
                try { await writer.WriteLineAsync(msg.Serialize()); }
                catch { break; }
            }
            else
            {
                await Task.Delay(5, ct);
            }
        }
    }

    private static (string Host, int Port) SplitAddr(string addr)
    {
        int idx = addr.LastIndexOf(':');
        return (addr.Substring(0, idx), int.Parse(addr.Substring(idx + 1)));
    }
}
