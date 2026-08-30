using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;

namespace MoreAIPlayer.Companion.Automation;

/// <summary>Generic live-boss execution. Progression-specific summoning and arena construction stay
/// in milestone providers, while this layer guarantees that a boss already in the world is never
/// ignored in favour of chopping another tree or opening another chest.</summary>
public static class AutoBossController
{
    private const float RangedPreferredTiles = 10f;
    private const float ClosePreferredTiles = 3.5f;
    private const float PanicDistanceTiles = 2.75f;

    public static bool HasActiveBoss() => AutoCampaignController.HasLiveTarget();

    public static AutoTaskResult Fight(AutoContext ctx)
    {
        NPC target = FindTarget(ctx.Me.Center, ignoreDistance: false,
            AutoCampaignController.TargetNpcType(ctx.State.CampaignStage));
        if (target is null)
            return AutoTaskResult.Succeeded;

        ctx.Me.MinionAttackTargetNPC = target.whoAmI;
        if (AutoCombatPreparationController.TryUseNextOwnedBuff(ctx))
            return AutoTaskResult.Running;

        int weaponSlot = AutoCombatController.BestCombatInventoryIndex(ctx, target,
            out AutoWeaponAssessment assessment);
        if (weaponSlot < 0)
            return AutoTaskResult.AlternativeRequired;

        Item weapon = ctx.Me.inventory[weaponSlot];
        bool ranged = assessment.Role is AutoWeaponRole.Ranged or AutoWeaponRole.Magic ||
            weapon.shoot > ProjectileID.None || weapon.shootSpeed > 0f ||
            weapon.useAmmo > AmmoID.None || weapon.mana > 0;
        float classDistance = ctx.State.PreferredClass switch
        {
            AutoCombatClass.Ranged or AutoCombatClass.Magic or AutoCombatClass.Summoner => 13f,
            AutoCombatClass.Melee => ClosePreferredTiles,
            _ => ranged ? RangedPreferredTiles : ClosePreferredTiles
        };
        float preferred = classDistance * 16f;
        Vector2 offset = target.Center - ctx.Me.Center;
        float distance = offset.Length();
        int away = target.Center.X >= ctx.Me.Center.X ? -1 : 1;

        // Close-range collision and high-speed approaches get an explicit retreat/jump input. This
        // is intentionally simple and deterministic; the ordinary navigator still owns terrain.
        bool bossClosing = target.velocity.X * (ctx.Me.Center.X - target.Center.X) > 0f;
        bool projectileDanger = IncomingProjectile(ctx.Me, out int projectileAway);
        if (projectileDanger)
            away = projectileAway;
        if (TryStageMovement(ctx, target, away, projectileDanger))
        {
            // Stage-specific movement owns this tick; aiming/attack still runs below.
        }
        else if (distance < PanicDistanceTiles * 16f || bossClosing && distance < preferred * 0.75f ||
            projectileDanger)
        {
            ctx.Navigator.Navigate(ctx.Me, ctx.Me.Center + new Vector2(away * 8f * 16f, -48f));
            ctx.Me.controlJump = true;
        }
        else if (distance > preferred * 1.35f)
        {
            ctx.Navigator.Navigate(ctx.Me, target.Center);
        }
        else if (distance < preferred * 0.72f)
        {
            ctx.Navigator.Navigate(ctx.Me, ctx.Me.Center + new Vector2(away * 6f * 16f, 0f));
        }
        else
        {
            // Use the prepared platform length instead of standing still inside a projectile
            // stream. Alternating thirds also stops every death from repeating the same dodge.
            if (ctx.State.ArenaAnchor is Point arena)
            {
                int phase = (int)(Main.GameUpdateCount / 240 % 2);
                float x = (arena.X + (phase == 0 ? -34 : 34)) * 16f;
                float y = (arena.Y - (ctx.State.StageFailureCount % 3) * 11) * 16f;
                ctx.Navigator.Navigate(ctx.Me, new Vector2(x, y));
            }
            else
            {
                int phase = (int)(Main.GameUpdateCount / 120 % 2);
                float flank = (phase == 0 ? -1f : 1f) * preferred;
                ctx.Navigator.Navigate(ctx.Me,
                    target.Center + new Vector2(flank, -4f * 16f));
            }
        }

        if (ctx.Me.velocity.Y > 6f && ctx.Profile.Hook[0] is Item hook && !hook.IsAir)
        {
            ctx.Instance.AimWorld = ctx.Me.Center + new Vector2(away * 9f * 16f, -7f * 16f);
            ctx.Me.controlHook = true;
        }

        // Projectile/magic weapons may attack while repositioning. Contact weapons wait until the
        // target is actually reachable so their animation does not pin the companion in danger.
        if (ranged || distance <= preferred * 1.2f)
            ctx.Actuator.UseItemInSlot(ctx.Instance, ctx.Me, weaponSlot, target.Center);
        return AutoTaskResult.Running;
    }

