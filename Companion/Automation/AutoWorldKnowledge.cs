using System;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;

namespace MoreAIPlayer.Companion.Automation;

/// <summary>
/// Bounded, local world reads for the automation companion's Phase 2 resource work: the nearest
/// tree, the nearest mineable tile of a kind, and a flat patch of ground to place a station on.
/// These are deliberately small radius scans around the companion — the frame-sliced whole-world
/// scan and interest-point cache described for later phases live in a separate system.
///
/// Everything here re-reads the live tiles; nothing authorises acting on a remembered tile that no
/// longer exists (design §6.2).
/// </summary>
public static class AutoWorldKnowledge
{
    /// <summary>Ore tiles a pre-hardmode pickaxe should legally target for the early metal chain.
    /// Evil ores, meteorite and hellstone are deliberately excluded — they gate on pick power and
    /// bosses handled in later phases.</summary>
    public static readonly int[] BasicOreTiles =
    [
        TileID.Copper, TileID.Tin, TileID.Iron, TileID.Lead,
        TileID.Silver, TileID.Tungsten, TileID.Gold, TileID.Platinum
    ];

    /// <summary>Finds the nearest actual tree base, never the nearest branch/canopy tile. A base is
    /// an axe tile resting directly on solid ground. Locking this coordinate makes one ordinary axe
    /// hit fell the whole vanilla tree instead of shaving off whichever irregular branch happened
    /// to be closest to the player's tile-reach area.</summary>
    public static Point? FindNearestTreeBase(Vector2 center, int radius,
        Func<Point, bool> accept = null)
    {
        return FindNearest(center, radius, (x, y, tile) => IsTreeBase(x, y, tile), accept);
    }

    public static bool IsChoppableTreeBase(Point point)
    {
        if (!WorldGen.InWorld(point.X, point.Y, 4))
            return false;
        return IsTreeBase(point.X, point.Y, Main.tile[point.X, point.Y]);
    }

    public static bool IsChoppableTreeTile(Point point)
    {
        if (!WorldGen.InWorld(point.X, point.Y, 4))
            return false;
        Tile tile = Main.tile[point.X, point.Y];
        return tile.HasTile && !tile.IsActuated && tile.TileType < Main.tileAxe.Length &&
            Main.tileAxe[tile.TileType] && WorldGen.CanKillTile(point.X, point.Y);
    }

    /// <summary>Returns a collision-free standing point beside a tree base. Candidates use real
    /// ground and the live player's dimensions, so navigation aims beside the trunk rather than at
    /// the solid tree tile itself.</summary>
    public static Vector2? FindTreeChopPosition(Player me, Item axe, Point treeBase)
    {
        Vector2? best = null;
        float bestDistance = float.MaxValue;
        int preferredSide = me.Center.X <= treeBase.X * 16f + 8f ? -1 : 1;
        for (int horizontal = 2; horizontal <= 4; horizontal++)
        for (int sideIndex = 0; sideIndex < 2; sideIndex++)
        {
            int side = sideIndex == 0 ? preferredSide : -preferredSide;
            int x = treeBase.X + side * horizontal;
            for (int floorY = treeBase.Y - 3; floorY <= treeBase.Y + 5; floorY++)
            {
                if (!WorldGen.InWorld(x, floorY, 8))
                    continue;
                Tile floor = Main.tile[x, floorY];
                bool supportsPlayer = floor.HasTile && !floor.IsActuated &&
                    floor.TileType < Main.tileSolid.Length && Main.tileSolid[floor.TileType];
                if (!supportsPlayer)
                    continue;

                Vector2 position = new(x * 16f + 8f - me.width * 0.5f,
                    floorY * 16f - me.height);
                if (Collision.SolidCollision(position, me.width, me.height) ||
                    !TileInReachFrom(position, me.width, me.height, axe, treeBase))
                    continue;
                Vector2 center = position + new Vector2(me.width * 0.5f, me.height * 0.5f);
                float distance = Vector2.DistanceSquared(me.Center, center);
                if (distance >= bestDistance)
                    continue;
                bestDistance = distance;
                best = center;
            }
        }
        return best;
    }

    private static bool TileInReachFrom(Vector2 position, int width, int height, Item tool,
        Point tile) =>
        position.X / 16f - Player.tileRangeX - tool.tileBoost <= tile.X &&
        (position.X + width) / 16f + Player.tileRangeX + tool.tileBoost - 1f >= tile.X &&
        position.Y / 16f - Player.tileRangeY - tool.tileBoost <= tile.Y &&
        (position.Y + height) / 16f + Player.tileRangeY + tool.tileBoost - 2f >= tile.Y;

    private static bool IsTreeBase(int x, int y, Tile tile)
    {
        if (!tile.HasTile || tile.IsActuated || tile.TileType >= Main.tileAxe.Length ||
            !Main.tileAxe[tile.TileType] || !WorldGen.CanKillTile(x, y) ||
            !WorldGen.InWorld(x, y + 1, 4))
            return false;
        Tile ground = Main.tile[x, y + 1];
        return ground.HasTile && !ground.IsActuated && ground.TileType < Main.tileSolid.Length &&
            Main.tileSolid[ground.TileType] && !Main.tileSolidTop[ground.TileType];
    }

