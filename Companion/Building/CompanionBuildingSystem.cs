using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using MoreAIPlayer.Companion.AI;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace MoreAIPlayer.Companion.Building;

/// <summary>
/// Runtime construction queue shared by one owner's active companions. Plans contain only
/// empty target cells: builders never mine, hammer, replace or otherwise revise the world.
/// Every successful placement consumes one real item from an active companion backpack first,
/// then from the owner's non-favourited inventory.
/// </summary>
public sealed class CompanionBuildingSystem : ModSystem
{
    private const int MaximumQueuedJobs = 2000;
    private const int BuildReachTiles = 6;
    private const int PlacementCooldownTicks = 6;
    private const int ReservationLeaseTicks = 90;
    private const int RetryDelayTicks = 20;
    private const int MaximumPlacementAttempts = 12;

    private static readonly Dictionary<int, OwnerBuildState> States = [];

    private enum BuildJobKind
    {
        Tile,
        Wall
    }

    private enum PlacementResult
    {
        Placed,
        AlreadyComplete,
        Retry,
        NoMaterial,
        Obstructed
    }

    private readonly record struct JobKey(int X, int Y, BuildJobKind Kind);

    private sealed class BuildJob
    {
        public int Id;
        public int X;
        public int Y;
        public BuildJobKind Kind;
        public int ItemType;
        public int PlaceType;
        public int Style;
        public bool AllowAnchorPlacement;
        public int ReservedSlot = -1;
        public int Lease;
        public int RetryDelay;
        public int Attempts;
        public int PrerequisiteJobId = -1;
    }

    private sealed class OwnerBuildState
    {
        public readonly List<BuildJob> Jobs = [];
        public readonly HashSet<JobKey> Keys = [];
        public int NextJobId = 1;
        public int LastTileItemType = -1;
        public int LastWallItemType = -1;
        public Point LastObservedPoint = new(-100000, -100000);
        public int LastObservedItemType = -1;
        public bool LastObservationWasWall;
    }

    private sealed class MaterialSource
    {
        public Item Item;
    }

    public override void OnWorldLoad() => States.Clear();

    public override void OnWorldUnload() => States.Clear();

    public override void Unload() => States.Clear();

    public override void PostUpdateWorld() =>
        CompanionFaultGuard.Run("companion construction upkeep", PostUpdateWorldCore);

    private static void PostUpdateWorldCore()
    {
        if (Main.netMode != NetmodeID.SinglePlayer)
        {
            States.Clear();
            return;
        }

        foreach (OwnerBuildState state in States.Values)
        {
            foreach (BuildJob job in state.Jobs)
            {
                if (job.Lease > 0 && --job.Lease <= 0)
                    job.ReservedSlot = -1;
                if (job.RetryDelay > 0)
                    job.RetryDelay--;
            }
        }
    }

