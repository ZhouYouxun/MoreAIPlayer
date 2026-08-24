using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;

namespace MoreAIPlayer.Companion.Automation;

/// <summary>Automation-only body-overlap recovery. It uses the companion's real pickaxe through
/// ItemCheck and only targets breakable, ordinary solid terrain intersecting its collision box.</summary>
public sealed class AutoEscapeController
{
    private const int MicroRelocateAfterTicks = 180;
    private Point lastTarget = new(-1, -1);
    private int targetTicks;

    public bool TryMineBodyObstruction(AutoContext ctx)
    {
        Player me = ctx.Me;
        int pick = AutoInventory.BestToolIndex(me, item => item.pick);
        if (pick < 0 || ctx.Actuator.ItemUseSpent)
            return false;

        Rectangle body = me.Hitbox;
        int left = System.Math.Max(1, body.Left / 16);
        int right = System.Math.Min(Main.maxTilesX - 2, (body.Right - 1) / 16);
        int top = System.Math.Max(1, body.Top / 16);
        int bottom = System.Math.Min(Main.maxTilesY - 2, (body.Bottom - 1) / 16);

        Point? best = null;
        float bestDistance = float.MaxValue;
        for (int x = left; x <= right; x++)
        {
            for (int y = top; y <= bottom; y++)
            {
                Tile tile = Main.tile[x, y];
                if (!tile.HasTile || tile.IsActuated || tile.TileType >= Main.tileSolid.Length ||
                    !Main.tileSolid[tile.TileType] || Main.tileSolidTop[tile.TileType] ||
                    Main.tileFrameImportant[tile.TileType] || !WorldGen.CanKillTile(x, y))
                    continue;

                Rectangle tileBox = new(x * 16, y * 16, 16, 16);
                if (!tileBox.Intersects(body))
                    continue;
                Vector2 center = tileBox.Center.ToVector2();
                float distance = Vector2.DistanceSquared(center, me.Center);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = new Point(x, y);
                }
            }
        }

        if (best is not Point target)
        {
            lastTarget = new Point(-1, -1);
            targetTicks = 0;
            return false;
        }
        if (target == lastTarget)
            targetTicks++;
        else
        {
            lastTarget = target;
            targetTicks = 1;
        }

        // A tile can be breakable in world terms but still exceed the current pick's power (or a
        // mod hook can refuse every hit). After three seconds of real pick attempts, perform one
        // tightly local collision escape instead of freezing the entire progression forever.
        if (targetTicks >= MicroRelocateAfterTicks && TryMicroRelocate(ctx))
        {
            lastTarget = new Point(-1, -1);
            targetTicks = 0;
            return true;
        }
        ctx.Navigator.Idle(me);
        ctx.Actuator.UseItemInSlot(ctx.Instance, me, pick,
            new Vector2(target.X * 16f + 8f, target.Y * 16f + 8f), target);
        return true;
    }

    private static bool TryMicroRelocate(AutoContext ctx)
    {
        Player me = ctx.Me;
        Vector2 origin = me.position;
        for (int radius = 1; radius <= 8; radius++)
        {
            for (int dx = -radius; dx <= radius; dx++)
            {
                for (int dy = -radius; dy <= radius; dy++)
                {
                    if (System.Math.Abs(dx) != radius && System.Math.Abs(dy) != radius)
                        continue;
                    Vector2 candidate = origin + new Vector2(dx * 16f, dy * 16f);
                    Rectangle hitbox = new((int)candidate.X, (int)candidate.Y, me.width, me.height);
                    Point center = new(hitbox.Center.X / 16, hitbox.Center.Y / 16);
                    if (!WorldGen.InWorld(center.X, center.Y, 10) ||
                        Collision.SolidCollision(candidate, me.width, me.height) || HasDangerousLiquid(hitbox))
                        continue;
                    me.position = candidate;
                    me.velocity = Vector2.Zero;
                    me.fallStart = (int)(me.position.Y / 16f);
                    ctx.Navigator.Reset();
                    ctx.TaskRuntime.RecordWorldProgress();
                    return true;
                }
            }
        }
        return false;
    }

    private static bool HasDangerousLiquid(Rectangle hitbox)
    {
        int left = System.Math.Max(1, hitbox.Left / 16);
        int right = System.Math.Min(Main.maxTilesX - 2, (hitbox.Right - 1) / 16);
        int top = System.Math.Max(1, hitbox.Top / 16);
        int bottom = System.Math.Min(Main.maxTilesY - 2, (hitbox.Bottom - 1) / 16);
        for (int x = left; x <= right; x++)
        {
            for (int y = top; y <= bottom; y++)
            {
                Tile tile = Main.tile[x, y];
                if (tile.LiquidAmount > 0 && tile.LiquidType is LiquidID.Lava or LiquidID.Shimmer)
                    return true;
            }
        }
        return false;
    }
}
