using Microsoft.Xna.Framework;
using Terraria;

namespace MoreAIPlayer.Companion.Automation;

/// <summary>Small, bounded work performed while the primary route is on watchdog backoff. It
/// collects nearby drops, pays replant debt, and patrols a short safe interval instead of leaving
/// the synthetic player motionless for up to ten seconds. The patrol never changes campaign
/// milestones or the retained primary target.</summary>
public sealed class AutoBackgroundController
{
    private Vector2 patrolAnchor;
    private int patrolDirection = 1;
    private bool active;

    public void Maintain(AutoContext ctx, AutoPlan pausedPlan)
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

        // Resource/navigation backoff must expand the search area, not pace six tiles around the
        // failure point. Exploration owns one persistent world-space target, so it cannot reverse
        // direction every time a local A* attempt fails; the paused primary plan is re-evaluated as
        // soon as its short backoff expires and can then claim newly discovered resources.
        if (pausedPlan.Goal is AutoGoalId.GatherWood or AutoGoalId.GatherStone or
            AutoGoalId.GatherGel or AutoGoalId.MineOre or AutoGoalId.MineIronTierOre or
            AutoGoalId.ExploreWorld or AutoGoalId.AcquireVitalUpgrade or
            AutoGoalId.AcquireWeaponUpgrade)
        {
            active = false;
            AutoExplorationController.Explore(ctx);
            return;
        }

        if (!active || !float.IsFinite(patrolAnchor.X) || !float.IsFinite(patrolAnchor.Y) ||
            Vector2.DistanceSquared(me.Center, patrolAnchor) > 18f * 16f * 18f * 16f)
        {
            patrolAnchor = me.Center;
            patrolDirection = me.direction == 0 ? 1 : me.direction;
            active = true;
        }

        // Crafting/building/storage retries stay near their workstation. Navigation and resource
        // retries scan a slightly wider local strip, but never turn a cooldown into a long detour.
        float radiusTiles = pausedPlan.Goal is AutoGoalId.CraftStation or AutoGoalId.CraftGear or
            AutoGoalId.CraftConsumable or AutoGoalId.BuildShelter or AutoGoalId.DepositSurplus
            ? 2.5f : 6f;
        Vector2 target = patrolAnchor + new Vector2(patrolDirection * radiusTiles * 16f, 0f);
        if (Vector2.DistanceSquared(me.Center, target) < 1.25f * 16f * (1.25f * 16f))
        {
            patrolDirection *= -1;
            ctx.Navigator.Reset();
            target = patrolAnchor + new Vector2(patrolDirection * radiusTiles * 16f, 0f);
        }
        ctx.Navigator.Navigate(me, target);
    }

    public void Reset() => active = false;
}
