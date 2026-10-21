using System.Collections.Concurrent;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using CasuMp.Protocol;
using CasuMp.Runtime;
using CasuMpServer;

var tests = new (string Name, Action Run)[]
{
    ("TailCodec roundtrip", TestTailRoundtrip),
    ("TailCodec name boundary", TestTailNameBoundary),
    ("AtomicJsonFile write", TestAtomicJson),
    ("RequestRegistry cap", TestRequestRegistryCap),
    ("Handshake gzip cap", TestHandshakeGzipCap),
    ("Handshake malformed credentials", TestHandshakeMalformedCredentials),
    ("World cell cap", TestWorldCellCap),
    ("Generation token matching", TestGenerationMatching),
    ("Instance command generation", TestInstanceCommandGeneration),
    ("Instance stale exit ignored", TestInstanceExitGenerationGuard),
    ("Failed spawn releases resources", TestFailedSpawnCleanup),
};

int failed = 0;
foreach ((string name, Action run) in tests)
{
    try
    {
        run();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception ex)
    {
        failed++;
        Console.WriteLine($"FAIL {name}: {ex.Message}");
    }
}
return failed == 0 ? 0 : 1;

static void TestTailRoundtrip()
{
    byte[] intro = { 1, 2, 3, 4, 5 };
    byte[] tail = TailCodec.Encode(76561198000000001, 42, true, false, "PlayerOne");
    byte[] raw = intro.Concat(tail).ToArray();

    Assert(TailCodec.TryFind(raw, intro.Length, tail.Length, out TailInfo info), "tail not found");
    Assert(info.SteamId == 76561198000000001, "steamId");
    Assert(info.ClientId == 42, "clientId");
    Assert(info.IsReturning, "isReturning");
    Assert(!info.IsMigratingArrival, "isMigratingArrival");
    Assert(info.Name == "PlayerOne", "name");
}

static void TestTailNameBoundary()
{
    byte[] intro = { 9, 8, 7 };
    byte[] tail = TailCodec.Encode(1, 2, false, false, "abc");
    int nameLengthOffset = intro.Length + TailCodec.FixedTailSize;
    tail[nameLengthOffset - intro.Length] = 200;
    byte[] raw = intro.Concat(tail).Concat(new byte[200]).ToArray();

    Assert(TailCodec.TryFind(raw, intro.Length, tail.Length, out TailInfo info), "tail not found");
    Assert(info.Name == null, "name read past user data");
}

static void TestAtomicJson()
{
    string dir = Path.Combine(Path.GetTempPath(), "casump-tests", Guid.NewGuid().ToString("N"));
    string path = Path.Combine(dir, "state.json");
    try
    {
        AtomicJsonFile.Write(path, "{\"ok\":true}");
        Assert(AtomicJsonFile.Read(path) == "{\"ok\":true}", "content mismatch");
        Assert(Directory.GetFiles(dir, "*.tmp").Length == 0, "temporary file left behind");
    }
    finally
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }
}

static void TestRequestRegistryCap()
{
    var registry = new RequestRegistry<Dummy>();
    for (int i = 0; i < RequestRegistry<Dummy>.MaxPending; i++)
    {
        Assert(registry.Register($"r{i}") != null, $"register {i}");
    }
    Assert(registry.Register("overflow") == null, "registry did not cap");
    registry.Remove("r0");
    Assert(registry.Register("after-remove") != null, "registry did not free slot");
}

static void TestHandshakeGzipCap()
{
    byte[] compressed = CompressCredentials(new byte[1024 * 1024]);
    byte[] raw = BuildHandshake("Tester", compressed);
    Assert(!HandshakeReader.TryParseCredentials(raw, out _, out _), "gzip bomb accepted");

    byte[] valid = CompressCredentials(Encoding.Latin1.GetBytes("secret"));
    byte[] validRaw = BuildHandshake("Tester", valid);
    Assert(HandshakeReader.TryParseCredentials(validRaw, out string username, out string password), "valid credentials rejected");
    Assert(username == "Tester", "username");
    Assert(password == "secret", "password");
}

static void TestHandshakeMalformedCredentials()
{
    byte[] raw = BuildHandshake("Tester", new byte[] { 1, 2, 3, 4 });
    Assert(!HandshakeReader.TryParseCredentials(raw, out _, out _), "malformed credentials accepted");
}

static void TestWorldCellCap()
{
    var store = new WorldSnapshotStore();
    var tooLarge = new MapWorldBegin
    {
        Width = 8192,
        Height = 8192,
        WorldVersion = "large",
    };
    Assert(store.BeginWorld("depth-1", tooLarge) == null, "oversized world accepted");

    var valid = new MapWorldBegin
    {
        Width = 16,
        Height = 16,
        WorldVersion = "small",
    };
    Assert(store.BeginWorld("depth-1", valid) != null, "small world rejected");
}

static byte[] BuildHandshake(string username, byte[] compressed)
{
    using var stream = new MemoryStream();
    using var writer = new BinaryWriter(stream, Encoding.Latin1, leaveOpen: true);
    writer.Write(0UL);
    byte[] version = Encoding.Latin1.GetBytes("v");
    writer.Write((ushort)version.Length);
    writer.Write(version);
    byte[] name = Encoding.Latin1.GetBytes(username);
    writer.Write((ushort)name.Length);
    writer.Write(name);
    writer.Write((ushort)compressed.Length);
    writer.Write(compressed);
    writer.Flush();
    return stream.ToArray();
}

