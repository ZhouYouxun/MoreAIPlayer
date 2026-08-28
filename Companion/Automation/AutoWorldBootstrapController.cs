using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;

namespace MoreAIPlayer.Companion.Automation;

/// <summary>
/// One bounded reconciliation pass when a live automation player enters an existing world. Saved
/// coordinates are claims, not facts: each is checked against current tiles, nearby public world
/// infrastructure is adopted, local chest-opportunity memory is refreshed, and the frame-sliced
/// whole-world index is restarted. Inventory classification remains with AutoGearController.
/// </summary>
public static class AutoWorldBootstrapController
{
    private const int InfrastructureSearchRadius = 120;
    private const int ChestResurveyRadius = 140;

    public static void Reconcile(AutoContext ctx)
    {
        AutoCompanionState state = ctx.State;
        Player me = ctx.Me;

        RevalidateStation(ref state.WorkBench, TileID.WorkBenches);
        RevalidateStation(ref state.Furnace, TileID.Furnaces);
        RevalidateStation(ref state.Anvil, TileID.Anvils);
        RevalidateStation(ref state.Hellforge, TileID.Hellforge);
        RevalidateStation(ref state.HardmodeAnvil, TileID.MythrilAnvil);
        RevalidateStation(ref state.HardmodeForge, TileID.AdamantiteForge);
        RevalidateStation(ref state.AncientManipulator, TileID.LunarCraftingStation);

        bool nearbyInfrastructure = AdoptNearbyInfrastructure(me, state);
        if (!state.WorldAdaptationInitialized)
        {
            bool establishedWorld = AutoCampaignController.HasEstablishedWorldProgress() ||
                nearbyInfrastructure || HasNearbyStockedChest(me);
            AutoResourceBudget.InitializeHousingPlan(state, establishedWorld);
        }

        if (state.HomeAnchor is not Point home || !WorldGen.InWorld(home.X, home.Y, 10))
        {
            state.HomeAnchor = null;
            state.ShelterCompleted = false;
            state.HousingRoomsCompleted = 0;
        }
        else if (state.ShelterCompleted && !AutoShelterController.IsShelterComplete(home, state))
        {
            state.ShelterCompleted = false;
        }

        if (state.Chest is Point chest &&
            (Chest.FindChest(chest.X, chest.Y) < 0 || Chest.IsLocked(chest.X, chest.Y)))
            state.Chest = null;

        RevalidateStation(ref state.PendingAltar, TileID.DemonAltar);
        RevalidateStation(ref state.PendingEvilOrb, TileID.ShadowOrbs);
        if (state.HasDeathReturnTarget)
        {
            Point death = state.DeathReturnTarget.ToTileCoordinates();
            if (!float.IsFinite(state.DeathReturnTarget.X) ||
                !float.IsFinite(state.DeathReturnTarget.Y) ||
                !WorldGen.InWorld(death.X, death.Y, 10))
            {
                state.HasDeathReturnTarget = false;
                state.DeathReturnAttempts = 0;
            }
        }

        // Re-open only the local opportunity window. A player may have stocked nearby containers
        // since the last summon; distant explored chests retain their bounded memory.
        state.ForgetReviewedChestsNear(me.Center, ChestResurveyRadius);
        AutoWorldIndexSystem.RestartScan();
        ctx.TaskRuntime.Log("bootstrap reconciled world, inventory and nearby chests");
    }

    private static void RevalidateStation(ref Point? point, int tileType)
    {
        if (point is not Point value)
            return;
        if (!WorldGen.InWorld(value.X, value.Y, 4) || !Main.tile[value.X, value.Y].HasTile ||
            Main.tile[value.X, value.Y].TileType != tileType)
            point = null;
    }

