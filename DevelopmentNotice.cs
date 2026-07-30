using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace MoreAIPlayer;

/// <summary>
/// A one-time welcome item. It is deliberately inert: its only job is to make the mod's
/// development status and compatibility policy visible to every new player.
/// </summary>
public sealed class DevelopmentNotice : ModItem
{
    // Borrow the vanilla Keybrand sprite until the mod has a dedicated icon for this notice.
    public override string Texture => $"Terraria/Images/Item_{ItemID.Keybrand}";

    public override void SetDefaults()
    {
        Item.width = 52;
        Item.height = 52;
        Item.maxStack = 1;
        Item.value = 0;
        Item.rare = ItemRarityID.White;
        Item.noMelee = true;
        Item.noUseGraphic = true;
        Item.useStyle = ItemUseStyleID.None;
    }

    public override bool CanUseItem(Player player) => false;

    public override void ModifyTooltips(System.Collections.Generic.List<TooltipLine> tooltips)
    {
        tooltips.Add(new TooltipLine(Mod, "Development", GetText("Development"))
        {
            OverrideColor = new Color(255, 205, 92)
        });
        tooltips.Add(new TooltipLine(Mod, "SoloCreator", GetText("SoloCreator"))
        {
            OverrideColor = new Color(125, 205, 255)
        });
        tooltips.Add(new TooltipLine(Mod, "Updates", GetText("Updates"))
        {
            OverrideColor = new Color(140, 255, 175)
        });
        tooltips.Add(new TooltipLine(Mod, "Compatibility", GetText("Compatibility"))
        {
            OverrideColor = new Color(255, 165, 125)
        });
        tooltips.Add(new TooltipLine(Mod, "Complexity", GetText("Complexity"))
        {
            OverrideColor = new Color(205, 180, 255)
        });
    }

    private string GetText(string key) =>
        Terraria.Localization.Language.GetTextValue($"Mods.MoreAIPlayer.Items.DevelopmentNotice.{key}");
}