    /// <summary>Records an actual vanilla placement made by the owner and extends its pattern
    /// when intelligent assistance is enabled.</summary>
    public static void ObserveOwnerPlacement(
        Player owner,
        CompanionPlayer ownerData,
        Point point,
        int itemType,
        int createTile,
        int createWall,
        int style)
    {
        if (!ValidOwner(owner) || ownerData is null || itemType <= ItemID.None)
            return;

        OwnerBuildState state = GetState(owner.whoAmI);
        bool isWall = createWall > 0;
        if (isWall)
            state.LastWallItemType = itemType;
        else if (createTile >= 0)
            state.LastTileItemType = itemType;

        if (!ownerData.SmartBuildEnabled || CompanionEntitySystem.CountActiveOwnedBy(owner.whoAmI) <= 0)
        {
            RememberObservation(state, point, itemType, isWall);
            return;
        }

        if (isWall)
        {
            // One wall placement usually means "fill this room". A compact 5x5 patch assists
            // without silently consuming a whole stack or leaking through distant rooms.
            for (int x = point.X - 2; x <= point.X + 2; x++)
            for (int y = point.Y - 2; y <= point.Y + 2; y++)
                QueueWall(state, x, y, itemType, createWall);
        }
        else if (createTile >= 0 && createTile < TileLoader.TileCount)
        {
            Item sample = ContentSamples.ItemsByType[itemType];
            if (IsPlatform(sample))
            {
                int direction = owner.direction == 0 ? 1 : owner.direction;
                if (!state.LastObservationWasWall && state.LastObservedItemType == itemType &&
                    state.LastObservedPoint.Y == point.Y && state.LastObservedPoint.X != point.X)
                    direction = Math.Sign(point.X - state.LastObservedPoint.X);

                for (int step = 1; step <= 6; step++)
                    QueueTile(state, point.X + direction * step, point.Y, itemType, createTile, style);
            }
            else if (IsRope(sample))
            {
                for (int step = 1; step <= 10; step++)
                    QueueTile(state, point.X, point.Y + step, itemType, createTile, style);
            }
            else if (IsTorch(sample))
            {
                // Torches are remembered as the preferred light source. Blindly duplicating a
                // torch beside the original is visual clutter, so template plans use it later.
            }
            else if (IsSimpleSolidBlock(sample) && !state.LastObservationWasWall &&
                state.LastObservedItemType == itemType)
            {
                int dx = point.X - state.LastObservedPoint.X;
                int dy = point.Y - state.LastObservedPoint.Y;
                if (Math.Abs(dx) + Math.Abs(dy) == 1)
                {
                    // Two consecutive blocks establish the owner's line. Extend only that
                    // explicit direction; a single click never guesses and seals a doorway.
                    for (int step = 1; step <= 4; step++)
                        QueueTile(state, point.X + dx * step, point.Y + dy * step, itemType, createTile, style);
                }
            }
        }

        RememberObservation(state, point, itemType, isWall);
    }

    public static bool TrySchedulePlan(
        Player owner,
        CompanionBuildMode mode,
        Point target,
        out int queued,
        out string failureKey)
    {
        queued = 0;
        failureKey = string.Empty;
        if (!ValidOwner(owner))
        {
            failureKey = "SingleplayerOnly";
            return false;
        }

        if (!WorldGen.InWorld(target.X, target.Y, 10))
        {
            failureKey = "BuilderInvalidTarget";
            return false;
        }

        OwnerBuildState state = GetState(owner.whoAmI);
        int before = state.Jobs.Count;

        switch (mode)
        {
            case CompanionBuildMode.Bridge:
                if (!TryChooseMaterial(owner, state.LastTileItemType, IsPlatform, out int platformItem))
                {
                    failureKey = "BuilderNeedPlatforms";
                    return false;
                }
                ScheduleBridge(owner, state, target, platformItem);
                break;

            case CompanionBuildMode.Shelter:
                if (!TryChooseMaterial(owner, state.LastTileItemType, IsSimpleSolidBlock, out int blockItem))
                {
                    failureKey = "BuilderNeedBlocks";
                    return false;
                }
                TryChooseMaterial(owner, state.LastWallItemType, IsWallItem, out int wallItem);
                TryChooseMaterial(owner, -1, IsTorch, out int torchItem);
                ScheduleShelter(owner, state, target, blockItem, wallItem, torchItem);
                break;

            case CompanionBuildMode.Arena:
                if (!TryChooseMaterial(owner, state.LastTileItemType, IsPlatform, out int arenaPlatformItem))
                {
                    failureKey = "BuilderNeedPlatforms";
                    return false;
                }
                TryChooseMaterial(owner, -1, IsTorch, out int arenaTorchItem);
                ScheduleArena(state, target, arenaPlatformItem, arenaTorchItem);
                break;

            case CompanionBuildMode.RopeShaft:
                if (!TryChooseMaterial(owner, state.LastTileItemType, IsRope, out int ropeItem))
                {
                    failureKey = "BuilderNeedRope";
                    return false;
                }
                TryChooseMaterial(owner, -1, IsTorch, out int shaftTorchItem);
                ScheduleRopeShaft(owner, state, target, ropeItem, shaftTorchItem);
                break;

            default:
                failureKey = "BuilderInvalidTarget";
                return false;
        }

        queued = state.Jobs.Count - before;
        if (queued <= 0)
        {
            failureKey = "BuilderNothingToBuild";
            return false;
        }
        return true;
    }

