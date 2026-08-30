using Terraria;
using Terraria.ID;

namespace MoreAIPlayer.Companion.Automation;

/// <summary>Shared topology facts for ordinary and inverted vanilla seeds. Remix/Get fixed boi
/// worlds begin and develop around a deep habitable band; treating worldSurface as the universal
/// floor sends exploration, housing and arenas thousands of tiles away from the actual start.</summary>
public static class AutoWorldTopology
{
    public static bool UsesInvertedProgression => Main.remixWorld || Main.zenithWorld;

    public static int PreferredHabitableRow => UsesInvertedProgression
        ? System.Math.Clamp(Main.spawnTileY, 80, Main.maxTilesY - 120)
        : System.Math.Clamp((int)Main.worldSurface, 80, Main.maxTilesY - 120);

    /// <summary>Finds a solid floor with three clear body tiles, preferring rows nearest the
    /// world's actual progression band. The scan is column-local and bounded.</summary>
    public static int? FindStandingFloorRow(int x, int preferredRow, int radius)
    {
        if (Main.maxTilesX <= 0 || Main.maxTilesY <= 0)
            return null;
        x = System.Math.Clamp(x, 10, Main.maxTilesX - 11);
        int preferred = System.Math.Clamp(preferredRow, 12, Main.maxTilesY - 12);
        int boundedRadius = System.Math.Clamp(radius, 1, Main.maxTilesY);
        for (int offset = 0; offset <= boundedRadius; offset++)
        {
            int first = preferred + offset;
            if (IsSafeFloor(x, first))
                return first;
            if (offset == 0)
                continue;
            int second = preferred - offset;
            if (IsSafeFloor(x, second))
                return second;
        }
        return null;
    }

    public static float TraversalCenterY(int x, Player player)
    {
        int preferred = UsesInvertedProgression
            ? player.Center.ToTileCoordinates().Y : PreferredHabitableRow;
        int radius = UsesInvertedProgression ? 260 :
            System.Math.Max(220, (int)Main.rockLayer - (int)Main.worldSurface + 140);
        int? floor = FindStandingFloorRow(x, preferred, radius);
        return floor.HasValue ? floor.Value * 16f - player.height * 0.5f - 2f : player.Center.Y;
    }

    private static bool IsSafeFloor(int x, int y)
    {
        if (!WorldGen.InWorld(x, y, 10) || !WorldGen.SolidTile(x, y))
            return false;
        for (int clearY = y - 3; clearY < y; clearY++)
        {
            if (WorldGen.SolidTile(x, clearY))
                return false;
            Tile tile = Main.tile[x, clearY];
            if (tile.LiquidAmount >= 96 && tile.LiquidType is LiquidID.Lava or LiquidID.Shimmer)
                return false;
        }
        return true;
    }
}
