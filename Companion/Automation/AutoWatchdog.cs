using Microsoft.Xna.Framework;
using Terraria;

namespace MoreAIPlayer.Companion.Automation;

/// <summary>What the brain should do this tick given the watchdog's read of progress.</summary>
public enum AutoWatchdogAction : byte
{
    /// <summary>The plan ran and is progressing (or legitimately travelling); carry on.</summary>
    Continue,
    /// <summary>No progress; the watchdog has applied a recovery step (repath / relocate / go home)
    /// and the brain should relocate this tick rather than keep grinding the same action.</summary>
    Recover,
    /// <summary>The goal is on backoff (repeated RetryLater / blocked); idle until it clears.</summary>
    Wait,
    /// <summary>Every recovery and alternative has failed; sit in an honest blocked state and retry
    /// periodically.</summary>
    Blocked
}

/// <summary>
/// The unified stuck watchdog (design §4.2). It gives each kind of goal a sensible no-progress
/// window instead of one shared 60-tick timer, and drives a fixed recovery ladder when a window
/// elapses without the task metric changing:
///
/// <list type="number">
/// <item>Re-validate the target/station/recipe (controllers already do this each tick).</item>
/// <item>Clear the path cache and repath from the live position.</item>
/// <item>Relocate so a different same-type target comes into range.</item>
/// <item>Relocate further / note that no alternate-route provider exists for this goal yet.</item>
/// <item>Return to the shelter/spawn checkpoint and rebuild.</item>
/// <item>One safe checkpoint teleport for a pure non-boss navigation deadlock.</item>
/// <item>Enter an honest Blocked state with a reason and periodic retry — never a silent jitter.</item>
/// </list>
///
/// Progress is the goal's task metric changing (<see cref="AutoTaskRuntime.ComputeSignature"/>), or,
/// for the soft window only, net relocation past <see cref="AutoTaskRuntime.ProgressDistanceTiles"/>
/// — so genuine travel is not mistaken for being stuck, while walking forever toward an unreachable
/// target still trips the hard window.
/// </summary>
public sealed class AutoWatchdog
{
    private const int MaxRecoveryLevel = 7;
    private const int RetryBackoffBaseTicks = 30;
    private const int RetryBackoffCapTicks = 600;   // 10s
    private const int BlockedRetryTicks = 300;       // 5s between blocked-state re-checks
    private const int MaxRetryAttemptsBeforeAlternative = 3;

    private readonly AutoTaskRuntime runtime;

    public AutoWatchdog(AutoTaskRuntime runtime) => this.runtime = runtime;

    public AutoTaskRuntime Runtime => runtime;

    /// <summary>True when the current goal is on backoff and must not be re-executed this tick.
    /// Called before running the plan so a repeatedly-failing goal idles instead of spinning.</summary>
    public bool OnBackoff(AutoPlan plan)
    {
        if (runtime.IsDeferred(plan))
            return true;
        return runtime.CurrentPlanKey == plan.StableKey &&
               runtime.BlockedUntilTick > (long)Main.GameUpdateCount;
    }

