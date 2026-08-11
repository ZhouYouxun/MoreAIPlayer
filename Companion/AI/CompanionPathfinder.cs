using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace MoreAIPlayer.Companion.AI;

public enum PathMoveKind : byte
{
    /// <summary>Walk, jump or fall toward the waypoint.</summary>
    Move,
    /// <summary>Hold down to fall through the platform under the feet.</summary>
    DropThroughPlatform,
    /// <summary>Step onto a rope column and grab it (align, then press up/down).</summary>
    EnterRope,
    /// <summary>Climb up the rope toward the waypoint (press up).</summary>
    ClimbRopeUp,
    /// <summary>Climb down the rope toward the waypoint (press down).</summary>
    ClimbRopeDown,
    /// <summary>Leave the rope onto adjacent standable ground/platform.</summary>
    ExitRope
}

public readonly struct PathStep
{
    /// <summary>Tile column the feet should be centered on.</summary>
    public readonly int X;
    /// <summary>Tile row of the feet (the support is at Y + 1).</summary>
    public readonly int Y;
    public readonly PathMoveKind Kind;

    public PathStep(int x, int y, PathMoveKind kind)
    {
        X = x;
        Y = y;
        Kind = kind;
    }

    public Vector2 FeetWorld => new(X * 16f + 8f, Y * 16f + 16f);

    /// <summary>Center of this tile — the point a climbing companion aligns to on a rope column.</summary>
    public Vector2 TileCenterWorld => new(X * 16f + 8f, Y * 16f + 8f);

    /// <summary>True when reaching this waypoint means being on / grabbing a rope.</summary>
    public bool IsRopeMove => Kind is PathMoveKind.EnterRope or PathMoveKind.ClimbRopeUp or PathMoveKind.ClimbRopeDown;
}

/// <summary>Movement capabilities used to generate jump/flight edges.</summary>
public readonly struct MovementCaps
{
    /// <summary>Maximum tiles the companion can rise in one air segment (jump + extra jumps + wings).</summary>
    public readonly int MaxRise;
    /// <summary>Maximum tiles of horizontal gap the companion can clear.</summary>
    public readonly int MaxGap;

    public MovementCaps(int maxRise, int maxGap)
    {
        MaxRise = System.Math.Max(3, maxRise);
        MaxGap = System.Math.Max(2, maxGap);
    }

    public static MovementCaps From(Player player)
    {
        int jumpTiles = 6 + (int)(player.jumpSpeedBoost * 1.2f);
        int extraJumpTiles = player.AnyExtraJumpUsable() ? 6 : 0;
        int wingTiles = player.wingTimeMax > 0 ? System.Math.Clamp(player.wingTimeMax / 8, 6, 24) : 0;
        int maxRise = System.Math.Clamp(jumpTiles + extraJumpTiles + wingTiles, 6, 30);
        int maxGap = wingTiles > 0 ? 8 : extraJumpTiles > 0 ? 6 : 5;
        return new MovementCaps(maxRise, maxGap);
    }
}

/// <summary>
/// Tile-grid A* for player-sized bodies (2x3 tiles of clearance approximated as a
/// 1-column corridor that must not be pinched from both sides). Edges model walking,
/// ledge jumps and flight, falling, gap jumps, and dropping through platforms.
/// </summary>
public static class CompanionPathfinder
{
    private const int MaxExpansions = 3500;
    private const int MaxFallTiles = 45;
    private const int SearchPadding = 40;

    private struct NodeRecord
    {
        public float GCost;
        public long Parent;
        public PathMoveKind ArrivalKind;
        public bool Closed;
    }

    public static List<PathStep> FindPath(Point start, Point goal, MovementCaps caps,
        bool allowPlatformDrop = false, int maxDropTiles = NavigationRequest.DefaultSafeDropTiles)
    {
        if (!TrySnapToStand(ref start, 30) || !TrySnapToStand(ref goal, 25))
            return null;

        if (start == goal)
            return [];

        int minX = System.Math.Min(start.X, goal.X) - SearchPadding;
        int maxX = System.Math.Max(start.X, goal.X) + SearchPadding;
        int minY = System.Math.Min(start.Y, goal.Y) - SearchPadding;
        int maxY = System.Math.Max(start.Y, goal.Y) + SearchPadding;

        Dictionary<long, NodeRecord> records = [];
        PriorityQueue<long, float> open = new();

        long startKey = Key(start.X, start.Y);
        records[startKey] = new NodeRecord { GCost = 0f, Parent = -1, ArrivalKind = PathMoveKind.Move };
        open.Enqueue(startKey, Heuristic(start.X, start.Y, goal));

        int expansions = 0;
        List<(int X, int Y, float Cost, PathMoveKind Kind)> neighbors = new(64);

        while (open.Count > 0 && expansions < MaxExpansions)
        {
            long currentKey = open.Dequeue();
            NodeRecord current = records[currentKey];
            if (current.Closed)
                continue;
            current.Closed = true;
            records[currentKey] = current;
            expansions++;

            int cx = UnpackX(currentKey);
            int cy = UnpackY(currentKey);

            if (System.Math.Abs(cx - goal.X) <= 1 && System.Math.Abs(cy - goal.Y) <= 2)
                return Reconstruct(records, currentKey);

            neighbors.Clear();
            GenerateNeighbors(cx, cy, caps, allowPlatformDrop, maxDropTiles, neighbors);

            foreach ((int nx, int ny, float cost, PathMoveKind kind) in neighbors)
            {
                if (nx < minX || nx > maxX || ny < minY || ny > maxY)
                    continue;

                long neighborKey = Key(nx, ny);
                float g = current.GCost + cost;
                if (records.TryGetValue(neighborKey, out NodeRecord existing) && (existing.Closed || existing.GCost <= g))
                    continue;

                records[neighborKey] = new NodeRecord { GCost = g, Parent = currentKey, ArrivalKind = kind };
                open.Enqueue(neighborKey, g + Heuristic(nx, ny, goal));
            }
        }

        return null;
    }

