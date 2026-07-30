using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
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
    private const float RescueDistance = 5f * 16f;
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

    // Escort mode: bodyguard spacing, tighter than the roaming follow distance.
    private const float EscortFollowSide = 34f;
    private const float EscortIdleDistance = 30f;
    // How near the owner an enemy has to be before an escort engages it, and how near itself
    // before it defends regardless. Escort answers threats; it does not go hunting.
    private const float EscortGuardRadius = 20f * 16f;
    private const float EscortSelfDefenseRadius = 12f * 16f;

    // Garrison / posted-hold: how far the companion may stray from its anchor to fight before
    // it is dragged back, and how close counts as "standing on the spot".
    private const float HoldLeash = 8f * 16f;
    private const float HoldReturnDistance = 20f;

    // Command-book waypoint: how close counts as "arrived".
    private const float WaypointArriveDistance = 24f;

    // Stuck-teleport: while actively trying to travel, failing to advance this far for this
    // many ticks means the companion is wedged and recalls to the owner.
    private const float StuckProgressDistance = 24f;
    private const int StuckTeleportTicks = 120; // 2s

    // Revive delay after a death that vanilla actually landed.
    private const int NormalReviveTicks = 300; // 5s
    private const int BossReviveTicks = 600;   // 10s during a boss fight

    // A single-tick owner position jump this large is a teleport (mirror, recall, teleporter,
    // Rod of Discord…), not running — companions follow it instantly instead of waiting out
    // the far-away timer. Far above any continuous movement speed.
    private const float OwnerTeleportJump = 40f * 16f;

    private readonly CompanionNavigator navigator = new();

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

    // Leash applied to combat navigation while holding a post (garrison / reached waypoint).
    private bool combatLeashActive;
    private Vector2 combatLeashAnchor;

    // Stuck-teleport tracking.
    private Vector2 stuckReference;
    private int notProgressingTicks;

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
    private float minionSlotsAtSummonStart;
    private int fruitlessSummons;
    private int summonBackoff;

    // Miss-streak aggression: melee-class weapons push closer after a couple of whiffs.
    private int consecutiveMeleeMisses;
    private bool wasMeleeCombatSwingLastTick;

    // Ticks the guard weapon stays out after the swarm that summoned it has broken up.
    private int guardHoldTicks;

    // Watchdog for an item animation that never ends: how long the brain waits for the slot it
    // wants before taking it by force. 1.5s is longer than any legitimate weapon animation.
    private const int BlockedAttackForceTicks = 90;
    private int blockedAttackTicks;

    public bool ProtectCritters { get; private set; }

    /// <summary>Why the companion is or is not attacking, refreshed every tick and surfaced in
    /// the backpack panel so a companion standing idle can be diagnosed at a glance.</summary>
    public CompanionAttackState AttackState { get; private set; } = CompanionAttackState.NoTarget;

    /// <summary>Name of the weapon the brain settled on, for the same readout. Empty when none.</summary>
    public string ChosenWeaponName { get; private set; } = string.Empty;

    public void Update(CompanionInstance instance, Player owner, CompanionProfile profile)
    {
        Player me = instance.Entity;

        // A weapon use "completed" when the animation ran last tick and has now ended.
        lastUseCompleted = lastItemAnimationSeen > 0 && me.itemAnimation == 0;
        lastItemAnimationSeen = me.itemAnimation;

        if (lastUseCompleted && wasMeleeCombatSwingLastTick)
        {
            CompanionPlayer selfData = me.GetModPlayer<CompanionPlayer>();
            consecutiveMeleeMisses = selfData.MeleeHitLanded ? 0 : System.Math.Min(consecutiveMeleeMisses + 1, 4);
            selfData.MeleeHitLanded = false;
        }
        wasMeleeCombatSwingLastTick = false;

        ResetControls(me);
        itemUseClaimedThisTick = false;
        instance.WorkTile = null;
        instance.WantsAltUse = false;
        instance.WantsUseItem = false;
        instance.AimWorld = me.Center + new Vector2(me.direction * 160f, 0f);
        AttackState = CompanionAttackState.NoTarget;
        ChosenWeaponName = string.Empty;

        if (HandleDeathAndRevive(instance, me, owner))
            return; // still dead this tick — nothing else runs
        me.statMana = me.statManaMax2;

        if (owner.dead)
        {
            navigator.Idle(me);
            return;
        }


        // Summon upkeep comes before everything else that uses an item: the companion tops up
        // its summon slots first, and only then falls through to the next non-summon weapon
        // in hotbar order. A re-summoned companion starts with no minions of its own, so this
        // naturally re-casts every staff without any extra bookkeeping.
        MaintainSummons(instance, me, profile);

        // Resolve mode transitions and the command-book waypoint into a hold anchor.
        UpdateCommandState(instance, me, profile);

        CompanionPlayer ownerData = owner.GetModPlayer<CompanionPlayer>();
        // Falls back to Attack when the mode's tool slot was emptied behind its back.
        CompanionMode mode = CompanionModeRules.Effective(profile);

        // Critter protection is a property of Catch mode, not of owning a net: outside the mode
        // the companion fights normally and a net in the slot is just equipment. Set before
        // SelectTarget, which filters critters out through IsValidTarget.
        ProtectCritters = mode == CompanionMode.Catch;
        if (ProtectCritters)
            CollectCaughtCritters(me, profile);

        // Escort never seeks out enemies — it just shadows the owner. Every other mode targets
        // and attacks identically; only the movement below differs.
        // Evaluated unconditionally so the sticky timer keeps ticking down even on frames with
        // no target at all.
        bool guarding = GuardEngaged(me, out NPC nearestThreat);

        NPC target = SelectTarget(me, owner, ownerData);

        // Escort guards rather than hunts. It previously refused to target anything at all,
        // which is not what "follow and defend" means: a bodyguard that stands next to its
        // charge and watches it get eaten is indistinguishable from a broken companion, and
        // was in fact repeatedly reported as one — every weapon appeared to stop working,
        // because the mode never selected a weapon in the first place. It now fights whatever
        // comes to the owner, and only refuses to go looking for trouble.
        if (mode == CompanionMode.Escort && target is not null && !IsThreatToOwner(target, owner, me))
            target = null;

        CompanionWeaponPlan plan = target is null ? null : CompanionWeaponBehaviors.SelectWeapon(me, target, profile);

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
            CompanionWeaponBehaviors.GuardPlan(profile) is CompanionWeaponPlan guardPlan)
        {
            plan = guardPlan;
            target = nearestThreat ?? target;
            instance.AimWorld = target.Center;
        }

        bool traveling = instance.Waypoint.HasValue;

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
                TryAttack(instance, me, target, plan, profile); // shoot on the move
        }
        else if (target is not null && plan is not null)
        {
            // An escort chasing a target must not drift off its charge, so it fights on the
            // same leash a posted companion uses — anchored to the owner rather than a spot.
            bool escortLeash = mode == CompanionMode.Escort && !holdActive;
            combatLeashActive = holdActive || escortLeash;
            combatLeashAnchor = escortLeash ? owner.Center : holdAnchor;
            PositionForCombat(me, target, plan);
            TryAttack(instance, me, target, plan, profile);
            combatLeashActive = false;
        }
        else if (holdActive)
        {
            HoldAtAnchor(me, owner);
        }
        else if (mode == CompanionMode.Escort)
        {
            FollowOwner(me, owner, EscortFollowSide, EscortIdleDistance);
        }
        else if (mode == CompanionMode.Catch && TryCatchNearbyCritter(instance, me, profile))
        {
        }
        else if (mode == CompanionMode.Mining && TryPerformMiningOrTreeWork(instance, me, owner, ownerData, profile))
        {
        }
        else if (mode == CompanionMode.Fishing)
        {
            // Casting is not implemented yet; standing still is the closest correct behaviour,
            // and matches the "stays put like Garrison" half of the mode's contract.
            navigator.Idle(me);
        }
        else
            FollowOwner(me, owner, 64f, IdleDistance * 0.5f);

        UpdateTeleportPolicy(instance, me, owner, ownerData, profile, holdActive || traveling);
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
            instance.ReviveTicksLeft = IsBossFightActive() ? BossReviveTicks : NormalReviveTicks;

        instance.ReviveTicksLeft--;
        if (instance.ReviveTicksLeft <= 0)
        {
            instance.ReviveTicksLeft = 0;
            me.dead = false;
            me.respawnTimer = 0;
            me.statLife = System.Math.Max(100, me.statLifeMax);
            ResetCorpseGibs(me);
            TeleportBeside(me, owner);
            me.GetModPlayer<CompanionPlayer>().CustomPotionCooldown = 0;
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
    private static void ResetCorpseGibs(Player me)
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

    private static bool IsBossFightActive()
    {
        foreach (NPC npc in Main.ActiveNPCs)
        {
            if (npc.boss || NPCID.Sets.ShouldBeCountedAsBoss[npc.type])
                return true;
        }
        return false;
    }

    private NPC SelectTarget(Player me, Player owner, CompanionPlayer data)
    {
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
            if (ownerDistance > MaximumCombatRange && companionDistance > MaximumCombatRange)
                continue;

            bool requested = data.CombatRequestTime > 0 && data.RequestedTarget == candidate.whoAmI;
            bool closeThreat = System.Math.Min(ownerDistance, companionDistance) <= AutomaticAggroRange;
            bool boss = candidate.boss;
            bool miniBoss = NPCID.Sets.ShouldBeCountedAsBoss[candidate.type] || candidate.rarity > 0 || candidate.lifeMax >= 10000;
            if (!requested && !closeThreat && !boss && !miniBoss)
                continue;

            float score = ScoreTarget(candidate, ownerDistance, companionDistance, requested, closeThreat, boss, miniBoss);
            if (score > bestScore)
            {
                bestScore = score;
                currentTarget = candidate.whoAmI;
            }
        }

        return IsValidTarget(currentTarget) ? Main.npc[currentTarget] : null;
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
            if (me.slotsMinions > minionSlotsAtSummonStart)
                fruitlessSummons = 0;
            else if (++fruitlessSummons >= 3)
            {
                fruitlessSummons = 0;
                summonBackoff = 600;
            }
        }
        summonedLastTick = false;

        if (summonBackoff > 0)
        {
            summonBackoff--;
            return;
        }

        int staffSlot = FindSummonStaffSlot(me, profile);
        if (staffSlot < 0)
            return;

        if (me.itemAnimation == 0)
            minionSlotsAtSummonStart = me.slotsMinions;

        // Staffs spawn their minion on the player regardless of aim; a point just ahead keeps
        // the companion facing the way it is already looking.
        UseHeldItem(instance, me, staffSlot, me.Center + new Vector2(me.direction * 64f, -16f));
        summonedLastTick = true;
    }

    /// <summary>
    /// Summon upkeep reads one slot and one slot only — the dedicated summon slot. Scanning the
    /// whole hotbar meant any staff the player parked as a regular weapon got re-cast on
    /// cooldown forever; now the player says explicitly which staff is the one to keep topped
    /// up. Returns the entity inventory index to cast, or -1 when there is nothing to do.
    /// </summary>
    private static int FindSummonStaffSlot(Player me, CompanionProfile profile)
    {
        Item staff = profile.SummonWeapon;
        if (!CompanionWeaponBehaviors.IsSummonStaff(staff))
            return -1;

        // Sentries are capped separately from minions, by maxTurrets.
        bool hasRoom = CompanionWeaponBehaviors.IsSentryStaff(staff)
            ? CountOwnedSentries(me) < me.maxTurrets
            : me.slotsMinions < me.maxMinions;

        return hasRoom ? CompanionInstance.HotbarToInventoryIndex(CompanionProfile.SummonSlot) : -1;
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

    private (bool isMeleeClass, float standoffFactor, float aggressionFactor) CombatFactors(Player me, CompanionWeaponPlan plan)
    {
        bool isMeleeClass = plan.Weapon.CountsAsClass(DamageClass.Melee);
        // Calamity "holdout" melee weapons (GrandDad and similar) are a self-propelled swing
        // hitbox, not a travelling shot, so the weapon selector's shoot > 0 check mislabels
        // them as UsesProjectile; left alone they'd be kept at gun range and whiff every
        // swing. Standing at half that distance puts them back in their actual swing range.
        bool isMeleeHoldout = isMeleeClass && plan.Weapon.channel && plan.UsesProjectile;
        float holdoutFactor = isMeleeHoldout ? 0.5f : 1f;
        // After a couple of whiffed swings in a row, a melee companion pushes in tighter
        // instead of repeating the exact same losing position.
        float aggressionFactor = isMeleeClass && consecutiveMeleeMisses >= 2 ? 0.7f : 1f;
        return (isMeleeClass, holdoutFactor * aggressionFactor, aggressionFactor);
    }

    /// <summary>Movement half of combat: walks to a firing/striking position. Identical in every
    /// mode except that a posted companion is leashed to its anchor (see <see cref="Travel"/>).</summary>
    private void PositionForCombat(Player me, NPC target, CompanionWeaponPlan plan)
    {
        float distance = Vector2.Distance(me.Center, target.Center);
        bool hasLineOfSight = Collision.CanHitLine(me.Center, 1, 1, target.Center, 1, 1);
        (_, float standoffFactor, float aggressionFactor) = CombatFactors(me, plan);

        if (plan.UsesProjectile)
        {
            float standoffMax = RangedStandoffMax * standoffFactor;
            float standoffMin = RangedStandoffMin * standoffFactor;
            if (distance > standoffMax || !hasLineOfSight)
            {
                Travel(me, target.Center);
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
                Travel(me, target.Center);
            else
                navigator.Idle(me);
        }
    }

    /// <summary>Attack half of combat: fires/swings when the target is reachable. Shared by every
    /// mode, and layered on top of waypoint travel so a companion shoots what it passes.</summary>
    private void TryAttack(CompanionInstance instance, Player me, NPC target, CompanionWeaponPlan plan, CompanionProfile profile)
    {
        float distance = Vector2.Distance(me.Center, target.Center);
        bool hasLineOfSight = Collision.CanHitLine(me.Center, 1, 1, target.Center, 1, 1);
        (bool isMeleeClass, float standoffFactor, float aggressionFactor) = CombatFactors(me, plan);

        // Defensive pre-attack: an enemy closing in from behind cover gets met with attacks
        // before it breaks line of sight, instead of the companion standing idle.
        float closingSpeed = distance > 1f
            ? Vector2.Dot(target.velocity, (me.Center - target.Center) / distance)
            : 0f;
        bool approachingThreat = closingSpeed > 0.8f && distance < 340f;

        bool inUseRange = plan.UsesProjectile
            ? distance <= GenericProjectileUseRange * standoffFactor
            : distance <= CompanionWeaponBehaviors.GetMeleeReach(me, plan.Weapon) * aggressionFactor;
        bool defensiveNoLineOfSight = approachingThreat && (plan.UsesProjectile
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

        Vector2 aim = plan.UsesProjectile
            ? ComputeInterceptAim(me, target, plan.Weapon, profile)
            : target.Center;
        wasMeleeCombatSwingLastTick = isMeleeClass;
        UseHeldItem(instance, me, plan.Slot, aim, useAlt);
    }

    /// <summary>Navigate toward a goal, clamped to the hold leash while posted so combat chasing
    /// never drags the companion off its anchor.</summary>
    private void Travel(Player me, Vector2 goal)
    {
        if (combatLeashActive)
        {
            Vector2 fromAnchor = goal - combatLeashAnchor;
            if (fromAnchor.Length() > HoldLeash)
                goal = combatLeashAnchor + Vector2.Normalize(fromAnchor) * HoldLeash;
        }
        navigator.Navigate(me, goal);
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
    private static Vector2 ComputeInterceptAim(Player me, NPC target, Item weapon, CompanionProfile profile)
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
            aim = target.Center + target.velocity * travelTicks;
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
        // The pick comes from the dedicated slot and nowhere else. Scanning the whole inventory
        // turned any pickaxe the companion happened to be carrying into a licence to dig, which
        // is the loose behaviour the pick slot exists to tighten up.
        int pickaxeSlot = !profile.PickTool.IsAir && CompanionSlotRules.IsPickaxe(profile.PickTool)
            ? CompanionInstance.HotbarToInventoryIndex(CompanionProfile.PickSlot)
            : -1;
        if (pickaxeSlot >= 0 && observation.MiningObservationTime > 0)
        {
            Point targetTile = observation.TunnelDirectionTime >= 180
                ? FindTunnelTile(me, observation)
                : FindSameTypeTile(me, observation);
            if (targetTile.X >= 0)
                return MoveAndWorkTile(instance, me, pickaxeSlot, targetTile);
        }

        int axeSlot = FindToolInventoryIndex(profile, item => item.axe > 0);
        if (axeSlot >= 0 && observation.TreeAssistTime > 0)
        {
            Point treeTile = FindNearestTreeTile(owner.Center, 20);
            if (treeTile.X >= 0)
                return MoveAndWorkTile(instance, me, axeSlot, treeTile);
        }

        return false;
    }

    private Point FindSameTypeTile(Player me, CompanionPlayer observation)
    {
        if (observation.ObservedTileType < 0)
            return new Point(-1, -1);

        Point best = new(-1, -1);
        float bestDistance = float.MaxValue;
        for (int x = observation.ObservedTileX - 8; x <= observation.ObservedTileX + 8; x++)
        {
            for (int y = observation.ObservedTileY - 8; y <= observation.ObservedTileY + 8; y++)
            {
                if (!CanMineNaturalTile(x, y, observation.ObservedTileType))
                    continue;
                if (x == observation.ObservedTileX && y == observation.ObservedTileY)
                    continue;

                float distance = Vector2.DistanceSquared(me.Center, new Vector2(x * 16f + 8f, y * 16f + 8f));
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = new Point(x, y);
                }
            }
        }
        return best;
    }

    private Point FindTunnelTile(Player me, CompanionPlayer observation)
    {
        for (int step = 1; step <= 7; step++)
        {
            int x = observation.ObservedTileX + observation.TunnelDirection.X * step;
            int y = observation.ObservedTileY + observation.TunnelDirection.Y * step;
            if (CanMineNaturalTile(x, y, null))
                return new Point(x, y);
        }
        return FindSameTypeTile(me, observation);
    }

    private static bool CanMineNaturalTile(int x, int y, int? requiredType)
    {
        if (!WorldGen.InWorld(x, y, 3))
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

    private bool MoveAndWorkTile(CompanionInstance instance, Player me, int toolInventorySlot, Point targetTile)
    {
        if (!WorldGen.InWorld(targetTile.X, targetTile.Y, 3) || !Main.tile[targetTile.X, targetTile.Y].HasTile)
            return false;

        Vector2 targetCenter = new(targetTile.X * 16f + 8f, targetTile.Y * 16f + 8f);
        Item tool = me.inventory[toolInventorySlot];
        // Same reach formula vanilla uses in IsTargetTileInItemRange, so the companion only
        // starts swinging once ItemCheck_UseMiningTools will actually hit the tile.
        bool inRange =
            me.position.X / 16f - Player.tileRangeX - tool.tileBoost <= targetTile.X &&
            (me.position.X + me.width) / 16f + Player.tileRangeX + tool.tileBoost - 1f >= targetTile.X &&
            me.position.Y / 16f - Player.tileRangeY - tool.tileBoost <= targetTile.Y &&
            (me.position.Y + me.height) / 16f + Player.tileRangeY + tool.tileBoost - 2f >= targetTile.Y;

        if (!inRange || !Collision.CanHitLine(me.Center, 1, 1, targetCenter, 1, 1))
        {
            navigator.Navigate(me, targetCenter);
            return true;
        }

        navigator.Idle(me);
        instance.WorkTile = targetTile;
        UseHeldItem(instance, me, toolInventorySlot, targetCenter);
        return true;
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
            if (!candidate.active || candidate.catchItem <= 0)
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
            navigator.Navigate(me, critter.Center);
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
            navigator.Navigate(me, waypoint);
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

        navigator.Navigate(me, holdAnchor);
    }

    private void FollowOwner(Player me, Player owner, float sideOffset, float idleReachX)
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

        Vector2 standPoint = owner.Center + new Vector2(followSide * sideOffset, 0f);
        Vector2 offset = standPoint - me.Center;
        if (System.Math.Abs(offset.X) <= idleReachX && System.Math.Abs(offset.Y) <= 96f)
        {
            navigator.Idle(me);
            if (!me.controlLeft && !me.controlRight)
                me.ChangeDir(owner.direction);
            return;
        }

        navigator.Navigate(me, standPoint);
    }

    private void UpdateTeleportPolicy(CompanionInstance instance, Player me, Player owner, CompanionPlayer ownerData, CompanionProfile profile, bool posted)
    {
        if (rescueCooldown > 0)
            rescueCooldown--;

        // Track the owner's per-tick position every tick (even when an early return follows)
        // so a teleport is never missed.
        bool ownerTeleported = DetectOwnerTeleport(owner);
        float distance = Vector2.Distance(me.Center, owner.Center);

        // The owner just teleported (mirror, recall, teleporter, Rod of Discord…). Follow
        // instantly instead of waiting out the far-away timer. In Defense / Garrison mode,
        // an owner teleport breaks the post and snaps the companion back to Attack mode at owner's side.
        if (ownerTeleported && distance > RescueDistance)
        {
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
        bool wantRescue = ownerData.ManualRescueTime > 0 ||
            (ownerData.RescueSignalTime > 0 && profile.Mode != CompanionMode.Garrison);
        if (wantRescue && rescueCooldown == 0 && distance > RescueDistance)
        {
            TeleportBeside(me, owner);
            rescueCooldown = RescueCooldownTicks;
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
