using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;

namespace MoreAIPlayer.Companion.Automation;

/// <summary>Automation-only Demolitionist transaction. It physically reaches the NPC, pays real
/// carried coins and receives a bounded stock; bypassing the shop UI does not bypass its economy.</summary>
public static class AutoExplosiveController
{
    public const int DesiredStock = 24;

    public static int Count(CompanionProfile profile) =>
        AutoInventory.Count(profile, ItemID.Bomb) +
        AutoInventory.Count(profile, ItemID.StickyBomb) +
        AutoInventory.Count(profile, ItemID.Dynamite) +
        AutoInventory.Count(profile, ItemID.StickyDynamite);

    public static bool CanAffordBomb(CompanionProfile profile) =>
        AutoTownPurchaseService.CanAfford(profile, Item.buyPrice(silver: 3));

    public static bool DemolitionistExists(out NPC demolitionist)
    {
        return AutoTownPurchaseService.TryFindHousedNpc(NPCID.Demolitionist, out demolitionist);
    }

    public static AutoTaskResult Acquire(AutoContext ctx)
    {
        if (Count(ctx.Profile) >= DesiredStock)
            return AutoTaskResult.Succeeded;
        if (!DemolitionistExists(out NPC npc))
            return AutoTaskResult.AlternativeRequired;

        if (Vector2.DistanceSquared(ctx.Me.Center, npc.Center) > 6f * 16f * 6f * 16f)
            return AutoWorldTravelController.TravelTo(ctx, npc.Center, 5f);

        int missing = DesiredStock - Count(ctx.Profile);
        // Dynamite is preferred for arena excavation. If money is short, bombs are still useful
        // and buying one can never consume money needed for an unaffordable larger batch.
        int batch = System.Math.Min(4, missing);
        int itemType = ItemID.Dynamite;
        if (!AutoTownPurchaseService.TryPurchase(ctx.Profile, itemType, batch,
            Item.buyPrice(silver: 20)))
        {
            batch = System.Math.Min(10, missing);
            itemType = ItemID.Bomb;
            if (!AutoTownPurchaseService.TryPurchase(ctx.Profile, itemType, batch,
                Item.buyPrice(silver: 3)))
                return AutoTaskResult.AlternativeRequired;
        }
        ctx.TaskRuntime.RecordWorldProgress();
        return AutoTaskResult.Running;
    }
}
