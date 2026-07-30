using Microsoft.Xna.Framework;
using MoreAIPlayer.Companion.UI;
using Terraria;
using Terraria.ID;
using Terraria.Localization;
using Terraria.ModLoader;

namespace MoreAIPlayer.Companion.Content;

public sealed class CompanionDebugger : ModItem
{
    public override string Texture => $"Terraria/Images/Item_{ItemID.Book}";

    public override void SetDefaults()
    {
        Item.width = 28;
        Item.height = 30;
        Item.useStyle = ItemUseStyleID.HoldUp;
        Item.useAnimation = 24;
        Item.useTime = 24;
        Item.rare = ItemRarityID.Blue;
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
            Main.NewText(Language.GetTextValue("Mods.MoreAIPlayer.CompanionCommand.SingleplayerOnly"), new Color(255, 160, 120));
            return true;
        }

        if (player.altFunctionUse == 2)
        {
            // Right click opens the companion manager panel instead of dismissing directly.
            CompanionManagerPanel.Toggle();
            return true;
        }

        CompanionPlayer playerData = player.GetModPlayer<CompanionPlayer>();
        CompanionProfile profile = null;
        foreach (CompanionProfile candidate in playerData.Profiles)
        {
            if (CompanionEntitySystem.FindByProfile(player.whoAmI, candidate.Id) is null)
            {
                profile = candidate;
                break;
            }
        }

        profile ??= playerData.CreateProfile(playerData.Profiles.Count == 0);

        if (!CompanionEntitySystem.Summon(player, profile, Main.MouseWorld))
            Main.NewText(Language.GetTextValue("Mods.MoreAIPlayer.CompanionCommand.NoFreeSlot"), new Color(255, 160, 120));
        return true;
    }

    public override void AddRecipes()
    {
        CreateRecipe()
            .AddIngredient(ItemID.DirtBlock)
            .Register();
    }
}
