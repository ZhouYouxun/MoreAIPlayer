using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;

namespace MoreAIPlayer.Companion.Automation;

/// <summary>Long-range travel for perfect-information objectives. It drives ordinary movement,
/// mines only the immediate obstructing corridor, avoids lethal liquids and uses the owned hook;
/// it never teleports to a resource.</summary>
public static class AutoWorldTravelController
{
    public static AutoTaskResult TravelTo(AutoContext ctx, Vector2 destination,
        float arrivalTiles = 5f)
    {
        Player me = ctx.Me;
        if (!float.IsFinite(destination.X) || !float.IsFinite(destination.Y))
            return AutoTaskResult.AlternativeRequired;
        if (Vector2.DistanceSquared(me.Center, destination) <=
            arrivalTiles * 16f * arrivalTiles * 16f)
        {
            ctx.Navigator.Idle(me);
            return AutoTaskResult.Succeeded;
        }

        Vector2 delta = destination - me.Center;
        Vector2 direction = delta.SafeNormalize(Vector2.UnitX * me.direction);
        if (ctx.Fieldcraft.TryBuildVerticalAccess(ctx, destination))
            return AutoTaskResult.Running;
        if (TryMineImmediateCorridor(ctx, direction))
            return AutoTaskResult.Running;

        Vector2 waypoint = me.Center + direction * System.Math.Min(delta.Length(), 18f * 16f);
        Point waypointTile = waypoint.ToTileCoordinates();
        if (DangerousLiquid(waypointTile))
        {
            int side = destination.X >= me.Center.X ? 1 : -1;
            waypoint = me.Center + new Vector2(side * 12f * 16f, -8f * 16f);
        }

        // A hook is especially useful in the excavated vertical rooms and underworld arena. The
        // same virtual mouse path used by weapons supplies its aim point.
        if (me.velocity.Y > 5f && ctx.Profile.Hook[0] is Item hook && !hook.IsAir)
        {
            ctx.Instance.AimWorld = me.Center + new Vector2(direction.X * 10f * 16f, -8f * 16f);
            me.controlHook = true;
        }
        ctx.Navigator.NavigateToward(me, waypoint, destination);
        return AutoTaskResult.Running;
    }

    private static bool TryMineImmediateCorridor(AutoContext ctx, Vector2 direction)
    {
        int pick = AutoInventory.BestToolIndex(ctx.Me, item => item.pick);
        if (pick < 0)
            return false;

        Point center = ctx.Me.Center.ToTileCoordinates();
        int horizontal = direction.X >= 0f ? 1 : -1;
        for (int step = 1; step <= 4; step++)
        {
            int x = center.X + (System.Math.Abs(direction.X) > 0.2f ? horizontal * step : 0);
            int baseY = center.Y + (int)System.MathF.Round(direction.Y * step);
            for (int y = baseY - 1; y <= baseY + 1; y++)
            {
                if (!WorldGen.InWorld(x, y, 8))
                    continue;
                Tile tile = Main.tile[x, y];
                if (!tile.HasTile || !Main.tileSolid[tile.TileType] || Main.tileSolidTop[tile.TileType] ||
                    Main.tileFrameImportant[tile.TileType] || !WorldGen.CanKillTile(x, y))
                    continue;
                Point obstacle = new(x, y);
                ctx.TaskRuntime.LastTargetTile = obstacle;
                return ctx.Actuator.MoveAndWorkTile(ctx.Instance, ctx.Me, ctx.Navigator, pick, obstacle);
            }
        }
        return false;
    }

    private static bool DangerousLiquid(Point point)
    {
        if (!WorldGen.InWorld(point.X, point.Y, 8))
            return true;
        for (int x = point.X - 1; x <= point.X + 1; x++)
        for (int y = point.Y - 2; y <= point.Y + 2; y++)
        {
            Tile tile = Main.tile[x, y];
            if (tile.LiquidAmount >= 96 && tile.LiquidType is LiquidID.Lava or LiquidID.Shimmer)
                return true;
        }
        return false;
    }
}
