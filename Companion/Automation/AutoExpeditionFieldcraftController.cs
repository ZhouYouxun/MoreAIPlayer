using Microsoft.Xna.Framework;
using MoreAIPlayer.Companion.AI;
using Terraria;
using Terraria.ID;

namespace MoreAIPlayer.Companion.Automation;

/// <summary>
/// Small, opportunistic pieces of expert exploration craft which support the current campaign
/// route without becoming campaign goals themselves: observer-facing cave lighting, incremental
/// vertical access and a short emergency barrier against a dangerous approaching enemy. Every
/// placement goes through vanilla item use, and each unconfirmed attempt is bounded before the
/// controller falls back or lets the main route continue.
/// </summary>
public sealed class AutoExpeditionFieldcraftController
{
    private static readonly int[] BarrierBlockItemTypes =
    [
        ItemID.DirtBlock, ItemID.MudBlock, ItemID.ClayBlock, ItemID.SnowBlock,
        ItemID.StoneBlock, ItemID.Wood
    ];

    private const float UndergroundDarknessThreshold = 0.58f;
    private const int LightingSpacingTiles = 9;
    private const int LightingSuccessCooldownTicks = 180;
    private const int LightingFailureCooldownTicks = 45;
    private const int LightingPlacementVerificationTicks = 24;
    private const int LightingFailureLimit = 3;
    private const int VerticalPlacementVerificationTicks = 24;
    private const int VerticalFailureLimit = 2;
    private const int VerticalFailureBackoffTicks = 600;
    private const int BarrierHeight = 3;
    private const int BarrierAttemptCooldownTicks = 12;
    private const int BarrierFailureLimit = 4;
    private const int BarrierFailureBackoffTicks = 300;
    private const int MaximumUnarmedRetreatTicks = 45;
    private const int UnarmedPlannerGraceTicks = 240;
    private const float UnarmedRetreatTriggerTiles = 6f;
    private const float UnarmedRetreatSafeTiles = 9f;

    private long nextLightingTick;
    private Point? pendingLightingTile;
    private long pendingLightingTick;
    private int lightingFailures;
    private Point rejectedLightingTile = new(-1, -1);
    private long rejectedLightingUntil;
    private Point? pendingVerticalTile;
    private int pendingVerticalTileType = -1;
    private bool pendingVerticalWasRope;
    private long pendingVerticalTick;
    private int ropeFailures;
    private int platformFailures;
    private long ropeBackoffUntil;
    private long platformBackoffUntil;
    private int barrierNpc = -1;
    private Point barrierAnchor = new(-1, -1);
    private Point? pendingBarrierTile;
    private long pendingBarrierTick;
    private int barrierFailures;
    private long nextBarrierAttemptTick;
    private long barrierBackoffUntil;
    private int retreatNpc = -1;
    private int retreatSide;
    private Vector2 retreatGoal;
    private long retreatGoalUntil;
    private long unarmedRetreatStartedTick = -1;
    private long unarmedPlannerGraceUntil;
    private Vector2 unarmedRetreatGoal;

    /// <summary>A player-supplied torch, rope, platform or block changes which fieldcraft branch is
    /// viable. Clear only transient attempts/cooldowns so the next tick rechecks the live loadout;
    /// real tiles already placed in the world remain the authority and will be rediscovered.</summary>
    public void NotifyExternalInventoryEdit()
    {
        nextLightingTick = 0;
        pendingLightingTile = null;
        lightingFailures = 0;
        rejectedLightingTile = new Point(-1, -1);
        rejectedLightingUntil = 0;

        pendingVerticalTile = null;
        pendingVerticalTileType = -1;
        ropeFailures = 0;
        platformFailures = 0;
        ropeBackoffUntil = 0;
        platformBackoffUntil = 0;

        nextBarrierAttemptTick = 0;
        barrierBackoffUntil = 0;
        retreatNpc = -1;
        retreatGoalUntil = 0;
        unarmedRetreatStartedTick = -1;
        unarmedPlannerGraceUntil = 0;
        unarmedRetreatGoal = Vector2.Zero;
        ResetBarrier(-1);
    }

