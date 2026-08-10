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
            return AutoPlan.Simple(AutoGoalId.CraftGear); // CraftGear body smelts ore into bars
        }

        // 4. Turn real ore into a basic independent loadout. The gear controller equips only items
        // it actually crafted and falls back to more mining when the available bar mixture cannot
        // satisfy the next recipe.
        if (!AutoGearController.HasBasicMetalLoadout(ctx.Profile))
            return AutoPlan.Simple(AutoGoalId.CraftGear);

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
