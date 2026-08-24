using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;

namespace MoreAIPlayer.Companion.Automation;

/// <summary>Executes one route chosen by <see cref="AutoVanillaWeaponStrategy"/>. Route choice and
/// low-level execution are intentionally separate so a blocked gun raid cannot corrupt chest,
/// fishing or generic exploration state.</summary>
public static class AutoVanillaWeaponRouteController
{
    public static AutoTaskResult Execute(AutoContext ctx, AutoWeaponRouteKind route)
    {
        ctx.State.LastWeaponRoute = route;
        return route switch
        {
            AutoWeaponRouteKind.CraftStageWeapon => AutoVanillaWeaponCraftingController.CraftBestAvailable(ctx),
            AutoWeaponRouteKind.WorldChest => AutoLootController.LootBestWorldWeaponChest(ctx),
            AutoWeaponRouteKind.EvilOrbGun => AutoEvilOrbWeaponController.AcquireFirstGun(ctx),
            AutoWeaponRouteKind.Fishing => ctx.Fishing.Fish(ctx),
            AutoWeaponRouteKind.HuntDrops => AutoExplorationController.Explore(ctx),
            _ => AutoTaskResult.Succeeded
        };
    }
}

/// <summary>Perfect-information version of the common first-orb gun raid. It still obtains a
/// hammer/explosive, physically reaches the orb, breaks one live orb and picks up the guaranteed
/// vanilla gun/ammunition; lucky stronger weapons make the planner skip this route entirely.</summary>
public static class AutoEvilOrbWeaponController
{
    private static readonly int[] OrbTiles = [TileID.ShadowOrbs];

    public static AutoTaskResult AcquireFirstGun(AutoContext ctx)
    {
        AutoInventory.TryCollectNearby(ctx.Instance, ctx.Profile, 14f,
            item => IsGun(item) || item.ammo == AmmoID.Bullet);
        AutoGearController.OrganizeOwnedGear(ctx.Me, ctx.Profile);
        if (HasGun(ctx.Profile))
        {
            ctx.State.FirstEvilOrbBroken = true;
            ctx.State.PendingEvilOrb = null;
            ctx.TaskRuntime.ClearActiveTarget();
            return AutoTaskResult.Succeeded;
        }

        if (ctx.State.PendingEvilOrb is Point pending && !LiveOrb(pending))
        {
            ctx.State.PendingEvilOrb = null;
            ctx.State.FirstEvilOrbBroken = true;
            ctx.TaskRuntime.ClearActiveTarget();
            ctx.TaskRuntime.RecordWorldProgress();
            return AutoTaskResult.Running;
        }
        if (ctx.State.FirstEvilOrbBroken)
            return AutoTaskResult.AlternativeRequired;

        AutoTaskResult hammer = EnsureHammer(ctx);
        if (hammer != AutoTaskResult.Succeeded)
            return hammer;

        Point? indexed = ctx.State.PendingEvilOrb ?? AutoWorldIndexSystem.FindNearest(
            ctx.Me.Center, OrbTiles, p => !ctx.TaskRuntime.IsTargetRejected(
                AutoGoalId.AcquireWeaponUpgrade, p));
        if (indexed is not Point target)
            return AutoWorldIndexSystem.ScanComplete
                ? AutoTaskResult.AlternativeRequired : AutoExplorationController.Explore(ctx);

        int hammerSlot = AutoInventory.BestToolIndex(ctx.Me, item => item.hammer);
        if (ctx.TaskRuntime.RecoveryLevel >= 7 && ForceExposeOrb(ctx, target))
        {
            ctx.TaskRuntime.RecordWorldProgress();
            return AutoTaskResult.Running;
        }
        Vector2 targetWorld = new(target.X * 16f + 8f, target.Y * 16f + 8f);
        bool inHammerReach = hammerSlot >= 0 && AutoActuator.IsTileInReach(ctx.Me,
            ctx.Me.inventory[hammerSlot], target.X, target.Y);

        if (!inHammerReach && !HasExplosive(ctx.Profile))
        {
            AutoTaskResult loot = AutoLootController.LootBestWorldItem(ctx, IsUsefulExplosive);
            if (loot != AutoTaskResult.AlternativeRequired)
                return loot;
        }

        ctx.State.PendingEvilOrb = target;
        ctx.TaskRuntime.SetActiveTarget(AutoGoalId.AcquireWeaponUpgrade, target);
        float distance = Vector2.Distance(ctx.Me.Center, targetWorld);
        if (!inHammerReach && distance <= 36f * 16f && TryUseExplosive(ctx, targetWorld))
            return AutoTaskResult.Running;
        if (!inHammerReach)
            return AutoWorldTravelController.TravelTo(ctx, targetWorld, 4f);

        if (hammerSlot < 0 || !ctx.Actuator.MoveAndWorkTile(ctx.Instance, ctx.Me,
            ctx.Navigator, hammerSlot, target))
        {
            ctx.TaskRuntime.RejectActiveTarget(51, 3600);
            ctx.State.PendingEvilOrb = null;
            return AutoTaskResult.AlternativeRequired;
        }
        return AutoTaskResult.Running;
    }

