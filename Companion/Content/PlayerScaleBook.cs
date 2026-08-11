using Microsoft.Xna.Framework;
using MoreAIPlayer.Companion.UI;
using Terraria;
using Terraria.ID;
using Terraria.Localization;
using Terraria.ModLoader;

namespace MoreAIPlayer.Companion.Content;

/// <summary>Fifth book: crystal-matrix control for the real player's body scale.</summary>
public sealed class PlayerScaleBook : ModItem
{
    public override string Texture => "MoreAIPlayer/图片/倍率书";

    public override void SetDefaults()
    {
        Item.width = 28;
        Item.height = 30;
        Item.useStyle = ItemUseStyleID.HoldUp;
        Item.useAnimation = 20;
        Item.useTime = 20;
        Item.rare = ItemRarityID.LightRed;
        Item.value = 0;
        Item.noMelee = true;
        Item.noUseGraphic = false;
        Item.autoReuse = false;
    }

    public override bool AltFunctionUse(Player player) => true;

    public override bool CanUseItem(Player player) => !PlayerScaleMatrixPanel.IsOpen;

    public override bool? UseItem(Player player)
    {
        if (player.whoAmI != Main.myPlayer)
            return true;

        if (Main.netMode != NetmodeID.SinglePlayer)
        {
            Main.NewText(Language.GetTextValue("Mods.MoreAIPlayer.CompanionCommand.ScaleSingleplayerOnly"),
                new Color(255, 160, 120));
            return true;
        }

        if (player.altFunctionUse == 2)
        {
            PlayerScaleMatrixPanel.Toggle();
            return true;
        }

        player.GetModPlayer<PlayerScalePlayer>().ToggleScale();
        return true;
    }

    public override void AddRecipes()
    {
        CreateRecipe()
            .AddIngredient(ItemID.DirtBlock)
            .Register();
    }
}
