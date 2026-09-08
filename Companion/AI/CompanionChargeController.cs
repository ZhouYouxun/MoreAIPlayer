using System;
using System.Collections.Generic;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace MoreAIPlayer.Companion.AI;

/// <summary>Probes silent channelled holdouts with a real button release, then learns from damage.</summary>
internal sealed class CompanionChargeController
{
    private readonly Dictionary<string, int> probeDurations = [];
    private CompanionProfile profile;
    private string weaponKey;
    private int itemType;
    private int shootType;
    private int heldTicks;
    private int silentTicks;
    private int releaseTicks;
    private bool holding;
    private Projectile holdout;
    private int holdoutIdentity;
    private int pendingSession;
    private string pendingKey;
    private int pendingDuration;
    private int pendingTicks;
    private bool outputWhileHeld;
    private int primarySpawns;

    internal int Session { get; private set; }
    internal bool IsObserving => holding || releaseTicks > 0 || pendingTicks > 0;

    internal void Update(CompanionInstance instance, CompanionProfile currentProfile)
    {
        if (!CompanionRuntimePolicy.CanRunSyntheticPlayers)
            return;
        profile = currentProfile;
        if (pendingTicks > 0 && --pendingTicks == 0)
        {
            // A failed short probe may simply not have charged enough. Bounded backoff gives
            // slow weapons a chance without repeatedly interrupting them at the same duration.
            if (!profile.ChargeReleaseTicks.ContainsKey(pendingKey) && probeDurations.Count < 128)
                probeDurations[pendingKey] = Math.Min(1800, pendingDuration * 2);
        }

        Player me = instance.Entity;
        Item weapon = me.HeldItem;
        if (me.dead || !me.active || weapon.type != itemType && (holding || releaseTicks > 0))
        {
            ResetUse();
            return;
        }

        if (releaseTicks > 0)
        {
            Release(instance);
            releaseTicks++;
            // Give the original holdout time to observe the released button and finish its
            // own attack. Never Kill it or zero itemAnimation to force a charged shot.
            if (releaseTicks >= 6 && (!HasHoldout || releaseTicks >= 180))
                ResetUse();
            return;
        }

        if (!instance.WantsUseItem || !IsCandidate(weapon))
        {
            ResetUse();
            return;
        }

        string key = CompanionWeaponOverrides.ItemKey(weapon) + (instance.WantsAltUse ? "/right" : "/left");
        if (!holding || weaponKey != key)
        {
            ResetUse();
            holding = true;
            weaponKey = key;
            itemType = weapon.type;
            shootType = weapon.shoot;
            Session++;
        }

        heldTicks++;
        silentTicks++;
        if (me.heldProj >= 0 && me.heldProj < Main.maxProjectiles)
        {
            Projectile current = Main.projectile[me.heldProj];
            if (current.active && current.owner == me.whoAmI && current.type == shootType)
                TrackHoldout(current);
        }

        if (!HasHoldout)
            return; // No live holdout is not evidence that releasing will help.

        bool learned = profile.ChargeReleaseTicks.TryGetValue(key, out int duration);
        if (!learned)
            duration = probeDurations.TryGetValue(key, out int next) ? next : (HasHoldoutName(holdout) ? 180 : 300);
        if (heldTicks < duration || (!learned && (silentTicks < duration || outputWhileHeld)))
            return;

        pendingSession = Session;
        pendingKey = key;
        pendingDuration = heldTicks;
        pendingTicks = 180;
        releaseTicks = 1;
        Release(instance);
    }

    private static bool IsCandidate(Item weapon) => !weapon.IsAir && weapon.ModItem is not null &&
        weapon.channel && weapon.shoot > ProjectileID.None && weapon.damage > 0 &&
        !CompanionWeaponBehaviors.IsSummonStaff(weapon) && !CompanionWeaponBehaviors.IsFlailWeapon(weapon) &&
        CompanionWeaponOverrides.GetMode(weapon) != CompanionAttackMode.CoinVolley;

    private static bool HasHoldoutName(Projectile projectile)
    {
        string name = projectile.ModProjectile?.Name ?? string.Empty;
        return name.Contains("Hold", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Charge", StringComparison.OrdinalIgnoreCase);
    }

    private bool HasHoldout => holdout is not null && holdout.active && holdout.identity == holdoutIdentity;

    private void TrackHoldout(Projectile projectile)
    {
        holdout = projectile;
        holdoutIdentity = projectile.identity;
    }

    // Some holdouts use Owner.GetSource_ItemUse again for the released payload instead of
    // GetSource_FromThis. Keep that payload attached to the same observation session.
    internal int SourceSession(Item item) => holding && item.type == itemType ? Session : 0;

    internal void ObserveSpawn(Projectile projectile, int session, bool fromItem)
    {
        if (session <= 0 || session != Session || !holding)
            return;
        if (fromItem && projectile.type == shootType)
        {
            // A channelled gun may fire its bullets directly from Shoot rather than through
            // a holdout. Repeated primary projectiles are output too, even if they all miss.
            if (++primarySpawns > 1 && releaseTicks == 0)
            {
                silentTicks = 0;
                outputWhileHeld = true;
            }
            TrackHoldout(projectile);
            return;
        }
        if (releaseTicks == 0 && projectile.friendly && projectile.damage > 0 &&
            !projectile.minion && !projectile.sentry && projectile.type != shootType && !HasHoldoutName(projectile))
        {
            // Continuous guns can miss every shot. Actual emitted ammunition still proves
            // that holding works; don't classify them as charge weapons based on misses.
            silentTicks = 0;
            outputWhileHeld = true;
        }
    }

    internal void ObserveHit(int session, int damageDone)
    {
        if (damageDone <= 0 || session <= 0)
            return;
        if (pendingTicks > 0 && session == pendingSession && profile is not null)
        {
            if (profile.ChargeReleaseTicks.Count < 128 || profile.ChargeReleaseTicks.ContainsKey(pendingKey))
                profile.ChargeReleaseTicks[pendingKey] = Math.Clamp(pendingDuration, 60, 1800);
            pendingTicks = 0;
        }
        if (session == Session && releaseTicks == 0)
        {
            silentTicks = 0;
            outputWhileHeld = true;
        }
    }

    private static void Release(CompanionInstance instance)
    {
        instance.Entity.channel = false;
        instance.Entity.controlUseItem = false;
        instance.Entity.controlUseTile = false;
        instance.Entity.releaseUseItem = true;
        instance.WantsUseItem = false;
        instance.WantsAltUse = false;
    }

    private void ResetUse()
    {
        holding = false;
        heldTicks = silentTicks = releaseTicks = 0;
        outputWhileHeld = false;
        primarySpawns = 0;
        holdout = null;
    }
}