    public static int CancelAll(Player owner)
    {
        if (owner is null || !States.TryGetValue(owner.whoAmI, out OwnerBuildState state))
            return 0;

        int removed = state.Jobs.Count;
        state.Jobs.Clear();
        state.Keys.Clear();
        foreach (CompanionInstance instance in CompanionEntitySystem.All)
        {
            if (instance.OwnerWhoAmI == owner.whoAmI)
                instance.BuildJobId = -1;
        }
        return removed;
    }

    /// <summary>Called from the idle branch of CompanionBrain. Combat, waypoints, garrison,
    /// mining and fishing all reach their branches first and therefore interrupt construction.</summary>
    public static bool TryPerformBuildWork(
        CompanionInstance instance,
        Player owner,
        CompanionProfile profile,
        CompanionNavigator navigator)
    {
        if (Main.netMode != NetmodeID.SinglePlayer || instance is null || !instance.Entity.active ||
            !ValidOwner(owner) || profile is null || navigator is null ||
            !States.TryGetValue(owner.whoAmI, out OwnerBuildState state) || state.Jobs.Count == 0)
        {
            if (instance is not null)
                instance.BuildJobId = -1;
            return false;
        }

        Player builder = instance.Entity;
        if (instance.BuildActionCooldown > 0)
            instance.BuildActionCooldown--;

        BuildJob job = FindJob(state, instance.BuildJobId);
        if (job is null || job.RetryDelay > 0 || (job.ReservedSlot >= 0 && job.ReservedSlot != instance.Slot))
        {
            instance.BuildJobId = -1;
            job = ClaimNearestJob(state, builder, instance.Slot);
            if (job is null)
                return false;
            instance.BuildJobId = job.Id;
        }

        job.ReservedSlot = instance.Slot;
        job.Lease = ReservationLeaseTicks;
        Vector2 targetWorld = new(job.X * 16f + 8f, job.Y * 16f + 8f);
        instance.AimWorld = targetWorld;

        if (Vector2.DistanceSquared(builder.Center, targetWorld) > BuildReachTiles * 16f * BuildReachTiles * 16f)
        {
            navigator.Navigate(builder, targetWorld);
            return true;
        }

        navigator.Idle(builder);
        if (instance.BuildActionCooldown > 0)
            return true;

        PlacementResult result = TryPlaceJob(instance, owner, job);
        switch (result)
        {
            case PlacementResult.Placed:
            case PlacementResult.AlreadyComplete:
            case PlacementResult.Obstructed:
                RemoveJob(state, job);
                instance.BuildJobId = -1;
                instance.BuildActionCooldown = PlacementCooldownTicks;
                break;

            case PlacementResult.NoMaterial:
                job.ReservedSlot = -1;
                job.Lease = 0;
                job.RetryDelay = 60;
                instance.BuildJobId = -1;
                return false;

            case PlacementResult.Retry:
                job.Attempts++;
                job.ReservedSlot = -1;
                job.Lease = 0;
                job.RetryDelay = RetryDelayTicks;
                instance.BuildJobId = -1;
                if (job.Attempts >= MaximumPlacementAttempts)
                    RemoveJob(state, job);
                break;
        }
        return true;
    }

    private static void ScheduleBridge(Player owner, OwnerBuildState state, Point target, int itemType)
    {
        Item sample = ContentSamples.ItemsByType[itemType];
        int startX = (int)(owner.Center.X / 16f);
        // A bridge is an extension of the surface the owner is currently using, not a line at
        // the cursor's height. The target still chooses its horizontal direction and length.
        int bridgeY = FindStandingSurfaceY(owner);
        int delta = Math.Clamp(target.X - startX, -160, 160);
        int endX = startX + delta;
        int step = endX >= startX ? 1 : -1;
        int prerequisiteJobId = -1;
        for (int x = startX; ; x += step)
        {
            int queuedJobId = QueueTile(state, x, bridgeY, itemType, sample.createTile, sample.placeStyle,
                allowAnchorPlacement: prerequisiteJobId < 0, prerequisiteJobId: prerequisiteJobId);
            if (queuedJobId >= 0)
                prerequisiteJobId = queuedJobId;
            if (x == endX)
                break;
        }
    }

