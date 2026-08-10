using Terraria;
using Terraria.ID;

namespace MoreAIPlayer.Companion.Automation;

/// <summary>
/// A cheap per-refresh snapshot of what the automation companion can currently do: its best tool
/// powers, the crafting stations within reach right now, and the key early-game material counts.
/// The planner reads this to choose the next goal (§5.2). It is a snapshot, not the authority on
/// craftability — <see cref="AutoCraftingService"/> asks vanilla for that when a craft is attempted.
/// </summary>
public sealed class AutoCapabilities
{
    public int PickPower;
    public int AxePower;
    public int HammerPower;

    public bool NearWorkBench;
    public bool NearFurnace;
    public bool NearAnvil;

    public int Wood;
    public int Stone;
    public int Gel;
    public int Torches;
    public int IronLeadOre;
    public int IronLeadBars;

    /// <summary>Recomputes the snapshot from the companion's inventory and its current surroundings.
    /// Calls <see cref="Player.AdjTiles"/> so the station flags reflect where the companion is
    /// standing this instant.</summary>
    public void Refresh(Player me, CompanionProfile profile)
    {
        PickPower = AutoInventory.BestPickaxe(profile).pick;
        AxePower = AutoInventory.BestAxe(profile).axe;
        HammerPower = AutoInventory.BestHammer(profile).hammer;

        me.AdjTiles();
        NearWorkBench = Adjacent(me, TileID.WorkBenches);
        NearFurnace = Adjacent(me, TileID.Furnaces) || Adjacent(me, TileID.Hellforge);
        NearAnvil = Adjacent(me, TileID.Anvils) || Adjacent(me, TileID.MythrilAnvil);

        Wood = AutoInventory.Count(profile, ItemID.Wood);
        Stone = AutoInventory.Count(profile, ItemID.StoneBlock);
        Gel = AutoInventory.Count(profile, ItemID.Gel);
        Torches = AutoInventory.Count(profile, ItemID.Torch);
        IronLeadOre = AutoInventory.Count(profile, ItemID.IronOre) + AutoInventory.Count(profile, ItemID.LeadOre);
        IronLeadBars = AutoInventory.Count(profile, ItemID.IronBar) + AutoInventory.Count(profile, ItemID.LeadBar);
    }

    private static bool Adjacent(Player me, int tileType) =>
        tileType >= 0 && tileType < me.adjTile.Length && me.adjTile[tileType];
}
