using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;

namespace MoreAIPlayer.Companion.Automation;

/// <summary>Why the independent spatial self-check decided that ordinary task execution is no
/// longer healthy. This is deliberately separate from <see cref="AutoBlockReason"/>: the watchdog
/// diagnoses a goal, while this controller diagnoses the player's recent physical behaviour.</summary>
public enum AutoSelfRescueReason : byte
{
    Stationary,
    Oscillation,
    Embedded,
    ReturnRouteFailed
}

/// <summary>The concrete recovery which was selected for diagnostics and optional progress chat.</summary>
public enum AutoSelfRescueAction : byte
{
    MineEscape,
    ChangeRoute,
    ReturnHome,
    ForceRecall
}

/// <summary>
/// Independent deadlock circuit breaker for the automation player. The goal watchdog measures
/// task metrics and target approach; this controller additionally samples the player's trajectory
/// so running left/right inside the same few tiles cannot masquerade as useful travel.
///
/// A detected incident escalates through four bounded remedies: mine the immediate body/corridor,
/// reject the bad target and change route, physically return to a verified checkpoint, then use a
/// legally owned recall item if even the return route fails. Real task/world progress resets the
/// ladder; exhausting the recall rung defers the failed route before starting a
/// fresh ladder, so the same route cannot immediately send it back into the same loop.
/// </summary>
public sealed class AutoSelfRescueController
{
    private const int SampleIntervalTicks = 15;
    private const int MaximumSamples = 32;                 // rolling eight-second trace
    private const int MaximumMeaninglessIdleTicks = 60;    // absolute one-second ceiling
    private const float PurposeApproachPixels = 10f;
    private const int EmbeddedTriggerTicks = 45;
    private const int BaseStationaryTriggerTicks = 210;    // 3.5s
    private const int BaseOscillationTriggerTicks = 330;   // 5.5s
    private const int RecoveryCooldownTicks = 180;
    private const int ReturnNoApproachTicks = 240;
    private const int ReturnDeadlineTicks = 1800;

    private readonly Queue<SpatialSample> samples = new(MaximumSamples);

    private long observedPlanKey;
    private long lastSignature;
    private long lastProgressSerial;
    private int sampleCountdown;
    private int ticksWithoutRealProgress;
    private int embeddedTicks;
    private int cooldownTicks;
    private int recoveryLevel;
    private int meaninglessIdleTicks;
    private float bestPurposeDistance = float.MaxValue;
    private Point? observedWorkTile;
    private int observedWorkDamage;
    private int observedCombatNpc = -1;
    private int observedCombatLife;

    private bool recoveryPending;
    private int pendingLevel;
    private AutoSelfRescueReason pendingReason;
    private AutoPlan pendingPlan;

    private bool returningToCheckpoint;
    private Vector2 returnCheckpoint;
    private float bestReturnDistanceSquared = float.MaxValue;
    private int returnTicks;
    private int returnTicksWithoutApproach;

    /// <summary>A live backpack edit changes the facts the previous failed route was based on.
    /// Cancel a queued/ongoing route recovery so it cannot delay the next inventory-driven plan;
    /// a genuinely unresolved physical obstruction will be detected again from the fresh trace.</summary>
    public void NotifyExternalInventoryEdit(Vector2 currentPosition)
    {
        recoveryPending = false;
        returningToCheckpoint = false;
        recoveryLevel = 0;
        cooldownTicks = 0;
        ticksWithoutRealProgress = 0;
        embeddedTicks = 0;
        observedPlanKey = 0;
        lastSignature = 0;
        ClearSpatialWindow(currentPosition);
    }

