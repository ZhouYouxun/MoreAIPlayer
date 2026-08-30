using Microsoft.Xna.Framework;
using System;
using Terraria;
using Terraria.ID;

namespace MoreAIPlayer.Companion.Automation;

/// <summary>Opportunistic chest routing. A chest can replace a prescribed gear expedition when its
/// live contents already provide an equivalent capability; locked and previously reviewed chests
/// are never ground on forever.</summary>
public static class AutoLootController
{
    private const int SearchRadiusTiles = 90;
    private const int BootstrapSearchRadiusTiles = 140;
    private const float InteractRangeTiles = 3.5f;

    public static bool HasWorthwhileNearbyChest(AutoContext ctx, out int utility) =>
        TryFindBestChest(ctx, out _, out utility);

    public static bool HasWorthwhileWorldWeaponChest(AutoContext ctx)
    {
        int current = AutoWeaponEvaluator.BestScore(ctx.Profile);
        int minimum = System.Math.Max(current * 112 / 100,
            AutoCampaignController.RequiredWeaponScore(ctx.State.CampaignStage) * 70 / 100);
        return TryFindBestChest(ctx, global: true,
            item => PortableWeaponScore(item) >= minimum, out _, out _);
    }

    public static AutoTaskResult LootBestNearbyChest(AutoContext ctx)
    {
        Point? target = ResolveTarget(ctx);
        if (target is not Point chestPoint)
            return AutoTaskResult.AlternativeRequired;

        return LootChestAt(ctx, chestPoint, null);
    }

    public static AutoTaskResult LootBestWorldWeaponChest(AutoContext ctx)
    {
        int current = AutoWeaponEvaluator.BestScore(ctx.Profile);
        int minimum = System.Math.Max(current * 112 / 100,
            AutoCampaignController.RequiredWeaponScore(ctx.State.CampaignStage) * 70 / 100);
        return LootBestWorldItem(ctx, item => PortableWeaponScore(item) >= minimum);
    }

    public static AutoTaskResult LootBestWorldItem(AutoContext ctx, Func<Item, bool> desired,
        AutoGoalId targetGoal = AutoGoalId.AcquireWeaponUpgrade)
    {
        Point? target = ResolveWorldTarget(ctx, desired, targetGoal);
        if (target is not Point chestPoint)
            return AutoTaskResult.AlternativeRequired;
        return LootChestAt(ctx, chestPoint, desired);
    }

    private static AutoTaskResult LootChestAt(AutoContext ctx, Point chestPoint,
        Func<Item, bool> desired)
    {

        int chestIndex = Chest.FindChest(chestPoint.X, chestPoint.Y);
        if (chestIndex < 0 || Main.chest[chestIndex] is not Chest chest || Chest.IsLocked(chest.x, chest.y))
        {
            ctx.State.MarkChestReviewed(chestPoint);
            ctx.TaskRuntime.ClearActiveTarget();
            return AutoTaskResult.AlternativeRequired;
        }

        // Re-prove provenance immediately before moving the first item. A target selected on an
        // earlier tick may have been destroyed, replaced by a player, or claimed by a human while
        // the companion was walking toward it.
        if (!AutoChestProvenanceSystem.IsWorldGeneratedChest(chest))
        {
            ctx.State.MarkChestReviewed(chestPoint);
            ctx.TaskRuntime.ClearActiveTarget();
            return AutoTaskResult.AlternativeRequired;
        }

        Vector2 center = new(chest.x * 16f + 16f, chest.y * 16f + 16f);
        if (Vector2.Distance(ctx.Me.Center, center) > InteractRangeTiles * 16f + ctx.Me.width)
        {
            ctx.Navigator.Navigate(ctx.Me, center);
            return AutoTaskResult.Running;
        }

        ctx.Navigator.Idle(ctx.Me);
        bool movedAny = false;
        for (int slot = 0; slot < chest.item.Length; slot++)
        {
            Item source = chest.item[slot];
            if (source is null || source.IsAir || OpportunityValue(ctx.Profile, source) <= 0 &&
                (desired is null || !desired(source)))
                continue;

            Item incoming = source.Clone();
            int before = incoming.stack;
            ctx.Profile.TryAddToBackpack(incoming);
            int moved = before - incoming.stack;
            if (moved <= 0)
                continue;
            source.stack -= moved;
            if (source.stack <= 0)
                source.TurnToAir();
            movedAny = true;
            ctx.TaskRuntime.RecordWorldProgress();
        }

        AutoGearController.OrganizeOwnedGear(ctx.Me, ctx.Profile);
        if (ChestUtility(ctx.Profile, chest) > 0 && !AutoInventory.HasBackpackSpace(ctx.Profile))
            return AutoTaskResult.RetryLater;

        ctx.State.MarkChestReviewed(chestPoint);
        ctx.TaskRuntime.ClearActiveTarget();
        return movedAny ? AutoTaskResult.Succeeded : AutoTaskResult.AlternativeRequired;
    }

    private static Point? ResolveTarget(AutoContext ctx)
    {
        if (ctx.TaskRuntime.TryGetActiveTarget(AutoGoalId.LootChest,
            p => IsWorthwhileChest(ctx, p), out Point active))
            return active;
        if (!TryFindBestChest(ctx, out Point found, out _))
            return null;
        ctx.TaskRuntime.SetActiveTarget(AutoGoalId.LootChest, found);
        return found;
    }