    private static List<PathStep> Reconstruct(Dictionary<long, NodeRecord> records, long endKey)
    {
        // Each step's Kind describes how that waypoint is reached from the previous one,
        // so the follower reads the kind of the waypoint it is currently approaching.
        List<PathStep> path = [];
        long key = endKey;
        while (key != -1)
        {
            NodeRecord record = records[key];
            path.Add(new PathStep(UnpackX(key), UnpackY(key), record.ArrivalKind));
            key = record.Parent;
        }
        path.Reverse();
        return path;
    }

    private static void GenerateNeighbors(int x, int y, MovementCaps caps,
        bool allowPlatformDrop, int maxDropTiles, List<(int, int, float, PathMoveKind)> result)
    {
        // Hanging on a rope is its own movement mode: from here the only real moves are climbing
        // the rope up/down and stepping off onto adjacent ground. Ground walk/jump edges do not
        // apply mid-air, so a rope node never mixes the two.
        if (CompanionRopeRegistry.IsRope(x, y))
        {
            GenerateRopeNeighbors(x, y, result);
            return;
        }

        // Walking (includes one-tile step-ups and small step-downs).
        for (int direction = -1; direction <= 1; direction += 2)
        {
            int nx = x + direction;
            foreach (int ny in WalkCandidateRows(y))
            {
                if (!IsStandable(nx, ny))
                    continue;
                if (ny < y && !BodyFits(x, ny))
                    continue; // step-up also needs headroom in the current column
                result.Add((nx, ny, 1f + 0.4f * System.Math.Abs(ny - y), PathMoveKind.Move));
                break;
            }
        }

        // Falling off a ledge (sideways into open air, landing below).
        for (int direction = -1; direction <= 1; direction += 2)
        {
            int nx = x + direction;
            if (!BodyFits(nx, y) || HasSupport(nx, y))
                continue;

            for (int ny = y + 1; ny <= y + MaxFallTiles; ny++)
            {
                if (!BodyFits(nx, ny))
                    break;
                if (HasSupport(nx, ny))
                {
                    result.Add((nx, ny, 1.2f + 0.25f * (ny - y), PathMoveKind.Move));
                    break;
                }
            }
        }

        // Dropping straight down through a platform — only when this travel order explicitly
        // permits it, and only within the requested safe-drop distance. A goal that merely sits
        // lower no longer implies permission to fall through the floor; that decision is made by
        // the caller's NavigationRequest and re-validated at execution time by
        // CompanionPlatformDrop.TryApprove.
        if (allowPlatformDrop && SupportIsDroppablePlatform(x, y))
        {
            int dropLimit = System.Math.Min(MaxFallTiles, System.Math.Max(2, maxDropTiles));
            for (int ny = y + 1; ny <= y + dropLimit; ny++)
            {
                if (IsSolid(x, ny) || IsSolid(x - 1, ny) && IsSolid(x + 1, ny))
                    break;
                if (ny > y + 1 && HasSupport(x, ny) && BodyFits(x, ny))
                {
                    result.Add((x, ny, 1f + 0.2f * (ny - y), PathMoveKind.DropThroughPlatform));
                    break;
                }
            }
        }

        // Stepping onto a rope column within reach: the rope becomes a cheap vertical corridor.
        AddEnterRopeEdges(x, y, result);

        // Jumping (or flying) up to ledges. The vertical corridor must stay clear as the
        // body rises; platforms are passable from below so they do not block the corridor.
        bool corridorClear = true;
        for (int rise = 1; rise <= caps.MaxRise && corridorClear; rise++)
        {
            int ny = y - rise;
            corridorClear = RowPassable(x, ny - 2);
            if (!corridorClear)
                break;

            for (int dx = -2; dx <= 2; dx++)
            {
                int nx = x + dx;
                if (nx == x && rise == 1)
                    continue; // covered by walking
                if (!IsStandable(nx, ny))
                    continue;
                if (dx != 0 && !BodyFits(x + System.Math.Sign(dx), ny))
                    continue; // need room to shift over at the apex

                float cost = 2f + 0.8f * rise + 0.4f * System.Math.Abs(dx);
                result.Add((nx, ny, cost, PathMoveKind.Move));
            }
        }

        // Horizontal gap jumps at roughly the same height.
        for (int direction = -1; direction <= 1; direction += 2)
        {
            for (int gap = 2; gap <= caps.MaxGap; gap++)
            {
                int nx = x + direction * gap;
                bool corridor = true;
                for (int cx = x + direction; cx != nx; cx += direction)
                {
                    if (!BodyFits(cx, y - 1))
                    {
                        corridor = false;
                        break;
                    }
                }
                if (!corridor)
                    break;

                bool landed = false;
                for (int ny = y - 1; ny <= y + 3; ny++)
                {
                    if (IsStandable(nx, ny))
                    {
                        result.Add((nx, ny, 1.3f * gap + 0.3f * System.Math.Abs(ny - y), PathMoveKind.Move));
                        landed = true;
                        break;
                    }
                }
                if (landed)
                    break;
            }
        }
    }

