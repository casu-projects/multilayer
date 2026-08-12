namespace CasuMpOrchestrator;

// 크로스 인스턴스 투표 시스템
// VOTE_START(모드) -> VOTE_RUN 브로드캐스트 -> VOTE_TALLY 통해 합산 -> VOTE_RESULT 확정
public sealed class VoteCoordinator
{
    public readonly record struct VoteFinalizeResult(
        string VoteId, string Kind, int Yes, int No, int Ignore,
        Dictionary<string, string> Payload);

    private sealed class ActiveVote
    {
        public required string VoteId;
        public required string Kind;
        public required Dictionary<string, string> Payload;
        public required HashSet<string> ExpectedInstanceKeys;
        public readonly Dictionary<string, (int Yes, int No, int Ignore)> Tallies = new();
        public readonly DateTime StartedAtUtc = DateTime.UtcNow;
        public required float TimeoutSeconds;
    }

    private readonly object _lock = new();
    private readonly int _graceSeconds;
    private ActiveVote? _active;

    public VoteCoordinator(int graceSeconds)
    {
        _graceSeconds = graceSeconds;
    }

    public bool IsActive
    {
        get { lock (_lock) { return _active != null; } }
    }

    public bool TryStart(VoteStartMarker marker, IReadOnlyCollection<string> expectedInstanceKeys)
    {
        lock (_lock)
        {
            if (_active != null)
            {
                return false;
            }

            _active = new ActiveVote
            {
                VoteId = marker.VoteId,
                Kind = marker.Kind,
                Payload = marker.Payload,
                ExpectedInstanceKeys = new HashSet<string>(expectedInstanceKeys),
                TimeoutSeconds = marker.TimeoutSeconds,
            };
            return true;
        }
    }

    public void RecordTally(VoteTallyMarker marker, string reportingInstanceKey)
    {
        lock (_lock)
        {
            if (_active == null || _active.VoteId != marker.VoteId) return;
            _active.Tallies[reportingInstanceKey] = (marker.Yes, marker.No, marker.Ignore);
        }
    }

    public bool TryFinalize(DateTime nowUtc, out VoteFinalizeResult result)
    {
        lock (_lock)
        {
            result = default;
            if (_active == null) return false;

            bool allReported = _active.ExpectedInstanceKeys.All(k => _active.Tallies.ContainsKey(k));
            bool deadlinePassed = (nowUtc - _active.StartedAtUtc).TotalSeconds
                > _active.TimeoutSeconds + _graceSeconds;

            if (!allReported && !deadlinePassed) return false;

            int totalYes = _active.Tallies.Values.Sum(t => t.Yes);
            int totalNo = _active.Tallies.Values.Sum(t => t.No);
            int totalIgnore = _active.Tallies.Values.Sum(t => t.Ignore);

            result = new VoteFinalizeResult(_active.VoteId, _active.Kind, totalYes, totalNo, totalIgnore,
                _active.Payload);

            Console.WriteLine($"[Vote] 투표 {_active.VoteId}({_active.Kind}) 집계: "
                + $"찬성 {totalYes}, 반대 {totalNo}, 기권 {totalIgnore} "
                + $"({_active.Tallies.Count}/{_active.ExpectedInstanceKeys.Count}개 인스턴스"
                + (deadlinePassed && !allReported ? ", 유예 초과 강제 확정" : "") + ").");

            _active = null;
            return true;
        }
    }
}

// VOTE_START (mod -> orchestrator)
public sealed class VoteStartMarker
{
    public string VoteId { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Title { get; set; } = "";
    public string PromptBody { get; set; } = "";
    public float TimeoutSeconds { get; set; }
    public Dictionary<string, string> Payload { get; set; } = new();
}

// VOTE_TALLY (mod -> orchestrator)
public sealed class VoteTallyMarker
{
    public string VoteId { get; set; } = "";
    public int Yes { get; set; }
    public int No { get; set; }
    public int Ignore { get; set; }
}
