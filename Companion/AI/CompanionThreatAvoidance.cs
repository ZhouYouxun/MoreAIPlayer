using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using MoreAIPlayer.Reflection.CalamityMod;
using Terraria;
using Terraria.ID;
using Terraria.Localization;

namespace MoreAIPlayer.Companion.AI;

/// <summary>
/// Predicts nearby hostile projectile/NPC motion and temporarily overrides ordinary travel
/// controls when survival requires it. Decisions are deliberately sticky: the controller
/// commits to one escape for several ticks and only reverses early when another option is
/// substantially safer. That hysteresis is what prevents per-frame left/right oscillation.
/// </summary>
public sealed class CompanionThreatAvoidance
{
    private const float ProjectileVision = 1_050f;
    private const float NpcVision = 620f;
    private const float BossVision = 960f;
    private const int PredictionTicks = 42;
    private const int PredictionStep = 3;
    private const int ScanInterval = 5;
    private const float NoticeRisk = 18f;
    private const float CriticalRisk = 700f;
    private const int MinimumCommitTicks = 16;
    private const int MaximumCommitTicks = 28;
    private const float OwnerSoftLeash = 30f * 16f;

    private enum MoveKind : byte
    {
        None,
        Run,
        Jump,
        Drop,
        Dash,
        Grapple
    }

    private readonly struct MoveCandidate
    {
        public readonly MoveKind Kind;
        public readonly Vector2 Direction;
        public readonly Vector2 Anchor;

        public MoveCandidate(MoveKind kind, Vector2 direction, Vector2 anchor = default)
        {
            Kind = kind;
            Direction = direction;
            Anchor = anchor;
        }
    }

    private readonly List<Projectile> hostileProjectiles = [];
    private readonly List<NPC> hostileNpcs = [];
    private readonly List<MoveCandidate> candidates = [];

    private MoveCandidate plan;
    private int planTicks;
    private int scanCooldown;
    private int reactionTicks;
    private int dashCooldown;
    private int grappleCooldown;
    private int garrisonGrappleCooldown;
    private int mountCooldown;
    private int quietTicks;
    private bool avoidanceGrappleActive;
    private bool garrisonGrappleActive;
    private float avoidanceGrappleStartDistance;
    private int avoidanceGrappleTicks;
    private bool mountedForFastFall;
    private bool environmentThreatVisible;

    public bool Active => plan.Kind != MoveKind.None && planTicks > 0;
    public float CurrentRisk { get; private set; }
    public int VisibleThreats => hostileProjectiles.Count + hostileNpcs.Count + (environmentThreatVisible ? 1 : 0);
    public string StateText { get; private set; } = Text("AvoidNoThreat");

    public void Reset(Player me)
    {
        if ((avoidanceGrappleActive || garrisonGrappleActive) && me is not null)
            me.RemoveAllGrapplingHooks();
        if (mountedForFastFall && me is not null && me.mount.Active)
            me.mount.Dismount(me);

        hostileProjectiles.Clear();
        hostileNpcs.Clear();
        candidates.Clear();
        plan = default;
        planTicks = 0;
        scanCooldown = 0;
        reactionTicks = 0;
        dashCooldown = 0;
        grappleCooldown = 0;
        garrisonGrappleCooldown = 0;
        mountCooldown = 0;
        quietTicks = 0;
        avoidanceGrappleActive = false;
        garrisonGrappleActive = false;
        avoidanceGrappleStartDistance = 0f;
        avoidanceGrappleTicks = 0;
        mountedForFastFall = false;
        environmentThreatVisible = false;
        CurrentRisk = 0f;
        StateText = Text("AvoidNoThreat");
    }

