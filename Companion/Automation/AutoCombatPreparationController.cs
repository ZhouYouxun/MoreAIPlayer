using Terraria;
using Terraria.ID;

namespace MoreAIPlayer.Companion.Automation;

/// <summary>Consumes legally owned combat buffs one at a time before an encounter. This mirrors a
/// real player's arena preparation without prescribing potion farming as a hard progression gate.</summary>
public static class AutoCombatPreparationController
{
    private static readonly int[] GeneralBuffs =
    [
        BuffID.WellFed, BuffID.WellFed2, BuffID.WellFed3,
        BuffID.Ironskin, BuffID.Regeneration, BuffID.Swiftness,
        BuffID.Endurance, BuffID.Lifeforce, BuffID.Wrath, BuffID.Rage
    ];

    public static bool TryUseNextOwnedBuff(AutoContext ctx)
    {
        if (ctx.Me.itemAnimation != 0)
            return false;
        foreach (int buff in GeneralBuffs)
            if (TryUseBuff(ctx, buff))
                return true;

        int[] classBuffs = ctx.State.PreferredClass switch
        {
            AutoCombatClass.Ranged => [BuffID.Archery, BuffID.AmmoReservation],
            AutoCombatClass.Magic => [BuffID.MagicPower, BuffID.ManaRegeneration],
            AutoCombatClass.Summoner => [BuffID.Summoning],
            _ => []
        };
        foreach (int buff in classBuffs)
            if (TryUseBuff(ctx, buff))
                return true;
        return false;
    }

    public static int OwnedBuffStackCount(CompanionProfile profile)
    {
        int total = 0;
        foreach (Item[] items in new[] { profile.Hotbar, profile.Backpack })
        foreach (Item item in items)
            if (item is not null && !item.IsAir && item.consumable && item.buffType > 0 &&
                item.buffTime > 0)
                total += item.stack;
        return total;
    }

    private static bool TryUseBuff(AutoContext ctx, int buffType)
    {
        if (ctx.Me.HasBuff(buffType))
            return false;
        int slot = AutoInventory.FindInventoryIndex(ctx.Me, item =>
            item.consumable && item.buffType == buffType && item.buffTime > 0);
        return slot >= 0 && ctx.Actuator.UseItemInSlot(ctx.Instance, ctx.Me, slot, ctx.Me.Center);
    }
}