    /// <summary>Returns the tile row supporting the owner's feet. A bridge can overlap the
    /// existing floor at its start; occupied tiles are safely skipped and the extension begins
    /// at the first empty cell beyond it.</summary>
    private static int FindStandingSurfaceY(Player owner)
    {
        int feetY = owner.Bottom.ToTileCoordinates().Y;
        int leftX = owner.Left.ToTileCoordinates().X;
        int rightX = owner.Right.ToTileCoordinates().X;
        for (int y = feetY; y <= feetY + 4; y++)
        {
            for (int x = leftX; x <= rightX; x++)
            {
                if (!WorldGen.InWorld(x, y, 5))
                    continue;

                Tile tile = Main.tile[x, y];
                if (tile.HasTile && (Main.tileSolid[tile.TileType] || TileID.Sets.Platforms[tile.TileType]))
                    return y;
            }
        }

        // If the player is airborne, retain a predictable row at their feet rather than using
        // an unrelated mouse height.
        return feetY;
    }

    /// <summary>Uses the first solid/platform surface beneath the requested construction point.
    /// Plans retain their requested horizontal location but no longer float because the cursor
    /// happened to be at head height.</summary>
    private static int FindConstructionSurfaceY(int x, int requestedY)
    {
        for (int y = requestedY; y <= requestedY + 60; y++)
        {
            if (!WorldGen.InWorld(x, y, 5))
                break;

            Tile tile = Main.tile[x, y];
            if (tile.HasTile && (Main.tileSolid[tile.TileType] || TileID.Sets.Platforms[tile.TileType]))
                return y;
        }

        return requestedY;
    }

    private static void ScheduleShelter(
        Player owner,
        OwnerBuildState state,
        Point target,
        int blockItemType,
        int wallItemType,
        int torchItemType)
    {
        Item block = ContentSamples.ItemsByType[blockItemType];
        int left = target.X - 5;
        int right = target.X + 5;
        int bottom = FindConstructionSurfaceY(target.X, target.Y);
        int top = bottom - 6;
        int doorwayX = owner.direction >= 0 ? right : left;

        for (int x = left; x <= right; x++)
        {
            QueueTile(state, x, bottom, blockItemType, block.createTile, block.placeStyle,
                allowAnchorPlacement: x == target.X);
            QueueTile(state, x, top, blockItemType, block.createTile, block.placeStyle);
        }
        for (int y = top + 1; y < bottom; y++)
        {
            QueueTile(state, left, y, blockItemType, block.createTile, block.placeStyle);
            QueueTile(state, right, y, blockItemType, block.createTile, block.placeStyle);
        }

        // Leave a three-tile opening on the owner's facing side. The system deliberately does
        // not guess which door the player wants or replace terrain to force one in.
        for (int y = bottom - 3; y < bottom; y++)
            RemoveQueuedCell(state, doorwayX, y, BuildJobKind.Tile);

        if (wallItemType > ItemID.None)
        {
            Item wall = ContentSamples.ItemsByType[wallItemType];
            for (int x = left + 1; x < right; x++)
            for (int y = top + 1; y < bottom; y++)
                QueueWall(state, x, y, wallItemType, wall.createWall);
        }

        if (torchItemType > ItemID.None)
        {
            Item torch = ContentSamples.ItemsByType[torchItemType];
            QueueTile(state, left + 1, top + 3, torchItemType, torch.createTile, torch.placeStyle);
            QueueTile(state, right - 1, top + 3, torchItemType, torch.createTile, torch.placeStyle);
        }
    }

    private static void ScheduleArena(OwnerBuildState state, Point center, int platformItemType, int torchItemType)
    {
        Item platform = ContentSamples.ItemsByType[platformItemType];
        int floorY = FindConstructionSurfaceY(center.X, center.Y);
        for (int row = 0; row < 3; row++)
        {
            int y = floorY - row * 10;
            for (int x = center.X - 30; x <= center.X + 30; x++)
                QueueTile(state, x, y, platformItemType, platform.createTile, platform.placeStyle,
                    allowAnchorPlacement: x == center.X);
        }

        if (torchItemType > ItemID.None)
        {
            Item torch = ContentSamples.ItemsByType[torchItemType];
            for (int row = 0; row < 3; row++)
            for (int column = -1; column <= 1; column += 2)
                QueueTile(state, center.X + column * 20, floorY - row * 10 - 1,
                    torchItemType, torch.createTile, torch.placeStyle);
        }
    }

