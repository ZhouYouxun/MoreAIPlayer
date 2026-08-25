using Microsoft.Xna.Framework;
using MoreAIPlayer.Companion.AI;
using Terraria;
using Terraria.ID;

namespace MoreAIPlayer.Companion.Automation;

/// <summary>
/// High-level decision making for the world's automation companion. Its live instance latches the
/// automation identity when summoned and runs this in place of the ordinary
/// <see cref="CompanionBrain"/> until retirement; like the ordinary brain, it only writes simulated
/// inputs, while vanilla still performs movement physics, item animation and rendering.
///
/// Death/revive timing is automation-specific (1s / 5s during a boss). Beyond that, each tick it
/// refreshes a capability snapshot, asks the planner for the next goal, and runs the matching
/// controller through the shared navigator/actuator. The deterministic early resource loop is
/// augmented by bounded chest, fishing and exploration opportunities; later progression stages
/// still extend the same capability scheduler rather than replacing it with a fixed walkthrough.
/// </summary>
public sealed class AutoCompanionBrain
{
    // Automation-only revive delays (design §1.4). These must not touch the ordinary companion's
    // NormalReviveTicks/BossReviveTicks constants.
    private const int NormalReviveTicks = 60;  // 1s
    private const int BossReviveTicks = 300;   // 5s while a boss is on the field

    private readonly CompanionNavigator navigator = new();
    private readonly AutoActuator actuator = new();
    private readonly AutoCapabilities capabilities = new();
    private readonly AutoFishingController fishing = new();
    private readonly AutoEscapeController escape = new();
    private readonly AutoSelfRescueController selfRescue = new();
    private readonly AutoBackgroundController background = new();
    private readonly AutoTaskRuntime taskRuntime = new();
    private readonly AutoWatchdog watchdog;
    private AutoContext context;
    private bool runtimeInitialized;
    private bool restoredPersistentMemory;
    private long nextMinionSummonTick;
    private long nextSentrySummonTick;

    // Simple surface wander used when the local area holds no more of what the current goal needs.
    private int wanderDir = 1;
    private int wanderStuckTicks;
    private float lastWanderX;
    private Vector2 wanderTarget;
    private long wanderPlanKey;
    private int wanderTravelTicks;
    private bool wanderActive;
    private long lastBlockedNoticeTick = -3600;

    public AutoCompanionBrain()
    {
        watchdog = new AutoWatchdog(taskRuntime);
    }

    public AutoTaskRuntime TaskRuntime => taskRuntime;

