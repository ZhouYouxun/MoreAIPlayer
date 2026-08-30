using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;

namespace MoreAIPlayer.Companion.Automation;

/// <summary>Short-range ordinary-enemy interruption shared by resource work and exploration.
/// Campaign bosses remain owned by <see cref="AutoBossController"/>, while threats beyond the
/// current loadout's tolerance remain owned by <see cref="AutoRecoveryController"/>.</summary>
public static class AutoThreatController
{
    private const float ImmediateRadiusTiles = 12f;
    private const float AwarenessRadiusTiles = 22f;
    private const float ExplorationRadiusTiles = 20f;
    private const float RangedAttackTiles = 9f;
    private const float RangedRetreatTiles = 3f;
    private const float MeleeApproachTiles = 2.25f;
    private const float MeleeUseTiles = 3.5f;

    public static bool TryDefendImmediate(AutoContext ctx)
    {
        if (AutoCampaignController.HasLiveTarget() || AutoRecoveryController.IsOverwhelmed(ctx))
            return false;
        NPC target = FindApproachingOrdinaryHostile(ctx.Me.Center, AwarenessRadiusTiles);
        if (target is null)
            return false;
        PrepareWeaponEarly(ctx, target);
        if (IsHighThreat(ctx, target) && CanBeStoppedByBarrier(target) &&
            ctx.Fieldcraft.TryBuildThreatBarrier(ctx, target))
            return true;
        // Once a hostile is observably closing, defending owns this tick. Otherwise the main task
        // would immediately switch back to its pickaxe/axe and undo the early weapon draw.
        return Engage(ctx, target);
    }

    public static bool TryDefendWhileExploring(AutoContext ctx)
    {
        NPC target = FindNearestOrdinaryHostile(ctx.Me.Center, ExplorationRadiusTiles);
        if (target is null)
            return false;
        PrepareWeaponEarly(ctx, target);
        if (IsHighThreat(ctx, target) && CanBeStoppedByBarrier(target) &&
            ctx.Fieldcraft.TryBuildThreatBarrier(ctx, target))
            return true;
        return Engage(ctx, target);
    }

    public static bool TryEngageTarget(AutoContext ctx, NPC target)
    {
        if (target is null || !target.active || target.friendly || target.townNPC || target.boss ||
            target.life <= 0 || target.dontTakeDamage || target.damage <= 0)
            return false;
        PrepareWeaponEarly(ctx, target);
        return Engage(ctx, target);
    }

    private static bool Engage(AutoContext ctx, NPC target)
    {
        int weaponSlot = AutoCombatController.BestCombatInventoryIndex(ctx, target,
            out AutoWeaponAssessment assessment);
        if (weaponSlot < 0 || !assessment.AmmoReady || !assessment.ManaReady)
        {
            // The old unconditional retreat ran before planning every frame. A slime changing
            // sides therefore reversed the escape point forever and a fresh profile never got a
            // single tick to gather wood or craft its requested weapon. Evasion is now bounded;
            // once released, false deliberately returns control to the retained progression plan.
            return ctx.Fieldcraft.TryRetreatUnarmed(ctx, target);
        }

        Item weapon = ctx.Me.inventory[weaponSlot];
        bool ranged = assessment.Role is AutoWeaponRole.Ranged or AutoWeaponRole.Magic ||
            weapon.shoot > ProjectileID.None || weapon.shootSpeed > 0f ||
            weapon.useAmmo > AmmoID.None || weapon.mana > 0;
        float distance = Vector2.Distance(ctx.Me.Center, target.Center);
        float targetRadius = System.Math.Max(0f, target.width * 0.5f);
        float stopRange = ranged
            ? RangedAttackTiles * 16f
            : MeleeApproachTiles * 16f + targetRadius;
        float useRange = ranged
            ? RangedAttackTiles * 16f
            : MeleeUseTiles * 16f + targetRadius;

        if (ranged && distance < RangedRetreatTiles * 16f)
        {
            // Keep one real escape point while continuing to fire. A relative point rebuilt from
            // the current body position moved away forever, and flipping it whenever a slime
            // crossed the player produced the same visible left/right pacing as unarmed retreat.
            ctx.Fieldcraft.RetreatFromThreat(ctx, target, 6f);
        }
        else if (distance > stopRange)
            ctx.Navigator.Navigate(ctx.Me, target.Center);
        else
            ctx.Navigator.Idle(ctx.Me);

        // A melee fighter begins swinging during the last part of the approach, but never stops
        // at that outer use radius. It closes to the smaller stop range, eliminating the old
        // seven-tile stand-and-stare failure while preserving continuous attack animation.
        if (ranged || distance <= useRange)
            ctx.Actuator.UseItemInSlot(ctx.Instance, ctx.Me, weaponSlot, target.Center);
        return true;
    }