    /// <summary>An unarmed fresh profile may evade one nearby enemy, but evasion cannot own the
    /// scheduler forever. It chooses one fixed direction for at most three quarters of a second,
    /// then gives the planner a four-second protected work window to reach a tree, bench or other
    /// real provider. Enemy side changes never reverse the retained escape point.</summary>
    public bool TryRetreatUnarmed(AutoContext ctx, NPC threat)
    {
        long now = (long)Main.GameUpdateCount;
        if (now < unarmedPlannerGraceUntil || threat is null || !threat.active)
            return false;

        float threatDistance = Vector2.Distance(ctx.Me.Center, threat.Center);
        if (unarmedRetreatStartedTick < 0)
        {
            if (threatDistance > UnarmedRetreatTriggerTiles * 16f)
                return false;

            int side = threat.Center.X >= ctx.Me.Center.X ? -1 : 1;
            float margin = 20f * 16f;
            unarmedRetreatGoal = new Vector2(
                MathHelper.Clamp(ctx.Me.Center.X + side * 10f * 16f,
                    margin, Main.maxTilesX * 16f - margin),
                MathHelper.Clamp(ctx.Me.Center.Y - 2f * 16f,
                    margin, Main.maxTilesY * 16f - margin));
            unarmedRetreatStartedTick = now;
            ctx.TaskRuntime.Log($"unarmed retreat start side={side}");
        }

        bool safe = threatDistance >= UnarmedRetreatSafeTiles * 16f;
        bool reached = Vector2.DistanceSquared(ctx.Me.Center, unarmedRetreatGoal) <=
            2f * 16f * 2f * 16f;
        bool exhausted = now - unarmedRetreatStartedTick >= MaximumUnarmedRetreatTicks;
        if (safe || reached || exhausted)
        {
            unarmedRetreatStartedTick = -1;
            unarmedPlannerGraceUntil = now + UnarmedPlannerGraceTicks;
            unarmedRetreatGoal = Vector2.Zero;
            ctx.Navigator.Reset();
            ctx.TaskRuntime.Log($"unarmed retreat released planner safe={safe} reached={reached}");
            return false;
        }

        ctx.Navigator.Navigate(ctx.Me, unarmedRetreatGoal);
        return true;
    }

    /// <summary>Moves toward one retained escape point instead of recentering a relative waypoint
    /// every frame. The stable purpose lets the spatial watchdog distinguish real retreat from
    /// left/right pacing while the main planner remains free to replace the route once safe.</summary>
    public void RetreatFromThreat(AutoContext ctx, NPC threat, float distanceTiles = 10f)
    {
        long now = (long)Main.GameUpdateCount;
        int side = threat.Center.X >= ctx.Me.Center.X ? -1 : 1;
        bool reached = Vector2.DistanceSquared(ctx.Me.Center, retreatGoal) <=
            2f * 16f * 2f * 16f;
        if (retreatNpc != threat.whoAmI || now >= retreatGoalUntil ||
            !float.IsFinite(retreatGoal.X) || !float.IsFinite(retreatGoal.Y))
        {
            float margin = 20f * 16f;
            retreatGoal = new Vector2(
                MathHelper.Clamp(ctx.Me.Center.X + side * distanceTiles * 16f,
                    margin, Main.maxTilesX * 16f - margin),
                MathHelper.Clamp(ctx.Me.Center.Y - 2f * 16f,
                    margin, Main.maxTilesY * 16f - margin));
            retreatNpc = threat.whoAmI;
            retreatSide = side;
            retreatGoalUntil = now + 180;
        }
        if (reached)
        {
            ctx.Navigator.Idle(ctx.Me);
            return;
        }
        ctx.Navigator.Navigate(ctx.Me, retreatGoal);
    }

