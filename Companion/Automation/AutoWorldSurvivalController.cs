using Terraria;
using Terraria.ID;

namespace MoreAIPlayer.Companion.Automation;

/// <summary>Special-seed survival which cannot be inferred from ordinary health alone. Constant
/// worlds need a legal food route; owned meals are eaten through normal item use, otherwise public
/// world chests and bounded fishing/exploration remain parallel acquisition paths.</summary>
public static class AutoWorldSurvivalController
{
    public static bool NeedsFood(AutoContext ctx) => Main.dontStarveWorld &&
        !HasFoodBuff(ctx.Me);

    public static AutoTaskResult AcquireFood(AutoContext ctx)
    {
        if (!NeedsFood(ctx))
            return AutoTaskResult.Succeeded;
        int slot = AutoInventory.FindInventoryIndex(ctx.Me, IsFood);
        if (slot >= 0)
            return ctx.Actuator.UseItemInSlot(ctx.Instance, ctx.Me, slot, ctx.Me.Center)
                ? AutoTaskResult.Running : AutoTaskResult.RetryLater;

        AutoTaskResult chest = AutoLootController.LootBestWorldItem(ctx, IsFood,
            AutoGoalId.LootChest);
        if (chest != AutoTaskResult.AlternativeRequired)
            return chest;
        if (ctx.Fishing.HasOpportunity(ctx))
            return ctx.Fishing.Fish(ctx);
        return AutoExplorationController.Explore(ctx);
    }

    public static long ProgressSignature(AutoContext ctx) =>
        (HasFoodBuff(ctx.Me) ? 1_000_000_000L : 0L) + CountFood(ctx.Profile);

    public static bool IsFood(Item item) => item is not null && !item.IsAir && item.consumable &&
        item.buffTime > 0 && item.buffType is BuffID.WellFed or BuffID.WellFed2 or BuffID.WellFed3;

    private static bool HasFoodBuff(Player player) => player.HasBuff(BuffID.WellFed) ||
        player.HasBuff(BuffID.WellFed2) || player.HasBuff(BuffID.WellFed3);

    private static int CountFood(CompanionProfile profile)
    {
        int total = 0;
        foreach (Item[] items in new[] { profile.Hotbar, profile.Backpack })
        foreach (Item item in items)
            if (IsFood(item))
                total += item.stack;
        return total;
    }
}