    public void Update(CompanionInstance instance, Player owner, CompanionProfile profile)
    {
        Player me = instance.Entity;
        if (!me.active || me.dead)
        {
            Reset(me);
            return;
        }

        TickCooldowns();

        if (scanCooldown > 0)
        {
            scanCooldown--;
            if (Active)
                ExecutePlan(instance, me, profile);
            else
                FinishMobilityTools(me);
            return;
        }
        scanCooldown = ScanInterval;

        GatherThreats(me);
        MoveCandidate stay = new(MoveKind.None, Vector2.Zero);
        CurrentRisk = Evaluate(me, owner, stay);

        if (VisibleThreats == 0 || CurrentRisk < NoticeRisk)
        {
            if (++quietTicks >= 2)
                ClearPlan(me);
            else if (Active)
                ExecutePlan(instance, me, profile);
            return;
        }
        quietTicks = 0;

        // Human-like reaction latency. A collision predicted in the immediate future bypasses
        // it; ordinary visible danger takes roughly 3-8 ticks to become a movement decision.
        if (!Active && plan.Kind == MoveKind.None && reactionTicks <= 0 && CurrentRisk < CriticalRisk)
        {
            reactionTicks = Main.rand.Next(3, 9);
            StateText = Text("AvoidReacting");
            return;
        }
        if (!Active && plan.Kind == MoveKind.None && reactionTicks > 0 && CurrentRisk < CriticalRisk)
        {
            reactionTicks = Math.Max(0, reactionTicks - ScanInterval);
            if (reactionTicks > 0)
                return;
        }

        BuildCandidates(me, profile);
        MoveCandidate best = stay;
        float bestScore = Evaluate(me, owner, stay);

        foreach (MoveCandidate candidate in candidates)
        {
            float score = Evaluate(me, owner, candidate) + ActionCost(candidate);
            if (Active)
            {
                // Reversing an existing plan is expensive unless the new route is clearly safer.
                float dot = Vector2.Dot(plan.Direction.SafeNormalize(Vector2.Zero),
                    candidate.Direction.SafeNormalize(Vector2.Zero));
                if (dot < -0.2f)
                    score += 34f;
                else if (candidate.Kind != plan.Kind)
                    score += 8f;
            }

            if (score < bestScore)
            {
                bestScore = score;
                best = candidate;
            }
        }

        float currentPlanScore = Active ? Evaluate(me, owner, plan) + ActionCost(plan) : float.MaxValue;
        bool mustReplace = !Active || planTicks <= 0 || currentPlanScore >= CriticalRisk;
        bool worthwhileSwitch = bestScore + 22f < currentPlanScore;
        if (best.Kind != MoveKind.None && (mustReplace || worthwhileSwitch))
        {
            if (avoidanceGrappleActive && plan.Kind == MoveKind.Grapple && best.Kind != MoveKind.Grapple)
            {
                me.RemoveAllGrapplingHooks();
                avoidanceGrappleActive = false;
                avoidanceGrappleStartDistance = 0f;
                avoidanceGrappleTicks = 0;
            }

            plan = best;
            planTicks = Main.rand.Next(MinimumCommitTicks, MaximumCommitTicks + 1);
            StateText = Describe(best);
        }
        else if (!Active && best.Kind == MoveKind.None)
        {
            StateText = Text("AvoidHolding");
            return;
        }

        ExecutePlan(instance, me, profile);
    }

    private void TickCooldowns()
    {
        // Long grapples need more time than the ordinary 16-28 tick movement commitment.
        if (planTicks > 0 && !(plan.Kind == MoveKind.Grapple && avoidanceGrappleActive))
            planTicks--;
        if (dashCooldown > 0)
            dashCooldown--;
        if (grappleCooldown > 0)
            grappleCooldown--;
        if (garrisonGrappleCooldown > 0)
            garrisonGrappleCooldown--;
        if (mountCooldown > 0)
            mountCooldown--;
    }