    /// <summary>Only movement rules whose geometry is fundamentally different from generic
    /// distance keeping live here. They remain deterministic and use ordinary navigation/input.</summary>
    private static bool TryStageMovement(AutoContext ctx, NPC target, int away,
        bool projectileDanger)
    {
        Point? arena = ctx.State.ArenaAnchor;
        if (target.type == NPCID.WallofFlesh)
        {
            int advance = target.velocity.X == 0f
                ? (target.Center.X < Main.maxTilesX * 8f ? 1 : -1)
                : System.Math.Sign(target.velocity.X);
            float y = arena.HasValue ? arena.Value.Y * 16f - 20f : ctx.Me.Center.Y;
            float x = System.Math.Clamp(target.Center.X + advance * 18f * 16f,
                80f * 16f, (Main.maxTilesX - 80f) * 16f);
            ctx.Navigator.Navigate(ctx.Me, new Vector2(x, y));
            if (projectileDanger || System.Math.Abs(target.Center.X - ctx.Me.Center.X) < 7f * 16f)
                ctx.Me.controlJump = true;
            return true;
        }

        if (arena is not Point anchor)
            return false;
        int phase;
        Vector2 destination;
        switch (ctx.State.CampaignStage)
        {
            case AutoCampaignStage.Plantera:
                phase = (int)(Main.GameUpdateCount / 105 % 4);
                destination = new Vector2(
                    (anchor.X + (phase is 0 or 3 ? -24 : 24)) * 16f,
                    (anchor.Y + (phase < 2 ? -10 : 10)) * 16f);
                ctx.Navigator.Navigate(ctx.Me, destination);
                return true;
            case AutoCampaignStage.Golem:
                phase = (int)(Main.GameUpdateCount / 135 % 2);
                destination = new Vector2((anchor.X + (phase == 0 ? -20 : 20)) * 16f,
                    (anchor.Y - 4) * 16f);
                ctx.Navigator.Navigate(ctx.Me, destination);
                if (projectileDanger || target.velocity.Y > 3f)
                    ctx.Me.controlJump = true;
                return true;
            case AutoCampaignStage.MoonLord:
                phase = (int)(Main.GameUpdateCount / 210 % 2);
                destination = new Vector2(
                    (anchor.X + (phase == 0 ? -70 : 70)) * 16f,
                    (anchor.Y - 12) * 16f);
                ctx.Navigator.Navigate(ctx.Me, destination);
                if (projectileDanger)
                    ctx.Me.controlJump = true;
                return true;
            case AutoCampaignStage.MechanicalBosses when target.type is NPCID.TheDestroyer or
                NPCID.TheDestroyerBody or NPCID.TheDestroyerTail:
                phase = (int)(Main.GameUpdateCount / 150 % 2);
                destination = new Vector2((anchor.X + (phase == 0 ? -42 : 42)) * 16f,
                    (anchor.Y - 20) * 16f);
                ctx.Navigator.Navigate(ctx.Me, destination);
                return true;
            default:
                return false;
        }
    }

    public static long RemainingBossLife()
    {
        long total = 0;
        foreach (NPC npc in Main.ActiveNPCs)
            if (AutoCampaignController.IsCampaignTarget(npc))
                total += npc.life;
        return total;
    }

    private static NPC FindTarget(Vector2 center, bool ignoreDistance, int preferredType = NPCID.None)
    {
        NPC best = null;
        NPC shieldedPillar = null;
        NPC invulnerableBoss = null;
        float bestDistance = float.MaxValue;
        foreach (NPC npc in Main.ActiveNPCs)
        {
            if (!AutoCampaignController.IsCampaignTarget(npc))
                continue;
            if (AutoCampaignController.IsPillar(npc.type) && npc.dontTakeDamage)
            {
                if (shieldedPillar is null || npc.type == preferredType)
                    shieldedPillar = npc;
                continue;
            }
            if (npc.dontTakeDamage)
            {
                invulnerableBoss ??= npc;
                continue;
            }
            if (ignoreDistance)
                return npc;
            float distance = Vector2.DistanceSquared(center, npc.Center);
            if (npc.type == preferredType)
                distance -= 1_000_000_000f;
            if (distance >= bestDistance)
                continue;
            bestDistance = distance;
            best = npc;
        }
        if (best is not null)
            return best;
        if (shieldedPillar is not null)
        {
            NPC eventEnemy = FindPillarEnemy(shieldedPillar, center, ignoreDistance);
            return eventEnemy ?? shieldedPillar;
        }
        return invulnerableBoss;
    }

    private static NPC FindPillarEnemy(NPC pillar, Vector2 center, bool ignoreDistance)
    {
        NPC best = null;
        float bestDistance = float.MaxValue;
        foreach (NPC npc in Main.ActiveNPCs)
        {
            if (!npc.active || npc.friendly || npc.townNPC || npc.life <= 0 || npc.dontTakeDamage ||
                npc.damage <= 0 || Vector2.DistanceSquared(npc.Center, pillar.Center) > 140f * 16f * 140f * 16f)
                continue;
            if (ignoreDistance)
                return npc;
            float distance = Vector2.DistanceSquared(center, npc.Center);
            if (distance >= bestDistance)
                continue;
            bestDistance = distance;
            best = npc;
        }
        return best;
    }

    private static bool IncomingProjectile(Player me, out int away)
    {
        away = me.direction;
        float best = 220f * 220f;
        foreach (Projectile projectile in Main.ActiveProjectiles)
        {
            if (!projectile.active || !projectile.hostile || projectile.damage <= 0)
                continue;
            Vector2 toPlayer = me.Center - projectile.Center;
            float distance = toPlayer.LengthSquared();
            if (distance >= best || Vector2.Dot(projectile.velocity, toPlayer) <= 0f)
                continue;
            best = distance;
            away = projectile.Center.X >= me.Center.X ? -1 : 1;
        }
        return best < 220f * 220f;
    }
}
