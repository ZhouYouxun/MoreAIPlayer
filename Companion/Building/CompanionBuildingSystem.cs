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
///
/// The three tactics are persistent: while the builder book has one switched on, this system
/// re-evaluates the world every tick and queues fresh cells reactively (battle platform and
/// defensive build) or in response to the owner's own placements (smart build). Turning the
/// tactic off, switching tactic, or losing every companion clears the queue.
/// </summary>
public sealed class CompanionBuildingSystem : ModSystem
{
    private const int MaximumQueuedJobs = 2000;
    private const int BuildReachTiles = 6;
    private const int PlacementCooldownTicks = 6;
    private const int ReservationLeaseTicks = 90;
    private const int RetryDelayTicks = 20;
    private const int MaximumPlacementAttempts = 12;
    private const int SmartAssistExtensionTiles = 4;
    private const int NoTilePlacement = -1;

    // Battle platform: only ever look this far ahead of the owner, so a bottomless gap is never
    // paved in one command — the window slides forward as the owner walks, which is what makes
    // the bridge extend continuously instead of all at once.
    private const int BattlePlatformLookaheadTiles = 6;

    // How far below the owner's bridge the companion must drop before it is treated as knocked
    // off and recalled to the owner's side.
    private const int BattlePlatformFallTiles = 4;

    // Defensive build tuning.
    private const int DefensiveScanTiles = 26;
    private const int DefensiveWallOffsetTiles = 3;
    private const int DefensiveWallHeight = 4;
    private const int DefensiveRecomputeTicks = 30;

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
        public bool ForceFlat;
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

        /// <summary>The tactic the currently queued jobs belong to (null = builder is switched
        /// off). When the desired tactic changes, the queue is cleared so tactics never blend.</summary>
        public CompanionBuildMode? CurrentTactic;

