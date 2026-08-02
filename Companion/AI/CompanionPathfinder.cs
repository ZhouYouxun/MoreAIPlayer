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
    DropThroughPlatform
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

    public static List<PathStep> FindPath(Point start, Point goal, MovementCaps caps)
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
            GenerateNeighbors(cx, cy, caps, neighbors);

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

    private static void GenerateNeighbors(int x, int y, MovementCaps caps, List<(int, int, float, PathMoveKind)> result)
    {
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

        // Dropping straight down through a platform.
        if (SupportIsDroppablePlatform(x, y))
        {
            for (int ny = y + 1; ny <= y + MaxFallTiles; ny++)
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
            if (IsStandable(x, y))
            {
                tile = new Point(x, y);
                return true;
            }
        }

        // Also try snapping upward slightly: the reference point may be inside ground.
        for (int y = tile.Y - 1; y >= tile.Y - 6; y--)
        {
            if (IsStandable(x, y))
            {
                tile = new Point(x, y);
                return true;
            }
        }
        return false;
    }

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

    private static bool IsSolidOrPlatform(int x, int y) => IsSolid(x, y) || IsPlatform(x, y);
}
