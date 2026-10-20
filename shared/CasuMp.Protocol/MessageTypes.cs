namespace CasuMp.Protocol;

public static class MessageTypes
{
    public const string Ack = "ACK";

    public const string AgentHello = "AGENT_HELLO";
    public const string AgentHelloAck = "AGENT_HELLO_ACK";
    public const string ModHello = "MOD_HELLO";
    public const string ModHelloAck = "MOD_HELLO_ACK";

    public const string Shutdown = "SHUTDOWN";
    public const string Log = "LOG";


    public const string InstanceReady = "INSTANCE_READY";
    public const string InstanceFault = "INSTANCE_FAULT";
    public const string InstanceExited = "INSTANCE_EXITED";
    public const string Spawn = "SPAWN";
    public const string Stop = "STOP";
    public const string Reset = "RESET";
    public const string StartRun = "START_RUN";
    public const string ProcessSuspend = "PROCESS_SUSPEND";
    public const string ProcessResume = "PROCESS_RESUME";
    public const string CleanupAll = "CLEANUP_ALL";

    public const string LayerEnd = "LAYER_END";
    public const string Respawn = "RESPAWN";
    public const string Respawned = "RESPAWNED";
    public const string Died = "DIED";
    public const string Freeze = "FREEZE";
    public const string FreezeDone = "FREEZE_DONE";
    public const string PreClear = "PRECLEAR";
    public const string PreClearDone = "PRECLEAR_DONE";
    public const string Unfreeze = "UNFREEZE";
    public const string Resume = "RESUME";
    public const string ResumeDone = "RESUME_DONE";
    public const string WorldgenDone = "WORLDGEN_DONE";
    public const string PlayerLeft = "PLAYER_LEFT";
    public const string Release = "RELEASE";
    public const string TriggerWorldgen = "TRIGGER_WORLDGEN";
    public const string KickPlayer = "KICK_PLAYER";

    public const string PlayerDataSubmit = "PLAYER_DATA_SUBMIT";
    public const string PlayerDataRequest = "PLAYER_DATA_REQUEST";
    public const string PlayerDataResponse = "PLAYER_DATA_RESPONSE";

    public const string Chat = "CHAT";
    public const string ListRequest = "LIST_REQUEST";
    public const string ListResult = "LIST_RESULT";
    public const string CallAdmin = "CALLADMIN";
    public const string Announce = "ANNOUNCE";
    public const string Console = "CONSOLE";

    public const string VoteStart = "VOTE_START";
    public const string VoteRun = "VOTE_RUN";
    public const string VoteTally = "VOTE_TALLY";
    public const string VoteResult = "VOTE_RESULT";
    public const string VoteRejected = "VOTE_REJECTED";

    public const string RunRulesState = "RUN_RULES_STATE";
    public const string PlayerPositions = "PLAYER_POSITIONS";
    public const string PlayerDetailRequest = "PLAYER_DETAIL_REQUEST";
    public const string PlayerDetailResponse = "PLAYER_DETAIL_RESPONSE";
    public const string ObjectQuery = "OBJECT_QUERY";
    public const string ObjectQueryResponse = "OBJECT_QUERY_RESPONSE";
    public const string ObjectDetailRequest = "OBJECT_DETAIL_REQUEST";
    public const string ObjectDetailResponse = "OBJECT_DETAIL_RESPONSE";
    public const string ObjectBackgrounds = "OBJECT_BACKGROUNDS";
}
