using System;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;

namespace MoreAIPlayer.Companion.Automation;

/// <summary>
/// The DepositSurplus branch (design §4.4.1): when the backpack fills, the companion no longer spins
/// on an infinite RetryLater. It adopts or builds its own chest and moves surplus raw materials into
/// it, protecting task-critical items (tools, weapons, accessories, ammo, a torch/light supply and a
/// keep-stock of each crafting material). If it cannot build storage yet, it returns RetryLater and
/// the watchdog escalates it into an honest Blocked state rather than a silent stall.
///
/// Deposits write directly into the singleplayer-authoritative chest array — the same thing vanilla
/// does when the player drops an item into an open chest — so no packet or open-UI is needed.
/// </summary>
public static class AutoStorageController
{
    private const int ChestSearchRadius = 16;
    private const float NearChestTiles = 2.5f;

    // Keep-stock per crafting material; only the excess above this is deposited. Junk (anything not
    // listed and not essential) has a keep of zero and is deposited in full.
    private static int KeepThreshold(int type, AutoCompanionState state)
    {
        AutoCampaignStage stage = state?.CampaignStage ?? AutoCampaignStage.Foundation;
        return type switch
        {
        ItemID.Wood => AutoResourceBudget.StorageWoodKeep(state),
        ItemID.StoneBlock => 200,
        ItemID.CopperOre or ItemID.TinOre or ItemID.IronOre or ItemID.LeadOre or
        ItemID.SilverOre or ItemID.TungstenOre or ItemID.GoldOre or ItemID.PlatinumOre
            when stage == AutoCampaignStage.Foundation => 100,
        ItemID.CopperBar or ItemID.TinBar or ItemID.IronBar or ItemID.LeadBar or
        ItemID.SilverBar or ItemID.TungstenBar or ItemID.GoldBar or ItemID.PlatinumBar
            when stage == AutoCampaignStage.Foundation => 50,
        ItemID.DemoniteOre or ItemID.CrimtaneOre or ItemID.DemoniteBar or ItemID.CrimtaneBar or
        ItemID.ShadowScale or ItemID.TissueSample when stage <= AutoCampaignStage.WallOfFlesh => 100,
        ItemID.Hellstone or ItemID.Obsidian when stage == AutoCampaignStage.WallOfFlesh => 240,
        ItemID.HellstoneBar when stage == AutoCampaignStage.WallOfFlesh => 65,
        ItemID.CobaltOre or ItemID.PalladiumOre or ItemID.MythrilOre or ItemID.OrichalcumOre or
        ItemID.AdamantiteOre or ItemID.TitaniumOre
            when stage is >= AutoCampaignStage.MechanicalBosses and <= AutoCampaignStage.Plantera => 240,
        ItemID.CobaltBar or ItemID.PalladiumBar or ItemID.MythrilBar or ItemID.OrichalcumBar or
        ItemID.AdamantiteBar or ItemID.TitaniumBar
            when stage is >= AutoCampaignStage.MechanicalBosses and <= AutoCampaignStage.Plantera => 80,
        ItemID.HallowedBar when stage is >= AutoCampaignStage.MechanicalBosses and < AutoCampaignStage.Completed => 80,
        ItemID.ChlorophyteOre when stage is >= AutoCampaignStage.Plantera and < AutoCampaignStage.Completed => 240,
        ItemID.ChlorophyteBar when stage is >= AutoCampaignStage.Plantera and < AutoCampaignStage.Completed => 80,
        ItemID.SoulofMight or ItemID.SoulofSight or ItemID.SoulofFright or
        ItemID.SoulofLight or ItemID.SoulofNight or ItemID.SoulofFlight
            when Main.hardMode && stage < AutoCampaignStage.Completed => 99,
        ItemID.FragmentSolar or ItemID.FragmentVortex or ItemID.FragmentNebula or
        ItemID.FragmentStardust when stage < AutoCampaignStage.Completed => 99,
        ItemID.Gel => 100,
        _ => 0
        };
    }

