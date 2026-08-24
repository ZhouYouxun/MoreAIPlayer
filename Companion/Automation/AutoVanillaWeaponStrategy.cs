using Terraria;
using Terraria.ID;

namespace MoreAIPlayer.Companion.Automation;

public enum AutoWeaponRouteKind : byte
{
    None,
    CraftStageWeapon,
    WorldChest,
    EvilOrbGun,
    Fishing,
    HuntDrops
}

/// <summary>Vanilla-only strategic overlay. Acquisition routes model what a practical player does
/// (take a lucky chest upgrade, raid one orb for the guaranteed gun, fish when already equipped,
/// otherwise hunt drops) while the generic assessment engine remains responsible for mod weapons.</summary>
public static class AutoVanillaWeaponStrategy
{
    public static AutoWeaponRouteKind ChooseUpgradeRoute(AutoContext ctx)
    {
        if (!AutoCampaignController.NeedsLoadoutWork(ctx))
            return AutoWeaponRouteKind.None;
        if (AutoVanillaWeaponCraftingController.HasPotentialUpgrade(ctx))
            return AutoWeaponRouteKind.CraftStageWeapon;
        if (AutoLootController.HasWorthwhileWorldWeaponChest(ctx))
            return AutoWeaponRouteKind.WorldChest;
        if (!Main.hardMode && ctx.State.CampaignStage <= AutoCampaignStage.EvilBoss &&
            !ctx.State.FirstEvilOrbBroken && BestRangedScore(ctx.Profile) < 36)
            return AutoWeaponRouteKind.EvilOrbGun;
        if (ctx.Fishing.HasOpportunity(ctx))
            return AutoWeaponRouteKind.Fishing;
        return AutoWeaponRouteKind.HuntDrops;
    }

    public static float TacticalMultiplier(Item item, NPC target, AutoCampaignStage stage)
    {
        if (item is null || item.IsAir || item.ModItem is not null)
            return 1f;
        float multiplier = 1f;
        if (target is not null && (target.realLife >= 0 || target.type is NPCID.EaterofWorldsHead or
            NPCID.EaterofWorldsBody or NPCID.EaterofWorldsTail or NPCID.TheDestroyer or
            NPCID.TheDestroyerBody or NPCID.TheDestroyerTail))
        {
            if (item.type is ItemID.Vilethorn or ItemID.WaterBolt or ItemID.DemonScythe or
                ItemID.Starfury or ItemID.SolarEruption or ItemID.DayBreak)
                multiplier *= 1.24f;
        }
        if (stage is AutoCampaignStage.Plantera or AutoCampaignStage.Golem &&
            item.type is ItemID.Megashark or ItemID.Uzi or ItemID.ChlorophyteShotbow or
                ItemID.DeathSickle or ItemID.TrueNightsEdge or ItemID.TrueExcalibur)
            multiplier *= 1.12f;
        if (stage >= AutoCampaignStage.LunaticCultist &&
            item.type is ItemID.Xenopopper or ItemID.ChainGun or ItemID.InfluxWaver or
                ItemID.RazorbladeTyphoon or ItemID.TerraBlade or ItemID.Flairon)
            multiplier *= 1.15f;
        if (stage == AutoCampaignStage.MoonLord &&
            item.type is ItemID.Phantasm or ItemID.NebulaBlaze or ItemID.NebulaArcanum or
                ItemID.SolarEruption or ItemID.DayBreak)
            multiplier *= 1.18f;
        return multiplier;
    }

    private static int BestRangedScore(CompanionProfile profile)
    {
        int best = 0;
        void Scan(Item[] items)
        {
            foreach (Item item in items)
                if (!item.IsAir && AutoWeaponEvaluator.Role(item) == AutoWeaponRole.Ranged)
                    best = System.Math.Max(best, AutoWeaponEvaluator.CombatScore(item));
        }
        Scan(profile.Hotbar);
        Scan(profile.Backpack);
        return best;
    }
}
