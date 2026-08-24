using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;

namespace MoreAIPlayer.Companion.Automation;

/// <summary>Uses the whole-world tile index to acquire permanent health. Targets are retained and
/// physically reached/mined; perfect information removes blind prospecting, not acquisition.</summary>
public static class AutoVitalController
{
    public static bool NeedsUpgrade(AutoContext ctx)
    {
        AutoCompanionState state = ctx.State;
        AutoCampaignStage stage = state.CampaignStage;
        int crystalTarget = stage switch
        {
            AutoCampaignStage.Foundation or AutoCampaignStage.EyeOfCthulhu => 5,
            AutoCampaignStage.EvilBoss or AutoCampaignStage.Skeletron => 10,
            _ => 15
        };
        if (state.LifeCrystals < crystalTarget)
            return true;

        // Life Fruit is valuable but not a progression key. Take every currently indexed fruit
        // after the first mechanical victory, then continue immediately when none is live instead
        // of standing still until random jungle growth reaches an arbitrary quota.
        return Main.hardMode && AnyMechanicalBossDown() && state.LifeFruits < 20 &&
            AutoWorldIndexSystem.FindNearest(ctx.Me.Center, [TileID.LifeFruit]) is not null;
    }

    public static AutoTaskResult Acquire(AutoContext ctx)
    {
        AutoCompanionState state = ctx.State;
        bool fruit = state.LifeCrystals >= 15 && Main.hardMode && AnyMechanicalBossDown() &&
            state.LifeFruits < 20;
        int tileType = fruit ? TileID.LifeFruit : TileID.Heart;
        int itemType = fruit ? ItemID.LifeFruit : ItemID.LifeCrystal;

        AutoInventory.TryCollectNearby(ctx.Instance, ctx.Profile, 8f, item => item.type == itemType);
        int before = fruit ? state.LifeFruits : state.LifeCrystals;
        AutoGearController.ConsumePermanentUpgrade(ctx.Me, ctx.Profile);
        int after = fruit ? state.LifeFruits : state.LifeCrystals;
        if (after > before)
        {
            ctx.TaskRuntime.ClearActiveTarget();
            ctx.TaskRuntime.RecordWorldProgress();
            return AutoTaskResult.Running;
        }
        if (!NeedsUpgrade(ctx))
            return AutoTaskResult.Succeeded;

        bool Valid(Point p) => WorldGen.InWorld(p.X, p.Y, 5) && Main.tile[p.X, p.Y].HasTile &&
            Main.tile[p.X, p.Y].TileType == tileType && WorldGen.CanKillTile(p.X, p.Y);

        Point target;
        if (!ctx.TaskRuntime.TryGetActiveTarget(AutoGoalId.AcquireVitalUpgrade, Valid, out target))
        {
            Point? found = AutoWorldIndexSystem.FindNearest(ctx.Me.Center, [tileType],
                p => !ctx.TaskRuntime.IsTargetRejected(AutoGoalId.AcquireVitalUpgrade, p));
            if (found is not Point indexed)
            {
                // Keep accomplishing useful exploration while the frame-sliced index catches up.
                return AutoWorldIndexSystem.ScanComplete
                    ? AutoTaskResult.AlternativeRequired
                    : AutoExplorationController.Explore(ctx);
            }
            target = indexed;
            ctx.TaskRuntime.SetActiveTarget(AutoGoalId.AcquireVitalUpgrade, target);
        }

        Vector2 targetWorld = new(target.X * 16f + 8f, target.Y * 16f + 8f);
        if (Vector2.DistanceSquared(ctx.Me.Center, targetWorld) > 7f * 16f * 7f * 16f)
            return AutoWorldTravelController.TravelTo(ctx, targetWorld, 6f);

        int pick = AutoInventory.BestToolIndex(ctx.Me, item => item.pick);
        if (pick < 0 || !ctx.Actuator.MoveAndWorkTile(ctx.Instance, ctx.Me, ctx.Navigator, pick, target))
        {
            ctx.TaskRuntime.RejectActiveTarget(3, 3600);
            return AutoTaskResult.AlternativeRequired;
        }
        return AutoTaskResult.Running;
    }

    private static bool AnyMechanicalBossDown() => NPC.downedMechBoss1 ||
        NPC.downedMechBoss2 || NPC.downedMechBoss3;
}
