using System;
using System.Reflection;
using HarmonyLib;
using Together;
using UnityEngine;
using CasualtiesTogetherUtils;

namespace CasuMod.Patch;

// 사망 처리 대체: 개인(리스폰 안내 팝업+채팅) + 타 레이어 공지.
// base 의 everyone-died 블록(전체 서버 알림/호스트 팝업)은 비활성화한다
// (개인 !respawn 리스폰이 가능하므로). 바닐라 유지분(발신 레이어 로컬 사망 공지 +
// 튜토리얼 완료 + 시체 컴포넌트)만 재현한다.
//
// 주의: Harmony 는 Prefix 가 원본을 스킵해도 Postfix 를 계속 실행하므로,
//       DiscordEventPatch 의 DIED 보고(Postfix)는 그대로 동작한다.
[HarmonyPatch(typeof(ServerMain), nameof(ServerMain.OnPlayerDeath))]
[HarmonyPriority(Priority.First)]
internal static class DeathAnnouncePatch
{
    // base private 헬퍼 - 사망 메시지(태그 포함) 생성
    private static readonly MethodInfo GenerateDeathMessageMethod =
        AccessTools.Method(typeof(ServerMain), "GenerateDeathMessage", new[] { typeof(ScavPlayer) });

    private static bool Prefix(ScavPlayer plr)
    {
        if (plr == null) return true;
        try
        {
            if (Net.IsServer)
            {
                _ = plr.netBody.body;

                // base 강화 사망 메시지(사유/살해자/무기 태그 포함) 생성
                string tagged = null;
                if (GenerateDeathMessageMethod != null
                    && GenerateDeathMessageMethod.Invoke(null, new object[] { plr }) is string s
                    && !string.IsNullOrEmpty(s))
                {
                    tagged = s;
                }

                // 발신 레이어 로컬 사망 공지 - base 원문 그대로 (태그는 클라가 해석)
                if (Multiplayer.rules.TrackPlayerDeaths && !string.IsNullOrEmpty(tagged))
                {
                    Chat.Server_ChatAnnouncement(tagged);
                }

                // 바닐라 유지: 튜토리얼 완료 처리
                if (Util.IsTutorialWorld())
                {
                    plr.OnFinishTutorial(tp: false);
                }

                // 개인 리스폰 안내 (팝업 + 채팅) - 노출 메시지는 영어
                plr.Server_DoAlertSingle(
                    "<color=#FF8080><b>*You died.*</b></color>\nUse !respawn to revive.",
                    important: true, reliable: true);
                AnnounceRelay.SendChatAnnouncementTo("<color=#FF8080><b>*You died.*</b></color>", plr.playerId);
                AnnounceRelay.SendChatAnnouncementTo("Use !respawn to revive.", plr.playerId);

                // 타 레이어 릴레이 - 태그 해석한 평문 (발신 레이어는 Handle 에서 제외)
                string plain = !string.IsNullOrEmpty(tagged)
                    ? Lang.ParseTaggedString(tagged)
                    : plr.playerName + " died.";
                AnnounceRelay.SendDeath(plr, plain);
            }

            // 바닐라 유지: 시체 컴포넌트 (Get-or-Add)
            CorpseScriptMultiplayerRewrite corpse = plr.gameObject.GetComponent<CorpseScriptMultiplayerRewrite>();
            if (corpse == null)
            {
                corpse = plr.gameObject.AddComponent<CorpseScriptMultiplayerRewrite>();
            }
            corpse.animalCorpse = false;
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[Announce] 사망 처리 실패: {ex.Message}");
        }

        return false; // 원본 스킵 - everyone-died 블록 비활성화
    }
}