    public static AutoTaskResult DepositSurplus(AutoContext ctx)
    {
        Player me = ctx.Me;

        // Done the moment there is breathing room again.
        if (AutoInventory.HasBackpackSpace(ctx.Profile))
            return AutoTaskResult.Succeeded;

        // 1. A usable chest: the recorded one, or one nearby to adopt.
        int chestId = ResolveChest(ctx);
        if (chestId < 0)
            return EnsureChest(ctx);

        Chest chest = Main.chest[chestId];
        Vector2 chestCenter = new(chest.x * 16f + 16f, chest.y * 16f + 16f);
        if (Vector2.Distance(me.Center, chestCenter) > NearChestTiles * 16f + me.width)
        {
            ctx.Navigator.Navigate(me, chestCenter);
            return AutoTaskResult.Running;
        }

        ctx.Navigator.Idle(me);
        bool moved = DepositSurplusInto(chest, ctx.Profile, ctx.State);
        if (AutoInventory.HasBackpackSpace(ctx.Profile))
            return AutoTaskResult.Succeeded;

        // Standing at the chest but nothing could be moved (backpack full of only kept essentials):
        // storage cannot help here. RetryLater lets the watchdog surface it honestly instead of
        // looping. A full deposit that still did not free a slot means the same.
        return moved ? AutoTaskResult.Running : AutoTaskResult.AlternativeRequired;
    }

    /// <summary>Returns a valid chest id (recorded or freshly adopted), or -1 if none is in range.</summary>
    private static int ResolveChest(AutoContext ctx)
    {
        AutoCompanionState st = ctx.State;
        if (st.Chest is Point recorded)
        {
            int recordedId = Chest.FindChest(recorded.X, recorded.Y);
            if (recordedId >= 0 && ChestHasEmptySlot(Main.chest[recordedId]))
                return recordedId;
            st.Chest = null; // it was removed or is completely saturated
        }

        // After local storage has failed repeatedly, perfect world knowledge may adopt the nearest
        // unlocked world chest as temporary overflow. This gives a full early-game backpack a
        // route to recovery even before it has the iron bars and free result slot needed to craft
        // its own chest; the completed aerial house later replaces this anchor with owned storage.
        int searchRadius = ctx.TaskRuntime.RecoveryLevel >= 4
            ? System.Math.Max(Main.maxTilesX, Main.maxTilesY) : ChestSearchRadius;
        int nearby = FindNearbyChestId(ctx.Me, searchRadius);
        if (nearby >= 0)
        {
            st.Chest = new Point(Main.chest[nearby].x, Main.chest[nearby].y);
            return nearby;
        }
        return -1;
    }

    /// <summary>Crafts and places the companion's own chest, mirroring the station flow.</summary>
    private static AutoTaskResult EnsureChest(AutoContext ctx)
    {
        Player me = ctx.Me;

        int chestItemIndex = AutoInventory.FindInventoryIndex(me, item => item.createTile == TileID.Containers);
        if (chestItemIndex >= 0)
        {
            if (AutoWorldKnowledge.FindStationSpot(me, 2) is not Point spot)
                return AutoTaskResult.AlternativeRequired; // no clear ground; relocate
            ctx.Actuator.MoveAndPlace(ctx.Instance, me, ctx.Navigator, chestItemIndex, spot);
            return AutoTaskResult.Running;
        }

        // Need to craft a Chest (8 wood at a Work Bench). If materials/station are missing the
        // planner rebuilds those first; RetryLater here is bounded by the watchdog.
        if (ctx.State.WorkBench is Point bench && WorldGen.InWorld(bench.X, bench.Y, 2) &&
            Main.tile[bench.X, bench.Y].HasTile)
        {
            if (Vector2.Distance(me.Center, new Vector2(bench.X * 16f + 8f, bench.Y * 16f + 8f)) > NearChestTiles * 16f + me.width)
            {
                ctx.Navigator.Navigate(me, new Vector2(bench.X * 16f + 8f, bench.Y * 16f + 8f));
                return AutoTaskResult.Running;
            }
        }

        bool crafted = AutoCraftingService.TryCraft(ctx.Instance, ctx.Profile, ItemID.Chest);
        return crafted ? AutoTaskResult.Running :
            AutoCraftingService.IsAvailable ? AutoTaskResult.RetryLater : AutoTaskResult.Faulted;
    }

    private static int FindNearbyChestId(Player me, int radiusTiles)
    {
        Point origin = me.Center.ToTileCoordinates();
        int best = -1;
        int bestDistance = int.MaxValue;
        for (int i = 0; i < Main.chest.Length; i++)
        {
            Chest chest = Main.chest[i];
            if (chest is null || Chest.IsLocked(chest.x, chest.y) || !ChestHasEmptySlot(chest))
                continue;
            int distance = Math.Abs(chest.x - origin.X) + Math.Abs(chest.y - origin.Y);
            if (distance <= radiusTiles && distance < bestDistance)
            {
                bestDistance = distance;
                best = i;
            }
        }
        return best;
    }

    private static bool ChestHasEmptySlot(Chest chest)
    {
        if (chest is null)
            return false;
        foreach (Item item in chest.item)
            if (item is null || item.IsAir)
                return true;
        return false;
    }

