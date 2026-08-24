using Microsoft.Xna.Framework;
using MoreAIPlayer.Companion.AI;
using Terraria;
using Terraria.ID;

namespace MoreAIPlayer.Companion.Automation;

/// <summary>
/// The automation companion's low-level "press the use button" and "walk to a tile and swing a
/// tool at it" primitive. It writes exactly the same virtual inputs the ordinary
/// <see cref="CompanionBrain"/> uses (selectedItem + controlUseItem + virtual mouse + WorkTile),
/// so vanilla's ItemCheck performs the real mining, chopping, placing and item use — no shortcut,
/// no synthesised results.
///
/// It intentionally implements only the subset the resource loop needs (plain tools, placeables and
/// simple weapon swings), not the channelled-holdout weapon handling in the ordinary brain; that is
/// added when combat lands in a later phase.
/// </summary>
public sealed class AutoActuator
{
    private bool itemUseClaimedThisTick;

    /// <summary>Once-per-tick reset. Only one item use may be started per tick, exactly like a human
    /// pressing one button.</summary>
    public void BeginTick() => itemUseClaimedThisTick = false;

    /// <summary>True once an item use has been claimed this tick.</summary>
    public bool ItemUseSpent => itemUseClaimedThisTick;

    /// <summary>
    /// Selects the item in <paramref name="slot"/> and presses the virtual use button, aiming at
    /// <paramref name="aimWorld"/>. For mining/chopping/placing, pass the target tile in
    /// <paramref name="workTile"/> so the virtual mouse resolves to it. Returns false if the use
    /// could not be started this tick (already spent, empty slot, or a swing is still finishing).
    /// </summary>
    public bool UseItemInSlot(CompanionInstance instance, Player me, int slot, Vector2 aimWorld,
        Point? workTile = null, bool altUse = false)
    {
        if (itemUseClaimedThisTick)
            return false;
        if (slot < 0 || slot >= me.inventory.Length || me.inventory[slot].IsAir)
            return false;

        // Finish the current swing before switching to a different slot.
        if (me.itemAnimation > 0 && me.selectedItem != slot)
            return false;

        if (me.itemAnimation == 0)
        {
            // Clear leftover item time when switching weapons, matching the ordinary brain's note:
            // stale itemTime makes ItemTimeIsZero false on the first tick and silently skips
            // ItemCheck_Shoot, so a ranged weapon animates but never fires.
            if (me.selectedItem != slot)
                me.itemTime = me.itemTimeMax = 0;
            me.selectedItem = slot;
            instance.ActiveItemType = me.inventory[slot].type;
        }

        if (me.selectedItem != slot)
            return false;

        itemUseClaimedThisTick = true;
        me.controlUseItem = true;
        instance.WantsUseItem = true;
        me.releaseUseItem = me.itemAnimation == 0;
        if (me.itemAnimation == 0)
            me.altFunctionUse = altUse ? 1 : 0;
        instance.WantsAltUse = altUse;
        instance.AimWorld = aimWorld;
        instance.WorkTile = workTile;
        return true;
    }

    /// <summary>
    /// Walks within tool reach of <paramref name="tile"/> and swings the tool in
    /// <paramref name="slot"/> at it. Returns true while the job is in progress (navigating or
    /// swinging), false if the tile is invalid.
    /// </summary>
    public bool MoveAndWorkTile(CompanionInstance instance, Player me, CompanionNavigator navigator,
        int slot, Point tile)
    {
        if (!WorldGen.InWorld(tile.X, tile.Y, 3) || !Main.tile[tile.X, tile.Y].HasTile)
            return false;

        Vector2 center = new(tile.X * 16f + 8f, tile.Y * 16f + 8f);
        Item tool = me.inventory[slot];
        if (!IsTileInReach(me, tool, tile.X, tile.Y))
        {
            navigator.Navigate(me, center);
            return true;
        }

        navigator.Idle(me);
        UseItemInSlot(instance, me, slot, center, tile);
        return true;
    }

    /// <summary>Wall-removal counterpart used by the self-repairing sky housing. A wall can exist
    /// without a foreground tile, so it cannot pass through <see cref="MoveAndWorkTile"/>.</summary>
    public bool MoveAndWorkWall(CompanionInstance instance, Player me, CompanionNavigator navigator,
        int slot, Point wall)
    {
        if (!WorldGen.InWorld(wall.X, wall.Y, 3) || Main.tile[wall.X, wall.Y].WallType == WallID.None)
            return false;
        Vector2 center = new(wall.X * 16f + 8f, wall.Y * 16f + 8f);
        Item tool = me.inventory[slot];
        if (!IsTileInReach(me, tool, wall.X, wall.Y))
        {
            navigator.Navigate(me, center);
            return true;
        }
        navigator.Idle(me);
        UseItemInSlot(instance, me, slot, center, wall);
        return true;
    }

    /// <summary>
    /// Walks adjacent to <paramref name="tile"/> and places the block/wall item in
    /// <paramref name="slot"/> there. Placement uses the same virtual-mouse WorkTile path as mining,
    /// so vanilla consumes the real material and runs its normal placement validity.
    /// </summary>
    public bool MoveAndPlace(CompanionInstance instance, Player me, CompanionNavigator navigator,
        int slot, Point tile)
    {
        if (!WorldGen.InWorld(tile.X, tile.Y, 3))
            return false;

        Vector2 center = new(tile.X * 16f + 8f, tile.Y * 16f + 8f);
        Item item = me.inventory[slot];
        if (!IsTileInReach(me, item, tile.X, tile.Y))
        {
            navigator.Navigate(me, center);
            return true;
        }

        navigator.Idle(me);
        UseItemInSlot(instance, me, slot, center, tile);
        return true;
    }

    /// <summary>Vanilla's tile reach test (Player.tileRangeX/Y + the item's tileBoost), matching
    /// ItemCheck_UseMiningTools — mining/placing needs reach only, never line of sight.</summary>
    public static bool IsTileInReach(Player me, Item tool, int x, int y) =>
        me.position.X / 16f - Player.tileRangeX - tool.tileBoost <= x &&
        (me.position.X + me.width) / 16f + Player.tileRangeX + tool.tileBoost - 1f >= x &&
        me.position.Y / 16f - Player.tileRangeY - tool.tileBoost <= y &&
        (me.position.Y + me.height) / 16f + Player.tileRangeY + tool.tileBoost - 2f >= y;
}
