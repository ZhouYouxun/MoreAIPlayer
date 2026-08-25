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
    private const int StarterWoodTarget = 500;
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
        if (!AutoInventory.HasBackpackSpace(ctx.Profile) && NeedsDepositBefore(ctx, plan))
        {
            return AutoPlan.Simple(AutoGoalId.DepositSurplus, AutoPlanKind.Support, 2);
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

        if (deferred.Goal == AutoGoalId.AcquireWeaponUpgrade)
        {
            AutoWeaponRouteKind blockedRoute = (AutoWeaponRouteKind)deferred.Amount;
            AutoWeaponRouteKind alternate = AutoVanillaWeaponStrategy.ChooseUpgradeRoute(ctx,
                route => route != blockedRoute &&
                    !ctx.TaskRuntime.IsDeferred(AutoPlan.WeaponRoute(route)));
            if (alternate != AutoWeaponRouteKind.None)
            {
                AutoPlan alternatePlan = AutoPlan.WeaponRoute(alternate);
                if (!ctx.TaskRuntime.IsDeferred(alternatePlan))
                    candidates.Add((alternatePlan, 92));
            }
        }

        if (!AutoInventory.HasBackpackSpace(ctx.Profile) && NeedsDepositBefore(ctx, deferred))
        {
            chosen = AutoPlan.Simple(AutoGoalId.DepositSurplus, AutoPlanKind.Support, 2);
            return !ctx.TaskRuntime.IsDeferred(chosen);
        }

        int woodTarget = AutoResourceBudget.Evaluate(ctx).RawWoodTarget;
        if (deferred.Goal != AutoGoalId.GatherWood && cap.Wood < woodTarget)
            candidates.Add((AutoPlan.Gather(AutoGoalId.GatherWood, woodTarget,
                AutoPlanKind.Support, 2), 70 + System.Math.Min(200, woodTarget - cap.Wood)));

        if (deferred.Goal != AutoGoalId.GatherStone && cap.Stone < StoneForFurnace)
            candidates.Add((AutoPlan.Gather(AutoGoalId.GatherStone, StoneForFurnace,
                AutoPlanKind.Support, 2), 66 + StoneForFurnace - cap.Stone));

        if (cap.Torches < TorchesForFurnace && deferred.Goal != AutoGoalId.ExploreWorld)
        {
            AutoPlan torchPlan = cap.Gel > 0 && cap.Wood > 0
                ? AutoPlan.Product(ItemID.Torch, AutoPlanKind.Support, 2)
                // Gel is opportunistic: pots, chest loot and any slime encountered on a real route
                // are welcome, but a missing slime must never become an early-game hard gate.
                : AutoPlan.Simple(AutoGoalId.ExploreWorld, AutoPlanKind.Support, 2);
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
        // These activities can discover arbitrary new item types, so one genuinely empty slot is
        // required. Building, crafting, arenas and class upgrades may consume a full inventory and
        // must never be pre-empted merely because no slot is empty.
        AutoGoalId.LootChest or AutoGoalId.ExploreWorld or AutoGoalId.FishOpportunity or
            AutoGoalId.AcquireWeaponUpgrade => true,
        _ => false
    };

    private static AutoPlan PlanCore(AutoContext ctx)
    {
        AutoCapabilities cap = ctx.Capabilities;
        AutoCompanionState st = ctx.State;
        AutoResourceBudget.WoodBudget woodBudget = AutoResourceBudget.Evaluate(ctx);

        if (AutoBossController.HasActiveBoss())
            return AutoPlan.Simple(AutoGoalId.FightBoss, AutoPlanKind.HardGate, 5);

        if (st.HasDeathReturnTarget)
            return AutoPlan.Simple(AutoGoalId.ReturnToDeathSite, AutoPlanKind.HardGate, 5);

        if (AutoRecoveryController.IsOverwhelmed(ctx))
            return AutoPlan.Simple(AutoGoalId.RiskRecovery, AutoPlanKind.Support, 4);

        // Foundation starts with a deliberate resource runway. The first copper pick can already
        // mine basic ore, so do not detour through slime farming, a bench or a furnace first:
        // secure a conservative 500 wood, restore every felled tree that can be restored, then
        // descend for an initial ore survey. Later housing/arena budgeting remains independent.
        if (st.CampaignStage == AutoCampaignStage.Foundation)
        {
            if (cap.Wood < StarterWoodTarget || st.PendingReplantCount > 0)
                return AutoPlan.Gather(AutoGoalId.GatherWood, StarterWoodTarget,
                    AutoPlanKind.HardGate, 5);
            if (AutoResourceController.CountBasicOre(ctx.Profile) < OreStock)
                return AutoPlan.Gather(AutoGoalId.MineOre, OreStock, AutoPlanKind.HardGate, 5);
        }

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
                // Mining and exploration can continue without a torch. Search productive terrain
                // for pots/chests and only fight slimes which actually appear in the way.
                return AutoPlan.Simple(AutoGoalId.ExploreWorld, AutoPlanKind.Support, 3);
            }

            if (cap.Stone < StoneForFurnace)
                return AutoPlan.Gather(AutoGoalId.GatherStone, StoneStock);
            if (cap.Wood < WoodForFurnace)
                return AutoPlan.Gather(AutoGoalId.GatherWood, System.Math.Max(50, WoodForFurnace));
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
            if (cap.Wood < woodBudget.RawWoodTarget)
                return AutoPlan.Gather(AutoGoalId.GatherWood, woodBudget.RawWoodTarget);
            return AutoPlan.Simple(AutoGoalId.BuildShelter);
        }

        AutoCampaignController.RefreshStage(ctx);
        if (st.CampaignStage == AutoCampaignStage.Completed)
            return AutoPlan.Idle(AutoTaskResult.Succeeded);

        // Re-evaluate after every milestone. Housing demand, arena dimensions and operational
        // reserves all rise with progression; no later module may silently spend an early-game
        // fixed stockpile which was already promised to another job.
        woodBudget = AutoResourceBudget.Evaluate(ctx);
        int woodRefillTrigger = System.Math.Max(50, woodBudget.RawWoodTarget * 4 / 5);
        if (cap.Wood < woodRefillTrigger)
            return AutoPlan.Gather(AutoGoalId.GatherWood, woodBudget.RawWoodTarget,
                AutoPlanKind.Support, 4);

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
