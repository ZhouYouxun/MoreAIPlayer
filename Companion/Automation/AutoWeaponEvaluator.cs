using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace MoreAIPlayer.Companion.Automation;

public enum AutoWeaponRole : byte
{
    None,
    Close,
    Ranged,
    Magic,
    Whip
}

/// <summary>
/// A deterministic combat-value estimate built from the actual Item and projectile samples. It is
/// intentionally not a hard-coded tier list: vanilla and modded drops are compared by expected
/// damage cadence, crit, reach, penetration, knockback and whether the current player can sustain
/// their ammo/mana cost. The number is a scheduling/loadout score, not a promise of target-dummy DPS.
/// </summary>
public static class AutoWeaponEvaluator
{
    public static int CombatScore(Item item) => CombatScore(null, item);

    public static float TheoreticalDps(Player user, Item item)
    {
        try
        {
            return TheoreticalDpsCore(user, item);
        }
        catch (System.Exception exception)
        {
            CompanionFaultGuard.Report(CompanionFaultGuard.AutomationWeaponEvaluationLane,
                exception);
            return 0f;
        }
    }

    private static float TheoreticalDpsCore(Player user, Item item)
    {
        if (item is null || item.IsAir || !CompanionSlotRules.IsRegularWeapon(item) || item.damage <= 0)
            return 0f;
        int damage = user is null ? item.damage : System.Math.Max(item.damage, user.GetWeaponDamage(item));
        int useTime = item.useTime > 0 ? item.useTime : item.useAnimation;
        int animation = item.useAnimation > 0 ? item.useAnimation : useTime;
        useTime = System.Math.Max(1, useTime + System.Math.Max(0, item.reuseDelay));
        animation = System.Math.Max(1, animation + System.Math.Max(0, item.reuseDelay));
        float usesPerSecond = 60f / useTime;
        if (user is not null)
            usesPerSecond *= System.Math.Max(0.05f, user.GetWeaponAttackSpeed(item));
        if (item.useLimitPerAnimation is int limit && limit > 0)
            usesPerSecond = System.Math.Min(usesPerSecond, limit * 60f / animation);
        int crit = user is null ? item.crit : user.GetWeaponCrit(item);
        return damage * usesPerSecond * (1f + System.Math.Max(0, crit) / 100f);
    }

    public static int CombatScore(Player user, Item item)
    {
        try
        {
            return CombatScoreCore(user, item);
        }
        catch (System.Exception exception)
        {
            // Mod weapons may execute foreign stat hooks from GetWeaponDamage/AttackSpeed/Crit.
            // Reject only that item so one broken hook cannot retire the whole automation player.
            CompanionFaultGuard.Report(CompanionFaultGuard.AutomationWeaponEvaluationLane,
                exception);
            return 0;
        }
    }

    private static int CombatScoreCore(Player user, Item item)
    {
        if (item is null || item.IsAir || !CompanionSlotRules.IsRegularWeapon(item))
            return 0;

        int damage = user is null ? item.damage : System.Math.Max(item.damage, user.GetWeaponDamage(item));
        if (damage <= 0)
            return 0;

        int useTime = item.useTime > 0 ? item.useTime : item.useAnimation;
        int useAnimation = item.useAnimation > 0 ? item.useAnimation : useTime;
        useTime = System.Math.Max(1, useTime + System.Math.Max(0, item.reuseDelay));
        useAnimation = System.Math.Max(1, useAnimation + System.Math.Max(0, item.reuseDelay));

        float usesPerSecond = 60f / useTime;
        if (user is not null)
            usesPerSecond *= System.Math.Max(0.05f, user.GetWeaponAttackSpeed(item));
        if (item.useLimitPerAnimation is int limit && limit > 0)
            usesPerSecond = System.Math.Min(usesPerSecond, limit * 60f / useAnimation);

        int crit = user is null ? item.crit : user.GetWeaponCrit(item);
        float expectedDamage = damage * (1f + System.Math.Max(0, crit) / 100f);
        float score = expectedDamage * usesPerSecond;

        bool projectileWeapon = item.shoot > ProjectileID.None || item.shootSpeed > 0f;
        if (projectileWeapon)
        {
            float rangeBonus = 1.12f + System.Math.Min(0.18f, System.Math.Max(0f, item.shootSpeed) / 100f);
            score *= rangeBonus;

            if (ContentSamples.ProjectilesByType.TryGetValue(item.shoot, out Projectile sample))
            {
                int penetration = sample.penetrate < 0 ? 5 : System.Math.Max(1, sample.penetrate);
                score *= 1f + System.Math.Min(4, penetration - 1) * 0.055f;
                if (!sample.tileCollide)
                    score *= 1.08f;
                if (sample.minion || sample.sentry)
                    return 0; // persistent summons belong in their dedicated upkeep slots
            }
        }
        else
        {
            // Pure contact weapons carry more risk and lose uptime against flying/pass-through
            // targets. Large scale and knockback claw a small part of that back.
            score *= 0.90f + System.Math.Min(0.08f, System.Math.Max(0f, item.scale - 1f) * 0.12f);
        }

        score *= 1f + System.Math.Min(0.10f, System.Math.Max(0f, item.knockBack) * 0.012f);
        if (item.autoReuse)
            score *= 1.04f;

        if (user is not null && item.useAmmo > AmmoID.None && !user.HasAmmo(item))
            score *= 0.22f;
        if (item.mana > 0)
        {
            float casts = user is null ? 6f : System.Math.Max(1f, user.statManaMax2 / (float)item.mana);
            score *= System.Math.Clamp(0.70f + casts * 0.035f, 0.72f, 1f);
        }

        return System.Math.Max(1, (int)System.MathF.Round(score));
    }

