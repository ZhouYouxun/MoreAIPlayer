using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace MoreAIPlayer.Companion.Automation;

/// <summary>Immutable result from the automation phone's virtual DPS lab. The score is deliberately
/// target-specific: panel DPS is only the starting point, then range, line of sight, projectile
/// speed, penetration, sustain and contact risk decide how much damage is realistically usable.</summary>
public readonly record struct AutoWeaponAssessment(
    int ItemType,
    AutoWeaponRole Role,
    AutoCombatClass CombatClass,
    float TheoreticalDps,
    float EffectiveDps,
    int DecisionScore,
    int Confidence,
    bool AmmoReady,
    bool ManaReady,
    bool IsVanilla,
    string ReasonCode);

public static class AutoWeaponDecisionEngine
{
    public static AutoWeaponAssessment Assess(Player user, Item item, NPC target,
        AutoCampaignStage stage)
    {
        if (item is null || item.IsAir || !CompanionSlotRules.IsRegularWeapon(item))
            return default;

        float theoretical = AutoWeaponEvaluator.TheoreticalDps(user, item);
        float baseCombatScore = AutoWeaponEvaluator.CombatScore(user, item);
        if (theoretical <= 0f || baseCombatScore <= 0f)
            return default;

        bool ammoReady = item.useAmmo <= AmmoID.None || user.HasAmmo(item);
        bool manaReady = item.mana <= 0 || user.statMana >= item.mana ||
            !user.inventory[CompanionProfile.ManaPotionSlot].IsAir;
        float sustain = ammoReady ? 1f : 0.08f;
        if (item.mana > 0)
        {
            float casts = System.Math.Max(0f, user.statManaMax2 / (float)item.mana);
            sustain *= manaReady ? System.Math.Clamp(0.64f + casts * 0.045f, 0.68f, 1f) : 0.18f;
        }

        AutoWeaponRole role = AutoWeaponEvaluator.Role(item);
        float targetFit = 1f;
        float safety = 1f;
        string reason = "PanelDps";
        Projectile projectileSample = null;
        if (item.shoot > ProjectileID.None)
            ContentSamples.ProjectilesByType.TryGetValue(item.shoot, out projectileSample);

        if (target is not null && target.active)
        {
            float distance = Vector2.Distance(user.Center, target.Center);
            float targetSpeed = target.velocity.Length();
            bool projectileWeapon = item.shoot > ProjectileID.None || item.shootSpeed > 0f ||
                item.useAmmo > AmmoID.None;
            bool lineOfSight = Collision.CanHitLine(user.position, user.width, user.height,
                target.position, target.width, target.height);

            if (!projectileWeapon)
            {
                float reach = System.Math.Max(52f,
                    System.Math.Max(item.width, item.height) * System.Math.Max(1f, item.scale) * 2.1f);
                if (distance > reach * 1.35f)
                {
                    targetFit *= 0.16f;
                    reason = "OutOfReach";
                }
                else if (target.noGravity || target.velocity.LengthSquared() > 20f)
                    targetFit *= 0.78f;
                if (target.damage > user.statDefense + 20)
                    safety *= stage >= AutoCampaignStage.MechanicalBosses ? 0.62f : 0.76f;
            }
            else
            {
                float speed = System.Math.Max(item.shootSpeed, projectileSample?.velocity.Length() ?? 0f);
                if (speed > 0f && targetSpeed > speed * 0.72f)
                    targetFit *= 0.72f;
                if (!lineOfSight && projectileSample?.tileCollide != false)
                {
                    targetFit *= 0.20f;
                    reason = "BlockedProjectile";
                }
                else if (!lineOfSight)
                {
                    targetFit *= 1.12f;
                    reason = "IgnoresTerrain";
                }
            }

            int penetration = projectileSample is null ? 1 :
                projectileSample.penetrate < 0 ? 5 : System.Math.Max(1, projectileSample.penetrate);
            if (penetration > 1 && IsMultiTargetEncounter(target))
            {
                targetFit *= 1f + System.Math.Min(4, penetration - 1) * 0.09f;
                reason = "PiercingTarget";
            }
            if (target.defense > 0)
            {
                int damage = System.Math.Max(1, user.GetWeaponDamage(item));
                float afterDefense = System.Math.Max(1f, damage - target.defense * 0.5f);
                targetFit *= System.Math.Clamp(afterDefense / damage, 0.20f, 1f);
            }
        }

        float vanillaTactics = AutoVanillaWeaponStrategy.TacticalMultiplier(item, target, stage);
        bool vanilla = item.ModItem is null;
        int confidence = vanilla ? 92 : 62;
        // A modded weapon remains fully eligible from public Item/projectile data, but its custom
        // AI may hide secondary attacks the generic evaluator cannot prove without live telemetry.
        if (!vanilla && projectileSample is not null)
            confidence += 8;

        float effective = baseCombatScore * sustain * targetFit * safety * vanillaTactics;
        int score = System.Math.Max(1, (int)System.MathF.Round(effective));
        return new AutoWeaponAssessment(item.type, role, ClassOf(item), theoretical, effective,
            score, confidence, ammoReady, manaReady, vanilla, reason);
    }

    private static AutoCombatClass ClassOf(Item item)
    {
        if (item.DamageType == DamageClass.SummonMeleeSpeed)
            return AutoCombatClass.Summoner;
        if (item.mana > 0 || item.DamageType == DamageClass.Magic)
            return AutoCombatClass.Magic;
        if (item.useAmmo > AmmoID.None || item.DamageType == DamageClass.Ranged)
            return AutoCombatClass.Ranged;
        return AutoCombatClass.Melee;
    }

    private static bool IsMultiTargetEncounter(NPC target)
    {
        if (target.realLife >= 0 || target.type is NPCID.EaterofWorldsHead or
            NPCID.EaterofWorldsBody or NPCID.EaterofWorldsTail or NPCID.TheDestroyer or
            NPCID.TheDestroyerBody or NPCID.TheDestroyerTail)
            return true;
        int nearby = 0;
        foreach (NPC npc in Main.ActiveNPCs)
        {
            if (!npc.active || npc.friendly || npc.life <= 0 ||
                Vector2.DistanceSquared(npc.Center, target.Center) > 9f * 16f * 9f * 16f)
                continue;
            if (++nearby >= 3)
                return true;
        }
        return false;
    }
}
