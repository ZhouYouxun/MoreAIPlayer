using Microsoft.Xna.Framework;
using MoreAIPlayer.Companion.Building;
using MoreAIPlayer.Companion.Commands;
using MoreAIPlayer.Reflection.CalamityMod;
using Terraria;
using Terraria.ID;
using Terraria.Localization;
using Terraria.ModLoader;

namespace MoreAIPlayer.Companion.AI;

/// <summary>
/// High-level decision making for one companion. Runs before the vanilla player update and
/// only writes simulated inputs (movement controls, selected item, use-item, virtual aim);
/// vanilla then performs the actual movement, item animation, mining and combat.
/// </summary>
public sealed class CompanionBrain
{
    private const float IdleDistance = 96f;
    private const float TeleportDistance = 800f;
    private const int TeleportDelayTicks = 240;
    // A companion this close is already beside its owner. No automatic recall is allowed inside
    // this radius, including the stuck and vertical-follow safeguards below.
    private const float NoTeleportDistance = 3f * 16f;
    private const int RescueCooldownTicks = 60;
    private const float AutomaticAggroRange = 15f * 16f;
    private const float MaximumCombatRange = 800f;
    private const float RangedStandoffMin = 180f;
    private const float RangedStandoffMax = 700f;
    private const float GenericProjectileUseRange = 760f;

    // Guard response: how badly the companion has to be swarmed before the guard slot takes
    // over the fight. Crowd size relaxes how close that crowd has to be — a lone enemy still
    // has to get in the companion's face, four or more trigger from across the scan radius.
    private const float GuardScanRadius = 340f;
    private const float GuardLoneRange = 130f;
    private const float GuardCrowdRange = 220f;
    private const int GuardMidCrowd = 2;
    private const int GuardFullCrowd = 4;
    // Once engaged the guard weapon is held briefly even as the crowd thins, so a companion
    // on the edge of the threshold does not flicker between weapons every few ticks.
    private const int GuardStickyTicks = 45;

    // How close a netted critter has to land for the companion to pocket it.
    private const float CritterPickupRange = 6f * 16f;

    // Mining: when nothing is within pick reach, how far from the owner's dig point the companion
    // may sit before it closes the gap. Kept just past a pick's reach so it digs shoulder to
    // shoulder with the owner instead of hanging back and swinging at nothing.
    private const float MiningApproachDistance = 5f * 16f;
    private const int SustainedTunnelTicks = 120; // two seconds of one deliberate mining direction
    private const float MiningSnapDistance = 10f;
    private const float MiningDarknessThreshold = 0.24f;
    private const int MiningLightCooldownTicks = 180;

    // Fishing mirrors vanilla's basic pond thresholds: fewer than 75 liquid tiles cannot fish,
    // while 300 is the normal full-power target. The local scan is capped at that useful target
    // and refreshed infrequently, so a lake full of companions does not become a per-frame world
    // flood-fill.
    private const int FishingMinimumWaterTiles = 75;
    private const int FishingIdealWaterTiles = 300;
    private const int FishingSearchRadiusTiles = 26;
    private const int FishingVerticalSearchTiles = 18;
    private const int FishingPoolHalfWidth = 36;
    private const int FishingPoolDepthScan = 80;
    private const int FishingRescanTicks = 180;

    // Escort mode: bodyguard spacing, tighter than the roaming follow distance.
    private const float EscortFollowSide = 34f;
    private const float EscortIdleDistance = 30f;

    // Squad spacing: how far apart squadmates fan out around the shared follow point so multiple
    // companions read as a loose group ("spring outing") instead of stacking on one spot. Escort
    // lines up tighter than a loose Attack follow.
    private const float AttackSpreadSpacing = 32f;
    private const float EscortSpreadSpacing = 20f;
    // How near the owner an enemy has to be before an escort engages it, and how near itself
    // before it defends regardless. Escort answers threats; it does not go hunting.
    private const float EscortGuardRadius = 20f * 16f;
    private const float EscortSelfDefenseRadius = 12f * 16f;

    // Garrison / posted-hold: how far the companion may stray from its anchor to fight before
    // it is dragged back, and how close counts as "standing on the spot".
    private const float HoldLeash = 8f * 16f;
    private const float HoldReturnDistance = 20f;

    // Free-roam: the leash radius around the owner the companion wanders and hunts within, and how
    // close counts as reaching a wander goal. FreeRoam is NOT a posted mode — the ordinary
    // far-away / stuck / rescue teleport policy still recalls it; this only bounds where it
    // chooses to go and which enemies it is willing to chase.
    private const float FreeRoamLeash = 40f * 16f;
    private const float FreeRoamArrive = 24f;
    private const int FreeRoamGoalTimeout = 300; // give up on an unreachable wander goal after 5s

    // Command-book waypoint: how close counts as "arrived".
    private const float WaypointArriveDistance = 24f;

    // Stuck-teleport: while actively trying to travel, failing to advance this far for this
    // many ticks means the companion is wedged and recalls to the owner.
    private const float StuckProgressDistance = 24f;
    private const int StuckTeleportTicks = 120; // 2s

    // Vertical follow rescue: a companion below its owner still gets several genuine jump
    // attempts to solve terrain normally. If it keeps trying but gains no meaningful height,
    // recall it instead of leaving it to hop below a cliff forever.
    private const float VerticalRescueMinimumHeight = 8f * 16f;
    private const float VerticalRescueProgressDistance = 24f;
    private const int VerticalRescueJumpAttempts = 3;
    private const int VerticalRescueNoProgressLimit = 90;

    // Revive delay after a death that vanilla actually landed.
    private const int NormalReviveTicks = 300; // 5s
    private const int BossReviveTicks = 600;   // 10s during a boss fight

    // A single-tick owner position jump this large is a teleport (mirror, recall, teleporter,
    // Rod of Discord…), not running — companions follow it instantly instead of waiting out
    // the far-away timer. Far above any continuous movement speed.
    private const float OwnerTeleportJump = 40f * 16f;

    private readonly CompanionNavigator navigator = new();
    private readonly CompanionThreatAvoidance threatAvoidance = new();

    private int currentTarget = -1;
    private int retargetTimer;
    private int farFromOwnerTicks;
    private int rescueCooldown;

    // Mode/command movement state.
    private CompanionMode lastMode = CompanionMode.Attack;
    private Vector2 garrisonMark;          // spot marked on entering Garrison mode
    private bool softHoldActive;           // holding a reached waypoint while NOT in Garrison
    private Vector2 softAnchor;
    private bool holdActive;               // resolved each tick: is a hold anchor in force?
    private Vector2 holdAnchor;

    // Leash applied to combat navigation while holding a post (garrison / reached waypoint), or
    // while an escort/free-roamer chases anchored to the owner. The radius varies by case: a tight
    // hold/escort leash, or the wide free-roam leash.
    private bool combatLeashActive;
    private Vector2 combatLeashAnchor;
    private float combatLeashRadius = HoldLeash;

    // Free-roam wander state: the current wander goal, whether one is set, the pause timer at a
    // reached spot, the give-up timer for an unreachable goal, and the glance-around timer.
    private Vector2 freeRoamGoal;
    private bool hasFreeRoamGoal;
    private int freeRoamDwellTicks;
    private int freeRoamGoalTicks;
    private int freeRoamFlipTimer;

    // Fishing keeps one good water aim briefly instead of re-measuring the same pond every tick.
    private FishingSpot? fishingSpot;
    private int fishingSpotScanCooldown;

    // Tunnel lighting is intentionally occasional: one companion should light a dark tunnel,
    // not burn through an entire backpack of torches while the player holds the mouse down.
    private int miningLightCooldown;

    // Stuck-teleport tracking.
    private Vector2 stuckReference;
    private int notProgressingTicks;

    // Vertical-follow rescue tracking. The best reached Y is deliberately kept across a jump's
    // rise and fall: hopping up a little and landing back at the same ledge is not progress.
    private bool verticalRescueActive;
    private float verticalRescueBestY;
    private int verticalRescueNoProgressTicks;
    private int verticalRescueJumpAttempts;
    private bool verticalRescueJumpHeld;

    // Owner-teleport detection (compare the owner's per-tick position jump).
    private Vector2 lastOwnerCenter;
    private bool ownerPositionKnown;

    // Follow-position side switching with a human-like reaction delay.
    private int followSide;
    private int lastOwnerDirection;
    private int sideSwitchTimer;

    // 弹金币 (coin volley) script state for Midas Prime / Crackshot Colt.
    private int coinVolleyThrowsLeft = 4;
    private bool coinVolleyShootPhase;
    private string coinVolleyWeaponKey;

    private int lastItemAnimationSeen;
    private bool lastUseCompleted;
    private bool itemUseClaimedThisTick;

    // Summon upkeep: filling the summon slots outranks attacking, so it needs a way out of
    // a staff that never fills them (a modded minion taking 0 slots, a slot cap of 0).
    private bool summonedLastTick;
    private bool summonedSentryLastTick;
    private float summonProgressAtStart;
    private int fruitlessMinionSummons;
    private int fruitlessSentrySummons;
    private int minionSummonBackoff;
    private int sentrySummonBackoff;

    // Attack feedback is deliberately local to Attack / Free Roam. Those two modes are allowed
    // to adapt their position; Escort and Garrison must respect their defensive posts instead.
    private int consecutiveCloseCombatMisses;
    private int consecutiveRangedMisses;
    private bool pendingAdaptiveAttack;
    private bool pendingAttackWasCloseCombat;
    private int pendingHitSerial;
    private int pendingAttackAge;
    private int feedbackTarget = -1;
    private string feedbackWeaponKey;

    // A projectile can still be travelling when a rapid-fire weapon's use animation ends. Give
    // ranged shots half a second to report a hit before calling them a miss; close weapons are
    // resolved as soon as their swing ends.
    private const int RangedFeedbackWindowTicks = 30;

    // Ticks the guard weapon stays out after the swarm that summoned it has broken up.
    private int guardHoldTicks;

    // Watchdog for an item animation that never ends: how long the brain waits for the slot it
    // wants before taking it by force. 1.5s is longer than any legitimate weapon animation.
    private const int BlockedAttackForceTicks = 90;
    private const float FlailSpinDistance = 10f * 16f;
    private int blockedAttackTicks;

    private readonly record struct FishingSpot(Vector2 AimWorld, int WaterTiles);

    public bool ProtectCritters { get; private set; }

    /// <summary>Why the companion is or is not attacking, refreshed every tick and surfaced in
    /// the backpack panel so a companion standing idle can be diagnosed at a glance.</summary>
    public CompanionAttackState AttackState { get; private set; } = CompanionAttackState.NoTarget;

    /// <summary>Name of the weapon the brain settled on, for the same readout. Empty when none.</summary>
    public string ChosenWeaponName { get; private set; } = string.Empty;
    public string AvoidanceStateText => threatAvoidance.StateText;
    public float AvoidanceRisk => threatAvoidance.CurrentRisk;
    public int VisibleThreats => threatAvoidance.VisibleThreats;

