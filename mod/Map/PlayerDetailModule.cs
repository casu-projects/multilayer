using CasuMp.Protocol;
using System.Collections.Generic;
using System.Linq;
using Together;
using UnityEngine;

namespace CasuMod;

public static class PlayerDetailModule
{
    public static void HandleRequest(PlayerDetailRequest? request)
    {
        if (request == null || string.IsNullOrEmpty(request.RequestId)) return;
        OrchestratorClient client = OrchestratorClient.Instance;
        if (client == null) return;

        PlayerDetailResponse response = Build(request);
        client.SendEvent(MessageType.PLAYER_DETAIL_RESPONSE, response);
    }

    private static PlayerDetailResponse Build(PlayerDetailRequest request)
    {
        ScavPlayer? player = ScavPlayer.AllLivingPlayers
            .Concat(ScavPlayer.AllDeadPlayers)
            .FirstOrDefault(candidate => candidate != null
                && MigrationModule.PlayerKeyOf(candidate) == request.PlayerKey);

        if (player == null || player.body == null)
        {
            return new PlayerDetailResponse
            {
                RequestId = request.RequestId,
                InstanceKey = request.InstanceKey,
                PlayerKey = request.PlayerKey,
                Found = false,
                Error = "player not found",
            };
        }

        Body body = player.body;
        FacialExpression? face = body.GetComponentInChildren<FacialExpression>();
        PlayerDetail detail = new()
        {
            PlayerKey = request.PlayerKey,
            Name = player.playerName ?? "",
            SteamId = player.SteamId == 0 ? "" : player.SteamId.ToString(),
            Dead = ScavPlayer.AllDeadPlayers.Contains(player),
            X = player.pos.x,
            Y = player.pos.y,
            BlockX = WorldGeneration.world != null ? WorldGeneration.world.WorldToBlockPos(player.pos).x : 0,
            BlockY = WorldGeneration.world != null ? WorldGeneration.world.WorldToBlockPos(player.pos).y : 0,
            HeadSprite = face?.head != null && face.head.sprite != null ? face.head.sprite.name : "",
            EyeSprite = face?.eyes != null && face.eyes.sprite != null ? face.eyes.sprite.name : "",
            Skills = new PlayerSkills
            {
                Str = BuildSkill(body.skills.STR, body.skills.expSTR, body.skills.minSTR, body.skills.maxSTR),
                Res = BuildSkill(body.skills.RES, body.skills.expRES, body.skills.minRES, body.skills.maxRES),
                Int = BuildSkill(body.skills.INT, body.skills.expINT, body.skills.minINT, body.skills.maxINT),
            },
            Health = new PlayerHealth
            {
                BrainHealth = body.brainHealth,
                Consciousness = body.consciousness,
                BloodVolume = body.bloodVolume,
                BloodOxygen = body.bloodOxygen,
                BloodPressure = body.bloodPressure,
                HeartRate = body.heartRate,
                RespiratoryRate = body.respiratoryRate,
                Hunger = body.hunger,
                Thirst = body.thirst,
                Temperature = body.temperature,
                Stamina = body.stamina,
                Energy = body.energy,
                Pain = body.averagePain,
                Bleeding = body.totalBleedSpeed,
                Sickness = body.sicknessAmount,
                Immunity = body.immunity,
                Happiness = body.totalHappiness,
                WeightOffset = body.weightOffset,
                Hemothorax = body.hemothorax,
                DefibShockedFrames = body.defibShockedFrames,
                SepticShock = body.septicShock,
                RadiationSickness = body.radiationSickness,
                Fibrillation = body.fibrillationProgress,
                InCardiacArrest = body.inCardiacArrest,
                Sleeping = body.sleeping,
            },
            Limbs = BuildLimbs(body),
            Inventory = BuildInventory(body),
        };

        return new PlayerDetailResponse
        {
            RequestId = request.RequestId,
            InstanceKey = request.InstanceKey,
            PlayerKey = request.PlayerKey,
            Found = true,
            Detail = detail,
        };
    }

    private static PlayerSkillDetail BuildSkill(int level, float exp, int minExp, int maxExp)
    {
        return new PlayerSkillDetail
        {
            Level = level,
            Exp = exp,
            MinExp = minExp,
            MaxExp = maxExp,
        };
    }

    private static List<PlayerLimbDetail> BuildLimbs(Body body)
    {
        var limbs = new List<PlayerLimbDetail>();
        if (body.limbs == null) return limbs;
        foreach (Limb limb in body.limbs)
        {
            if (limb == null) continue;
            limbs.Add(new PlayerLimbDetail
            {
                Name = string.IsNullOrEmpty(limb.fullName) ? limb.shortName ?? "" : limb.fullName,
                IsHead = limb.isHead,
                IsVital = limb.isVital,
                SkinHealth = limb.skinHealth,
                MuscleHealth = limb.muscleHealth,
                Pain = limb.pain,
                Bleed = limb.bleedAmount,
                Broken = limb.broken,
                Dislocated = limb.dislocated,
                Infected = limb.infected,
                Splinted = limb.splinted,
                Dismembered = limb.dismembered,
            });
        }
        return limbs;
    }

    private static List<PlayerInventoryItem> BuildInventory(Body body)
    {
        var items = new List<PlayerInventoryItem>();
        if (body.slots != null)
        {
            for (int i = 0; i < body.slots.Length; i++)
            {
                Item item = body.GetItem(i);
                if (item != null) items.Add(BuildItem(item, i, ""));
            }
        }
        foreach (Item wearable in body.GetAllWearables())
        {
            if (wearable != null)
                items.Add(BuildItem(wearable, -1, wearable.Stats != null ? wearable.Stats.wearSlotId : ""));
        }
        return items;
    }

    private static PlayerInventoryItem BuildItem(Item item, int slot, string wearSlot)
    {
        var entry = new PlayerInventoryItem
        {
            Slot = slot,
            WearSlot = wearSlot ?? "",
            Id = item.id ?? "",
            Name = item.Stats != null ? item.Stats.fullName : item.id ?? "",
            Condition = item.condition,
            Favourited = item.favourited,
        };
        foreach (Transform child in item.transform)
        {
            Item childItem = child.GetComponent<Item>();
            if (childItem == null) continue;
            entry.Contents.Add(BuildItem(childItem, -1, ""));
        }
        return entry;
    }
}
