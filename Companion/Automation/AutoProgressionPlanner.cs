using System;
using Terraria;
using Terraria.ID;

namespace MoreAIPlayer.Companion.Automation;

/// <summary>
/// Builds a parallel portfolio of currently useful routes. Campaign flags still describe real
/// world gates, but they no longer prescribe one walkthrough: gathering, exploration, crafting,
/// survival and equipment providers compete continuously, and an equivalent lucky result removes
/// redundant providers on the next tick.
/// </summary>
public static class AutoProgressionPlanner
{
    private const int WoodForBench = 12;
    private const int WoodForFurnace = 6;
    private const int StoneForFurnace = 25;
    private const int StoneStock = 70;
    private const int TorchesForFurnace = 3;
    private const int OreForBars = 15;
    private const int OreStock = 30;
    private const int BarsForAnvil = 5;

    private static readonly AutoPlanPortfolio Portfolio = new();

    private static readonly int[] BarItems =
    [
        ItemID.CopperBar, ItemID.TinBar, ItemID.IronBar, ItemID.LeadBar,
        ItemID.SilverBar, ItemID.TungstenBar, ItemID.GoldBar, ItemID.PlatinumBar
    ];

    private static readonly AutoWeaponRouteKind[] WeaponRoutes =
    [
        AutoWeaponRouteKind.CraftStageWeapon,
        AutoWeaponRouteKind.WorldChest,
        AutoWeaponRouteKind.EvilOrbGun,
        AutoWeaponRouteKind.Fishing,
        AutoWeaponRouteKind.HuntDrops
    ];

    public static AutoPlan Plan(AutoContext ctx)
    {
        bool needsPersonalFoundation = AutoCampaignController.NeedsPersonalFoundation(ctx);
        if (ctx.State.CampaignStage == AutoCampaignStage.Completed && !needsPersonalFoundation)
            return AutoPlan.Idle(AutoTaskResult.Succeeded);

        Portfolio.Clear();
        AddEmergencyCandidates(ctx);
        AddInventoryCandidate(ctx);
        AddLiveOpportunities(ctx);

        // Personal readiness and world progression are parallel facts. In an established world a
        // newly created automation player may still need a bench, tools and shelter, but that must
        // not erase available late-world chests, resources or current campaign gates.
        if (needsPersonalFoundation)
            AddFoundationCandidates(ctx);
        if (ctx.State.CampaignStage is not AutoCampaignStage.Foundation and not AutoCampaignStage.Completed)
            AddCampaignCandidates(ctx);

        // There is always productive frontier work. It is intentionally low utility, so any
        // concrete need or live opportunity wins, but the player never reaches a silent Idle.
        Portfolio.Add(AutoPlan.Explore(System.Math.Max(0, ctx.State.StrategyVariant)),
            AutoNeed.Exploration | AutoNeed.Opportunity, 28);

        if (Portfolio.TryChoose(ctx, out AutoPlan chosen))
            return FinalizeSelection(ctx, chosen);

        // Every known provider is cooling down. Start a new exploration generation outside the
        // rejected route instead of waiting at the failure point or pacing beside a workstation.
        ctx.State.StrategyVariant++;
        ctx.State.ExplorationTargetTileX = -1;
        AutoPlan frontier = AutoPlan.Explore(System.Math.Max(0, ctx.State.StrategyVariant),
            AutoPlanKind.Support, 2);
        Portfolio.Add(frontier, AutoNeed.Exploration | AutoNeed.Opportunity, 30);
        return FinalizeSelection(ctx,
            Portfolio.TryChoose(ctx, out chosen) ? chosen : frontier);
    }

    private static AutoPlan FinalizeSelection(AutoContext ctx, AutoPlan selected)
    {
        AutoPlan final = EnsureOutputCapacity(ctx, selected);
        bool capacityOverride = final.StableKey != selected.StableKey;
        ctx.TaskRuntime.RecordPortfolioDecision(final.StableKey, Portfolio.Count,
            Portfolio.LastAvailableCount,
            capacityOverride ? 0 : Portfolio.LastSelectedScore,
            capacityOverride ? AutoNeed.InventorySpace : Portfolio.LastSelectedCoverage);
        return final;
    }

