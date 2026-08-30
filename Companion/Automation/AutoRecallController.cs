using Terraria;
using Terraria.ID;

namespace MoreAIPlayer.Companion.Automation;

/// <summary>Legal home recall shared by risk and deadlock recovery. Perfect position knowledge is
/// intrinsic; teleportation is not, so the effect always comes from a real owned mirror/phone or
/// consumable recall item used through ordinary Player.ItemCheck.</summary>
public static class AutoRecallController
{
    public static bool TryUseOwnedRecall(AutoContext ctx)
    {
        int slot = AutoInventory.FindInventoryIndex(ctx.Me, IsRecallItem);
        return slot >= 0 && ctx.Actuator.UseItemInSlot(ctx.Instance, ctx.Me, slot, ctx.Me.Center);
    }

    public static bool IsRecallItem(Item item) => item is not null && !item.IsAir &&
        item.type is ItemID.MagicMirror or ItemID.IceMirror or ItemID.CellPhone or
            ItemID.Shellphone or ItemID.ShellphoneSpawn or ItemID.ShellphoneOcean or
            ItemID.ShellphoneHell or ItemID.RecallPotion or ItemID.PotionOfReturn;
}
