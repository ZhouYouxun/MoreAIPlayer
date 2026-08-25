using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;

namespace MoreAIPlayer.Companion.Automation;

/// <summary>Short-range ordinary-enemy interruption shared by resource work and exploration.
/// Campaign bosses remain owned by <see cref="AutoBossController"/>, while threats beyond the
/// current loadout's tolerance remain owned by <see cref="AutoRecoveryController"/>.</summary>
public static class AutoThreatController
{
    private const float ImmediateRadiusTiles = 8f;
    private const float ExplorationRadiusTiles = 14f;

    public static bool TryDefendImmediate(AutoContext ctx)
    {
        if (AutoCampaignController.HasLiveTarget() || AutoRecoveryController.IsOverwhelmed(ctx))
            return false;
        return TryEngage(ctx, ImmediateRadiusTiles);
    }

    public static bool TryDefendWhileExploring(AutoContext ctx) =>
        TryEngage(ctx, ExplorationRadiusTiles);

    private static bool TryEngage(AutoContext ctx, float radiusTiles)
    {
        NPC target = FindNearestOrdinaryHostile(ctx.Me.Center, radiusTiles);
        if (target is null)
            return false;

        int weaponSlot = AutoCombatController.BestCombatInventoryIndex(ctx, target,
            out AutoWeaponAssessment assessment);
        if (weaponSlot < 0)
            return false;

        Item weapon = ctx.Me.inventory[weaponSlot];
        bool ranged = assessment.Role is AutoWeaponRole.Ranged or AutoWeaponRole.Magic ||
            weapon.shoot > ProjectileID.None || weapon.shootSpeed > 0f ||
            weapon.useAmmo > AmmoID.None || weapon.mana > 0;
        float distance = Vector2.Distance(ctx.Me.Center, target.Center);
        float attackRange = ranged ? 9f * 16f : 3.5f * 16f;

        if (ranged && distance < 3f * 16f)
        {
            int away = target.Center.X >= ctx.Me.Center.X ? -1 : 1;
            ctx.Navigator.Navigate(ctx.Me,
                ctx.Me.Center + new Vector2(away * 6f * 16f, -16f));
        }
        else if (distance > attackRange)
            ctx.Navigator.Navigate(ctx.Me, target.Center);
        else
            ctx.Navigator.Idle(ctx.Me);

        if (ranged || distance <= attackRange)
            ctx.Actuator.UseItemInSlot(ctx.Instance, ctx.Me, weaponSlot, target.Center);
        return true;
    }

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
}