    private static void AddEmergencyCandidates(AutoContext ctx)
    {
        if (AutoBossController.HasActiveBoss())
        {
            Portfolio.Add(AutoPlan.Simple(AutoGoalId.FightBoss, AutoPlanKind.HardGate, 5),
                AutoNeed.ImmediateSafety | AutoNeed.Encounter | AutoNeed.WorldGate, 520,
                completionBonus: 80);
        }

        if (ctx.State.HasDeathReturnTarget)
        {
            Portfolio.Add(AutoPlan.Simple(AutoGoalId.ReturnToDeathSite,
                    AutoPlanKind.HardGate, 5),
                AutoNeed.ImmediateSafety | AutoNeed.Recovery, 460);
        }

        if (AutoRecoveryController.IsOverwhelmed(ctx))
        {
            Portfolio.Add(AutoPlan.Simple(AutoGoalId.RiskRecovery,
                    AutoPlanKind.Support, 4),
                AutoNeed.ImmediateSafety | AutoNeed.Recovery, 430);
        }
    }

    private static void AddInventoryCandidate(AutoContext ctx)
    {
        if (AutoInventory.HasBackpackSpace(ctx.Profile))
            return;
        Portfolio.Add(AutoPlan.Simple(AutoGoalId.DepositSurplus, AutoPlanKind.Support, 3),
            AutoNeed.InventorySpace, 40, completionBonus: 25);
    }

    private static AutoPlan EnsureOutputCapacity(AutoContext ctx, AutoPlan selected)
    {
        if (AutoInventory.HasBackpackSpace(ctx.Profile) || !NeedsDepositBefore(ctx, selected))
            return selected;
        AutoPlan deposit = AutoPlan.Simple(AutoGoalId.DepositSurplus,
            AutoPlanKind.Support, 3);
        return ctx.TaskRuntime.IsPlanCoolingDown(deposit) ? selected : deposit;
    }

    private static bool NeedsDepositBefore(AutoContext ctx, AutoPlan plan) => plan.Goal switch
    {
        AutoGoalId.GatherWood => !AutoInventory.HasBackpackCapacityFor(ctx.Profile, ItemID.Wood),
        AutoGoalId.GatherStone => !AutoInventory.HasBackpackCapacityFor(ctx.Profile, ItemID.StoneBlock),
        AutoGoalId.GatherGel => !AutoInventory.HasBackpackCapacityFor(ctx.Profile, ItemID.Torch, ItemID.Gel),
        AutoGoalId.MineOre => !AutoInventory.HasBackpackCapacityFor(ctx.Profile,
            AutoResourceController.BasicOreItems),
        AutoGoalId.MineIronTierOre => !AutoInventory.HasBackpackCapacityFor(ctx.Profile,
            ItemID.IronOre, ItemID.LeadOre),
        AutoGoalId.AcquireVitalUpgrade => !AutoInventory.HasBackpackCapacityFor(ctx.Profile,
            ctx.State.LifeCrystals < 15 ? ItemID.LifeCrystal : ItemID.LifeFruit),
        AutoGoalId.CraftStation => AutoInventory.FindInventoryIndex(ctx.Me,
            item => item.type == plan.ItemType) < 0 &&
            !AutoInventory.HasBackpackCapacityFor(ctx.Profile, plan.ItemType),
        AutoGoalId.CraftGear => true,
        AutoGoalId.CraftConsumable => !AutoInventory.HasBackpackCapacityFor(ctx.Profile,
            plan.ItemType),
        AutoGoalId.BuildShelter => AutoShelterController.NeedsDepositForNextSupply(ctx),
        AutoGoalId.PrepareArena => AutoArenaController.NeedsDepositForNextSupply(ctx),
        AutoGoalId.AcquireExplosives => !AutoInventory.HasBackpackCapacityFor(ctx.Profile,
            ItemID.Bomb, ItemID.Dynamite, ItemID.StickyBomb, ItemID.StickyDynamite),
        AutoGoalId.UpgradeClassLoadout => true,
        AutoGoalId.AcquireCombatSupplies => !AutoInventory.HasBackpackCapacityFor(ctx.Profile,
            ItemID.WoodenArrow, ItemID.MusketBall, ItemID.RocketI),
        AutoGoalId.LootChest or AutoGoalId.ExploreWorld or AutoGoalId.FishOpportunity or
            AutoGoalId.AcquireWeaponUpgrade => true,
        _ => false
    };