    private void GatherThreats(Player me)
    {
        hostileProjectiles.Clear();
        hostileNpcs.Clear();
        Vector2 hazardScanPosition = me.position - new Vector2(80f);
        environmentThreatVisible =
            (!me.lavaImmune && Collision.LavaCollision(hazardScanPosition, me.width + 160, me.height + 160)) ||
            Collision.AnyHurtingTiles(hazardScanPosition, me.width + 160, me.height + 160);

        float projectileVisionSquared = ProjectileVision * ProjectileVision;
        foreach (Projectile projectile in Main.ActiveProjectiles)
        {
            if (!projectile.hostile || projectile.damage <= 0 ||
                Vector2.DistanceSquared(me.Center, projectile.Center) > projectileVisionSquared)
            {
                continue;
            }

            // Very distant projectiles travelling away cannot enter the prediction window.
            Vector2 toMe = me.Center - projectile.Center;
            if (toMe.LengthSquared() > 320f * 320f && Vector2.Dot(projectile.velocity, toMe) <= 0f)
                continue;

            hostileProjectiles.Add(projectile);
        }

        foreach (NPC npc in Main.ActiveNPCs)
        {
            if (npc.friendly || npc.damage <= 0 || npc.dontTakeDamage && !npc.boss)
                continue;

            float vision = npc.boss || NPCID.Sets.ShouldBeCountedAsBoss[npc.type] ? BossVision : NpcVision;
            if (Vector2.DistanceSquared(me.Center, npc.Center) <= vision * vision)
                hostileNpcs.Add(npc);
        }
    }

    private void BuildCandidates(Player me, CompanionProfile profile)
    {
        candidates.Clear();

        candidates.Add(new MoveCandidate(MoveKind.Run, -Vector2.UnitX));
        candidates.Add(new MoveCandidate(MoveKind.Run, Vector2.UnitX));
        candidates.Add(new MoveCandidate(MoveKind.Jump, new Vector2(-0.72f, -0.72f)));
        candidates.Add(new MoveCandidate(MoveKind.Jump, -Vector2.UnitY));
        candidates.Add(new MoveCandidate(MoveKind.Jump, new Vector2(0.72f, -0.72f)));
        candidates.Add(new MoveCandidate(MoveKind.Drop, new Vector2(-0.72f, 0.72f)));
        candidates.Add(new MoveCandidate(MoveKind.Drop, Vector2.UnitY));
        candidates.Add(new MoveCandidate(MoveKind.Drop, new Vector2(0.72f, 0.72f)));

        if (CanDash(me, out bool omnidirectionalDash))
        {
            candidates.Add(new MoveCandidate(MoveKind.Dash, -Vector2.UnitX));
            candidates.Add(new MoveCandidate(MoveKind.Dash, Vector2.UnitX));
            if (omnidirectionalDash)
            {
                candidates.Add(new MoveCandidate(MoveKind.Dash, new Vector2(-0.7f, -0.7f)));
                candidates.Add(new MoveCandidate(MoveKind.Dash, new Vector2(0.7f, -0.7f)));
                candidates.Add(new MoveCandidate(MoveKind.Dash, Vector2.UnitY));
            }
        }

        if (!profile.HookTool.IsAir)
            AddGrappleCandidates(me);
    }

    private static bool CanDash(Player me, out bool omnidirectional)
    {
        omnidirectional = false;
        if (me.dashDelay != 0 || me.mount.Active)
            return false;
        if (me.dashType != 0)
            return true;

        // A Calamity custom dash commonly sets vanilla dashType back to zero.
        return CalamityModReflection.TryGetEquippedDashInfo(me, out omnidirectional);
    }

    private void AddGrappleCandidates(Player me)
    {
        const float maxRange = 480f;
        Vector2[] rays =
        [
            new(-0.85f, -0.52f), new(-0.55f, -0.84f), -Vector2.UnitY,
            new(0.55f, -0.84f), new(0.85f, -0.52f),
            -Vector2.UnitX, Vector2.UnitX,
            new(-0.7f, 0.7f), new(0.7f, 0.7f)
        ];

        foreach (Vector2 ray in rays)
        {
            Vector2 direction = ray.SafeNormalize(-Vector2.UnitY);
            for (float distance = 96f; distance <= maxRange; distance += 24f)
            {
                Vector2 point = me.Center + direction * distance;
                Point tile = point.ToTileCoordinates();
                if (!WorldGen.InWorld(tile.X, tile.Y, 2))
                    break;
                if (!WorldGen.SolidOrSlopedTile(tile.X, tile.Y))
                    continue;

                Vector2 anchor = new(tile.X * 16f + 8f, tile.Y * 16f + 8f);
                Vector2 beforeTile = anchor - direction * 18f;
                if (Collision.CanHitLine(me.Center, 1, 1, beforeTile, 1, 1))
                    candidates.Add(new MoveCandidate(MoveKind.Grapple, direction, anchor));
                break;
            }
        }
    }