    /// <summary>Runs a previously scheduled remedy before ordinary task controllers can overwrite
    /// its inputs. Returns true when self-rescue owns this tick.</summary>
    public bool TryContinueRecovery(AutoContext ctx, AutoEscapeController escape)
    {
        if (returningToCheckpoint)
            return ContinueReturnToCheckpoint(ctx);
        if (!recoveryPending)
            return false;

        recoveryPending = false;
        ClearCurrentAction(ctx);
        ctx.Navigator.Reset();

        switch (pendingLevel)
        {
            case 1:
                if (escape.TryMineBodyObstruction(ctx) ||
                    escape.TryMineEscapeCorridor(ctx, PreferredDirection(ctx)))
                {
                    SetLastAction(ctx, AutoSelfRescueAction.MineEscape);
                }
                else
                {
                    ChangeRoute(ctx, pendingPlan, deferTicks: 180);
                    AutoExplorationController.Explore(ctx);
                    SetLastAction(ctx, AutoSelfRescueAction.ChangeRoute);
                }
                break;

            case 2:
                ChangeRoute(ctx, pendingPlan, deferTicks: 420);
                AutoExplorationController.Explore(ctx);
                SetLastAction(ctx, AutoSelfRescueAction.ChangeRoute);
                break;

            case 3:
                ChangeRoute(ctx, pendingPlan, deferTicks: 900);
                BeginReturnToCheckpoint(ctx);
                SetLastAction(ctx, AutoSelfRescueAction.ReturnHome);
                return ContinueReturnToCheckpoint(ctx);

            default:
                ChangeRoute(ctx, pendingPlan, deferTicks: 1200);
                ForceRecall(ctx, pendingReason, incrementCount: false);
                return true;
        }

        cooldownTicks = RecoveryCooldownTicks;
        ClearSpatialWindow(ctx.Me.Center);
        return true;
    }

    /// <summary>Observes the action which just ran. Returns true when it scheduled a recovery and
    /// cleared the old action's inputs, so the caller must not also run the ordinary watchdog.</summary>
    public bool ObserveAndRecover(AutoContext ctx, AutoPlan plan, AutoTaskResult result)
    {
        if (returningToCheckpoint || recoveryPending)
            return true;

        long signature = AutoTaskRuntime.ComputeSignature(ctx, plan);
        long progressSerial = ctx.TaskRuntime.ProgressSerial;
        bool planChanged = observedPlanKey != plan.StableKey;
        if (observedPlanKey == 0)
        {
            observedPlanKey = plan.StableKey;
            lastSignature = signature;
            lastProgressSerial = progressSerial;
            ticksWithoutRealProgress = 0;
            embeddedTicks = 0;
            ClearSpatialWindow(ctx.Me.Center);
            return false;
        }

        // A portfolio switch is not progress. The former per-plan reset let two failing routes
        // alternate forever while clearing the stationary/oscillation trace on every switch.
        bool realProgress = (!planChanged && signature != lastSignature) ||
            progressSerial != lastProgressSerial;
        observedPlanKey = plan.StableKey;
        lastSignature = signature;
        lastProgressSerial = progressSerial;
        if (realProgress || result == AutoTaskResult.Succeeded)
        {
            ResetAfterRealProgress(ctx);
            return false;
        }

        if (plan.Goal == AutoGoalId.Idle)
        {
            ticksWithoutRealProgress = 0;
            ClearSpatialWindow(ctx.Me.Center);
            return false;
        }

        // This is deliberately stricter and faster than the longer spatial recovery ladder below.
        // It catches the controller producing no work at all: no meaningful movement, no changing
        // task/world fact, no real tile action, no live fight and no cast bobber. Switching between
        // plans cannot reset it. At exactly one second the bad route is deferred and exploration is
        // started in this same frame, so there is no extra recovery frame spent standing still.
        if (PurposefullyApproached(ctx) || HasAdvancedPhysicalWork(ctx) ||
            HasAdvancedCombat(ctx) ||
            IsLegitimatePassiveWork(ctx, plan))
        {
            meaninglessIdleTicks = 0;
        }
        else if (++meaninglessIdleTicks >= MaximumMeaninglessIdleTicks)
        {
            RecoverMeaninglessIdle(ctx, plan);
            return true;
        }

        ticksWithoutRealProgress++;
        embeddedTicks = Collision.SolidCollision(ctx.Me.position, ctx.Me.width, ctx.Me.height)
            ? embeddedTicks + 1
            : 0;
        Sample(ctx.Me.Center);

        if (cooldownTicks > 0)
        {
            cooldownTicks--;
            return false;
        }

        AutoSelfRescueReason? reason = null;
        if (embeddedTicks >= EmbeddedTriggerTicks)
            reason = AutoSelfRescueReason.Embedded;
        else if (ticksWithoutRealProgress >= StationaryTrigger(plan.Goal) && IsStationary())
            reason = AutoSelfRescueReason.Stationary;
        else if (ticksWithoutRealProgress >= OscillationTrigger(plan.Goal) && IsOscillating())
            reason = AutoSelfRescueReason.Oscillation;

        if (reason is not AutoSelfRescueReason detected)
            return false;

        ScheduleRecovery(ctx, plan, detected);
        return true;
    }