    private static void ScheduleRopeShaft(
        Player owner,
        OwnerBuildState state,
        Point target,
        int ropeItemType,
        int torchItemType)
    {
        Item rope = ContentSamples.ItemsByType[ropeItemType];
        int top = FindStandingSurfaceY(owner);
        int bottom = Math.Min(top + 120, Math.Max(top, target.Y));
        for (int y = top; y <= bottom; y++)
            QueueTile(state, target.X, y, ropeItemType, rope.createTile, rope.placeStyle,
                allowAnchorPlacement: y == top);

        if (torchItemType <= ItemID.None)
            return;

        Item torch = ContentSamples.ItemsByType[torchItemType];
        for (int y = top; y <= bottom; y += 12)
        {
            Point? light = FindSupportedTorchCell(target.X, y);
            if (light is Point spot)
                QueueTile(state, spot.X, spot.Y, torchItemType, torch.createTile, torch.placeStyle);
        }
    }

    private static Point? FindSupportedTorchCell(int ropeX, int y)
    {
        foreach (int offset in new[] { -1, 1 })
        {
            int x = ropeX + offset;
            if (!WorldGen.InWorld(x, y, 5) || Main.tile[x, y].HasTile)
                continue;
            if (Main.tile[x, y].WallType > WallID.None || SolidTile(x + offset, y) || SolidTile(x, y + 1))
                return new Point(x, y);
        }
        return null;
    }

    private static bool SolidTile(int x, int y) =>
        WorldGen.InWorld(x, y, 2) && Main.tile[x, y].HasTile &&
        Main.tileSolid[Main.tile[x, y].TileType] && !TileID.Sets.Platforms[Main.tile[x, y].TileType];

    private static PlacementResult TryPlaceJob(CompanionInstance instance, Player owner, BuildJob job)
    {
        if (!WorldGen.InWorld(job.X, job.Y, 5))
            return PlacementResult.Obstructed;

        Tile tile = Main.tile[job.X, job.Y];
        if (job.Kind == BuildJobKind.Tile)
        {
            if (tile.HasTile)
                return tile.TileType == job.PlaceType ? PlacementResult.AlreadyComplete : PlacementResult.Obstructed;
        }
        else if (tile.WallType > WallID.None)
            return tile.WallType == job.PlaceType ? PlacementResult.AlreadyComplete : PlacementResult.Obstructed;

        MaterialSource source = FindExactMaterial(owner, instance.ProfileId, job.ItemType);
        if (source is null)
            return PlacementResult.NoMaterial;

        Item hookItem = source.Item.Clone();
        bool placed;
        if (job.Kind == BuildJobKind.Tile)
        {
            placed = WorldGen.PlaceTile(job.X, job.Y, job.PlaceType, mute: false, forced: false,
                plr: instance.Slot, style: job.Style);
            placed = placed && Main.tile[job.X, job.Y].HasTile && Main.tile[job.X, job.Y].TileType == job.PlaceType;
            if (!placed && job.AllowAnchorPlacement && !Main.tile[job.X, job.Y].HasTile)
            {
                placed = WorldGen.PlaceTile(job.X, job.Y, job.PlaceType, mute: false, forced: true,
                    plr: instance.Slot, style: job.Style);
                placed = placed && Main.tile[job.X, job.Y].HasTile && Main.tile[job.X, job.Y].TileType == job.PlaceType;
            }
            if (placed)
            {
                TileLoader.PlaceInWorld(job.X, job.Y, hookItem);
                WorldGen.SquareTileFrame(job.X, job.Y);
            }
        }
        else
        {
            WorldGen.PlaceWall(job.X, job.Y, job.PlaceType, mute: false);
            placed = Main.tile[job.X, job.Y].WallType == job.PlaceType;
            if (placed)
            {
                WallLoader.PlaceInWorld(job.X, job.Y, hookItem);
                WorldGen.SquareWallFrame(job.X, job.Y);
            }
        }

        if (!placed)
            return PlacementResult.Retry;

        ConsumeOne(source.Item);
        return PlacementResult.Placed;
    }