    /// <summary>
    /// Edges out of a rope node: climb up, climb down, and step off onto adjacent standable ground
    /// or a platform. Vanilla owns the climbing physics; the cost model just keeps ropes cheaper
    /// than repeatedly jumping a wall yet dearer than flat walking, so a distant rope does not lure
    /// the search into a huge detour.
    /// </summary>
    private static void GenerateRopeNeighbors(int x, int y, List<(int, int, float, PathMoveKind)> result)
    {
        const float climbCost = 1.35f;
        const float exitCost = 1.8f;

        if (CompanionRopeRegistry.IsRope(x, y - 1) && BodyFits(x, y - 1))
            result.Add((x, y - 1, climbCost, PathMoveKind.ClimbRopeUp));

        if (CompanionRopeRegistry.IsRope(x, y + 1) && BodyFits(x, y + 1))
            result.Add((x, y + 1, climbCost, PathMoveKind.ClimbRopeDown));

        // Step off sideways onto adjacent ground/platform at a nearby height.
        for (int direction = -1; direction <= 1; direction += 2)
        {
            int nx = x + direction;
            foreach (int ny in WalkCandidateRows(y))
            {
                if (IsStandable(nx, ny))
                {
                    result.Add((nx, ny, exitCost + 0.3f * System.Math.Abs(ny - y), PathMoveKind.ExitRope));
                    break;
                }
            }
        }

        // Step off straight down when the rope ends just above solid ground.
        if (!CompanionRopeRegistry.IsRope(x, y + 1))
        {
            for (int ny = y + 1; ny <= y + 3; ny++)
            {
                if (IsSolid(x, ny))
                    break;
                if (IsStandable(x, ny))
                {
                    result.Add((x, ny, exitCost + 0.3f * (ny - y), PathMoveKind.ExitRope));
                    break;
                }
            }
        }
    }

    /// <summary>
    /// From a standable ground node, an edge onto a rope column whose body the companion could
    /// grab: the same column or one to either side, with a rope tile somewhere in the body's
    /// vertical span. The rope node picked is the rope row nearest the current feet.
    /// </summary>
    private static void AddEnterRopeEdges(int x, int y, List<(int, int, float, PathMoveKind)> result)
    {
        const float enterCost = 2.2f;
        for (int dx = -1; dx <= 1; dx++)
        {
            int nx = x + dx;
            int bestRow = int.MinValue;
            // Prefer a rope tile within the body span (rows y-2..y+1) closest to the feet.
            for (int ny = y - 2; ny <= y + 1; ny++)
            {
                if (CompanionRopeRegistry.IsRope(nx, ny) && BodyFits(nx, ny) &&
                    (bestRow == int.MinValue || System.Math.Abs(ny - y) < System.Math.Abs(bestRow - y)))
                {
                    bestRow = ny;
                }
            }
            if (bestRow != int.MinValue)
                result.Add((nx, bestRow, enterCost + System.Math.Abs(dx) * 0.4f, PathMoveKind.EnterRope));
        }
    }

    private static IEnumerable<int> WalkCandidateRows(int y)
    {
        yield return y;
        yield return y - 1;
        yield return y + 1;
        yield return y + 2;
    }

    private static float Heuristic(int x, int y, Point goal)
    {
        float dx = x - goal.X;
        float dy = y - goal.Y;
        return (float)System.Math.Sqrt(dx * dx + dy * dy) * 1.05f;
    }