    /// <summary>Persistent summons cannot be valued with use animation (the staff is cast once,
    /// while the projectile keeps attacking). Estimate sustained contribution from the owned
    /// staff's real damage and projectile traits on a scale comparable with active-weapon DPS.</summary>
    public static int SummonScore(Player user, Item item)
    {
        try
        {
            return SummonScoreCore(user, item);
        }
        catch (System.Exception exception)
        {
            CompanionFaultGuard.Report(CompanionFaultGuard.AutomationWeaponEvaluationLane,
                exception);
            return 0;
        }
    }

    private static int SummonScoreCore(Player user, Item item)
    {
        if (item is null || item.IsAir ||
            !CompanionWeaponBehaviors.IsMinionStaff(item) &&
            !CompanionWeaponBehaviors.IsSentryStaff(item))
            return 0;

        int damage = user is null ? item.damage : System.Math.Max(item.damage, user.GetWeaponDamage(item));
        if (damage <= 0)
            return 0;
        float cadence = CompanionWeaponBehaviors.IsSentryStaff(item) ? 1.8f : 3.2f;
        float score = damage * cadence * 1.12f;
        if (item.shoot > ProjectileID.None &&
            ContentSamples.ProjectilesByType.TryGetValue(item.shoot, out Projectile sample))
        {
            int penetration = sample.penetrate < 0 ? 4 : System.Math.Max(1, sample.penetrate);
            score *= 1f + System.Math.Min(3, penetration - 1) * 0.06f;
            if (!sample.tileCollide)
                score *= 1.06f;
        }
        return System.Math.Max(1, (int)System.MathF.Round(score));
    }

    public static AutoWeaponRole Role(Item item)
    {
        try
        {
            return RoleCore(item);
        }
        catch (System.Exception exception)
        {
            CompanionFaultGuard.Report(CompanionFaultGuard.AutomationWeaponEvaluationLane,
                exception);
            return AutoWeaponRole.None;
        }
    }

    private static AutoWeaponRole RoleCore(Item item)
    {
        if (item is null || item.IsAir || !CompanionSlotRules.IsRegularWeapon(item))
            return AutoWeaponRole.None;
        if (item.mana > 0)
            return AutoWeaponRole.Magic;
        if (item.useAmmo > AmmoID.None)
            return AutoWeaponRole.Ranged;
        if (item.DamageType == DamageClass.SummonMeleeSpeed)
            return AutoWeaponRole.Whip;
        return item.shoot > ProjectileID.None || item.shootSpeed > 0f
            ? AutoWeaponRole.Ranged
            : AutoWeaponRole.Close;
    }

    public static int BestScore(CompanionProfile profile)
    {
        int best = 0;
        foreach (Item item in profile.Hotbar)
            best = System.Math.Max(best, System.Math.Max(CombatScore(item), SummonScore(null, item)));
        foreach (Item item in profile.Backpack)
            best = System.Math.Max(best, System.Math.Max(CombatScore(item), SummonScore(null, item)));
        return best;
    }

    public static int BestUsableScore(Player user, CompanionProfile profile)
    {
        int best = 0;
        foreach (Item item in profile.Hotbar)
            best = System.Math.Max(best, System.Math.Max(CombatScore(user, item), SummonScore(user, item)));
        foreach (Item item in profile.Backpack)
            best = System.Math.Max(best, System.Math.Max(CombatScore(user, item), SummonScore(user, item)));
        return best;
    }

    public static int BestRegularScore(CompanionProfile profile)
    {
        int best = 0;
        foreach (Item item in profile.Hotbar)
            best = System.Math.Max(best, CombatScore(item));
        foreach (Item item in profile.Backpack)
            best = System.Math.Max(best, CombatScore(item));
        return best;
    }

    /// <summary>Only clearly dominated weapons are storage candidates. Favorited items and anything
    /// close enough to the best score remain protected, leaving room for situational sidegrades.</summary>
    public static bool IsObsolete(CompanionProfile profile, Item item)
    {
        if (item is null || item.IsAir || item.favorited || !CompanionSlotRules.IsRegularWeapon(item))
            return false;
        int score = CombatScore(item);
        // Summon contribution is scored for class choice, but must not make the last usable active
        // weapon look obsolete: minions supplement the attack loop rather than replacing its aim.
        int best = BestRegularScore(profile);
        return best >= 24 && score * 100 < best * 58;
    }
}
