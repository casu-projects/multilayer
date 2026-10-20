using System;
using System.Collections.Generic;

namespace CasuMp.Protocol;

public sealed class AgentHelloPayload
{
    public string MachineId { get; set; } = "";
    public int Capacity { get; set; }
    public string Address { get; set; } = "";
    public List<AgentInstanceInfo> Instances { get; set; } = new();
}

public sealed class AgentInstanceInfo
{
    public string InstanceKey { get; set; } = "";
    public int Depth { get; set; }
    public int Port { get; set; }
    public int Pid { get; set; }
    public bool Suspended { get; set; }
}

public sealed class SpawnPayload
{
    public string InstanceKey { get; set; } = "";
    public int Depth { get; set; }
    public int Port { get; set; }
    public string? ServerName { get; set; }
    public string? ServerPassword { get; set; }
}

public sealed class StopPayload
{
    public string InstanceKey { get; set; } = "";
}

public sealed class ProcessControlPayload
{
    public string InstanceKey { get; set; } = "";
}

public sealed class InstanceReadyPayload
{
    public string InstanceKey { get; set; } = "";
}

public sealed class InstanceExitedPayload
{
    public string InstanceKey { get; set; } = "";
    public int Code { get; set; }
}

public sealed class InstanceFaultPayload
{
    public string InstanceKey { get; set; } = "";
    public string? Reason { get; set; }
}

public sealed class ModHelloPayload
{
    public string InstanceKey { get; set; } = "";
    public int Port { get; set; }
    public int Depth { get; set; }
}

public sealed class LayerEndPayload
{
    public string PlayerKey { get; set; } = "";
    public int FromDepth { get; set; }
    public int MaxLayers { get; set; }
}

public sealed class RespawnPayload
{
    public string PlayerKey { get; set; } = "";
    public int FromDepth { get; set; }
}

public sealed class LogPayload
{
    public string Source { get; set; } = "";
    public string Message { get; set; } = "";
}

public sealed class PlayerKeyPayload
{
    public string PlayerKey { get; set; } = "";
    public int Epoch { get; set; } = -1;
}

public sealed class ChatPayload
{
    public string Speaker { get; set; } = "";
    public string Message { get; set; } = "";
    public string Color { get; set; } = "";
    public string Layer { get; set; } = "";
    public string Prefix { get; set; } = "";
    public string PrefixColor { get; set; } = "";
    public bool Web { get; set; }
}

public sealed class ListRequestPayload
{
    public string PlayerKey { get; set; } = "";
}

public sealed class ListResultPayload
{
    public string PlayerKey { get; set; } = "";
    public string[] Lines { get; set; } = Array.Empty<string>();
}

public sealed class CallAdminPayload
{
    public string PlayerKey { get; set; } = "";
    public string Username { get; set; } = "";
}

public sealed class PlayerEventPayload
{
    public string PlayerKey { get; set; } = "";
    public string Username { get; set; } = "";
    public string Layer { get; set; } = "";
}

public sealed class AnnouncePayload
{
    public string Kind { get; set; } = "";
    public string PlayerKey { get; set; } = "";
    public string Name { get; set; } = "";
    public int FromDepth { get; set; }
    public int ToDepth { get; set; }
    public string OriginInstance { get; set; } = "";
    public string Text { get; set; } = "";
}

public sealed class KickPlayerPayload
{
    public string PlayerKey { get; set; } = "";
    public string Reason { get; set; } = "";
}

public sealed class ConsolePayload
{
    public string Command { get; set; } = "";
}

public sealed class RunRulesPayload
{
    public Dictionary<string, string> RunSettings { get; set; } = new();
    public Dictionary<string, string> Rules { get; set; } = new();
}

public sealed class VoteRunPayload
{
    public string VoteId { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Title { get; set; } = "";
    public string PromptBody { get; set; } = "";
    public float TimeoutSeconds { get; set; }
    public Dictionary<string, string> Payload { get; set; } = new();
}

public sealed class VoteResultPayload
{
    public string VoteId { get; set; } = "";
    public string Kind { get; set; } = "";
    public int Yes { get; set; }
    public int No { get; set; }
    public int Ignore { get; set; }
    public Dictionary<string, string> Payload { get; set; } = new();
}

public sealed class VoteRejectedPayload
{
    public string CallerClientId { get; set; } = "";
    public string Reason { get; set; } = "";
}

public sealed class LobbyMetadataPayload
{
    public string? ServerName { get; set; }
    public string? VersionTag { get; set; }
    public string? Motd { get; set; }
    public int MaxPlayers { get; set; }
    public bool HasPassword { get; set; }
    public int PlayerCount { get; set; }
    public int LivingCount { get; set; }
    public int HappinessSum { get; set; }
    public ulong[]? SteamIds { get; set; }
    public string? RulesBase64 { get; set; }
}