    public void Update(CompanionInstance instance, Player owner, CompanionProfile profile)
    {
        Player me = instance.Entity;

        ResetControls(me);
        actuator.BeginTick();
        navigator.UpdateOwnedDoors(me);
        instance.WorkTile = null;
        instance.WantsAltUse = false;
        instance.WantsUseItem = false;
        instance.AimWorld = me.Center + new Vector2(me.direction * 160f, 0f);

        if (HandleDeathAndRevive(instance, me, profile))
            return; // still dead this tick — nothing else runs

        if (profile.AutoState is null)
            return; // defensive: only runs while auto identity is live

        // NPC.downedMoonlord is the report's sole completion predicate. Conversion is deliberately
        // observed here rather than attached to one summon item or one NPC instance, so a victory
        // remains recognized after save/reload and when another legal source dealt the final hit.
        if (NPC.downedMoonlord)
        {
            owner.GetModPlayer<CompanionPlayer>().MarkAutomationCompletedForCurrentWorld();
            Main.NewText(Terraria.Localization.Language.GetTextValue(
                "Mods.MoreAIPlayer.CompanionCommand.AutomationCompleted", profile.Name), 120, 200, 255);
            navigator.Idle(me);
            // EndAutomation clears Running. Never leave this same live slot around for the entity
            // system to reinterpret as an ordinary companion on the next tick; retirement is
            // applied safely after the current slot enumeration completes.
            instance.DismissRequested = true;
            return;
        }

        AutoContext ctx = BuildContext(instance, owner, profile);
        if (!runtimeInitialized)
        {
            taskRuntime.BossFailureCount = profile.AutoState.BossFailureCount;
            profile.AutoState.RestoreMemory(taskRuntime);
            taskRuntime.BossFailureCount = profile.AutoState.BossFailureCount;
            runtimeInitialized = true;
            restoredPersistentMemory = true;
        }
        AutoClassController.Refresh(me, profile);
        AutoCampaignController.RefreshStage(ctx);
        // A pending recovery owns the tick before any ordinary controller can overwrite its
        // movement/item inputs. This includes a multi-tick physical return to the checkpoint.
        if (selfRescue.TryContinueRecovery(ctx, escape))
        {
            CapturePersistentMemory(profile.AutoState, instance);
            return;
        }
        if (escape.TryMineBodyObstruction(ctx))
        {
            // Body-overlap handling used to return before the main plan/watchdog and could therefore
            // mine one unbreakable tile forever. Feed that early branch into the spatial circuit
            // breaker as an explicit EscapeTerrain task.
            selfRescue.ObserveAndRecover(ctx,
                AutoPlan.Simple(AutoGoalId.EscapeTerrain), AutoTaskResult.Running);
            CapturePersistentMemory(profile.AutoState, instance);
            return;
        }
        // Boss bags, ore, hearts and arena materials must enter the same saved profile before the
        // next milestone is planned. This collector is automation-only and still runs pickup hooks.
        AutoInventory.TryCollectNearby(instance, profile, 12f, null);
        AutoBagController.OpenOne(instance, profile);
        AutoGearController.ConsumePermanentUpgrade(me, profile);
        AutoGearController.OrganizeOwnedGear(me, profile);
        // The companion's own chest is extended campaign memory, not a one-way trash sink. When
        // it is physically back at that chest, restore one stage-relevant stack before planning.
        AutoStorageController.RestoreCampaignStock(ctx);
        MaintainAutomationSummons(instance, me, profile);
        capabilities.Refresh(me, profile);

        // Ordinary enemies in immediate reach interrupt chopping/mining just as they would for a
        // human player. The retained task target stays untouched, so work resumes at the same tree
        // root, vein or structure as soon as the threat is gone.
        if (AutoThreatController.TryDefendImmediate(ctx))
        {
            selfRescue.ObserveAndRecover(ctx,
                AutoPlan.Simple(AutoGoalId.FightBoss, AutoPlanKind.Support),
                AutoTaskResult.Running);
            CapturePersistentMemory(profile.AutoState, instance);
            return;
        }

        // Replanting is persistent maintenance, not a condition that may hold the wood hard gate
        // hostage. Pay one actionable debt between safe tasks; missing seed/ground simply leaves
        // it queued while the campaign continues.
        if (!AutoCampaignController.HasLiveTarget() && !profile.AutoState.HasDeathReturnTarget &&
            !AutoRecoveryController.IsOverwhelmed(ctx))
        {
            AutoTaskResult? replantResult = AutoResourceController.ServicePendingReplant(ctx);
            if (replantResult is AutoTaskResult maintenanceResult)
            {
                selfRescue.ObserveAndRecover(ctx,
                    AutoPlan.Simple(AutoGoalId.GatherWood, AutoPlanKind.Support),
                    maintenanceResult);
                CapturePersistentMemory(profile.AutoState, instance);
                return;
            }
        }

        AutoPlan plan = AutoProgressionPlanner.Plan(ctx);
        AutoProgressReporter.ReportPlan(profile, profile.AutoState.CampaignStage, plan);
        if (restoredPersistentMemory)
        {
            long now = (long)Main.GameUpdateCount;
            if (taskRuntime.CurrentPlanKey == plan.StableKey)
            {
                taskRuntime.GoalStartedTick = now;
                taskRuntime.StepStartedTick = now;
                taskRuntime.LastProgressSignature = AutoTaskRuntime.ComputeSignature(ctx, plan);
                taskRuntime.LastObservedProgressSerial = taskRuntime.ProgressSerial;
                taskRuntime.WindowStartPosition = me.Center;
                taskRuntime.TicksWithoutProgress = 0;
                taskRuntime.TicksWithoutMetricChange = 0;
            }
            else
            {
                // The live world/inventory changed while recalled. Force the watchdog to start a
                // clean window for the newly selected plan, while retaining any still-valid target.
                taskRuntime.CurrentPlanKey = 0;
            }
            restoredPersistentMemory = false;
        }
        taskRuntime.CurrentStep = plan.Goal;
        if (watchdog.OnBackoff(plan))
        {
            background.Maintain(ctx, plan);
            selfRescue.ObserveAndRecover(ctx, plan, AutoTaskResult.Running);
            profile.AutoState.BossFailureCount = taskRuntime.BossFailureCount;
            CapturePersistentMemory(profile.AutoState, instance);
            return;
        }

        AutoTaskResult result = ExecutePlan(ctx, plan);
        if (instance.WorkTile is Point targetTile)
            taskRuntime.LastTargetTile = targetTile;
        if (selfRescue.ObserveAndRecover(ctx, plan, result))
        {
            profile.AutoState.BossFailureCount = taskRuntime.BossFailureCount;
            CapturePersistentMemory(profile.AutoState, instance);
            return;
        }
        AutoWatchdogAction action = watchdog.Observe(ctx, plan, result);
        profile.AutoState.BossFailureCount = taskRuntime.BossFailureCount;

        switch (action)
        {
            case AutoWatchdogAction.Recover:
                ExecuteWatchdogRecovery(ctx, plan);
                break;
            case AutoWatchdogAction.Wait:
                background.Maintain(ctx, plan);
                break;
            case AutoWatchdogAction.Blocked:
                background.Maintain(ctx, plan);
                AnnounceBlocked(plan);
                break;
            default:
                background.Reset();
                wanderActive = false;
                break;
        }
        CapturePersistentMemory(profile.AutoState, instance);
    }

