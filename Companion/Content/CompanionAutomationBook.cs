using Microsoft.Xna.Framework;
using MoreAIPlayer.Companion.UI;
using Terraria;
using Terraria.ID;
using Terraria.Localization;
using Terraria.ModLoader;

namespace MoreAIPlayer.Companion.Content;

/// <summary>
/// Sixth book: the Automation Tome (自动书). A standalone entertainment item, like the Hunter's Tome
/// and the Crystal Matrix Tome, it is not part of the ordinary companion command/build system.
///
/// Left click: summon this character-and-world's one automation companion. If it is already active,
/// nothing is reset or replaced — it just reports that it is already running. If a saved automation
/// companion for this world exists but is not currently out, left click re-summons that same
/// instance state (same name, same backpack) rather than making a new one.
///
/// Right click: open that automation companion's ordinary companion backpack. If none has been
/// summoned yet, it only shows a hint and never summons one on a right click.
///
/// Single-player only, sharing the project's existing virtual-Player boundary.
/// </summary>
public sealed class CompanionAutomationBook : ModItem
{
    // The Lunar Flare book texture is the one vanilla book icon the other six books have not
    // claimed. SpellTome is the only fallback if a runtime ever lacks that constant.
    public override string Texture => $"Terraria/Images/Item_{ItemID.LunarFlareBook}";

    private static readonly Color WarnColor = new(255, 160, 120);
    private static readonly Color InfoColor = new(120, 200, 255);

    public override void SetDefaults()
    {
        Item.width = 28;
        Item.height = 30;
        Item.useStyle = ItemUseStyleID.HoldUp;
        Item.useAnimation = 20;
        Item.useTime = 20;
        Item.rare = ItemRarityID.Purple;
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
            Message("AutomationSingleplayerOnly", WarnColor);
            return true;
        }

        CompanionPlayer data = player.GetModPlayer<CompanionPlayer>();
        if (player.altFunctionUse == 2)
            OpenBackpack(player, data);
        else
            SummonOrReport(player, data);
        return true;
    }

    /// <summary>Left click: create/summon/re-summon the one automation companion for this world.</summary>
    private static void SummonOrReport(Player player, CompanionPlayer data)
    {
        // The run already finished in this world: the profile has converted to a normal companion,
        // and a second automation companion must never be created here.
        if (data.IsAutomationCompletedForCurrentWorld())
        {
            CompanionProfile finished = data.GetAutomationProfileForCurrentWorld();
            Message("AutomationCompleted", InfoColor, finished?.Name ?? string.Empty);
            return;
        }

        CompanionProfile profile = data.GetAutomationProfileForCurrentWorld();
        if (profile is null)
        {
            profile = data.CreateAutomationProfileForCurrentWorld();
        }
        else if (CompanionEntitySystem.FindByProfile(player.whoAmI, profile.Id) is not null)
        {
            // Already out: never reset, refresh, replace, teleport or re-roll it.
            Message("AutomationAlreadyActive", WarnColor);
            return;
        }

        if (CompanionEntitySystem.Summon(player, profile, Main.MouseWorld))
            Message("AutomationSummoned", InfoColor, profile.Name);
        else
            Message("AutomationNoFreeSlot", WarnColor);
    }

    /// <summary>Right click: open the automation companion's backpack; hint-only if not summoned.</summary>
    private static void OpenBackpack(Player player, CompanionPlayer data)
    {
        CompanionProfile profile = data.GetAutomationProfileForCurrentWorld();
        if (profile is null)
        {
            Message("AutomationNotCreated", WarnColor);
            return;
        }

        CompanionInstance active = CompanionEntitySystem.FindByProfile(player.whoAmI, profile.Id);
        if (active is null)
        {
            // Never summon on a right click; only advertise that the companion must be out first.
            Message("AutomationNotCreated", WarnColor);
            return;
        }

        CompanionUISystem.Open(active.Slot);
    }

    private static void Message(string key, Color color, params object[] args) =>
        Main.NewText(Language.GetTextValue($"Mods.MoreAIPlayer.CompanionCommand.{key}", args), color);

    public override void AddRecipes()
    {
        CreateRecipe()
            .AddIngredient(ItemID.DirtBlock)
            .Register();
    }
}
