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

    private List<PathStep> path;
    private int pathIndex;
    private int repathCooldown;
    private Point lastGoalTile = new(-1, -1);
    private int stuckTicks;
    private Vector2 lastPosition;
    private bool jumpHeldLastTick;

    /// <summary>True when the most recent search could not find a route to the goal.</summary>
    public bool LastPathFailed { get; private set; }

    /// <summary>True when the last Navigate call actually pressed movement controls this tick.
    /// The brain uses it to notice a companion that wants to travel but is not getting
    /// anywhere (see its stuck-teleport policy). Idling clears it.</summary>
    public bool IsTryingToMove { get; private set; }

    public void Reset()
    {
        path = null;
        pathIndex = 0;
        repathCooldown = 0;
        lastGoalTile = new Point(-1, -1);
        stuckTicks = 0;
        LastPathFailed = false;
        jumpHeldLastTick = false;
    }

    /// <summary>Writes movement controls onto the player to travel toward the goal.</summary>
    public void Navigate(Player me, Vector2 goalWorld)
    {
        Vector2 toGoal = goalWorld - me.Center;
        if (System.Math.Abs(toGoal.X) > 12f)
            TryOpenDoorAhead(me, System.Math.Sign(toGoal.X));

        // Close targets never need a path.
        if (System.Math.Abs(toGoal.X) < 48f && System.Math.Abs(toGoal.Y) < 56f)
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
            path = CompanionPathfinder.FindPath(startTile, goalTile, MovementCaps.From(me));
            pathIndex = 0;
            stuckTicks = 0;
            lastGoalTile = goalTile;
            LastPathFailed = path is null;
            repathCooldown = path is null ? FailedRepathDelayTicks : RepathIntervalTicks;
        }

        if (path is not null && pathIndex < path.Count)
            FollowPath(me);
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
    }

    private void FollowPath(Player me)
    {
        Vector2 feet = me.Bottom;

        // Skip waypoints that are already reached (checks a couple ahead so shallow
        // zig-zags do not force the companion to touch every node exactly).
        int probes = 0;
        while (pathIndex < path.Count && probes < 3)
        {
            PathStep step = path[pathIndex];
            Vector2 waypoint = step.FeetWorld;
            if (System.Math.Abs(waypoint.X - feet.X) <= WaypointReachX &&
                System.Math.Abs(waypoint.Y - feet.Y) <= WaypointReachY)
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
        Vector2 target = current.FeetWorld;
        float dx = target.X - feet.X;
        float dy = target.Y - feet.Y; // positive: waypoint below the feet

        if (dx > 6f)
            me.controlRight = true;
        else if (dx < -6f)
            me.controlLeft = true;

        if (current.Kind == PathMoveKind.DropThroughPlatform)
        {
            // Hold down while still above the waypoint, easing off right before landing so
            // the companion does not immediately fall through the destination platform too.
            if (dy > 20f)
                me.controlDown = true;
        }
        else if (dy > 40f && StandingOnPlatform(me))
        {
            me.controlDown = true;
        }

        bool needUp = dy < -8f;

        // Same-height waypoints across a gap need a running jump: without this the
        // companion would walk straight off the edge into the hole.
        if (!needUp && me.velocity.Y == 0f && dy < 24f && System.Math.Abs(dx) > 24f &&
            NoGroundAhead(me, System.Math.Sign(dx)))
        {
            needUp = true;
        }

        ApplyJumpControls(me, needUp);
        TryOpenDoorForCurrentMovement(me);
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
        bool blocked = !onHammeredPlatformStair && (me.controlLeft || me.controlRight) && me.velocity.Y == 0f &&
                       System.Math.Abs(me.velocity.X) < 0.35f && stuckTicks > 10;
        bool needUp = toGoal.Y < -48f || blocked;

        if (toGoal.Y > 48f && StandingOnPlatform(me) && System.Math.Abs(toGoal.X) < 160f)
            me.controlDown = true;

        if (!needUp && me.velocity.Y == 0f && toGoal.Y < 32f && (me.controlLeft || me.controlRight) &&
            NoGroundAhead(me, me.controlRight ? 1f : -1f))
        {
            needUp = true; // hop gaps instead of walking into them
        }

        ApplyJumpControls(me, needUp);
        TryOpenDoorForCurrentMovement(me);
    }

    private static void TryOpenDoorForCurrentMovement(Player me)
    {
        int direction = me.controlRight && !me.controlLeft ? 1
            : me.controlLeft && !me.controlRight ? -1
            : 0;
        if (direction != 0)
            TryOpenDoorAhead(me, direction);
    }

    /// <summary>
    /// Opens only doors immediately in the companion's walking corridor. Trying the movement
    /// direction first makes the door swing away from the companion; the opposite direction
    /// is the vanilla fallback when furniture or another entity blocks that side.
    /// </summary>
    private static bool TryOpenDoorAhead(Player me, int direction)
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
                    int openedDirection = direction;
                    bool opened = WorldGen.OpenDoor(tileX, tileY, openedDirection);
                    if (!opened)
                    {
                        openedDirection = -direction;
                        opened = WorldGen.OpenDoor(tileX, tileY, openedDirection);
                    }

                    if (opened)
                    {
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
            else if (me.wingTime > 0f)
                hold = true; // falling with wings: fly
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