        /// <summary>Throttle between defensive-build re-evaluations.</summary>
        public int DefensiveCooldown;
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
            if (state.DefensiveCooldown > 0)
                state.DefensiveCooldown--;
        }

        Player owner = Main.LocalPlayer;
        if (ValidOwner(owner))
            DriveLocalOwnerTactic(owner);
    }

    /// <summary>Reactive scheduler for the local owner's currently selected tactic. Runs once per
    /// tick and keeps the shared queue in sync with the world; the companions drain it.</summary>
    private static void DriveLocalOwnerTactic(Player owner)
    {
        CompanionPlayer data = owner.GetModPlayer<CompanionPlayer>();
        bool hasCompanions = CompanionEntitySystem.CountActiveOwnedBy(owner.whoAmI) > 0;
        CompanionBuildMode? desired = data.BuildTacticActive && hasCompanions
            ? data.BuildMode
            : null;

        OwnerBuildState state = GetState(owner.whoAmI);
        if (!Nullable.Equals(state.CurrentTactic, desired))
        {
            ClearJobs(state, owner.whoAmI);
            state.CurrentTactic = desired;
        }

        switch (desired)
        {
            case CompanionBuildMode.BattlePlatform:
                TickBattlePlatform(owner, state);
                break;
            case CompanionBuildMode.DefensiveBuild:
                TickDefensiveBuild(owner, state);
                break;
            // SmartBuild schedules from ObserveOwnerPlacement, not here.
        }
    }

    /// <summary>Records an actual vanilla placement made by the owner and, while Smart Build is on,
    /// extends its pattern in the direction the owner is demonstrating.</summary>
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

        bool smartActive = ownerData.BuildTacticActive &&
            ownerData.BuildMode == CompanionBuildMode.SmartBuild;
        if (!smartActive || CompanionEntitySystem.CountActiveOwnedBy(owner.whoAmI) <= 0)
        {
            RememberObservation(state, point, itemType, isWall);
            return;
        }

        // Smart construction follows a direction the player actually demonstrated. It never
        // guesses from facing direction after a single click, and it only commits more of the
        // exact material while the owner's own inventory still contains that material.
        bool sameSequence = state.LastObservedItemType == itemType &&
            state.LastObservationWasWall == isWall;
        if (sameSequence && OwnerHasMaterial(owner, itemType) &&
            TryGetPlacementDirection(state.LastObservedPoint, point, out Point direction))
        {
            if (isWall)
            {
                for (int step = 1; step <= SmartAssistExtensionTiles; step++)
                    QueueWall(state, point.X + direction.X * step, point.Y + direction.Y * step,
                        itemType, createWall);
            }
            else if (createTile >= 0 && createTile < TileLoader.TileCount)
            {
                Item sample = ContentSamples.ItemsByType[itemType];
                if (IsSmartRepeatableTile(sample))
                {
                    for (int step = 1; step <= SmartAssistExtensionTiles; step++)
                        QueueTile(state, point.X + direction.X * step, point.Y + direction.Y * step,
                            itemType, createTile, style);
                }
            }
        }

        RememberObservation(state, point, itemType, isWall);
    }

    public static int CancelAll(Player owner)
    {
        if (owner is null || !States.TryGetValue(owner.whoAmI, out OwnerBuildState state))
            return 0;

        int removed = state.Jobs.Count;
        ClearJobs(state, owner.whoAmI);
        state.CurrentTactic = null;
        return removed;
    }

    private static void ClearJobs(OwnerBuildState state, int ownerWhoAmI)
    {
        if (state.Jobs.Count > 0 || state.Keys.Count > 0)
        {
            state.Jobs.Clear();
            state.Keys.Clear();
        }
        foreach (CompanionInstance instance in CompanionEntitySystem.All)
        {
            if (instance.OwnerWhoAmI == ownerWhoAmI)
                instance.BuildJobId = -1;
        }
    }

    /// <summary>Prevents a placement made before toggling Smart Build from being paired with the
    /// first placement made after the toggle as though they were one continuous line.</summary>
    public static void ResetSmartObservation(Player owner)
    {
        if (owner is null || !States.TryGetValue(owner.whoAmI, out OwnerBuildState state))
            return;

        state.LastObservedPoint = new Point(-100000, -100000);
        state.LastObservedItemType = -1;
        state.LastObservationWasWall = false;
    }

    /// <summary>
    /// True when the battle-platform tactic is running, the owner is up on a platform with work
    /// still queued, and this companion has dropped well below that bridge — i.e. it fell off or
    /// was knocked off. The brain answers by teleporting the companion back to the owner's side.
    /// </summary>
    public static bool ShouldRecallForBuild(CompanionInstance instance, Player owner)
    {
        if (Main.netMode != NetmodeID.SinglePlayer || instance is null || !ValidOwner(owner) ||
            instance.Entity is null || !instance.Entity.active)
            return false;

        CompanionPlayer data = owner.GetModPlayer<CompanionPlayer>();
        if (!data.BuildTacticActive || data.BuildMode != CompanionBuildMode.BattlePlatform)
            return false;

        if (!HasPendingJobs(owner.whoAmI))
            return false;

        int platformY = FindOwnerPlatformRow(owner);
        if (platformY == int.MinValue)
            return false;

        int builderFootY = instance.Entity.Bottom.ToTileCoordinates().Y;
        return builderFootY > platformY + BattlePlatformFallTiles;
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
            // A battle-platform bridge must be walked, never dropped through: keep the companion
            // on top of the platforms it is laying so it does not fall into the gap it is closing.
            if (job.ForceFlat)
                builder.controlDown = false;
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

    /// <summary>Returns the tile row supporting a player's feet (solid or platform). Used to keep
    /// construction level with where the owner actually stands.</summary>
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

        return feetY;
    }

    /// <summary>The platform row the owner is standing on, or int.MinValue if the owner is not on a
    /// platform. Battle platform only engages while the owner stands on a platform.</summary>
    private static int FindOwnerPlatformRow(Player owner)
    {
        int feetY = owner.Bottom.ToTileCoordinates().Y;
        int leftX = owner.Left.ToTileCoordinates().X;
        int rightX = owner.Right.ToTileCoordinates().X;
        for (int y = feetY - 1; y <= feetY + 2; y++)
        {
            for (int x = leftX; x <= rightX; x++)
            {
                if (!WorldGen.InWorld(x, y, 5))
                    continue;

                Tile tile = Main.tile[x, y];
                if (tile.HasTile && tile.TileType < TileID.Sets.Platforms.Length &&
                    TileID.Sets.Platforms[tile.TileType])
                    return y;
            }
        }

        return int.MinValue;
    }

    /// <summary>
    /// Battle platform. Only while the owner stands on a platform whose facing side breaks off
    /// within the lookahead window, lay flat platforms across the near gap — at most six ahead of
    /// the owner, so a bottomless drop is bridged only as fast as the owner advances into it.
    /// </summary>
    private static void TickBattlePlatform(Player owner, OwnerBuildState state)
    {
        if (!TryChooseMaterial(owner, -1, IsPlatform, out int platformItemType))
            return;

        int platformY = FindOwnerPlatformRow(owner);
        if (platformY == int.MinValue)
            return;

        Item platform = ContentSamples.ItemsByType[platformItemType];
        int direction = owner.direction >= 0 ? 1 : -1;
        int ownerX = owner.Center.ToTileCoordinates().X;

        for (int step = 1; step <= BattlePlatformLookaheadTiles; step++)
        {
            int x = ownerX + direction * step;
            if (!WorldGen.InWorld(x, platformY, 5))
                break;

            Tile tile = Main.tile[x, platformY];
            if (tile.HasTile)
            {
                // An existing platform is fine to walk past; solid ground ahead is a wall we must
                // not bury a hidden platform inside, so stop there.
                if (tile.TileType < TileID.Sets.Platforms.Length && TileID.Sets.Platforms[tile.TileType])
                    continue;
                break;
            }

            // Forced placement so the very first cell over open air still goes down without an
            // already-built neighbour to lean on. forceFlat keeps them flat, never hammered.
            QueueTile(state, x, platformY, platformItemType, platform.createTile, platform.placeStyle,
                allowAnchorPlacement: true, forceFlat: true);
        }
    }

    /// <summary>
    /// Defensive build. Idle unless a hostile NPC is near the owner; then raise a short pillar a
    /// few tiles toward the nearest threat, trying to block its approach. Any placeable solid
    /// block works; the pillar is deliberately small and offset so it never boxes the owner in.
    /// </summary>
    private static void TickDefensiveBuild(Player owner, OwnerBuildState state)
    {
        if (state.DefensiveCooldown > 0)
            return;

        NPC threat = FindNearestThreat(owner, DefensiveScanTiles * 16f);
        if (threat is null)
            return;

        if (!TryChooseMaterial(owner, -1, IsDefensiveBlock, out int blockItemType))
            return;

        state.DefensiveCooldown = DefensiveRecomputeTicks;

        Item block = ContentSamples.ItemsByType[blockItemType];
        int direction = threat.Center.X >= owner.Center.X ? 1 : -1;
        int baseY = FindStandingSurfaceY(owner);
        int wallX = owner.Center.ToTileCoordinates().X + direction * DefensiveWallOffsetTiles;

        for (int i = 1; i <= DefensiveWallHeight; i++)
            QueueTile(state, wallX, baseY - i, blockItemType, block.createTile, block.placeStyle,
                allowAnchorPlacement: true);
    }

    private static NPC FindNearestThreat(Player owner, float maxDistance)
    {
        NPC nearest = null;
        float bestDistance = maxDistance * maxDistance;
        for (int i = 0; i < Main.maxNPCs; i++)
        {
            NPC npc = Main.npc[i];
            if (!npc.active || npc.friendly || npc.townNPC || npc.damage <= 0 || npc.lifeMax <= 5 ||
                npc.dontTakeDamage && npc.immortal)
                continue;

            float distance = Vector2.DistanceSquared(owner.Center, npc.Center);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                nearest = npc;
            }
        }
        return nearest;
    }

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
            // A normal placement needs an already-built neighbour to anchor to. Cells queued in a
            // plan are filled nearest-first, so an inner frame tile can come up before the block it
            // would lean on and fail purely on ordering. Rather than retry forever and eventually
            // drop it — which is what left visible gaps in bridges — force the cell in once it has
            // clearly missed its turn. The cell is always part of the plan and is only ever empty
            // here, so forcing it cannot overwrite anything the player placed.
            if (!placed && (job.AllowAnchorPlacement || job.Attempts >= 3) && !Main.tile[job.X, job.Y].HasTile)
            {
                placed = WorldGen.PlaceTile(job.X, job.Y, job.PlaceType, mute: false, forced: true,
                    plr: instance.Slot, style: job.Style);
                placed = placed && Main.tile[job.X, job.Y].HasTile && Main.tile[job.X, job.Y].TileType == job.PlaceType;
            }
            if (placed)
            {
                TileLoader.PlaceInWorld(job.X, job.Y, hookItem);
                if (job.ForceFlat)
                {
                    // Tile is a readonly location handle in tML; copying the handle preserves
                    // writes while avoiding mutation through Tilemap's value-returning indexer.
                    Tile placedTile = Main.tile[job.X, job.Y];
                    placedTile.Slope = SlopeType.Solid;
                    placedTile.IsHalfBlock = false;
                }
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
            if (member.OwnerWhoAmI != owner.whoAmI || !member.Entity.active || member.IsAutoIdentity ||
                !checkedProfiles.Add(member.ProfileId))
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
            // Never pull build materials from the automation companion's backpack; it is isolated.
            if (member.OwnerWhoAmI != owner.whoAmI || !member.Entity.active || member.IsAutoIdentity ||
                !checkedProfiles.Add(member.ProfileId))
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

    private static bool IsSimpleSolidBlock(Item item) =>
        item is not null && item.createTile != -1 && item.createTile < TileLoader.TileCount &&
        Main.tileSolid[item.createTile] && !Main.tileFrameImportant[item.createTile] &&
        !TileID.Sets.Platforms[item.createTile] && !TileID.Sets.Torch[item.createTile];

    private static bool IsDefensiveBlock(Item item) => IsSimpleSolidBlock(item);

    private static bool IsSmartRepeatableTile(Item item) => IsPlatform(item) || IsSimpleSolidBlock(item);

    private static bool OwnerHasMaterial(Player owner, int itemType) =>
        FindInInventory(owner, item => item.type == itemType) is not null;

    private static bool TryGetPlacementDirection(Point previous, Point current, out Point direction)
    {
        int dx = current.X - previous.X;
        int dy = current.Y - previous.Y;
        direction = Point.Zero;

        // Allow the cursor to advance a few cells between observations, but only accept a
        // cardinal line. Diagonal guesses are especially destructive around doors and stairs.
        if (dy == 0 && dx != 0 && Math.Abs(dx) <= 3)
            direction = new Point(Math.Sign(dx), 0);
        else if (dx == 0 && dy != 0 && Math.Abs(dy) <= 3)
            direction = new Point(0, Math.Sign(dy));

        return direction != Point.Zero;
    }

    private static int QueueTile(
        OwnerBuildState state,
        int x,
        int y,
        int itemType,
        int tileType,
        int style,
        bool allowAnchorPlacement = false,
        int prerequisiteJobId = -1,
        bool forceFlat = false)
    {
        if (!WorldGen.InWorld(x, y, 5))
            return -1;
        Tile tile = Main.tile[x, y];
        if (tile.HasTile)
            return -1;
        return Queue(state, x, y, BuildJobKind.Tile, itemType, tileType, style,
            allowAnchorPlacement, prerequisiteJobId, forceFlat);
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
        int prerequisiteJobId = -1,
        bool forceFlat = false)
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
            PrerequisiteJobId = prerequisiteJobId,
            ForceFlat = forceFlat
        };
        state.Jobs.Add(job);
        return job.Id;
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

    private static bool HasPendingJobs(int ownerWhoAmI) =>
        States.TryGetValue(ownerWhoAmI, out OwnerBuildState state) && state.Jobs.Count > 0;

    private static bool ValidOwner(Player owner) =>
        Main.netMode == NetmodeID.SinglePlayer && owner is not null && owner.active &&
        owner.whoAmI == Main.myPlayer && !CompanionEntitySystem.IsCompanion(owner);
}