    private float Evaluate(Player me, Player owner, MoveCandidate candidate)
    {
        float risk = 0f;
        Vector2 previousCenter = me.Center;

        for (int ticks = PredictionStep; ticks <= PredictionTicks; ticks += PredictionStep)
        {
            Vector2 futureCenter = PredictPlayerCenter(me, candidate, ticks);
            Vector2 topLeft = futureCenter - new Vector2(me.width * 0.5f, me.height * 0.5f);
            if (Collision.SolidCollision(topLeft, me.width, me.height))
                risk += 420f;
            if (!me.lavaImmune && Collision.LavaCollision(topLeft, me.width, me.height))
                risk += 1_100f;
            if (Collision.HurtTiles(topLeft, me.width, me.height, me).type >= 0)
                risk += 960f;

            foreach (Projectile projectile in hostileProjectiles)
            {
                int updates = Math.Max(1, projectile.extraUpdates + 1);
                Vector2 projectileCenter = projectile.Center + projectile.velocity * ticks * updates;
                float radius = Math.Max(projectile.width, projectile.height) * 0.5f +
                    Math.Max(me.width, me.height) * 0.45f + 8f;
                float distance = Vector2.Distance(futureCenter, projectileCenter);

                // Sample the segment as well as its endpoint so very fast bullets cannot pass
                // completely between two three-tick samples.
                Vector2 projectilePrevious = projectile.Center +
                    projectile.velocity * Math.Max(0, ticks - PredictionStep) * updates;
                distance = Math.Min(distance,
                    DistancePointToSegment(futureCenter, projectilePrevious, projectileCenter));

                if (distance <= radius)
                    risk += 920f + Math.Min(projectile.damage, 500) * 1.5f;
                else if (distance < radius + 150f)
                    risk += (radius + 150f - distance) * 0.7f;
            }

            foreach (NPC npc in hostileNpcs)
            {
                Vector2 npcCenter = npc.Center + npc.velocity * ticks;
                float radius = Math.Max(npc.width, npc.height) * 0.55f +
                    Math.Max(me.width, me.height) * 0.5f + 14f;
                float distance = Vector2.Distance(futureCenter, npcCenter);
                if (distance <= radius)
                    risk += 760f + Math.Min(npc.damage, 400);
                else if (distance < radius + 120f)
                    risk += (radius + 120f - distance) * 0.42f;
            }

            // Avoid escape paths that abandon a living owner, but survival always outweighs
            // this soft leash when every owner-adjacent position is dangerous.
            if (owner is not null && owner.active && !owner.dead)
            {
                float ownerDistance = Vector2.Distance(futureCenter, owner.Center);
                if (ownerDistance > OwnerSoftLeash)
                    risk += (ownerDistance - OwnerSoftLeash) * 0.035f;
            }

            // Large reversals within one candidate are physically implausible and tend to mean
            // a collision has stopped it. Penalise paths making no progress.
            if (ticks > PredictionStep &&
                Vector2.DistanceSquared(previousCenter, futureCenter) < 1f &&
                candidate.Kind != MoveKind.None)
            {
                risk += 12f;
            }
            previousCenter = futureCenter;
        }

        return risk;
    }