    /// <summary>Moves surplus (non-essential material above its keep-stock) from the backpack into
    /// the chest. Returns true if anything moved.</summary>
    public static bool RestoreCampaignStock(AutoContext ctx)
    {
        if (ctx.State.Chest is not Point recorded)
            return false;
        int chestId = Chest.FindChest(recorded.X, recorded.Y);
        if (chestId < 0 || Main.chest[chestId] is not Chest chest)
            return false;
        Vector2 center = new(chest.x * 16f + 16f, chest.y * 16f + 16f);
        if (Vector2.DistanceSquared(ctx.Me.Center, center) > 4f * 16f * 4f * 16f)
            return false;

        foreach (Item source in chest.item)
        {
            if (source is null || source.IsAir)
                continue;
            int keep = KeepThreshold(source.type, ctx.State);
            int missing = keep - AutoInventory.Count(ctx.Profile, source.type);
            if (missing <= 0)
                continue;
            Item incoming = source.Clone();
            incoming.stack = System.Math.Min(source.stack, missing);
            int moved = incoming.stack;
            if (!AutoInventory.CanFit(ctx.Profile, incoming) || !AutoInventory.Give(ctx.Profile, incoming))
                continue;
            source.stack -= moved;
            if (source.stack <= 0)
                source.TurnToAir();
            ctx.TaskRuntime.RecordWorldProgress();
            return true;
        }
        return false;
    }

    private static bool DepositSurplusInto(Chest chest, CompanionProfile profile,
        AutoCompanionState state)
    {
        bool movedAny = false;
        for (int b = 0; b < profile.Backpack.Length; b++)
        {
            Item item = profile.Backpack[b];
            if (item.IsAir || IsEssential(item, profile))
                continue;

            int keep = KeepThreshold(item.type, state);
            int held = AutoInventory.Count(profile, item.type);
            int allowedToDeposit = keep <= 0 ? item.stack : Math.Max(0, held - keep);
            int depositFromSlot = Math.Min(item.stack, allowedToDeposit);
            if (depositFromSlot <= 0)
                continue;

            int moved = MoveStackToChest(chest, item, depositFromSlot);
            if (moved > 0)
            {
                item.stack -= moved;
                if (item.stack <= 0)
                    item.TurnToAir();
                movedAny = true;
            }
        }
        return movedAny;
    }

    private static int MoveStackToChest(Chest chest, Item source, int amount)
    {
        int moved = 0;
        // Stack into existing matching slots first.
        for (int s = 0; s < chest.item.Length && amount > 0; s++)
        {
            Item slot = chest.item[s];
            if (slot is not null && !slot.IsAir && slot.type == source.type && slot.stack < slot.maxStack)
            {
                int put = Math.Min(slot.maxStack - slot.stack, amount);
                slot.stack += put;
                amount -= put;
                moved += put;
            }
        }
        // Then fill empty slots.
        for (int s = 0; s < chest.item.Length && amount > 0; s++)
        {
            if (chest.item[s] is null || chest.item[s].IsAir)
            {
                Item clone = source.Clone();
                clone.stack = Math.Min(source.maxStack, amount);
                chest.item[s] = clone;
                amount -= clone.stack;
                moved += clone.stack;
            }
        }
        return moved;
    }

    /// <summary>Items the companion must never store: its tools, weapons, gear, ammo, light supply,
    /// coins and its own chests. Everything else (raw blocks, ores, bars, gel, junk) is surplus.</summary>
    private static bool IsEssential(Item item, CompanionProfile profile)
    {
        if (item.pick > 0 || item.axe > 0 || item.hammer > 0 || item.accessory ||
            item.ammo > 0 || item.fishingPole > 0 || item.bait > 0 ||
            item.headSlot >= 0 || item.bodySlot >= 0 || item.legSlot >= 0 || item.mountType >= MountID.Rudolph)
            return true;
        if (item.damage > 0)
            return !AutoWeaponEvaluator.IsObsolete(profile, item);
        if (item.potion || item.healLife > 0 || item.healMana > 0 ||
            item.consumable && item.buffType > 0 && item.buffTime > 0)
            return true;

        switch (item.type)
        {
            case ItemID.Torch:
            case ItemID.Glowstick:
            case ItemID.CopperCoin:
            case ItemID.SilverCoin:
            case ItemID.GoldCoin:
            case ItemID.PlatinumCoin:
                return true;
        }

        // Never deposit a chest into a chest, and keep the basic stations it still has to place.
        return item.createTile is TileID.Containers or TileID.WorkBenches or TileID.Furnaces or
            TileID.Anvils or TileID.Hellforge or TileID.AdamantiteForge or
            TileID.MythrilAnvil or TileID.LunarCraftingStation;
    }
}
