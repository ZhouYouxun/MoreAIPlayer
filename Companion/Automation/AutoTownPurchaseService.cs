using Terraria;
using Terraria.ID;

namespace MoreAIPlayer.Companion.Automation;

/// <summary>Atomic singleplayer town transaction shared by explosives and ammunition. It snapshots
/// the three coin-bearing inventories, pays real money, rolls back on any insertion failure and
/// never pretends a homeless/missing NPC shop is available.</summary>
public static class AutoTownPurchaseService
{
    public static bool TryFindHousedNpc(int npcType, out NPC found)
    {
        foreach (NPC npc in Main.ActiveNPCs)
        {
            if (npc.active && npc.type == npcType && npc.townNPC && !npc.homeless)
            {
                found = npc;
                return true;
            }
        }
        found = null;
        return false;
    }

    public static bool CanAfford(CompanionProfile profile, int copper) =>
        CountCoins(profile) >= copper;

    public static bool TryPurchase(CompanionProfile profile, int itemType, int stack,
        int unitPrice)
    {
        if (stack <= 0 || unitPrice < 0)
            return false;
        long longPrice = (long)unitPrice * stack;
        if (longPrice > int.MaxValue)
            return false;
        Item purchase = new(itemType, stack);
        int price = (int)longPrice;
        if (!AutoInventory.CanFit(profile, purchase) || CountCoins(profile) < price)
            return false;

        Item[] hotbar = Clone(profile.Hotbar);
        Item[] backpack = Clone(profile.Backpack);
        Item[] ammo = Clone(profile.Ammo);
        if (TrySpendCoins(profile, price) && AutoInventory.Give(profile, purchase))
            return true;
        Restore(profile, hotbar, backpack, ammo);
        return false;
    }

    private static bool TrySpendCoins(CompanionProfile profile, int copper)
    {
        long total = CountCoins(profile);
        if (total < copper)
            return false;
        ClearCoins(profile);
        return GiveCoins(profile, total - copper);
    }

    private static Item[] Clone(Item[] source)
    {
        Item[] copy = new Item[source.Length];
        for (int i = 0; i < source.Length; i++)
            copy[i] = source[i].Clone();
        return copy;
    }

    private static void Restore(CompanionProfile profile, Item[] hotbar, Item[] backpack,
        Item[] ammo)
    {
        System.Array.Copy(hotbar, profile.Hotbar, hotbar.Length);
        System.Array.Copy(backpack, profile.Backpack, backpack.Length);
        System.Array.Copy(ammo, profile.Ammo, ammo.Length);
    }

    private static long CountCoins(CompanionProfile profile)
    {
        long total = 0;
        foreach (Item[] items in new[] { profile.Hotbar, profile.Backpack, profile.Ammo })
        foreach (Item item in items)
            total += item.type switch
            {
                ItemID.CopperCoin => item.stack,
                ItemID.SilverCoin => item.stack * 100L,
                ItemID.GoldCoin => item.stack * 10000L,
                ItemID.PlatinumCoin => item.stack * 1000000L,
                _ => 0L
            };
        return total;
    }

    private static void ClearCoins(CompanionProfile profile)
    {
        foreach (Item[] items in new[] { profile.Hotbar, profile.Backpack, profile.Ammo })
        foreach (Item item in items)
            if (item.type is ItemID.CopperCoin or ItemID.SilverCoin or
                ItemID.GoldCoin or ItemID.PlatinumCoin)
                item.TurnToAir();
    }

    private static bool GiveCoins(CompanionProfile profile, long copper)
    {
        foreach ((int type, int value) in new[]
        {
            (ItemID.PlatinumCoin, 1000000), (ItemID.GoldCoin, 10000),
            (ItemID.SilverCoin, 100), (ItemID.CopperCoin, 1)
        })
        {
            long stack = copper / value;
            copper %= value;
            int maxStack = ContentSamples.ItemsByType[type].maxStack;
            while (stack > 0)
            {
                int batch = (int)System.Math.Min(stack, maxStack);
                if (!AutoInventory.Give(profile, new Item(type, batch)))
                    return false;
                stack -= batch;
            }
        }
        return true;
    }
}