    private static void AddLiveOpportunities(AutoContext ctx)
    {
        bool bootstrap = AutoCampaignController.NeedsPersonalFoundation(ctx);
        if (AutoLootController.HasWorthwhileNearbyChest(ctx, out int chestUtility) &&
            (bootstrap || chestUtility >= 12))
        {
            Portfolio.Add(AutoPlan.Simple(AutoGoalId.LootChest, AutoPlanKind.Optional, 3),
                AutoNeed.Opportunity | AutoNeed.Equipment | AutoNeed.WeaponPower |
                AutoNeed.Vitality | AutoNeed.Materials,
                bootstrap ? 188 : 105,
                System.Math.Min(180, chestUtility * (bootstrap ? 4 : 3)),
                bootstrap ? 60 : 25);
        }

        if (ctx.Fishing.HasOpportunity(ctx))
        {
            Portfolio.Add(AutoPlan.Simple(AutoGoalId.FishOpportunity,
                    AutoPlanKind.Optional, 3),
                AutoNeed.Opportunity | AutoNeed.Equipment | AutoNeed.WeaponPower |
                AutoNeed.Materials,
                66, opportunityBonus: 45);
        }
    }

    private static void AddFoundationCandidates(AutoContext ctx)
    {
        AutoCapabilities cap = ctx.Capabilities;
        AutoCompanionState state = ctx.State;
        AutoResourceBudget.WoodBudget woodBudget = AutoResourceBudget.Evaluate(ctx);
        bool bench = StationStanding(state.WorkBench, TileID.WorkBenches);
        bool furnace = StationStanding(state.Furnace, TileID.Furnaces);
        bool anvil = StationStanding(state.Anvil, TileID.Anvils);
        bool basicLoadout = AutoGearController.HasBasicMetalLoadout(ctx.Profile);

        int woodTarget = FoundationWoodTarget(ctx, woodBudget, bench, furnace, basicLoadout);
        if (cap.Wood < woodTarget)
        {
            Portfolio.Add(AutoPlan.Gather(AutoGoalId.GatherWood, woodTarget,
                    AutoPlanKind.Capability, 5),
                AutoNeed.Materials | AutoNeed.CraftingAccess | AutoNeed.Housing, 82);
        }

        if (AutoResourceController.CountBasicOre(ctx.Profile) < OreStock)
        {
            Portfolio.Add(AutoPlan.Gather(AutoGoalId.MineOre, OreStock,
                    AutoPlanKind.Capability, 5),
                AutoNeed.Materials | AutoNeed.Equipment, 92);
        }

        if (!bench)
        {
            if (cap.Wood >= WoodForBench)
            {
                Portfolio.Add(AutoPlan.Station(ItemID.WorkBench, TileID.WorkBenches,
                        AutoPlanKind.Capability, 4),
                    AutoNeed.CraftingAccess | AutoNeed.Housing, 155,
                    completionBonus: 75);
            }
            else
            {
                Portfolio.Add(AutoPlan.Gather(AutoGoalId.GatherWood,
                        System.Math.Max(woodTarget, WoodForBench), AutoPlanKind.Capability, 4),
                    AutoNeed.Materials | AutoNeed.CraftingAccess, 110,
                    completionBonus: 30);
            }
        }

        if (!furnace)
        {
            if (cap.Stone < StoneForFurnace)
            {
                Portfolio.Add(AutoPlan.Gather(AutoGoalId.GatherStone, StoneStock,
                        AutoPlanKind.Capability, 4),
                    AutoNeed.Materials | AutoNeed.CraftingAccess, 88);
            }
            if (cap.Torches < TorchesForFurnace)
            {
                if (cap.Gel > 0 && cap.Wood > 0)
                {
                    Portfolio.Add(AutoPlan.Product(ItemID.Torch, TorchesForFurnace,
                            AutoPlanKind.Capability, 3),
                        AutoNeed.Materials | AutoNeed.CraftingAccess, 132,
                        completionBonus: 55);
                }
                else
                {
                    Portfolio.Add(AutoPlan.Explore(System.Math.Max(0, ctx.State.StrategyVariant),
                            AutoPlanKind.Support, 3),
                        AutoNeed.Opportunity | AutoNeed.Materials | AutoNeed.Exploration, 58);
                }
            }
            if (bench && cap.Stone >= StoneForFurnace &&
                cap.Torches >= TorchesForFurnace && cap.Wood >= WoodForFurnace)
            {
                Portfolio.Add(AutoPlan.Station(ItemID.Furnace, TileID.Furnaces,
                        AutoPlanKind.Capability, 4),
                    AutoNeed.CraftingAccess | AutoNeed.Equipment, 158,
                    completionBonus: 80);
            }
        }

        if (furnace && !anvil)
        {
            if (cap.IronLeadBars >= BarsForAnvil)
            {
                int anvilItem = AutoInventory.Count(ctx.Profile, ItemID.IronBar) >= BarsForAnvil
                    ? ItemID.IronAnvil : ItemID.LeadAnvil;
                Portfolio.Add(AutoPlan.Station(anvilItem, TileID.Anvils,
                        AutoPlanKind.Capability, 4),
                    AutoNeed.CraftingAccess | AutoNeed.Equipment, 160,
                    completionBonus: 85);
            }
            else if (cap.IronLeadOre < OreForBars)
            {
                Portfolio.Add(AutoPlan.Gather(AutoGoalId.MineIronTierOre, OreForBars,
                        AutoPlanKind.Capability, 4),
                    AutoNeed.Materials | AutoNeed.CraftingAccess | AutoNeed.Equipment, 104);
            }
            else
            {
                Portfolio.Add(AutoPlan.Simple(AutoGoalId.CraftGear,
                        AutoPlanKind.Capability, 4),
                    AutoNeed.CraftingAccess | AutoNeed.Equipment, 138,
                    completionBonus: 55);
            }
        }

        if (furnace && anvil && !basicLoadout)
        {
            Portfolio.Add(AutoPlan.Simple(AutoGoalId.CraftGear,
                    AutoPlanKind.Capability, 6),
                AutoNeed.Equipment | AutoNeed.WeaponPower | AutoNeed.Materials, 132,
                completionBonus: 35);
        }

        if (!state.ShelterCompleted || state.HomeAnchor is not Microsoft.Xna.Framework.Point home ||
            !AutoShelterController.IsShelterComplete(home, state))
        {
            if (bench && cap.Wood >= woodBudget.RawWoodTarget)
            {
                Portfolio.Add(AutoPlan.Simple(AutoGoalId.BuildShelter,
                        AutoPlanKind.Capability, 6),
                    AutoNeed.Housing | AutoNeed.CraftingAccess, 118,
                    completionBonus: 40);
            }
            else if (basicLoadout && cap.Wood < woodBudget.RawWoodTarget)
            {
                Portfolio.Add(AutoPlan.Gather(AutoGoalId.GatherWood,
                        woodBudget.RawWoodTarget, AutoPlanKind.Capability, 5),
                    AutoNeed.Materials | AutoNeed.Housing, 96);
            }
        }
    }