    public void Update(CompanionInstance instance, Player owner, CompanionProfile profile)
    {
        Player me = instance.Entity;

        // A weapon use "completed" when the animation ran last tick and has now ended.
        lastUseCompleted = lastItemAnimationSeen > 0 && me.itemAnimation == 0;
        lastItemAnimationSeen = me.itemAnimation;

        if (pendingAdaptiveAttack)
        {
            pendingAttackAge++;
            bool feedbackReady = pendingAttackWasCloseCombat
                ? lastUseCompleted
                : pendingAttackAge >= RangedFeedbackWindowTicks;
            if (feedbackReady)
            {
                CompanionPlayer selfData = me.GetModPlayer<CompanionPlayer>();
                bool hitLanded = selfData.CombatHitSerial != pendingHitSerial;
                if (pendingAttackWasCloseCombat)
                    consecutiveCloseCombatMisses = hitLanded ? 0 : System.Math.Min(consecutiveCloseCombatMisses + 1, 4);
                else
                    consecutiveRangedMisses = hitLanded ? 0 : System.Math.Min(consecutiveRangedMisses + 1, 5);
                pendingAdaptiveAttack = false;
            }
        }

        ResetControls(me);
        navigator.UpdateOwnedDoors(me);
        itemUseClaimedThisTick = false;
        instance.WorkTile = null;
        instance.WantsAltUse = false;
        instance.WantsUseItem = false;
        instance.TickItemUseBackoff();
        instance.AimWorld = me.Center + new Vector2(me.direction * 160f, 0f);
        AttackState = CompanionAttackState.NoTarget;
        ChosenWeaponName = string.Empty;

        if (HandleDeathAndRevive(instance, me, owner))
        {
            threatAvoidance.Reset(me);
            return; // still dead this tick — nothing else runs
        }
        me.statMana = me.statManaMax2;

        // Catch mode grants temporary critter protection. A Guide to Critter Companionship in
        // this companion's backpack makes the same protection permanent in every work mode.
        CompanionMode mode = owner.dead ? CompanionMode.Attack : CompanionModeRules.Effective(profile);
        ProtectCritters = mode == CompanionMode.Catch || profile.HasCritterCompanionshipGuide;

        // Summon upkeep comes before everything else that uses an item: the companion tops up
        // its summon slots first, and only then falls through to the next non-summon weapon
        // in hotbar order. A re-summoned companion starts with no minions of its own, so this
        // naturally re-casts every staff without any extra bookkeeping.
        MaintainSummons(instance, me, profile);

        // Resolve mode transitions and the command-book waypoint into a hold anchor.
        if (!owner.dead)
            UpdateCommandState(instance, me, profile);
        else
        {
            holdActive = false;
            combatLeashActive = false;
        }

        CompanionPlayer ownerData = owner.GetModPlayer<CompanionPlayer>();
        // Falls back to Attack when the mode's tool slot was emptied behind its back.
        if (ProtectCritters)
            CollectCaughtCritters(me, profile);

        // Escort never seeks out enemies — it just shadows the owner. Every other mode targets
        // and attacks identically; only the movement below differs.
        // Evaluated unconditionally so the sticky timer keeps ticking down even on frames with
        // no target at all.
        bool guarding = GuardEngaged(me, out NPC nearestThreat);

        // Free-roam is a proactive hunter: it engages any valid enemy inside its leash around the
        // owner, not just close threats and bosses. Other modes pass 0 and keep the old gating.
        float huntRadius = mode == CompanionMode.FreeRoam ? FreeRoamLeash : 0f;
        NPC target = SelectTarget(me, owner, ownerData, profile, huntRadius);

        // Escort guards rather than hunts. It previously refused to target anything at all,
        // which is not what "follow and defend" means: a bodyguard that stands next to its
        // charge and watches it get eaten is indistinguishable from a broken companion, and
        // was in fact repeatedly reported as one — every weapon appeared to stop working,
        // because the mode never selected a weapon in the first place. It now fights whatever
        // comes to the owner, and only refuses to go looking for trouble.
        if (mode == CompanionMode.Escort && target is not null && !IsThreatToOwner(target, owner, me))
            target = null;

        CompanionWeaponPlan plan = target is null ? null : CompanionWeaponBehaviors.SelectWeapon(
            me, target, profile, instance.BlockedItemUseInventorySlot);

        // Aiming at a known target is unconditional, decided here rather than inside TryAttack.
        // Every path that declines to attack — out of range, no line of sight, leashed to an
        // anchor, mode suppression, no weapon at all — used to leave the aim vector at its idle
        // "160px straight ahead" value, so a companion that was not shooting was also visibly
        // pointing the wrong way, and the two symptoms were easy to mistake for one fault. The
        // companion now always looks at what it is thinking about, whatever it decides to do.
        if (target is not null)
            instance.AimWorld = target.Center;

        if (target is not null && plan is null)
            AttackState = CompanionAttackState.NoWeapon;

        // Catch mode puts netting ahead of fighting. Only a threat close enough to trip the
        // swarm test — the same "it is in my face" measure the guard slot uses — is worth
        // abandoning a critter for.
        if (mode == CompanionMode.Catch && !guarding)
        {
            target = null;
            plan = null;
            AttackState = CompanionAttackState.ModeSuppressed;
        }

        // Being swarmed overrides the weapon rotation outright. The target is pulled onto the
        // closest body as well: a "guard" response that keeps firing at the boss across the
        // arena while six slimes eat the companion is not guarding anything.
        if (guarding && target is not null &&
            CompanionWeaponBehaviors.GuardPlan(profile, instance.BlockedItemUseInventorySlot) is CompanionWeaponPlan guardPlan)
        {
            plan = guardPlan;
            target = nearestThreat ?? target;
            instance.AimWorld = target.Center;
        }

        bool traveling = !owner.dead && instance.Waypoint.HasValue;

        // A posted companion (garrison, or a reached waypoint) refuses to chase a target it
        // could not reach without abandoning its spot. It still fires at anything it can hit
        // from within its leash — that filtering is what keeps it near the anchor.
        if (plan is not null && holdActive && !traveling && !CanEngageFromHold(me, target, plan))
        {
            target = null;
            plan = null;
            AttackState = CompanionAttackState.HoldLeashed;
        }

        profile.ActiveWeaponSlot = plan?.Slot ?? -1;
        ChosenWeaponName = plan is null ? string.Empty : plan.Weapon.Name;

        // Movement. Attack behaviour (TryAttack) is shared; where the companion stands is what
        // each mode changes.
        if (traveling)
        {
            NavigateToWaypoint(instance, me);
            if (target is not null && plan is not null)
                TryAttack(instance, me, target, plan, profile, mode is CompanionMode.Attack or CompanionMode.FreeRoam); // shoot on the move
        }
        else if (target is not null && plan is not null)
        {
            // An escort chasing a target must not drift off its charge, so it fights on the
            // same leash a posted companion uses — anchored to the owner rather than a spot. A
            // free-roamer chases on the same principle but with a much longer leash: it hunts
            // across its roam zone yet still cannot be dragged to the far side of the map.
            bool escortLeash = mode == CompanionMode.Escort && !holdActive;
            bool freeRoamLeash = mode == CompanionMode.FreeRoam && !holdActive;
            combatLeashActive = holdActive || escortLeash || freeRoamLeash;
            combatLeashAnchor = holdActive ? holdAnchor : owner.Center;
            combatLeashRadius = freeRoamLeash ? FreeRoamLeash : HoldLeash;
            bool adaptiveCombat = mode is CompanionMode.Attack or CompanionMode.FreeRoam;
            PositionForCombat(me, target, plan, adaptiveCombat);
            TryAttack(instance, me, target, plan, profile, adaptiveCombat);
            combatLeashActive = false;
        }
        else if (holdActive)
        {
            HoldAtAnchor(me, owner);
        }
        else if (profile.WheelCommandsActive && profile.WorkMode == CompanionWorkMode.Exploration &&
                 profile.ExplorationLighting && TryLightAtCurrentPosition(instance, me, profile))
        {
        }
        else if (profile.WheelCommandsActive && profile.WorkMode == CompanionWorkMode.Farming &&
                 TryPerformMiningOrTreeWork(instance, me, owner, ownerData, profile))
        {
        }
        else if (mode == CompanionMode.Catch && TryCatchNearbyCritter(instance, me, profile))
        {
        }
        else if (mode == CompanionMode.Mining && TryPerformMiningOrTreeWork(instance, me, owner, ownerData, profile))
        {
        }
        else if (mode == CompanionMode.Fishing)
        {
            TryFishNearby(instance, me, profile);
        }
        else if (mode == CompanionMode.FreeRoam)
        {
            // 自由行动: wander the owner's vicinity on its own when there is nothing to fight.
            // Placed ahead of build/follow so a free-roamer neither builds nor shadows the owner.
            FreeRoamWander(instance, profile, me, owner);
        }
        else if ((!profile.WheelCommandsActive || profile.WorkMode == CompanionWorkMode.Building) &&
                 CompanionBuildingSystem.ShouldRecallForBuild(instance, owner))
        {
            // Battle platform: the companion fell/was knocked off the bridge while the owner is
            // still up on it. Snap back to the owner's side and resume laying platforms.
            TeleportBeside(me, owner);
        }
        else if ((!profile.WheelCommandsActive || profile.WorkMode == CompanionWorkMode.Building) &&
                 CompanionBuildingSystem.TryPerformBuildWork(instance, owner, profile, navigator))
        {
        }
        // The public contract is that companions never take world drops on their own. More
        // importantly, making every companion path to fresh loot turns a boss loot pile into
        // several concurrent A* searches, which can stall the game. The backpack remains a
        // manual storage surface: put items in, transfer them out, or use Drop All yourself.
        else if (mode == CompanionMode.Escort)
        {
            CompanionEntitySystem.SquadPosition(instance, out int ordinal, out int squadCount);
            FollowOwner(me, owner, EscortFollowSide, EscortIdleDistance, ordinal, squadCount, EscortSpreadSpacing);
        }
        else if (!owner.dead)
        {
            CompanionEntitySystem.SquadPosition(instance, out int ordinal, out int squadCount);
            FollowOwner(me, owner, 64f, IdleDistance * 0.5f, ordinal, squadCount, AttackSpreadSpacing);
        }
        else
            navigator.Idle(me);

        // Calamity normally advances dashes only for Main.myPlayer. Continue a companion dash
        // under its virtual-player context before avoidance possibly starts a new one.
        CalamityModReflection.UpdateActiveDash(me, instance.AimWorld);

        // Survival may temporarily override the movement chosen above, while ordinary combat
        // and aiming continue unless a grapple needs the virtual cursor for this tick.
        threatAvoidance.Update(instance, owner, profile);

        // A settled garrison companion can brace against knockback by hooking straight down.
        // Threat avoidance owns the hook as soon as danger is noticed.
        bool settledAtGarrison = !owner.dead && mode == CompanionMode.Garrison &&
            holdActive && !traveling &&
            System.Math.Abs(me.Center.X - holdAnchor.X) <= HoldReturnDistance &&
            System.Math.Abs(me.Center.Y - holdAnchor.Y) <= 96f;
        threatAvoidance.UpdateGarrisonAnchor(instance, profile, settledAtGarrison);

        if (!owner.dead)
            UpdateTeleportPolicy(instance, me, owner, ownerData, profile, mode, holdActive || traveling);
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
    /// Companions are hard to kill but not truly invincible. When one does die it stays down
    /// for a few seconds and then revives at the owner's side — 5s normally, 10s during a boss
    /// fight (measured at the moment of death). Returns true while still dead so the rest of
    /// the brain sits out those ticks. The countdown itself is drawn over the owner's head.
    /// </summary>
    private bool HandleDeathAndRevive(CompanionInstance instance, Player me, Player owner)
    {
        bool down = me.dead || me.statLife <= 0;
        if (!down && instance.ReviveTicksLeft <= 0)
            return false;

        // First tick of a fresh death: pick the timer.
        if (instance.ReviveTicksLeft <= 0)
        {
            instance.ReviveTicksLeft = IsBossFightActive() ? BossReviveTicks : NormalReviveTicks;
            CompanionEntitySystem.ResetCompanionDamageState(instance);
            if (MoreAIPlayerClientConfig.Current?.UseChatForCompanionReviveTimer == true)
            {
                int seconds = (int)System.Math.Ceiling(instance.ReviveTicksLeft / 60f);
                Main.NewText(Language.GetTextValue(
                    "Mods.MoreAIPlayer.CompanionCommand.ReviveChatStarted", me.name, seconds), 255, 120, 110);
            }
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
            ResetCorpseGibs(me);
            TeleportBeside(me, owner);
            if (MoreAIPlayerClientConfig.Current?.UseChatForCompanionReviveTimer == true)
            {
                Main.NewText(Language.GetTextValue(
                    "Mods.MoreAIPlayer.CompanionCommand.ReviveChatFinished", me.name), 120, 255, 150);
            }
            if (instance.TryGetContext(out _, out CompanionProfile profile))
                profile.Mode = CompanionMode.Attack;
            softHoldActive = false;
            instance.Waypoint = null;
            instance.WaypointTimeLeft = 0;
            // The owner may have wandered far during the down-time; reseed the teleport
            // tracker so that catch-up is not misread as an owner teleport next tick.
            ownerPositionKnown = false;
            return true; // revived this tick; resume normally next tick
        }

        // Hold the corpse down and keep vanilla from respawning it before the timer ends.
        me.dead = true;
        me.statLife = 0;
        instance.PersistentLife = 0;
        instance.PersistentLifeInitialized = true;
        me.velocity = Vector2.Zero;
        me.respawnTimer = instance.ReviveTicksLeft + 60;
        navigator.Idle(me);
        return true;
    }

    /// <summary>
    /// Undoes the corpse drift a death leaves in the head/body/leg draw offsets.
    ///
    /// Player.UpdateDead integrates those offsets every tick and piles gravity onto their
    /// velocities (Player.cs:14367-14378), so the displacement grows quadratically: a five
    /// second revive timer alone is worth thousands of pixels. They are not a death-only
    /// rendering path — PlayerDrawLayers and PlayerDrawHeadLayers fold them into the normal
    /// layer positions, and not every layer reads them, which is what tears the sprite apart.
    ///
    /// Vanilla only ever zeroes them inside Player.Spawn, and a companion's revive is written
    /// here instead of going through Spawn, so the reset has to happen explicitly. Vanilla's
    /// other reset (Player.KillMe) never runs for a companion either — Player.Hurt gates the
    /// KillMe call on whoAmI == Main.myPlayer (Player.cs:31607), which is also why a dying
    /// companion drops no tombstone.
    /// </summary>
    internal static void ResetCorpseGibs(Player me)
    {
        me.headPosition = Vector2.Zero;
        me.bodyPosition = Vector2.Zero;
        me.legPosition = Vector2.Zero;
        me.headRotation = 0f;
        me.bodyRotation = 0f;
        me.legRotation = 0f;
        // Spawn leaves these alone because vanilla reseeds them in KillMe. With neither ever
        // running, UpdateDead's gravity is the only thing that touches them, and without this
        // each death would resume accelerating from where the last one stopped.
        me.headVelocity = Vector2.Zero;
        me.bodyVelocity = Vector2.Zero;
        me.legVelocity = Vector2.Zero;
    }

    internal static bool IsBossFightActive()
    {
        foreach (NPC npc in Main.ActiveNPCs)
        {
            if (npc.boss || NPCID.Sets.ShouldBeCountedAsBoss[npc.type])
                return true;
        }
        return false;
    }

    private NPC SelectTarget(Player me, Player owner, CompanionPlayer data, CompanionProfile profile, float huntRadius = 0f)
    {
        // The wheel's Idle shell is intentionally peaceful. The old books still select their
        // historical modes directly and therefore retain their exact combat behaviour.
        if (profile.WheelCommandsActive && profile.WorkMode == CompanionWorkMode.Idle)
        {
            currentTarget = -1;
            return null;
        }
        CompanionInstance commandInstance = CompanionEntitySystem.GetBySlot(me.whoAmI);
        // A failed search deliberately has the same short cache as a successful one.
        // Previously currentTarget == -1 bypassed this block, causing the companion to
        // enumerate every active NPC on every idle frame. Besides being unnecessary, a
        // populated world made an otherwise stationary companion impose a permanent CPU
        // cost. A new scan still happens promptly every 12 ticks, and any valid cached
        // target keeps the original behaviour.
        if (retargetTimer > 0 && currentTarget < 0)
        {
            retargetTimer--;
            return null;
        }

        if (retargetTimer > 0 && IsValidTarget(currentTarget))
        {
            retargetTimer--;
            return Main.npc[currentTarget];
        }

        retargetTimer = 12;
        currentTarget = -1;
        float bestScore = float.MinValue;

        foreach (NPC candidate in Main.ActiveNPCs)
        {
            if (!IsValidTarget(candidate.whoAmI))
                continue;

            float ownerDistance = Vector2.Distance(owner.Center, candidate.Center);
            float companionDistance = Vector2.Distance(me.Center, candidate.Center);
            bool utilityWork = profile.WheelCommandsActive &&
                profile.WorkMode is (CompanionWorkMode.Exploration or CompanionWorkMode.Building or CompanionWorkMode.Farming);
            if (utilityWork && System.Math.Min(ownerDistance, companionDistance) > 20f * 16f)
                continue;
            if (profile.WheelCommandsActive && profile.WorkMode == CompanionWorkMode.Combat &&
                profile.CombatCommand == CombatCommand.Patrol && instanceCommandAreaRejects(candidate))
                continue;
            if (ownerDistance > MaximumCombatRange && companionDistance > MaximumCombatRange)
                continue;

            bool requested = data.CombatRequestTime > 0 && data.RequestedTarget == candidate.whoAmI;
            bool closeThreat = System.Math.Min(ownerDistance, companionDistance) <= AutomaticAggroRange;
            bool boss = candidate.boss;
            bool miniBoss = NPCID.Sets.ShouldBeCountedAsBoss[candidate.type] || candidate.rarity > 0 || candidate.lifeMax >= 10000;
            // Free-roam hunting: anything inside the leash counts as fair game, so the companion
            // actively clears its roam zone instead of waiting for enemies to wander into aggro range.
            bool inHuntZone = huntRadius > 0f && System.Math.Min(ownerDistance, companionDistance) <= huntRadius;
            if (!requested && !closeThreat && !boss && !miniBoss && !inHuntZone)
                continue;

            float score = ScoreTarget(candidate, ownerDistance, companionDistance, requested, closeThreat, boss, miniBoss);
            if (profile.WheelCommandsActive && profile.WorkMode == CompanionWorkMode.Combat)
            {
                score += profile.CombatCommand switch
                {
                    CombatCommand.SelfDefense => -companionDistance * 24f,
                    CombatCommand.ProtectOwner => requested ? 900_000f : -ownerDistance * 24f,
                    CombatCommand.SharedTarget => requested ? 1_500_000f : 0f,
                    CombatCommand.Patrol when commandInstance?.HasCommandArea == true =>
                        -Vector2.Distance(commandInstance.CommandAnchor, candidate.Center) * 18f,
                    _ => 0f
                };
            }
            if (score > bestScore)
            {
                bestScore = score;
                currentTarget = candidate.whoAmI;
            }
        }

        return IsValidTarget(currentTarget) ? Main.npc[currentTarget] : null;

        bool instanceCommandAreaRejects(NPC candidate)
        {
            return commandInstance is not null && commandInstance.HasCommandArea &&
                !commandInstance.CommandArea.Contains(candidate.Center.ToPoint());
        }
    }

    /// <summary>
    /// Whether an enemy counts as something an escort should deal with: close to the person it
    /// is guarding, or already on top of the escort itself. Anything further away is someone
    /// else's problem — that restraint is the whole difference between Escort and Attack.
    /// </summary>
    private static bool IsThreatToOwner(NPC target, Player owner, Player me) =>
        Vector2.Distance(owner.Center, target.Center) <= EscortGuardRadius ||
        Vector2.Distance(me.Center, target.Center) <= EscortSelfDefenseRadius;

    /// <summary>
    /// True while the companion should be answering with its guard weapon, including the brief
    /// sticky window after the crowd breaks up. <paramref name="nearestThreat"/> is the closest
    /// enemy inside the scan radius, or null when nothing is in range.
    /// </summary>
    private bool GuardEngaged(Player me, out NPC nearestThreat)
    {
        if (ScanForSwarm(me, out nearestThreat))
        {
            guardHoldTicks = GuardStickyTicks;
            return true;
        }

        if (guardHoldTicks > 0)
        {
            guardHoldTicks--;
            return true;
        }
        return false;
    }

    /// <summary>
    /// The swarm test. The old behaviour keyed the close-quarters response off distance alone,
    /// which read one adjacent enemy exactly like six closing in — so a crowd only ever tripped
    /// it once something was already touching the companion, far too late to matter. Here the
    /// crowd size is what relaxes the trigger distance: one enemy has to reach
    /// <see cref="GuardLoneRange"/>, two or three <see cref="GuardCrowdRange"/>, and four or
    /// more trip it from anywhere inside <see cref="GuardScanRadius"/>.
    /// </summary>
    private bool ScanForSwarm(Player me, out NPC nearestThreat)
    {
        nearestThreat = null;
        int crowd = 0;
        float nearestDistance = float.MaxValue;

        foreach (NPC candidate in Main.ActiveNPCs)
        {
            if (!IsValidTarget(candidate.whoAmI))
                continue;

            float distance = Vector2.Distance(me.Center, candidate.Center);
            if (distance > GuardScanRadius)
                continue;

            crowd++;
            if (distance < nearestDistance)
            {
                nearestDistance = distance;
                nearestThreat = candidate;
            }
        }

        if (crowd == 0)
            return false;

        float triggerRange = crowd >= GuardFullCrowd
            ? GuardScanRadius
            : crowd >= GuardMidCrowd
                ? GuardCrowdRange
                : GuardLoneRange;
        return nearestDistance <= triggerRange;
    }

    private bool IsValidTarget(int npcIndex)
    {
        if (npcIndex < 0 || npcIndex >= Main.maxNPCs)
            return false;

        NPC target = Main.npc[npcIndex];
        if (ProtectCritters && target.catchItem > 0)
            return false;
        return target.active && !target.friendly && !target.dontTakeDamage && !target.immortal && target.lifeMax > 5;
    }

    private float ScoreTarget(NPC target, float ownerDistance, float companionDistance, bool requested, bool closeThreat, bool boss, bool miniBoss)
    {
        float score = 0f;
        if (boss)
            score += 1_000_000f;
        else if (miniBoss)
            score += 500_000f;
        if (requested)
            score += 250_000f;
        if (closeThreat)
            score += 80_000f;

        int clusterSize = CountHostilesNear(target.Center, 10f * 16f);
        score += clusterSize * 12_000f;
        score += System.Math.Min(target.lifeMax, 100_000) * 0.2f;
        score -= System.Math.Min(ownerDistance, companionDistance) * 8f;
        return score;
    }

    private int CountHostilesNear(Vector2 center, float radius)
    {
        int count = 0;
        float radiusSquared = radius * radius;
        foreach (NPC candidate in Main.ActiveNPCs)
        {
            if (IsValidTarget(candidate.whoAmI) && Vector2.DistanceSquared(center, candidate.Center) <= radiusSquared)
                count++;
        }
        return count;
    }

    /// <summary>
    /// Keeps the companion's own summon slots filled from the staffs in its hotbar. The
    /// companion is a real player, so vanilla owns the slot accounting entirely: ResetEffects
    /// gives it the same base of one minion slot plus whatever its equipment adds, and
    /// Projectile.Update recounts slotsMinions against every live minion each tick.
    /// </summary>
    private void MaintainSummons(CompanionInstance instance, Player me, CompanionProfile profile)
    {
        // A finished cast that produced no minion means this staff will never satisfy the
        // slot check. Attacking matters more than retrying it every animation cycle.
        if (summonedLastTick && lastUseCompleted)
        {
            float progress = summonedSentryLastTick ? CountOwnedSentries(me) : me.slotsMinions;
            if (progress > summonProgressAtStart)
            {
                if (summonedSentryLastTick)
                    fruitlessSentrySummons = 0;
                else
                    fruitlessMinionSummons = 0;
            }
            else if (summonedSentryLastTick && ++fruitlessSentrySummons >= 3)
            {
                fruitlessSentrySummons = 0;
                sentrySummonBackoff = 600;
            }
            else if (!summonedSentryLastTick && ++fruitlessMinionSummons >= 3)
            {
                fruitlessMinionSummons = 0;
                minionSummonBackoff = 600;
            }
        }
        summonedLastTick = false;

        if (minionSummonBackoff > 0)
            minionSummonBackoff--;
        if (sentrySummonBackoff > 0)
            sentrySummonBackoff--;

        int staffSlot = FindSummonStaffSlot(me, profile, minionSummonBackoff <= 0,
            sentrySummonBackoff <= 0, out bool isSentry);
        if (staffSlot < 0)
            return;

        if (me.itemAnimation == 0)
            summonProgressAtStart = isSentry ? CountOwnedSentries(me) : me.slotsMinions;

        // Staffs spawn their minion on the player regardless of aim; a point just ahead keeps
        // the companion facing the way it is already looking.
        UseHeldItem(instance, me, staffSlot, me.Center + new Vector2(me.direction * 64f, -16f));
        summonedLastTick = true;
        summonedSentryLastTick = isSentry;
    }

    /// <summary>
    /// Summon upkeep reads one slot and one slot only — the dedicated summon slot. Scanning the
    /// whole hotbar meant any staff the player parked as a regular weapon got re-cast on
    /// cooldown forever; now the player says explicitly which staff is the one to keep topped
    /// up. Returns the entity inventory index to cast, or -1 when there is nothing to do.
    /// </summary>
    private static int FindSummonStaffSlot(Player me, CompanionProfile profile, bool allowMinion,
        bool allowSentry, out bool isSentry)
    {
        isSentry = false;
        Item minionStaff = profile.SummonWeapon;
        if (allowMinion && CompanionWeaponBehaviors.IsMinionStaff(minionStaff) && me.slotsMinions < me.maxMinions)
            return CompanionInstance.HotbarToInventoryIndex(CompanionProfile.SummonSlot);

        Item sentryStaff = profile.SentryWeapon;
        if (allowSentry && CompanionWeaponBehaviors.IsSentryStaff(sentryStaff) &&
            CountOwnedSentries(me) < me.maxTurrets)
        {
            isSentry = true;
            return CompanionInstance.HotbarToInventoryIndex(CompanionProfile.SentrySlot);
        }

        return -1;
    }

    private static int CountOwnedSentries(Player me)
    {
        int count = 0;
        foreach (Projectile projectile in Main.ActiveProjectiles)
        {
            if (projectile.owner == me.whoAmI && projectile.sentry)
                count++;
        }
        return count;
    }

    private (bool isCloseCombat, float standoffFactor, float aggressionFactor) CombatFactors(Player me, CompanionWeaponPlan plan, bool adaptiveCombat)
    {
        bool isCloseCombat = CompanionWeaponBehaviors.IsCloseCombatWeapon(plan.Weapon);
        // A close weapon that keeps whiffing closes the gap in measured steps. Four misses put
        // it at roughly half its nominal reach, enough to correct a spear's stale stand-off but
        // not enough to make the companion overlap a boss every time one animation misses.
        float aggressionFactor = !adaptiveCombat || !isCloseCombat
            ? 1f
            : MathHelper.Lerp(1f, 0.5f, consecutiveCloseCombatMisses / 4f);
        // Ranged attacks stay at their intended firing distance at first. Only after a sustained
        // miss streak do they step closer, which helps a slow shot blocked by terrain without
        // turning normal guns into melee weapons.
        float standoffFactor = adaptiveCombat && !isCloseCombat && consecutiveRangedMisses >= 4
            ? 0.75f
            : 1f;
        return (isCloseCombat, standoffFactor, aggressionFactor);
    }

    /// <summary>Movement half of combat: walks to a firing/striking position. Identical in every
    /// mode except that a posted companion is leashed to its anchor (see <see cref="Travel"/>).</summary>
    private void PositionForCombat(Player me, NPC target, CompanionWeaponPlan plan, bool adaptiveCombat)
    {
        float distance = Vector2.Distance(me.Center, target.Center);
        bool hasLineOfSight = Collision.CanHitLine(me.Center, 1, 1, target.Center, 1, 1);
        (bool isCloseCombat, float standoffFactor, float aggressionFactor) = CombatFactors(me, plan, adaptiveCombat);

        if (CompanionWeaponBehaviors.IsFlailWeapon(plan.Weapon))
        {
            // Close targets are guarded by spinning in place. At range, close only until the
            // target is inside throwing distance; never backpedal merely because it approached.
            if (distance > GenericProjectileUseRange || !hasLineOfSight)
                Travel(me, target.Center, NavigationIntent.Combat, allowPlatformDrop: false);
            else
                navigator.Idle(me);
            return;
        }

        if (plan.UsesProjectile && !isCloseCombat)
        {
            float standoffMax = RangedStandoffMax * standoffFactor;
            float standoffMin = RangedStandoffMin * standoffFactor;
            if (distance > standoffMax || !hasLineOfSight)
            {
                // Ranged / magic / summon keep their combat layer: an enemy below is never a
                // reason to drop off the platform when the shot can be taken from up here.
                Travel(me, target.Center, NavigationIntent.Combat, allowPlatformDrop: false);
            }
            else if (distance < standoffMin)
            {
                // Back off without pathfinding; vanilla handles small obstacles.
                navigator.Idle(me);
                if (target.Center.X >= me.Center.X)
                    me.controlLeft = true;
                else
                    me.controlRight = true;
            }
            else
            {
                navigator.Idle(me);
            }
        }
        else
        {
            float reach = CompanionWeaponBehaviors.GetMeleeReach(me, plan.Weapon) * aggressionFactor;
            if (distance > reach * 0.8f || !hasLineOfSight)
            {
                // Melee may commit to a single, landing-verified drop only when the enemy is
                // clearly on a lower layer and cannot be struck from the current one.
                bool targetClearlyBelow = target.Center.Y > me.Center.Y + 1.5f * 16f;
                bool mustDropToReach = targetClearlyBelow && !hasLineOfSight;
                Travel(me, target.Center, NavigationIntent.Combat, allowPlatformDrop: mustDropToReach);
            }
            else
                navigator.Idle(me);
        }
    }

    /// <summary>Attack half of combat: fires/swings when the target is reachable. Shared by every
    /// mode, and layered on top of waypoint travel so a companion shoots what it passes.</summary>
    private void TryAttack(CompanionInstance instance, Player me, NPC target, CompanionWeaponPlan plan, CompanionProfile profile, bool adaptiveCombat)
    {
        float distance = Vector2.Distance(me.Center, target.Center);
        bool hasLineOfSight = Collision.CanHitLine(me.Center, 1, 1, target.Center, 1, 1);
        (bool isCloseCombat, float standoffFactor, float aggressionFactor) = CombatFactors(me, plan, adaptiveCombat);

        if (CompanionWeaponBehaviors.IsFlailWeapon(plan.Weapon))
        {
            if (distance > GenericProjectileUseRange || !hasLineOfSight)
            {
                AttackState = distance > GenericProjectileUseRange
                    ? CompanionAttackState.Approaching
                    : CompanionAttackState.NoLineOfSight;
                FaceToward(me, target.Center);
                return;
            }

            Vector2 flailAim = ComputeInterceptAim(me, target, plan.Weapon, profile);
            if (distance <= FlailSpinDistance)
            {
                // Keep the button held while the enemy is close: vanilla Flail AI remains in
                // its spinning phase and repeatedly covers the companion's immediate space.
                UseHeldItem(instance, me, plan.Slot, flailAim);
            }
            else if (!CompanionWeaponBehaviors.HasActiveFlailProjectile(me))
            {
                // Start one new spin. On the following tick the live projectile is detected and
                // input stays released, which makes vanilla throw it. Wait for its return before
                // beginning the next throw.
                UseHeldItem(instance, me, plan.Slot, flailAim);
            }
            else
            {
                instance.AimWorld = flailAim;
                FaceToward(me, target.Center);
                AttackState = CompanionAttackState.Attacking;
            }
            return;
        }

        // Defensive pre-attack: an enemy closing in from behind cover gets met with attacks
        // before it breaks line of sight, instead of the companion standing idle.
        float closingSpeed = distance > 1f
            ? Vector2.Dot(target.velocity, (me.Center - target.Center) / distance)
            : 0f;
        bool approachingThreat = closingSpeed > 0.8f && distance < 340f;

        bool inUseRange = plan.UsesProjectile && !isCloseCombat
            ? distance <= GenericProjectileUseRange * standoffFactor
            : distance <= CompanionWeaponBehaviors.GetMeleeReach(me, plan.Weapon) * aggressionFactor;
        bool defensiveNoLineOfSight = approachingThreat && (plan.UsesProjectile && !isCloseCombat
            ? distance <= 220f
            : distance <= CompanionWeaponBehaviors.GetMeleeReach(me, plan.Weapon) * 1.6f);

        if (!inUseRange || !hasLineOfSight && !defensiveNoLineOfSight)
        {
            AttackState = !inUseRange ? CompanionAttackState.Approaching : CompanionAttackState.NoLineOfSight;
            FaceToward(me, target.Center);
            return;
        }

        // 潜伏 mode: rogue weapons hold fire until Calamity stealth is completely full, so
        // every attack is a stealth strike (attack cadence = stealth charge time).
        if (profile.RogueStealthWait && CompanionModBridge.IsRogueWeapon(plan.Weapon) &&
            CompanionModBridge.TryGetRogueStealth(me, out float stealth, out float stealthMax) &&
            stealthMax > 0f && stealth < stealthMax * 0.995f)
        {
            AttackState = CompanionAttackState.StealthCharging;
            FaceToward(me, target.Center);
            return;
        }

        CompanionAttackMode attackMode = CompanionWeaponOverrides.GetMode(plan.Weapon);
        if (attackMode == CompanionAttackMode.CoinVolley)
        {
            RunCoinVolley(instance, me, target, plan, profile);
            return;
        }

        bool useAlt = attackMode switch
        {
            CompanionAttackMode.ForceRight => true,
            CompanionAttackMode.ForceLeft => false,
            _ => CompanionWeaponOverrides.HasConfigurableAltUse(plan.Weapon) && profile.PrefersAltUse(plan.Weapon)
        };

        float leadMultiplier = adaptiveCombat && !isCloseCombat
            ? 1f + System.Math.Min(consecutiveRangedMisses, 3) * 0.12f
            : 1f;
        Vector2 aim = plan.UsesProjectile
            ? ComputeInterceptAim(me, target, plan.Weapon, profile, leadMultiplier)
            : target.Center;
        if (adaptiveCombat && me.itemAnimation == 0 && !itemUseClaimedThisTick)
            BeginAdaptiveAttackFeedback(me, target, plan, isCloseCombat);
        UseHeldItem(instance, me, plan.Slot, aim, useAlt);
    }

    private void BeginAdaptiveAttackFeedback(Player me, NPC target, CompanionWeaponPlan plan, bool isCloseCombat)
    {
        string weaponKey = CompanionWeaponOverrides.ItemKey(plan.Weapon);
        if (feedbackTarget != target.whoAmI || feedbackWeaponKey != weaponKey)
        {
            feedbackTarget = target.whoAmI;
            feedbackWeaponKey = weaponKey;
            consecutiveCloseCombatMisses = 0;
            consecutiveRangedMisses = 0;
            pendingAdaptiveAttack = false;
            pendingAttackAge = 0;
        }

        // A rapid-fire weapon can start several animations while the first projectile is still
        // in the air. Keep that one observation window intact instead of resetting it every
        // use; any hit inside the window counts as a successful firing position.
        if (pendingAdaptiveAttack)
            return;

        pendingAdaptiveAttack = true;
        pendingAttackWasCloseCombat = isCloseCombat;
        pendingHitSerial = me.GetModPlayer<CompanionPlayer>().CombatHitSerial;
        pendingAttackAge = 0;
    }

    /// <summary>Navigate toward a goal, clamped to the hold leash while posted so combat chasing
    /// never drags the companion off its anchor.</summary>
    private void Travel(Player me, Vector2 goal, NavigationIntent intent = NavigationIntent.Follow, bool allowPlatformDrop = false)
    {
        if (combatLeashActive)
        {
            Vector2 fromAnchor = goal - combatLeashAnchor;
            if (fromAnchor.Length() > combatLeashRadius)
                goal = combatLeashAnchor + Vector2.Normalize(fromAnchor) * combatLeashRadius;
        }
        navigator.Navigate(me, new NavigationRequest(goal, intent, allowPlatformDrop,
            preferKeepElevation: intent == NavigationIntent.Combat));
    }

    /// <summary>
    /// Builds a travel order for a non-combat goal, deciding platform-drop permission from the
    /// intent. Follow / Mine / Build only allow a drop when the goal is clearly on a lower layer;
    /// a user waypoint and free-roam exploration allow the search to use (still safety-gated) drop
    /// edges; everything else stays on the current layer.
    /// </summary>
    private static NavigationRequest NavRequest(Player me, Vector2 goal, NavigationIntent intent)
    {
        bool clearlyBelow = goal.Y > me.Center.Y + 24f;
        bool allowDrop = intent switch
        {
            NavigationIntent.Waypoint => true,
            NavigationIntent.Explore => true,
            NavigationIntent.Escape => true,
            NavigationIntent.Follow or NavigationIntent.Mine or NavigationIntent.Build => clearlyBelow,
            _ => false
        };
        return new NavigationRequest(goal, intent, allowPlatformDrop: allowDrop);
    }

    /// <summary>A posted companion only takes a target it can actually shoot/reach from within its
    /// leash of the anchor; anything farther is left alone so it keeps its post.</summary>
    private bool CanEngageFromHold(Player me, NPC target, CompanionWeaponPlan plan)
    {
        float engageReach = plan.UsesProjectile
            ? GenericProjectileUseRange
            : CompanionWeaponBehaviors.GetMeleeReach(me, plan.Weapon);
        return Vector2.Distance(holdAnchor, target.Center) <= HoldLeash + engageReach;
    }

    /// <summary>
    /// 弹金币 script, exclusive to Midas Prime / Crackshot Colt: throw 4 coins with the right
    /// click, then fire one left-click shot at one of the airborne coins, and repeat.
    /// </summary>
    private void RunCoinVolley(CompanionInstance instance, Player me, NPC target, CompanionWeaponPlan plan, CompanionProfile profile)
    {
        string weaponKey = CompanionWeaponOverrides.ItemKey(plan.Weapon);
        if (coinVolleyWeaponKey != weaponKey)
        {
            coinVolleyWeaponKey = weaponKey;
            coinVolleyThrowsLeft = 4;
            coinVolleyShootPhase = false;
        }

        if (lastUseCompleted && me.selectedItem == plan.Slot)
        {
            if (coinVolleyShootPhase)
            {
                coinVolleyShootPhase = false;
                coinVolleyThrowsLeft = 4;
            }
            else if (--coinVolleyThrowsLeft <= 0)
            {
                coinVolleyShootPhase = true;
            }
        }

        if (!coinVolleyShootPhase)
        {
            // Throw coins in an arc toward the enemy.
            UseHeldItem(instance, me, plan.Slot, target.Center - new Vector2(0f, 120f), altUse: true);
            return;
        }

        // Shoot phase: aim the left click at one of our own lingering coins if any survive,
        // otherwise just shoot the target directly.
        Vector2 aim = FindNearestOwnedCoin(me) ?? ComputeInterceptAim(me, target, plan.Weapon, profile);
        UseHeldItem(instance, me, plan.Slot, aim);
    }

    private static Vector2? FindNearestOwnedCoin(Player me)
    {
        Projectile best = null;
        float bestDistance = 700f;
        foreach (Projectile projectile in Main.ActiveProjectiles)
        {
            if (projectile.owner != me.whoAmI || projectile.minion || projectile.sentry || projectile.timeLeft < 10)
                continue;
            // Coins linger and drift slowly; freshly fired bullets are fast and short-lived.
            if (projectile.velocity.Length() > 8f)
                continue;
            float distance = Vector2.Distance(me.Center, projectile.Center);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = projectile;
            }
        }
        return best?.Center;
    }

