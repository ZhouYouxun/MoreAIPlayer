using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.Localization;
using Terraria.ModLoader;

namespace MoreAIPlayer.Companion.Content;

/// <summary>
/// Developer-only harness for repeatedly exercising the ordinary companion paths without
/// touching a real saved profile. It deliberately has no recipe or normal acquisition path.
/// </summary>
public sealed class CompanionDevelopmentBook : ModItem
{
    public override string Texture => $"Terraria/Images/Item_{ItemID.GoldenShower}";

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

    // This is intentionally a left-click-only tool. It is not a player-facing companion book.
    public override bool AltFunctionUse(Player player) => false;

    public override bool? UseItem(Player player)
    {
        if (player.whoAmI != Main.myPlayer)
            return true;

        if (Main.netMode != NetmodeID.SinglePlayer)
        {
            Main.NewText(Language.GetTextValue("Mods.MoreAIPlayer.CompanionCommand.SingleplayerOnly"),
                new Color(255, 160, 120));
            return true;
        }

        bool started = CompanionDevelopmentTestSystem.Toggle(player);
        string key = started ? "Started" : "Stopped";
        Main.NewText(Language.GetTextValue($"Mods.MoreAIPlayer.Items.CompanionDevelopmentBook.{key}"),
            started ? new Color(130, 255, 175) : new Color(255, 210, 125));
        return true;
    }
}
