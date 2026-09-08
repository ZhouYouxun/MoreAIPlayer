using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace MoreAIPlayer.Companion.AI;

/// <summary>
/// Turns "go to this world position" into simulated player controls. Uses the A* pathfinder
/// with caching, follows waypoints (jumping, flying, swimming, dropping through platforms),
/// and falls back to direct steering when no path exists.
/// </summary>
public sealed class CompanionNavigator
{
    private const float WaypointReachX = 12f;
    private const float WaypointReachY = 24f;
    private const int RepathIntervalTicks = 30;
    private const int FailedRepathDelayTicks = 60;
    private const int StuckRepathTicks = 60;

    // After a platform drop completes, hold off on another one briefly so the companion cannot
    // chain-fall through several layers in a single continuous press.
    private const int PlatformDropCooldownTicks = 16;

    private List<PathStep> path;
    private int pathIndex;
    private int repathCooldown;
    private Point lastGoalTile = new(-1, -1);
    private int stuckTicks;
    private Vector2 lastPosition;
    private bool jumpHeldLastTick;

    // The order currently being executed. FollowPath / DirectSteer read it to decide whether a
    // platform drop is even permitted; the parameterless Navigate overload maps to a safe order
    // that never drops.
    private NavigationRequest currentRequest;

    // Platform-drop state. cooldown blocks a new drop for a short window; the commitment row is
    // the platform being left during an active drop — Down is released the instant the body clears
    // it, which is what stops a single order from punching through the next platform too.
    private int platformDropCooldown;
    private int dropCommitmentSourceRow = -1;
    // After a drop is refused for a real terrain reason (hazard / no landing), platform drops are
    // suppressed for a short window so the search routes around instead of replanning the exact
    // same denied fall every frame.
    private int dropSuppressionTicks;
    private DropDenialReason lastDropDenial = DropDenialReason.Approved;

    private readonly List<OpenedDoor> openedDoors = [];

    // A door is remembered only when this navigator successfully opened it. This keeps the
    // companion from acting as a global auto-close switch for doors opened by the owner, wiring,
    // or another companion.
    private readonly record struct OpenedDoor(
        Point ClosedTile,
        int TravelDirection,
        int OpenedTileType,
        bool TallGate,
        int Age);

    private const int DoorRecordLifetimeTicks = 600;

    /// <summary>True when the most recent search could not find a route to the goal.</summary>
    public bool LastPathFailed { get; private set; }

    /// <summary>True when the last Navigate call actually pressed movement controls this tick.
    /// The brain uses it to notice a companion that wants to travel but is not getting
    /// anywhere (see its stuck-teleport policy). Idling clears it.</summary>
    public bool IsTryingToMove { get; private set; }

    /// <summary>The destination currently being approached. Automation watchdogs use this as a
    /// purpose signal: walking only counts when it produces net approach, so ten-pixel pacing can
    /// no longer reset an idle timer forever.</summary>
    public Vector2? CurrentGoalWorld { get; private set; }

    /// <summary>True while the navigator is driving a rope climb this tick. The brain must not let
    /// ordinary combat positioning fight the climb for vertical control (see doc §2.3).</summary>
    public bool IsClimbing { get; private set; }

    /// <summary>Why the most recent platform-drop request was refused, for diagnostics.</summary>
    public DropDenialReason LastDropDenial => lastDropDenial;

    public void Reset()
    {
        path = null;
        pathIndex = 0;
        repathCooldown = 0;
        lastGoalTile = new Point(-1, -1);
        stuckTicks = 0;
        LastPathFailed = false;
        IsTryingToMove = false;
        jumpHeldLastTick = false;
        IsClimbing = false;
        CurrentGoalWorld = null;
        platformDropCooldown = 0;
        dropCommitmentSourceRow = -1;
        openedDoors.Clear();
    }