    /// <summary>
    /// Predictive aim for projectile weapons: solves an intercept point from the fired
    /// projectile's effective speed — shootSpeed scaled by (1 + extraUpdates), since extra
    /// updates move the projectile multiple times per tick — against the target's velocity.
    /// A few fixed-point iterations converge close enough for straight-line projectiles.
    /// </summary>
    private static Vector2 ComputeInterceptAim(Player me, NPC target, Item weapon, CompanionProfile profile, float leadMultiplier = 1f)
    {
        Vector2 fallback = target.Center;
        if (target.velocity.LengthSquared() < 2.25f)
            return fallback; // slow targets do not need a lead

        int projectileType = weapon.shoot;
        float speed = weapon.shootSpeed;
        if (weapon.useAmmo > AmmoID.None)
        {
            Item ammo = FindAmmoItem(profile, weapon.useAmmo);
            if (ammo is not null)
            {
                // Mirrors PickAmmo: the ammo decides the projectile (except gunProj
                // weapons) and its shootSpeed adds onto the weapon's.
                if (ammo.shoot > ProjectileID.None && !ItemID.Sets.gunProj[weapon.type])
                    projectileType = ammo.shoot;
                speed += ammo.shootSpeed;
            }
        }

        if (projectileType <= ProjectileID.None || speed < 0.5f)
            return fallback;

        int extraUpdates = ContentSamples.ProjectilesByType.TryGetValue(projectileType, out Projectile sample)
            ? sample.extraUpdates
            : 0;
        float effectiveSpeed = speed * (1 + extraUpdates);

        Vector2 aim = fallback;
        for (int i = 0; i < 3; i++)
        {
            float travelTicks = MathHelper.Clamp(Vector2.Distance(me.Center, aim) / effectiveSpeed, 0f, 40f);
            aim = target.Center + target.velocity * travelTicks * leadMultiplier;
        }
        return aim;
    }

