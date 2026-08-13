using Steamworks;

namespace CasuMpGateway;

// Steam 접속 이름 해석 - 인트로의 username은 1바이트/문자 인코딩이라 CJK가 저바이트로 잘린다.
// 깨진 이름(제어문자 포함)일 때만 Steamworks 로컬 조회로 진짜 유저명을 얻는다 (HTTP 없음).
internal static class SteamNameResolver
{
    // 1바이트 잘림의 특징: 0x00~0x1F / 0x7F 제어문자가 이름에 섞인다.
    internal static bool IsBrokenName(string name)
    {
        if (string.IsNullOrEmpty(name)) return true;
        foreach (char c in name)
        {
            if (c < 0x20 || c == 0x7F) return true;
        }
        return false;
    }

    // Steamworks 초기화/로그온 상태에서만 정확 - 실패 시 null (기존 이름 유지).
    internal static string? ResolveName(ulong steamId)
    {
        try
        {
            if (!SteamAPI.IsSteamRunning()) return null;
            string name = SteamFriends.GetFriendPersonaName(new CSteamID(steamId));
            return string.IsNullOrEmpty(name) ? null : name;
        }
        catch
        {
            return null;
        }
    }
}