    /// <summary>
    /// Closes doors this companion opened after its whole body has crossed to the far side.
    /// Call once per brain tick even when pathfinding is not currently steering the companion,
    /// because combat or an idle branch may take over immediately after it walks through.
    /// </summary>
    public void UpdateOwnedDoors(Player me)
    {
        if (me is null || Main.netMode == NetmodeID.MultiplayerClient)
            return;

        for (int i = openedDoors.Count - 1; i >= 0; i--)
        {
            OpenedDoor pending = openedDoors[i] with { Age = openedDoors[i].Age + 1 };
            openedDoors[i] = pending;

            if (pending.Age > DoorRecordLifetimeTicks)
            {
                openedDoors.RemoveAt(i);
                continue;
            }

            float closedDoorLeft = pending.ClosedTile.X * 16f;
            bool crossed = pending.TravelDirection > 0
                ? me.Left.X > closedDoorLeft + 20f
                : me.Right.X < closedDoorLeft - 4f;
            if (!crossed)
                continue;

            if (!TryFindOpenedDoor(pending, out Point openTile))
            {
                // It was already closed, destroyed, or changed by somebody else.
                openedDoors.RemoveAt(i);
                continue;
            }

            bool closed = pending.TallGate
                ? WorldGen.ShiftTallGate(openTile.X, openTile.Y, closing: true)
                : WorldGen.CloseDoor(openTile.X, openTile.Y);
            if (!closed)
                continue; // another player/NPC is still in the doorway; retry later

            if (Main.netMode == NetmodeID.Server)
            {
                NetMessage.SendData(
                    MessageID.ToggleDoorState,
                    -1,
                    -1,
                    null,
                    pending.TallGate ? 5 : 1,
                    openTile.X,
                    openTile.Y,
                    pending.TravelDirection);
            }
            openedDoors.RemoveAt(i);
        }
    }

    /// <summary>
    /// Legacy safe entry point: travel to a world position without ever actively dropping through
    /// a platform. Every call site that has not opted into a lower layer inherits this, which is
    /// what removes the erroneous combat/follow platform drops in one stroke.
    /// </summary>
    public void Navigate(Player me, Vector2 goalWorld) => Navigate(me, NavigationRequest.Safe(goalWorld));

    /// <summary>Steers through a short safe waypoint while exposing the real final destination to
    /// progress observers. Without this distinction, a waypoint kept eighteen tiles ahead appears
    /// to stay at constant distance even while a long journey is succeeding.</summary>
    public void NavigateToward(Player me, Vector2 waypointWorld, Vector2 purposeWorld)
    {
        Navigate(me, NavigationRequest.Safe(waypointWorld));
        CurrentGoalWorld = purposeWorld;
    }

    /// <summary>Writes movement controls onto the player to travel toward the request's goal,
    /// honouring its intent and platform-drop permission.</summary>
    public void Navigate(Player me, in NavigationRequest request)
    {
        currentRequest = request;
        CurrentGoalWorld = request.GoalWorld;
        IsClimbing = false;
        if (platformDropCooldown > 0)
            platformDropCooldown--;
        if (dropSuppressionTicks > 0)
            dropSuppressionTicks--;
        bool dropAllowedNow = request.AllowPlatformDrop && dropSuppressionTicks <= 0;

        // A drop that has landed (feet back on the ground) always ends the commitment and starts
        // the cooldown, even if the drop waypoint was skipped this tick before the drop handler ran.
        if (dropCommitmentSourceRow >= 0 && me.velocity.Y == 0f)
        {
            dropCommitmentSourceRow = -1;
            platformDropCooldown = PlatformDropCooldownTicks;
        }

        Vector2 goalWorld = request.GoalWorld;
        Vector2 toGoal = goalWorld - me.Center;
        if (System.Math.Abs(toGoal.X) > 12f)
            TryOpenDoorAhead(me, System.Math.Sign(toGoal.X));

        // Close targets never need a path — but a rope climb still does, so a nearly-vertical goal
        // reached by a rope must not shortcut to DirectSteer.
        if (System.Math.Abs(toGoal.X) < 48f && System.Math.Abs(toGoal.Y) < 56f && !me.pulley)
        {
            path = null;
            DirectSteer(me, goalWorld);
            TrackProgress(me);
            return;
        }

        if (repathCooldown > 0)
            repathCooldown--;

        Point goalTile = new((int)(goalWorld.X / 16f), (int)(goalWorld.Y / 16f));
        bool goalMoved = System.Math.Abs(goalTile.X - lastGoalTile.X) + System.Math.Abs(goalTile.Y - lastGoalTile.Y) > 6;
        bool pathExhausted = path is not null && pathIndex >= path.Count;
        bool stuck = stuckTicks > StuckRepathTicks;

        if (repathCooldown <= 0 && (path is null || goalMoved || pathExhausted || stuck))
        {
            Point startTile = new((int)(me.Bottom.X / 16f), (int)((me.Bottom.Y - 1f) / 16f));
            path = CompanionPathfinder.FindPath(startTile, goalTile, MovementCaps.From(me),
                dropAllowedNow, request.MaximumSafeDropTiles);
            pathIndex = 0;
            stuckTicks = 0;
            lastGoalTile = goalTile;
            LastPathFailed = path is null;
            repathCooldown = path is null ? FailedRepathDelayTicks : RepathIntervalTicks;
        }

        if (path is not null && pathIndex < path.Count)
            FollowPath(me, goalWorld);
        else
            DirectSteer(me, goalWorld);

        TrackProgress(me);
    }

