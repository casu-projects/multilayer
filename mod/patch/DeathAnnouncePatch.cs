using System;
using System.Reflection;
using HarmonyLib;
using Together;
using UnityEngine;
using CasualtiesTogetherUtils;

namespace CasuMod.Patch;

[HarmonyPatch(typeof(ServerMain), nameof(ServerMain.OnPlayerDeath))]
[HarmonyPriority(Priority.First)]
internal static class DeathAnnouncePatch
{
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

                string tagged = null;
                if (GenerateDeathMessageMethod != null
                    && GenerateDeathMessageMethod.Invoke(null, new object[] { plr }) is string s
                    && !string.IsNullOrEmpty(s))
                {
                    tagged = s;
                }

                if (Multiplayer.rules.TrackPlayerDeaths && !string.IsNullOrEmpty(tagged))
                {
                    Chat.Server_ChatAnnouncement(tagged);
                }

                if (Util.IsTutorialWorld())
                {
                    plr.OnFinishTutorial(tp: false);
                }

                plr.Server_DoAlertSingle(
                    "<color=#FF8080><b>*You died.*</b></color>\nUse !respawn to revive.",
                    important: true, reliable: true);
                AnnounceRelay.SendChatAnnouncementTo("<color=#FF8080><b>*You died.*</b></color>", plr.playerId);
                AnnounceRelay.SendChatAnnouncementTo("Use !respawn to revive.", plr.playerId);

                string plain = !string.IsNullOrEmpty(tagged)
                    ? Lang.ParseTaggedString(tagged)
                    : plr.playerName + " died.";
                AnnounceRelay.SendDeath(plr, plain);
            }

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
