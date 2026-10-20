using System.Collections.Generic;

namespace CasuMp.Protocol;

public sealed class MapPaletteEntry
{
    public int Block { get; set; }
    public string Color { get; set; } = "#000000";
    public string Name { get; set; } = "";
}

public sealed class MapRleRun
{
    public int Count { get; set; }
    public int Block { get; set; }
}

public sealed class MapWorldBegin
{
    public string RequestId { get; set; } = "";
    public string InstanceKey { get; set; } = "";
    public int Depth { get; set; }
    public int BiomeDepth { get; set; }
    public string WorldVersion { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public List<MapPaletteEntry> Palette { get; set; } = new();
}

public sealed class MapWorldChunk
{
    public string RequestId { get; set; } = "";
    public string WorldVersion { get; set; } = "";
    public int Offset { get; set; }
    public List<MapRleRun> Runs { get; set; } = new();
}

public sealed class MapUploadAck
{
    public string RequestId { get; set; } = "";
    public bool Ok { get; set; }
    public string WorldVersion { get; set; } = "";
    public int Version { get; set; }
    public int Received { get; set; }
    public bool Complete { get; set; }
    public string Error { get; set; } = "";
}

public sealed class MapBlockChange
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Block { get; set; }
}

public sealed class MapBlockUpload
{
    public string RequestId { get; set; } = "";
    public string WorldVersion { get; set; } = "";
    public int BaseVersion { get; set; }
    public List<MapBlockChange> Changes { get; set; } = new();
}

public sealed class PlayerPositionEntry
{
    public string PlayerKey { get; set; } = "";
    public ulong SteamId { get; set; }
    public string Name { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    public int BlockX { get; set; }
    public int BlockY { get; set; }
    public bool Dead { get; set; }
    public string HeadSprite { get; set; } = "";
    public string EyeSprite { get; set; } = "";
}

public sealed class PlayerPositionsPayload
{
    public string InstanceKey { get; set; } = "";
    public string WorldVersion { get; set; } = "";
    public bool HasSpawn { get; set; }
    public int SpawnBlockX { get; set; }
    public int SpawnBlockY { get; set; }
    public List<PlayerPositionEntry> Players { get; set; } = new();
}

public sealed class PlayerDetailRequest
{
    public string RequestId { get; set; } = "";
    public string InstanceKey { get; set; } = "";
    public string PlayerKey { get; set; } = "";
}

public sealed class PlayerDetailResponse
{
    public string RequestId { get; set; } = "";
    public string InstanceKey { get; set; } = "";
    public string PlayerKey { get; set; } = "";
    public bool Found { get; set; }
    public string? Error { get; set; }
    public PlayerDetail? Detail { get; set; }
}

public sealed class PlayerDetail
{
    public string PlayerKey { get; set; } = "";
    public string Name { get; set; } = "";
    public string SteamId { get; set; } = "";
    public bool Dead { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public int BlockX { get; set; }
    public int BlockY { get; set; }
    public string HeadSprite { get; set; } = "";
    public string EyeSprite { get; set; } = "";
    public PlayerHealth Health { get; set; } = new();
    public PlayerSkills Skills { get; set; } = new();
    public List<PlayerLimbDetail> Limbs { get; set; } = new();
    public List<PlayerInventoryItem> Inventory { get; set; } = new();
}

public sealed class PlayerSkills
{
    public PlayerSkillDetail Str { get; set; } = new();
    public PlayerSkillDetail Res { get; set; } = new();
    public PlayerSkillDetail Int { get; set; } = new();
}

public sealed class PlayerSkillDetail
{
    public int Level { get; set; }
    public float Exp { get; set; }
    public int MinExp { get; set; }
    public int MaxExp { get; set; }
}

public sealed class PlayerHealth
{
    public float BrainHealth { get; set; }
    public float Consciousness { get; set; }
    public float BloodVolume { get; set; }
    public float BloodOxygen { get; set; }
    public float BloodPressure { get; set; }
    public float HeartRate { get; set; }
    public float RespiratoryRate { get; set; }
    public float Hunger { get; set; }
    public float Thirst { get; set; }
    public float Temperature { get; set; }
    public float Stamina { get; set; }
    public float Energy { get; set; }
    public float Pain { get; set; }
    public float Bleeding { get; set; }
    public float Sickness { get; set; }
    public float Immunity { get; set; }
    public float Happiness { get; set; }
    public float WeightOffset { get; set; }
    public float Hemothorax { get; set; }
    public int DefibShockedFrames { get; set; }
    public float SepticShock { get; set; }
    public float RadiationSickness { get; set; }
    public float Fibrillation { get; set; }
    public bool InCardiacArrest { get; set; }
    public bool Sleeping { get; set; }
}

public sealed class PlayerLimbDetail
{
    public string Name { get; set; } = "";
    public bool IsHead { get; set; }
    public bool IsVital { get; set; }
    public float SkinHealth { get; set; }
    public float MuscleHealth { get; set; }
    public float Pain { get; set; }
    public float Bleed { get; set; }
    public bool Broken { get; set; }
    public bool Dislocated { get; set; }
    public bool Infected { get; set; }
    public bool Splinted { get; set; }
    public bool Dismembered { get; set; }
}

public sealed class PlayerInventoryItem
{
    public int Slot { get; set; } = -1;
    public string WearSlot { get; set; } = "";
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public float Condition { get; set; }
    public bool Favourited { get; set; }
    public List<PlayerInventoryItem> Contents { get; set; } = new();
}
