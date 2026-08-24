using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace MoreAIPlayer.Companion.Automation;

/// <summary>Opens one legally owned boss/crate bag per tick inside the synthetic-player context.
/// Expert and Master worlds therefore expose the same progression drops as Classic worlds.</summary>
public static class AutoBagController
{
    public static bool OpenOne(CompanionInstance instance, CompanionProfile profile)
    {
        Item bag = null;
        foreach (Item[] items in new[] { profile.Hotbar, profile.Backpack })
        {
            foreach (Item item in items)
            {
                if (item.IsAir || item.type <= ItemID.None || item.type >= ItemID.Sets.OpenableBag.Length ||
                    !ItemID.Sets.OpenableBag[item.type] || !ItemLoader.CanRightClick(item))
                    continue;
                bag = item;
                break;
            }
            if (bag is not null)
                break;
        }
        if (bag is null)
            return false;

        bool opened = false;
        // Bag right-click hooks are isolated from the brain and crafting circuits. A broken
        // modded bag must not disable unrelated progression work for the rest of the session.
        CompanionFaultGuard.RunCritical(CompanionFaultGuard.AutomationBagOpeningLane, () =>
        {
            using VirtualPlayerExecutionContext.Scope scope =
                VirtualPlayerExecutionContext.Enter(instance.Entity, instance.Entity.Center, false, false);
            ItemLoader.RightClick(bag, instance.Entity);
            opened = true;
        });
        return opened;
    }
}
