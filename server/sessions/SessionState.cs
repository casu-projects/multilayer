namespace CasuMpServer;

public enum SessionState
{
    Accepted,

    Routing,

    Connecting,

    Active,

    Swapping,

    Closing,

    Closed,
}