    /// <summary>Places a reusable real torch only in dark underground space and only when no
    /// nearby torch already serves the observer. The one-item reserve is an explicit automation
    /// exception; <see cref="CompanionGlobalItem"/> prevents placement from consuming it.</summary>
    public bool TryMaintainUndergroundLighting(AutoContext ctx)
    {
        long now = (long)Main.GameUpdateCount;
        ResolvePendingLighting(ctx, now);
        if (pendingLightingTile.HasValue || now < nextLightingTick ||
            ctx.Actuator.ItemUseSpent || ctx.Me.itemAnimation > 0 || ctx.Me.wet)
            return false;

        Point center = ctx.Me.Center.ToTileCoordinates();
        if (!WorldGen.InWorld(center.X, center.Y, LightingSpacingTiles + 5) ||
            center.Y <= Main.worldSurface + 4 ||
            Lighting.Brightness(center.X, center.Y) >= UndergroundDarknessThreshold ||
            HasNearbyTorch(center, LightingSpacingTiles))
            return false;

        int torchSlot = EnsureReusableTorch(ctx);
        Point target = FindNearbyTorchPlacement(ctx.Me, now);
        if (torchSlot < 0 || target.X < 0 ||
            !AutoActuator.IsTileInReach(ctx.Me, ctx.Me.inventory[torchSlot], target.X, target.Y))
        {
            nextLightingTick = now + LightingFailureCooldownTicks;
            return false;
        }

        ctx.Navigator.Idle(ctx.Me);
        bool used = ctx.Actuator.UseItemInSlot(ctx.Instance, ctx.Me, torchSlot,
            TileCenter(target), target);
        if (!used)
        {
            RegisterLightingFailure(target, now);
            return false;
        }

        // A successful ItemCheck call only proves that vanilla accepted an attempt. It does not
        // prove a tile appeared (foreign hooks, placement rules and network guards may still veto
        // it), so the long cooldown begins only after the world confirms the torch.
        pendingLightingTile = target;
        pendingLightingTick = now;
        nextLightingTick = now + LightingPlacementVerificationTicks;
        ctx.TaskRuntime.Log($"field light attempt {target.X},{target.Y}");
        return used;
    }

    /// <summary>When a retained destination is substantially above the companion, add one real
    /// climb aid within reach. Rope is preferred; two unconfirmed rope attempts temporarily switch
    /// this local obstacle to wooden platforms instead of repeating the same placement forever.</summary>
    public bool TryBuildVerticalAccess(AutoContext ctx, Vector2 destination)
    {
        Vector2 delta = destination - ctx.Me.Center;
        if (delta.Y > -5f * 16f || System.Math.Abs(delta.X) >
            System.Math.Max(12f * 16f, -delta.Y * 1.35f))
            return false;

        long now = (long)Main.GameUpdateCount;
        ResolvePendingVerticalPlacement(ctx, now);
        if (pendingVerticalTile.HasValue && now - pendingVerticalTick < VerticalPlacementVerificationTicks)
            return false;

        Point body = ctx.Me.Center.ToTileCoordinates();
        if (HasClimbableRopeAbove(body))
            return false; // the pathfinder already knows how to enter and climb it
        if (ctx.Actuator.ItemUseSpent || ctx.Me.itemAnimation > 0)
            return false; // being busy is not a failed rope or platform placement

        int ropeSlot = now >= ropeBackoffUntil
            ? AutoInventory.FindInventoryIndex(ctx.Me, IsRopeItem)
            : -1;
        if (ropeSlot >= 0)
        {
            Point ropeTarget = FindRopePlacement(ctx.Me, destination);
            if (ropeTarget.X >= 0)
            {
                if (TryStartPlacement(ctx, ropeSlot, ropeTarget))
                {
                    BeginVerticalVerification(ropeTarget, ctx.Me.inventory[ropeSlot].createTile,
                        rope: true, now: now);
                    ctx.TaskRuntime.Log($"vertical rope attempt {ropeTarget.X},{ropeTarget.Y}");
                    return true;
                }
                RegisterVerticalFailure(now, rope: true);
            }
        }

        int platformSlot = AutoInventory.FindInventoryIndex(ctx.Me,
            item => item.type == ItemID.WoodPlatform);
        if (now < platformBackoffUntil)
            return false;
        if (platformSlot < 0 && AutoInventory.Count(ctx.Profile, ItemID.Wood) > 0 &&
            AutoCraftingService.TryCraftUntil(ctx.Instance, ctx.Profile, ItemID.WoodPlatform, 6))
        {
            ctx.TaskRuntime.Log("vertical platforms crafted");
            return true;
        }
        platformSlot = AutoInventory.FindInventoryIndex(ctx.Me,
            item => item.type == ItemID.WoodPlatform);
        if (platformSlot < 0)
            return false;

        Point platformTarget = FindPlatformStep(ctx.Me, destination);
        if (platformTarget.X < 0)
            return false;
        if (!TryStartPlacement(ctx, platformSlot, platformTarget))
        {
            RegisterVerticalFailure(now, rope: false);
            return false;
        }

        BeginVerticalVerification(platformTarget, ctx.Me.inventory[platformSlot].createTile,
            rope: false, now: now);
        ctx.TaskRuntime.Log($"vertical platform attempt {platformTarget.X},{platformTarget.Y}");
        return true;
    }

