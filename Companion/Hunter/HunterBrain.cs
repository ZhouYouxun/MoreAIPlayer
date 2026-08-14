using Microsoft.Xna.Framework;
using MoreAIPlayer.Companion.AI;
using Terraria;
using Terraria.ID;
using Terraria.Localization;

namespace MoreAIPlayer.Companion.Hunter;

public enum HunterState
{
    Hunting,
    Resting
}

public sealed class HunterBrain
{
    private readonly CompanionNavigator navigator = new();

    public HunterState State { get; private set; } = HunterState.Hunting;
    private int stateTimer = HuntingDurationTicks;
    private int potionCooldown;
    private Vector2 restingSpot;
    private bool hasRestingSpot;
    private Vector2 progressAnchor;
    private bool hasProgressAnchor;
    private int stuckTicks;
    private int relocationCooldown;
    private int aimRefreshTicks;
    private Vector2 aimError;

    public const int HuntingDurationTicks = 5400;  // 90 seconds of pressure
    public const int RestingDurationTicks = 900;   // 15-second tactical reset
    private const int StuckRecoveryTicks = 300;
    private const int RelocationCooldownTicks = 600;
    private const float MeaningfulProgressDistance = 24f;
    private const float FlailSpinDistance = 10f * 16f;

    public void ResetForNewLife(Player me)
    {
        State = HunterState.Hunting;
        stateTimer = CurrentHuntingDuration();
        potionCooldown = 0;
        restingSpot = me?.Center ?? Vector2.Zero;
        hasRestingSpot = false;
        progressAnchor = me?.Center ?? Vector2.Zero;
        hasProgressAnchor = me is not null;
        stuckTicks = 0;
        relocationCooldown = 0;
        aimRefreshTicks = 0;
        aimError = Vector2.Zero;
        navigator.Reset();
    }

    public void Update(HunterInstance instance, Player targetPlayer)
    {
        Player me = instance.Entity;
        if (me is null || !me.active || me.dead)
            return;

        // Reset inputs each tick
        me.controlLeft = false;
        me.controlRight = false;
        me.controlUp = false;
        me.controlDown = false;
        me.controlJump = false;
        me.controlUseItem = false;
        me.controlUseTile = false;
        // Releasing is a real input edge for channelled weapons. Keeping this false for the
        // whole animation made flails spin forever until the hunter lost its target.
        me.releaseUseItem = true;
        me.releaseUseTile = true;
        navigator.UpdateOwnedDoors(me);
        instance.WantsUseItem = false;
        instance.WantsAltUse = false;
        instance.AimWorld = me.Center + new Vector2(me.direction * 160f, 0f);

        if (potionCooldown > 0)
            potionCooldown--;
        if (relocationCooldown > 0)
            relocationCooldown--;
        if (aimRefreshTicks > 0)
            aimRefreshTicks--;

        // Breakout is a continuous interception: a hunter never inexplicably stops resting while
        // the target runs for the exit. Other trials retain short, readable pressure windows.
        if (!HunterSystem.HuntersMayRest && State == HunterState.Resting)
        {
            State = HunterState.Hunting;
            stateTimer = CurrentHuntingDuration();
            hasRestingSpot = false;
        }
        else if (HunterSystem.HuntersMayRest)
        {
            stateTimer--;
            if (stateTimer <= 0 && State == HunterState.Hunting)
            {
                State = HunterState.Resting;
                stateTimer = CurrentRestingDuration();
                restingSpot = me.Center;
                hasRestingSpot = true;
                Main.NewText(Language.GetTextValue("Mods.MoreAIPlayer.CompanionCommand.HunterResting", me.name), 255, 200, 100);
            }
            else if (stateTimer <= 0)
            {
                State = HunterState.Hunting;
                stateTimer = CurrentHuntingDuration();
                hasRestingSpot = false;
                Main.NewText(Language.GetTextValue("Mods.MoreAIPlayer.CompanionCommand.HunterHunting", me.name), 255, 100, 100);
            }
        }

        // Auto healing potion if HP <= 50%
        int maxHp = System.Math.Max(1, me.statLifeMax2 > 0 ? me.statLifeMax2 : me.statLifeMax);
        if (potionCooldown <= 0 && me.statLife <= maxHp * 0.5f)
        {
            UsePotion(me, maxHp);
        }

        if (targetPlayer is null || !targetPlayer.active || targetPlayer.dead)
        {
            navigator.Idle(me);
            return;
        }

        if (State == HunterState.Hunting)
        {
            UpdateHuntingBehavior(instance, me, targetPlayer);
            UpdateStuckRecovery(me, targetPlayer);
        }
        else
        {
            UpdateRestingBehavior(instance, me, targetPlayer);
            ResetProgressTracking(me);
        }
    }