    private static Item FindAmmoItem(CompanionProfile profile, int ammoType)
    {
        foreach (Item item in profile.Ammo)
        {
            if (!item.IsAir && item.ammo == ammoType && item.stack > 0)
                return item;
        }
        foreach (Item item in profile.Backpack)
        {
            if (!item.IsAir && item.ammo == ammoType && item.stack > 0)
                return item;
        }

        // Nothing stocked means the shot will use the ammo class's default round for free
        // (CompanionGlobalItem.NeedsAmmo), so lead the target with THAT round's stats rather
        // than the bare weapon's. Read-only use of the shared sample.
        return ContentSamples.ItemsByType.TryGetValue(ammoType, out Item defaultAmmo) && defaultAmmo.ammo == ammoType
            ? defaultAmmo
            : null;
    }

    private bool TryPerformMiningOrTreeWork(CompanionInstance instance, Player me, Player owner, CompanionPlayer observation, CompanionProfile profile)
    {
        if (miningLightCooldown > 0)
            miningLightCooldown--;

        bool wheelExploration = profile.WheelCommandsActive && profile.WorkMode == CompanionWorkMode.Exploration;

        // The pick comes from the dedicated slot and nowhere else. Scanning the whole inventory
        // turned any pickaxe the companion happened to be carrying into a licence to dig, which
        // is the loose behaviour the pick slot exists to tighten up.
        int pickaxeSlot = !profile.PickTool.IsAir && CompanionSlotRules.IsPickaxe(profile.PickTool)
            ? CompanionInstance.HotbarToInventoryIndex(CompanionProfile.PickSlot)
            : -1;
        if (pickaxeSlot >= 0 && wheelExploration && profile.ExplorationGathering)
        {
            Point ore = FindNearestExposedOreTile(instance, me.Center, 14);
            if (ore.X >= 0)
                return MoveAndWorkTile(instance, me, pickaxeSlot, ore, mining: true);
        }
        if (pickaxeSlot >= 0 && observation.MiningObservationTime > 0)
        {
            Item pick = me.inventory[pickaxeSlot];
            bool sustainedTunnel = observation.TunnelDirectionTime >= SustainedTunnelTicks &&
                (observation.TunnelDirection.Y > 0 || observation.TunnelDirection.X != 0);

            // A narrow shaft/tunnel cannot accommodate an ordinary follow offset. After two
            // seconds of one deliberate mining direction, share the owner's exact position and
            // work the same leading face.
            if (sustainedTunnel && Vector2.DistanceSquared(me.Center, owner.Center) > MiningSnapDistance * MiningSnapDistance)
            {
                SnapToMiningOwner(me, owner);
                return true;
            }

            if (TryLightMiningTunnel(instance, me, profile, observation))
                return true;

            Point targetTile = FindReachableMiningTile(instance, me, observation, pick, sustainedTunnel);
            if (targetTile.X >= 0)
                return MoveAndWorkTile(instance, me, pickaxeSlot, targetTile, mining: true);

            // Nothing minable within reach: close in on the spot the owner is digging so fresh
            // tiles come into range, rather than standing back and swinging at nothing.
            Vector2 digCenter = new(observation.ObservedTileX * 16f + 8f, observation.ObservedTileY * 16f + 8f);
            if (Vector2.Distance(me.Center, digCenter) > MiningApproachDistance)
            {
                navigator.Navigate(me, NavRequest(me, digCenter, NavigationIntent.Mine));
                return true;
            }
        }

        int axeSlot = !profile.AxeTool.IsAir && CompanionSlotRules.IsAxe(profile.AxeTool)
            ? CompanionInstance.HotbarToInventoryIndex(CompanionProfile.AxeSlot)
            : -1;
        bool autonomousTreeFarm = profile.WheelCommandsActive && profile.WorkMode == CompanionWorkMode.Farming &&
            profile.FarmingCommand is FarmingCommand.TreeFarm or FarmingCommand.Combined;
        if (axeSlot >= 0 && (observation.TreeAssistTime > 0 || autonomousTreeFarm))
        {
            Vector2 center = autonomousTreeFarm && instance.HasCommandArea ? instance.CommandAnchor : owner.Center;
            Point treeTile = FindNearestTreeTile(center, 20);
            if (treeTile.X >= 0)
                return MoveAndWorkTile(instance, me, axeSlot, treeTile);
        }

        return false;
    }