    /// <summary>Raises a three-tile real-block screen between the companion and a dangerous
    /// approaching ordinary enemy. This is deliberately a short emergency action, not a reason to
    /// chase an enemy or keep rebuilding a failed wall.</summary>
    public bool TryBuildThreatBarrier(AutoContext ctx, NPC threat)
    {
        long now = (long)Main.GameUpdateCount;
        if (threat is null || !threat.active || threat.noTileCollide || threat.noGravity ||
            now < barrierBackoffUntil ||
            ctx.Actuator.ItemUseSpent)
            return false;

        if (barrierNpc != threat.whoAmI || Vector2.DistanceSquared(ctx.Me.Center, threat.Center) >
            15f * 16f * 15f * 16f)
            ResetBarrier(threat.whoAmI);

        ResolvePendingBarrier(ctx, now);
        if (now < nextBarrierAttemptTick)
        {
            if (barrierAnchor.X >= 0 && FindMissingBarrierSegment(barrierAnchor).X < 0)
            {
                RetreatFromThreat(ctx, threat, 6f);
                return true;
            }
            return pendingBarrierTile.HasValue ||
                (barrierAnchor.X >= 0 && FindMissingBarrierSegment(barrierAnchor).X >= 0);
        }
        if (pendingBarrierTile.HasValue && now - pendingBarrierTick < BarrierAttemptCooldownTicks)
            return true;
        if (barrierFailures >= BarrierFailureLimit)
        {
            barrierBackoffUntil = now + BarrierFailureBackoffTicks;
            ResetBarrier(-1);
            return false;
        }

        float distance = Vector2.Distance(ctx.Me.Center, threat.Center);
        if (distance < 4.25f * 16f || distance > 14f * 16f)
            return false;

        int side = threat.Center.X >= ctx.Me.Center.X ? 1 : -1;
        int playerTileX = ctx.Me.Center.ToTileCoordinates().X;
        if (barrierAnchor.X >= 0 && (barrierAnchor.X - playerTileX) * side <= 0)
            ResetBarrier(threat.whoAmI);
        if (barrierAnchor.X < 0)
        {
            int x = playerTileX + side * 3;
            int floor = FindSolidFloor(x, ctx.Me.Bottom.ToTileCoordinates().Y - 1);
            if (floor < 0 || !BarrierSiteIsSafe(x, floor))
                return false;
            barrierAnchor = new Point(x, floor - 1);
        }

        Point target = FindMissingBarrierSegment(barrierAnchor);
        if (target.X < 0)
        {
            RetreatFromThreat(ctx, threat, 6f);
            return true;
        }

        int missingSegments = CountMissingBarrierSegments(barrierAnchor);
        if (CountSpendableBarrierBlocks(ctx) < missingSegments)
        {
            // If the player removed material after construction began, preserve the partial cover
            // and back away instead of switching to melee and walking into our own wall.
            if (missingSegments < BarrierHeight)
            {
                RetreatFromThreat(ctx, threat, 6f);
                return true;
            }
            return false;
        }

        int blockSlot = FindCheapSolidBlock(ctx);
        if (blockSlot < 0 || !AutoActuator.IsTileInReach(ctx.Me,
            ctx.Me.inventory[blockSlot], target.X, target.Y))
            return false;

        ctx.Navigator.Idle(ctx.Me);
        bool used = ctx.Actuator.UseItemInSlot(ctx.Instance, ctx.Me, blockSlot,
            TileCenter(target), target);
        nextBarrierAttemptTick = now + BarrierAttemptCooldownTicks;
        if (!used)
            return ctx.Me.itemAnimation > 0;

        pendingBarrierTile = target;
        pendingBarrierTick = now;
        ctx.TaskRuntime.Log($"threat barrier {target.X},{target.Y}");
        return true;
    }