    private static AutoTaskResult EnsureHammer(AutoContext ctx)
    {
        if (AutoInventory.BestHammer(ctx.Profile).hammer > 0)
            return AutoTaskResult.Succeeded;
        if (ctx.State.WorkBench is Point bench && Vector2.DistanceSquared(ctx.Me.Center,
            new Vector2(bench.X * 16f + 8f, bench.Y * 16f + 8f)) > 5f * 16f * 5f * 16f)
            return AutoWorldTravelController.TravelTo(ctx,
                new Vector2(bench.X * 16f + 8f, bench.Y * 16f + 8f), 4f);
        return AutoCraftingService.TryCraft(ctx.Instance, ctx.Profile, ItemID.WoodenHammer)
            ? AutoTaskResult.Running : AutoTaskResult.RetryLater;
    }

    private static bool TryUseExplosive(AutoContext ctx, Vector2 target)
    {
        int slot = AutoInventory.FindInventoryIndex(ctx.Me, IsUsefulExplosive);
        return slot >= 0 && ctx.Actuator.UseItemInSlot(ctx.Instance, ctx.Me, slot, target);
    }

    private static bool HasExplosive(CompanionProfile profile)
    {
        foreach (Item item in profile.Hotbar)
            if (IsUsefulExplosive(item))
                return true;
        foreach (Item item in profile.Backpack)
            if (IsUsefulExplosive(item))
                return true;
        return false;
    }

    private static bool IsUsefulExplosive(Item item) => item is not null && item.type is
        ItemID.Bomb or ItemID.StickyBomb or ItemID.Dynamite or ItemID.StickyDynamite;

    private static bool HasGun(CompanionProfile profile)
    {
        foreach (Item item in profile.Hotbar)
            if (IsGun(item))
                return true;
        foreach (Item item in profile.Backpack)
            if (IsGun(item))
                return true;
        return false;
    }

    private static bool IsGun(Item item) => item is not null && !item.IsAir &&
        item.useAmmo == AmmoID.Bullet && item.damage > 0;

    /// <summary>Absolute single-player insurance after every physical route has failed. It opens
    /// only the small ordinary-tile pocket around the retained orb, leaving the orb itself for the
    /// real hammer action and never touching chests or other frame-important structures.</summary>
    private static bool ForceExposeOrb(AutoContext ctx, Point target)
    {
        bool singlePlayerAuthority = Main.netMode == NetmodeID.SinglePlayer;
        if (!singlePlayerAuthority || !LiveOrb(target))
            return false;
        bool changed = false;
        for (int x = target.X - 4; x <= target.X + 4; x++)
        for (int y = target.Y - 4; y <= target.Y + 4; y++)
        {
            if (!WorldGen.InWorld(x, y, 8) || x == target.X && y == target.Y)
                continue;
            Tile tile = Main.tile[x, y];
            if (!tile.HasTile || tile.TileType == TileID.ShadowOrbs ||
                Main.tileFrameImportant[tile.TileType])
                continue;
            WorldGen.KillTile(x, y, false, false, true);
            if (Main.tile[x, y].HasTile)
                Main.tile[x, y].ClearTile();
            changed = true;
        }
        return changed;
    }

    private static bool LiveOrb(Point point) => WorldGen.InWorld(point.X, point.Y, 5) &&
        Main.tile[point.X, point.Y].HasTile && Main.tile[point.X, point.Y].TileType == TileID.ShadowOrbs;
}
