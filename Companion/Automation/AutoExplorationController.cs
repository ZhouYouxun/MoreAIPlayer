using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;

namespace MoreAIPlayer.Companion.Automation;

/// <summary>Productive fallback after the starter base: sweep the world in bounded segments,
/// collect worthwhile legal drops and defend against nearby ordinary enemies. Nearby chests are
/// promoted into their own higher-utility plan by the progression planner.</summary>
public static class AutoExplorationController
{
    private const int SegmentTiles = 150;
    private const int WorldMarginTiles = 120;
    private const float PickupTiles = 8f;

    public static AutoTaskResult Explore(AutoContext ctx)
    {
        AutoInventory.TryCollectNearby(ctx.Instance, ctx.Profile, PickupTiles,
            item => AutoLootController.OpportunityValue(ctx.Profile, item) > 0);
        AutoGearController.OrganizeOwnedGear(ctx.Me, ctx.Profile);

        if (AutoThreatController.TryDefendWhileExploring(ctx))
            return AutoTaskResult.Running;

        AutoCompanionState state = ctx.State;
        int currentX = ctx.Me.Center.ToTileCoordinates().X;
        int leftLimit = WorldMarginTiles;
        int rightLimit = Main.maxTilesX - WorldMarginTiles;
        if (state.ExplorationDirection == 0)
            state.ExplorationDirection = 1;

        bool targetInvalid = state.ExplorationTargetTileX < leftLimit || state.ExplorationTargetTileX > rightLimit;
        bool reached = !targetInvalid && System.Math.Abs(currentX - state.ExplorationTargetTileX) <= 8;
        if (targetInvalid || reached)
        {
            if (reached)
            {
                state.ExplorationSegments++;
                ctx.TaskRuntime.RecordWorldProgress();
            }
            if (currentX <= leftLimit + 10)
                state.ExplorationDirection = 1;
            else if (currentX >= rightLimit - 10)
                state.ExplorationDirection = -1;
            state.ExplorationTargetTileX = System.Math.Clamp(
                currentX + state.ExplorationDirection * SegmentTiles, leftLimit, rightLimit);
        }

        Vector2 target = new(state.ExplorationTargetTileX * 16f + 8f,
            FindSurfaceCenterY(state.ExplorationTargetTileX, ctx.Me));
        ctx.Navigator.Navigate(ctx.Me, target);
        return AutoTaskResult.Running;
    }

    private static float FindSurfaceCenterY(int x, Player me)
    {
        int start = System.Math.Max(10, (int)Main.worldSurface - 180);
        int end = System.Math.Min(Main.maxTilesY - 10, (int)Main.rockLayer + 120);
        for (int y = start; y <= end; y++)
        {
            if (!WorldGen.SolidTile(x, y))
                continue;
            bool headroom = !WorldGen.SolidTile(x, y - 1) && !WorldGen.SolidTile(x, y - 2) &&
                !WorldGen.SolidTile(x, y - 3);
            if (headroom)
                return y * 16f - me.height * 0.5f - 2f;
        }
        return me.Center.Y;
    }

}
