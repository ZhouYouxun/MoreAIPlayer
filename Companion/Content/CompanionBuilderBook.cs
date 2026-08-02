using Microsoft.Xna.Framework;
using MoreAIPlayer.Companion.Building;
using Terraria;
using Terraria.ID;
using Terraria.Localization;
using Terraria.ModLoader;

namespace MoreAIPlayer.Companion.Content;

/// <summary>
/// Fourth companion book. Right click selects a construction plan; left click toggles smart
/// assistance or assigns the selected plan at the cursor. Down + left click cancels the queue.
/// </summary>
public sealed class CompanionBuilderBook : ModItem
{
    public override string Texture => $"Terraria/Images/Item_{ItemID.MagnetSphere}";

    public override void SetDefaults()
    {
        Item.width = 28;
        Item.height = 30;
        Item.useStyle = ItemUseStyleID.HoldUp;
        Item.useAnimation = 20;
        Item.useTime = 20;
        Item.rare = ItemRarityID.Orange;
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
            Message("SingleplayerOnly", new Color(255, 160, 120));
            return true;
        }

        CompanionPlayer data = player.GetModPlayer<CompanionPlayer>();
        if (player.altFunctionUse == 2)
        {
            data.BuildMode = (CompanionBuildMode)(((int)data.BuildMode + 1) % 5);
            Main.NewText(Language.GetTextValue(
                "Mods.MoreAIPlayer.CompanionCommand.BuilderModeChanged",
                ModeName(data.BuildMode)), 120, 255, 170);
            return true;
        }

        if (player.controlDown)
        {
            int cancelled = CompanionBuildingSystem.CancelAll(player);
            Main.NewText(Language.GetTextValue(
                "Mods.MoreAIPlayer.CompanionCommand.BuilderCancelled",
                cancelled), 255, 210, 120);
            return true;
        }

        if (CompanionCommandBook.OwnedActiveCompanions(player.whoAmI).Count == 0)
        {
            Message("NoCompanion", new Color(255, 160, 120));
            return true;
        }

        if (data.BuildMode == CompanionBuildMode.SmartAssist)
        {
            data.SmartBuildEnabled = !data.SmartBuildEnabled;
            Message(data.SmartBuildEnabled ? "BuilderSmartEnabled" : "BuilderSmartDisabled",
                data.SmartBuildEnabled ? new Color(120, 255, 170) : new Color(210, 210, 210));
            return true;
        }

        Point target = Main.MouseWorld.ToTileCoordinates();
        if (CompanionBuildingSystem.TrySchedulePlan(
            player, data.BuildMode, target, out int queued, out string failureKey))
        {
            Main.NewText(Language.GetTextValue(
                "Mods.MoreAIPlayer.CompanionCommand.BuilderQueued",
                ModeName(data.BuildMode), queued), 120, 255, 170);
        }
        else
            Message(failureKey, new Color(255, 180, 120));

        return true;
    }

    private static string ModeName(CompanionBuildMode mode) =>
        Language.GetTextValue($"Mods.MoreAIPlayer.CompanionCommand.BuilderMode{mode}");

    private static void Message(string key, Color color) =>
        Main.NewText(Language.GetTextValue($"Mods.MoreAIPlayer.CompanionCommand.{key}"), color);

    public override void AddRecipes()
    {
        CreateRecipe()
            .AddIngredient(ItemID.DirtBlock)
            .Register();
    }
}
