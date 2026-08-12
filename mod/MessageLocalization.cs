using System.Collections.Generic;
using HarmonyLib;
using KrokoshaCasualtiesMP;
using KrokoshaCasualtiesUtils;

namespace CasuMod;

// 내장 시스템 메시지 한글화/비활성화 - 접속/킥 공지 한글화("Host is starting game." 비활성화),
// 사망 공지(개인 + 전 레이어 ANNOUNCE 릴레이), 데디 자동 종료 비활성화
// (인스턴스 수명은 오케스트레이터가 관리). 한글 문자열은 로케일 키가 아니므로 그대로 표시된다
public static class MessageLocalization
{
    // 채팅 공지 패턴 번역 - "[이름] just joined the game!" / "Kicked [이름]" /
    // "Host is starting game." 처리 후 false 반환(원본 스킵), 그 외 true
    // subjectName: 접속 공지의 플레이어 이름 (크로스 레이어 ANNOUNCE 발신용), 그 외 null
    internal static bool TryTranslateAnnouncement(string message, out string? translated,
        out bool suppress, out string? subjectName)
    {
        translated = null;
        suppress = false;
        subjectName = null;

        // A-3: 게임 시작 공지 비활성화
        if (message == "Host is starting game.")
        {
            suppress = true;
            return true;
        }

        // A-1: 접속 - "{이름} just joined the game!" (+ "{n}/{min} minimum to start." 접미사 제거)
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

        // A-2: 킥 - "Kicked {이름}"
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

// A - 채팅 공지 번역/비활성화 (Server_ChatAnnouncement 단일 지점)
// 바닐라는 AllClientIds 고정이므로, 한국어 재전송은 AnnounceRelay의 10098 와이어 미러를 사용한다
// 1-arg 오버로드의 매개변수는 byref(string& - in string) - by-value string으로 지정하면
// 타겟 매칭 실패로 PatchAll이 throw한다 ( 확인). MakeByRefType으로 지정
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

        // 마이그레이션/재접속 도착 - 접속 공지 대체: 마이그레이션 "L1->L2 이동" 공지가
        // 대체 표시하므로 join 공지를 억제한다
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
                    // 신규 접속 - 전 레이어 공지 (ANNOUNCE 에코가 발신 레이어 포함 표시)
                    // 마이그레이션/재접속 도착은 위 IsMigrationArrivalJoin에서 억제됐다
                    // playerKey - 접속자 본인 제외용 (이름 매칭 실패 시 빈 값 - 본인 표시 폴백)
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
                    // [*] 시스템 메시지 통일 - 10098 type 2 (name="*") 단일 전송
                    // LogMessage("*SERVER*") 에코 제거 - 클라이언트 [*SERVER*] 접두사 통일
                    AnnounceRelay.SendChatAnnouncementTo(translated, new List<knetid>(ServerMain.AllClientIds));
                }
            }
            return false;
        }
        return true;
    }

    // join 공지가 마이그레이션 도착인지 판정 - MigrationArrivalTracker(마이그레이션
    // 도착 - isMigratingArrival=true) 또는 FREEZE 상태면 억제 대상
    // 퇴장 후 재접속(일반 isReturning)은 접속 공지를 표시한다 - 마이그레이션 시에만
    // 접속/퇴장 메시지를 띄우지 않는 것이 목표
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

// B - 사망 처리 대체: 개인(팝업+채팅) + 전체(모든 레이어) 공지,
// everyone-died 블록 비활성화. 바닐라 유지분(튜토리얼/시체 컴포넌트)만 재현한다
// 기존 Discord DIED Postfix(DiscordEventPatch)는 원본 스킵 후에도 실행된다
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


        // 개인 - 팝업 (10006, important) - 볼드 + 별표 감싸기 강조. 사망 - 연한 빨강 (#FF8080)
        plr.Server_DoAlertSingle("<color=#FF8080><b>*사망했습니다.*</b></color>\n!respawn 명령어를 사용하여 부활하세요.",
            important: true, reliable: true);
        // 개인 - 채팅 (10098 타겟 - 사망자 본인) - 볼드 + 별표 감싸기 (TMP 리치 텍스트 확인됨)
        // 줄넘김은 \n이 아니라 메시지 2회 전송으로 표현 (채팅 패널 라인 렌더링 안전)
        AnnounceRelay.SendChatAnnouncementTo("<color=#FF8080><b>*사망했습니다.*</b></color>", plr.clientId);
        AnnounceRelay.SendChatAnnouncementTo("!respawn 명령어를 사용하여 부활하세요.", plr.clientId);

        // 전체 - 모든 레이어 (오케스트레이터 ANNOUNCE 릴레이)
        AnnounceRelay.SendDeath(plr);

        // 바닐라 유지: 튜토리얼 완료 처리 (데디에서 발신 안 되는 경로)
        if (Util.IsTutorialWorld())
        {
            plr.OnFinishTutorial(tp: false);
        }
        // everyone-died 블록 - 비활성화 (개인 리스폰이 가능하므로 - 채팅/알림/호스트 팝업 생략)

        // 바닐라 유지: 시체 컴포넌트 (ComponentHolderProtocol.AddComponent 대응 - Get-or-Add)
        var corpse = plr.gameObject.GetComponent<Krokosha_CorpseScript_MultiplayerAdditionComponent>();
        if (corpse == null)
        {
            corpse = plr.gameObject.AddComponent<Krokosha_CorpseScript_MultiplayerAdditionComponent>();
        }
        corpse.animalCorpse = false;

        return false;
    }
}

// C - 데디 서버 자동 종료 비활성화 (전원 퇴장/사망 시 ToMainMenu로 인스턴스 자살)
// 오케스트레이터의 RUN DORMANT/유휴 정리가 인스턴스 수명을 관리한다
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