    private static int EnsureReusableTorch(AutoContext ctx)
    {
        int slot = AutoInventory.FindInventoryIndex(ctx.Me, IsTorchItem);
        if (slot >= 0)
            return slot;

        Item reserve = new(ItemID.Torch, 1);
        if (!AutoInventory.Give(ctx.Profile, reserve))
            return -1;
        return AutoInventory.FindInventoryIndex(ctx.Me, IsTorchItem);
    }

    private static bool HasNearbyTorch(Point center, int radius)
    {
        int radiusSquared = radius * radius;
        for (int x = center.X - radius; x <= center.X + radius; x++)
        for (int y = center.Y - radius; y <= center.Y + radius; y++)
        {
            if (!WorldGen.InWorld(x, y, 3))
                continue;
            int dx = x - center.X;
            int dy = y - center.Y;
            if (dx * dx + dy * dy > radiusSquared)
                continue;
            Tile tile = Main.tile[x, y];
            if (tile.HasTile && tile.TileType < TileID.Sets.Torch.Length &&
                TileID.Sets.Torch[tile.TileType])
                return true;
        }
        return false;
    }

    private Point FindNearbyTorchPlacement(Player me, long now)
    {
        Point center = me.Center.ToTileCoordinates();
        Point[] candidates =
        [
            new(center.X - 2, center.Y), new(center.X + 2, center.Y),
            new(center.X - 1, center.Y - 1), new(center.X + 1, center.Y - 1),
            new(center.X, center.Y - 2), new(center.X - 2, center.Y - 1),
            new(center.X + 2, center.Y - 1)
        ];
        foreach (Point target in candidates)
        {
            if (now < rejectedLightingUntil && target == rejectedLightingTile)
                continue;
            if (!CanPlaceEmptyDry(target) || !HasPlacementAnchor(target))
                continue;
            return target;
        }
        return new Point(-1, -1);
    }

    private void ResolvePendingLighting(AutoContext ctx, long now)
    {
        if (pendingLightingTile is not Point target)
            return;
        if (TileIsTorch(target))
        {
            pendingLightingTile = null;
            lightingFailures = 0;
            rejectedLightingTile = new Point(-1, -1);
            rejectedLightingUntil = 0;
            nextLightingTick = now + LightingSuccessCooldownTicks;
            ctx.TaskRuntime.RecordWorldProgress();
            ctx.TaskRuntime.Log($"field light confirmed {target.X},{target.Y}");
            return;
        }
        if (now - pendingLightingTick < LightingPlacementVerificationTicks)
            return;

        pendingLightingTile = null;
        RegisterLightingFailure(target, now);
    }

    private void RegisterLightingFailure(Point target, long now)
    {
        rejectedLightingTile = target;
        rejectedLightingUntil = now + LightingSuccessCooldownTicks;
        lightingFailures++;
        bool exhausted = lightingFailures >= LightingFailureLimit;
        nextLightingTick = now + (exhausted ? LightingSuccessCooldownTicks : LightingFailureCooldownTicks);
        if (exhausted)
            lightingFailures = 0;
    }

    private static bool TileIsTorch(Point target)
    {
        if (!WorldGen.InWorld(target.X, target.Y, 3))
            return false;
        Tile tile = Main.tile[target.X, target.Y];
        return tile.HasTile && tile.TileType < TileID.Sets.Torch.Length &&
            TileID.Sets.Torch[tile.TileType];
    }

