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
    public int MaxLife;
    public int LifePercent;
    public int Defense;
    public int BestWeaponScore;
    public int HealingPotionCount;
    public int UsefulAmmoCount;
    public int FreeBackpackSlots;
    public bool HasHook;
    public bool HasMount;
    public bool HasFlightAccessory;

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
        MaxLife = me.statLifeMax2;
        LifePercent = MaxLife <= 0 ? 0 : System.Math.Clamp(me.statLife * 100 / MaxLife, 0, 100);
        Defense = me.statDefense;
        BestWeaponScore = AutoWeaponEvaluator.BestUsableScore(me, profile);
        HealingPotionCount = Count(profile, item => item.potion && item.healLife > 0);
        UsefulAmmoCount = AutoCombatSupplyController.CountAllUsefulAmmo(profile);
        FreeBackpackSlots = Count(profile.Backpack, item => item.IsAir);
        HasHook = profile.Hook.Length > 0 && !profile.Hook[0].IsAir;
        HasMount = profile.Mount.Length > 0 && !profile.Mount[0].IsAir;
        HasFlightAccessory = Count(profile.Accessories, item => item.wingSlot > 0) > 0 ||
            Count(profile.ExtraAccessories, item => item.wingSlot > 0) > 0;
    }

    private static bool Adjacent(Player me, int tileType) =>
        tileType >= 0 && tileType < me.adjTile.Length && me.adjTile[tileType];

    private static int Count(CompanionProfile profile, System.Func<Item, bool> match) =>
        Count(profile.Hotbar, match) + Count(profile.Backpack, match) +
        Count(profile.Ammo, match);

    private static int Count(Item[] items, System.Func<Item, bool> match)
    {
        int count = 0;
        foreach (Item item in items)
            if (item is not null && !item.IsAir && match(item))
                count += item.stack;
        return count;
    }
}
