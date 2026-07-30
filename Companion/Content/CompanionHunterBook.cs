using Microsoft.Xna.Framework;
using MoreAIPlayer.Companion.Hunter;
using MoreAIPlayer.Companion.UI;
using Terraria;
using Terraria.ID;
using Terraria.Localization;
using Terraria.ModLoader;

namespace MoreAIPlayer.Companion.Content;

/// <summary>
/// Third companion book: Hunter's Tome (猎人手稿).
/// Left click toggles Hostile Hunter PVP Mode ON/OFF.
/// Right click opens the Hunter Configuration Panel (HunterConfigPanel) to adjust hunter count
/// and options.
/// </summary>
public sealed class CompanionHunterBook : ModItem
{
    public override string Texture => $"Terraria/Images/Item_{ItemID.DemonScythe}";

    public override void SetDefaults()
    {
        Item.width = 28;
        Item.height = 30;
        Item.useStyle = ItemUseStyleID.HoldUp;
        Item.useAnimation = 20;
        Item.useTime = 20;
        Item.rare = ItemRarityID.Red;
        Item.value = 0;
        Item.noUseGraphic = false;
        Item.autoReuse = false;
    }

    public override bool AltFunctionUse(Player player) => true;

    public override bool? UseItem(Player player)
    {
        if (player.whoAmI != Main.myPlayer)
            return true;

        if (Main.netMode != NetmodeID.SinglePlayer)
        {
            Main.NewText(Language.GetTextValue("Mods.MoreAIPlayer.CompanionCommand.HunterSingleplayerOnly"), 255, 160, 120);
            return true;
        }

        if (player.altFunctionUse == 2)
        {
            // Right click opens the hunter config panel
            HunterConfigPanel.Toggle();
            return true;
        }

        // Left click toggles hunter mode
        HunterSystem.ToggleMode();
        return true;
    }

    public override void AddRecipes()
    {
        CreateRecipe()
            .AddIngredient(ItemID.DirtBlock)
            .Register();
    }
}
