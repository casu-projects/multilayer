using CasuMp.Protocol;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using Together;
using UnityEngine;

namespace CasuMod;

public sealed class OrchestratorClient : MonoBehaviour
{
    public static OrchestratorClient Instance { get; private set; }

    private readonly ConcurrentQueue<ControlMessage> _inbound = new();
    private readonly ConcurrentQueue<ControlMessage> _outbound = new();
    private readonly SemaphoreSlim _outboundSignal = new(0, 1);

    private Thread _thread;
    private volatile bool _running;
    private volatile bool _connected;
    private volatile bool _readyReportedOnConnection = true;
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

    private void TryReportReadyOnConnection()
    {
        if (!_connected || _readyReportedOnConnection) return;
        if (!CasualtiesTogetherUtils.Util.IsWorldGenerated()) return;
        _readyReportedOnConnection = true;
        SendEvent(MessageTypes.InstanceReady, new { instanceKey = InstanceKey });
    }

    public void ProcessInbound()
    {
        while (_inbound.TryDequeue(out ControlMessage msg))
        {
            try { HandleIncoming(msg); }
            catch (System.Exception ex) { Plugin.Log.LogWarning($"[Orch] 명령 처리 실패 ({msg.Type}): {ex.Message}"); }
        }
    }

    public void SendEvent(string type, object payload)
    {
        _outbound.Enqueue(ControlMessage.Create(0, type, payload));
        SignalOutbound();
    }

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
        SignalOutbound();
    }

    private void HandleIncoming(ControlMessage msg)
    {
        switch (msg.Type)
        {
            case MessageTypes.StartRun:
                RunModule.HandleStartRun();
                Ack(msg, true);
                break;
            case MessageTypes.Shutdown:
                RunModule.HandleShutdown();
                Ack(msg, true);
                break;
            case MessageTypes.Reset:
                RunModule.HandleReset();
                Ack(msg, true);
                break;
            case MessageTypes.Freeze:
                MigrationModule.HandleFreeze(msg);
                Ack(msg, true);
                break;
            case MessageTypes.PreClear:
                MigrationModule.HandlePreClear(msg);
                Ack(msg, true);
                break;
            case MessageTypes.Unfreeze:
                MigrationModule.HandleUnfreeze(msg);
                Ack(msg, true);
                break;
            case MessageTypes.Resume:
                MigrationModule.HandleResume(msg);
                Ack(msg, true);
                break;
            case MessageTypes.TriggerWorldgen:
                MigrationModule.HandleTriggerWorldgen(msg.PayloadAs<PlayerKeyPayload>()?.PlayerKey ?? "");
                Ack(msg, true);
                break;
            case MessageTypes.Release:
                MigrationModule.HandleRelease(msg.PayloadAs<PlayerKeyPayload>()?.PlayerKey ?? "",
                    msg.PayloadAs<PlayerKeyPayload>()?.Epoch ?? -1);
                Ack(msg, true);
                break;
            case MessageTypes.PlayerDataResponse:
                SaveModule.HandlePlayerDataResponse(msg);
                break;
            case MessageTypes.PlayerDetailRequest:
                PlayerDetailModule.HandleRequest(msg.PayloadAs<PlayerDetailRequest>());
                break;
            case MessageTypes.ObjectQuery:
                ObjectIndexModule.Instance?.HandleQuery(msg.PayloadAs<MapObjectQueryRequest>());
                break;
            case MessageTypes.ObjectDetailRequest:
                ObjectIndexModule.Instance?.HandleDetail(msg.PayloadAs<MapObjectDetailRequest>());
                break;
            case MessageTypes.ModHelloAck:
                break;
            case MessageTypes.RunRulesState:
                RunRuleState.Apply(msg.PayloadAs<RunRulesPayload>());
                break;
            case MessageTypes.Chat:
                ChatRelay.Receive(msg.PayloadAs<ChatPayload>());
                break;
            case MessageTypes.Announce:
                AnnounceRelay.Handle(msg.PayloadAs<AnnouncePayload>());
                break;
            case MessageTypes.VoteRun:
                VoteRelay.HandleRun(msg.PayloadAs<VoteRunPayload>());
                break;
            case MessageTypes.VoteResult:
                VoteRelay.HandleResult(msg.PayloadAs<VoteResultPayload>());
                break;
            case MessageTypes.VoteRejected:
                VoteRelay.HandleRejected(msg.PayloadAs<VoteRejectedPayload>());
                break;
            case MessageTypes.ListResult:
                ChatCommands.HandleListResult(msg);
                break;
            case MessageTypes.Console:
                RunModule.HandleConsole(msg.PayloadAs<ConsolePayload>()?.Command ?? "");
                Ack(msg, true);
                break;
            case MessageTypes.KickPlayer:
                HandleKickPlayer(msg);
                break;
            default:
                Ack(msg, false, "unknown");
                break;
        }
    }

    private static void HandleKickPlayer(ControlMessage msg)
    {
        var payload = msg.PayloadAs<KickPlayerPayload>();
        if (payload == null || string.IsNullOrEmpty(payload.PlayerKey)) return;
        try
        {
            ScavPlayer plr = ChatCommands.FindByPersistentId(payload.PlayerKey);
            if (plr == null)
            {
                return;
            }
            Net.Server_Kick(plr.playerId, payload.Reason ?? "Migration Failed. Please reconnect.");
            Plugin.Log.LogWarning($"[Orch] {plr.playerName} 마이그레이션 실패 추방: {payload.Reason}");
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[Orch] KICK_PLAYER 처리 실패: {ex.Message}");
        }
    }

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

                if (_everConnected)
                {
                    _readyReportedOnConnection = false;
                }
                _everConnected = true;

                writer.WriteLine(ControlMessage.Create(1, MessageTypes.ModHello, new
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
                _readyReportedOnConnection = false; // 재연결 시 READY 재보고 허용
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

    private void SignalOutbound()
    {
        if (_outboundSignal.CurrentCount != 0) return;
        try { _outboundSignal.Release(); }
        catch (SemaphoreFullException) { }
    }

    private async Task WriteLoopAsync(StreamWriter writer, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _outboundSignal.WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            while (_outbound.TryDequeue(out ControlMessage msg))
            {
                try { await writer.WriteLineAsync(msg.Serialize()); }
                catch { return; }
            }
        }
    }

    private static (string Host, int Port) SplitAddr(string addr)
    {
        int idx = addr.LastIndexOf(':');
        return (addr.Substring(0, idx), int.Parse(addr.Substring(idx + 1)));
    }
}
