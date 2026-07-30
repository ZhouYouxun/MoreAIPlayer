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
    private int stateTimer = 7200; // 120s hunting initial
    private int potionCooldown;
    private Vector2 restingSpot;
    private bool hasRestingSpot;

    public const int HuntingDurationTicks = 7200;  // 2 minutes hunting
    public const int RestingDurationTicks = 2700;  // 45 seconds resting/developing

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
        me.releaseUseItem = me.itemAnimation == 0;
        me.releaseUseTile = true;
        instance.WantsUseItem = false;
        instance.WantsAltUse = false;
        instance.AimWorld = me.Center + new Vector2(me.direction * 160f, 0f);

        if (potionCooldown > 0)
            potionCooldown--;

        // State cycling
        stateTimer--;
        if (stateTimer <= 0)
        {
            if (State == HunterState.Hunting)
            {
                State = HunterState.Resting;
                stateTimer = RestingDurationTicks;
                restingSpot = me.Center;
                hasRestingSpot = true;
                Main.NewText(Language.GetTextValue("Mods.MoreAIPlayer.CompanionCommand.HunterResting", me.name), 255, 200, 100);
            }
            else
            {
                State = HunterState.Hunting;
                stateTimer = HuntingDurationTicks;
                hasRestingSpot = false;
                Main.NewText(Language.GetTextValue("Mods.MoreAIPlayer.CompanionCommand.HunterHunting", me.name), 255, 100, 100);
            }
        }

        // Auto healing potion if HP <= 50%
        int maxHp = System.Math.Max(200, me.statLifeMax2 > 0 ? me.statLifeMax2 : me.statLifeMax);
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
        }
        else
        {
            UpdateRestingBehavior(instance, me, targetPlayer);
        }
    }

    private void UpdateHuntingBehavior(HunterInstance instance, Player me, Player targetPlayer)
    {
        float distance = Vector2.Distance(me.Center, targetPlayer.Center);

        // Catch-up teleport if player is too far (e.g. Magic Mirror / Teleport > 2000px)
        if (distance > 2000f)
        {
            Vector2 spawnOffset = new(Main.rand.NextBool() ? -800f : 800f, Main.rand.NextFloat(-100f, 100f));
            me.Center = targetPlayer.Center + spawnOffset;
            me.velocity = Vector2.Zero;
            CompanionEntitySystem.SpawnRecallDustBurst(me.Center);
            distance = Vector2.Distance(me.Center, targetPlayer.Center);
        }

        // Select best weapon slot (0 or 1)
        int selectedSlot = 0;
        Item weapon = me.inventory[0];
        if (weapon is null || weapon.IsAir || weapon.damage <= 0)
        {
            weapon = me.inventory[1];
            selectedSlot = 1;
        }

        // Aiming with slight jitter (makes it balanced and dodgeable)
        Vector2 aimTarget = targetPlayer.Center + new Vector2(Main.rand.NextFloat(-24f, 24f), Main.rand.NextFloat(-24f, 24f));
        instance.AimWorld = aimTarget;

        if (weapon is not null && !weapon.IsAir && weapon.damage > 0)
        {
            me.selectedItem = selectedSlot;

            // Non-auto-reuse weapons must release controlUseItem while animating so releaseUseItem resets for next shot
            bool allowPress = weapon.autoReuse || me.itemAnimation == 0;
            if (allowPress)
            {
                instance.WantsUseItem = true;
                me.controlUseItem = true;
            }
        }

        // Movement towards player
        if (distance > 120f)
        {
            navigator.Navigate(me, targetPlayer.Center);
        }
        else if (distance < 60f)
        {
            // Back up slightly if too close
            if (targetPlayer.Center.X < me.Center.X)
                me.controlRight = true;
            else
                me.controlLeft = true;
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
                me.statLife = System.Math.Min(maxHp, me.statLife + item.healLife);
                CombatText.NewText(me.Hitbox, Color.Green, item.healLife.ToString());
                Terraria.Audio.SoundEngine.PlaySound(SoundID.Item3, me.Center);
                potionCooldown = 1800; // 30s CD
                break;
            }
        }
    }
}
