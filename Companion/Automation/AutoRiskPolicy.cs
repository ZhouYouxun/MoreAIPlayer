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
    private const int EarlyWeaponCapability = 24;

    public static int BestWeaponCapability(CompanionProfile profile)
    {
        return AutoWeaponEvaluator.BestScore(profile);
    }

    public static bool HasAdequateEarlyWeapon(CompanionProfile profile) =>
        BestWeaponCapability(profile) >= EarlyWeaponCapability;

    public static int WeaponCapability(Item item) => AutoWeaponEvaluator.CombatScore(item);

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
            AutoGoalId.LootChest => 10,
            AutoGoalId.ExploreWorld => 16,
            AutoGoalId.FishOpportunity => 4,
            AutoGoalId.AcquireWeaponUpgrade => 14,
            AutoGoalId.AcquireCombatSupplies => 4,
            AutoGoalId.FightBoss => 90,
            AutoGoalId.AcquireVitalUpgrade => 26,
            AutoGoalId.ReturnToDeathSite => 35,
            AutoGoalId.PrepareArena => 30,
            AutoGoalId.AcquireExplosives => 8,
            AutoGoalId.SummonBoss => 75,
            AutoGoalId.RiskRecovery => 1,
            AutoGoalId.UpgradeClassLoadout => 28,
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
