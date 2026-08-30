using System;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace MoreAIPlayer.Companion.Automation;

/// <summary>
/// Inventory queries and legal pickup for the automation companion. Everything operates on the
/// companion's own profile arrays (which alias its live entity inventory), so counts, deposits and
/// pickups all flow into the saved profile without touching the owner's inventory.
/// </summary>
public static class AutoInventory
{
    /// <summary>Total stack of an item type across the companion's hotbar, backpack and ammo.</summary>
    public static int Count(CompanionProfile profile, int type)
    {
        int total = 0;
        total += CountIn(profile.Hotbar, type);
        total += CountIn(profile.Backpack, type);
        total += CountIn(profile.Ammo, type);
        return total;
    }

    private static int CountIn(Item[] items, int type)
    {
        int total = 0;
        foreach (Item item in items)
        {
            if (!item.IsAir && item.type == type)
                total += item.stack;
        }
        return total;
    }

    /// <summary>Whether the companion holds at least <paramref name="amount"/> of a type.</summary>
    public static bool Has(CompanionProfile profile, int type, int amount = 1) => Count(profile, type) >= amount;

    /// <summary>
    /// Entity inventory index (0–57) of the first item matching <paramref name="predicate"/>, or -1.
    /// Any index works as a selectable slot — the companion selects backpack indices directly, the
    /// same way the ordinary brain selects its net at 52 and rod at 53.
    /// </summary>
    public static int FindInventoryIndex(Player me, Func<Item, bool> predicate)
    {
        for (int i = 0; i < me.inventory.Length && i < 58; i++)
        {
            Item item = me.inventory[i];
            if (!item.IsAir && predicate(item))
                return i;
        }
        return -1;
    }

    /// <summary>Finds a live inventory slot and, when same-frame crafting replaced an empty
    /// backpack Item reference, immediately rebinds that one profile slot into the synthetic
    /// Player. This removes the otherwise unnecessary next-tick bind before physical placement.</summary>
    public static int FindAndBindInventoryIndex(Player me, CompanionProfile profile,
        Func<Item, bool> predicate)
    {
        int existing = FindInventoryIndex(me, predicate);
        if (existing >= 0)
            return existing;
        for (int backpackSlot = 0; backpackSlot < profile.Backpack.Length; backpackSlot++)
        {
            Item item = profile.Backpack[backpackSlot];
            if (item.IsAir || !predicate(item))
                continue;
            int inventoryIndex = CompanionInstance.BackpackToInventoryIndex(backpackSlot);
            me.inventory[inventoryIndex] = item;
            return inventoryIndex;
        }
        return -1;
    }

    /// <summary>Adds an item to the companion's backpack (stacking first). Returns false if full.</summary>
    public static bool Give(CompanionProfile profile, Item item) => profile.TryAddToBackpack(item);

    /// <summary>
    /// Picks up one nearby world item the companion is allowed to take, moving as much as fits into
    /// its backpack and shrinking/removing the world item. Respects mod CanPickup hooks. Returns the
    /// item type collected, or -1 if nothing was taken this call.
    ///
    /// This is the auto-only collector (§2.2/§7.3): vanilla only lets <c>Main.myPlayer</c> grab
    /// items, so ordinary companions never auto-collect and this never runs for them.
    /// <paramref name="wanted"/> filters to items the current task actually wants; pass null to take
    /// any collectable item in range (used right after the companion drops something itself).
    /// </summary>
    public static int TryCollectNearby(CompanionInstance instance, CompanionProfile profile,
        float rangeTiles, Func<Item, bool> wanted)
    {
        Player me = instance.Entity;
        float range = rangeTiles * 16f;
        Rectangle reach = new(
            (int)(me.position.X - range), (int)(me.position.Y - range),
            (int)(me.width + range * 2f), (int)(me.height + range * 2f));

        for (int j = 0; j < Main.maxItems; j++)
        {
            Item ground = Main.item[j];
            if (!ground.active || ground.IsAir || ground.noGrabDelay > 0)
                continue;
            if (!reach.Intersects(ground.Hitbox))
                continue;
            if (wanted is not null && !wanted(ground))
                continue;
            if (!ItemLoader.CanPickup(ground, me))
                continue;

            Item incoming = ground.Clone();
            int before = incoming.stack;
            profile.TryAddToBackpack(incoming);
            int added = before - incoming.stack;
            if (added <= 0)
                continue;

            int type = ground.type;
            ground.stack -= added;
            if (ground.stack <= 0)
                ground.TurnToAir();
            return type;
        }
        return -1;
    }