    private static void AddCampaignCandidates(AutoContext ctx)
    {
        AutoCompanionState state = ctx.State;
        AutoResourceBudget.WoodBudget woodBudget = AutoResourceBudget.Evaluate(ctx);
        int woodRefillTrigger = System.Math.Max(50, woodBudget.RawWoodTarget * 4 / 5);
        if (ctx.Capabilities.Wood < woodRefillTrigger)
        {
            Portfolio.Add(AutoPlan.Gather(AutoGoalId.GatherWood, woodBudget.RawWoodTarget,
                    AutoPlanKind.Support, 4),
                AutoNeed.Materials | AutoNeed.Housing | AutoNeed.Arena, 68);
        }

        bool needsVitality = AutoVitalController.NeedsUpgrade(ctx);
        if (needsVitality)
        {
            Portfolio.Add(AutoPlan.Simple(AutoGoalId.AcquireVitalUpgrade,
                    AutoPlanKind.Capability, 6),
                AutoNeed.Vitality | AutoNeed.Equipment, 132);
        }

        bool needsMining = AutoCampaignGearController.NeedsMiningUpgrade(ctx);
        if (needsMining)
        {
            Portfolio.Add(AutoPlan.Simple(AutoGoalId.UpgradeClassLoadout,
                    AutoPlanKind.Capability, 8),
                AutoNeed.Equipment | AutoNeed.WorldGate | AutoNeed.WeaponPower, 122);
        }

        bool needsSupplies = AutoCombatSupplyController.NeedsRestock(ctx);
        if (needsSupplies)
        {
            Portfolio.Add(AutoPlan.Simple(AutoGoalId.AcquireCombatSupplies,
                    AutoPlanKind.Capability, 5),
                AutoNeed.Ammunition | AutoNeed.WeaponPower, 114,
                completionBonus: 25);
        }

        bool loadoutWork = AutoCampaignController.NeedsLoadoutWork(ctx);
        bool preparationWindow = loadoutWork &&
            (state.ExplorationSegments + state.StageFailureCount * 3) % 6 < 3;
        if (loadoutWork)
            AddWeaponRoutes(ctx, preparationWindow);

        bool undergroundArena = state.CampaignStage is AutoCampaignStage.WallOfFlesh or
            AutoCampaignStage.Plantera or AutoCampaignStage.Golem;
        if (undergroundArena &&
            AutoExplosiveController.Count(ctx.Profile) < AutoExplosiveController.DesiredStock &&
            AutoExplosiveController.DemolitionistExists(out _) &&
            AutoExplosiveController.CanAffordBomb(ctx.Profile))
        {
            Portfolio.Add(AutoPlan.Simple(AutoGoalId.AcquireExplosives,
                    AutoPlanKind.Support, 4),
                AutoNeed.Materials | AutoNeed.Arena, 72);
        }

        bool arenaReady = AutoArenaController.IsPrepared(state);
        if (!arenaReady)
        {
            Portfolio.Add(AutoPlan.Simple(AutoGoalId.PrepareArena,
                    AutoPlanKind.Capability, 8),
                AutoNeed.Arena | AutoNeed.WorldGate, 86);
        }

        bool mayAttempt = !loadoutWork || !preparationWindow;
        if (arenaReady && mayAttempt && !needsSupplies)
        {
            if (AutoCampaignController.RequiresNight(state.CampaignStage) && Main.dayTime)
            {
                Portfolio.Add(AutoPlan.Explore(System.Math.Max(0, state.StrategyVariant),
                        AutoPlanKind.Support, 3),
                    AutoNeed.Opportunity | AutoNeed.Exploration | AutoNeed.Materials, 72,
                    opportunityBonus: 25);
            }
            else
            {
                Portfolio.Add(AutoPlan.Simple(AutoGoalId.SummonBoss,
                        AutoPlanKind.HardGate, 8),
                    AutoNeed.Encounter | AutoNeed.WorldGate, 205,
                    completionBonus: 55);
            }
        }
    }