    /// <summary>One local pass adopts every missing station; seven separate square scans caused a
    /// visible hitch precisely in the established bases this bootstrap is meant to recover.</summary>
    private static bool AdoptNearbyInfrastructure(Player me, AutoCompanionState state)
    {
        bool foundAny = state.WorkBench.HasValue || state.Furnace.HasValue || state.Anvil.HasValue ||
            state.Hellforge.HasValue || state.HardmodeAnvil.HasValue ||
            state.HardmodeForge.HasValue || state.AncientManipulator.HasValue;
        if (state.WorkBench.HasValue && state.Furnace.HasValue && state.Anvil.HasValue &&
            state.Hellforge.HasValue && state.HardmodeAnvil.HasValue &&
            state.HardmodeForge.HasValue && state.AncientManipulator.HasValue)
            return true;

        Point origin = me.Center.ToTileCoordinates();
        int radiusSquared = InfrastructureSearchRadius * InfrastructureSearchRadius;
        bool needsBench = !state.WorkBench.HasValue;
        bool needsFurnace = !state.Furnace.HasValue;
        bool needsAnvil = !state.Anvil.HasValue;
        bool needsHellforge = !state.Hellforge.HasValue;
        bool needsHardmodeAnvil = !state.HardmodeAnvil.HasValue;
        bool needsHardmodeForge = !state.HardmodeForge.HasValue;
        bool needsManipulator = !state.AncientManipulator.HasValue;
        int benchDistance = int.MaxValue;
        int furnaceDistance = int.MaxValue;
        int anvilDistance = int.MaxValue;
        int hellforgeDistance = int.MaxValue;
        int hardmodeAnvilDistance = int.MaxValue;
        int hardmodeForgeDistance = int.MaxValue;
        int manipulatorDistance = int.MaxValue;

        for (int x = origin.X - InfrastructureSearchRadius;
            x <= origin.X + InfrastructureSearchRadius; x++)
        for (int y = origin.Y - InfrastructureSearchRadius;
            y <= origin.Y + InfrastructureSearchRadius; y++)
        {
            if (!WorldGen.InWorld(x, y, 4))
                continue;
            int dx = x - origin.X;
            int dy = y - origin.Y;
            int distance = dx * dx + dy * dy;
            if (distance > radiusSquared)
                continue;
            Tile tile = Main.tile[x, y];
            if (!tile.HasTile)
                continue;
            Point point = new(x, y);
            switch (tile.TileType)
            {
                case TileID.WorkBenches when needsBench && distance < benchDistance:
                    state.WorkBench = point;
                    benchDistance = distance;
                    foundAny = true;
                    break;
                case TileID.Furnaces when needsFurnace && distance < furnaceDistance:
                    state.Furnace = point;
                    furnaceDistance = distance;
                    foundAny = true;
                    break;
                case TileID.Anvils when needsAnvil && distance < anvilDistance:
                    state.Anvil = point;
                    anvilDistance = distance;
                    foundAny = true;
                    break;
                case TileID.Hellforge when needsHellforge && distance < hellforgeDistance:
                    state.Hellforge = point;
                    hellforgeDistance = distance;
                    foundAny = true;
                    break;
                case TileID.MythrilAnvil when needsHardmodeAnvil && distance < hardmodeAnvilDistance:
                    state.HardmodeAnvil = point;
                    hardmodeAnvilDistance = distance;
                    foundAny = true;
                    break;
                case TileID.AdamantiteForge when needsHardmodeForge && distance < hardmodeForgeDistance:
                    state.HardmodeForge = point;
                    hardmodeForgeDistance = distance;
                    foundAny = true;
                    break;
                case TileID.LunarCraftingStation when needsManipulator && distance < manipulatorDistance:
                    state.AncientManipulator = point;
                    manipulatorDistance = distance;
                    foundAny = true;
                    break;
            }
        }
        return foundAny;
    }

    private static bool HasNearbyStockedChest(Player me)
    {
        Point origin = me.Center.ToTileCoordinates();
        int radiusSquared = ChestResurveyRadius * ChestResurveyRadius;
        foreach (Chest chest in Main.chest)
        {
            if (chest is null || Chest.IsLocked(chest.x, chest.y))
                continue;
            int dx = chest.x - origin.X;
            int dy = chest.y - origin.Y;
            if (dx * dx + dy * dy > radiusSquared)
                continue;
            foreach (Item item in chest.item)
                if (item is not null && !item.IsAir)
                    return true;
        }
        return false;
    }
}
