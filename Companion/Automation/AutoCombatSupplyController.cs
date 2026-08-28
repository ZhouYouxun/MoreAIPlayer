using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;

namespace MoreAIPlayer.Companion.Automation;

/// <summary>Real-ammunition sustain for the phone's best ranged option. It never grants the free
/// default ammo ordinary companions receive: arrows are crafted or bought, bullets come from the
/// Arms Dealer, and rockets come from the Cyborg, all with real housing and coin checks.</summary>
public static class AutoCombatSupplyController
{
    private const int DesiredStock = 250;
    private const int LowStock = 45;

    public static bool NeedsRestock(AutoContext ctx) =>
        TryFindAmmoWeapon(ctx, out Item weapon) && CountAmmoClass(ctx.Profile, weapon.useAmmo) < LowStock &&
        HasAvailableSupplyRoute(ctx.Profile, weapon.useAmmo);

    public static AutoTaskResult Acquire(AutoContext ctx)
    {
        if (!TryFindAmmoWeapon(ctx, out Item weapon))
            return AutoTaskResult.Succeeded;
        int ammoClass = weapon.useAmmo;
        int held = CountAmmoClass(ctx.Profile, ammoClass);
        if (held >= DesiredStock)
            return AutoTaskResult.Succeeded;

        if (ammoClass == AmmoID.Arrow &&
            AutoInventory.Count(ctx.Profile, ItemID.Wood) >
                AutoResourceBudget.OperationalReserve(ctx.State) &&
            AutoCraftingService.TryCraftUntil(ctx.Instance, ctx.Profile,
                ItemID.WoodenArrow, DesiredStock))
        {
            ctx.TaskRuntime.RecordWorldProgress();
            return AutoTaskResult.Running;
        }

        int npcType;
        int ammoItem;
        if (ammoClass == AmmoID.Bullet)
        {
            npcType = NPCID.ArmsDealer;
            ammoItem = ItemID.MusketBall;
        }
        else if (ammoClass == AmmoID.Arrow)
        {
            npcType = NPCID.Merchant;
            ammoItem = ItemID.WoodenArrow;
        }
        else if (ammoClass == AmmoID.Rocket)
        {
            npcType = NPCID.Cyborg;
            ammoItem = ItemID.RocketI;
        }
        else
            return AutoTaskResult.AlternativeRequired;

        if (!AutoTownPurchaseService.TryFindHousedNpc(npcType, out NPC vendor))
            return AutoTaskResult.AlternativeRequired;
        if (Vector2.DistanceSquared(ctx.Me.Center, vendor.Center) > 6f * 16f * 6f * 16f)
            return AutoWorldTravelController.TravelTo(ctx, vendor.Center, 5f);

        int batch = System.Math.Min(200, DesiredStock - held);
        int unitPrice = UnitPrice(ammoItem);
        while (batch > 0)
        {
            if (AutoTownPurchaseService.TryPurchase(ctx.Profile, ammoItem, batch, unitPrice))
            {
                ctx.TaskRuntime.RecordWorldProgress();
                return AutoTaskResult.Running;
            }
            batch /= 2;
        }
        return AutoTaskResult.AlternativeRequired;
    }

    public static int CountAllUsefulAmmo(CompanionProfile profile)
    {
        int total = 0;
        foreach (Item[] items in new[] { profile.Ammo, profile.Backpack, profile.Hotbar })
        foreach (Item item in items)
            if (!item.IsAir && item.ammo > AmmoID.None)
                total += item.stack;
        return total;
    }

    private static bool TryFindAmmoWeapon(AutoContext ctx, out Item weapon)
    {
        weapon = null;
        int best = 0;
        int fallback = 0;
        for (int slot = 0; slot < ctx.Me.inventory.Length && slot < 58; slot++)
        {
            Item candidate = ctx.Me.inventory[slot];
            if (candidate.IsAir)
                continue;
            int score = AutoWeaponEvaluator.CombatScore(candidate);
            if (candidate.useAmmo <= AmmoID.None)
            {
                fallback = System.Math.Max(fallback, AutoWeaponEvaluator.CombatScore(ctx.Me, candidate));
                continue;
            }
            if (score <= best)
                continue;
            best = score;
            weapon = candidate;
        }
        if (weapon is null || best * 100 < fallback * 118)
        {
            weapon = null;
            return false;
        }
        return true;
    }

    private static bool HasAvailableSupplyRoute(CompanionProfile profile, int ammoClass)
    {
        if (ammoClass == AmmoID.Arrow && AutoInventory.Count(profile, ItemID.Wood) >
                AutoResourceBudget.OperationalReserve(profile.AutoState) &&
            AutoInventory.Count(profile, ItemID.StoneBlock) > 0)
            return true;
        int npc = ammoClass == AmmoID.Bullet ? NPCID.ArmsDealer :
            ammoClass == AmmoID.Arrow ? NPCID.Merchant :
            ammoClass == AmmoID.Rocket ? NPCID.Cyborg : NPCID.None;
        int itemType = ammoClass == AmmoID.Bullet ? ItemID.MusketBall :
            ammoClass == AmmoID.Arrow ? ItemID.WoodenArrow :
            ammoClass == AmmoID.Rocket ? ItemID.RocketI : ItemID.None;
        return npc > NPCID.None && itemType > ItemID.None &&
            AutoTownPurchaseService.TryFindHousedNpc(npc, out _) &&
            AutoTownPurchaseService.CanAfford(profile, UnitPrice(itemType));
    }

    private static int CountAmmoClass(CompanionProfile profile, int ammoClass)
    {
        int count = 0;
        foreach (Item[] items in new[] { profile.Ammo, profile.Backpack, profile.Hotbar })
        foreach (Item item in items)
            if (!item.IsAir && item.ammo == ammoClass)
                count += item.stack;
        return count;
    }

    private static int UnitPrice(int ammoItem) => ammoItem == ItemID.MusketBall
        ? Item.buyPrice(copper: 7) : ammoItem == ItemID.WoodenArrow
            ? Item.buyPrice(copper: 5) : ammoItem == ItemID.RocketI
                ? Item.buyPrice(silver: 1)
                : System.Math.Max(1, ContentSamples.ItemsByType[ammoItem].value);
}
