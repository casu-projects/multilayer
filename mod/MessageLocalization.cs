using System.Collections.Generic;
using HarmonyLib;
using KrokoshaCasualtiesMP;
using KrokoshaCasualtiesUtils;

namespace CasuMod;

// 내장 시스템 메시지 한글화/비활성화 — 접속/킥 공지 번역, 사망 처리 대체,
// 데디 서버 자동 종료 비활성화 (인스턴스 수명은 오케스트레이터가 관리).
public static class MessageLocalization
{
    // 채팅 공지 패턴 번역 — 처리 시 true + translated/suppress/subjectName 설정.
    internal static bool TryTranslateAnnouncement(string message, out string? translated,
        out bool suppress, out string? subjectName)
    {
        translated = null;
        suppress = false;
        subjectName = null;

        // "Host is starting game." — 비활성화.
        if (message == "Host is starting game.")
        {
            suppress = true;
            return true;
        }

        // "{이름} just joined the game!" → 접속 공지.
        const string joinSuffix = " just joined the game!";
        int joinIdx = message.IndexOf(joinSuffix, System.StringComparison.Ordinal);
        if (joinIdx >= 0)
        {
            string name = message.Substring(0, joinIdx);
            if (!string.IsNullOrEmpty(name))
            {
                translated = $"{name}님이 접속했습니다.";
                subjectName = name;
                return true;
            }
        }

        // "Kicked {이름}" → 추방 공지.
        const string kickPrefix = "Kicked ";
        if (message.StartsWith(kickPrefix, System.StringComparison.Ordinal))
        {
            string name = message.Substring(kickPrefix.Length);
            if (!string.IsNullOrEmpty(name))
            {
                translated = $"{name}님이 추방되었습니다.";
                return true;
            }
        }

        return false;
    }
}

// 채팅 공지 번역/비활성화 (Server_ChatAnnouncement 단일 지점).
// 1-arg 오버로드의 매개변수는 byref(string&) — by-value로 지정하면 PatchAll이 실패한다.
[HarmonyPatch]
[HarmonyPriority(Priority.First)]
internal static class Chat_ServerChatAnnouncement_LocalizePatch
{
    [HarmonyTargetMethod]
    private static System.Reflection.MethodBase TargetMethod()
    {
        return AccessTools.DeclaredMethod(typeof(Chat), nameof(Chat.Server_ChatAnnouncement),
            new[] { typeof(string).MakeByRefType() });
    }

    private static bool Prefix(ref string message)
    {
        if (!KrokoshaScavMultiplayer.is_dedicated_server)
            return true;

        // 마이그레이션 도착 — "L1→L2 이동" 공지가 대체 표시하므로 join 공지를 억제한다.
        if (IsMigrationArrivalJoin(message))
            return false;

        if (MessageLocalization.TryTranslateAnnouncement(message, out string? translated, out bool suppress,
                out string? subjectName))
        {
            if (suppress)
                return false;

            if (!string.IsNullOrEmpty(translated))
            {
                if (subjectName != null)
                {
                    string joinPid = "";
                    foreach (NetPlayer p in NetPlayer.ClientIdToPlayerDict.Values)
                    {
                        if (p.playername == subjectName)
                        {
                            joinPid = p.GetPersistentId();
                            break;
                        }
                    }
                    AnnounceRelay.SendJoin(subjectName, joinPid);
                }
                else
                {
                    AnnounceRelay.SendChatAnnouncementTo(translated, new List<knetid>(ServerMain.AllClientIds));
                }
            }
            return false;
        }
        return true;
    }

    // join 공지가 마이그레이션 도착인지 판정 — 마이그레이션 도착/동결이면 억제.
    // 퇴장 후 일반 재접속은 접속 공지를 표시한다.
    private static bool IsMigrationArrivalJoin(string message)
    {
        const string joinSuffix = " just joined the game!";
        int idx = message.IndexOf(joinSuffix, System.StringComparison.Ordinal);
        if (idx < 0) return false;
        string name = message.Substring(0, idx);
        if (string.IsNullOrEmpty(name)) return false;

        foreach (NetPlayer plr in NetPlayer.ClientIdToPlayerDict.Values)
        {
            if (plr.playername != name) continue;
            return MigrationModule.IsFrozen(plr.GetPersistentId())
                || MigrationArrivalTracker.ClientIds.Contains(plr.clientId);
        }
        return false;
    }
}

// 사망 처리 대체 — 개인(팝업+채팅) + 전체(모든 레이어) 공지. everyone-died 블록 비활성화.
// 바닐라 유지분(튜토리얼/시체 컴포넌트)만 재현한다.
[HarmonyPatch(typeof(ServerMain), nameof(ServerMain.OnPlayerDeath))]
[HarmonyPriority(Priority.First)]
internal static class ServerMain_OnPlayerDeath_LocalizedPatch
{
    private static bool Prefix(NetPlayer plr)
    {
        if (!KrokoshaScavMultiplayer.is_dedicated_server)
            return true;
        if (plr == null)
            return true;


        // 개인 — 팝업 + 채팅 (사망자 본인).
        plr.Server_DoAlertSingle("<color=#FF8080><b>*사망했습니다.*</b></color>\n!respawn 명령어를 사용하여 부활하세요.",
            important: true, reliable: true);
        AnnounceRelay.SendChatAnnouncementTo("<color=#FF8080><b>*사망했습니다.*</b></color>", plr.clientId);
        AnnounceRelay.SendChatAnnouncementTo("!respawn 명령어를 사용하여 부활하세요.", plr.clientId);

        // 전체 — 모든 레이어 (오케스트레이터 ANNOUNCE 릴레이).
        AnnounceRelay.SendDeath(plr);

        // 바닐라 유지: 튜토리얼 완료 처리.
        if (Util.IsTutorialWorld())
        {
            plr.OnFinishTutorial(tp: false);
        }

        // 바닐라 유지: 시체 컴포넌트 (Get-or-Add).
        var corpse = plr.gameObject.GetComponent<Krokosha_CorpseScript_MultiplayerAdditionComponent>();
        if (corpse == null)
        {
            corpse = plr.gameObject.AddComponent<Krokosha_CorpseScript_MultiplayerAdditionComponent>();
        }
        corpse.animalCorpse = false;

        return false;
    }
}

// 데디 서버 자동 종료 비활성화 (전원 퇴장/사망 시 ToMainMenu).
[HarmonyPatch(typeof(ServerMain), "HandleDedicatedServerUpdate")]
[HarmonyPriority(Priority.First)]
internal static class ServerMain_HandleDedicatedServerUpdate_DisablePatch
{
    private static bool Prefix()
    {
        if (!KrokoshaScavMultiplayer.is_dedicated_server)
            return true;
        return false;
    }
}