    private static MaterialSource FindExactMaterial(Player owner, int preferredProfileId, int itemType)
    {
        if (itemType <= ItemID.None)
            return null;

        CompanionPlayer data = owner.GetModPlayer<CompanionPlayer>();
        CompanionProfile preferred = data.GetProfile(preferredProfileId);
        MaterialSource source = FindInBackpack(preferred, item => item.type == itemType);
        if (source is not null)
            return source;

        HashSet<int> checkedProfiles = [];
        if (preferred is not null)
            checkedProfiles.Add(preferred.Id);
        foreach (CompanionInstance member in CompanionEntitySystem.All)
        {
            if (member.OwnerWhoAmI != owner.whoAmI || !member.Entity.active || !checkedProfiles.Add(member.ProfileId))
                continue;
            source = FindInBackpack(data.GetProfile(member.ProfileId), item => item.type == itemType);
            if (source is not null)
                return source;
        }

        return FindInInventory(owner, item => item.type == itemType);
    }

    private static bool TryChooseMaterial(Player owner, int preferredType, Predicate<Item> predicate, out int itemType)
    {
        itemType = -1;
        if (preferredType > ItemID.None)
        {
            MaterialSource preferred = FindAvailableMaterial(owner, item => item.type == preferredType && predicate(item));
            if (preferred is not null)
            {
                itemType = preferred.Item.type;
                return true;
            }
        }

        MaterialSource source = FindAvailableMaterial(owner, predicate);
        if (source is null)
            return false;
        itemType = source.Item.type;
        return true;
    }

    private static MaterialSource FindAvailableMaterial(Player owner, Predicate<Item> predicate)
    {
        CompanionPlayer data = owner.GetModPlayer<CompanionPlayer>();
        HashSet<int> checkedProfiles = [];
        foreach (CompanionInstance member in CompanionEntitySystem.All)
        {
            if (member.OwnerWhoAmI != owner.whoAmI || !member.Entity.active || !checkedProfiles.Add(member.ProfileId))
                continue;
            MaterialSource source = FindInBackpack(data.GetProfile(member.ProfileId), predicate);
            if (source is not null)
                return source;
        }
        return FindInInventory(owner, predicate);
    }

    private static MaterialSource FindInBackpack(CompanionProfile profile, Predicate<Item> predicate)
    {
        if (profile is null)
            return null;
        foreach (Item item in profile.Backpack)
        {
            if (!item.IsAir && item.stack > 0 && !item.favorited && predicate(item))
                return new MaterialSource { Item = item };
        }
        return null;
    }

    private static MaterialSource FindInInventory(Player owner, Predicate<Item> predicate)
    {
        foreach (Item item in owner.inventory)
        {
            if (!item.IsAir && item.stack > 0 && !item.favorited && predicate(item))
                return new MaterialSource { Item = item };
        }
        return null;
    }

    private static void ConsumeOne(Item item)
    {
        item.stack--;
        if (item.stack <= 0)
            item.TurnToAir();
    }

    private static bool IsPlatform(Item item) =>
        item is not null && item.createTile != -1 && item.createTile < TileLoader.TileCount &&
        TileID.Sets.Platforms[item.createTile];

    private static bool IsRope(Item item) =>
        item is not null && item.type > ItemID.None && item.type < ItemID.Sets.SortingPriorityRopes.Length &&
        ItemID.Sets.SortingPriorityRopes[item.type] != -1 && item.createTile != -1;

    private static bool IsTorch(Item item) =>
        item is not null && item.createTile != -1 && item.createTile < TileLoader.TileCount &&
        TileID.Sets.Torch[item.createTile];

    private static bool IsWallItem(Item item) => item is not null && item.createWall > WallID.None;

    private static bool IsSimpleSolidBlock(Item item) =>
        item is not null && item.createTile != -1 && item.createTile < TileLoader.TileCount &&
        Main.tileSolid[item.createTile] && !Main.tileFrameImportant[item.createTile] &&
        !TileID.Sets.Platforms[item.createTile] && !TileID.Sets.Torch[item.createTile];