    private void ResolvePendingVerticalPlacement(AutoContext ctx, long now)
    {
        if (pendingVerticalTile is not Point target)
            return;
        Tile tile = Main.tile[target.X, target.Y];
        if (tile.HasTile && (tile.TileType == pendingVerticalTileType ||
            CompanionRopeRegistry.IsRope(target.X, target.Y) || Main.tileSolidTop[tile.TileType]))
        {
            pendingVerticalTile = null;
            pendingVerticalTileType = -1;
            if (pendingVerticalWasRope)
                ropeFailures = 0;
            else
                platformFailures = 0;
            ctx.TaskRuntime.RecordWorldProgress();
            return;
        }
        if (now - pendingVerticalTick < VerticalPlacementVerificationTicks)
            return;
        bool failedRope = pendingVerticalWasRope;
        pendingVerticalTile = null;
        pendingVerticalTileType = -1;
        RegisterVerticalFailure(now, failedRope);
    }

    private void BeginVerticalVerification(Point target, int tileType, bool rope, long now)
    {
        pendingVerticalTile = target;
        pendingVerticalTileType = tileType;
        pendingVerticalWasRope = rope;
        pendingVerticalTick = now;
    }

    private void RegisterVerticalFailure(long now, bool rope)
    {
        if (rope)
        {
            ropeFailures++;
            if (ropeFailures < VerticalFailureLimit)
                return;
            ropeBackoffUntil = now + VerticalFailureBackoffTicks;
            ropeFailures = 0;
        }
        else
        {
            platformFailures++;
            if (platformFailures < VerticalFailureLimit)
                return;
            platformBackoffUntil = now + VerticalFailureBackoffTicks;
            platformFailures = 0;
        }
    }

    private static bool HasClimbableRopeAbove(Point body)
    {
        for (int y = body.Y - 5; y < body.Y; y++)
            if (CompanionRopeRegistry.IsRope(body.X, y))
                return true;
        return false;
    }

    private bool TryStartPlacement(AutoContext ctx, int slot, Point target)
    {
        Item item = ctx.Me.inventory[slot];
        if (!CanPlaceEmptyDry(target) || !AutoActuator.IsTileInReach(ctx.Me, item, target.X, target.Y))
            return false;
        ctx.Navigator.Idle(ctx.Me);
        return ctx.Actuator.UseItemInSlot(ctx.Instance, ctx.Me, slot, TileCenter(target), target);
    }

    private static Point FindRopePlacement(Player me, Vector2 destination)
    {
        Point center = me.Center.ToTileCoordinates();
        int side = destination.X >= me.Center.X ? 1 : -1;
        for (int rise = 3; rise >= 1; rise--)
        {
            Point[] candidates =
            [
                new(center.X, center.Y - rise),
                new(center.X + side, center.Y - rise),
                new(center.X - side, center.Y - rise)
            ];
            foreach (Point target in candidates)
                if (CanPlaceEmptyDry(target) && HasPlacementAnchor(target))
                    return target;
        }
        return new Point(-1, -1);
    }

    private static Point FindPlatformStep(Player me, Vector2 destination)
    {
        Point center = me.Center.ToTileCoordinates();
        int side = destination.X >= me.Center.X ? 1 : -1;
        Point[] candidates =
        [
            new(center.X + side * 2, center.Y - 2),
            new(center.X + side, center.Y - 3),
            new(center.X - side * 2, center.Y - 2),
            new(center.X - side, center.Y - 3),
            new(center.X, center.Y - 3)
        ];
        foreach (Point target in candidates)
            if (CanPlaceEmptyDry(target) && HasPlacementAnchor(target))
                return target;
        return new Point(-1, -1);
    }

    private void ResolvePendingBarrier(AutoContext ctx, long now)
    {
        if (pendingBarrierTile is not Point target)
            return;
        if (Main.tile[target.X, target.Y].HasTile)
        {
            pendingBarrierTile = null;
            barrierFailures = 0;
            ctx.TaskRuntime.RecordWorldProgress();
            return;
        }
        if (now - pendingBarrierTick < BarrierAttemptCooldownTicks)
            return;
        pendingBarrierTile = null;
        barrierFailures++;
    }

