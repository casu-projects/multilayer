namespace CasuMpOrchestrator;

internal static class RulesBlobBuilder
{
    internal const int BlobSize = 112;

    internal static byte[] Build(Dictionary<string, string> data)
    {
        var buf = new byte[BlobSize];
        var span = new Span<byte>(buf);

        const int R = 4;
        SetBool(span, R + 0, data, "sv_cheats", false);
        SetBool(span, R + 1, data, "AllowClientCheatCommands", false);
        SetByte(span, R + 2, data, "PLAYER_COUNT_LIMIT", 6);
        SetBool(span, R + 3, data, "ShowPlayerDirections", true);
        SetBool(span, R + 4, data, "EnableNametags", true);
        SetBool(span, R + 5, data, "EnableStatusIcons", true);
        SetBool(span, R + 6, data, "UnchippedHideNametags", true);
        SetBool(span, R + 7, data, "EnableChatbox", true);
        SetBool(span, R + 8, data, "OnlyProximityChat", false);
        SetBool(span, R + 9, data, "UnchippedProximityChat", true);
        SetBool(span, R + 10, data, "UnchippedIsIndividual", true);
        SetBool(span, R + 11, data, "ConsentForFeeding", true);
        SetByte(span, R + 12, data, "ScatterMinGroupSize", 51);
        SetFloat(span, R + 16, data, "ScatterPunishDistance", 80f);
        SetByte(span, R + 20, data, "LayerFinishPlrPercent", 101);
        SetBool(span, R + 21, data, "LayerFinishKeepXOffset", true);
        SetByte(span, R + 22, data, "StragglerRadlinePercent", 30);
        SetBool(span, R + 23, data, "RadlineExtraDamage", true);
        SetBool(span, R + 24, data, "NoInventoryLock", false);
        SetBool(span, R + 25, data, "EnableSleep", false);
        SetBool(span, R + 26, data, "EnableTimeManipulation", false);
        SetBool(span, R + 27, data, "SpeechImpairedChat", true);
        SetBool(span, R + 28, data, "HearingLossChat", true);
        SetBool(span, R + 29, data, "SpeechImpairedVC", true);
        SetBool(span, R + 30, data, "HearingLossVC", true);
        SetBool(span, R + 31, data, "MindwipeDisablesChat", true);
        SetBool(span, R + 32, data, "DeadTextchat", true);
        SetBool(span, R + 33, data, "DeadVoicechat", true);
        SetBool(span, R + 34, data, "SleepingMute", false);
        SetBool(span, R + 35, data, "TrackPlayerDeaths", true);
        SetBool(span, R + 36, data, "Permadeath", false);
        SetBool(span, R + 37, data, "ReviveOnNextLevel", false);
        SetBool(span, R + 38, data, "ReviveFromTrader", true);
        SetBool(span, R + 39, data, "RespawnKeepInventory", false);
        SetBool(span, R + 40, data, "RespawnKeepSkills", false);
        SetBool(span, R + 41, data, "AllowSpectatorFreecam", true);
        SetBool(span, R + 42, data, "AllowPush", true);
        SetBool(span, R + 43, data, "AlwaysAllowCarry", false);
        SetByte(span, R + 44, data, "PiggybackMaxStack", 1);
        SetFloat(span, R + 48, data, "PiggybackWeightMultiplier", 0.8f);
        SetByte(span, R + 52, data, "MinigameMaxPlayers", 50);
        SetBool(span, R + 53, data, "SpectateWhileUnconscious", false);
        SetBool(span, R + 54, data, "EnableMP3Sync", true);
        SetByte(span, R + 55, data, "VoicechatQuality", 4);
        SetBool(span, R + 56, data, "VoicechatEnabled", true);
        SetFloat(span, R + 60, data, "ProximityHearDistance", 55f);
        SetBool(span, R + 64, data, "CharacterYapPublic", true);
        SetBool(span, R + 65, data, "Teams", false);
        SetBool(span, R + 66, data, "PVP", false);
        SetBool(span, R + 67, data, "PVPCombatDismember", true);
        SetFloat(span, R + 68, data, "PVPMoodDebuff", 0.5f);
        SetFloat(span, R + 72, data, "PVPDamageMultiplier", 1f);
        SetBool(span, R + 76, data, "LateJoinAllowed", true);
        SetBool(span, R + 77, data, "LateJoinSpectate", false);
        SetBool(span, R + 78, data, "SpawnOnPlayer", true);
        SetBool(span, R + 79, data, "AmputateHealthyPlayers", true);
        SetFloat(span, R + 80, data, "AdditionalBrainRegen", 1f);
        SetFloat(span, R + 84, data, "AdditionalHealthRegen", 1f);
        SetFloat(span, R + 88, data, "AdditionalHealthDecay", 1f);
        SetBool(span, R + 92, data, "LastStandAllowed", true);
        SetFloat(span, R + 96, data, "SelfharmWitnessMoodDebuff", 3f);
        SetBool(span, R + 100, data, "SavePlayerState", true);
        SetBool(span, R + 101, data, "SavePlayerInventory", true);
        SetBool(span, R + 102, data, "SavePlayerPosition", true);
        SetBool(span, R + 103, data, "AutoContinue", false);
        SetUshort(span, R + 104, data, "AutoMinPlrsToStart", 2);
        SetBool(span, R + 106, data, "AutoExitWhenAllDied", true);
        SetBool(span, R + 107, data, "AutoExitWhenAllLeft", true);

        return buf;
    }

    private static void SetBool(Span<byte> span, int offset, Dictionary<string, string> data, string key, bool defaultVal)
    {
        span[offset] = (byte)(data.TryGetValue(key, out string? v) && bool.TryParse(v, out bool r) ? (r ? 1 : 0) : (defaultVal ? 1 : 0));
    }

    private static void SetByte(Span<byte> span, int offset, Dictionary<string, string> data, string key, byte defaultVal)
    {
        span[offset] = byte.TryParse(data.GetValueOrDefault(key), out byte r) ? r : defaultVal;
    }

    private static void SetFloat(Span<byte> span, int offset, Dictionary<string, string> data, string key, float defaultVal)
    {
        float val = float.TryParse(data.GetValueOrDefault(key), out float r) ? r : defaultVal;
        BitConverter.TryWriteBytes(span.Slice(offset, 4), val);
    }

    private static void SetUshort(Span<byte> span, int offset, Dictionary<string, string> data, string key, ushort defaultVal)
    {
        ushort val = ushort.TryParse(data.GetValueOrDefault(key), out ushort r) ? r : defaultVal;
        BitConverter.TryWriteBytes(span.Slice(offset, 2), val);
    }
}