    /// <summary>Interprets the executed plan's result, updates the runtime, applies the next
    /// recovery step when a window elapses, and returns what the brain should do.</summary>
    public AutoWatchdogAction Observe(AutoContext ctx, AutoPlan plan, AutoTaskResult result)
    {
        long now = (long)Main.GameUpdateCount;

        // Goal change resets the per-goal window and recovery ladder.
        if (runtime.CurrentPlanKey != plan.StableKey)
        {
            runtime.Log($"plan {runtime.CurrentGoal}->{plan.Goal} ({plan.ItemType}/{plan.Amount})");
            runtime.CurrentGoal = plan.Goal;
            runtime.CurrentPlanKey = plan.StableKey;
            runtime.GoalStartedTick = now;
            runtime.StepStartedTick = now;
            runtime.AttemptCount = 0;
            runtime.RecoveryLevel = 0;
            runtime.TicksWithoutProgress = 0;
            runtime.TicksWithoutMetricChange = 0;
            runtime.LastProgressSignature = AutoTaskRuntime.ComputeSignature(ctx, plan);
            runtime.LastObservedProgressSerial = runtime.ProgressSerial;
            runtime.WindowStartPosition = ctx.Me.Center;
            runtime.BlockedUntilTick = 0;
            runtime.LastFailureReason = AutoBlockReason.None;
            if (runtime.ActiveTargetGoal != plan.Goal)
                runtime.ClearActiveTarget();
        }

        runtime.LastResult = result;
        runtime.LastKnownPosition = ctx.Me.Center;

        // Metric progress: did the task actually accomplish anything since last tick?
        long signature = AutoTaskRuntime.ComputeSignature(ctx, plan);
        bool metricChanged = signature != runtime.LastProgressSignature;
        runtime.LastProgressSignature = signature;

        bool worldChanged = runtime.ProgressSerial != runtime.LastObservedProgressSerial;
        runtime.LastObservedProgressSerial = runtime.ProgressSerial;

        // Travel progress must be toward the retained target. Raw movement in any direction let an
        // unreachable tree look healthy forever while the companion paced back and forth.
        bool approachedTarget = false;
        bool relocated = false;
        if (runtime.ActiveTargetTile is Point activeTarget)
        {
            Vector2 targetWorld = new(activeTarget.X * 16f + 8f, activeTarget.Y * 16f + 8f);
            float distanceSquared = Vector2.DistanceSquared(ctx.Me.Center, targetWorld);
            if (runtime.BestTargetDistanceSquared == float.MaxValue)
            {
                runtime.BestTargetDistanceSquared = distanceSquared;
            }
            else
            {
                float bestDistance = System.MathF.Sqrt(runtime.BestTargetDistanceSquared);
                float currentDistance = System.MathF.Sqrt(distanceSquared);
                if (currentDistance + 24f < bestDistance)
                {
                    approachedTarget = true;
                    runtime.BestTargetDistanceSquared = distanceSquared;
                }
            }
        }
        else
        {
            relocated = Vector2.DistanceSquared(ctx.Me.Center, runtime.WindowStartPosition) >
                AutoTaskRuntime.ProgressDistanceTiles * 16f * (AutoTaskRuntime.ProgressDistanceTiles * 16f);
        }

        if (metricChanged || worldChanged)
        {
            runtime.TicksWithoutMetricChange = 0;
            runtime.TicksWithoutProgress = 0;
            runtime.WindowStartPosition = ctx.Me.Center;
            // Real accomplishment relaxes the recovery ladder one rung.
            if (runtime.RecoveryLevel > 0)
                runtime.RecoveryLevel--;
        }
        else
        {
            runtime.TicksWithoutMetricChange++;
            if (approachedTarget || relocated)
            {
                runtime.TicksWithoutProgress = 0;
                runtime.WindowStartPosition = ctx.Me.Center;
            }
            else
            {
                runtime.TicksWithoutProgress++;
            }
        }

        // Terminal-ish results decide directly.
        switch (result)
        {
            case AutoTaskResult.Succeeded:
                runtime.Log($"{plan.Goal} succeeded");
                runtime.ClearActiveTarget();
                runtime.AttemptCount = 0;
                runtime.RecoveryLevel = 0;
                runtime.LastFailureReason = AutoBlockReason.None;
                return AutoWatchdogAction.Continue;

            case AutoTaskResult.RetryLater:
                return HandleRetryLater(ctx, plan, now);

            case AutoTaskResult.BlockedMissingWorldResource:
                return EnterBlocked(AutoBlockReason.MissingWorldResource, now);
            case AutoTaskResult.BlockedUnsupportedWorld:
                return EnterBlocked(AutoBlockReason.UnsupportedWorld, now);
            case AutoTaskResult.BlockedUnsupportedMod:
                return EnterBlocked(AutoBlockReason.UnsupportedMod, now);
            case AutoTaskResult.Faulted:
                return EnterBlocked(AutoBlockReason.Faulted, now);

            case AutoTaskResult.AlternativeRequired:
                // The local target is gone/unreachable: climb toward relocation immediately.
                RejectCurrentTarget(plan.Goal);
                return Escalate(ctx, plan, now, AutoBlockReason.NoAlternative);

            case AutoTaskResult.Running:
            default:
                break;
        }

        // Running: only escalate when a window elapses without progress.
        int softWindow = SoftWindow(plan.Goal);
        int hardWindow = HardWindow(plan.Goal);
        if (runtime.TicksWithoutProgress >= softWindow || runtime.TicksWithoutMetricChange >= hardWindow)
        {
            return Escalate(ctx, plan, now, AutoBlockReason.NoProgress);
        }

        return AutoWatchdogAction.Continue;
    }

    private AutoWatchdogAction HandleRetryLater(AutoContext ctx, AutoPlan plan, long now)
    {
        runtime.AttemptCount++;
        int backoff = System.Math.Min(RetryBackoffCapTicks,
            RetryBackoffBaseTicks * (1 << System.Math.Min(runtime.AttemptCount, 4)));
        runtime.BlockedUntilTick = now + backoff;
        runtime.LastFailureReason = AutoBlockReason.NoProgress;
        runtime.Log($"{plan.Goal} retry #{runtime.AttemptCount} +{backoff}t");

        // Persistent RetryLater eventually means "this route is not working" — hand it to the
        // recovery ladder so it can relocate or, ultimately, block honestly, instead of waiting
        // forever. This is the concrete fix for the old infinite-RetryLater spin.
        if (runtime.AttemptCount >= MaxRetryAttemptsBeforeAlternative)
            return Escalate(ctx, plan, now, AutoBlockReason.NoProgress);

        return AutoWatchdogAction.Wait;
    }