    /// <summary>Nearest tile of the given type within radius that can be legally broken, or null.</summary>
    public static Point? FindNearestMineable(Vector2 center, int radius, int tileType,
        Func<Point, bool> accept = null)
    {
        return FindNearest(center, radius, (x, y, tile) =>
            tile.TileType == tileType && !Main.tileFrameImportant[tile.TileType] && WorldGen.CanKillTile(x, y),
            accept);
    }

    /// <summary>Nearest tile of any of the given types within radius that can be legally broken.</summary>
    public static Point? FindNearestMineableOfAny(Vector2 center, int radius, int[] tileTypes,
        Func<Point, bool> accept = null)
    {
        return FindNearest(center, radius, (x, y, tile) =>
        {
            if (Main.tileFrameImportant[tile.TileType] || !WorldGen.CanKillTile(x, y))
                return false;
            foreach (int type in tileTypes)
            {
                if (tile.TileType == type)
                    return true;
            }
            return false;
        }, accept);
    }

    /// <summary>Nearest tile of an exact type within radius (frame-important allowed, e.g. a placed
    /// crafting station), or null. Used to find and revalidate the companion's own stations.</summary>
    public static Point? FindNearestTileOfType(Vector2 center, int radius, int tileType,
        Func<Point, bool> accept = null)
    {
        return FindNearest(center, radius, (x, y, tile) => tile.TileType == tileType, accept);
    }

    private static Point? FindNearest(Vector2 center, int radius, Func<int, int, Tile, bool> match,
        Func<Point, bool> accept = null)
    {
        Point origin = center.ToTileCoordinates();
        Point best = default;
        bool found = false;
        float bestDistance = float.MaxValue;

        for (int x = origin.X - radius; x <= origin.X + radius; x++)
        {
            for (int y = origin.Y - radius; y <= origin.Y + radius; y++)
            {
                if (!WorldGen.InWorld(x, y, 4))
                    continue;
                Tile tile = Main.tile[x, y];
                Point point = new(x, y);
                if (!tile.HasTile || !match(x, y, tile) || (accept is not null && !accept(point)))
                    continue;

                float distance = Vector2.DistanceSquared(center, new Vector2(x * 16f + 8f, y * 16f + 8f));
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = new Point(x, y);
                    found = true;
                }
            }
        }
        return found ? best : null;
    }

    public static bool IsMineableTile(Point point, int tileType)
    {
        if (!WorldGen.InWorld(point.X, point.Y, 4))
            return false;
        Tile tile = Main.tile[point.X, point.Y];
        return tile.HasTile && tile.TileType == tileType && !Main.tileFrameImportant[tile.TileType] &&
            WorldGen.CanKillTile(point.X, point.Y);
    }

    public static bool IsMineableTileOfAny(Point point, int[] tileTypes)
    {
        if (!WorldGen.InWorld(point.X, point.Y, 4))
            return false;
        Tile tile = Main.tile[point.X, point.Y];
        if (!tile.HasTile || Main.tileFrameImportant[tile.TileType] ||
            !WorldGen.CanKillTile(point.X, point.Y))
            return false;
        foreach (int type in tileTypes)
        {
            if (tile.TileType == type)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Finds a flat, clear patch of ground near the companion wide enough to place a station of
    /// <paramref name="width"/> tiles: <paramref name="width"/> consecutive columns whose surface
    /// row is air, has a solid tile directly below and at least two tiles of headroom above.
    /// Returns the left surface tile to aim placement at, or null if no clear spot is nearby.
    /// </summary>
    public static Point? FindStationSpot(Player me, int width, int searchTiles = 8)
    {
        Point feet = new((int)(me.Bottom.X / 16f), (int)(me.Bottom.Y / 16f));
        for (int dx = 0; dx <= searchTiles; dx++)
        {
            foreach (int sign in dx == 0 ? new[] { 1 } : new[] { 1, -1 })
            {
                int startX = feet.X + dx * sign;
                if (IsFlatGround(startX, feet.Y, width, out int surfaceRow))
                    return new Point(startX, surfaceRow);
            }
        }
        return null;
    }

    private static bool IsFlatGround(int startX, int nearRow, int width, out int surfaceRow)
    {
        surfaceRow = -1;
        // Look a little up and down from the companion's feet for a consistent surface row.
        for (int row = nearRow - 2; row <= nearRow + 3; row++)
        {
            if (RowIsClearWithSupport(startX, row, width))
            {
                surfaceRow = row;
                return true;
            }
        }
        return false;
    }

    private static bool RowIsClearWithSupport(int startX, int row, int width)
    {
        for (int x = startX; x < startX + width; x++)
        {
            if (!WorldGen.InWorld(x, row, 5))
                return false;

            Tile surface = Main.tile[x, row];
            Tile head1 = Main.tile[x, row - 1];
            Tile head2 = Main.tile[x, row - 2];
            Tile below = Main.tile[x, row + 1];

            bool surfaceClear = !surface.HasTile || !Main.tileSolid[surface.TileType];
            bool headClear = (!head1.HasTile || !Main.tileSolid[head1.TileType]) &&
                             (!head2.HasTile || !Main.tileSolid[head2.TileType]);
            bool supported = below.HasTile && Main.tileSolid[below.TileType] && !Main.tileSolidTop[below.TileType];
            if (!surfaceClear || !headClear || !supported)
                return false;
        }
        return true;
    }
}
