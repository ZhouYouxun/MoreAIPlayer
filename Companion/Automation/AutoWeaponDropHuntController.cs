using Microsoft.Xna.Framework;
using Terraria;

namespace MoreAIPlayer.Companion.Automation;

/// <summary>A real drop hunt, distinct from generic exploration. It chases one feasible ordinary
/// hostile across a wider radius, fights it with the phone-selected weapon, and only completes when
/// the owned usable-weapon capability actually rises. Empty hunts are bounded by the normal route
/// watchdog and then yield to the other parallel weapon providers.</summary>
public static class AutoWeaponDropHuntController
{
    private const float HuntRadiusTiles = 90f;

    public static AutoTaskResult Hunt(AutoContext ctx)
    {
        AutoInventory.TryCollectNearby(ctx.Instance, ctx.Profile, 12f,
            item => CompanionSlotRules.IsRegularWeapon(item));
        AutoGearController.OrganizeOwnedGear(ctx.Me, ctx.Profile);
        if (!AutoCampaignController.NeedsLoadoutWork(ctx))
            return AutoTaskResult.Succeeded;

        NPC target = FindFeasibleTarget(ctx);
        if (target is null)
            return AutoExplorationController.Explore(ctx);
        if (Vector2.DistanceSquared(ctx.Me.Center, target.Center) > 14f * 16f * (14f * 16f))
            return AutoWorldTravelController.TravelTo(ctx, target.Center, 10f);
        return AutoThreatController.TryEngageTarget(ctx, target)
            ? AutoTaskResult.Running : AutoTaskResult.AlternativeRequired;
    }

    private static NPC FindFeasibleTarget(AutoContext ctx)
    {
        NPC best = null;
        float bestScore = float.MinValue;
        float radiusSquared = HuntRadiusTiles * 16f * HuntRadiusTiles * 16f;
        int capability = System.Math.Max(1, ctx.Capabilities.BestWeaponScore);
        foreach (NPC npc in Main.ActiveNPCs)
        {
            if (!npc.active || npc.friendly || npc.townNPC || npc.boss || npc.life <= 0 ||
                npc.dontTakeDamage || npc.damage <= 0 || npc.realLife >= 0 || npc.lifeMax <= 5)
                continue;
            float distance = Vector2.DistanceSquared(ctx.Me.Center, npc.Center);
            if (distance > radiusSquared)
                continue;
            // Never turn an upgrade hunt into a suicidal miniboss loop. More valuable enemies are
            // preferred only inside a loadout-relative health/damage ceiling.
            if (npc.lifeMax > capability * 45 + ctx.Me.statLifeMax2 * 4 ||
                npc.damage > ctx.Me.statDefense + ctx.Me.statLifeMax2 / 3)
                continue;
            float score = npc.value * 0.02f + npc.lifeMax * 0.08f -
                System.MathF.Sqrt(distance) * 0.03f;
            if (score <= bestScore)
                continue;
            bestScore = score;
            best = npc;
        }
        return best;
    }
}