    private AutoWatchdogAction Escalate(AutoContext ctx, AutoPlan plan, long now, AutoBlockReason reason)
    {
        runtime.RecoveryLevel = System.Math.Min(MaxRecoveryLevel, runtime.RecoveryLevel + 1);
        runtime.AttemptCount++;
        runtime.TicksWithoutProgress = 0;
        runtime.WindowStartPosition = ctx.Me.Center;
        runtime.Log($"{plan.Goal} recover L{runtime.RecoveryLevel} ({reason})");

        int deferLevel = System.Math.Clamp(plan.FailureBudget, 2, 4);
        if (runtime.RecoveryLevel == deferLevel)
        {
            RejectCurrentTarget(plan.Goal);
            runtime.Defer(plan, DeferralTicks(plan.Kind));
            ctx.Navigator.Reset();
            return AutoWatchdogAction.Recover;
        }

        switch (runtime.RecoveryLevel)
        {
            case 1:
                // Re-validate: controllers already revalidate targets/stations each tick. Nothing to
                // force here; the next tick re-checks from a clean progress window.
                return AutoWatchdogAction.Continue;

            case 2:
                // The repath did not help. Reject the retained target cluster so the controller's
                // next bounded scan is forced to choose a genuinely different tree/vein/pot.
                RejectCurrentTarget(plan.Goal);
                ctx.Navigator.Reset();
                return AutoWatchdogAction.Recover;

            case 3:
                // A plan with a larger budget gets one more relocation window before deferral.
                ctx.Navigator.Reset();
                return AutoWatchdogAction.Recover;

            case 4:
                // Return toward the known checkpoint under ordinary navigation first.
                ctx.Navigator.Reset();
                return AutoWatchdogAction.Recover;

            case 5:
                // One safe checkpoint teleport is allowed for a pure non-boss navigation deadlock
                // — never across an unopened world gate, and never as routine pathing.
                RelocateToCheckpoint(ctx);
                return AutoWatchdogAction.Recover;

            default:
                return EnterBlocked(reason, now);
        }
    }

    private void RejectCurrentTarget(AutoGoalId goal)
    {
        int radius = goal switch
        {
            AutoGoalId.GatherWood => 16,
            AutoGoalId.GatherStone or AutoGoalId.MineOre or AutoGoalId.MineIronTierOre => 3,
            _ => 2
        };
        runtime.RejectActiveTarget(radius);
    }

    private static int DeferralTicks(AutoPlanKind kind) => kind switch
    {
        AutoPlanKind.Optional => 3600,
        AutoPlanKind.Capability => 1200,
        AutoPlanKind.Support => 600,
        _ => 600
    };

    private AutoWatchdogAction EnterBlocked(AutoBlockReason reason, long now)
    {
        runtime.LastFailureReason = reason;
        runtime.BlockedUntilTick = now + BlockedRetryTicks;
        runtime.Log($"blocked: {reason}");
        return AutoWatchdogAction.Blocked;
    }

    /// <summary>Teleports the automation companion to its verified shelter, or the world spawn, as a
    /// last-resort deadlock break. Safe destinations only; never crosses a world gate.</summary>
    private static void RelocateToCheckpoint(AutoContext ctx)
    {
        Player me = ctx.Me;
        int x = Main.spawnTileX;
        int y = Main.spawnTileY;
        if (ctx.State?.HomeAnchor is Point home && AutoShelterController.IsShelterComplete(home))
        {
            x = home.X + 6;
            y = home.Y + 6;
        }
        me.Center = new Vector2(x * 16f + 8f, y * 16f - me.height * 0.5f - 2f);
        me.velocity = Vector2.Zero;
        me.fallStart = (int)(me.position.Y / 16f);
        ctx.Navigator.Reset();
    }

    private static int SoftWindow(AutoGoalId goal) => goal switch
    {
        AutoGoalId.GatherWood or AutoGoalId.GatherStone or AutoGoalId.GatherGel or
        AutoGoalId.MineOre or AutoGoalId.MineIronTierOre => 360,      // ~6s of no block/inventory change
        AutoGoalId.CraftStation or AutoGoalId.CraftGear or AutoGoalId.CraftConsumable => 360,
        AutoGoalId.BuildShelter => 480,                               // ~8s
        AutoGoalId.DepositSurplus => 360,
        _ => 300                                                      // ~5s default (short-range nav/pickup)
    };

    private static int HardWindow(AutoGoalId goal) => goal switch
    {
        AutoGoalId.BuildShelter => 1800,                             // ~30s
        AutoGoalId.GatherWood or AutoGoalId.GatherStone or AutoGoalId.GatherGel or
        AutoGoalId.MineOre or AutoGoalId.MineIronTierOre => 1440,    // ~24s of no metric change at all
        _ => 1200                                                    // ~20s
    };
}
