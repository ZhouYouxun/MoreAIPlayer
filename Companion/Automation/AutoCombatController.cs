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
    private const float AttackTiles = 7f;

    public static AutoTaskResult GatherGel(AutoContext ctx)
    {
        AutoInventory.TryCollectNearby(ctx.Instance, ctx.Profile, 8f,
            item => item.type == ItemID.Gel || AutoResourceController.IsTorchOrCoin(item));
        if (AutoInventory.Has(ctx.Profile, ItemID.Gel) || AutoInventory.Has(ctx.Profile, ItemID.Torch, 3))
            return AutoTaskResult.Succeeded;

        // Pots are safer and also supply coins; prefer them before deliberately entering combat.
        Point? pot = AutoWorldKnowledge.FindNearestTileOfType(ctx.Me.Center, 46, TileID.Pots);
        if (pot.HasValue)
            return AutoResourceController.LootPots(ctx);

        NPC target = FindNearestSlime(ctx.Me.Center);
        if (target is null)
            return AutoTaskResult.AlternativeRequired;

        float distance = Vector2.Distance(ctx.Me.Center, target.Center);
        if (distance > AttackTiles * 16f)
        {
            ctx.Navigator.Navigate(ctx.Me, target.Center);
            return AutoTaskResult.Running;
        }

        int weapon = AutoInventory.FindInventoryIndex(ctx.Me, item =>
            !item.IsAir && item.damage > 0 && item.pick <= 0 && item.hammer <= 0);
        if (weapon < 0)
            weapon = AutoInventory.BestToolIndex(ctx.Me, item => item.axe);
        if (weapon < 0)
            return AutoTaskResult.AlternativeRequired;

        ctx.Navigator.Idle(ctx.Me);
        ctx.Actuator.UseItemInSlot(ctx.Instance, ctx.Me, weapon, target.Center);
        return AutoTaskResult.Running;
    }

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