    private void ResetBarrier(int npc)
    {
        barrierNpc = npc;
        barrierAnchor = new Point(-1, -1);
        pendingBarrierTile = null;
        barrierFailures = 0;
    }

    private static int FindSolidFloor(int x, int nearY)
    {
        for (int offset = -2; offset <= 5; offset++)
        {
            int y = nearY + offset;
            if (WorldGen.InWorld(x, y, 8) && WorldGen.SolidTile(x, y))
                return y;
        }
        return -1;
    }

    private static bool BarrierSiteIsSafe(int x, int floor)
    {
        for (int y = floor - BarrierHeight; y < floor; y++)
        {
            if (!WorldGen.InWorld(x, y, 8))
                return false;
            Tile tile = Main.tile[x, y];
            if (tile.LiquidAmount >= 64 || (tile.HasTile && !IsSolidBarrierTile(tile)))
                return false;
        }
        return true;
    }

    private static Point FindMissingBarrierSegment(Point anchor)
    {
        for (int level = 0; level < BarrierHeight; level++)
        {
            Point target = new(anchor.X, anchor.Y - level);
            if (!IsSolidBarrierTile(Main.tile[target.X, target.Y]))
                return target;
        }
        return new Point(-1, -1);
    }

    private static int CountMissingBarrierSegments(Point anchor)
    {
        int missing = 0;
        for (int level = 0; level < BarrierHeight; level++)
            if (!IsSolidBarrierTile(Main.tile[anchor.X, anchor.Y - level]))
                missing++;
        return missing;
    }

    private static int CountSpendableBarrierBlocks(AutoContext ctx)
    {
        int total = 0;
        foreach (int type in BarrierBlockItemTypes)
            total += System.Math.Max(0, AutoInventory.Count(ctx.Profile, type) -
                AutoStorageController.KeepThreshold(type, ctx.State));
        return total;
    }

    private static int FindCheapSolidBlock(AutoContext ctx)
    {
        foreach (int type in BarrierBlockItemTypes)
        {
            int keep = AutoStorageController.KeepThreshold(type, ctx.State);
            if (AutoInventory.Count(ctx.Profile, type) <= keep)
                continue;
            int preferredSlot = AutoInventory.FindInventoryIndex(ctx.Me, item => item.type == type);
            if (preferredSlot >= 0)
                return preferredSlot;
        }
        return -1;
    }

    private static bool IsSolidBarrierTile(Tile tile) =>
        tile.HasTile && tile.TileType < Main.tileSolid.Length && Main.tileSolid[tile.TileType] &&
        !Main.tileSolidTop[tile.TileType] && !Main.tileFrameImportant[tile.TileType];

    private static bool IsTorchItem(Item item) =>
        !item.IsAir && item.type > ItemID.None && item.type < ItemID.Sets.Torches.Length &&
        ItemID.Sets.Torches[item.type];

    private static bool IsRopeItem(Item item) =>
        !item.IsAir && item.createTile >= TileID.Dirt && item.createTile < Main.tileRope.Length &&
        Main.tileRope[item.createTile];

    private static bool CanPlaceEmptyDry(Point target)
    {
        if (!WorldGen.InWorld(target.X, target.Y, 5))
            return false;
        Tile tile = Main.tile[target.X, target.Y];
        return !tile.HasTile && tile.LiquidAmount < 64;
    }

    private static bool HasPlacementAnchor(Point target)
    {
        Tile tile = Main.tile[target.X, target.Y];
        return tile.WallType != WallID.None ||
            WorldGen.SolidTile(target.X, target.Y + 1) ||
            WorldGen.SolidTile(target.X - 1, target.Y) ||
            WorldGen.SolidTile(target.X + 1, target.Y) ||
            CompanionRopeRegistry.IsRope(target.X, target.Y + 1) ||
            CompanionRopeRegistry.IsRope(target.X, target.Y - 1);
    }

    private static Vector2 TileCenter(Point tile) =>
        new(tile.X * 16f + 8f, tile.Y * 16f + 8f);
}