    private static Vector2 PredictPlayerCenter(Player me, MoveCandidate candidate, int ticks)
    {
        if (candidate.Kind == MoveKind.None)
        {
            if (Math.Abs(me.velocity.Y) < 0.01f)
                return me.Center + new Vector2(me.velocity.X * ticks, 0f);
            float fall = me.velocity.Y >= 0f ? 0.18f * ticks * ticks : 0.08f * ticks * ticks;
            return me.Center + me.velocity * ticks + new Vector2(0f, fall);
        }

        Vector2 direction = candidate.Direction.SafeNormalize(Vector2.Zero);
        float runSpeed = Math.Max(5.5f, Math.Max(me.accRunSpeed, me.maxRunSpeed));
        Vector2 targetVelocity = new(direction.X * runSpeed, me.velocity.Y);

        switch (candidate.Kind)
        {
            case MoveKind.Jump:
            {
                bool sustainedFlight = me.wingTimeMax > 0 || me.AnyExtraJumpUsable();
                float upwardSpeed = sustainedFlight ? -7.5f : -6.2f + ticks * 0.24f;
                targetVelocity.Y = direction.Y < -0.2f ? upwardSpeed : me.velocity.Y;
                break;
            }
            case MoveKind.Drop:
                targetVelocity.Y = Math.Max(11f, me.maxFallSpeed);
                break;
            case MoveKind.Dash:
                targetVelocity = direction * 15.5f;
                if (Math.Abs(direction.Y) < 0.25f)
                    targetVelocity.Y = me.velocity.Y;
                break;
            case MoveKind.Grapple:
            {
                Vector2 toAnchor = candidate.Anchor - me.Center;
                Vector2 pullDirection = toAnchor.SafeNormalize(Vector2.Zero);
                float stopDistance = Math.Max(me.width, me.height) * 0.75f;
                float travel = Math.Min(Math.Max(0f, toAnchor.Length() - stopDistance), ticks * 13.5f);
                return me.Center + pullDirection * travel;
            }
        }

        float blend = MathHelper.Clamp(ticks / 10f, 0f, 1f);
        Vector2 velocity = Vector2.Lerp(me.velocity, targetVelocity, blend);
        return me.Center + (me.velocity + velocity) * 0.5f * ticks;
    }

    private void ExecutePlan(CompanionInstance instance, Player me, CompanionProfile profile)
    {
        if (!Active)
        {
            FinishMobilityTools(me);
            return;
        }

        Vector2 direction = plan.Direction.SafeNormalize(Vector2.Zero);

        // Survival movement outranks the ordinary follow/combat path for this tick.
        me.controlLeft = false;
        me.controlRight = false;
        me.controlUp = false;
        me.controlDown = false;
        me.controlJump = false;

        if (direction.X < -0.18f)
            me.controlLeft = true;
        else if (direction.X > 0.18f)
            me.controlRight = true;

        if (plan.Kind == MoveKind.Jump || direction.Y < -0.3f)
        {
            me.controlUp = true;
            // Ground jump, wing flight and extra jumps all pass through the vanilla jump
            // pipeline. releaseJump makes an extra jump a real new press rather than a hold.
            me.controlJump = me.velocity.Y < 0f || me.wingTime > 0f ||
                me.releaseJump && (me.velocity.Y == 0f || me.AnyExtraJumpUsable());
        }
        else if (plan.Kind == MoveKind.Drop || direction.Y > 0.3f)
        {
            me.controlDown = true;
            TryFastFallMount(me, profile);
        }

        if (plan.Kind == MoveKind.Dash && dashCooldown <= 0)
        {
            Vector2 dashAim = me.Center + direction * 320f;
            bool dashed = CalamityModReflection.TryStartEquippedDash(me, direction, dashAim);
            if (!dashed && me.dashType != 0 && me.dashDelay == 0 && Math.Abs(direction.X) > 0.2f)
            {
                // Prime vanilla's second-tap window and feed the matching fresh press. The
                // original DashMovement still owns speed, effects, collision and cooldown.
                me.dashTime = direction.X > 0f ? 15 : -15;
                if (direction.X > 0f)
                    me.releaseRight = true;
                else
                    me.releaseLeft = true;
                dashed = true;
            }
            dashCooldown = dashed ? 45 : 15;
        }

        if (plan.Kind == MoveKind.Grapple)
            ExecuteGrapple(instance, me);
        else if (avoidanceGrappleActive)
        {
            me.RemoveAllGrapplingHooks();
            avoidanceGrappleActive = false;
            avoidanceGrappleStartDistance = 0f;
            avoidanceGrappleTicks = 0;
        }
    }