    /// <summary>Stops pathing and clears movement state (used when idling next to the owner).</summary>
    public void Idle(Player me)
    {
        path = null;
        stuckTicks = 0;
        jumpHeldLastTick = false;
        IsTryingToMove = false;
        IsClimbing = false;
        CurrentGoalWorld = null;
        dropCommitmentSourceRow = -1;
    }

    private void FollowPath(Player me, Vector2 goalWorld)
    {
        Vector2 feet = me.Bottom;
        int goalDirection = HorizontalDirection(feet.X, goalWorld.X);

        // Skip waypoints that are already reached, as well as ordinary ground waypoints which are
        // now behind the player while the final destination is still ahead. A knockback, conveyor,
        // or slope step which moves the player toward the destination is useful progress; never
        // spend a jump's airtime walking backward merely to touch an obsolete A* node.
        int probes = 0;
        while (pathIndex < path.Count && probes < 8)
        {
            PathStep step = path[pathIndex];
            Vector2 waypoint = step.FeetWorld;
            if ((System.Math.Abs(waypoint.X - feet.X) <= WaypointReachX &&
                 System.Math.Abs(waypoint.Y - feet.Y) <= WaypointReachY) ||
                HasSafelyPassedGroundWaypoint(feet, waypoint, goalDirection, step))
            {
                pathIndex++;
                probes++;
                continue;
            }
            break;
        }

        if (pathIndex >= path.Count)
            return;

        PathStep current = path[pathIndex];

        // Rope steps own vertical (and, before grabbing, horizontal) movement entirely; nothing
        // else in FollowPath runs while climbing.
        if (current.IsRopeMove)
        {
            ExecuteRopeClimb(me, current);
            return;
        }

        Vector2 target = current.FeetWorld;
        float dx = target.X - feet.X;
        float dy = target.Y - feet.Y; // positive: waypoint below the feet

        int steerDirection = HorizontalDirection(feet.X, target.X);
        // Preserve net forward progress in the air. Ground paths may legitimately route around a
        // wall, but an old same-height node behind an airborne player is never worth reversing for.
        if (me.velocity.Y != 0f && goalDirection != 0 && steerDirection == -goalDirection &&
            System.Math.Abs(target.Y - feet.Y) <= 3f * 16f)
        {
            steerDirection = goalDirection;
        }
        ApplyHorizontalDirection(me, steerDirection);

        // Leaving a rope onto a platform/ground: stop climbing, walk toward the exit tile. Not
        // pressing up/down lets vanilla release the pulley as horizontal movement carries the
        // companion off the rope.
        if (current.Kind == PathMoveKind.ExitRope)
        {
            dropCommitmentSourceRow = -1;
            ApplyJumpControls(me, dy < -24f);
            TryOpenDoorForCurrentMovement(me);
            return;
        }

        // The ONLY way controlDown is pressed for a fall-through is an explicit, approved
        // DropThroughPlatform step. The old "waypoint is a bit lower, so hold Down" shortcut is
        // gone — that shortcut is exactly what knocked companions off battle platforms.
        if (current.Kind == PathMoveKind.DropThroughPlatform)
            ExecutePlatformDrop(me, dy);

        // Terraria walks up a clear one-tile rise by itself. The pathfinder emits that as a normal
        // walk edge, so treating its -16px waypoint delta as a jump is both wasteful and the source
        // of the visible hop-on-every-step behaviour. Two or more tiles (or a real gap) still jump.
        bool needUp = dy < -24f;

        // Same-height waypoints across a gap need a running jump: without this the
        // companion would walk straight off the edge into the hole.
        if (!needUp && me.velocity.Y == 0f && dy < 24f && System.Math.Abs(dx) > 24f &&
            !CanWalkUpSingleTile(me, System.Math.Sign(dx)) &&
            NoGroundAhead(me, System.Math.Sign(dx)))
        {
            needUp = true;
        }

        ApplyJumpControls(me, needUp);
        TryOpenDoorForCurrentMovement(me);
    }