static byte[] CompressCredentials(byte[] password)
{
    using var output = new MemoryStream();
    using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
    using (var inner = new BinaryWriter(gzip, Encoding.Latin1, leaveOpen: true))
    {
        inner.Write((ushort)password.Length);
        inner.Write(password);
    }
    return output.ToArray();
}

static void TestGenerationMatching()
{
    Assert(InstanceGeneration.Matches("g2", "g2"), "matching generation rejected");
    Assert(!InstanceGeneration.Matches("g2", "g1"), "stale generation accepted");
    Assert(InstanceGeneration.Matches("", "g1"), "legacy generation rejected");
    Assert(InstanceGeneration.MatchesExact("g2", "g2"), "exact matching generation rejected");
    Assert(!InstanceGeneration.MatchesExact("g2", "g1"), "exact stale generation accepted");
    Assert(!InstanceGeneration.MatchesExact("", "g1"), "empty command generation accepted");
    Assert(InstanceGeneration.MatchesExact("", ""), "empty generation pair rejected");
}

static void TestInstanceCommandGeneration()
{
    var stop = ControlMessage.Create(1, MessageType.STOP,
        new { instanceKey = "depth-1", generation = "g2" });
    StopPayload? stopPayload = stop.PayloadAs<StopPayload>();
    Assert(stopPayload?.InstanceKey == "depth-1", "stop instance key");
    Assert(stopPayload?.Generation == "g2", "stop generation");

    var suspend = ControlMessage.Create(2, MessageType.PROCESS_SUSPEND,
        new { instanceKey = "depth-2", generation = "g3" });
    ProcessControlPayload? suspendPayload = suspend.PayloadAs<ProcessControlPayload>();
    Assert(suspendPayload?.InstanceKey == "depth-2", "suspend instance key");
    Assert(suspendPayload?.Generation == "g3", "suspend generation");

    var resume = ControlMessage.Create(3, MessageType.PROCESS_RESUME,
        new { instanceKey = "depth-3", generation = "g4" });
    ProcessControlPayload? resumePayload = resume.PayloadAs<ProcessControlPayload>();
    Assert(resumePayload?.InstanceKey == "depth-3", "resume instance key");
    Assert(resumePayload?.Generation == "g4", "resume generation");
}

static void TestInstanceExitGenerationGuard()
{
    var config = new ServerConfig();
    var hub = new ControlHub(config, CancellationToken.None);
    var manager = new InstanceManager(config, hub);

    var instancesField = typeof(InstanceManager).GetField("_instances",
        BindingFlags.Instance | BindingFlags.NonPublic)!;
    var portsField = typeof(InstanceManager).GetField("_usedPorts",
        BindingFlags.Instance | BindingFlags.NonPublic)!;
    var instances = (ConcurrentDictionary<string, InstanceInfo>)instancesField.GetValue(manager)!;
    var usedPorts = (HashSet<int>)portsField.GetValue(manager)!;

    var info = new InstanceInfo
    {
        Key = "depth-1",
        Depth = 1,
        Port = 17790,
        Generation = "g2",
        Status = InstanceStatus.Ready,
    };
    instances["depth-1"] = info;
    usedPorts.Add(17790);

    Assert(!manager.OnInstanceExited("depth-1", "g1", 1), "stale exit accepted");
    Assert(info.Status == InstanceStatus.Ready, "stale exit changed status");
    Assert(usedPorts.Contains(17790), "stale exit freed port");

    Assert(manager.OnInstanceExited("depth-1", "g2", 0), "current exit rejected");
    Assert(info.Status == InstanceStatus.Stopped, "current exit did not stop instance");
    Assert(!usedPorts.Contains(17790), "current exit did not free port");
}

static void TestFailedSpawnCleanup()
{
    var config = new ServerConfig();
    var hub = new ControlHub(config, CancellationToken.None);
    var manager = new InstanceManager(config, hub);

    var agentsField = typeof(InstanceManager).GetField("_agents",
        BindingFlags.Instance | BindingFlags.NonPublic)!;
    var cleanedField = typeof(InstanceManager).GetField("_cleanedMachines",
        BindingFlags.Instance | BindingFlags.NonPublic)!;
    var instancesField = typeof(InstanceManager).GetField("_instances",
        BindingFlags.Instance | BindingFlags.NonPublic)!;
    var portsField = typeof(InstanceManager).GetField("_usedPorts",
        BindingFlags.Instance | BindingFlags.NonPublic)!;
    var runNeededField = typeof(InstanceManager).GetField("_runNeeded",
        BindingFlags.Instance | BindingFlags.NonPublic)!;
    var spawnMethod = typeof(InstanceManager).GetMethod("Spawn",
        BindingFlags.Instance | BindingFlags.NonPublic)!;

    var agents = (AgentRegistry)agentsField.GetValue(manager)!;
    agents.Register("machine-1", 4, "127.0.0.1");
    ((HashSet<string>)cleanedField.GetValue(manager)!).Add("machine-1");

    Assert(spawnMethod.Invoke(manager, new object[] { 1 }) == null, "spawn without agent connection succeeded");
    Assert(((ConcurrentDictionary<string, InstanceInfo>)instancesField.GetValue(manager)!).IsEmpty,
        "failed spawn left instance");
    Assert(((HashSet<int>)portsField.GetValue(manager)!).Count == 0, "failed spawn leaked port");
    Assert(((HashSet<string>)runNeededField.GetValue(manager)!).Count == 0, "failed spawn left run-needed entry");
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

public sealed class Dummy
{
}
