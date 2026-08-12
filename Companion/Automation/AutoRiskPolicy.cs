using Terraria;
using Terraria.ID;

namespace MoreAIPlayer.Companion.Automation;

/// <summary>
/// Shared capability and risk policy. It deliberately evaluates what the companion can do rather
/// than asking whether it owns one prescribed item. A lucky strong drop therefore satisfies the
/// weapon requirement and removes the value of a redundant, dangerous weapon expedition.
/// </summary>
public static class AutoRiskPolicy
{
    private const int EarlyWeaponCapability = 14;

    public static int BestWeaponCapability(CompanionProfile profile)
    {
        int best = 0;
        foreach (Item item in profile.Hotbar)
            best = System.Math.Max(best, WeaponCapability(item));
        foreach (Item item in profile.Backpack)
            best = System.Math.Max(best, WeaponCapability(item));
        return best;
    }

    public static bool HasAdequateEarlyWeapon(CompanionProfile profile) =>
        BestWeaponCapability(profile) >= EarlyWeaponCapability;

    /// <summary>Small, stable capability estimate for early scheduling. This is not advertised as
    /// exact DPS: it only needs to distinguish no weapon, a usable early weapon and a lucky strong
    /// weapon without maintaining an ItemID whitelist.</summary>
    public static int WeaponCapability(Item item)
    {
        if (item is null || item.IsAir || item.damage <= 0 || item.pick > 0 || item.axe > 0 || item.hammer > 0)
            return 0;

        int useTime = System.Math.Max(8, item.useTime > 0 ? item.useTime : item.useAnimation);
        int score = item.damage * 60 / useTime;
        if (item.shoot > ProjectileID.None || item.shootSpeed > 0f)
            score += System.Math.Max(2, item.damage / 3);
        if (item.autoReuse)
            score += 2;
        return score;
    }

    /// <summary>Relative danger used only to choose between already-useful support plans. A hard
    /// gate never disappears because this number is high; it is cooled down and revisited.</summary>
    public static int EstimateTaskRisk(AutoContext ctx, AutoPlan plan)
    {
        int risk = plan.Goal switch
        {
            AutoGoalId.GatherWood => 5,
            AutoGoalId.GatherStone => 12,
            AutoGoalId.GatherGel => 35,
            AutoGoalId.MineOre or AutoGoalId.MineIronTierOre => 22,
            AutoGoalId.CraftStation or AutoGoalId.CraftConsumable or AutoGoalId.DepositSurplus => 2,
            AutoGoalId.BuildShelter => 8,
            AutoGoalId.CraftGear => 4,
            _ => 15
        };

        if (ctx.Me.statLifeMax2 > 0 && ctx.Me.statLife * 100 / ctx.Me.statLifeMax2 < 45)
            risk += 25;
        if (ctx.Me.statDefense < 4 && plan.Goal is AutoGoalId.GatherGel or AutoGoalId.MineOre or AutoGoalId.MineIronTierOre)
            risk += 12;
        if (!HasAdequateEarlyWeapon(ctx.Profile) && plan.Goal == AutoGoalId.GatherGel)
            risk += 20;
        return risk;
    }
}