    private bool TryLightAtCurrentPosition(CompanionInstance instance, Player me, CompanionProfile profile)
    {
        if (miningLightCooldown > 0)
            return false;
        Point anchor = me.Center.ToTileCoordinates();
        if (!WorldGen.InWorld(anchor.X, anchor.Y, 5) || Lighting.Brightness(anchor.X, anchor.Y) > MiningDarknessThreshold)
            return false;

        if (me.wet || Main.tile[anchor.X, anchor.Y].LiquidAmount > 0)
        {
            int glowstickSlot = FindToolInventoryIndex(profile, IsGlowstick);
            if (glowstickSlot < 0)
                return false;
            navigator.Idle(me);
            UseHeldItem(instance, me, glowstickSlot, me.Center);
            miningLightCooldown = MiningLightCooldownTicks;
            return true;
        }

        int torchSlot = FindToolInventoryIndex(profile, IsTorch);
        Point placement = FindNearbyTorchPlacement(me);
        if (torchSlot < 0 || placement.X < 0)
            return false;
        navigator.Idle(me);
        instance.WorkTile = placement;
        UseHeldItem(instance, me, torchSlot, new Vector2(placement.X * 16f + 8f, placement.Y * 16f + 8f));
        miningLightCooldown = MiningLightCooldownTicks;
        return true;
    }

