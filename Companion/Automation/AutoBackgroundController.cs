using Terraria;

namespace MoreAIPlayer.Companion.Automation;

/// <summary>Useful work performed while the selected route is on watchdog backoff. It collects
/// nearby drops, pays replant debt, then advances the persistent exploration frontier. A local
/// two-point patrol was deliberately removed because it looked busy while making no sandbox
/// progress and could turn a temporary crafting failure into permanent pacing.</summary>
public sealed class AutoBackgroundController
{
    public void Maintain(AutoContext ctx, AutoPlan _)
    {
        Player me = ctx.Me;
        if (me.itemAnimation > 0)
        {
            ctx.Navigator.Idle(me);
            return;
        }

        if (AutoInventory.TryCollectNearby(ctx.Instance, ctx.Profile, 10f, null) >= 0)
            return;

        AutoTaskResult? replant = AutoResourceController.ServicePendingReplant(ctx);
        if (replant.HasValue)
            return;

        AutoExplorationController.Explore(ctx);
    }
}
