using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;

namespace MoreAIPlayer.Companion.Automation;

public readonly record struct AutoPhoneWorldSnapshot(
    int TileX,
    int TileY,
    int DepthTiles,
    int NearbyHostiles,
    int ThreatScore,
    bool IsDay,
    bool IsRaining);

/// <summary>Intrinsic location/radar/recall side of the automation phone. The ordinary Cell Phone
/// exposes coordinates, depth, time, weather, nearby enemies and home teleport; code can consume
/// those facts directly without occupying an inventory or accessory slot.</summary>
public static class AutoPhoneWorldService
{
    private static long nextEmergencyRecallTick;

    internal static void ResetWorldSession() => nextEmergencyRecallTick = 0;

    public static AutoPhoneWorldSnapshot Read(Player me)
    {
        Point tile = me.Center.ToTileCoordinates();
        Rectangle area = me.Hitbox;
        area.Inflate(18 * 16, 12 * 16);
        int count = 0;
        int threat = 0;
        foreach (NPC npc in Main.ActiveNPCs)
        {
            if (!npc.active || npc.friendly || npc.damage <= 0 || npc.life <= 0 || npc.boss ||
                !area.Intersects(npc.Hitbox))
                continue;
            count++;
            threat += npc.damage + System.Math.Min(250, npc.lifeMax / 8);
        }
        return new AutoPhoneWorldSnapshot(tile.X, tile.Y, tile.Y - (int)Main.worldSurface,
            count, threat, Main.dayTime, Main.raining);
    }

    public static bool TryEmergencyRecall(AutoContext ctx)
    {
        if (Main.netMode != NetmodeID.SinglePlayer ||
            (long)Main.GameUpdateCount < nextEmergencyRecallTick)
            return false;
        Point home = ctx.State.HomeAnchor is Point built &&
            AutoShelterController.IsShelterComplete(built, ctx.State)
            ? built : new Point(Main.spawnTileX - 6, Main.spawnTileY - 5);
        Vector2 destination = new((home.X + 6) * 16f + 8f,
            (home.Y + 5) * 16f - ctx.Me.height * 0.5f);
        if (Vector2.DistanceSquared(ctx.Me.Center, destination) < 24f * 16f * 24f * 16f)
            return false;

        ctx.Me.Center = destination;
        ctx.Me.velocity = Vector2.Zero;
        ctx.Me.fallStart = (int)(ctx.Me.position.Y / 16f);
        nextEmergencyRecallTick = (long)Main.GameUpdateCount + 120;
        return true;
    }
}
