using Microsoft.Xna.Framework;
using Terraria;

namespace MoreAIPlayer.Companion.Automation;

/// <summary>
/// Crafts and places the companion's own crafting stations, and crafts consumable products (bars,
/// tools) at them. Placement uses the real placement ItemCheck, and the placed tile is re-found in
/// the world before it is trusted, so a coordinate is never assumed valid after a player or another
/// mod removes it (§6.2).
/// </summary>
public static class AutoStationController
{
    private const int StationSearchRadius = 14;
    private const float NearStationTiles = 2f;

    /// <summary>
    /// Drives one station from "not built" to "built and recorded". Order of operations per tick:
    /// return success if the recorded tile is still there; otherwise re-find one nearby (covers the
    /// tick right after placing); otherwise place the crafted item; otherwise walk to the
    /// prerequisite station and craft the item. <paramref name="recorded"/> is the state slot for
    /// this station and is updated in place.
    /// </summary>
    public static AutoTaskResult EnsureStation(AutoContext ctx, int itemType, int tileType, int width,
        Point? prerequisite, ref Point? recorded)
    {
        Player me = ctx.Me;

        // 1. Still standing where we left it?
        if (recorded is Point rp && IsTile(rp, tileType))
            return AutoTaskResult.Succeeded;
        recorded = null;

        // 2. A station tile of this kind already exists nearby (just placed, or a spare we can adopt).
        if (AutoWorldKnowledge.FindNearestTileOfType(me.Center, StationSearchRadius, tileType) is Point found)
        {
            recorded = found;
            return AutoTaskResult.Succeeded;
        }

        // 3. Have the station item — place it on flat ground.
        int itemIndex = AutoInventory.FindInventoryIndex(me, item => item.type == itemType);
        if (itemIndex >= 0)
        {
            if (AutoWorldKnowledge.FindStationSpot(me, width) is not Point spot)
                return AutoTaskResult.AlternativeRequired; // no clear ground here; relocate
            ctx.Actuator.MoveAndPlace(ctx.Instance, me, ctx.Navigator, itemIndex, spot);
            return AutoTaskResult.Running;
        }

        // 4. Need to craft the item; be next to the prerequisite station first. The craft's own
        // FindRecipes is the real adjacency authority, so here we only need the prerequisite tile to
        // still exist and to walk up to it.
        if (prerequisite is Point pre)
        {
            if (!IsAnyTileAt(pre))
                return AutoTaskResult.RetryLater; // prerequisite gone; the planner rebuilds it first
            if (!NearTile(me, pre))
            {
                ctx.Navigator.Navigate(me, TileCenter(pre));
                return AutoTaskResult.Running;
            }
        }

        return CraftResult(AutoCraftingService.TryCraft(ctx.Instance, ctx.Profile, itemType));
    }

    /// <summary>
    /// Crafts a consumable product (e.g. a metal bar) at a station the companion must stand next to.
    /// Navigates to <paramref name="stationTile"/> first. Returns Running while working, Succeeded
    /// once at least <paramref name="target"/> of the product is held, RetryLater if it cannot be
    /// crafted here yet.
    /// </summary>
    public static AutoTaskResult CraftAtStation(AutoContext ctx, int productType, int target, Point? stationTile)
    {
        if (AutoInventory.Count(ctx.Profile, productType) >= target)
            return AutoTaskResult.Succeeded;

        if (stationTile is Point station)
        {
            if (!IsAnyTileAt(station))
                return AutoTaskResult.RetryLater; // station gone; rebuild goal will handle it
            if (!NearTile(ctx.Me, station))
            {
                ctx.Navigator.Navigate(ctx.Me, TileCenter(station));
                return AutoTaskResult.Running;
            }
        }

        bool crafted = AutoCraftingService.TryCraftUntil(
            ctx.Instance, ctx.Profile, productType, target);
        return AutoInventory.Count(ctx.Profile, productType) >= target
            ? AutoTaskResult.Succeeded : CraftResult(crafted);
    }

    /// <summary>
    /// Smelts every currently craftable batch in the caller-supplied bar family immediately.
    /// Recipe.FindRecipes is the authority: this method must not inspect only the starter copper-
    /// through-platinum ores because it is also used by Hellstone and every Hardmode ore tier.
    /// </summary>
    public static AutoTaskResult SmeltOre(AutoContext ctx, Point? furnace, int[] barTypes)
    {
        if (furnace is Point f)
        {
            if (!IsAnyTileAt(f))
                return AutoTaskResult.RetryLater;
            if (!NearTile(ctx.Me, f))
            {
                ctx.Navigator.Navigate(ctx.Me, TileCenter(f));
                return AutoTaskResult.Running;
            }
        }

        bool crafted = false;
        foreach (int bar in barTypes)
        {
            crafted |= AutoCraftingService.TryCraftAllAvailable(ctx.Instance, ctx.Profile, bar);
            if (!AutoCraftingService.IsAvailable)
                return AutoTaskResult.Faulted;
        }
        if (crafted)
            return AutoTaskResult.Running;
        return AutoTaskResult.RetryLater;
    }

    private static AutoTaskResult CraftResult(bool crafted) => crafted
        ? AutoTaskResult.Running
        : AutoCraftingService.IsAvailable ? AutoTaskResult.RetryLater : AutoTaskResult.Faulted;

    private static bool IsTile(Point tile, int tileType) =>
        WorldGen.InWorld(tile.X, tile.Y, 2) && Main.tile[tile.X, tile.Y].HasTile &&
        Main.tile[tile.X, tile.Y].TileType == tileType;

    private static bool IsAnyTileAt(Point tile) =>
        WorldGen.InWorld(tile.X, tile.Y, 2) && Main.tile[tile.X, tile.Y].HasTile;

    private static bool NearTile(Player me, Point tile) =>
        Vector2.Distance(me.Center, TileCenter(tile)) <= NearStationTiles * 16f + me.width;

    private static Vector2 TileCenter(Point tile) => new(tile.X * 16f + 8f, tile.Y * 16f + 8f);
}
