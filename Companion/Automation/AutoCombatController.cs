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
        AutoTaskResult potResult = AutoInventory.HasBackpackCapacityFor(ctx.Profile, ItemID.Torch)
            ? AutoResourceController.LootPots(ctx)
            : AutoTaskResult.AlternativeRequired;
        if (potResult != AutoTaskResult.AlternativeRequired)
            return potResult;

        // No usable pot remains in this area (including temporarily rejected bad targets). This is
        // the real plan B, not another scan that immediately selects the same pot again.
        NPC target = FindNearestSlime(ctx.Me.Center);
        if (target is null)
            return AutoTaskResult.AlternativeRequired;

        float distance = Vector2.Distance(ctx.Me.Center, target.Center);
        if (distance > AttackTiles * 16f)
        {
            ctx.Navigator.Navigate(ctx.Me, target.Center);
            return AutoTaskResult.Running;
        }

        int weapon = BestCombatInventoryIndex(ctx, target, out _);
        if (weapon < 0)
            weapon = AutoInventory.BestToolIndex(ctx.Me, item => item.axe);
        if (weapon < 0)
            return AutoTaskResult.AlternativeRequired;

        ctx.Navigator.Idle(ctx.Me);
        ctx.Actuator.UseItemInSlot(ctx.Instance, ctx.Me, weapon, target.Center);
        return AutoTaskResult.Running;
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
