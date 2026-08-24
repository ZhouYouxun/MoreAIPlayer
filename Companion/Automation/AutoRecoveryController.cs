using Microsoft.Xna.Framework;
using Terraria;

namespace MoreAIPlayer.Companion.Automation;

/// <summary>Death-site continuation and live risk retreat. Saved objectives remain intact; only
/// the route/arena variant changes after failure.</summary>
public static class AutoRecoveryController
{
    public static bool IsOverwhelmed(AutoContext ctx)
    {
        Player me = ctx.Me;
        if (ctx.State.HomeAnchor is Point home && Vector2.DistanceSquared(me.Center,
            new Vector2((home.X + 6) * 16f, (home.Y + 5) * 16f)) < 12f * 16f * 12f * 16f &&
            me.statLife * 100 / System.Math.Max(1, me.statLifeMax2) >= 40)
            return false;
        if (me.statLifeMax2 > 0 && me.statLife * 100 / me.statLifeMax2 < 24)
            return true;

        int threat = AutoPhoneWorldService.Read(me).ThreatScore;
        int capability = AutoWeaponEvaluator.BestUsableScore(ctx.Me, ctx.Profile) * 3 +
            me.statDefense * 8 + me.statLifeMax2;
        return threat > capability;
    }

    public static AutoTaskResult ReturnToDeathSite(AutoContext ctx)
    {
        if (!ctx.State.HasDeathReturnTarget)
            return AutoTaskResult.Succeeded;
        if (IsOverwhelmed(ctx))
        {
            // Keep the saved destination, but abandon this concrete approach before it repeats the
            // same lethal enemy pack. Recovery heals at home and advances StrategyVariant; the next
            // tick then approaches the still-persisted death site from another side/height.
            if (ctx.TaskRuntime.ActiveTargetTile.HasValue)
                ctx.TaskRuntime.RejectActiveTarget(42, 1800);
            return RecoverRisk(ctx);
        }
        Vector2 target = ctx.State.DeathReturnTarget;
        if (!float.IsFinite(target.X) || !float.IsFinite(target.Y))
        {
            ctx.State.HasDeathReturnTarget = false;
            return AutoTaskResult.Succeeded;
        }

        // Absolute recovery insurance: only after the watchdog has exhausted several physical
        // route variants do we use perfect world information to select an open landing cell near
        // the saved location. This prevents an unbreakable mod tile enclosure from ending a run.
        if (ctx.TaskRuntime.RecoveryLevel >= 7 && TryFindSafeLanding(target, out Vector2 landing))
        {
            ctx.Me.Center = landing;
            ctx.Me.velocity = Vector2.Zero;
            ctx.Me.fallStart = (int)(ctx.Me.position.Y / 16f);
        }

        // Each failed route approaches from a different side/height instead of repeating the same
        // tunnel into the same enemy pack or lava pocket.
        int variant = ctx.State.StrategyVariant % 5;
        Vector2 approach = target + variant switch
        {
            1 => new Vector2(-12f * 16f, -8f * 16f),
            2 => new Vector2(12f * 16f, -8f * 16f),
            3 => new Vector2(-20f * 16f, 0f),
            4 => new Vector2(20f * 16f, 0f),
            _ => Vector2.Zero
        };
        AutoTaskResult travel = AutoWorldTravelController.TravelTo(ctx, approach, 7f);
        if (travel != AutoTaskResult.Succeeded)
            return travel;

        ctx.State.HasDeathReturnTarget = false;
        ctx.State.DeathReturnAttempts = 0;
        ctx.TaskRuntime.RecordWorldProgress();
        AutoProgressReporter.ReportRecovery(ctx.Profile, "AutomationReturnResumed", ctx.Profile.Name);
        return AutoTaskResult.Succeeded;
    }

    private static bool TryFindSafeLanding(Vector2 target, out Vector2 landing)
    {
        Point origin = target.ToTileCoordinates();
        for (int radius = 0; radius <= 36; radius++)
        for (int dx = -radius; dx <= radius; dx++)
        for (int dy = -radius; dy <= radius; dy++)
        {
            int x = origin.X + dx;
            int y = origin.Y + dy;
            if (!WorldGen.InWorld(x, y, 12))
                continue;
            Rectangle body = new(x * 16 - 10, y * 16 - 42, 20, 42);
            if (Collision.SolidCollision(body.TopLeft(), body.Width, body.Height))
                continue;
            Tile floor = Main.tile[x, y + 1];
            if (!floor.HasTile || !Main.tileSolid[floor.TileType] || Main.tileSolidTop[floor.TileType])
                continue;
            landing = new Vector2(x * 16f + 8f, y * 16f - 21f);
            return true;
        }
        landing = target;
        return false;
    }

    public static AutoTaskResult RecoverRisk(AutoContext ctx)
    {
        AutoPhoneWorldService.TryEmergencyRecall(ctx);
        Point home = ctx.State.HomeAnchor ?? new Point(Main.spawnTileX, Main.spawnTileY);
        Vector2 safe = new((home.X + 6) * 16f + 8f, (home.Y + 5) * 16f + 8f);
        AutoTaskResult travel = AutoWorldTravelController.TravelTo(ctx, safe, 6f);
        if (travel != AutoTaskResult.Succeeded)
            return travel;

        ctx.Me.lifeRegen += 12;

        int heal = CompanionProfile.HealingPotionSlot;
        if (heal >= 0 && heal < ctx.Me.inventory.Length && !ctx.Me.inventory[heal].IsAir &&
            ctx.Me.statLife < ctx.Me.statLifeMax2 * 3 / 4)
        {
            ctx.Actuator.UseItemInSlot(ctx.Instance, ctx.Me, heal, ctx.Me.Center);
            return AutoTaskResult.Running;
        }

        ctx.State.StrategyVariant++;
        if (ctx.TaskRuntime.ActiveTargetTile.HasValue)
            ctx.TaskRuntime.RejectActiveTarget(12, 1800);
        if (ctx.State.StageFailureCount > 0)
        {
            ctx.State.ArenaCompleted = false;
            ctx.State.ArenaAnchor = null;
        }
        return AutoTaskResult.Succeeded;
    }
}