    private void ExecuteWatchdogRecovery(AutoContext ctx, AutoPlan plan)
    {
        if (taskRuntime.RecoveryLevel < 4)
        {
            if (plan.Goal is AutoGoalId.GatherWood or AutoGoalId.GatherStone or
                AutoGoalId.GatherGel or AutoGoalId.MineOre or AutoGoalId.MineIronTierOre or
                AutoGoalId.ExploreWorld or AutoGoalId.AcquireVitalUpgrade or
                AutoGoalId.AcquireWeaponUpgrade)
            {
                AutoExplorationController.Explore(ctx);
            }
            else
            {
                Wander(ctx, plan);
            }
            return;
        }

        Point checkpoint = ctx.State.HomeAnchor is Point home &&
            AutoShelterController.IsShelterComplete(home)
            ? new Point(home.X + 6, home.Y + 6)
            : new Point(Main.spawnTileX, Main.spawnTileY);
        Vector2 safe = new(checkpoint.X * 16f + 8f,
            checkpoint.Y * 16f - ctx.Me.height * 0.5f - 2f);
        if (Vector2.DistanceSquared(ctx.Me.Center, safe) > 6f * 16f * 6f * 16f)
            AutoWorldTravelController.TravelTo(ctx, safe, 5f);
        else
            background.Maintain(ctx, plan);
    }

    public void CapturePersistentMemory(AutoCompanionState state, CompanionInstance instance) =>
        state?.CaptureMemory(taskRuntime, instance?.Entity, instance?.ReviveTicksLeft ?? 0);

    private AutoContext BuildContext(CompanionInstance instance, Player owner, CompanionProfile profile)
    {
        context ??= new AutoContext();
        context.Instance = instance;
        context.Me = instance.Entity;
        context.Profile = profile;
        context.State = profile.AutoState;
        context.Navigator = navigator;
        context.Actuator = actuator;
        context.Capabilities = capabilities;
        context.TaskRuntime = taskRuntime;
        context.Fishing = fishing;
        return context;
    }

