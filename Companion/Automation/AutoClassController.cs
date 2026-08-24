using System.Collections.Generic;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace MoreAIPlayer.Companion.Automation;

/// <summary>Chooses a class from real owned weapons with hysteresis. A lucky high-tier drop may
/// redirect the campaign, but a tiny sidegrade cannot make armour and ammo plans oscillate.</summary>
public static class AutoClassController
{
    private const float SwitchMargin = 1.18f;

    private static readonly Dictionary<int, AutoCombatClass> ArmourClasses = BuildArmourClasses();

    public static bool Refresh(Player me, CompanionProfile profile)
    {
        AutoCompanionState state = profile.AutoState;
        if (state is null)
            return false;

        int melee = 0, ranged = 0, magic = 0, summon = 0;
        Score(profile.Hotbar);
        Score(profile.Backpack);

        AutoCombatClass bestClass = AutoCombatClass.Melee;
        int best = melee;
        Consider(AutoCombatClass.Ranged, ranged);
        Consider(AutoCombatClass.Magic, magic);
        Consider(AutoCombatClass.Summoner, summon);

        AutoCombatClass current = state.PreferredClass;
        int currentScore = current switch
        {
            AutoCombatClass.Melee => melee,
            AutoCombatClass.Ranged => ranged,
            AutoCombatClass.Magic => magic,
            AutoCombatClass.Summoner => summon,
            _ => 0
        };

        if (best <= 0)
            bestClass = AutoCombatClass.Flexible;
        else if (current != AutoCombatClass.Flexible && currentScore > 0 &&
            best < currentScore * SwitchMargin)
            bestClass = current;

        if (bestClass == state.PreferredClass)
            return false;
        state.PreferredClass = bestClass;
        return true;

        void Score(Item[] items)
        {
            foreach (Item item in items)
            {
                if (item is null || item.IsAir)
                    continue;
                if (CompanionWeaponBehaviors.IsMinionStaff(item) ||
                    CompanionWeaponBehaviors.IsSentryStaff(item))
                {
                    summon = System.Math.Max(summon, AutoWeaponEvaluator.SummonScore(me, item));
                    continue;
                }
                int score = AutoWeaponEvaluator.CombatScore(me, item);
                // Projectile-emitting melee weapons (Terra Blade and many modded swords) are
                // tactically ranged but still scale from melee armour. Class choice follows the
                // declared DamageType first; Role remains the separate movement/range decision.
                if (item.DamageType == DamageClass.SummonMeleeSpeed)
                {
                    summon = System.Math.Max(summon, score);
                    continue;
                }
                if (item.DamageType == DamageClass.Melee)
                {
                    melee = System.Math.Max(melee, score);
                    continue;
                }
                if (item.DamageType == DamageClass.Ranged)
                {
                    ranged = System.Math.Max(ranged, score);
                    continue;
                }
                if (item.DamageType == DamageClass.Magic)
                {
                    magic = System.Math.Max(magic, score);
                    continue;
                }
                switch (AutoWeaponEvaluator.Role(item))
                {
                    case AutoWeaponRole.Close:
                        melee = System.Math.Max(melee, score);
                        break;
                    case AutoWeaponRole.Ranged:
                        ranged = System.Math.Max(ranged, score);
                        break;
                    case AutoWeaponRole.Magic:
                        magic = System.Math.Max(magic, score);
                        break;
                    case AutoWeaponRole.Whip:
                        summon = System.Math.Max(summon, score);
                        break;
                }
            }
        }

        void Consider(AutoCombatClass candidate, int score)
        {
            if (score <= best)
                return;
            best = score;
            bestClass = candidate;
        }
    }

    /// <summary>Class affinity is deliberately secondary to defence, not a hard whitelist. This
    /// makes a class-matching set win among similar tiers while a vastly safer off-class drop can
    /// still be worn until the intended set exists.</summary>
    public static int ArmourScore(Item item, AutoCombatClass preferred)
    {
        if (item is null || item.IsAir)
            return 0;
        int score = item.defense * 100 + System.Math.Max(0, item.rare) * 12;
        if (ArmourClasses.TryGetValue(item.type, out AutoCombatClass armourClass) &&
            (armourClass == preferred || armourClass == AutoCombatClass.Flexible ||
                preferred == AutoCombatClass.Flexible))
            score += 180;
        return score;
    }