    private void UpdateHuntingBehavior(HunterInstance instance, Player me, Player targetPlayer)
    {
        float distance = Vector2.Distance(me.Center, targetPlayer.Center);
        Vector2 pursuitAnchor = HunterSystem.GetPursuitAnchor(instance, targetPlayer);

        // Hunters deliberately enter from well beyond the screen. Only a true long-distance
        // relocation (mirror, pylon, teleporter) snaps them back into the pursuit.
        if (distance > HunterSystem.CatchUpTeleportDistance)
        {
            RelocateForPursuit(me, targetPlayer, remote: true);
            distance = Vector2.Distance(me.Center, targetPlayer.Center);
        }

        // Every kit has a ranged/throwing pressure weapon and a close-range fallback. This is
        // an actual choice now, rather than an unreachable secondary inventory slot.
        bool useFallback = distance <= 92f;
        int selectedSlot = useFallback ? 1 : 0;
        Item weapon = me.inventory[selectedSlot];
        if (weapon is null || weapon.IsAir || weapon.damage <= 0)
        {
            selectedSlot = useFallback ? 0 : 1;
            weapon = me.inventory[selectedSlot];
        }

        bool canSeeTarget = Collision.CanHitLine(
            me.position, me.width, me.height,
            targetPlayer.position, targetPlayer.width, targetPlayer.height);

        // Keep the deliberate inaccuracy, but refresh it in short bursts instead of rolling a
        // completely different aim point every tick. A modest velocity lead makes ranged kits
        // track a moving target without becoming perfectly accurate.
        if (aimRefreshTicks <= 0)
        {
            aimRefreshTicks = Main.rand.Next(10, 17);
            aimError = new Vector2(Main.rand.NextFloat(-24f, 24f), Main.rand.NextFloat(-18f, 18f));
        }

        float projectileSpeed = weapon is null ? 0f : weapon.shootSpeed;
        float leadTicks = !useFallback && projectileSpeed > 1f
            ? MathHelper.Clamp(distance / projectileSpeed, 0f, 24f)
            : 0f;
        instance.AimWorld = targetPlayer.Center + targetPlayer.velocity * leadTicks + aimError;

        if (canSeeTarget && weapon is not null && !weapon.IsAir && weapon.damage > 0)
        {
            me.selectedItem = selectedSlot;

            bool isFlail = CompanionWeaponBehaviors.IsFlailWeapon(weapon);
            bool activeFlail = isFlail && CompanionWeaponBehaviors.HasActiveFlailProjectile(me);
            bool keepFlailSpinning = isFlail && distance <= FlailSpinDistance;

            // A close flail remains held and spinning. A distant flail gets one initial press,
            // then remains released until its thrown projectile has returned. Other channelled
            // weapons keep their existing continuous-input behavior.
            bool allowPress = isFlail
                ? keepFlailSpinning || !activeFlail && me.itemAnimation == 0
                : weapon.autoReuse || me.itemAnimation == 0;
            if (allowPress)
            {
                instance.WantsUseItem = true;
                me.controlUseItem = true;
                me.releaseUseItem = me.itemAnimation == 0;
            }
        }

        // Ranged hunters create pressure from their kit's intended range; at melee distance
        // they draw the fallback weapon instead of continually trying to flee through terrain.
        float preferredRange = instance.Loadout.PreferredRange;
        // Range alone is not enough: if a wall breaks line of sight, keep pathing until there
        // is a real firing lane instead of standing at the preferred distance shooting stone.
        if (!canSeeTarget)
        {
            navigator.Navigate(me, pursuitAnchor);
        }
        else if (!useFallback && distance > preferredRange + 48f)
        {
            navigator.Navigate(me, pursuitAnchor);
        }
        else if (!useFallback && distance < preferredRange - 48f)
        {
            if (targetPlayer.Center.X < me.Center.X)
                me.controlRight = true;
            else
                me.controlLeft = true;
        }
        else if (useFallback && distance > 72f)
        {
            navigator.Navigate(me, pursuitAnchor);
        }

        // Jump if stuck or target is higher
        if (targetPlayer.Center.Y < me.Center.Y - 48f || me.velocity.X == 0f && (me.controlLeft || me.controlRight))
        {
            me.controlJump = true;
        }
    }

