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

        AutoCapabilities cap = ctx.Capabilities;
        if (cap.LifePercent < 30)
            risk += 50;
        else if (cap.LifePercent < 55)
            risk += 25;
        if (cap.HealingPotionCount <= 0 && IsCombatOrExpedition(plan.Goal))
            risk += 18;
        if (cap.Defense < ExpectedDefense(ctx.State.CampaignStage) && IsCombatOrExpedition(plan.Goal))
            risk += 8 + System.Math.Min(28,
                (ExpectedDefense(ctx.State.CampaignStage) - cap.Defense) / 2);
        if (cap.BestWeaponScore < AutoCampaignController.RequiredWeaponScore(ctx.State.CampaignStage) &&
            IsCombatOrExpedition(plan.Goal))
            risk += 18;
        if (!cap.HasHook && !cap.HasFlightAccessory &&
            plan.Goal is AutoGoalId.ExploreWorld or AutoGoalId.AcquireVitalUpgrade or
                AutoGoalId.PrepareArena or AutoGoalId.UpgradeClassLoadout)
            risk += 12;
        if (cap.FreeBackpackSlots <= 1 && plan.Goal is not AutoGoalId.DepositSurplus)
            risk += 16;
        if ((Main.bloodMoon || Main.eclipse || Main.invasionType > InvasionID.None) &&
            plan.Goal is not AutoGoalId.FightBoss and not AutoGoalId.RiskRecovery)
            risk += 24;
        if (ctx.Me.ZoneUnderworldHeight && !cap.HasFlightAccessory)
            risk += 10;
        risk += System.Math.Min(30, ctx.State.StageFailureCount * 3);
        return risk;
    }

    private static int ExpectedDefense(AutoCampaignStage stage) => stage switch
    {
        AutoCampaignStage.Foundation or AutoCampaignStage.EyeOfCthulhu => 6,
        AutoCampaignStage.EvilBoss or AutoCampaignStage.Skeletron => 12,
        AutoCampaignStage.WallOfFlesh => 20,
        AutoCampaignStage.MechanicalBosses => 32,
        AutoCampaignStage.Plantera => 42,
        AutoCampaignStage.Golem => 48,
        AutoCampaignStage.LunaticCultist or AutoCampaignStage.SolarPillar or
            AutoCampaignStage.VortexPillar or AutoCampaignStage.NebulaPillar or
            AutoCampaignStage.StardustPillar => 54,
        _ => 60
    };

    private static bool IsCombatOrExpedition(AutoGoalId goal) => goal is
        AutoGoalId.GatherGel or AutoGoalId.LootChest or AutoGoalId.ExploreWorld or
        AutoGoalId.AcquireWeaponUpgrade or AutoGoalId.AcquireVitalUpgrade or
        AutoGoalId.ReturnToDeathSite or AutoGoalId.PrepareArena or AutoGoalId.SummonBoss or
        AutoGoalId.FightBoss or AutoGoalId.UpgradeClassLoadout;
}
