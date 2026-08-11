using System.Text.Json;

namespace CasuMpOrchestrator;

// 그룹 상태 — 1인 1그룹, joinable(직접 가입 허용) 토글, Discord 스레드 연동.
// 영속화: saves/groups/groups.json. 내부 상태는 락으로 보호 — Discord 스레드 콜백
// (백그라운드 스레드)에서도 SetThreadId가 호출될 수 있다.
public sealed class Group
{
    public required string Name { get; set; }
    public required string OwnerKey { get; set; }
    public bool Joinable { get; set; } = true;
    public HashSet<string> MemberKeys { get; set; } = new();
    public ulong DiscordThreadId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class GroupStore
{
    private readonly object _lock = new();
    private readonly Dictionary<string, Group> _groups = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<PlayerKey, string> _membership = new();
    private readonly string _path;
    private readonly int _maxGroups;
    private readonly int _maxGroupMembers;

    public GroupStore(OrchestratorConfig config)
    {
        _path = Path.Combine(config.SaveRootPath, "groups", "groups.json");
        _maxGroups = config.MaxGroups;
        _maxGroupMembers = config.MaxGroupMembers;
        Load();
    }

    public IReadOnlyCollection<Group> All
    {
        get { lock (_lock) { return _groups.Values.ToList(); } }
    }

    public Group? GetByPlayer(PlayerKey player)
    {
        lock (_lock)
        {
            return _membership.TryGetValue(player, out string? name) && _groups.TryGetValue(name, out Group? g) ? g : null;
        }
    }

    public Group? GetByName(string name)
    {
        lock (_lock) { return _groups.TryGetValue(name.Trim(), out Group? g) ? g : null; }
    }

    public bool TryGetByThreadId(ulong threadId, out Group group)
    {
        lock (_lock)
        {
            group = _groups.Values.FirstOrDefault(g => g.DiscordThreadId == threadId)!;
            return group != null;
        }
    }

    public void SetThreadId(string name, ulong threadId)
    {
        lock (_lock)
        {
            if (_groups.TryGetValue(name, out Group? g))
            {
                g.DiscordThreadId = threadId;
                Save();
            }
        }
    }

    // 그룹 생성 + 생성자 가입. 오류 시 오류 문자열, 성공 시 null.
    public string? TryCreate(PlayerKey owner, string name, out Group? created)
    {
        created = null;
        lock (_lock)
        {
            string trimmed = name.Trim();
            if (trimmed.Length == 0 || trimmed.Length > 16)
            {
                return "그룹 이름은 1~16자여야 합니다.";
            }
            if (_groups.ContainsKey(trimmed))
            {
                return $"이미 존재하는 그룹입니다: {trimmed}";
            }
            if (_membership.ContainsKey(owner))
            {
                return "이미 그룹에 가입되어 있습니다.";
            }
            if (_groups.Count >= _maxGroups)
            {
                return "그룹 수 제한에 도달했습니다.";
            }

            var g = new Group { Name = trimmed, OwnerKey = owner.Value };
            g.MemberKeys.Add(owner.Value);
            _groups[trimmed] = g;
            _membership[owner] = trimmed;
            created = g;
            Save();
            return null;
        }
    }

    // 그룹 가입 — 존재 확인 + joinable 게이트 + 1인 1그룹 + 인원 상한.
    public string? TryJoin(PlayerKey player, string name, out Group? joined)
    {
        joined = null;
        lock (_lock)
        {
            string trimmed = name.Trim();
            if (!_groups.TryGetValue(trimmed, out Group? g))
            {
                return $"해당 이름의 그룹은 존재하지 않습니다: {trimmed}";
            }
            if (!g.Joinable)
            {
                return "초대 전용 그룹입니다. 초대를 받아야 가입할 수 있습니다.";
            }
            if (_membership.ContainsKey(player))
            {
                return "이미 그룹에 가입되어 있습니다.";
            }
            if (g.MemberKeys.Count >= _maxGroupMembers)
            {
                return "그룹 인원이 가득 찼습니다.";
            }

            g.MemberKeys.Add(player.Value);
            _membership[player] = trimmed;
            joined = g;
            Save();
            return null;
        }
    }

    // 초대 가입 — joinable과 무관하게 추가.
    public string? AcceptInvite(PlayerKey player, string name, out Group? joined)
    {
        joined = null;
        lock (_lock)
        {
            if (!_groups.TryGetValue(name.Trim(), out Group? g))
            {
                return $"그룹이 존재하지 않습니다: {name}";
            }
            if (_membership.ContainsKey(player))
            {
                return "이미 그룹에 가입되어 있습니다.";
            }
            if (g.MemberKeys.Count >= _maxGroupMembers)
            {
                return "그룹 인원이 가득 찼습니다.";
            }

            g.MemberKeys.Add(player.Value);
            _membership[player] = g.Name;
            joined = g;
            Save();
            return null;
        }
    }

    // 퇴장 — 마지막 멤버면 그룹 삭제 (스레드 정리용으로 삭제된 그룹 반환).
    // 그룹 생성자는 퇴장 불가 — remove로 그룹을 삭제해야 한다.
    public string? Leave(PlayerKey player, out Group? removedGroup)
    {
        removedGroup = null;
        lock (_lock)
        {
            if (!_membership.Remove(player, out string? name))
            {
                return "그룹에 가입되어 있지 않습니다.";
            }
            if (_groups.TryGetValue(name, out Group? g))
            {
                if (g.OwnerKey == player.Value)
                {
                    // 소유권 이전 없이는 생성자가 나가면 그룹이 주인 없는 상태가 되므로 금지.
                    _membership[player] = name;
                    return "그룹 생성자는 퇴장할 수 없습니다. !group remove로 그룹을 삭제하세요.";
                }
                g.MemberKeys.Remove(player.Value);
                if (g.MemberKeys.Count == 0)
                {
                    _groups.Remove(name);
                    removedGroup = g;
                }
                Save();
            }
            return null;
        }
    }

    // 그룹 삭제 (생성자만) — 멤버 전원 방출.
    public string? Remove(PlayerKey player, out Group? removedGroup)
    {
        removedGroup = null;
        lock (_lock)
        {
            if (!_membership.TryGetValue(player, out string? name))
            {
                return "그룹에 가입되어 있지 않습니다.";
            }
            if (!_groups.TryGetValue(name, out Group? g))
            {
                return "그룹에 가입되어 있지 않습니다.";
            }
            if (g.OwnerKey != player.Value)
            {
                return "그룹 생성자만 삭제할 수 있습니다.";
            }

            foreach (string memberKey in g.MemberKeys.ToList())
            {
                _membership.Remove(PlayerKey.FromString(memberKey));
            }
            _groups.Remove(name);
            removedGroup = g;
            Save();
            return null;
        }
    }

    // joinable 토글 (생성자만). 반환: 현재 상태 문자열.
    public string? ToggleJoinable(PlayerKey player, out Group? g)
    {
        g = null;
        lock (_lock)
        {
            if (!_membership.TryGetValue(player, out string? name) || !_groups.TryGetValue(name, out g))
            {
                return "그룹에 가입되어 있지 않습니다.";
            }
            if (g.OwnerKey != player.Value)
            {
                return "그룹 생성자만 변경할 수 있습니다.";
            }
            g.Joinable = !g.Joinable;
            Save();
            return null;
        }
    }

    // 전체 그룹 목록 (이름순).
    public string[] ListAll()
    {
        lock (_lock)
        {
            if (_groups.Count == 0) return new[] { "생성된 그룹이 없습니다." };
            var lines = new List<string> { $"전체 그룹 {_groups.Count}개 :" };
            foreach (Group g in _groups.Values.OrderBy(g => g.Name, StringComparer.Ordinal))
            {
                string state = g.Joinable ? "가입 가능" : "초대 전용";
                lines.Add($"{g.Name} — 멤버 {g.MemberKeys.Count}명, {state}");
            }
            return lines.ToArray();
        }
    }

    // 내 그룹 멤버 목록 + 온라인/오프라인 (세션 상태 기준).
    public string[] PlayerList(PlayerSessionStore sessions, PlayerKey player)
    {
        lock (_lock)
        {
            if (!_membership.TryGetValue(player, out string? name) || !_groups.TryGetValue(name, out Group? g))
            {
                return new[] { "그룹에 가입되어 있지 않습니다." };
            }
            if (g.MemberKeys.Count == 0) return new[] { $"그룹 [{g.Name}]에 멤버가 없습니다." };

            var lines = new List<string> { $"그룹 [{g.Name}] 멤버 {g.MemberKeys.Count}명 :" };
            foreach (string memberKey in g.MemberKeys.OrderBy(k => k, StringComparer.Ordinal))
            {
                var key = PlayerKey.FromString(memberKey);
                var state = sessions.Get(key);
                bool online = state != null && state.Session != PlayerSessionState.Offline;
                string label = state?.Username ?? key.Value;
                lines.Add($"  {label} — {(online ? "온라인" : "오프라인")}");
            }
            return lines.ToArray();
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var flat = _groups.Values.Select(g => new
            {
                g.Name,
                g.OwnerKey,
                g.Joinable,
                members = g.MemberKeys.ToList(),
                g.DiscordThreadId,
                g.CreatedAt,
            }).ToList();
            string tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(flat, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"그룹 저장 실패: {ex.Message}");
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            // 대소문자 무시 — Save의 익명 타입은 소문자 "members"를 기록한다 (구버전 파일 호환).
            var raw = JsonSerializer.Deserialize<List<GroupDto>>(File.ReadAllText(_path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (raw == null) return;

            foreach (GroupDto dto in raw)
            {
                var g = new Group
                {
                    Name = dto.Name,
                    OwnerKey = dto.OwnerKey,
                    Joinable = dto.Joinable,
                    MemberKeys = new HashSet<string>(dto.Members ?? new()),
                    DiscordThreadId = dto.DiscordThreadId,
                    CreatedAt = dto.CreatedAt != default ? dto.CreatedAt : DateTime.UtcNow,
                };
                _groups[g.Name] = g;
                foreach (string member in g.MemberKeys)
                {
                    _membership[PlayerKey.FromString(member)] = g.Name;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"그룹 로드 실패: {ex.Message}");
        }
    }

    private sealed class GroupDto
    {
        public string Name { get; set; } = "";
        public string OwnerKey { get; set; } = "";
        public bool Joinable { get; set; } = true;
        public List<string>? Members { get; set; }
        public ulong DiscordThreadId { get; set; }
        public DateTime CreatedAt { get; set; }
    }
}