    /// <summary>
    /// Presses Down for an approved platform fall-through, then releases the moment the body has
    /// cleared the platform it is leaving. Refuses (and re-plans without a drop) when the central
    /// approval says no — cooldown, a hazardous landing, or having already slipped off the platform.
    /// </summary>
    private void ExecutePlatformDrop(Player me, float dy)
    {
        // Already committed to a drop: hold Down only until the body top clears the source
        // platform, then release so vanilla catches the companion on the destination platform.
        if (dropCommitmentSourceRow >= 0)
        {
            bool clearedSource = me.Top.Y > (dropCommitmentSourceRow + 1) * 16f;
            if (clearedSource || me.velocity.Y == 0f)
            {
                dropCommitmentSourceRow = -1;
                platformDropCooldown = PlatformDropCooldownTicks;
                return;
            }
            if (dy > 20f)
                me.controlDown = true;
            return;
        }

        // Starting a fresh drop: it must pass the central gate every time.
        if (CompanionPlatformDrop.TryApprove(me, currentRequest, platformDropCooldown, out _, out DropDenialReason reason))
        {
            lastDropDenial = DropDenialReason.Approved;
            dropCommitmentSourceRow = (int)(me.Bottom.Y / 16f);
            if (dy > 20f)
                me.controlDown = true;
            return;
        }

        // Refused. Do not press Down. For a real terrain refusal (hazard / no landing / blocked
        // channel), suppress drops briefly and re-plan so the follower routes around instead of
        // grinding on the same denied fall. Cooldown and "slipped off the platform" just wait.
        lastDropDenial = reason;
        if (reason is DropDenialReason.LandingHazard or DropDenialReason.NoLanding or DropDenialReason.ChannelBlocked)
        {
            dropSuppressionTicks = 40;
            path = null;
            repathCooldown = 0;
            lastGoalTile = new Point(-1, -1);
        }
    }

    /// <summary>
    /// Drives a rope climb. The companion is a real Player, so the vanilla pulley code does the
    /// physics — this only aligns to the rope column and presses up/down toward the waypoint, and
    /// never presses Jump (which would detach). Being knocked off simply stops the climb; it does
    /// not spam Up in mid-air.
    /// </summary>
    private void ExecuteRopeClimb(Player me, PathStep current)
    {
        float ropeColumnX = current.X * 16f + 8f;
        float dxToRope = ropeColumnX - me.Center.X;

        // Which way along the rope: the step kind for a climb, or the waypoint height for an entry.
        bool goUp = current.Kind switch
        {
            PathMoveKind.ClimbRopeUp => true,
            PathMoveKind.ClimbRopeDown => false,
            _ => RopeEntryGoesUp(current, me)
        };

        int bodyTop = (int)(me.Top.Y / 16f);
        int bodyBottom = (int)(me.Bottom.Y / 16f);
        bool overlappingRope = CompanionRopeRegistry.ColumnHasRope(current.X, bodyTop - 1, bodyBottom + 1);

        if (me.pulley)
        {
            // On the rope: vanilla snaps X, so only press up/down. Horizontal input here would
            // walk off the rope.
            IsClimbing = true;
            if (goUp)
                me.controlUp = true;
            else
                me.controlDown = true;
            me.controlJump = false;
        }
        else if (overlappingRope && System.Math.Abs(dxToRope) < 12f)
        {
            // Beside/over the rope but not attached yet: a tiny alignment nudge plus up/down makes
            // FindPulley grab it (the grab requires up or down to be held).
            if (dxToRope > 4f)
                me.controlRight = true;
            else if (dxToRope < -4f)
                me.controlLeft = true;
            if (goUp)
                me.controlUp = true;
            else
                me.controlDown = true;
            me.controlJump = false;
            IsClimbing = true;
        }
        else
        {
            // Not on the rope and not overlapping it: approach horizontally. Do NOT press Up in the
            // air — there is nothing to grab, and holding Up wedged against terrain is the failure
            // mode the rope contract calls out.
            if (dxToRope > 4f)
                me.controlRight = true;
            else if (dxToRope < -4f)
                me.controlLeft = true;
        }
    }

