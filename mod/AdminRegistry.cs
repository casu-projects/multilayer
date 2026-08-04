using System.Collections.Generic;
using KrokoshaCasualtiesMP;

namespace CasuMod;

/// <summary>어드민 SteamID 목록 — 오케스트레이터 ADMIN_LIST 푸시로 갱신 (채팅 [*ADMIN*] 태그 판정).
/// 살아있는 플레이어만 태그 대상 — 사망 어드민은 바닐라 사망 태그를 유지한다.</summary>
public static class AdminRegistry
{
    private static readonly HashSet<string> SteamIds = new();

    public static void SetAdminSteamIds(IEnumerable<string>? ids)
    {
        lock (SteamIds)
        {
            SteamIds.Clear();
            if (ids == null) return;
            foreach (string id in ids)
            {
                if (!string.IsNullOrWhiteSpace(id)) SteamIds.Add(id);
            }
        }
    }

    public static bool IsAdmin(NetPlayer plr) =>
        plr != null && plr.steam_id != 0 && SteamIds.Contains(plr.steam_id.ToString());
}

/// <summary>ADMIN_LIST 페이로드 (오케스트레이터 → 모드).</summary>
public sealed class AdminListPayload
{
    public string[] AdminSteamIds { get; set; } = System.Array.Empty<string>();
}
