using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;

namespace MoreAIPlayer.Companion.Automation;

/// <summary>
/// The deliberately small Phase-2 combat primitive: obtain Gel from a nearby ordinary slime when
/// pots do not provide torches. Boss targeting and stage-specific dodging remain separate work;
/// this method never attacks players, town NPCs, critters or bosses.
/// </summary>
public static class AutoCombatController
{
    private const float SearchTiles = 55f;

    public static AutoTaskResult GatherGel(AutoContext ctx)
    {
        AutoInventory.TryCollectNearby(ctx.Instance, ctx.Profile, 8f,
            item => item.type == ItemID.Gel || AutoResourceController.IsTorchOrCoin(item));
        if (AutoInventory.Has(ctx.Profile, ItemID.Gel) || AutoInventory.Has(ctx.Profile, ItemID.Torch, 3))
            return AutoTaskResult.Succeeded;

        // Pots are safer and also supply coins; prefer them before deliberately entering combat.
        AutoTaskResult potResult = AutoInventory.HasBackpackCapacityFor(ctx.Profile, ItemID.Torch)
            ? AutoResourceController.LootPots(ctx)
            : AutoTaskResult.AlternativeRequired;
        if (potResult != AutoTaskResult.AlternativeRequired)
            return potResult;

        // Deliberately hunting a slime is never the way to discover that the fresh profile owns
        // no real combat kit. Let the high-priority starter-arsenal provider run first.
        if (!AutoStarterArsenalController.RequirementSatisfied(ctx.Profile))
            return AutoTaskResult.AlternativeRequired;

        // No usable pot remains in this area (including temporarily rejected bad targets). This is
        // the real plan B, not another scan that immediately selects the same pot again.
        NPC target = FindNearestSlime(ctx.Me.Center);
        if (target is null)
            return AutoTaskResult.AlternativeRequired;
        return AutoThreatController.TryEngageTarget(ctx, target)
            ? AutoTaskResult.Running : AutoTaskResult.AlternativeRequired;
    }

    public static int BestCombatInventoryIndex(AutoContext ctx, NPC target,
        out AutoWeaponAssessment assessment) =>
        AutoCombatPhoneService.SelectBestWeapon(ctx, target, out assessment);

    private static NPC FindNearestSlime(Vector2 center)
    {
        NPC best = null;
        float bestDistance = SearchTiles * 16f;
        foreach (NPC npc in Main.ActiveNPCs)
        {
            if (!npc.active || npc.friendly || npc.townNPC || npc.boss || npc.life <= 0 ||
                npc.dontTakeDamage || npc.aiStyle != NPCAIStyleID.Slime)
                continue;
            float distance = Vector2.Distance(center, npc.Center);
            if (distance >= bestDistance)
                continue;
            bestDistance = distance;
            best = npc;
        }
        return best;
    }
}