    private static void AddWeaponRoutes(AutoContext ctx, bool preparationWindow)
    {
        foreach (AutoWeaponRouteKind route in WeaponRoutes)
        {
            if (!AutoVanillaWeaponStrategy.IsUpgradeRouteAvailable(ctx, route))
                continue;

            int utility = route switch
            {
                AutoWeaponRouteKind.CraftStageWeapon => 112,
                AutoWeaponRouteKind.WorldChest => 118,
                AutoWeaponRouteKind.EvilOrbGun => 106,
                AutoWeaponRouteKind.Fishing => 72,
                AutoWeaponRouteKind.HuntDrops => 54,
                _ => 40
            };
            int opportunity = route switch
            {
                AutoWeaponRouteKind.WorldChest => 55,
                AutoWeaponRouteKind.Fishing => 35,
                _ => 0
            };
            if (preparationWindow)
                utility += 38;
            else
                utility -= 36;

            AutoNeed coverage = AutoNeed.WeaponPower | AutoNeed.Equipment;
            if (route is AutoWeaponRouteKind.WorldChest or AutoWeaponRouteKind.Fishing or
                AutoWeaponRouteKind.HuntDrops)
                coverage |= AutoNeed.Opportunity | AutoNeed.Exploration;
            Portfolio.Add(AutoPlan.WeaponRoute(route), coverage, utility, opportunity);
        }
    }

    private static int FoundationWoodTarget(AutoContext ctx,
        AutoResourceBudget.WoodBudget budget, bool bench, bool furnace, bool basicLoadout)
    {
        int target = 24;
        if (!bench)
            target += WoodForBench;
        if (!furnace)
            target += WoodForFurnace;

        // Before equipment exists, wood is one useful line among ore, chests and exploration—not a
        // mandatory 500-piece opening ceremony. Once a shelter build is genuinely actionable, use
        // the shared live budget so it can finish without repeated two-item return trips.
        if (!ctx.State.ShelterCompleted)
            target = basicLoadout
                ? System.Math.Max(target, budget.RawWoodTarget)
                : System.Math.Max(target, System.Math.Min(160, budget.RawWoodTarget));
        return System.Math.Max(48, target);
    }

    private static bool StationStanding(System.Nullable<Microsoft.Xna.Framework.Point> recorded,
        int tileType)
    {
        if (recorded is not Microsoft.Xna.Framework.Point point)
            return false;
        return WorldGen.InWorld(point.X, point.Y, 2) && Main.tile[point.X, point.Y].HasTile &&
            Main.tile[point.X, point.Y].TileType == tileType;
    }

    public static int[] SmeltableBarItems => BarItems;
}