    private static Point? ResolveWorldTarget(AutoContext ctx, Func<Item, bool> desired,
        AutoGoalId targetGoal)
    {
        if (ctx.TaskRuntime.TryGetActiveTarget(targetGoal,
            p => IsWorthwhileChest(ctx, p, desired), out Point active))
            return active;
        if (!TryFindBestChest(ctx, global: true, desired, out Point found, out _))
            return null;
        ctx.TaskRuntime.SetActiveTarget(targetGoal, found);
        return found;
    }

    private static bool TryFindBestChest(AutoContext ctx, out Point best, out int bestUtility)
        => TryFindBestChest(ctx, global: false, null, out best, out bestUtility);

    private static bool TryFindBestChest(AutoContext ctx, bool global, Func<Item, bool> desired,
        out Point best, out int bestUtility)
    {
        best = default;
        bestUtility = 0;
        Point origin = ctx.Me.Center.ToTileCoordinates();
        bool bootstrapSurvey = !global && AutoCampaignController.NeedsPersonalFoundation(ctx);
        int radius = bootstrapSurvey ? BootstrapSearchRadiusTiles : SearchRadiusTiles;
        int radiusSquared = radius * radius;
        for (int i = 0; i < Main.chest.Length; i++)
        {
            Chest chest = Main.chest[i];
            if (chest is null)
                continue;
            Point point = new(chest.x, chest.y);
            int dx = point.X - origin.X;
            int dy = point.Y - origin.Y;
            int distanceSquared = dx * dx + dy * dy;
            if (!global && distanceSquared > radiusSquared || ctx.State.WasChestReviewed(point) ||
                ctx.State.Chest == point || Chest.IsLocked(chest.x, chest.y) ||
                !AutoChestProvenanceSystem.IsWorldGeneratedChest(chest))
                continue;
            int utility = ChestUtility(ctx.Profile, chest, desired);
            if (!global)
                utility -= (int)System.MathF.Sqrt(distanceSquared) / (bootstrapSurvey ? 6 : 3);
            if (utility <= bestUtility)
                continue;
            bestUtility = utility;
            best = point;
        }
        return bestUtility > 0;
    }

    private static bool IsWorthwhileChest(AutoContext ctx, Point point)
        => IsWorthwhileChest(ctx, point, null);

    private static bool IsWorthwhileChest(AutoContext ctx, Point point, Func<Item, bool> desired)
    {
        if (ctx.State.WasChestReviewed(point) || ctx.State.Chest == point)
            return false;
        int index = Chest.FindChest(point.X, point.Y);
        Chest chest = index >= 0 ? Main.chest[index] : null;
        return chest is not null && !Chest.IsLocked(chest.x, chest.y) &&
            AutoChestProvenanceSystem.IsWorldGeneratedChest(chest) &&
            ChestUtility(ctx.Profile, chest, desired) > 0;
    }

    private static int ChestUtility(CompanionProfile profile, Chest chest,
        Func<Item, bool> desired = null)
    {
        int utility = 0;
        foreach (Item item in chest.item)
            if (item is not null && !item.IsAir)
            {
                int value = OpportunityValue(profile, item);
                if (desired is not null && desired(item))
                    value = System.Math.Max(value, 500 + AutoWeaponEvaluator.CombatScore(item));
                utility = System.Math.Max(utility, value);
            }
        return utility;
    }

    public static int OpportunityValue(CompanionProfile profile, Item item)
    {
        if (item is null || item.IsAir)
            return 0;
        if (CompanionSlotRules.IsRegularWeapon(item))
        {
            int score = AutoWeaponEvaluator.CombatScore(item);
            int best = AutoWeaponEvaluator.BestRegularScore(profile);
            return score >= System.Math.Max(10, best * 70 / 100) ? 45 + System.Math.Max(0, score - best) : 0;
        }
        if (item.pick > AutoInventory.BestPickaxe(profile).pick || item.axe > AutoInventory.BestAxe(profile).axe ||
            item.hammer > AutoInventory.BestHammer(profile).hammer)
            return 80;
        if (item.type is ItemID.LifeCrystal or ItemID.ManaCrystal or ItemID.LifeFruit)
            return 120;
        if (AutoWorldSurvivalController.IsFood(item))
            return Main.dontStarveWorld ? 180 : 48;
        if (item.accessory || item.headSlot >= 0 || item.bodySlot >= 0 || item.legSlot >= 0 ||
            item.fishingPole > 0 || item.bait > 0 || item.ammo > 0)
            return 65;
        if (item.type is ItemID.MagicMirror or ItemID.IceMirror or ItemID.RecallPotion || item.potion)
            return 55;
        if (item.createTile >= TileID.Dirt || item.material || item.rare > ItemRarityID.White)
            return 28;
        if (item.type is ItemID.CopperCoin or ItemID.SilverCoin or ItemID.GoldCoin or ItemID.PlatinumCoin)
            return 20;
        return 0;
    }

    private static int PortableWeaponScore(Item item) => System.Math.Max(
        AutoWeaponEvaluator.CombatScore(item), AutoWeaponEvaluator.SummonScore(null, item));
}