    private static Dictionary<int, AutoCombatClass> BuildArmourClasses()
    {
        Dictionary<int, AutoCombatClass> map = [];
        Add(AutoCombatClass.Melee,
            ItemID.MoltenHelmet, ItemID.MoltenBreastplate, ItemID.MoltenGreaves,
            ItemID.CobaltMask, ItemID.CobaltBreastplate, ItemID.CobaltLeggings,
            ItemID.PalladiumMask, ItemID.PalladiumBreastplate, ItemID.PalladiumLeggings,
            ItemID.MythrilHelmet, ItemID.MythrilChainmail, ItemID.MythrilGreaves,
            ItemID.OrichalcumMask, ItemID.OrichalcumBreastplate, ItemID.OrichalcumLeggings,
            ItemID.AdamantiteHelmet, ItemID.AdamantiteBreastplate, ItemID.AdamantiteLeggings,
            ItemID.TitaniumMask, ItemID.TitaniumBreastplate, ItemID.TitaniumLeggings,
            ItemID.HallowedMask, ItemID.HallowedPlateMail, ItemID.HallowedGreaves,
            ItemID.TurtleHelmet, ItemID.TurtleScaleMail, ItemID.TurtleLeggings,
            ItemID.BeetleHelmet, ItemID.BeetleScaleMail, ItemID.BeetleLeggings,
            ItemID.SolarFlareHelmet, ItemID.SolarFlareBreastplate, ItemID.SolarFlareLeggings);
        Add(AutoCombatClass.Ranged,
            ItemID.NecroHelmet, ItemID.NecroBreastplate, ItemID.NecroGreaves,
            ItemID.CobaltHelmet, ItemID.CobaltBreastplate, ItemID.CobaltLeggings,
            ItemID.PalladiumHelmet, ItemID.PalladiumBreastplate, ItemID.PalladiumLeggings,
            ItemID.MythrilHat, ItemID.MythrilChainmail, ItemID.MythrilGreaves,
            ItemID.OrichalcumHelmet, ItemID.OrichalcumBreastplate, ItemID.OrichalcumLeggings,
            ItemID.AdamantiteMask, ItemID.AdamantiteBreastplate, ItemID.AdamantiteLeggings,
            ItemID.TitaniumHelmet, ItemID.TitaniumBreastplate, ItemID.TitaniumLeggings,
            ItemID.HallowedHelmet, ItemID.HallowedPlateMail, ItemID.HallowedGreaves,
            ItemID.ShroomiteHeadgear, ItemID.ShroomiteMask, ItemID.ShroomiteHelmet,
            ItemID.ShroomiteBreastplate, ItemID.ShroomiteLeggings,
            ItemID.VortexHelmet, ItemID.VortexBreastplate, ItemID.VortexLeggings);
        Add(AutoCombatClass.Magic,
            ItemID.MeteorHelmet, ItemID.MeteorSuit, ItemID.MeteorLeggings,
            ItemID.JungleHat, ItemID.JungleShirt, ItemID.JunglePants,
            ItemID.CobaltHat, ItemID.CobaltBreastplate, ItemID.CobaltLeggings,
            ItemID.PalladiumHeadgear, ItemID.PalladiumBreastplate, ItemID.PalladiumLeggings,
            ItemID.MythrilHood, ItemID.MythrilChainmail, ItemID.MythrilGreaves,
            ItemID.OrichalcumHeadgear, ItemID.OrichalcumBreastplate, ItemID.OrichalcumLeggings,
            ItemID.AdamantiteHeadgear, ItemID.AdamantiteBreastplate, ItemID.AdamantiteLeggings,
            ItemID.TitaniumHeadgear, ItemID.TitaniumBreastplate, ItemID.TitaniumLeggings,
            ItemID.HallowedHeadgear, ItemID.HallowedPlateMail, ItemID.HallowedGreaves,
            ItemID.SpectreHood, ItemID.SpectreMask, ItemID.SpectreRobe, ItemID.SpectrePants,
            ItemID.NebulaHelmet, ItemID.NebulaBreastplate, ItemID.NebulaLeggings);
        Add(AutoCombatClass.Summoner,
            ItemID.BeeHeadgear, ItemID.BeeBreastplate, ItemID.BeeGreaves,
            ItemID.ObsidianHelm, ItemID.ObsidianShirt, ItemID.ObsidianPants,
            ItemID.HallowedHood, ItemID.HallowedPlateMail, ItemID.HallowedGreaves,
            ItemID.TikiMask, ItemID.TikiShirt, ItemID.TikiPants,
            ItemID.SpookyHelmet, ItemID.SpookyBreastplate, ItemID.SpookyLeggings,
            ItemID.StardustHelmet, ItemID.StardustBreastplate, ItemID.StardustLeggings);
        Add(AutoCombatClass.Flexible,
            ItemID.CobaltBreastplate, ItemID.CobaltLeggings,
            ItemID.PalladiumBreastplate, ItemID.PalladiumLeggings,
            ItemID.MythrilChainmail, ItemID.MythrilGreaves,
            ItemID.OrichalcumBreastplate, ItemID.OrichalcumLeggings,
            ItemID.AdamantiteBreastplate, ItemID.AdamantiteLeggings,
            ItemID.TitaniumBreastplate, ItemID.TitaniumLeggings,
            ItemID.HallowedPlateMail, ItemID.HallowedGreaves,
            ItemID.ChlorophytePlateMail, ItemID.ChlorophyteGreaves);
        return map;

        void Add(AutoCombatClass cls, params int[] ids)
        {
            foreach (int id in ids)
                map[id] = cls;
        }
    }
}