    private static long Key(int x, int y) => ((long)x << 32) | (uint)y;

    private static int UnpackX(long key) => (int)(key >> 32);

    private static int UnpackY(long key) => (int)key;

    public static bool TrySnapToStand(ref Point tile, int maxScanDown)
    {
        int x = tile.X;
        for (int y = tile.Y; y <= tile.Y + maxScanDown; y++)
        {
            if (IsPathNode(x, y))
            {
                tile = new Point(x, y);
                return true;
            }
        }

        // Also try snapping upward slightly: the reference point may be inside ground.
        for (int y = tile.Y - 1; y >= tile.Y - 6; y--)
        {
            if (IsPathNode(x, y))
            {
                tile = new Point(x, y);
                return true;
            }
        }
        return false;
    }

    /// <summary>A tile the companion can occupy as a path node: standing on ground/platform, or
    /// hanging on a rope column with room for the body.</summary>
    private static bool IsPathNode(int x, int y) =>
        IsStandable(x, y) || (CompanionRopeRegistry.IsRope(x, y) && BodyFits(x, y));

    public static bool IsStandable(int x, int y) => BodyFits(x, y) && HasSupport(x, y);

    /// <summary>Body occupies rows y-2..y. A row fits if the center column is passable and the
    /// body is not pinched by solids on both sides (players are wider than one tile).</summary>
    public static bool BodyFits(int x, int y)
    {
        for (int row = y - 2; row <= y; row++)
        {
            if (!RowPassable(x, row))
                return false;
        }
        return true;
    }

    private static bool RowPassable(int x, int y)
    {
        if (IsSolid(x, y))
            return false;
        return !(IsSolid(x - 1, y) && IsSolid(x + 1, y));
    }

    public static bool HasSupport(int x, int y)
    {
        if (IsSolidOrPlatform(x, y + 1))
            return true;
        // A body wider than one tile can straddle a one-tile hole.
        return IsSolidOrPlatform(x - 1, y + 1) && IsSolidOrPlatform(x + 1, y + 1);
    }

    public static bool SupportIsDroppablePlatform(int x, int y)
    {
        if (!IsPlatform(x, y + 1))
            return false;
        return !IsSolid(x - 1, y + 1) && !IsSolid(x + 1, y + 1);
    }

    public static bool IsSolid(int x, int y)
    {
        if (!WorldGen.InWorld(x, y, 2))
            return true;
        Tile tile = Main.tile[x, y];
        // Plan through doors that the navigator can actually open. Locked vanilla doors keep
        // OpenDoorID == -1 and remain solid, so the pathfinder will not run into them forever.
        if (!tile.IsActuated &&
            (TileLoader.OpenDoorID(tile) >= 0 || tile.TileType == TileID.TallGateClosed))
        {
            return false;
        }
        return tile.HasTile && !tile.IsActuated && Main.tileSolid[tile.TileType] && !Main.tileSolidTop[tile.TileType];
    }

    public static bool IsPlatform(int x, int y)
    {
        if (!WorldGen.InWorld(x, y, 2))
            return false;
        Tile tile = Main.tile[x, y];
        return tile.HasTile && !tile.IsActuated &&
               (TileID.Sets.Platforms[tile.TileType] || Main.tileSolidTop[tile.TileType] && Main.tileSolid[tile.TileType]);
    }

    /// <summary>
    /// Hammering a platform can turn it into a one-way sloped step. It is still a platform for
    /// pathfinding purposes, but it must not be treated like a wall by the movement fallback:
    /// vanilla player collision already walks smoothly along this shape when fed horizontal
    /// input alone.
    /// </summary>
    public static bool IsHammeredPlatform(int x, int y)
    {
        if (!IsPlatform(x, y))
            return false;
        return Main.tile[x, y].Slope != SlopeType.Solid;
    }

    /// <summary>Checks the actual contact band plus one tile in the intended walking direction.
    /// A slope shifts the contact height inside the tile, so a single fixed "feet row" is not
    /// reliable here.</summary>
    public static bool IsNearHammeredPlatformStair(Player player, int direction)
    {
        int left = (int)(player.Left.X / 16f) - 1;
        int right = (int)((player.Right.X - 1f) / 16f) + 1;
        if (direction > 0)
            right += 1;
        else if (direction < 0)
            left -= 1;

        int firstRow = (int)((player.Bottom.Y - 3f) / 16f) - 1;
        int lastRow = (int)((player.Bottom.Y + 3f) / 16f) + 1;
        for (int x = left; x <= right; x++)
        {
            for (int y = firstRow; y <= lastRow; y++)
            {
                if (IsHammeredPlatform(x, y))
                    return true;
            }
        }
        return false;
    }

    private static bool IsSolidOrPlatform(int x, int y) => IsSolid(x, y) || IsPlatform(x, y);
}