    private static int QueueTile(
        OwnerBuildState state,
        int x,
        int y,
        int itemType,
        int tileType,
        int style,
        bool allowAnchorPlacement = false,
        int prerequisiteJobId = -1)
    {
        if (!WorldGen.InWorld(x, y, 5))
            return -1;
        Tile tile = Main.tile[x, y];
        if (tile.HasTile)
            return -1;
        return Queue(state, x, y, BuildJobKind.Tile, itemType, tileType, style, allowAnchorPlacement, prerequisiteJobId);
    }

    private static void QueueWall(OwnerBuildState state, int x, int y, int itemType, int wallType)
    {
        if (!WorldGen.InWorld(x, y, 5) || Main.tile[x, y].WallType > WallID.None)
            return;
        Queue(state, x, y, BuildJobKind.Wall, itemType, wallType, 0, false);
    }

    private static int Queue(
        OwnerBuildState state,
        int x,
        int y,
        BuildJobKind kind,
        int itemType,
        int placeType,
        int style,
        bool allowAnchorPlacement,
        int prerequisiteJobId = -1)
    {
        if (state.Jobs.Count >= MaximumQueuedJobs || itemType <= ItemID.None || placeType < 0)
            return -1;
        JobKey key = new(x, y, kind);
        if (!state.Keys.Add(key))
            return -1;
        BuildJob job = new()
        {
            Id = state.NextJobId++,
            X = x,
            Y = y,
            Kind = kind,
            ItemType = itemType,
            PlaceType = placeType,
            Style = style,
            AllowAnchorPlacement = allowAnchorPlacement,
            PrerequisiteJobId = prerequisiteJobId
        };
        state.Jobs.Add(job);
        return job.Id;
    }

    private static void RemoveQueuedCell(OwnerBuildState state, int x, int y, BuildJobKind kind)
    {
        for (int i = state.Jobs.Count - 1; i >= 0; i--)
        {
            BuildJob job = state.Jobs[i];
            if (job.X == x && job.Y == y && job.Kind == kind)
            {
                state.Jobs.RemoveAt(i);
                state.Keys.Remove(new JobKey(x, y, kind));
            }
        }
    }

    private static void RemoveJob(OwnerBuildState state, BuildJob job)
    {
        state.Jobs.Remove(job);
        state.Keys.Remove(new JobKey(job.X, job.Y, job.Kind));
    }

    private static BuildJob FindJob(OwnerBuildState state, int id)
    {
        if (id < 0)
            return null;
        foreach (BuildJob job in state.Jobs)
        {
            if (job.Id == id)
                return job;
        }
        return null;
    }

    private static BuildJob ClaimNearestJob(OwnerBuildState state, Player builder, int slot)
    {
        BuildJob best = null;
        float bestDistance = float.MaxValue;
        foreach (BuildJob job in state.Jobs)
        {
            if (job.RetryDelay > 0 || (job.ReservedSlot >= 0 && job.ReservedSlot != slot) ||
                (job.PrerequisiteJobId >= 0 && FindJob(state, job.PrerequisiteJobId) is not null))
                continue;
            Vector2 position = new(job.X * 16f + 8f, job.Y * 16f + 8f);
            float distance = Vector2.DistanceSquared(builder.Center, position);
            if (distance < bestDistance)
            {
                best = job;
                bestDistance = distance;
            }
        }
        if (best is not null)
        {
            best.ReservedSlot = slot;
            best.Lease = ReservationLeaseTicks;
        }
        return best;
    }

    private static void RememberObservation(OwnerBuildState state, Point point, int itemType, bool wasWall)
    {
        state.LastObservedPoint = point;
        state.LastObservedItemType = itemType;
        state.LastObservationWasWall = wasWall;
    }

    private static OwnerBuildState GetState(int ownerWhoAmI)
    {
        if (!States.TryGetValue(ownerWhoAmI, out OwnerBuildState state))
        {
            state = new OwnerBuildState();
            States[ownerWhoAmI] = state;
        }
        return state;
    }

    private static bool ValidOwner(Player owner) =>
        Main.netMode == NetmodeID.SinglePlayer && owner is not null && owner.active &&
        owner.whoAmI == Main.myPlayer && !CompanionEntitySystem.IsCompanion(owner);
}