    private void ExecuteGrapple(CompanionInstance instance, Player me)
    {
        instance.AimWorld = plan.Anchor;
        instance.WantsUseItem = false;
        instance.WantsAltUse = false;
        me.controlUseItem = false;
        me.controlUseTile = false;

        // A stationary garrison hook must never be mistaken for the beginning of an
        // avoidance grapple. Survival movement always owns the hook when danger appears.
        if (garrisonGrappleActive)
        {
            me.RemoveAllGrapplingHooks();
            garrisonGrappleActive = false;
        }

        if (HasOwnedGrapple(me))
        {
            avoidanceGrappleActive = true;
            avoidanceGrappleTicks++;

            // A bad or obstructed hook must not lock the whole avoidance controller forever.
            if (avoidanceGrappleTicks >= 120)
            {
                me.RemoveAllGrapplingHooks();
                avoidanceGrappleActive = false;
                avoidanceGrappleStartDistance = 0f;
                avoidanceGrappleTicks = 0;
                planTicks = 0;
                return;
            }

            // Releasing shortly before reaching the tile preserves the pull velocity. Waiting
            // for the hook to finish would set up the dangerous stop-and-reaccelerate cycle.
            bool attached = me.grapCount > 0 || me.grappling[0] >= 0;
            float remainingDistance = Vector2.Distance(me.Center, plan.Anchor);
            float releaseDistance = Math.Max(16f, avoidanceGrappleStartDistance * 0.10f);
            if (attached && avoidanceGrappleStartDistance > 0f &&
                remainingDistance <= releaseDistance)
            {
                Vector2 retainedVelocity = me.velocity;
                me.controlJump = true;
                me.releaseJump = true;
                me.RemoveAllGrapplingHooks();
                me.velocity = retainedVelocity;
                avoidanceGrappleActive = false;
                avoidanceGrappleStartDistance = 0f;
                avoidanceGrappleTicks = 0;
                StateText = Text("AvoidGrappleRelease");
            }
            return;
        }
        if (grappleCooldown > 0)
            return;

        avoidanceGrappleStartDistance = Vector2.Distance(me.Center, plan.Anchor);
        avoidanceGrappleTicks = 0;
        try
        {
            using VirtualPlayerExecutionContext.Scope scope =
                VirtualPlayerExecutionContext.Enter(me, plan.Anchor, wantsUseItem: false, wantsAltUse: false);
            me.QuickGrapple();
            avoidanceGrappleActive = HasOwnedGrapple(me);
            if (!avoidanceGrappleActive)
            {
                avoidanceGrappleStartDistance = 0f;
                avoidanceGrappleTicks = 0;
            }
        }
        catch
        {
            avoidanceGrappleActive = false;
            avoidanceGrappleStartDistance = 0f;
            avoidanceGrappleTicks = 0;
        }
        grappleCooldown = 45;
    }

    /// <summary>
    /// While a knockback-vulnerable companion is settled at its garrison point, a downward
    /// hook acts as a physical brace. Any visible danger or ordinary movement releases it.
    /// </summary>
    public void UpdateGarrisonAnchor(CompanionInstance instance, CompanionProfile profile, bool requested)
    {
        Player me = instance.Entity;
        bool canAnchor = requested && !profile.HookTool.IsAir && !me.noKnockback &&
            !Active && CurrentRisk < NoticeRisk && !avoidanceGrappleActive;

        if (!canAnchor)
        {
            ReleaseGarrisonGrapple(me);
            return;
        }

        if (garrisonGrappleActive)
        {
            if (HasOwnedGrapple(me))
                return;
            garrisonGrappleActive = false;
        }

        if (HasOwnedGrapple(me) || garrisonGrappleCooldown > 0 ||
            !TryFindDownwardAnchor(me, out Vector2 anchor))
        {
            return;
        }

        instance.AimWorld = anchor;
        instance.WantsUseItem = false;
        instance.WantsAltUse = false;
        me.controlUseItem = false;
        me.controlUseTile = false;

        try
        {
            using VirtualPlayerExecutionContext.Scope scope =
                VirtualPlayerExecutionContext.Enter(me, anchor, wantsUseItem: false, wantsAltUse: false);
            me.QuickGrapple();
            garrisonGrappleActive = HasOwnedGrapple(me);
        }
        catch
        {
            garrisonGrappleActive = false;
        }

        garrisonGrappleCooldown = garrisonGrappleActive ? 30 : 12;
    }