    private void AnnounceBlocked(AutoPlan plan)
    {
        long now = (long)Main.GameUpdateCount;
        if (now - lastBlockedNoticeTick < 1800)
            return;
        lastBlockedNoticeTick = now;
        Main.NewText(Terraria.Localization.Language.GetTextValue(
            "Mods.MoreAIPlayer.CompanionCommand.AutomationBlocked",
            plan.Goal.ToString(), taskRuntime.LastFailureReason.ToString()), 255, 160, 120);
    }

    private AutoTaskResult ExecutePlan(AutoContext ctx, AutoPlan plan)
    {
        switch (plan.Goal)
        {
            case AutoGoalId.GatherWood:
                return AutoResourceController.GatherWood(ctx, plan.Amount);
            case AutoGoalId.GatherStone:
                return AutoResourceController.GatherStone(ctx, plan.Amount);
            case AutoGoalId.MineOre:
                return AutoResourceController.MineBasicOre(ctx, plan.Amount);
            case AutoGoalId.MineIronTierOre:
                return AutoResourceController.MineIronTierOre(ctx, plan.Amount);
            case AutoGoalId.GatherGel:
                return AutoCombatController.GatherGel(ctx);
            case AutoGoalId.CraftStation:
                return ExecuteStation(ctx, plan);
            case AutoGoalId.CraftGear:
                return AutoGearController.EnsureBasicMetalLoadout(ctx);
            case AutoGoalId.CraftConsumable:
                if (!AutoCraftingService.IsAvailable)
                    return AutoTaskResult.Faulted;
                bool crafted = AutoCraftingService.TryCraft(ctx.Instance, ctx.Profile, plan.ItemType);
                return crafted ? AutoTaskResult.Running :
                    AutoCraftingService.IsAvailable ? AutoTaskResult.RetryLater : AutoTaskResult.Faulted;
            case AutoGoalId.BuildShelter:
                return AutoShelterController.Build(ctx);
            case AutoGoalId.DepositSurplus:
                return AutoStorageController.DepositSurplus(ctx);
            case AutoGoalId.LootChest:
                return AutoLootController.LootBestNearbyChest(ctx);
            case AutoGoalId.ExploreWorld:
                return AutoExplorationController.Explore(ctx);
            case AutoGoalId.FishOpportunity:
                return ctx.Fishing.Fish(ctx);
            case AutoGoalId.FightBoss:
                return AutoBossController.Fight(ctx);
            case AutoGoalId.AcquireVitalUpgrade:
                return AutoVitalController.Acquire(ctx);
            case AutoGoalId.ReturnToDeathSite:
                return AutoRecoveryController.ReturnToDeathSite(ctx);
            case AutoGoalId.PrepareArena:
                return AutoArenaController.Prepare(ctx);
            case AutoGoalId.AcquireExplosives:
                return AutoExplosiveController.Acquire(ctx);
            case AutoGoalId.SummonBoss:
                return AutoCampaignController.Summon(ctx);
            case AutoGoalId.RiskRecovery:
                return AutoRecoveryController.RecoverRisk(ctx);
            case AutoGoalId.UpgradeClassLoadout:
                return AutoCampaignGearController.Upgrade(ctx);
            case AutoGoalId.AcquireWeaponUpgrade:
                return AutoVanillaWeaponRouteController.Execute(ctx,
                    (AutoWeaponRouteKind)plan.Amount);
            case AutoGoalId.AcquireCombatSupplies:
                return AutoCombatSupplyController.Acquire(ctx);
            case AutoGoalId.EscapeTerrain:
                return escape.TryMineBodyObstruction(ctx)
                    ? AutoTaskResult.Running : AutoTaskResult.Succeeded;
            case AutoGoalId.Shop:
                // No planner currently emits the generic Shop goal; purchases are explicit in the
                // explosive/ammunition controllers. Fail loudly if future code emits it without a
                // concrete shopping contract instead of silently falling through to Idle.
                return AutoTaskResult.AlternativeRequired;
            case AutoGoalId.Idle:
                navigator.Idle(ctx.Me);
                return plan.IdleReason;
            default:
                navigator.Idle(ctx.Me);
                return AutoTaskResult.Faulted;
        }
    }

