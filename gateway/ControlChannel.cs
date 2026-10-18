using CasuMp.Protocol;
using CasuMp.Runtime;

namespace CasuMpGateway;

public sealed class ControlChannel
{
    private readonly ControlClient _client;

    public ControlChannel(GatewayConfig config, GatewayCore core)
    {
        _client = new ControlClient(config.OrchestratorAddr, config.ControlReconnectIntervalSeconds,
            () =>
            {
                core.ReportActiveSessions();
                return ControlMessage.Create(core.NextSeq(), MessageTypes.GatewayHello, new { version = 1 });
            },
            core.EnqueueCommand,
            error => Logger.Local($"연결 오류: {error}"));
        core.OutboundSink = _client.Enqueue;
        Logger.SetSink(_client.Enqueue);
    }

    public Task RunAsync(CancellationToken ct) => _client.RunAsync(ct);
}
