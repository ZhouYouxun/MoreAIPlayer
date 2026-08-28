using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;

namespace MoreAIPlayer.Companion.Automation;

/// <summary>
/// Resolves an automation summon to live terrain. A remembered coordinate is only a preference:
/// player construction, world conversion or an older cursor-based spawn may have left it inside a
/// block. The observer's mouse and movement are deliberately absent from this decision.
/// </summary>
public static class AutoSpawnResolver
{
    private const int PlayerWidth = 20;
    private const int PlayerHeight = 42;
    private const int LocalSearchRadius = 18;

    public static Vector2 Resolve(Player owner, AutoCompanionState state)
    {
        if (state?.TryGetResumePosition(out Vector2 remembered) == true &&
            TryFindStandingPoint(remembered, LocalSearchRadius, out Vector2 resumed))
            return resumed;

        Vector2 besideOwner = owner.Center + new Vector2((owner.direction == 0 ? 1 : -owner.direction) * 48f, 0f);
        if (TryFindStandingPoint(besideOwner, LocalSearchRadius, out Vector2 local))
            return local;

        Vector2 worldSpawn = new(Main.spawnTileX * 16f + 8f, Main.spawnTileY * 16f);
        if (TryFindStandingPoint(worldSpawn, 24, out Vector2 fallback))
            return fallback;

        // World spawn is Terraria's final authority. Keep the emergency fallback bounded even if
        // a malformed/modded world offers no conventional floor in the searched neighbourhood.
        return ClampToWorld(worldSpawn);
    }

    private static bool TryFindStandingPoint(Vector2 preferred, int radius, out Vector2 result)
    {
        Point origin = ClampToWorld(preferred).ToTileCoordinates();
        for (int ring = 0; ring <= radius; ring++)
        {
            for (int dx = -ring; dx <= ring; dx++)
            for (int dy = -ring; dy <= ring; dy++)
            {
                if (ring > 0 && System.Math.Abs(dx) != ring && System.Math.Abs(dy) != ring)
                    continue;
                int x = origin.X + dx;
                int floorY = origin.Y + dy;
                if (!WorldGen.InWorld(x, floorY, 10))
                    continue;

                Vector2 center = new(x * 16f + 8f, floorY * 16f - PlayerHeight * 0.5f - 2f);
                Rectangle body = new((int)(center.X - PlayerWidth * 0.5f),
                    (int)(center.Y - PlayerHeight * 0.5f), PlayerWidth, PlayerHeight);
                if (!HasSupport(body) || Collision.SolidCollision(body.TopLeft(), body.Width, body.Height) ||
                    HasDangerousLiquid(body))
                    continue;

                result = center;
                return true;
            }
        }

        result = default;
        return false;
    }

    private static bool HasSupport(Rectangle body)
    {
        int left = System.Math.Max(2, (body.Left + 2) / 16);
        int right = System.Math.Min(Main.maxTilesX - 3, (body.Right - 3) / 16);
        int startY = System.Math.Max(2, body.Bottom / 16);
        int endY = System.Math.Min(Main.maxTilesY - 3, startY + 3);
        for (int x = left; x <= right; x++)
        for (int y = startY; y <= endY; y++)
        {
            Tile tile = Main.tile[x, y];
            if (tile.HasTile && !tile.IsActuated && tile.TileType < Main.tileSolid.Length &&
                (Main.tileSolid[tile.TileType] || Main.tileSolidTop[tile.TileType]))
                return true;
        }
        return false;
    }

    private static bool HasDangerousLiquid(Rectangle body)
    {
        int left = System.Math.Max(2, body.Left / 16);
        int right = System.Math.Min(Main.maxTilesX - 3, (body.Right - 1) / 16);
        int top = System.Math.Max(2, body.Top / 16);
        int bottom = System.Math.Min(Main.maxTilesY - 3, (body.Bottom - 1) / 16);
        for (int x = left; x <= right; x++)
        for (int y = top; y <= bottom; y++)
        {
            Tile tile = Main.tile[x, y];
            if (tile.LiquidAmount >= 96 && tile.LiquidType is LiquidID.Lava or LiquidID.Shimmer)
                return true;
        }
        return false;
    }

    private static Vector2 ClampToWorld(Vector2 position) => new(
        MathHelper.Clamp(position.X, 16f * 10f, 16f * (Main.maxTilesX - 10)),
        MathHelper.Clamp(position.Y, 16f * 10f, 16f * (Main.maxTilesY - 10)));
}