    /// <summary>For an EnterRope step, whether to grab and start climbing up or down: follow the
    /// next rope step's direction if there is one, otherwise the waypoint's height.</summary>
    private bool RopeEntryGoesUp(PathStep current, Player me)
    {
        if (path is not null && pathIndex + 1 < path.Count)
        {
            PathStep next = path[pathIndex + 1];
            if (next.Kind == PathMoveKind.ClimbRopeUp)
                return true;
            if (next.Kind == PathMoveKind.ClimbRopeDown)
                return false;
        }
        return current.TileCenterWorld.Y <= me.Center.Y;
    }

    private void DirectSteer(Player me, Vector2 goalWorld)
    {
        Vector2 toGoal = goalWorld - me.Center;

        if (toGoal.X > 12f)
            me.controlRight = true;
        else if (toGoal.X < -12f)
            me.controlLeft = true;

        int movementDirection = me.controlRight ? 1 : me.controlLeft ? -1 : 0;
        bool onHammeredPlatformStair = movementDirection != 0 &&
            CompanionPathfinder.IsNearHammeredPlatformStair(me, movementDirection);

        // A hammered platform briefly reduces horizontal speed while vanilla resolves the
        // one-way slope. That is normal stair contact, not a wall. Treating it as blocked made
        // the old fallback spam fresh jumps and wedge the companion on the next sloped piece.
        bool oneTileWalkUp = movementDirection != 0 && CanWalkUpSingleTile(me, movementDirection);
        bool blocked = !onHammeredPlatformStair && !oneTileWalkUp &&
                       (me.controlLeft || me.controlRight) && me.velocity.Y == 0f &&
                       System.Math.Abs(me.velocity.X) < 0.35f && stuckTicks > 10;
        bool needUp = toGoal.Y < -48f || blocked;

        // A lower goal no longer implies dropping through the floor. Fall through a platform here
        // only when the active order permits it AND the central gate approves — otherwise the
        // companion simply steers toward the goal on its current layer.
        if (currentRequest.AllowPlatformDrop && dropSuppressionTicks <= 0 &&
            toGoal.Y > 48f && System.Math.Abs(toGoal.X) < 160f && StandingOnPlatform(me))
            ExecutePlatformDrop(me, toGoal.Y);

        if (!needUp && !oneTileWalkUp && me.velocity.Y == 0f && toGoal.Y < 32f &&
            (me.controlLeft || me.controlRight) &&
            NoGroundAhead(me, me.controlRight ? 1f : -1f))
        {
            needUp = true; // hop gaps instead of walking into them
        }

        ApplyJumpControls(me, needUp);
        TryOpenDoorForCurrentMovement(me);
    }

    private void TryOpenDoorForCurrentMovement(Player me)
    {
        int direction = me.controlRight && !me.controlLeft ? 1
            : me.controlLeft && !me.controlRight ? -1
            : 0;
        if (direction != 0)
            TryOpenDoorAhead(me, direction);
    }

    private static bool HasSafelyPassedGroundWaypoint(Vector2 feet, Vector2 waypoint,
        int goalDirection, PathStep step) =>
        step.Kind == PathMoveKind.Move && goalDirection != 0 &&
        (feet.X - waypoint.X) * goalDirection > WaypointReachX &&
        System.Math.Abs(feet.Y - waypoint.Y) <= 3f * 16f;

    private static int HorizontalDirection(float fromX, float toX)
    {
        float delta = toX - fromX;
        return delta > 6f ? 1 : delta < -6f ? -1 : 0;
    }

    private static void ApplyHorizontalDirection(Player me, int direction)
    {
        if (direction > 0)
            me.controlRight = true;
        else if (direction < 0)
            me.controlLeft = true;
    }