    private static Point FindNearestExposedOreTile(CompanionInstance instance, Vector2 center, int radiusInTiles)
    {
        Point origin = center.ToTileCoordinates();
        Point best = new(-1, -1);
        float bestDistance = float.MaxValue;
        for (int x = origin.X - radiusInTiles; x <= origin.X + radiusInTiles; x++)
        {
            for (int y = origin.Y - radiusInTiles; y <= origin.Y + radiusInTiles; y++)
            {
                if (!WorldGen.InWorld(x, y, 3))
                    continue;
                Tile tile = Main.tile[x, y];
                if (!tile.HasTile || !IsOreTile(tile.TileType) || !WorldGen.CanKillTile(x, y) ||
                    CompanionMiningSafety.IsMiningBlocked(instance, x, y))
                    continue;
                bool exposed = !Main.tile[x - 1, y].HasTile || !Main.tile[x + 1, y].HasTile ||
                    !Main.tile[x, y - 1].HasTile || !Main.tile[x, y + 1].HasTile;
                if (!exposed)
                    continue;
                float distance = Vector2.DistanceSquared(center, new Vector2(x * 16f + 8f, y * 16f + 8f));
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = new Point(x, y);
                }
            }
        }
        return best;
    }

    /// <summary>
    /// Picks the best tile a mining companion can break from where it currently stands. Only tiles
    /// already within pick reach are considered, so the companion always makes progress instead of
    /// pathing at a buried block it can never line up on. Priority: exposed ore / Spelunker-
    /// highlighted blocks first (grabbing valuables is the whole point of a mining buddy), then the
    /// tiles closest to the spot the owner is digging, nudged toward the direction they are
    /// tunnelling — so the companion clears the same passage instead of poking scattered holes off
    /// to the side (which is what made its digging look misaligned).
    /// </summary>
    private static Point FindReachableMiningTile(CompanionInstance instance, Player me,
        CompanionPlayer observation, Item pick, bool sustainedTunnel)
    {
        Point dig = new(observation.ObservedTileX, observation.ObservedTileY);
        Point tunnel = observation.TunnelDirection;
        Point meTile = me.Center.ToTileCoordinates();
        int reachX = Player.tileRangeX + pick.tileBoost + 1;
        int reachY = Player.tileRangeY + pick.tileBoost + 1;
        int? requiredOreType = IsOreTile(observation.ObservedTileType) ? observation.ObservedTileType : null;

        Point best = new(-1, -1);
        float bestScore = float.MaxValue;
        for (int x = meTile.X - reachX; x <= meTile.X + reachX; x++)
        {
            for (int y = meTile.Y - reachY; y <= meTile.Y + reachY; y++)
            {
                // Cheap reach test first — it culls most of the box before the more expensive
                // CanKillTile check inside CanMineOrdinaryTarget ever runs.
                if (!IsTileInMiningReach(me, pick, x, y) ||
                    !CanMineOrdinaryTarget(instance, x, y, requiredOreType))
                    continue;

                // Once the owner has been cutting a deliberate shaft/tunnel for two seconds,
                // keep the partner on the same leading face. Vertical shafts stay narrow;
                // horizontal tunnels stay in the familiar three-tile-tall band.
                if (sustainedTunnel && !IsOnMiningFrontier(x, y, dig, tunnel))
                    continue;

                float score = System.Math.Abs(x - dig.X) + System.Math.Abs(y - dig.Y);
                int type = Main.tile[x, y].TileType;
                if (requiredOreType.HasValue)
                    score -= 1500f; // an observed ore means mine that exact ore type first
                else if (type < Main.tileSpelunker.Length && Main.tileSpelunker[type])
                    score -= 1000f; // ore already in reach is always worth taking first
                if (tunnel.X != 0 && System.Math.Sign(x - dig.X) == tunnel.X)
                    score -= 2f;
                if (tunnel.Y != 0 && System.Math.Sign(y - dig.Y) == tunnel.Y)
                    score -= 2f;

                if (score < bestScore)
                {
                    bestScore = score;
                    best = new Point(x, y);
                }
            }
        }
        return best;
    }

    private static bool IsOreTile(int tileType) =>
        tileType >= 0 &&
        ((tileType < TileID.Sets.Ore.Length && TileID.Sets.Ore[tileType]) ||
         (tileType < Main.tileSpelunker.Length && Main.tileSpelunker[tileType]));

    private static bool IsOnMiningFrontier(int x, int y, Point dig, Point direction)
    {
        if (direction.Y > 0)
            return y >= dig.Y - 1 && System.Math.Abs(x - dig.X) <= 1;
        if (direction.X > 0)
            return x >= dig.X - 1 && System.Math.Abs(y - dig.Y) <= 1;
        if (direction.X < 0)
            return x <= dig.X + 1 && System.Math.Abs(y - dig.Y) <= 1;
        return true;
    }

    private void SnapToMiningOwner(Player me, Player owner)
    {
        me.Center = owner.Center;
        me.velocity = owner.velocity;
        me.fallStart = (int)(me.position.Y / 16f);
        navigator.Reset();
        farFromOwnerTicks = 0;
        stuckReference = me.Center;
        notProgressingTicks = 0;
        ResetVerticalFollowRescue();
    }

    private bool TryLightMiningTunnel(CompanionInstance instance, Player me, CompanionProfile profile, CompanionPlayer observation)
    {
        if (miningLightCooldown > 0)
            return false;

        Point anchor = new(observation.ObservedTileX, observation.ObservedTileY);
        if (!WorldGen.InWorld(anchor.X, anchor.Y, 5) || Lighting.Brightness(anchor.X, anchor.Y) > MiningDarknessThreshold)
            return false;

        bool underwater = me.wet || Main.tile[anchor.X, anchor.Y].LiquidAmount > 0;
        if (underwater)
        {
            int glowstickSlot = FindToolInventoryIndex(profile, IsGlowstick);
            if (glowstickSlot < 0)
                return false;

            navigator.Idle(me);
            UseHeldItem(instance, me, glowstickSlot, new Vector2(anchor.X * 16f + 8f, anchor.Y * 16f + 8f));
            miningLightCooldown = MiningLightCooldownTicks;
            return true;
        }

        int torchSlot = FindToolInventoryIndex(profile, IsTorch);
        if (torchSlot < 0)
            return false;

        Point placement = FindNearbyTorchPlacement(me);
        if (placement.X < 0)
            return false;

        navigator.Idle(me);
        instance.WorkTile = placement;
        UseHeldItem(instance, me, torchSlot, new Vector2(placement.X * 16f + 8f, placement.Y * 16f + 8f));
        miningLightCooldown = MiningLightCooldownTicks;
        return true;
    }

    private static bool IsTorch(Item item) =>
        !item.IsAir && item.type > ItemID.None && item.type < ItemID.Sets.Torches.Length && ItemID.Sets.Torches[item.type];

    private static bool IsGlowstick(Item item) =>
        !item.IsAir && item.type > ItemID.None && item.type < ItemID.Sets.Glowsticks.Length && ItemID.Sets.Glowsticks[item.type];

    private static Point FindNearbyTorchPlacement(Player me)
    {
        Point center = me.Center.ToTileCoordinates();
        Point[] candidates =
        [
            new Point(center.X - 1, center.Y - 1), new Point(center.X + 1, center.Y - 1),
            new Point(center.X - 2, center.Y), new Point(center.X + 2, center.Y),
            new Point(center.X, center.Y - 2)
        ];

        foreach (Point point in candidates)
        {
            if (!WorldGen.InWorld(point.X, point.Y, 5))
                continue;
            Tile tile = Main.tile[point.X, point.Y];
            if (tile.HasTile || tile.LiquidAmount > 0)
                continue;
            if (WorldGen.SolidTile(point.X, point.Y + 1) || WorldGen.SolidTile(point.X - 1, point.Y) || WorldGen.SolidTile(point.X + 1, point.Y))
                return point;
        }
        return new Point(-1, -1);
    }

    /// <summary>Vanilla's tile-reach test (mirrors IsTargetTileInItemRange): whether the companion,
    /// at its current position, could hit this tile with the given tool.</summary>
    private static bool IsTileInMiningReach(Player me, Item tool, int x, int y) =>
        me.position.X / 16f - Player.tileRangeX - tool.tileBoost <= x &&
        (me.position.X + me.width) / 16f + Player.tileRangeX + tool.tileBoost - 1f >= x &&
        me.position.Y / 16f - Player.tileRangeY - tool.tileBoost <= y &&
        (me.position.Y + me.height) / 16f + Player.tileRangeY + tool.tileBoost - 2f >= y;

    private static bool CanMineOrdinaryTarget(CompanionInstance instance, int x, int y, int? requiredType)
    {
        if (!WorldGen.InWorld(x, y, 3))
            return false;
        if (CompanionMiningSafety.IsMiningBlocked(instance, x, y))
            return false;
        Tile tile = Main.tile[x, y];
        if (!tile.HasTile || requiredType.HasValue && tile.TileType != requiredType.Value)
            return false;
        if (Main.tileFrameImportant[tile.TileType] || !Main.tileSolid[tile.TileType])
            return false;
        return WorldGen.CanKillTile(x, y);
    }

    private static Point FindNearestTreeTile(Vector2 center, int radiusInTiles)
    {
        Point centerTile = center.ToTileCoordinates();
        Point best = new(-1, -1);
        float bestDistance = float.MaxValue;
        for (int x = centerTile.X - radiusInTiles; x <= centerTile.X + radiusInTiles; x++)
        {
            for (int y = centerTile.Y - radiusInTiles; y <= centerTile.Y + radiusInTiles; y++)
            {
                if (!WorldGen.InWorld(x, y, 3))
                    continue;
                Tile tile = Main.tile[x, y];
                if (!tile.HasTile || !Main.tileAxe[tile.TileType] || !WorldGen.CanKillTile(x, y))
                    continue;
                float distance = Vector2.DistanceSquared(center, new Vector2(x * 16f + 8f, y * 16f + 8f));
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = new Point(x, y);
                }
            }
        }
        return best;
    }

    private bool MoveAndWorkTile(CompanionInstance instance, Player me, int toolInventorySlot,
        Point targetTile, bool mining = false)
    {
        if (!WorldGen.InWorld(targetTile.X, targetTile.Y, 3) || !Main.tile[targetTile.X, targetTile.Y].HasTile)
            return false;
        // Re-check at actuation time as well as during target selection. A cached target must not
        // survive a live config change and reach vanilla ItemCheck after protection is enabled.
        if (mining && CompanionMiningSafety.IsMiningBlocked(instance, targetTile.X, targetTile.Y))
            return false;

        Vector2 targetCenter = new(targetTile.X * 16f + 8f, targetTile.Y * 16f + 8f);
        Item tool = me.inventory[toolInventorySlot];
        // Mining/chopping only needs the tile within reach — vanilla never requires line of sight
        // to it (that is a combat/placement rule). Requiring CanHitLine here made the companion
        // refuse to dig any tile buried one layer into rock and pace around it forever, the single
        // biggest cause of "mining feels broken". Reach alone, exactly as ItemCheck_UseMiningTools.
        if (!IsTileInMiningReach(me, tool, targetTile.X, targetTile.Y))
        {
            navigator.Navigate(me, NavRequest(me, targetCenter, NavigationIntent.Mine));
            return true;
        }

        navigator.Idle(me);
        instance.WorkTile = targetTile;
        UseHeldItem(instance, me, toolInventorySlot, targetCenter);
        return true;
    }

    /// <summary>
    /// Uses the actual vanilla fishing-pole path under the companion's virtual-player context.
    /// The companion only presses the rod to cast when no bobber exists, then waits for the
    /// bobber's catch state before pressing again to reel it in. Holding the rod every frame
    /// would immediately retrieve a fresh bobber instead of allowing a bite.
    /// </summary>
    private bool TryFishNearby(CompanionInstance instance, Player me, CompanionProfile profile)
    {
        if (!CompanionModeRules.HasFishingBait(profile))
        {
            navigator.Idle(me);
            return true;
        }

        int rodSlot = CompanionInstance.RodInventoryIndex;
        if (me.inventory[rodSlot].IsAir || me.inventory[rodSlot].fishingPole <= 0)
            return false;

        Projectile bobber = FindOwnedFishingBobber(me);
        if (bobber is not null)
        {
            navigator.Idle(me);
            if (bobber.localAI[1] != 0f)
                UseHeldItem(instance, me, rodSlot, bobber.Center);
            return true;
        }

        if (fishingSpotScanCooldown > 0)
            fishingSpotScanCooldown--;
        if (!fishingSpot.HasValue || fishingSpotScanCooldown <= 0)
        {
            fishingSpot = FindBestFishingSpot(me);
            fishingSpotScanCooldown = FishingRescanTicks;
        }

        if (!fishingSpot.HasValue)
        {
            navigator.Idle(me);
            return true;
        }

        navigator.Idle(me);
        UseHeldItem(instance, me, rodSlot, fishingSpot.Value.AimWorld);
        return true;
    }

    private static Projectile FindOwnedFishingBobber(Player me)
    {
        foreach (Projectile projectile in Main.ActiveProjectiles)
        {
            if (projectile.owner == me.whoAmI && projectile.bobber)
                return projectile;
        }
        return null;
    }

    /// <summary>
    /// Finds a water surface within ordinary rod range and scores it by vanilla-style connected
    /// water volume first, distance second. The counter follows Projectile.GetFishingPondState's
    /// width-at-surface then depth-per-column rule, capped once additional water no longer raises
    /// normal fishing quality.
    /// </summary>
    private static FishingSpot? FindBestFishingSpot(Player me)
    {
        Point origin = me.Center.ToTileCoordinates();
        FishingSpot? best = null;
        float bestScore = float.MinValue;
        float maxDistanceSquared = FishingSearchRadiusTiles * 16f * FishingSearchRadiusTiles * 16f;

        for (int x = origin.X - FishingSearchRadiusTiles; x <= origin.X + FishingSearchRadiusTiles; x++)
        {
            for (int y = origin.Y - FishingVerticalSearchTiles; y <= origin.Y + FishingVerticalSearchTiles; y++)
            {
                if (!WorldGen.InWorld(x, y, 10) || !IsWaterSurface(x, y))
                    continue;

                Vector2 aim = new(x * 16f + 8f, y * 16f + 8f);
                float distanceSquared = Vector2.DistanceSquared(me.MountedCenter, aim);
                if (distanceSquared > maxDistanceSquared)
                    continue;

                int waterTiles = CountFishingWaterTiles(x, y);
                if (waterTiles < FishingMinimumWaterTiles)
                    continue;

                float score = waterTiles * 10000f - distanceSquared;
                if (score > bestScore)
                {
                    bestScore = score;
                    best = new FishingSpot(aim, waterTiles);
                }
            }
        }

        return best;
    }

    private static bool IsWaterSurface(int x, int y)
    {
        if (!IsOpenWaterTile(x, y))
            return false;
        return !IsOpenWaterTile(x, y - 1);
    }

    private static bool IsOpenWaterTile(int x, int y)
    {
        if (!WorldGen.InWorld(x, y, 10))
            return false;
        Tile tile = Main.tile[x, y];
        return tile.LiquidAmount > 0 && tile.LiquidType == LiquidID.Water && !WorldGen.SolidTile(x, y);
    }

    private static int CountFishingWaterTiles(int startX, int surfaceY)
    {
        int minX = startX;
        int maxX = startX;
        for (int step = 1; step <= FishingPoolHalfWidth && IsOpenWaterTile(minX - 1, surfaceY); step++)
            minX--;
        for (int step = 1; step <= FishingPoolHalfWidth && IsOpenWaterTile(maxX + 1, surfaceY); step++)
            maxX++;

        int waterTiles = 0;
        for (int x = minX; x <= maxX; x++)
        {
            for (int y = surfaceY; y < surfaceY + FishingPoolDepthScan && IsOpenWaterTile(x, y); y++)
            {
                if (++waterTiles >= FishingIdealWaterTiles)
                    return FishingIdealWaterTiles;
            }
        }
        return waterTiles;
    }

    private bool TryCatchNearbyCritter(CompanionInstance instance, Player me, CompanionProfile profile)
    {
        Item net = profile.NetTool;
        if (!CompanionSlotRules.IsCatchingTool(net) || net.IsAir)
            return false;

        int netSlot = CompanionInstance.NetInventoryIndex;
        NPC critter = null;
        float bestDistance = 12f * 16f;
        foreach (NPC candidate in Main.ActiveNPCs)
        {
            // A mod can make a town NPC technically nettable by assigning catchItem. Catch mode
            // deliberately treats townNPC as a hard social boundary, so vanilla and modded town
            // residents are never selected while ordinary vanilla/modded critters still are.
            if (!candidate.active || candidate.townNPC || candidate.catchItem <= 0)
                continue;
            if (ItemID.Sets.IsLavaBait[candidate.catchItem] && !ItemID.Sets.LavaproofCatchingTool[net.type])
                continue;
            float distance = Vector2.Distance(me.Center, candidate.Center);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                critter = candidate;
            }
        }

        if (critter is null)
            return false;

        if (bestDistance > 56f)
        {
            navigator.Navigate(me, NavRequest(me, critter.Center, NavigationIntent.Follow));
            return true;
        }

        navigator.Idle(me);
        UseHeldItem(instance, me, netSlot, critter.Center);
        return true;
    }

    /// <summary>
    /// Sweeps caught critters off the ground and into the companion's own backpack.
    ///
    /// Netting a critter turns it into a dropped item, and that is where it would stay:
    /// vanilla's pickup pass in Item.Update only ever runs for Main.myPlayer, so a companion
    /// walks straight over its own catch. Restricted to items that can be placed back down as
    /// an NPC (Item.makeNPC), which is exactly the set a bug net produces — the companion is a
    /// catcher, not a vacuum that strips the floor of the owner's loot.
    /// </summary>
    private static void CollectCaughtCritters(Player me, CompanionProfile profile)
    {
        // This is still a world-item pickup path even though it is limited to critters caught
        // by this companion. Keep it behind the same opt-in gate: when disabled, do not scan
        // Main.ActiveItems or mutate a ground item at all.
        if (!CompanionLootSystem.WorldItemCollectionEnabled)
            return;

        // Item identity and teardown are controlled by every loaded mod. A failed collection
        // therefore opens only the world-item circuit; combat, following and the companion
        // itself keep running, while this path remains inert until the next world load.
        CompanionFaultGuard.RunCritical(
            CompanionFaultGuard.WorldItemCollectionLane,
            () => CollectCaughtCrittersCore(me, profile));
    }

    private static void CollectCaughtCrittersCore(Player me, CompanionProfile profile)
    {
        foreach (Item drop in Main.ActiveItems)
        {
            if (drop.makeNPC <= 0 || drop.noGrabDelay > 0)
                continue;
            if (Vector2.Distance(me.Center, drop.Center) > CritterPickupRange)
                continue;

            // TryAddToBackpack consumes the stack it takes; anything it could not fit stays on
            // the ground for the owner.
            Item incoming = drop.Clone();
            if (!profile.TryAddToBackpack(incoming) && incoming.stack > 0)
            {
                drop.stack = incoming.stack;
                continue;
            }
            drop.TurnToAir();
            drop.active = false;
        }
    }

    /// <summary>
    /// Reconciles this tick's mode/waypoint state into a single resolved hold anchor:
    /// <list type="bullet">
    /// <item>Entering Garrison marks the current spot; leaving it drops any hold.</item>
    /// <item>A waypoint counts down, sparkles, and on arrival becomes the hold anchor
    /// (updating the garrison mark in Garrison mode, or a soft post otherwise).</item>
    /// </list>
    /// </summary>
    private void UpdateCommandState(CompanionInstance instance, Player me, CompanionProfile profile)
    {
        if (profile.Mode != lastMode)
        {
            if (profile.Mode == CompanionMode.Garrison)
                garrisonMark = me.Center; // mark the fort the instant the order is given
            softHoldActive = false;       // switching modes cancels a soft post
            lastMode = profile.Mode;
        }

        if (instance.Waypoint is Vector2 waypoint)
        {
            CompanionEntitySystem.SpawnWaypointSparkle(waypoint);
            if (instance.WaypointTimeLeft > 0)
                instance.WaypointTimeLeft--;

            if (Vector2.Distance(me.Center, waypoint) <= WaypointArriveDistance)
            {
                // Reached: the marker becomes the spot to hold.
                if (profile.Mode == CompanionMode.Garrison)
                    garrisonMark = waypoint;
                else
                {
                    softHoldActive = true;
                    softAnchor = waypoint;
                }
                CompanionEntitySystem.SpawnWaypointPing(waypoint);
                instance.Waypoint = null;
                instance.WaypointTimeLeft = 0;
            }
            else if (instance.WaypointTimeLeft <= 0)
            {
                instance.Waypoint = null; // gave up before reaching it
            }
        }

        if (profile.Mode == CompanionMode.Garrison)
        {
            holdActive = true;
            holdAnchor = garrisonMark;
        }
        else if (softHoldActive)
        {
            holdActive = true;
            holdAnchor = softAnchor;
        }
        else
        {
            holdActive = false;
        }
    }

    private void NavigateToWaypoint(CompanionInstance instance, Player me)
    {
        if (instance.Waypoint is Vector2 waypoint)
            navigator.Navigate(me, NavRequest(me, waypoint, NavigationIntent.Waypoint));
    }

    /// <summary>Garrison / posted behaviour when there is nothing to fight: sit on the anchor,
    /// walking back if knocked off it.</summary>
    private void HoldAtAnchor(Player me, Player owner)
    {
        Vector2 offset = holdAnchor - me.Center;
        if (System.Math.Abs(offset.X) <= HoldReturnDistance && System.Math.Abs(offset.Y) <= 96f)
        {
            navigator.Idle(me);
            if (!me.controlLeft && !me.controlRight)
                me.ChangeDir(owner.Center.X >= me.Center.X ? 1 : -1);
            return;
        }

        navigator.Navigate(me, NavRequest(me, holdAnchor, NavigationIntent.Follow));
    }

    private void FollowOwner(Player me, Player owner, float sideOffset, float idleReachX,
        int squadOrdinal, int squadCount, float spreadSpacing)
    {
        // Stand slightly behind the owner. When the owner turns around, react with a
        // randomized human-like delay (0.5s–3s) before walking over to the other side.
        if (lastOwnerDirection == 0)
        {
            lastOwnerDirection = owner.direction;
            followSide = -owner.direction;
        }
        else if (owner.direction != lastOwnerDirection)
        {
            lastOwnerDirection = owner.direction;
            sideSwitchTimer = Main.rand.Next(30, 181);
        }

        if (sideSwitchTimer > 0 && --sideSwitchTimer == 0)
            followSide = -owner.direction;

        // Fan a multi-companion squad out around the shared follow point so it reads as a loose
        // group rather than one sprite stacked N deep. Each companion aims at its own offset and
        // settles there (the offset stays within idleReachX, so nobody paces back and forth). A
        // small stable per-companion jitter, seeded off the slot, breaks up the even line without
        // oscillating. Horizontal only: a vertical offset would make companions hop toward a point
        // above or below the owner.
        float spread = 0f;
        if (squadCount > 1)
        {
            spread = (squadOrdinal - (squadCount - 1) * 0.5f) * spreadSpacing;
            spread += (me.whoAmI * 37) % 15 - 7;
        }

        Vector2 standPoint = owner.Center + new Vector2(followSide * sideOffset + spread, 0f);
        Vector2 offset = standPoint - me.Center;
        if (System.Math.Abs(offset.X) <= idleReachX && System.Math.Abs(offset.Y) <= 96f)
        {
            navigator.Idle(me);
            if (!me.controlLeft && !me.controlRight)
                me.ChangeDir(owner.direction);
            return;
        }

        // Following: a platform drop is permitted only when the owner is clearly on a lower layer
        // (NavRequest gates on the goal being clearly below), and even then the landing is verified.
        navigator.Navigate(me, NavRequest(me, standPoint, NavigationIntent.Follow));
    }

    /// <summary>
    /// 自由行动 idle movement: wander between random spots inside the free-roam leash around the
    /// owner, pausing at each with a little idle life (glancing around). Never edits terrain. When
    /// the owner walks off, the next goal — and any current pause — is pulled back toward them, and
    /// the ordinary far-away / stuck teleport policy still recalls the companion if it ends up lost.
    /// </summary>
    private void FreeRoamWander(CompanionInstance instance, CompanionProfile profile, Player me, Player owner)
    {
        bool useCommandZone = UsesCapturedCommandZone(profile) && instance.HasCommandArea;
        Vector2 home = useCommandZone ? instance.CommandAnchor : owner.Center;
        float ownerDistance = Vector2.Distance(me.Center, home);

        // Dwell: standing at a reached spot, doing nothing in particular for a random beat.
        if (freeRoamDwellTicks > 0)
        {
            freeRoamDwellTicks--;
            navigator.Idle(me);
            // Glance around every so often so a paused companion still looks alive.
            if (--freeRoamFlipTimer <= 0)
            {
                me.ChangeDir(Main.rand.NextBool() ? 1 : -1);
                freeRoamFlipTimer = Main.rand.Next(40, 140);
            }
            // Do not keep lounging while the owner strides away — cut the pause short and re-home.
            if (ownerDistance > FreeRoamLeash)
            {
                freeRoamDwellTicks = 0;
                hasFreeRoamGoal = false;
            }
            return;
        }

        bool reached = hasFreeRoamGoal && Vector2.Distance(me.Center, freeRoamGoal) <= FreeRoamArrive;
        bool timedOut = hasFreeRoamGoal && --freeRoamGoalTicks <= 0;

        if (hasFreeRoamGoal && (reached || timedOut))
        {
            // Arrived, or gave up on an unreachable spot: pause here before choosing somewhere new.
            hasFreeRoamGoal = false;
            freeRoamDwellTicks = Main.rand.Next(30, 240);
            freeRoamFlipTimer = Main.rand.Next(20, 80);
            navigator.Idle(me);
            return;
        }

        if (!hasFreeRoamGoal)
        {
            freeRoamGoal = PickFreeRoamGoal(me, home, ownerDistance,
                useCommandZone ? instance.CommandArea : null);
            hasFreeRoamGoal = true;
            freeRoamGoalTicks = FreeRoamGoalTimeout;
        }

        navigator.Navigate(me, NavRequest(me, freeRoamGoal, NavigationIntent.Explore));
    }

    /// <summary>
    /// Picks the next wander destination for free-roam: a random horizontal offset around the
    /// owner, kept near the owner's feet so the navigator gets a reachable ground goal rather than
    /// a point floating in mid-air. Outside the leash it heads back toward the owner; occasionally
    /// it "checks in" by visiting the owner directly.
    /// </summary>
    private static Vector2 PickFreeRoamGoal(Player me, Vector2 home, float ownerDistance, Rectangle? commandArea)
    {
        int direction;
        if (ownerDistance > FreeRoamLeash)
            direction = home.X >= me.Center.X ? 1 : -1; // outside the leash: come back in
        else if (Main.rand.NextBool(5))
            return home;                                        // occasional check-in visit
        else
            direction = Main.rand.NextBool() ? 1 : -1;

        float reach = FreeRoamLeash * Main.rand.NextFloat(0.25f, 1f);
        float x = home.X + direction * reach;
        float y = home.Y + Main.rand.NextFloat(-48f, 48f);
        if (commandArea is Rectangle area)
        {
            x = MathHelper.Clamp(x, area.Left + 24f, area.Right - 24f);
            y = MathHelper.Clamp(y, area.Top + 24f, area.Bottom - 24f);
        }
        return new Vector2(x, y);
    }

    private static bool UsesCapturedCommandZone(CompanionProfile profile) =>
        profile.WheelCommandsActive && (profile.WorkMode switch
        {
            CompanionWorkMode.Idle => profile.IdleCommand == IdleCommand.FreeActivity,
            CompanionWorkMode.Combat => profile.CombatCommand == CombatCommand.Patrol,
            CompanionWorkMode.Exploration => profile.ExplorationCommand == ExplorationCommand.FreeExplore,
            CompanionWorkMode.Capturing => true,
            CompanionWorkMode.Farming => true,
            _ => false
        });

    private void UpdateTeleportPolicy(CompanionInstance instance, Player me, Player owner, CompanionPlayer ownerData,
        CompanionProfile profile, CompanionMode mode, bool posted)
    {
        if (rescueCooldown > 0)
            rescueCooldown--;

        // Track the owner's per-tick position every tick (even when an early return follows)
        // so a teleport is never missed.
        bool ownerTeleported = DetectOwnerTeleport(owner);
        float distance = Vector2.Distance(me.Center, owner.Center);

        // Three tiles is the hard inner safety radius. A companion already this close must never
        // blink around the owner merely because it is standing on a small ledge or facing a wall.
        if (distance <= NoTeleportDistance)
        {
            farFromOwnerTicks = 0;
            stuckReference = me.Center;
            notProgressingTicks = 0;
            ResetVerticalFollowRescue();
            return;
        }

        // The owner just teleported (mirror, recall, teleporter, Rod of Discord…). Follow
        // instantly instead of waiting out the far-away timer. In Defense / Garrison mode,
        // an owner teleport breaks the post and snaps the companion back to Attack mode at owner's side.
        if (ownerTeleported)
        {
            // A posted mode breaks on an owner teleport and snaps back to Attack at the owner's
            // side. Free-roam is a deliberate standing order, not a post, so it is preserved — the
            // companion simply teleports along and keeps roaming the owner's new vicinity.
            if (profile.Mode != CompanionMode.FreeRoam)
                profile.Mode = CompanionMode.Attack;
            softHoldActive = false;
            instance.Waypoint = null;
            instance.WaypointTimeLeft = 0;
            TeleportBeside(me, owner);
            return;
        }

        // 护驾: come guard the owner. A manual double-click recall applies in every mode; the
        // automatic "owner was hurt" signal is ignored in Garrison mode so a posted companion
        // is not yanked off its fort by a stray hit. Either outranks the far-away timer —
        // anything past a few tiles is too far to help. The cooldown stops lava/debuff tick
        // damage from turning this into a dust strobe.
        bool manualRescue = ownerData.ManualRescueTime > 0;
        bool automaticRescue = ownerData.RescueSignalTime > 0 && profile.Mode != CompanionMode.Garrison;
        if ((manualRescue || automaticRescue) && rescueCooldown == 0)
        {
            // Explicit player recalls keep their established behaviour. Automatic hurt rescues
            // instead require a clear, dry, non-hurting spot around the owner; if none exists,
            // keeping the companion where it is is safer than materialising on the hitbox.
            if (manualRescue || TeleportNearOwnerSafely(me, owner))
            {
                if (manualRescue)
                    TeleportBeside(me, owner);
                rescueCooldown = RescueCooldownTicks;
            }
            return;
        }

        // A companion standing below an owner has a different failure mode from a wall wedge:
        // every jump moves enough to reset the ordinary stuck timer, yet it never reaches the
        // upper level. Detect that failed upward progress before the generic stuck fallback.
        if (UpdateVerticalFollowRescue(me, owner, mode))
        {
            TeleportBeside(me, owner);
            return;
        }

        // Wedged: actively trying to travel but going nowhere for a sustained time. Recall to
        // the owner rather than leave it jittering against geometry forever.
        if (UpdateStuckDetection(me, owner))
            return;

        // The slow distance-based recall is skipped for a companion that is deliberately
        // posted (garrison / reached waypoint / travelling to one) and for Defense mode, which
        // is meant to keep up by pathing — it relies on the instant owner-teleport and stuck
        // recalls above, never on drifting far and waiting out a timer.
        if (posted || profile.Mode == CompanionMode.Escort)
        {
            farFromOwnerTicks = 0;
            return;
        }

        if (distance > TeleportDistance)
            farFromOwnerTicks++;
        else
            farFromOwnerTicks = System.Math.Max(0, farFromOwnerTicks - 2);

        // Teleport only as a last resort: far away from the owner for a sustained time.
        if (farFromOwnerTicks < TeleportDelayTicks)
            return;

        TeleportBeside(me, owner);
    }

    /// <summary>
    /// Returns true (and recalls to the owner) when the companion is wedged: it wants to move
    /// but has failed to make real headway for a sustained stretch — the "jumped several times
    /// and still cannot get out" case. Progress of any kind resets the timer.
    /// </summary>
    private bool UpdateStuckDetection(Player me, Player owner)
    {
        if (!navigator.IsTryingToMove)
        {
            stuckReference = me.Center;
            notProgressingTicks = 0;
            return false;
        }

        notProgressingTicks++;
        if (Vector2.DistanceSquared(me.Center, stuckReference) > StuckProgressDistance * StuckProgressDistance)
        {
            stuckReference = me.Center;
            notProgressingTicks = 0;
            return false;
        }

        if (notProgressingTicks < StuckTeleportTicks)
            return false;

        TeleportBeside(me, owner);
        return true;
    }

    /// <summary>
    /// Recalls only follow-oriented tactics when their owner is well above them and repeated jump
    /// presses have not produced a real net climb. Attack, Free Roam and Escort all eventually
    /// regroup with the owner; Garrison and utility orders intentionally do not use this shortcut.
    /// </summary>
    private bool UpdateVerticalFollowRescue(Player me, Player owner, CompanionMode mode)
    {
        bool followsOwner = mode is CompanionMode.Attack or CompanionMode.FreeRoam or CompanionMode.Escort;
        bool ownerClearlyAbove = owner.Center.Y < me.Center.Y - VerticalRescueMinimumHeight;
        if (!followsOwner || !ownerClearlyAbove || !navigator.IsTryingToMove)
        {
            ResetVerticalFollowRescue();
            return false;
        }

        // Do not start a rescue merely because the owner is standing on a higher platform. A
        // deliberate jump input is the evidence that navigation has actually tried to get there.
        if (!verticalRescueActive)
        {
            if (!me.controlJump)
                return false;

            verticalRescueActive = true;
            verticalRescueBestY = me.Center.Y;
            verticalRescueNoProgressTicks = 0;
            verticalRescueJumpAttempts = 0;
            verticalRescueJumpHeld = false;
        }

        if (me.Center.Y < verticalRescueBestY - VerticalRescueProgressDistance)
        {
            verticalRescueBestY = me.Center.Y;
            verticalRescueNoProgressTicks = 0;
        }
        else
            verticalRescueNoProgressTicks++;

        if (me.controlJump && !verticalRescueJumpHeld)
            verticalRescueJumpAttempts++;
        verticalRescueJumpHeld = me.controlJump;

        return verticalRescueJumpAttempts >= VerticalRescueJumpAttempts &&
            verticalRescueNoProgressTicks >= VerticalRescueNoProgressLimit;
    }

    private void ResetVerticalFollowRescue()
    {
        verticalRescueActive = false;
        verticalRescueBestY = 0f;
        verticalRescueNoProgressTicks = 0;
        verticalRescueJumpAttempts = 0;
        verticalRescueJumpHeld = false;
    }

    /// <summary>True on the tick the owner's position jumps too far to be ordinary movement —
    /// a teleport. Always records the owner's position so the next comparison is fresh.</summary>
    private bool DetectOwnerTeleport(Player owner)
    {
        Vector2 now = owner.Center;
        bool jumped = ownerPositionKnown &&
            Vector2.DistanceSquared(now, lastOwnerCenter) > OwnerTeleportJump * OwnerTeleportJump;
        lastOwnerCenter = now;
        ownerPositionKnown = true;
        return jumped;
    }

    private void TeleportBeside(Player me, Player owner)
    {
        CompanionEntitySystem.SpawnRecallDustBurst(me.Center);
        me.Center = owner.Center + new Vector2(owner.direction * -48f, -8f);
        me.velocity = Vector2.Zero;
        me.fallStart = (int)(me.position.Y / 16f);
        navigator.Reset();
        farFromOwnerTicks = 0;
        // Recalling to the owner ends a soft (waypoint-derived) post so the companion resumes
        // following. A true Garrison mark is left intact — the companion walks back to it.
        softHoldActive = false;
        stuckReference = me.Center;
        notProgressingTicks = 0;
        ResetVerticalFollowRescue();
        CompanionEntitySystem.SpawnRecallDustBurst(me.Center);
    }

    /// <summary>Moves an automatic rescue to a separate, terrain-safe position near the owner.
    /// A bad rescue is worse than no rescue when the owner is standing in lava, inside a boss,
    /// or on a narrow ledge, so this has no unsafe overlap fallback.</summary>
    private bool TeleportNearOwnerSafely(Player me, Player owner)
    {
        const float minimumDistance = 80f;
        const float worldMargin = 240f;
        int width = System.Math.Max(20, me.width);
        int height = System.Math.Max(42, me.height);
        int preferredDirection = (me.whoAmI & 1) == 0 ? -1 : 1;
        float worldRight = Main.maxTilesX * 16f - worldMargin;
        int ownerTileY = System.Math.Clamp((int)(owner.Center.Y / 16f), 20, Main.maxTilesY - 20);

        // Alternating sides and a slot-derived first side naturally fan a group out instead of
        // making every companion test the same tile first.
        for (int attempt = 0; attempt < 24; attempt++)
        {
            int direction = attempt % 2 == 0 ? preferredDirection : -preferredDirection;
            float distance = minimumDistance + (attempt / 2 % 6) * 24f;
            float x = MathHelper.Clamp(owner.Center.X + direction * distance, worldMargin, worldRight);
            int tileX = System.Math.Clamp((int)(x / 16f), 20, Main.maxTilesX - 20);
            for (int floorRow = System.Math.Max(20, ownerTileY - 20); floorRow <= System.Math.Min(Main.maxTilesY - 20, ownerTileY + 48); floorRow++)
            {
                Vector2 candidate = new(tileX * 16f + 8f, floorRow * 16f - height * 0.5f);
                if (!IsSafeRescueLocation(candidate, me, owner, width, height))
                    continue;

                RelocateAfterSafeRescue(me, candidate);
                return true;
            }
        }

        return false;
    }

    private static bool IsSafeRescueLocation(Vector2 center, Player me, Player owner, int width, int height)
    {
        Vector2 topLeft = center - new Vector2(width * 0.5f, height * 0.5f);
        if (Vector2.DistanceSquared(center, owner.Center) < 64f * 64f ||
            Collision.SolidCollision(topLeft, width, height) ||
            Collision.WetCollision(topLeft, width, height) ||
            Collision.AnyHurtingTiles(topLeft, width, height) ||
            !Collision.SolidCollision(topLeft + new Vector2(0f, 4f), width, height, acceptTopSurfaces: true))
            return false;

        Rectangle landingHitbox = new((int)topLeft.X, (int)topLeft.Y, width, height);
        foreach (Player player in Main.ActivePlayers)
        {
            if (player.whoAmI != me.whoAmI && player.Hitbox.Intersects(landingHitbox))
                return false;
        }

        foreach (NPC npc in Main.ActiveNPCs)
        {
            if (!npc.friendly && npc.damage > 0 && Vector2.DistanceSquared(npc.Center, center) < 144f * 144f)
                return false;
        }

        foreach (Projectile projectile in Main.ActiveProjectiles)
        {
            if (projectile.hostile && projectile.damage > 0 && Vector2.DistanceSquared(projectile.Center, center) < 112f * 112f)
                return false;
        }

        return true;
    }

    private void RelocateAfterSafeRescue(Player me, Vector2 destination)
    {
        CompanionEntitySystem.SpawnRecallDustBurst(me.Center);
        me.Center = destination;
        me.velocity = Vector2.Zero;
        me.fallStart = (int)(me.position.Y / 16f);
        navigator.Reset();
        farFromOwnerTicks = 0;
        softHoldActive = false;
        stuckReference = me.Center;
        notProgressingTicks = 0;
        ResetVerticalFollowRescue();
        CompanionEntitySystem.SpawnRecallDustBurst(me.Center);
    }

    /// <summary>
    /// Simulates one click. At most one of these lands per tick: the brain decides an entire
    /// tick's actions before vanilla runs a single ItemCheck, so itemAnimation still reads 0
    /// for every call here and a later caller would otherwise silently steal selectedItem
    /// from an earlier one. First caller wins, which is what makes the summon step (run
    /// first) actually outrank the attack that follows it.
    /// </summary>
    private void UseHeldItem(CompanionInstance instance, Player me, int inventorySlot, Vector2 aimWorld, bool altUse = false)
    {
        if (itemUseClaimedThisTick)
        {
            AttackState = CompanionAttackState.UseAlreadySpent;
            return;
        }

        if (inventorySlot < 0 || inventorySlot >= me.inventory.Length)
            return;

        // Channelled "holdout" weapons (GrandDad and similar) keep itemAnimation pinned
        // above 0 indefinitely while idling between swings, so the itemAnimation == 0 gate
        // below would never fire again once one is active. If the item actually sitting in
        // this slot no longer matches what started the current use — the backpack UI swapped
        // it — force the old use to end now instead of leaving the companion stuck on it.
        bool staleChanneledItem = me.itemAnimation > 0 && me.HeldItem.channel &&
            instance.ActiveItemType >= 0 &&
            (me.selectedItem != inventorySlot || me.HeldItem.type != instance.ActiveItemType);
        if (staleChanneledItem)
            instance.ForceReleaseChanneledWeapon();

        // Watchdog. Everything below is conditional on the current animation finishing, and a
        // companion whose animation never finishes would wait forever without ever saying so —
        // the failure mode is a companion that simply stands there, which has been mistaken for
        // half a dozen unrelated bugs. Rather than enumerate every way an animation can get
        // pinned (channelled holdouts, mod weapons that manage their own timers, an item swapped
        // mid-swing), give up after a fixed wait and take the slot by force.
        if (me.itemAnimation > 0 && me.selectedItem != inventorySlot)
        {
            if (++blockedAttackTicks >= BlockedAttackForceTicks)
            {
                blockedAttackTicks = 0;
                instance.ForceReleaseChanneledWeapon();
                me.itemAnimation = 0;
                me.itemTime = me.itemTimeMax = 0;
                me.channel = false;
                instance.ActiveItemType = -1;
            }
        }
        else
        {
            blockedAttackTicks = 0;
        }

        if (me.itemAnimation == 0)
        {
            // Vanilla only ever moves selectedItem through SmartSelectLookup/the hotbar, and
            // clears itemTime as it does (Player.ItemCheck_Inner, "prevent remaining item time
            // carrying over to new weapon"). The brain assigns selectedItem directly, so it
            // owns that reset too. Without it, leftover item time from the previous weapon —
            // the summon staff MaintainSummons casts ahead of every fight, most often — makes
            // ItemTimeIsZero false on the tick the new use starts, and
            // ItemCheck_OwnerOnlyCode's `itemAnimation > 0 && ItemTimeIsZero` gate silently
            // skips ItemCheck_Shoot: the swing animates and plays its sound, melee contact
            // damage still lands, but no projectile is ever spawned.
            if (me.selectedItem != inventorySlot)
                me.itemTime = me.itemTimeMax = 0;

            me.selectedItem = inventorySlot;
            instance.ActiveItemType = me.inventory[inventorySlot].type;
        }
        if (me.selectedItem != inventorySlot)
        {
            AttackState = CompanionAttackState.AnimationBusy;
            return; // finish the current swing before switching
        }

        // ItemCheck consumes controlUseItem for both primary and alternate item uses.
        // The virtual mouse buttons and altFunctionUse carry which of the two was chosen.
        itemUseClaimedThisTick = true;
        blockedAttackTicks = 0;
        me.controlUseItem = true;
        instance.WantsUseItem = true;
        me.releaseUseItem = me.itemAnimation == 0;
        // Vanilla promotes altFunctionUse 1 -> 2 when the use actually starts (ItemCheck),
        // and resets 2 -> 0 when the animation ends. Only steer it between uses.
        if (me.itemAnimation == 0)
            me.altFunctionUse = altUse ? 1 : 0;
        instance.WantsAltUse = altUse;
        instance.AimWorld = aimWorld;
        AttackState = CompanionAttackState.Attacking;
    }

    private static void FaceToward(Player me, Vector2 worldPosition)
    {
        if (me.controlLeft || me.controlRight)
            return;
        me.ChangeDir(worldPosition.X >= me.Center.X ? 1 : -1);
    }

    /// <summary>Finds a tool in the hotbar first, then the backpack; returns the entity inventory index.</summary>
    private static int FindToolInventoryIndex(CompanionProfile profile, System.Func<Item, bool> predicate)
    {
        for (int i = 0; i < profile.Hotbar.Length; i++)
        {
            if (!profile.Hotbar[i].IsAir && predicate(profile.Hotbar[i]))
                return CompanionInstance.HotbarToInventoryIndex(i);
        }
        for (int i = 0; i < profile.Backpack.Length; i++)
        {
            if (!profile.Backpack[i].IsAir && predicate(profile.Backpack[i]))
                return CompanionInstance.BackpackToInventoryIndex(i);
        }
        return -1;
    }
}
