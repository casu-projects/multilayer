namespace CasuMpOrchestrator;

/// <summary>rule.json → Steam 로비 EXTRADATA용 KrokoshaMultiplayerGameRules 구조체 이진
/// 레이아웃 재구성 (구 시스템 RulesBlobBuilder 이식 — 2026-08-03). 규칙은 RunRuleStore
/// (rule.json)가 단일 정본이므로 모드 경유 없이 여기서 직접 구성한다.
/// 100바이트: int deathcounter(4) + KrokoshaMultiplayerGameRules(96) — 구조체 오프셋은
/// 구 시스템 검증값을 그대로 사용한다 (게임 업데이트 시 재검증 필요).</summary>
internal static class RulesBlobBuilder
{
    internal static byte[] Build(Dictionary<string, string> data)
    {
        // 100 bytes: int deathcounter (4) + KrokoshaMultiplayerGameRules (96)
        var buf = new byte[100];
        var span = new Span<byte>(buf);

        // deathcounter at offset 0 (default 0)
        // rules start at offset 4
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
        SetByte(span, R + 11, data, "ScatterMinGroupSize", 51);
        SetFloat(span, R + 12, data, "ScatterPunishDistance", 80f);
        SetByte(span, R + 16, data, "LayerFinishPlrPercent", 101);
        SetBool(span, R + 17, data, "LayerFinishKeepXOffset", true);
        SetByte(span, R + 18, data, "StragglerRadlinePercent", 30);
        SetBool(span, R + 19, data, "NoInventoryLock", false);
        SetBool(span, R + 20, data, "EnableSleep", false);
        SetBool(span, R + 21, data, "EnableTimeManipulation", false);
        SetBool(span, R + 22, data, "SpeechImpairedChat", true);
        SetBool(span, R + 23, data, "HearingLossChat", true);
        SetBool(span, R + 24, data, "MindwipeDisablesChat", true);
        SetBool(span, R + 25, data, "DeadTextchat", true);
        SetBool(span, R + 26, data, "DeadVoicechat", true);
        SetBool(span, R + 27, data, "SleepingMute", false);
        SetBool(span, R + 28, data, "Permadeath", false);
        SetBool(span, R + 29, data, "ReviveOnNextLevel", false);
        SetBool(span, R + 30, data, "ReviveFromTrader", true);
        SetBool(span, R + 31, data, "RespawnKeepInventory", false);
        SetBool(span, R + 32, data, "RespawnKeepSkills", false);
        SetBool(span, R + 33, data, "AllowSpectatorFreecam", true);
        SetBool(span, R + 34, data, "AllowPush", true);
        SetBool(span, R + 35, data, "AlwaysAllowCarry", false);
        SetByte(span, R + 36, data, "PiggybackMaxStack", 1);
        // padding 37-39
        SetFloat(span, R + 40, data, "PiggybackWeightMultiplier", 0.8f);
        SetBool(span, R + 44, data, "SpectateWhileUnconscious", false);
        SetBool(span, R + 45, data, "EnableMP3Sync", true);
        SetByte(span, R + 46, data, "VoicechatQuality", 4);
        SetBool(span, R + 47, data, "VoicechatEnabled", true);
        SetFloat(span, R + 48, data, "ProximityHearDistance", 55f);
        SetBool(span, R + 52, data, "CharacterYapPublic", true);
        SetBool(span, R + 53, data, "Teams", false);
        SetBool(span, R + 54, data, "PVP", false);
        SetBool(span, R + 55, data, "PVPCombatDismember", true);
        SetFloat(span, R + 56, data, "PVPMoodDebuff", 0.5f);
        SetFloat(span, R + 60, data, "PVPDamageMultiplier", 1f);
        SetBool(span, R + 64, data, "LateJoinAllowed", true);
        SetBool(span, R + 65, data, "LateJoinSpectate", false);
        SetBool(span, R + 66, data, "AmputateHealthyPlayers", true);
        // padding 67
        SetFloat(span, R + 68, data, "AdditionalBrainRegen", 1f);
        SetFloat(span, R + 72, data, "AdditionalHealthRegen", 1f);
        SetFloat(span, R + 76, data, "AdditionalHealthDecay", 1f);
        SetBool(span, R + 80, data, "LastStandAllowed", true);
        // padding 81-83
        SetFloat(span, R + 84, data, "SelfharmWitnessMoodDebuff", 3f);
        SetBool(span, R + 88, data, "SavePlayerState", true);
        SetBool(span, R + 89, data, "SavePlayerInventory", true);
        SetBool(span, R + 90, data, "SavePlayerPosition", true);
        SetBool(span, R + 91, data, "AutoContinue", false);
        SetUshort(span, R + 92, data, "AutoMinPlrsToStart", 2);
        SetBool(span, R + 94, data, "AutoExitWhenAllDied", true);
        SetBool(span, R + 95, data, "AutoExitWhenAllLeft", true);

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