    private void ScheduleRecovery(AutoContext ctx, AutoPlan plan, AutoSelfRescueReason reason)
    {
        recoveryLevel = System.Math.Min(4, recoveryLevel + 1);
        pendingLevel = recoveryLevel;
        pendingReason = reason;
        pendingPlan = plan;
        recoveryPending = true;
        cooldownTicks = RecoveryCooldownTicks;
        ctx.TaskRuntime.LastFailureReason = AutoBlockReason.NoProgress;
        ctx.TaskRuntime.SelfRescueLevel = recoveryLevel;
        ctx.TaskRuntime.SelfRescueCount++;
        ctx.TaskRuntime.LastSelfRescueReason = reason;
        ctx.TaskRuntime.LastSelfRescueAction = PlannedAction(recoveryLevel);
        ctx.TaskRuntime.Log($"self-rescue L{recoveryLevel} {reason}/{PlannedAction(recoveryLevel)}");
        ClearCurrentAction(ctx);
        ctx.Navigator.Reset();
        ClearSpatialWindow(ctx.Me.Center);
    }

    private void RecoverMeaninglessIdle(AutoContext ctx, AutoPlan plan)
    {
        recoveryLevel = System.Math.Min(4, recoveryLevel + 1);
        ClearCurrentAction(ctx);
        ChangeRoute(ctx, plan, deferTicks: plan.Kind == AutoPlanKind.Optional ? 1800 : 300);
        AutoExplorationController.Explore(ctx);

        ctx.TaskRuntime.LastFailureReason = AutoBlockReason.NoProgress;
        ctx.TaskRuntime.SelfRescueLevel = recoveryLevel;
        ctx.TaskRuntime.SelfRescueCount++;
        ctx.TaskRuntime.LastSelfRescueReason = AutoSelfRescueReason.Stationary;
        ctx.TaskRuntime.LastSelfRescueAction = AutoSelfRescueAction.ChangeRoute;
        ctx.TaskRuntime.Log($"one-second idle breaker {plan.Goal}/ChangeRoute");
        AutoProgressReporter.ReportSelfRescue(ctx.Profile, AutoSelfRescueReason.Stationary,
            recoveryLevel, AutoSelfRescueAction.ChangeRoute);
        cooldownTicks = RecoveryCooldownTicks;
        ClearSpatialWindow(ctx.Me.Center);
    }

    private bool PurposefullyApproached(AutoContext ctx)
    {
        if (!ctx.Navigator.IsTryingToMove || ctx.Navigator.CurrentGoalWorld is not Vector2 goal)
            return false;

        float distance = Vector2.Distance(ctx.Me.Center, goal);
        if (bestPurposeDistance == float.MaxValue)
        {
            bestPurposeDistance = distance;
            return false;
        }
        if (distance > bestPurposeDistance - PurposeApproachPixels)
            return false;
        bestPurposeDistance = distance;
        return true;
    }

