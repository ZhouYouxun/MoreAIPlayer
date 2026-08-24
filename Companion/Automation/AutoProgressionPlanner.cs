using System.Collections.Generic;
using Terraria;
using Terraria.ID;

namespace MoreAIPlayer.Companion.Automation;

/// <summary>
/// Chooses the automation companion's next goal from its current capabilities and what it has
/// already built (§5, §11 stage A/B). A deterministic hard-gate ladder competes with live chest and
/// bounded fishing opportunities, so equivalent lucky finds can retire redundant gear routes. It
/// always leaves the companion with legal work, continued exploration, or an honest blocked reason
/// — never a silent stall.
/// </summary>
public static class AutoProgressionPlanner
{
    private const int WoodForBench = 12;
    private const int WoodStock = 50;
    private const int WoodForFurnace = 6;
    private const int StoneForFurnace = 25;
    private const int StoneStock = 70;
    private const int TorchesForFurnace = 3;
    private const int OreForBars = 15;
    private const int OreStock = 30;
    private const int BarsForAnvil = 5;

    private static readonly int[] BarItems =
    [
        ItemID.CopperBar, ItemID.TinBar, ItemID.IronBar, ItemID.LeadBar,
        ItemID.SilverBar, ItemID.TungstenBar, ItemID.GoldBar, ItemID.PlatinumBar
    ];

    public static AutoPlan Plan(AutoContext ctx)
    {
        AutoPlan plan = PlanCore(ctx);

        // A gather/mine goal cannot make progress with a full backpack — the old code returned an
        // endless RetryLater here. Store surplus first instead (design §4.2/§4.4.1). Crafting and
        // building are left alone: they consume inventory rather than fill it.
        if (!AutoInventory.HasBackpackSpace(ctx.Profile))
        {
            bool stationNeedsCrafting = plan.Goal == AutoGoalId.CraftStation &&
                AutoInventory.FindInventoryIndex(ctx.Me, item => item.type == plan.ItemType) < 0;
            if (IsGatherGoal(plan.Goal) || plan.Goal is AutoGoalId.CraftGear or AutoGoalId.CraftConsumable ||
                stationNeedsCrafting)
            {
                return AutoPlan.Simple(AutoGoalId.DepositSurplus, AutoPlanKind.Support, 2);
            }
        }

        // A repeatedly failing route is not allowed to monopolise the whole run. Keep its hard
        // objective, cool down this concrete plan, and spend the interval on useful low-risk
        // preparation from the same early stage. Once the cooldown expires the hard gate wins again.
        if (ctx.TaskRuntime.IsDeferred(plan) && TryChooseSupportPlan(ctx, plan, out AutoPlan support))
            return support;

        return plan;
    }

    private static bool TryChooseSupportPlan(AutoContext ctx, AutoPlan deferred, out AutoPlan chosen)
    {
        List<(AutoPlan Plan, int Utility)> candidates = [];
        AutoCapabilities cap = ctx.Capabilities;

        if (!AutoInventory.HasBackpackSpace(ctx.Profile))
        {
            chosen = AutoPlan.Simple(AutoGoalId.DepositSurplus, AutoPlanKind.Support, 2);
            return !ctx.TaskRuntime.IsDeferred(chosen);
        }

        if (deferred.Goal != AutoGoalId.GatherWood && cap.Wood < WoodStock)
            candidates.Add((AutoPlan.Gather(AutoGoalId.GatherWood, WoodStock,
                AutoPlanKind.Support, 2), 70 + WoodStock - cap.Wood));

        if (deferred.Goal != AutoGoalId.GatherStone && cap.Stone < StoneForFurnace)
            candidates.Add((AutoPlan.Gather(AutoGoalId.GatherStone, StoneForFurnace,
                AutoPlanKind.Support, 2), 66 + StoneForFurnace - cap.Stone));

        if (cap.Torches < TorchesForFurnace && deferred.Goal != AutoGoalId.GatherGel)
        {
            AutoPlan torchPlan = cap.Gel > 0 && cap.Wood > 0
                ? AutoPlan.Product(ItemID.Torch, AutoPlanKind.Support, 2)
                : AutoPlan.Simple(AutoGoalId.GatherGel, AutoPlanKind.Support, 2);
            candidates.Add((torchPlan, 62 + (TorchesForFurnace - cap.Torches) * 3));
        }

        if (deferred.Goal != AutoGoalId.MineIronTierOre && cap.IronLeadOre < OreForBars)
            candidates.Add((AutoPlan.Gather(AutoGoalId.MineIronTierOre, OreForBars,
                AutoPlanKind.Support, 2), 55 + OreForBars - cap.IronLeadOre));

        int bestScore = int.MinValue;
        chosen = default;
        foreach ((AutoPlan candidate, int utility) in candidates)
        {
            if (ctx.TaskRuntime.IsDeferred(candidate))
                continue;
            int score = utility - AutoRiskPolicy.EstimateTaskRisk(ctx, candidate);
            if (score <= bestScore)
                continue;
            bestScore = score;
            chosen = candidate;
        }
        return bestScore != int.MinValue;
    }

