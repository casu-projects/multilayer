namespace CasuMpServer;

public sealed class AgentRegistry
{
    private readonly Dictionary<string, (int Used, int Capacity, string Address)> _agents = new();

    public bool IsEmpty => _agents.Count == 0;

    public void Register(string machineId, int capacity, string address)
    {
        _agents[machineId] = (0, capacity, address);
    }

    public void Unregister(string machineId)
    {
        _agents.Remove(machineId);
    }

    public bool TryGetAddress(string machineId, out string address)
    {
        if (_agents.TryGetValue(machineId, out var agent))
        {
            address = agent.Address;
            return true;
        }
        address = "";
        return false;
    }

    public string? Select(Func<string, bool>? predicate = null)
    {
        return _agents
            .Where(kv => kv.Value.Used < kv.Value.Capacity)
            .Where(kv => predicate == null || predicate(kv.Key))
            .OrderBy(kv => (double)kv.Value.Used / kv.Value.Capacity)
            .Select(kv => kv.Key)
            .FirstOrDefault();
    }

    public int TotalCapacity(Func<string, bool>? predicate = null) =>
        _agents
            .Where(kv => predicate == null || predicate(kv.Key))
            .Sum(kv => Math.Max(0, kv.Value.Capacity));

    public int AvailableCapacity(Func<string, bool>? predicate = null) =>
        _agents
            .Where(kv => predicate == null || predicate(kv.Key))
            .Sum(kv => Math.Max(0, kv.Value.Capacity - kv.Value.Used));

    public void IncrementUsage(string machineId)
    {
        if (!_agents.TryGetValue(machineId, out var agent)) return;
        _agents[machineId] = (agent.Used + 1, agent.Capacity, agent.Address);
    }

    public void DecrementUsage(string machineId)
    {
        if (!_agents.TryGetValue(machineId, out var agent)) return;
        _agents[machineId] = (Math.Max(0, agent.Used - 1), agent.Capacity, agent.Address);
    }
}
