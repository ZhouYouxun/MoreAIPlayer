using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;

namespace MoreAIPlayer.Companion.Automation;

/// <summary>
/// Owns the first real combat capability. Before deliberately hunting an enemy, a fresh
/// automation profile must have both a close-range weapon and a bow-class weapon with a useful
/// arrow reserve. Stronger player-supplied equivalents satisfy the same capability immediately;
/// otherwise the deterministic fallback is a Wooden Sword, Wooden Bow and Wooden Arrows.
/// </summary>
public static class AutoStarterArsenalController
{
    public const int DesiredArrowStock = 100;

    // Vanilla recipe costs are planning estimates only. AutoCraftingService remains authoritative
    // if another mod changes a recipe: it asks Recipe.FindRecipes and consumes the real inputs.
    private const int WoodenSwordWood = 7;
    private const int WoodenBowWood = 10;
    private const int WoodenArrowBatchSize = 25;
    private const int WoodenArrowWoodPerBatch = 1;
    private const int WoodenArrowStonePerBatch = 1;
    private const float WorkBenchRangeTiles = 3f;

    public static bool HasStarterArsenal(CompanionProfile profile) =>
        HasCloseRangeWeapon(profile) && HasBow(profile) && CountArrows(profile) >= DesiredArrowStock;

    /// <summary>Once real metal gear supersedes the opening kit, the obsolete wooden fallback is
    /// no longer a permanent collectible. This prevents later inventory cleanup from causing a
    /// pointless craft/drop loop while still enforcing the complete kit during the fragile start.</summary>
    public static bool RequirementSatisfied(CompanionProfile profile) =>
        AutoGearController.HasBasicMetalLoadout(profile) || HasStarterArsenal(profile);

    public static int MissingWoodCost(CompanionProfile profile)
    {
        int cost = 0;
        if (!HasCloseRangeWeapon(profile))
            cost += WoodenSwordWood;
        if (!HasBow(profile))
            cost += WoodenBowWood;
        cost += MissingArrowBatches(profile) * WoodenArrowWoodPerBatch;
        return cost;
    }

    public static int MissingStoneCost(CompanionProfile profile) =>
        MissingArrowBatches(profile) * WoodenArrowStonePerBatch;

    /// <summary>Crafts every currently legal missing component in this update. There is no fake
    /// crafting UI, thought pose or per-recipe delay, and the transaction does not claim movement
    /// input; it may therefore finish while the body still has ordinary velocity.</summary>
    public static AutoTaskResult Ensure(AutoContext ctx)
    {
        AutoGearController.OrganizeOwnedGear(ctx.Me, ctx.Profile);
        if (RequirementSatisfied(ctx.Profile))
            return AutoTaskResult.Succeeded;

        if (!TryGetStandingWorkBench(ctx.State.WorkBench, out Point bench))
            return AutoTaskResult.RetryLater;

        Vector2 benchCenter = new(bench.X * 16f + 8f, bench.Y * 16f + 8f);
        if (Vector2.Distance(ctx.Me.Center, benchCenter) > WorkBenchRangeTiles * 16f)
        {
            ctx.Navigator.Navigate(ctx.Me, benchCenter);
            return AutoTaskResult.Running;
        }

        bool crafted = false;
        if (!HasCloseRangeWeapon(ctx.Profile))
            crafted |= AutoCraftingService.TryCraft(ctx.Instance, ctx.Profile, ItemID.WoodenSword);
        if (!HasBow(ctx.Profile))
            crafted |= AutoCraftingService.TryCraft(ctx.Instance, ctx.Profile, ItemID.WoodenBow);

        int arrowTarget = AutoInventory.Count(ctx.Profile, ItemID.WoodenArrow) +
            System.Math.Max(0, DesiredArrowStock - CountArrows(ctx.Profile));
        if (CountArrows(ctx.Profile) < DesiredArrowStock)
        {
            crafted |= AutoCraftingService.TryCraftUntil(ctx.Instance, ctx.Profile,
                ItemID.WoodenArrow, arrowTarget);
        }

        if (crafted)
        {
            AutoGearController.OrganizeOwnedGear(ctx.Me, ctx.Profile);
            ctx.TaskRuntime.RecordWorldProgress();
            return RequirementSatisfied(ctx.Profile)
                ? AutoTaskResult.Succeeded : AutoTaskResult.Running;
        }

        return AutoCraftingService.IsAvailable
            ? AutoTaskResult.AlternativeRequired : AutoTaskResult.Faulted;
    }

    private static int MissingArrowBatches(CompanionProfile profile) =>
        (System.Math.Max(0, DesiredArrowStock - CountArrows(profile)) +
            WoodenArrowBatchSize - 1) / WoodenArrowBatchSize;

    private static bool HasCloseRangeWeapon(CompanionProfile profile) =>
        AnyOwned(profile, item => CompanionSlotRules.IsRegularWeapon(item) &&
            item.damage > 0 && !item.noMelee && item.pick <= 0 && item.axe <= 0 &&
            item.hammer <= 0 && item.useAmmo == AmmoID.None && item.mana <= 0);

    private static bool HasBow(CompanionProfile profile) =>
        AnyOwned(profile, item => CompanionSlotRules.IsRegularWeapon(item) &&
            item.damage > 0 && item.useAmmo == AmmoID.Arrow);

    private static int CountArrows(CompanionProfile profile)
    {
        int total = 0;
        Count(profile.Hotbar);
        Count(profile.Backpack);
        Count(profile.Ammo);
        return total;

        void Count(Item[] items)
        {
            foreach (Item item in items)
                if (item is not null && !item.IsAir && item.ammo == AmmoID.Arrow)
                    total += item.stack;
        }
    }

    private static bool AnyOwned(CompanionProfile profile, System.Func<Item, bool> predicate)
    {
        foreach (Item[] inventory in new[] { profile.Hotbar, profile.Backpack })
        {
            foreach (Item item in inventory)
                if (item is not null && !item.IsAir && predicate(item))
                    return true;
        }
        return false;
    }

    private static bool TryGetStandingWorkBench(Point? recorded, out Point point)
    {
        if (recorded is Point candidate && WorldGen.InWorld(candidate.X, candidate.Y, 2) &&
            Main.tile[candidate.X, candidate.Y].HasTile &&
            Main.tile[candidate.X, candidate.Y].TileType == TileID.WorkBenches)
        {
            point = candidate;
            return true;
        }
        point = default;
        return false;
    }
}
