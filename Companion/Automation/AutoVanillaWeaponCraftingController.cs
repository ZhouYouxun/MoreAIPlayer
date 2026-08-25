using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;

namespace MoreAIPlayer.Companion.Automation;

/// <summary>Stage-aware vanilla crafting branch. It consumes only real bars/fragments and lets
/// vanilla Recipe.FindRecipes validate every ingredient and station. The list is a fallback tree,
/// never a whitelist for combat: any stronger mod weapon still wins in the phone evaluator.</summary>
public static class AutoVanillaWeaponCraftingController
{
    private static readonly int[] Tier3Bars = [ItemID.AdamantiteBar, ItemID.TitaniumBar];

    public static bool HasPotentialUpgrade(AutoContext ctx)
    {
        AutoCampaignStage stage = ctx.State.CampaignStage;
        if (stage == AutoCampaignStage.MoonLord && CurrentClassFragments(ctx) >= 18 &&
            AnyCandidateBeats(ctx, LunarCandidates(ctx.State.PreferredClass)))
            return true;
        if (stage >= AutoCampaignStage.MechanicalBosses &&
            AutoInventory.Count(ctx.Profile, ItemID.HallowedBar) >= 12 &&
            AnyCandidateBeats(ctx, HallowedCandidates(ctx.State.PreferredClass)))
            return true;
        if (Main.hardMode && AutoResourceController.CountAny(ctx.Profile, Tier3Bars) >= 13 &&
            AnyCandidateBeats(ctx, HardmodeOreCandidates(ctx.State.PreferredClass)))
            return true;
        return stage == AutoCampaignStage.WallOfFlesh &&
            AutoInventory.Count(ctx.Profile, ItemID.HellstoneBar) >= 15 &&
            AnyCandidateBeats(ctx, HellstoneCandidates(ctx.State.PreferredClass));
    }

    public static AutoTaskResult CraftBestAvailable(AutoContext ctx)
    {
        int before = AutoWeaponEvaluator.BestScore(ctx.Profile);
        if (ctx.State.CampaignStage == AutoCampaignStage.MoonLord && CurrentClassFragments(ctx) >= 18)
        {
            AutoTaskResult station = EnsureAncientManipulator(ctx);
            if (station != AutoTaskResult.Succeeded)
                return station;
            AutoTaskResult lunar = TryCandidates(ctx, ctx.State.AncientManipulator,
                LunarCandidates(ctx.State.PreferredClass), before);
            if (lunar != AutoTaskResult.AlternativeRequired)
                return lunar;
        }

        if (ctx.State.CampaignStage >= AutoCampaignStage.MechanicalBosses &&
            AutoInventory.Count(ctx.Profile, ItemID.HallowedBar) >= 12)
        {
            AutoTaskResult hallowed = TryCandidates(ctx, ctx.State.HardmodeAnvil,
                HallowedCandidates(ctx.State.PreferredClass), before);
            if (hallowed != AutoTaskResult.AlternativeRequired)
                return hallowed;
        }

        if (Main.hardMode && AutoResourceController.CountAny(ctx.Profile, Tier3Bars) >= 13)
        {
            AutoTaskResult ore = TryCandidates(ctx, ctx.State.HardmodeAnvil,
                HardmodeOreCandidates(ctx.State.PreferredClass), before);
            if (ore != AutoTaskResult.AlternativeRequired)
                return ore;
        }

        if (ctx.State.CampaignStage == AutoCampaignStage.WallOfFlesh &&
            AutoInventory.Count(ctx.Profile, ItemID.HellstoneBar) >= 15)
            return TryCandidates(ctx, ctx.State.Anvil,
                HellstoneCandidates(ctx.State.PreferredClass), before);
        return AutoTaskResult.AlternativeRequired;
    }

    private static AutoTaskResult EnsureAncientManipulator(AutoContext ctx)
    {
        if (LiveStation(ctx.State.AncientManipulator, TileID.LunarCraftingStation))
            return AutoTaskResult.Succeeded;
        if (!AutoInventory.Has(ctx.Profile, ItemID.LunarCraftingStation))
            return AutoTaskResult.AlternativeRequired;
        Point? near = ctx.State.HomeAnchor ?? ctx.State.WorkBench;
        return AutoStationController.EnsureStation(ctx, ItemID.LunarCraftingStation,
            TileID.LunarCraftingStation, 3, near, ref ctx.State.AncientManipulator);
    }