    /// <summary>True when the immediate front column is a clear, ordinary one-tile walk-up. This
    /// mirrors the pathfinder's existing walk edge and deliberately includes solid blocks and
    /// platforms/stairs which vanilla can step onto without pressing Jump.</summary>
    private static bool CanWalkUpSingleTile(Player me, int direction)
    {
        if (me.velocity.Y != 0f || direction == 0)
            return false;
        int frontX = (int)((direction > 0 ? me.Right.X + 4f : me.Left.X - 4f) / 16f);
        int feetRow = (int)((me.Bottom.Y - 2f) / 16f);
        int bodyColumn = (int)(me.Bottom.X / 16f);
        if (!WorldGen.InWorld(frontX, feetRow, 3))
            return false;
        // SolidOrSlopedTile covers ordinary blocks, half blocks and solid slopes. Hammered
        // platforms are covered by IsPlatform; neither shape should be misclassified as a wall
        // merely because horizontal speed briefly reaches zero during vanilla's step-up physics.
        bool hasStepSupport = WorldGen.SolidOrSlopedTile(frontX, feetRow) ||
            CompanionPathfinder.IsPlatform(frontX, feetRow);
        bool clearAboveStep = !WorldGen.SolidOrSlopedTile(frontX, feetRow - 1) &&
            !CompanionPathfinder.IsPlatform(frontX, feetRow - 1);
        return hasStepSupport && clearAboveStep &&
            CompanionPathfinder.BodyFits(frontX, feetRow - 1) &&
            CompanionPathfinder.BodyFits(bodyColumn, feetRow - 1);
    }

    /// <summary>
    /// Opens only doors immediately in the companion's walking corridor. Trying the movement
    /// direction first makes the door swing away from the companion; the opposite direction
    /// is the vanilla fallback when furniture or another entity blocks that side.
    /// </summary>
    private bool TryOpenDoorAhead(Player me, int direction)
    {
        // Never let a multiplayer client change tiles optimistically. Companions are currently
        // singleplayer-only, but keeping the mutation authority-safe here prevents this helper
        // becoming a desync source if server-hosted companions are added later.
        if (direction == 0 || Main.netMode == NetmodeID.MultiplayerClient)
            return false;

        float frontX = direction > 0 ? me.Right.X : me.Left.X;
        int topRow = (int)((me.Top.Y + 2f) / 16f);
        int bottomRow = (int)((me.Bottom.Y - 2f) / 16f);

        // Three tiles is close enough to feel like ordinary player auto-door opening, while
        // preventing a companion from opening every door merely visible along its route.
        for (int tileDistance = 0; tileDistance <= 2; tileDistance++)
        {
            int tileX = (int)((frontX + direction * (4f + tileDistance * 16f)) / 16f);
            for (int tileY = topRow; tileY <= bottomRow; tileY++)
            {
                if (!WorldGen.InWorld(tileX, tileY, 2))
                    continue;

                Tile tile = Main.tile[tileX, tileY];
                if (!tile.HasTile || tile.IsActuated)
                    continue;

                if (TileLoader.OpenDoorID(tile) >= 0)
                {
                    int openedTileType = TileLoader.OpenDoorID(tile);
                    int openedDirection = direction;
                    bool opened = WorldGen.OpenDoor(tileX, tileY, openedDirection);
                    if (!opened)
                    {
                        openedDirection = -direction;
                        opened = WorldGen.OpenDoor(tileX, tileY, openedDirection);
                    }

                    if (opened)
                    {
                        RememberOpenedDoor(tileX, tileY, openedDirection, openedTileType, tallGate: false);
                        if (Main.netMode == NetmodeID.Server)
                        {
                            NetMessage.SendData(
                                MessageID.ToggleDoorState,
                                -1,
                                -1,
                                null,
                                0,
                                tileX,
                                tileY,
                                openedDirection);
                        }
                        return true;
                    }
                }
                else if (tile.TileType == TileID.TallGateClosed &&
                    WorldGen.ShiftTallGate(tileX, tileY, closing: false))
                {
                    RememberOpenedDoor(tileX, tileY, direction, TileID.TallGateOpen, tallGate: true);
                    if (Main.netMode == NetmodeID.Server)
                    {
                        NetMessage.SendData(
                            MessageID.ToggleDoorState,
                            -1,
                            -1,
                            null,
                            4,
                            tileX,
                            tileY);
                    }
                    return true;
                }
            }
        }

        return false;
    }

    private void RememberOpenedDoor(
        int tileX,
        int tileY,
        int travelDirection,
        int openedTileType,
        bool tallGate)
    {
        Point closedTile = new(tileX, tileY);
        for (int i = 0; i < openedDoors.Count; i++)
        {
            if (openedDoors[i].ClosedTile == closedTile)
            {
                openedDoors[i] = new OpenedDoor(
                    closedTile, travelDirection, openedTileType, tallGate, 0);
                return;
            }
        }
        openedDoors.Add(new OpenedDoor(
            closedTile, travelDirection, openedTileType, tallGate, 0));
    }