    /// <summary>A long mining swing is meaningful only while Terraria's own HitTile damage is
    /// advancing. Merely waving a seed/tool at an invalid tile no longer grants an unlimited idle
    /// exemption, while a genuinely hard block may still take more than one second to break.</summary>
    private bool HasAdvancedPhysicalWork(AutoContext ctx)
    {
        if (ctx.Instance.WorkTile is not Point work ||
            !(ctx.Instance.WantsUseItem || ctx.Me.controlUseItem || ctx.Me.itemAnimation > 0))
        {
            observedWorkTile = null;
            observedWorkDamage = 0;
            return false;
        }

        int damage = System.Math.Max(HitDamage(ctx.Me.hitTile, work, hitType: 1),
            System.Math.Max(HitDamage(ctx.Me.hitTile, work, hitType: 2),
                HitDamage(ctx.Me.hitReplace, work, hitType: 1)));
        if (observedWorkTile != work)
        {
            observedWorkTile = work;
            observedWorkDamage = damage;
            return false;
        }
        if (damage <= observedWorkDamage)
            return false;
        observedWorkDamage = damage;
        return true;
    }

    private static int HitDamage(HitTile hitTile, Point work, int hitType)
    {
        if (hitTile?.data is null)
            return 0;
        int best = 0;
        foreach (HitTile.HitTileObject entry in hitTile.data)
            if (entry is not null && entry.type == hitType && entry.X == work.X &&
                entry.Y == work.Y)
                best = System.Math.Max(best, entry.damage);
        return best;
    }

    private bool HasAdvancedCombat(AutoContext ctx)
    {
        if (!(ctx.Instance.WantsUseItem || ctx.Me.controlUseItem || ctx.Me.itemAnimation > 0))
        {
            observedCombatNpc = -1;
            observedCombatLife = 0;
            return false;
        }
        NPC target = null;
        float bestAimDistance = 8f * 16f * (8f * 16f);
        foreach (NPC npc in Main.ActiveNPCs)
        {
            if (!npc.active || npc.friendly || npc.townNPC || npc.life <= 0 || npc.dontTakeDamage)
                continue;
            float aimDistance = Vector2.DistanceSquared(ctx.Instance.AimWorld, npc.Center);
            if (aimDistance >= bestAimDistance)
                continue;
            bestAimDistance = aimDistance;
            target = npc;
        }
        if (target is null)
            return false;
        if (observedCombatNpc != target.whoAmI)
        {
            observedCombatNpc = target.whoAmI;
            observedCombatLife = target.life;
            return false;
        }
        bool damaged = target.life < observedCombatLife;
        observedCombatLife = target.life;
        return damaged;
    }

    private static bool IsLegitimatePassiveWork(AutoContext ctx, AutoPlan plan)
    {

        if (plan.Goal == AutoGoalId.FishOpportunity)
        {
            foreach (Projectile projectile in Main.ActiveProjectiles)
                if (projectile.owner == ctx.Me.whoAmI && projectile.bobber)
                    return true;
        }

        return false;
    }

    private bool ContinueReturnToCheckpoint(AutoContext ctx)
    {
        Player me = ctx.Me;
        ClearCurrentAction(ctx);
        returnCheckpoint = FindSafeCheckpoint(ctx);
        float distanceSquared = Vector2.DistanceSquared(me.Center, returnCheckpoint);
        if (distanceSquared <= 5f * 16f * (5f * 16f))
        {
            ctx.Navigator.Idle(me);
            returningToCheckpoint = false;
            cooldownTicks = RecoveryCooldownTicks;
            ClearSpatialWindow(me.Center);
            ctx.TaskRuntime.Log("self-rescue reached checkpoint");
            return true;
        }

        returnTicks++;
        if (bestReturnDistanceSquared == float.MaxValue ||
            System.MathF.Sqrt(distanceSquared) + 24f < System.MathF.Sqrt(bestReturnDistanceSquared))
        {
            bestReturnDistanceSquared = distanceSquared;
            returnTicksWithoutApproach = 0;
        }
        else
        {
            returnTicksWithoutApproach++;
        }

        if (returnTicks >= ReturnDeadlineTicks || returnTicksWithoutApproach >= ReturnNoApproachTicks)
        {
            ForceRecall(ctx, AutoSelfRescueReason.ReturnRouteFailed, incrementCount: true);
            return true;
        }

        AutoWorldTravelController.TravelTo(ctx, returnCheckpoint, 5f);
        return true;
    }

