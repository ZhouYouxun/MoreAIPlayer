using Microsoft.Xna.Framework;
using MoreAIPlayer.Companion.AI;
using Terraria;
using Terraria.ID;

namespace MoreAIPlayer.Companion.Automation;

/// <summary>
/// High-level decision making for the world's automation companion. It runs in place of the
/// ordinary <see cref="CompanionBrain"/> whenever <see cref="CompanionProfile.IsAutoRunning"/> is
/// set, and — like the ordinary brain — only writes simulated inputs; vanilla still performs all
/// movement physics, item animation and rendering.
///
/// Death/revive timing is automation-specific (1s / 5s during a boss). Beyond that, each tick it
/// refreshes a capability snapshot, asks the planner for the next goal, and runs the matching
/// controller through the shared navigator/actuator. Phase 2 covers the early resource loop
/// (gather → craft stations → smelt); later phases add world scanning, fishing and combat.
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
    private readonly AutoTaskRuntime taskRuntime = new();
    private readonly AutoWatchdog watchdog;
    private AutoContext context;
    private bool runtimeInitialized;

    // Simple surface wander used when the local area holds no more of what the current goal needs.
    private int wanderDir = 1;
    private int wanderStuckTicks;
    private float lastWanderX;
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

        if (HandleDeathAndRevive(instance, me, profile.AutoState))
            return; // still dead this tick — nothing else runs

        me.statMana = me.statManaMax2;

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
            return;
        }

        AutoContext ctx = BuildContext(instance, owner, profile);
        capabilities.Refresh(me, profile);

        if (!runtimeInitialized)
        {
            taskRuntime.BossFailureCount = profile.AutoState.BossFailureCount;
            runtimeInitialized = true;
        }

        AutoPlan plan = AutoProgressionPlanner.Plan(ctx);
        taskRuntime.CurrentStep = plan.Goal;
        if (watchdog.OnBackoff(plan))
        {
            navigator.Idle(me);
            profile.AutoState.BossFailureCount = taskRuntime.BossFailureCount;
            return;
        }

        AutoTaskResult result = ExecutePlan(ctx, plan);
        if (instance.WorkTile is Point targetTile)
            taskRuntime.LastTargetTile = targetTile;
        AutoWatchdogAction action = watchdog.Observe(ctx, plan, result);
        profile.AutoState.BossFailureCount = taskRuntime.BossFailureCount;

        switch (action)
        {
            case AutoWatchdogAction.Recover:
                Wander(ctx);
                break;
            case AutoWatchdogAction.Wait:
                navigator.Idle(me);
                break;
            case AutoWatchdogAction.Blocked:
                navigator.Idle(me);
                AnnounceBlocked(plan);
                break;
        }
    }

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
            case AutoGoalId.Idle:
            default:
                navigator.Idle(ctx.Me);
                return plan.IdleReason;
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

    /// <summary>Crude surface exploration: walk one way until wedged, then flip. Real cross-region
    /// navigation and interest-point search arrive with the world-knowledge phase.</summary>
    private void Wander(AutoContext ctx)
    {
        Player me = ctx.Me;
        if (System.Math.Abs(me.Center.X - lastWanderX) < 8f)
            wanderStuckTicks++;
        else
            wanderStuckTicks = 0;
        lastWanderX = me.Center.X;

        if (wanderStuckTicks > 60)
        {
            wanderDir = -wanderDir;
            wanderStuckTicks = 0;
        }

        navigator.Navigate(me, me.Center + new Vector2(wanderDir * 30f * 16f, 0f));
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

    /// <summary>
    /// The automation companion is hard to kill but not invincible. When it dies it stays down for
    /// 1s (5s during a boss fight, measured at the moment of death) and then revives at a safe spot
    /// that does not depend on the owner. Returns true while still dead so the rest of the brain
    /// sits out those ticks. Death drops no items and clears no tasks.
    /// </summary>
    private bool HandleDeathAndRevive(CompanionInstance instance, Player me, AutoCompanionState state)
    {
        bool down = me.dead || me.statLife <= 0;
        if (!down && instance.ReviveTicksLeft <= 0)
            return false;

        if (instance.ReviveTicksLeft <= 0)
        {
            instance.ReviveTicksLeft = CompanionBrain.IsBossFightActive() ? BossReviveTicks : NormalReviveTicks;
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