    /// <summary>
    /// Finds any active segment of the opened object. TileLoader.CloseDoorID is the inverse of
    /// TileLoader.OpenDoorID and therefore covers properly registered modded doors as well as
    /// vanilla doors; no concrete mod tile IDs are hard-coded here.
    /// </summary>
    private static bool TryFindOpenedDoor(OpenedDoor pending, out Point openTile)
    {
        int firstX = pending.TallGate
            ? pending.ClosedTile.X
            : pending.TravelDirection > 0 ? pending.ClosedTile.X : pending.ClosedTile.X - 1;
        int lastX = pending.TallGate ? firstX : firstX + 1;
        int verticalRadius = pending.TallGate ? 6 : 3;

        for (int x = firstX; x <= lastX; x++)
        {
            for (int y = pending.ClosedTile.Y - verticalRadius;
                y <= pending.ClosedTile.Y + verticalRadius;
                y++)
            {
                if (!WorldGen.InWorld(x, y, 2))
                    continue;

                Tile tile = Main.tile[x, y];
                if (!tile.HasTile || tile.IsActuated)
                    continue;

                if (tile.TileType != pending.OpenedTileType)
                    continue;

                if (!pending.TallGate && TileLoader.CloseDoorID(tile) >= 0)
                {
                    openTile = new Point(x, y);
                    return true;
                }

                if (pending.TallGate && tile.TileType == TileID.TallGateOpen)
                {
                    openTile = new Point(x, y);
                    return true;
                }
            }
        }

        openTile = default;
        return false;
    }

    private void ApplyJumpControls(Player me, bool needUp)
    {
        // Player.ResetEffects clears autoJump every tick, so jumps must be triggered by a
        // fresh press: release for one tick (setting releaseJump), then press again.
        bool hold = false;
        if (needUp)
        {
            if (me.wet)
                hold = me.velocity.Y < 0f || !jumpHeldLastTick; // repeated swim strokes
            else if (me.velocity.Y == 0f)
                hold = !jumpHeldLastTick; // grounded: press on a fresh tick to launch
            else if (me.velocity.Y < 0f)
                hold = true; // rising: keep the jump boosted
            else if (me.wingTime > 0f || (me.mount.Active &&
                (me.mount.CanHover() || (me.mount.CanFly() && me.mount.FlyTime > 0))))
                hold = true; // falling with wings or an equipped flying mount: fly
            else if (me.AnyExtraJumpUsable())
                hold = !jumpHeldLastTick; // release one tick, then press for the extra jump
        }

        me.controlJump = hold;
        jumpHeldLastTick = hold;
    }

    private void TrackProgress(Player me)
    {
        bool tryingToMove = me.controlLeft || me.controlRight || me.controlJump || me.controlDown;
        IsTryingToMove = tryingToMove;
        if (tryingToMove && Vector2.DistanceSquared(me.position, lastPosition) < 1f)
            stuckTicks++;
        else
            stuckTicks = System.Math.Max(0, stuckTicks - 2);
        lastPosition = me.position;
    }

    /// <summary>True when the columns just ahead have no floor within a small step-down.</summary>
    private static bool NoGroundAhead(Player me, float direction)
    {
        int aheadX = (int)((me.Bottom.X + direction * 14f) / 16f);
        int feetRow = (int)((me.Bottom.Y - 1f) / 16f);
        for (int y = feetRow + 1; y <= feetRow + 4; y++)
        {
            if (CompanionPathfinder.IsSolid(aheadX, y) || CompanionPathfinder.IsPlatform(aheadX, y))
                return false;
        }
        return true;
    }

    private static bool StandingOnPlatform(Player me)
    {
        if (me.velocity.Y != 0f)
            return false;

        // The top surface of a hammered platform travels through its tile. Scan the short
        // contact band rather than only Bottom+2, which misses one side of a sloped platform
        // and prevents the normal down-to-drop behavior from ever engaging.
        int firstRow = (int)((me.Bottom.Y - 3f) / 16f) - 1;
        int lastRow = (int)((me.Bottom.Y + 3f) / 16f) + 1;
        int left = (int)(me.position.X / 16f);
        int right = (int)((me.position.X + me.width - 1f) / 16f);
        for (int y = firstRow; y <= lastRow; y++)
        {
            for (int x = left; x <= right; x++)
            {
                if (CompanionPathfinder.IsPlatform(x, y))
                    return true;
            }
        }
        return false;
    }
}