    private void BeginReturnToCheckpoint(AutoContext ctx)
    {
        returningToCheckpoint = true;
        returnCheckpoint = FindSafeCheckpoint(ctx);
        bestReturnDistanceSquared = Vector2.DistanceSquared(ctx.Me.Center, returnCheckpoint);
        returnTicks = 0;
        returnTicksWithoutApproach = 0;
    }

    private void ForceRecall(AutoContext ctx, AutoSelfRescueReason reason, bool incrementCount)
    {
        Player me = ctx.Me;
        bool usedRecall = AutoRecallController.TryUseOwnedRecall(ctx);
        ctx.Navigator.Reset();
        if (!usedRecall)
            AutoExplorationController.Explore(ctx);
        returningToCheckpoint = false;
        recoveryPending = false;
        recoveryLevel = 0; // the failed route is deferred; a new route gets a fresh bounded ladder
        cooldownTicks = 600;
        ClearSpatialWindow(me.Center);
        ticksWithoutRealProgress = 0;

        ctx.TaskRuntime.SelfRescueLevel = 4;
        if (incrementCount)
            ctx.TaskRuntime.SelfRescueCount++;
        ctx.TaskRuntime.LastSelfRescueReason = reason;
        ctx.TaskRuntime.LastSelfRescueAction = AutoSelfRescueAction.ForceRecall;
        ctx.TaskRuntime.Log(usedRecall
            ? $"self-rescue used owned recall ({reason})"
            : $"self-rescue no recall item; route deferred ({reason})");
        AutoProgressReporter.ReportSelfRescue(ctx.Profile, reason, 4,
            AutoSelfRescueAction.ForceRecall);
    }

    private void ResetAfterRealProgress(AutoContext ctx)
    {
        ticksWithoutRealProgress = 0;
        embeddedTicks = 0;
        recoveryLevel = 0;
        cooldownTicks = 0;
        ctx.TaskRuntime.SelfRescueLevel = 0;
        ClearSpatialWindow(ctx.Me.Center);
    }

    private void ChangeRoute(AutoContext ctx, AutoPlan plan, int deferTicks)
    {
        ctx.TaskRuntime.RejectActiveTarget(8, 1800);
        ctx.TaskRuntime.Defer(plan, deferTicks);
        ctx.State.StrategyVariant++;
        // Preserve a healthy outward frontier on the first rescue. Only the second failed route
        // earns one deliberate direction change; the next rungs return home rather than pacing.
        if (recoveryLevel >= 2)
            ctx.State.ExplorationDirection = ctx.State.ExplorationDirection < 0 ? 1 : -1;
        ctx.State.ExplorationTargetTileX = -1;
        ctx.Navigator.Reset();
    }

    private void SetLastAction(AutoContext ctx, AutoSelfRescueAction action)
    {
        ctx.TaskRuntime.LastSelfRescueReason = pendingReason;
        ctx.TaskRuntime.LastSelfRescueAction = action;
        ctx.TaskRuntime.Log($"self-rescue applied L{pendingLevel} {pendingReason}/{action}");
        AutoProgressReporter.ReportSelfRescue(ctx.Profile, pendingReason, pendingLevel, action);
    }

    private int PreferredDirection(AutoContext ctx)
    {
        if (ctx.TaskRuntime.ActiveTargetTile is Point target)
        {
            int currentX = ctx.Me.Center.ToTileCoordinates().X;
            if (target.X != currentX)
                return target.X > currentX ? 1 : -1;
        }
        if (ctx.Me.controlRight != ctx.Me.controlLeft)
            return ctx.Me.controlRight ? 1 : -1;
        return ctx.Me.direction == 0 ? 1 : ctx.Me.direction;
    }

    private void Sample(Vector2 position)
    {
        if (sampleCountdown > 0)
        {
            sampleCountdown--;
            return;
        }
        sampleCountdown = SampleIntervalTicks - 1;
        if (samples.Count >= MaximumSamples)
            samples.Dequeue();
        samples.Enqueue(new SpatialSample(position));
    }