    private AutoTaskResult ExecuteStation(AutoContext ctx, AutoPlan plan)
    {
        switch (plan.TileType)
        {
            case TileID.WorkBenches:
                return AutoStationController.EnsureStation(ctx, plan.ItemType, plan.TileType, 2, null, ref ctx.State.WorkBench);
            case TileID.Furnaces:
                return AutoStationController.EnsureStation(ctx, plan.ItemType, plan.TileType, 3, ctx.State.WorkBench, ref ctx.State.Furnace);
            case TileID.Anvils:
                return AutoStationController.EnsureStation(ctx, plan.ItemType, plan.TileType, 2, ctx.State.WorkBench, ref ctx.State.Anvil);
            default:
                return AutoTaskResult.RetryLater;
        }
    }

    /// <summary>Bounded relocation for a non-resource recovery. The destination is absolute and
    /// retained for the whole attempt; the old moving target (<c>me.Center + offset</c>) combined
    /// with one-second direction flips produced visible left/right jumping around obstacles.</summary>
    private void Wander(AutoContext ctx, AutoPlan plan)
    {
        Player me = ctx.Me;
        bool newAttempt = !wanderActive || wanderPlanKey != plan.StableKey ||
            !float.IsFinite(wanderTarget.X) || !float.IsFinite(wanderTarget.Y);
        if (newAttempt)
        {
            wanderActive = true;
            wanderPlanKey = plan.StableKey;
            wanderDir = me.direction == 0 ? 1 : me.direction;
            wanderTarget = me.Center + new Vector2(wanderDir * 30f * 16f, 0f);
            wanderStuckTicks = 0;
            wanderTravelTicks = 0;
            lastWanderX = me.Center.X;
            navigator.Reset();
        }

        if (System.Math.Abs(me.Center.X - lastWanderX) < 4f)
            wanderStuckTicks++;
        else
            wanderStuckTicks = 0;
        lastWanderX = me.Center.X;
        wanderTravelTicks++;

        bool reached = Vector2.DistanceSquared(me.Center, wanderTarget) <
            3f * 16f * 3f * 16f;
        // A real obstacle earns one deliberate reroute after three seconds, never a direction
        // reversal every frame. The long ceiling prevents an absolute waypoint living forever.
        if (reached || wanderStuckTicks > 180 || wanderTravelTicks > 900)
        {
            wanderDir = -wanderDir;
            wanderTarget = me.Center + new Vector2(wanderDir * 30f * 16f, 0f);
            wanderStuckTicks = 0;
            wanderTravelTicks = 0;
            navigator.Reset();
        }

        navigator.Navigate(me, wanderTarget);
    }