    private static bool TryFindDownwardAnchor(Player me, out Vector2 anchor)
    {
        const float maxRange = 480f;
        float startY = me.Bottom.Y + 8f;

        for (float distance = 0f; distance <= maxRange; distance += 16f)
        {
            Vector2 point = new(me.Center.X, startY + distance);
            Point tile = point.ToTileCoordinates();
            if (!WorldGen.InWorld(tile.X, tile.Y, 2))
                break;
            if (!WorldGen.SolidOrSlopedTile(tile.X, tile.Y))
                continue;

            anchor = new Vector2(tile.X * 16f + 8f, tile.Y * 16f + 8f);
            Vector2 beforeTile = anchor - Vector2.UnitY * 18f;
            if (Collision.CanHitLine(me.Center, 1, 1, beforeTile, 1, 1))
                return true;
            break;
        }

        anchor = default;
        return false;
    }

    private void ReleaseGarrisonGrapple(Player me)
    {
        if (!garrisonGrappleActive)
            return;

        me.RemoveAllGrapplingHooks();
        garrisonGrappleActive = false;
    }

    private static bool HasOwnedGrapple(Player me)
    {
        if (me.grapCount > 0 || me.grappling[0] >= 0)
            return true;

        foreach (Projectile projectile in Main.ActiveProjectiles)
        {
            if (projectile.owner == me.whoAmI &&
                projectile.type > ProjectileID.None && projectile.type < Main.projHook.Length &&
                Main.projHook[projectile.type])
            {
                return true;
            }
        }
        return false;
    }

    private void TryFastFallMount(Player me, CompanionProfile profile)
    {
        Item mountItem = profile.MountTool;
        bool slimeMount = !mountItem.IsAir &&
            (mountItem.mountType == MountID.Slime || mountItem.mountType == MountID.QueenSlime);
        if (!slimeMount || mountCooldown > 0)
            return;

        if (!me.mount.Active)
        {
            me.controlMount = true;
            mountedForFastFall = true;
            mountCooldown = 30;
        }
    }

    private void FinishMobilityTools(Player me)
    {
        if (avoidanceGrappleActive)
        {
            me.RemoveAllGrapplingHooks();
            avoidanceGrappleActive = false;
            avoidanceGrappleStartDistance = 0f;
            avoidanceGrappleTicks = 0;
        }

        if (mountedForFastFall && me.mount.Active && me.velocity.Y == 0f)
        {
            me.mount.Dismount(me);
            mountedForFastFall = false;
        }
    }

    private void ClearPlan(Player me)
    {
        plan = default;
        planTicks = 0;
        reactionTicks = 0;
        CurrentRisk = 0f;
        StateText = Text("AvoidNoThreat");
        FinishMobilityTools(me);
    }

    private static float ActionCost(MoveCandidate candidate) => candidate.Kind switch
    {
        MoveKind.None => 0f,
        MoveKind.Run => 2f,
        MoveKind.Jump => 4f,
        MoveKind.Drop => 5f,
        MoveKind.Dash => 12f,
        MoveKind.Grapple => 16f,
        _ => 0f
    };

    private static string Describe(MoveCandidate candidate)
    {
        string direction = Text(candidate.Direction.Y < -0.35f ? "DirectionUp"
            : candidate.Direction.Y > 0.35f ? "DirectionDown"
            : candidate.Direction.X < 0f ? "DirectionLeft"
            : "DirectionRight");
        return Text(candidate.Kind switch
        {
            MoveKind.Run => "AvoidRun",
            MoveKind.Jump => "AvoidJump",
            MoveKind.Drop => "AvoidDrop",
            MoveKind.Dash => "AvoidDash",
            MoveKind.Grapple => "AvoidGrapple",
            _ => "AvoidHolding"
        }, direction);
    }

    private static string Text(string key, params object[] args) =>
        Language.GetTextValue($"Mods.MoreAIPlayer.CompanionUI.{key}", args);

    private static float DistancePointToSegment(Vector2 point, Vector2 start, Vector2 end)
    {
        Vector2 segment = end - start;
        float lengthSquared = segment.LengthSquared();
        if (lengthSquared <= 0.001f)
            return Vector2.Distance(point, start);
        float amount = MathHelper.Clamp(Vector2.Dot(point - start, segment) / lengthSquared, 0f, 1f);
        return Vector2.Distance(point, start + segment * amount);
    }
}