    private bool IsStationary()
    {
        if (samples.Count < 12)
            return false;
        Bounds(out float minX, out float maxX, out float minY, out float maxY, out _, out _, out _);
        // A non-Idle automation plan is required to keep doing useful work. Remaining still with no
        // task metric change is therefore a deadlock even if a broken controller forgot to request
        // movement or item use; requiring an input here would make exactly that silent failure
        // invisible to the circuit breaker.
        return maxX - minX <= 1.5f * 16f && maxY - minY <= 4.5f * 16f;
    }

    private bool IsOscillating()
    {
        if (samples.Count < 20)
            return false;
        Bounds(out float minX, out float maxX, out float minY, out float maxY,
            out float travel, out float net, out int reversals);
        return maxX - minX <= 10f * 16f && maxY - minY <= 7f * 16f &&
            travel >= 12f * 16f && net <= 3.5f * 16f && reversals >= 4;
    }

    private void Bounds(out float minX, out float maxX, out float minY, out float maxY,
        out float travel, out float net, out int reversals)
    {
        minX = minY = float.MaxValue;
        maxX = maxY = float.MinValue;
        travel = 0f;
        reversals = 0;
        int lastDirection = 0;
        Vector2 first = default;
        Vector2 previous = default;
        bool hasPrevious = false;
        foreach (SpatialSample sample in samples)
        {
            Vector2 position = sample.Position;
            minX = System.MathF.Min(minX, position.X);
            maxX = System.MathF.Max(maxX, position.X);
            minY = System.MathF.Min(minY, position.Y);
            maxY = System.MathF.Max(maxY, position.Y);
            if (!hasPrevious)
            {
                first = previous = position;
                hasPrevious = true;
                continue;
            }

            Vector2 delta = position - previous;
            travel += delta.Length();
            if (System.MathF.Abs(delta.X) >= 8f)
            {
                int direction = delta.X > 0f ? 1 : -1;
                if (lastDirection != 0 && direction != lastDirection)
                    reversals++;
                lastDirection = direction;
            }
            previous = position;
        }
        net = hasPrevious ? Vector2.Distance(first, previous) : 0f;
    }

    private void ClearSpatialWindow(Vector2 position)
    {
        samples.Clear();
        samples.Enqueue(new SpatialSample(position));
        sampleCountdown = SampleIntervalTicks - 1;
        meaninglessIdleTicks = 0;
        bestPurposeDistance = float.MaxValue;
        observedWorkTile = null;
        observedWorkDamage = 0;
        observedCombatNpc = -1;
        observedCombatLife = 0;
    }

    private static int StationaryTrigger(AutoGoalId goal) => goal switch
    {
        AutoGoalId.FightBoss => 420,
        AutoGoalId.FishOpportunity => 600,
        AutoGoalId.ReturnToDeathSite or AutoGoalId.AcquireVitalUpgrade or
            AutoGoalId.AcquireWeaponUpgrade or AutoGoalId.UpgradeClassLoadout => 360,
        _ => BaseStationaryTriggerTicks
    };

    private static int OscillationTrigger(AutoGoalId goal) => goal switch
    {
        AutoGoalId.FightBoss => 540,
        AutoGoalId.FishOpportunity => 720,
        AutoGoalId.ReturnToDeathSite or AutoGoalId.AcquireVitalUpgrade or
            AutoGoalId.AcquireWeaponUpgrade or AutoGoalId.UpgradeClassLoadout => 480,
        _ => BaseOscillationTriggerTicks
    };

    private static AutoSelfRescueAction PlannedAction(int level) => level switch
    {
        1 => AutoSelfRescueAction.MineEscape,
        2 => AutoSelfRescueAction.ChangeRoute,
        3 => AutoSelfRescueAction.ReturnHome,
        _ => AutoSelfRescueAction.ForceRecall
    };

