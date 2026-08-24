using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace MoreAIPlayer.Companion.Automation;

/// <summary>Frame-sliced index of campaign-relevant world tiles. The automation player is allowed
/// perfect information, but a 20-million-tile world must not freeze for one giant scan.</summary>
public sealed class AutoWorldIndexSystem : ModSystem
{
    private const int TilesPerTick = 30000;
    private const int LandmarkStride = 24;
    private static readonly Dictionary<int, List<Point>> Tiles = [];
    private static int scanIndex;
    private static long completedAtTick;

    public static bool ScanComplete { get; private set; }

    public override void OnWorldLoad() => RestartScan();

    public override void OnWorldUnload()
    {
        Tiles.Clear();
        scanIndex = 0;
        ScanComplete = false;
    }

    public override void PostUpdateWorld()
    {
        if (Main.netMode == NetmodeID.MultiplayerClient || Main.maxTilesX <= 0 || Main.maxTilesY <= 0)
            return;

        // Hardmode conversion and plant growth add relevant tiles after world generation. A slow
        // periodic refresh retains perfect information without rescanning every frame.
        if (ScanComplete && (long)Main.GameUpdateCount - completedAtTick >= 60L * 60L * 5L)
            RestartScan();
        if (ScanComplete)
            return;

        int total = Main.maxTilesX * Main.maxTilesY;
        int end = System.Math.Min(total, scanIndex + TilesPerTick);
        for (; scanIndex < end; scanIndex++)
        {
            int x = scanIndex % Main.maxTilesX;
            int y = scanIndex / Main.maxTilesX;
            Tile tile = Main.tile[x, y];
            if (!tile.HasTile || !IsIndexed(tile.TileType, x, y))
                continue;
            if (!Tiles.TryGetValue(tile.TileType, out List<Point> points))
            {
                points = [];
                Tiles[tile.TileType] = points;
            }
            points.Add(new Point(x, y));
        }

        if (scanIndex >= total)
        {
            ScanComplete = true;
            completedAtTick = (long)Main.GameUpdateCount;
        }
    }

    public static void RestartScan()
    {
        Tiles.Clear();
        scanIndex = 0;
        ScanComplete = false;
        completedAtTick = 0;
    }

    public static Point? FindNearest(Vector2 center, int[] tileTypes,
        System.Func<Point, bool> extra = null)
    {
        Point? best = null;
        float bestDistance = float.MaxValue;
        foreach (int type in tileTypes)
        {
            if (!Tiles.TryGetValue(type, out List<Point> points))
                continue;
            foreach (Point point in points)
            {
                if (!LiveType(point, type) || extra is not null && !extra(point))
                    continue;
                float distance = Vector2.DistanceSquared(center,
                    new Vector2(point.X * 16f + 8f, point.Y * 16f + 8f));
                if (distance >= bestDistance)
                    continue;
                bestDistance = distance;
                best = point;
            }
        }
        return best;
    }

    public static Point? FindJungleLandmark(Vector2 center) =>
        FindNearest(center, [TileID.JungleGrass]);

    public static Point? FindTempleLandmark(Vector2 center) =>
        FindNearest(center, [TileID.LihzahrdAltar]) ?? FindNearest(center, [TileID.LihzahrdBrick]);

    public static Point? FindEvilLandmark(Vector2 center) =>
        FindNearest(center, WorldGen.crimson ? [TileID.CrimsonGrass] : [TileID.CorruptGrass]);

    private static bool LiveType(Point point, int type) =>
        WorldGen.InWorld(point.X, point.Y, 4) && Main.tile[point.X, point.Y].HasTile &&
        Main.tile[point.X, point.Y].TileType == type;

    private static bool IsIndexed(int type, int x, int y)
    {
        if (type is TileID.Heart or TileID.LifeFruit or TileID.Hellstone or TileID.Obsidian or
            TileID.Hellforge or
            TileID.LihzahrdAltar or
            TileID.DemonAltar or
            TileID.ShadowOrbs or
            TileID.Cobalt or TileID.Palladium or TileID.Mythril or TileID.Orichalcum or
            TileID.Adamantite or TileID.Titanium or TileID.Chlorophyte or
            TileID.Demonite or TileID.Crimtane or TileID.Meteorite)
            return true;

        // Jungle/temple tiles are landmarks, not mining targets. Sampling retains complete biome
        // coverage while avoiding millions of redundant points.
        if (type is TileID.JungleGrass or TileID.LihzahrdBrick or TileID.CorruptGrass or TileID.CrimsonGrass)
            return (x + y) % LandmarkStride == 0;
        return false;
    }
}