    /// <summary>Whether the companion has an empty backpack slot (room to collect more).</summary>
    public static bool HasBackpackSpace(CompanionProfile profile)
    {
        foreach (Item item in profile.Backpack)
        {
            if (item.IsAir)
                return true;
        }
        return false;
    }

    /// <summary>Whether at least one item of any requested type can enter the backpack. Unlike
    /// <see cref="HasBackpackSpace"/>, this recognises a partial matching stack, so a backpack with
    /// no empty slots can still keep mining/chopping into an existing resource stack.</summary>
    public static bool HasBackpackCapacityFor(CompanionProfile profile, params int[] itemTypes)
    {
        if (itemTypes is null || itemTypes.Length == 0)
            return HasBackpackSpace(profile);

        foreach (Item stored in profile.Backpack)
        {
            if (stored.IsAir)
                return true;
            if (stored.stack >= stored.maxStack)
                continue;
            foreach (int type in itemTypes)
                if (stored.type == type)
                    return true;
        }
        return false;
    }

    /// <summary>Checks whether the whole incoming stack can be stored without mutating either the
    /// backpack or the source item. Crafting uses this before consuming ingredients.</summary>
    public static bool CanFit(CompanionProfile profile, Item incoming)
    {
        if (incoming is null || incoming.IsAir)
            return true;

        int remaining = incoming.stack;
        foreach (Item stored in profile.Backpack)
        {
            if (stored.IsAir || stored.type != incoming.type || stored.prefix != incoming.prefix ||
                stored.stack >= stored.maxStack)
                continue;
            remaining -= stored.maxStack - stored.stack;
            if (remaining <= 0)
                return true;
        }

        foreach (Item stored in profile.Backpack)
        {
            if (!stored.IsAir)
                continue;
            remaining -= incoming.maxStack;
            if (remaining <= 0)
                return true;
        }
        return false;
    }

    /// <summary>A pickaxe in the companion's inventory with the highest pick power, or air.</summary>
    public static Item BestPickaxe(CompanionProfile profile) => Best(profile, item => item.pick);

    /// <summary>An axe in the companion's inventory with the highest axe power, or air.</summary>
    public static Item BestAxe(CompanionProfile profile) => Best(profile, item => item.axe);

    /// <summary>A hammer in the companion's inventory with the highest hammer power, or air.</summary>
    public static Item BestHammer(CompanionProfile profile) => Best(profile, item => item.hammer);

    /// <summary>Entity inventory index (0–57) of the highest-power tool for the given power
    /// selector (e.g. <c>it =&gt; it.pick</c>), or -1. Any index is directly selectable.</summary>
    public static int BestToolIndex(Player me, Func<Item, int> power)
    {
        int bestIndex = -1;
        int bestPower = 0;
        for (int i = 0; i < me.inventory.Length && i < 58; i++)
        {
            Item item = me.inventory[i];
            if (item.IsAir)
                continue;
            int p = power(item);
            if (p > bestPower)
            {
                bestPower = p;
                bestIndex = i;
            }
        }
        return bestIndex;
    }

    private static Item Best(CompanionProfile profile, Func<Item, int> power)
    {
        Item best = new();
        int bestPower = 0;
        void Scan(Item[] items)
        {
            foreach (Item item in items)
            {
                if (item.IsAir)
                    continue;
                int p = power(item);
                if (p > bestPower)
                {
                    bestPower = p;
                    best = item;
                }
            }
        }
        Scan(profile.Hotbar);
        Scan(profile.Backpack);
        return best;
    }
}