    private static bool IsGatherGoal(AutoGoalId goal) => goal is
        AutoGoalId.GatherWood or AutoGoalId.GatherStone or AutoGoalId.GatherGel or
        AutoGoalId.MineOre or AutoGoalId.MineIronTierOre or AutoGoalId.LootChest or
        AutoGoalId.ExploreWorld or AutoGoalId.FishOpportunity or AutoGoalId.AcquireVitalUpgrade or
        AutoGoalId.PrepareArena or AutoGoalId.AcquireExplosives or AutoGoalId.UpgradeClassLoadout or
        AutoGoalId.AcquireWeaponUpgrade or AutoGoalId.AcquireCombatSupplies;

    private static AutoPlan PlanCore(AutoContext ctx)
    {
        AutoCapabilities cap = ctx.Capabilities;
        AutoCompanionState st = ctx.State;

        if (AutoBossController.HasActiveBoss())
            return AutoPlan.Simple(AutoGoalId.FightBoss, AutoPlanKind.HardGate, 5);

        if (st.HasDeathReturnTarget)
            return AutoPlan.Simple(AutoGoalId.ReturnToDeathSite, AutoPlanKind.HardGate, 5);

        if (AutoRecoveryController.IsOverwhelmed(ctx))
            return AutoPlan.Simple(AutoGoalId.RiskRecovery, AutoPlanKind.Support, 4);

        // A nearby unlocked chest is an opportunity, not a prescribed detour. Its live contents
        // must be valuable enough relative to the current loadout; once looted, the ordinary
        // capability checks below automatically skip redundant crafting/expeditions.
        if (AutoLootController.HasWorthwhileNearbyChest(ctx, out int chestUtility) && chestUtility >= 24 &&
            (st.CampaignStage == AutoCampaignStage.Foundation || AutoCampaignController.NeedsLoadoutWork(ctx)))
            return AutoPlan.Simple(AutoGoalId.LootChest, AutoPlanKind.Optional, 2);
        if (ctx.Fishing.HasOpportunity(ctx) && st.CampaignStage == AutoCampaignStage.Foundation)
            return AutoPlan.Simple(AutoGoalId.FishOpportunity, AutoPlanKind.Optional, 2);

        // 1. Work Bench — the first station, craftable anywhere from wood.
        if (!StationStanding(st.WorkBench, TileID.WorkBenches))
        {
            return cap.Wood >= WoodForBench
                ? AutoPlan.Station(ItemID.WorkBench, TileID.WorkBenches)
                : AutoPlan.Gather(AutoGoalId.GatherWood, WoodForBench);
        }

        // 2. Furnace — needs stone, wood and torches. Torches come from pots (non-combat).
        if (!StationStanding(st.Furnace, TileID.Furnaces))
        {
            if (cap.Torches < TorchesForFurnace)
            {
                if (cap.Gel > 0 && cap.Wood > 0)
                    return AutoPlan.Product(ItemID.Torch);
                return AutoPlan.Simple(AutoGoalId.GatherGel);
            }

            if (cap.Stone < StoneForFurnace)
                return AutoPlan.Gather(AutoGoalId.GatherStone, StoneStock);
            if (cap.Wood < WoodForFurnace)
                return AutoPlan.Gather(AutoGoalId.GatherWood, WoodStock);
            return AutoPlan.Station(ItemID.Furnace, TileID.Furnaces);
        }

        // 3. Ore → bars → first Anvil.
        if (!StationStanding(st.Anvil, TileID.Anvils))
        {
            if (cap.IronLeadBars >= BarsForAnvil)
            {
                int anvilItem = AutoInventory.Count(ctx.Profile, ItemID.IronBar) >= BarsForAnvil
                    ? ItemID.IronAnvil : ItemID.LeadAnvil;
                return AutoPlan.Station(anvilItem, TileID.Anvils);
            }

            if (cap.IronLeadOre < OreForBars)
                return AutoPlan.Gather(AutoGoalId.MineIronTierOre, OreForBars);
            return AutoPlan.Simple(AutoGoalId.CraftGear, AutoPlanKind.Capability); // smelts ore into bars
        }

        // 4. Turn real ore into a basic independent loadout. The gear controller equips only items
        // it actually crafted and falls back to more mining when the available bar mixture cannot
        // satisfy the next recipe.
        if (!AutoGearController.HasBasicMetalLoadout(ctx.Profile))
            return AutoPlan.Simple(AutoGoalId.CraftGear, AutoPlanKind.Capability);

        // 5. Build a resumable starter room with storage. A generous stock avoids repeatedly
        // abandoning the template halfway through for two more pieces of wood.
        if (!st.ShelterCompleted || st.HomeAnchor is not Microsoft.Xna.Framework.Point home ||
            !AutoShelterController.IsShelterComplete(home))
        {
            if (cap.Wood < AutoShelterController.WoodReserve)
                return AutoPlan.Gather(AutoGoalId.GatherWood, AutoShelterController.WoodReserve);
            return AutoPlan.Simple(AutoGoalId.BuildShelter);
        }

        AutoCampaignController.RefreshStage(ctx);
        if (st.CampaignStage == AutoCampaignStage.Completed)
            return AutoPlan.Idle(AutoTaskResult.Succeeded);

        if (AutoVitalController.NeedsUpgrade(ctx))
            return AutoPlan.Simple(AutoGoalId.AcquireVitalUpgrade, AutoPlanKind.HardGate, 6);

        if (AutoCampaignGearController.NeedsMiningUpgrade(ctx))
            return AutoPlan.Simple(AutoGoalId.UpgradeClassLoadout, AutoPlanKind.Capability, 8);

        if (AutoCombatSupplyController.NeedsRestock(ctx))
            return AutoPlan.Simple(AutoGoalId.AcquireCombatSupplies, AutoPlanKind.Capability, 4);

        // Preparation is bounded. Three exploration segments look for legal upgrades and lucky
        // drops; then the real arena/attempt proceeds. A wipe changes the strategy variant and
        // opens another preparation window instead of farming forever for a prescribed item.
        if (AutoCampaignController.NeedsLoadoutWork(ctx) &&
            (st.ExplorationSegments + st.StageFailureCount * 3) % 6 < 3)
        {
            AutoWeaponRouteKind route = AutoVanillaWeaponStrategy.ChooseUpgradeRoute(ctx);
            if (route != AutoWeaponRouteKind.None)
                return AutoPlan.WeaponRoute(route);
        }

        if (cap.Wood < AutoArenaController.WoodReserve)
            return AutoPlan.Gather(AutoGoalId.GatherWood, AutoArenaController.WoodReserve,
                AutoPlanKind.Support, 4);

        bool undergroundArena = st.CampaignStage is AutoCampaignStage.WallOfFlesh or
            AutoCampaignStage.Plantera or AutoCampaignStage.Golem;
        if (undergroundArena && AutoExplosiveController.Count(ctx.Profile) < AutoExplosiveController.DesiredStock &&
            AutoExplosiveController.DemolitionistExists(out _) &&
            AutoExplosiveController.CanAffordBomb(ctx.Profile))
            return AutoPlan.Simple(AutoGoalId.AcquireExplosives, AutoPlanKind.Support, 3);

        if (!AutoArenaController.IsPrepared(st))
            return AutoPlan.Simple(AutoGoalId.PrepareArena, AutoPlanKind.HardGate, 8);

        // Night-only bosses use the waiting window productively. The summoner never changes the
        // world's clock and never starts a boss that would instantly despawn in daylight.
        if (AutoCampaignController.RequiresNight(st.CampaignStage) && Main.dayTime)
            return AutoPlan.Simple(AutoGoalId.ExploreWorld, AutoPlanKind.Support, 3);

        return AutoPlan.Simple(AutoGoalId.SummonBoss, AutoPlanKind.HardGate, 8);
    }

    private static bool StationStanding(System.Nullable<Microsoft.Xna.Framework.Point> recorded, int tileType)
    {
        if (recorded is not Microsoft.Xna.Framework.Point p)
            return false;
        return WorldGen.InWorld(p.X, p.Y, 2) && Main.tile[p.X, p.Y].HasTile && Main.tile[p.X, p.Y].TileType == tileType;
    }

    private static int CountBars(CompanionProfile profile)
    {
        int total = 0;
        foreach (int type in BarItems)
            total += AutoInventory.Count(profile, type);
        return total;
    }

    public static int[] SmeltableBarItems => BarItems;
}