    private static void PrepareWeaponEarly(AutoContext ctx, NPC target)
    {
        int weaponSlot = AutoCombatController.BestCombatInventoryIndex(ctx, target, out _);
        if (weaponSlot < 0)
            return;

        ctx.Instance.AimWorld = target.Center;
        ctx.Me.direction = target.Center.X >= ctx.Me.Center.X ? 1 : -1;
        if (ctx.Me.itemAnimation > 0 || ctx.Me.selectedItem == weaponSlot)
            return;
        ctx.Me.itemTime = ctx.Me.itemTimeMax = 0;
        ctx.Me.selectedItem = weaponSlot;
        ctx.Instance.ActiveItemType = ctx.Me.inventory[weaponSlot].type;
    }

    private static bool IsHighThreat(AutoContext ctx, NPC target)
    {
        int effectiveContactDamage = System.Math.Max(1, target.damage - (int)ctx.Me.statDefense / 2);
        bool heavyHit = effectiveContactDamage >= System.Math.Max(24, ctx.Me.statLife / 3);
        bool durable = target.lifeMax >= System.Math.Max(180, ctx.Me.statLifeMax2 * 2);
        bool fastClosing = IsClosingOn(target, ctx.Me.Center, 1.6f);
        return heavyHit || (durable && fastClosing);
    }

    private static bool CanBeStoppedByBarrier(NPC target) =>
        !target.noTileCollide && !target.noGravity;

    private static NPC FindNearestOrdinaryHostile(Vector2 center, float radiusTiles)
    {
        NPC best = null;
        float bestDistanceSquared = radiusTiles * 16f * radiusTiles * 16f;
        foreach (NPC npc in Main.ActiveNPCs)
        {
            if (!npc.active || npc.friendly || npc.townNPC || npc.boss || npc.life <= 0 ||
                npc.dontTakeDamage || npc.damage <= 0 || npc.realLife >= 0)
                continue;
            float distance = Vector2.DistanceSquared(center, npc.Center);
            if (distance >= bestDistanceSquared)
                continue;
            bestDistanceSquared = distance;
            best = npc;
        }
        return best;
    }

    private static NPC FindApproachingOrdinaryHostile(Vector2 center, float radiusTiles)
    {
        NPC best = null;
        float bestScore = float.MaxValue;
        float radiusSquared = radiusTiles * 16f * radiusTiles * 16f;
        foreach (NPC npc in Main.ActiveNPCs)
        {
            if (!IsOrdinaryHostile(npc))
                continue;
            float distanceSquared = Vector2.DistanceSquared(center, npc.Center);
            if (distanceSquared > radiusSquared)
                continue;
            float distance = System.MathF.Sqrt(distanceSquared);
            bool immediate = distance <= ImmediateRadiusTiles * 16f;
            float closingSpeed = ClosingSpeed(npc, center);
            if (!immediate && closingSpeed < 0.45f)
                continue;
            // Prefer the closer threat, then one which is closing faster. This arms early without
            // abandoning work to chase a harmless enemy moving away at the edge of awareness.
            float score = distance - closingSpeed * 40f;
            if (score >= bestScore)
                continue;
            bestScore = score;
            best = npc;
        }
        return best;
    }

    private static bool IsOrdinaryHostile(NPC npc) =>
        npc.active && !npc.friendly && !npc.townNPC && !npc.boss && npc.life > 0 &&
        !npc.dontTakeDamage && npc.damage > 0 && npc.realLife < 0;

    private static bool IsClosingOn(NPC npc, Vector2 center, float threshold) =>
        ClosingSpeed(npc, center) >= threshold;

    private static float ClosingSpeed(NPC npc, Vector2 center)
    {
        Vector2 toward = (center - npc.Center).SafeNormalize(Vector2.Zero);
        return Vector2.Dot(npc.velocity, toward);
    }
}
