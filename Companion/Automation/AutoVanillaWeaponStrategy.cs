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
    public static AutoWeaponRouteKind ChooseUpgradeRoute(AutoContext ctx,
        System.Func<AutoWeaponRouteKind, bool> allowed = null)
    {
        if (!AutoCampaignController.NeedsLoadoutWork(ctx))
            return AutoWeaponRouteKind.None;
        bool Allowed(AutoWeaponRouteKind route) => allowed is null || allowed(route);
        if (Allowed(AutoWeaponRouteKind.CraftStageWeapon) &&
            IsUpgradeRouteAvailableCore(ctx, AutoWeaponRouteKind.CraftStageWeapon))
            return AutoWeaponRouteKind.CraftStageWeapon;
        if (Allowed(AutoWeaponRouteKind.WorldChest) &&
            IsUpgradeRouteAvailableCore(ctx, AutoWeaponRouteKind.WorldChest))
            return AutoWeaponRouteKind.WorldChest;
        if (Allowed(AutoWeaponRouteKind.EvilOrbGun) &&
            IsUpgradeRouteAvailableCore(ctx, AutoWeaponRouteKind.EvilOrbGun))
            return AutoWeaponRouteKind.EvilOrbGun;
        if (Allowed(AutoWeaponRouteKind.Fishing) &&
            IsUpgradeRouteAvailableCore(ctx, AutoWeaponRouteKind.Fishing))
            return AutoWeaponRouteKind.Fishing;
        return Allowed(AutoWeaponRouteKind.HuntDrops) &&
            IsUpgradeRouteAvailableCore(ctx, AutoWeaponRouteKind.HuntDrops)
            ? AutoWeaponRouteKind.HuntDrops : AutoWeaponRouteKind.None;
    }

    /// <summary>Provider-level availability used by the parallel portfolio. It answers whether one
    /// route is actionable now without letting an earlier route hide later alternatives.</summary>
    public static bool IsUpgradeRouteAvailable(AutoContext ctx, AutoWeaponRouteKind route) =>
        AutoCampaignController.NeedsLoadoutWork(ctx) && IsUpgradeRouteAvailableCore(ctx, route);

    private static bool IsUpgradeRouteAvailableCore(AutoContext ctx,
        AutoWeaponRouteKind route) => route switch
    {
        AutoWeaponRouteKind.CraftStageWeapon =>
            AutoVanillaWeaponCraftingController.HasPotentialUpgrade(ctx),
        AutoWeaponRouteKind.WorldChest => AutoLootController.HasWorthwhileWorldWeaponChest(ctx),
        AutoWeaponRouteKind.EvilOrbGun => !Main.hardMode &&
            ctx.State.CampaignStage <= AutoCampaignStage.EvilBoss &&
            !ctx.State.FirstEvilOrbBroken && BestRangedScore(ctx.Profile) < 36,
        AutoWeaponRouteKind.Fishing => ctx.Fishing.HasOpportunity(ctx),
        AutoWeaponRouteKind.HuntDrops => true,
        _ => false
    };

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
