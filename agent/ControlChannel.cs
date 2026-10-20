using System.Collections.Concurrent;
using CasuMp.Protocol;
using CasuMp.Runtime;

namespace CasuMpAgent;

public sealed class ControlChannel
{
    private readonly ConcurrentQueue<(ControlMessage Msg, Action<ControlMessage> Ack)> _inbound;
    private readonly ControlClient _client;

    public ControlChannel(AgentConfig config, string localIp,
        ConcurrentQueue<(ControlMessage, Action<ControlMessage>)> inbound,
        Func<IReadOnlyList<AgentInstanceInfo>> instanceSnapshot)
    {
        _inbound = inbound;
        _client = new ControlClient(config.OrchestratorAddr, 2,
            () => ControlMessage.Create(1, MessageType.AGENT_HELLO, new
            {
                machineId = config.MachineId,
                capacity = config.Capacity,
                address = localIp,
                instances = instanceSnapshot(),
            }),
            message => _inbound.Enqueue((message, Ack)),
            error => Logger.Local($"오케스트레이터 연결 오류: {error}"));
        Logger.SetSink(_client.Enqueue);
    }

    public void Ack(ControlMessage ack) => _client.Enqueue(ack);

    public void Report(ControlMessage msg) => _client.Enqueue(msg);

    public Task RunAsync(CancellationToken ct) => _client.RunAsync(ct);
}