    private void UpdateRestingBehavior(HunterInstance instance, Player me, Player targetPlayer)
    {
        // If attacked by player recently, self-defend
        if (Vector2.Distance(me.Center, targetPlayer.Center) < 180f && targetPlayer.itemAnimation > 0)
        {
            UpdateHuntingBehavior(instance, me, targetPlayer);
            return;
        }

        // Otherwise wander locally around restingSpot
        if (!hasRestingSpot)
            restingSpot = me.Center;

        float distFromRest = Vector2.Distance(me.Center, restingSpot);
        if (distFromRest > 300f)
        {
            navigator.Navigate(me, restingSpot);
        }
        else
        {
            // Idle & periodic jump/step
            if (Main.rand.NextBool(120))
            {
                me.direction = Main.rand.NextBool() ? 1 : -1;
            }
        }
    }

    private void UsePotion(Player me, int maxHp)
    {
        for (int i = 0; i < me.inventory.Length; i++)
        {
            Item item = me.inventory[i];
            if (item is not null && !item.IsAir && item.healLife > 0)
            {
                int lifeBefore = me.statLife;
                me.statLife = System.Math.Min(maxHp, me.statLife + item.healLife);
                int healed = me.statLife - lifeBefore;
                if (healed <= 0)
                    return;

                item.stack--;
                if (item.stack <= 0)
                    item.TurnToAir();

                CombatText.NewText(me.Hitbox, Color.Green, healed.ToString());
                Terraria.Audio.SoundEngine.PlaySound(SoundID.Item3, me.Center);
                potionCooldown = 1800; // 30s CD
                break;
            }
        }
    }

    private void UpdateStuckRecovery(Player me, Player targetPlayer)
    {
        bool tryingToMove = me.controlLeft || me.controlRight || me.controlJump || me.controlDown;
        if (!tryingToMove)
        {
            ResetProgressTracking(me);
            return;
        }

        if (!hasProgressAnchor)
        {
            progressAnchor = me.Center;
            hasProgressAnchor = true;
            stuckTicks = 0;
            return;
        }

        if (Vector2.DistanceSquared(me.Center, progressAnchor) >=
            MeaningfulProgressDistance * MeaningfulProgressDistance)
        {
            progressAnchor = me.Center;
            stuckTicks = 0;
            return;
        }

        stuckTicks++;
        if (stuckTicks < StuckRecoveryTicks || relocationCooldown > 0)
            return;

        RelocateForPursuit(me, targetPlayer, remote: false);
    }

    private void RelocateForPursuit(Player me, Player targetPlayer, bool remote)
    {
        me.Center = remote
            ? HunterSystem.FindRemoteSpawnPosition(targetPlayer, me)
            : HunterSystem.FindRecoverySpawnPosition(targetPlayer, me);
        me.velocity = Vector2.Zero;
        me.fallStart = (int)(me.position.Y / 16f);
        navigator.Reset();
        relocationCooldown = RelocationCooldownTicks;
        progressAnchor = me.Center;
        hasProgressAnchor = true;
        stuckTicks = 0;
        CompanionEntitySystem.SpawnRecallDustBurst(me.Center);
    }

    private void ResetProgressTracking(Player me)
    {
        progressAnchor = me.Center;
        hasProgressAnchor = true;
        stuckTicks = 0;
    }

    private static int CurrentHuntingDuration() => HunterSystem.ActiveTrial switch
    {
        HunterTrialType.Survival => HuntingDurationTicks,
        HunterTrialType.Elimination => 4500,
        HunterTrialType.Breakout => int.MaxValue,
        _ => HuntingDurationTicks
    };

    private static int CurrentRestingDuration() => HunterSystem.ActiveTrial switch
    {
        HunterTrialType.Elimination => 720,
        _ => RestingDurationTicks
    };
}
