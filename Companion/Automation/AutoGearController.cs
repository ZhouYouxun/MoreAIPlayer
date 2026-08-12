using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;

namespace MoreAIPlayer.Companion.Automation;

/// <summary>
/// Converts mined ore into the first self-earned loadout. Crafting always goes through
/// <see cref="AutoCraftingService"/>; this controller only chooses recipes and moves items between
/// the companion's own backpack/equipment slots, the same inventory organization a player does.
/// </summary>
public static class AutoGearController
{
    private const int BetterThanCopperPickPower = 40;
    private const int OreBatch = 60;

    private static readonly int[] Picks =
    [
        ItemID.PlatinumPickaxe, ItemID.GoldPickaxe, ItemID.TungstenPickaxe, ItemID.SilverPickaxe,
        ItemID.LeadPickaxe, ItemID.IronPickaxe
    ];

    private static readonly int[] Swords =
    [
        ItemID.PlatinumBroadsword, ItemID.GoldBroadsword, ItemID.TungstenBroadsword,
        ItemID.SilverBroadsword, ItemID.LeadBroadsword, ItemID.IronBroadsword,
        ItemID.TinBroadsword, ItemID.CopperBroadsword
    ];

    private static readonly int[][] ArmourBySlot =
    [
        [ItemID.PlatinumHelmet, ItemID.GoldHelmet, ItemID.TungstenHelmet, ItemID.SilverHelmet,
            ItemID.LeadHelmet, ItemID.IronHelmet, ItemID.TinHelmet, ItemID.CopperHelmet],
        [ItemID.PlatinumChainmail, ItemID.GoldChainmail, ItemID.TungstenChainmail, ItemID.SilverChainmail,
            ItemID.LeadChainmail, ItemID.IronChainmail, ItemID.TinChainmail, ItemID.CopperChainmail],
        [ItemID.PlatinumGreaves, ItemID.GoldGreaves, ItemID.TungstenGreaves, ItemID.SilverGreaves,
            ItemID.LeadGreaves, ItemID.IronGreaves, ItemID.TinGreaves, ItemID.CopperGreaves]
    ];

    public static bool HasBasicMetalLoadout(CompanionProfile profile)
    {
        bool pick = AutoInventory.BestPickaxe(profile).pick >= BetterThanCopperPickPower;
        bool weapon = AutoRiskPolicy.HasAdequateEarlyWeapon(profile);
        bool armour = !profile.Armor[0].IsAir && !profile.Armor[1].IsAir && !profile.Armor[2].IsAir;
        return pick && weapon && armour;
    }

    public static AutoTaskResult EnsureBasicMetalLoadout(AutoContext ctx)
    {
        EquipCraftedItems(ctx.Profile);
        if (HasBasicMetalLoadout(ctx.Profile))
            return AutoTaskResult.Succeeded;

        // Smelt every real ore stack before deciding that another mining trip is necessary.
        if (AutoResourceController.CountBasicOre(ctx.Profile) > 0)
        {
            AutoTaskResult smelting = AutoStationController.SmeltOre(
                ctx, ctx.State.Furnace, AutoProgressionPlanner.SmeltableBarItems);
            if (smelting != AutoTaskResult.RetryLater)
                return smelting;
            // One or two leftover ore can be below every bar recipe's batch size. They are not a
            // reason to suppress crafting from the bars already earned.
        }

        if (ctx.State.Anvil is Point anvil)
        {
            if (!Near(ctx.Me, anvil))
            {
                ctx.Navigator.Navigate(ctx.Me, TileCenter(anvil));
                return AutoTaskResult.Running;
            }

            if (TryCraftNextMissing(ctx))
                return AutoTaskResult.Running;
            if (!AutoCraftingService.IsAvailable)
                return AutoTaskResult.Faulted;
        }

        // No currently available bar recipe could close the next loadout gap. Mine another real
        // batch instead of spinning forever beside the anvil with incompatible mixed bars.
        return AutoResourceController.MineBasicOre(ctx, OreBatch);
    }

    private static bool TryCraftNextMissing(AutoContext ctx)
    {
        if (AutoInventory.BestPickaxe(ctx.Profile).pick < BetterThanCopperPickPower && TryAny(ctx, Picks))
            return true;
        // Any equivalent world/mod drop closes this gap. Only craft a prescribed fallback when the
        // actual held loadout does not already meet the capability threshold.
        if (!AutoRiskPolicy.HasAdequateEarlyWeapon(ctx.Profile) && TryAny(ctx, Swords))
            return true;

        for (int slot = 0; slot < ArmourBySlot.Length; slot++)
        {
            if (ctx.Profile.Armor[slot].IsAir && TryAny(ctx, ArmourBySlot[slot]))
                return true;
        }
        return false;
    }

    private static bool TryAny(AutoContext ctx, int[] itemTypes)
    {
        foreach (int type in itemTypes)
        {
            if (AutoCraftingService.TryCraft(ctx.Instance, ctx.Profile, type))
                return true;
        }
        return false;
    }

    private static void EquipCraftedItems(CompanionProfile profile)
    {
        MoveBestBackpackItem(profile, item => item.pick >= BetterThanCopperPickPower,
            CompanionProfile.PickSlot, compare: item => item.pick);
        MoveBestBackpackItem(profile, IsWeapon, CompanionProfile.FirstCombatSlot,
            compare: AutoRiskPolicy.WeaponCapability);

        for (int slot = 0; slot < 3; slot++)
        {
            int armourSlot = slot;
            MoveBestArmour(profile, armourSlot);
        }
    }

    private static void MoveBestBackpackItem(CompanionProfile profile, System.Func<Item, bool> match,
        int hotbarSlot, System.Func<Item, int> compare)
    {
        int best = -1;
        int score = compare(profile.Hotbar[hotbarSlot]);
        for (int i = 0; i < profile.Backpack.Length; i++)
        {
            Item item = profile.Backpack[i];
            if (item.IsAir || !match(item) || compare(item) <= score)
                continue;
            score = compare(item);
            best = i;
        }
        if (best < 0)
            return;

        (profile.Hotbar[hotbarSlot], profile.Backpack[best]) =
            (profile.Backpack[best], profile.Hotbar[hotbarSlot]);
    }

    private static void MoveBestArmour(CompanionProfile profile, int slot)
    {
        int best = -1;
        int defense = profile.Armor[slot].defense;
        for (int i = 0; i < profile.Backpack.Length; i++)
        {
            Item item = profile.Backpack[i];
            if (item.IsAir || !FitsArmourSlot(item, slot) || item.defense <= defense)
                continue;
            defense = item.defense;
            best = i;
        }
        if (best >= 0)
            (profile.Armor[slot], profile.Backpack[best]) = (profile.Backpack[best], profile.Armor[slot]);
    }

    private static bool IsWeapon(Item item) => item is not null && !item.IsAir && item.damage > 0 &&
        item.pick <= 0 && item.axe <= 0 && item.hammer <= 0;

    private static bool FitsArmourSlot(Item item, int slot) => slot switch
    {
        0 => item.headSlot >= 0,
        1 => item.bodySlot >= 0,
        2 => item.legSlot >= 0,
        _ => false
    };

    private static bool Near(Player me, Point tile) =>
        Vector2.DistanceSquared(me.Center, TileCenter(tile)) <= 48f * 48f;

    private static Vector2 TileCenter(Point tile) => new(tile.X * 16f + 8f, tile.Y * 16f + 8f);
}