    private static AutoTaskResult TryCandidates(AutoContext ctx, Point? station,
        int[] candidates, int currentScore)
    {
        if (station is Point point && Vector2.DistanceSquared(ctx.Me.Center,
            new Vector2(point.X * 16f + 8f, point.Y * 16f + 8f)) > 5f * 16f * 5f * 16f)
            return AutoWorldTravelController.TravelTo(ctx,
                new Vector2(point.X * 16f + 8f, point.Y * 16f + 8f), 4f);

        foreach (int itemType in candidates)
        {
            Item sample = ContentSamples.ItemsByType[itemType];
            if (CandidateScore(sample) * 100 <= currentScore * 108 ||
                AutoInventory.Has(ctx.Profile, itemType))
                continue;
            if (AutoCraftingService.TryCraft(ctx.Instance, ctx.Profile, itemType))
            {
                AutoGearController.OrganizeOwnedGear(ctx.Me, ctx.Profile);
                ctx.TaskRuntime.RecordWorldProgress();
                return AutoTaskResult.Running;
            }
        }
        return AutoTaskResult.AlternativeRequired;
    }

    private static int CurrentClassFragments(AutoContext ctx) => ctx.State.PreferredClass switch
    {
        AutoCombatClass.Ranged => AutoInventory.Count(ctx.Profile, ItemID.FragmentVortex),
        AutoCombatClass.Magic => AutoInventory.Count(ctx.Profile, ItemID.FragmentNebula),
        AutoCombatClass.Summoner => AutoInventory.Count(ctx.Profile, ItemID.FragmentStardust),
        _ => AutoInventory.Count(ctx.Profile, ItemID.FragmentSolar)
    };

    private static bool AnyCandidateBeats(AutoContext ctx, int[] candidates)
    {
        int current = AutoWeaponEvaluator.BestScore(ctx.Profile);
        foreach (int itemType in candidates)
            if (!AutoInventory.Has(ctx.Profile, itemType) &&
                CandidateScore(ContentSamples.ItemsByType[itemType]) * 100 > current * 108)
                return true;
        return false;
    }

    private static int CandidateScore(Item item) => System.Math.Max(
        AutoWeaponEvaluator.CombatScore(item), AutoWeaponEvaluator.SummonScore(null, item));

    private static int[] LunarCandidates(AutoCombatClass cls) => cls switch
    {
        AutoCombatClass.Ranged => [ItemID.Phantasm, ItemID.VortexBeater],
        AutoCombatClass.Magic => [ItemID.NebulaBlaze, ItemID.NebulaArcanum],
        AutoCombatClass.Summoner => [ItemID.StardustDragonStaff, ItemID.StardustCellStaff],
        _ => [ItemID.SolarEruption, ItemID.DayBreak]
    };

    private static int[] HallowedCandidates(AutoCombatClass cls) => cls switch
    {
        AutoCombatClass.Ranged => [ItemID.HallowedRepeater],
        AutoCombatClass.Magic => [ItemID.MagicalHarp, ItemID.RainbowRod,
            ItemID.HallowedRepeater, ItemID.Excalibur],
        AutoCombatClass.Summoner => [ItemID.OpticStaff, ItemID.HallowedRepeater, ItemID.Excalibur],
        _ => [ItemID.Excalibur]
    };

    private static int[] HardmodeOreCandidates(AutoCombatClass cls) => cls switch
    {
        AutoCombatClass.Ranged => [ItemID.TitaniumRepeater, ItemID.AdamantiteRepeater],
        AutoCombatClass.Magic => [ItemID.CrystalStorm, ItemID.TitaniumRepeater,
            ItemID.AdamantiteRepeater, ItemID.TitaniumSword, ItemID.AdamantiteSword],
        AutoCombatClass.Summoner => [ItemID.SpiderStaff, ItemID.QueenSpiderStaff,
            ItemID.TitaniumRepeater, ItemID.AdamantiteRepeater,
            ItemID.TitaniumSword, ItemID.AdamantiteSword],
        _ => [ItemID.TitaniumSword, ItemID.AdamantiteSword]
    };

    private static int[] HellstoneCandidates(AutoCombatClass cls) => cls switch
    {
        AutoCombatClass.Ranged => [ItemID.MoltenFury, ItemID.PhoenixBlaster],
        AutoCombatClass.Magic => [ItemID.FlowerofFire, ItemID.MoltenFury, ItemID.FieryGreatsword],
        AutoCombatClass.Summoner => [ItemID.ImpStaff, ItemID.MoltenFury, ItemID.FieryGreatsword],
        _ => [ItemID.FieryGreatsword, ItemID.Flamarang]
    };

    private static bool LiveStation(Point? point, int tileType) => point is Point p &&
        WorldGen.InWorld(p.X, p.Y, 5) && Main.tile[p.X, p.Y].HasTile &&
        Main.tile[p.X, p.Y].TileType == tileType;
}
