using System.Collections.Generic;
using Terraria;
using Terraria.ID;

namespace MoreAIPlayer.Companion.Automation;

/// <summary>
/// Chooses the automation companion's next goal from its current capabilities and what it has
/// already built (§5, §11 stage A/B). It is a priority ladder that always leaves the companion with
/// a legal thing to do or an honest blocked reason — never a silent stall. Phase 2 covers the
/// early resource loop up to a Furnace, with ore and bars flowing toward the first Anvil; the
/// remaining stage-B goals (metal armour, NPC rooms, Merchant, shopping) are added incrementally.
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
        AutoGoalId.MineOre or AutoGoalId.MineIronTierOre;

    private static AutoPlan PlanCore(AutoContext ctx)
    {
        AutoCapabilities cap = ctx.Capabilities;
        AutoCompanionState st = ctx.State;

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

        return AutoPlan.Idle(AutoTaskResult.RetryLater);
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