    private static Vector2 FindSafeCheckpoint(AutoContext ctx)
    {
        Point preferred = ctx.State?.HomeAnchor is Point home &&
            AutoShelterController.IsShelterComplete(home, ctx.State)
            ? new Point(home.X + 6, home.Y + 6)
            : new Point(Main.spawnTileX, Main.spawnTileY);
        Player me = ctx.Me;

        for (int radius = 0; radius <= 12; radius++)
        {
            for (int dx = -radius; dx <= radius; dx++)
            {
                for (int dy = -radius; dy <= radius; dy++)
                {
                    if (radius > 0 && System.Math.Abs(dx) != radius && System.Math.Abs(dy) != radius)
                        continue;
                    int x = preferred.X + dx;
                    int floorY = preferred.Y + dy;
                    if (!WorldGen.InWorld(x, floorY, 10))
                        continue;
                    Vector2 candidate = new(x * 16f + 8f,
                        floorY * 16f - me.height * 0.5f - 2f);
                    Rectangle hitbox = new((int)(candidate.X - me.width * 0.5f),
                        (int)(candidate.Y - me.height * 0.5f), me.width, me.height);
                    if (Collision.SolidCollision(hitbox.TopLeft(), me.width, me.height) ||
                        HasDangerousLiquid(hitbox) || !HasLandingSupport(hitbox))
                        continue;
                    return candidate;
                }
            }
        }

        return new Vector2(Main.spawnTileX * 16f + 8f,
            Main.spawnTileY * 16f - me.height * 0.5f - 2f);
    }

    private static bool HasDangerousLiquid(Rectangle hitbox)
    {
        int left = System.Math.Max(1, hitbox.Left / 16);
        int right = System.Math.Min(Main.maxTilesX - 2, (hitbox.Right - 1) / 16);
        int top = System.Math.Max(1, hitbox.Top / 16);
        int bottom = System.Math.Min(Main.maxTilesY - 2, (hitbox.Bottom - 1) / 16);
        for (int x = left; x <= right; x++)
        for (int y = top; y <= bottom; y++)
        {
            Tile tile = Main.tile[x, y];
            if (tile.LiquidAmount >= 96 && tile.LiquidType is LiquidID.Lava or LiquidID.Shimmer)
                return true;
        }
        return false;
    }

    private static bool HasLandingSupport(Rectangle hitbox)
    {
        int left = System.Math.Max(1, (hitbox.Left + 2) / 16);
        int right = System.Math.Min(Main.maxTilesX - 2, (hitbox.Right - 3) / 16);
        int startY = System.Math.Max(1, hitbox.Bottom / 16);
        int endY = System.Math.Min(Main.maxTilesY - 2, startY + 4);
        for (int x = left; x <= right; x++)
        for (int y = startY; y <= endY; y++)
        {
            Tile tile = Main.tile[x, y];
            if (!tile.HasTile || tile.IsActuated || tile.TileType >= Main.tileSolid.Length)
                continue;
            if (Main.tileSolid[tile.TileType] || Main.tileSolidTop[tile.TileType])
                return true;
        }
        return false;
    }

    private static void ClearCurrentAction(AutoContext ctx)
    {
        ClearInputs(ctx.Me);
        ctx.Instance.WantsUseItem = false;
        ctx.Instance.WantsAltUse = false;
        ctx.Instance.WorkTile = null;
    }

    private static void ClearInputs(Player me)
    {
        me.controlLeft = false;
        me.controlRight = false;
        me.controlUp = false;
        me.controlDown = false;
        me.controlJump = false;
        me.controlUseItem = false;
        me.controlUseTile = false;
        me.controlHook = false;
        me.controlMount = false;
        me.controlThrow = false;
        me.controlInv = false;
        me.controlTorch = false;
        me.controlMap = false;
        me.controlSmart = false;
        me.controlQuickHeal = false;
        me.controlQuickMana = false;
        me.controlCreativeMenu = false;
        me.controlDownHold = false;
        me.releaseUseItem = true;
        me.releaseUseTile = true;
        me.releaseMount = true;
    }

    private readonly record struct SpatialSample(Vector2 Position);
}