    private static void ResetControls(Player me)
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
        me.releaseUseItem = true;
        me.releaseUseTile = true;
    }

    /// <summary>Keeps legally owned minion and sentry staffs active without letting a broken
    /// modded staff monopolise combat. Failed/no-op casts naturally wait two seconds before the
    /// next attempt, while ordinary attacks and movement continue on all intervening ticks.</summary>
    private void MaintainAutomationSummons(CompanionInstance instance, Player me,
        CompanionProfile profile)
    {
        if (me.itemAnimation != 0)
            return;
        long now = (long)Main.GameUpdateCount;
        Item minion = profile.SummonWeapon;
        if (now >= nextMinionSummonTick && CompanionWeaponBehaviors.IsMinionStaff(minion) &&
            me.slotsMinions < me.maxMinions)
        {
            actuator.UseItemInSlot(instance, me, CompanionProfile.SummonSlot,
                me.Center + new Vector2(me.direction * 64f, -16f));
            nextMinionSummonTick = now + 120;
            return;
        }

        Item sentry = profile.SentryWeapon;
        if (now < nextSentrySummonTick || !CompanionWeaponBehaviors.IsSentryStaff(sentry) ||
            CountOwnedSentries(me) >= me.maxTurrets)
            return;
        actuator.UseItemInSlot(instance, me, CompanionProfile.SentrySlot,
            me.Center + new Vector2(me.direction * 64f, -16f));
        nextSentrySummonTick = now + 120;
    }

    private static int CountOwnedSentries(Player me)
    {
        int count = 0;
        foreach (Projectile projectile in Main.ActiveProjectiles)
            if (projectile.owner == me.whoAmI && projectile.sentry)
                count++;
        return count;
    }

    /// <summary>
    /// The automation companion is hard to kill but not invincible. When it dies it stays down for
    /// 1s (5s during a boss fight, measured at the moment of death) and then revives at a safe spot
    /// that does not depend on the owner. Returns true while still dead so the rest of the brain
    /// sits out those ticks. Death drops no items and clears no tasks.
    /// </summary>
    private bool HandleDeathAndRevive(CompanionInstance instance, Player me, CompanionProfile profile)
    {
        AutoCompanionState state = profile.AutoState;
        bool down = me.dead || me.statLife <= 0;
        if (!down && instance.ReviveTicksLeft <= 0)
            return false;

        if (instance.ReviveTicksLeft <= 0)
        {
            bool bossFight = CompanionBrain.IsBossFightActive();
            instance.ReviveTicksLeft = bossFight ? BossReviveTicks : NormalReviveTicks;
            if (state is not null)
            {
                if (float.IsFinite(me.Center.X) && float.IsFinite(me.Center.Y))
                {
                    state.HasDeathReturnTarget = true;
                    state.DeathReturnTarget = me.Center;
                    state.DeathReturnAttempts++;
                }
                state.StrategyVariant++;
                if (bossFight)
                {
                    state.StageFailureCount++;
                    state.BossFailureCount++;
                    taskRuntime.BossFailureCount = state.BossFailureCount;
                    state.ArenaCompleted = false;
                }
                AutoProgressReporter.ReportRecovery(profile, "AutomationDeathRecorded", profile.Name);
            }
            CompanionEntitySystem.ResetCompanionDamageState(instance);
        }

        instance.ReviveTicksLeft--;
        if (instance.ReviveTicksLeft <= 0)
        {
            instance.ReviveTicksLeft = 0;
            me.dead = false;
            me.respawnTimer = 0;
            me.statLife = System.Math.Max(100, me.statLifeMax);
            instance.PersistentLife = me.statLife;
            instance.PersistentLifeInitialized = true;
            instance.LastSharedLifeMax = me.statLifeMax;
            instance.AutoHealCooldownTicks = 0;
            instance.AutoManaCooldownTicks = 0;
            CompanionEntitySystem.ResetCompanionDamageState(instance);
            CompanionBrain.ResetCorpseGibs(me);
            ReviveAtSafeSpot(me, state);
            return true; // revived this tick; resume normally next tick
        }

        // Hold the corpse down and keep vanilla from respawning it before the timer ends.
        me.dead = true;
        me.statLife = 0;
        instance.PersistentLife = 0;
        instance.PersistentLifeInitialized = true;
        me.velocity = Vector2.Zero;
        me.respawnTimer = instance.ReviveTicksLeft + 60;
        return true;
    }

    /// <summary>
    /// Prefer the independently built shelter once its live structure has been verified; the world
    /// spawn remains the safe fallback before a shelter exists or after somebody destroys it.
    /// </summary>
    private static void ReviveAtSafeSpot(Player me, AutoCompanionState state)
    {
        int spawnX = Main.spawnTileX;
        int floorY = Main.spawnTileY;
        if (state?.HomeAnchor is Point home && AutoShelterController.IsShelterComplete(home))
        {
            spawnX = home.X + 6;
            floorY = home.Y + 6;
        }
        me.Center = new Vector2(spawnX * 16f + 8f, floorY * 16f - me.height * 0.5f - 2f);
        me.velocity = Vector2.Zero;
        me.fallStart = (int)(me.position.Y / 16f);
    }
}
