using Microsoft.Xna.Framework;
using MoreAIPlayer.Companion.Building;
using Terraria;
using Terraria.ID;
using Terraria.Localization;
using Terraria.ModLoader;

namespace MoreAIPlayer.Companion.Content;

/// <summary>
/// Fourth companion book. Holding right click opens the tactic wheel; a left click switches the
/// selected tactic on or off. The three tactics act on their own while switched on, so there is
/// no cursor-targeted "use" — the book is a mode toggle, not a build command.
/// </summary>
public sealed class CompanionBuilderBook : ModItem
{
    public override string Texture => "MoreAIPlayer/图片/建造书";

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

    // Right click still counts as an alt function so vanilla treats it as "not a left-click use".
    // The tactic wheel reads the raw right button itself; CanUseItem then refuses the alt so the
    // book never animates or acts on right click.
    public override bool AltFunctionUse(Player player) => true;

    // Only a left click, and only while the wheel is closed, is a real use. That single left
    // click toggles the selected tactic.
    public override bool CanUseItem(Player player) =>
        player.altFunctionUse != 2 && !UI.CompanionBuilderModeWheel.Active;

    public override bool? UseItem(Player player)
    {
        if (player.whoAmI != Main.myPlayer)
            return true;

        if (Main.netMode != NetmodeID.SinglePlayer)
        {
            Message("SingleplayerOnly", new Color(255, 160, 120));
            return true;
        }

        if (UI.CompanionBuilderModeWheel.Active || player.altFunctionUse == 2)
            return true;

        if (CompanionCommandBook.OwnedActiveCompanions(player.whoAmI).Count == 0)
        {
            Message("NoCompanion", new Color(255, 160, 120));
            return true;
        }

        CompanionPlayer data = player.GetModPlayer<CompanionPlayer>();
        foreach (CompanionInstance companion in CompanionCommandBook.OwnedActiveCompanions(player.whoAmI))
        {
            if (companion.TryGetContext(out _, out CompanionProfile profile))
                profile.WheelCommandsActive = false;
        }
        data.BuildTacticActive = !data.BuildTacticActive;

        if (data.BuildTacticActive)
        {
            CompanionBuildingSystem.ResetSmartObservation(player);
            Main.NewText(Language.GetTextValue(
                "Mods.MoreAIPlayer.CompanionCommand.BuilderTacticOn",
                ModeName(data.BuildMode)), 120, 255, 170);
        }
        else
        {
            CompanionBuildingSystem.CancelAll(player);
            Main.NewText(Language.GetTextValue(
                "Mods.MoreAIPlayer.CompanionCommand.BuilderTacticOff",
                ModeName(data.BuildMode)), 210, 210, 210);
        }

        return true;
    }

    internal static string ModeName(CompanionBuildMode mode) =>
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
