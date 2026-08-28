using Microsoft.Xna.Framework;
using MoreAIPlayer.Companion.Automation;
using Terraria;
using Terraria.ID;
using Terraria.Localization;
using Terraria.ModLoader;

namespace MoreAIPlayer.Companion.Content;

/// <summary>
/// Sixth book: the Automation Tome (自动书). A standalone entertainment item, like the Hunter's Tome
/// and the Crystal Matrix Tome, it is not part of the ordinary companion command/build system.
///
/// Left click: summon this character-and-world's one automation companion, or temporarily recall it
/// when it is already active. Recall captures the live planner/position memory into the saved
/// profile; the next summon resumes that same run instead of replacing or resetting it.
///
/// Right click: toggle the local camera between the real player and the active automation companion.
/// This only changes the view; local-player identity and controls remain with the real player.
///
/// Single-player only, sharing the project's existing virtual-Player boundary.
/// </summary>
public sealed class CompanionAutomationBook : ModItem
{
    // The Lunar Flare book texture is the one vanilla book icon the other six books have not
    // claimed. SpellTome is the only fallback if a runtime ever lacks that constant.
    public override string Texture => "MoreAIPlayer/图片/自动书";

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
            ToggleCamera(player, data);
        else
            ToggleSummon(player, data);
        return true;
    }

    /// <summary>Left click: create/summon/re-summon, or recall the live automation companion.</summary>
    private static void ToggleSummon(Player player, CompanionPlayer data)
    {
        // The run already finished in this world. Its saved profile remains automation-owned, and
        // a second automation companion must never be created here.
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
        else if (CompanionEntitySystem.FindByProfile(player.whoAmI, profile.Id) is CompanionInstance active)
        {
            active.AutoBrain.CapturePersistentMemory(profile.AutoState, active);
            CompanionAutomationCameraSystem.StopFollowing(profile.Id);
            CompanionEntitySystem.Dismiss(active.Slot);
            Message("AutomationRecalled", InfoColor, profile.Name);
            return;
        }

        // The binding says this run is unfinished, therefore this profile must enter the
        // automation brain. Older/damaged saves can retain the binding while losing or clearing
        // Running; repair that state in place so the book can never summon an ordinary follower.
        profile.EnsureAutomationRunning();

        // The observer's cursor is not an automation command. Older builds spawned a fresh run at
        // Main.MouseWorld, so an innocent left click could put the synthetic player inside a wall
        // in an established base. Resume near its own saved position when possible; otherwise use
        // a collision-checked standing point near the real player.
        Vector2 spawn = AutoSpawnResolver.Resolve(player, profile.AutoState);
        if (CompanionEntitySystem.Summon(player, profile, spawn))
            Message("AutomationSummoned", InfoColor, profile.Name);
        else
            Message("AutomationNoFreeSlot", WarnColor);
    }

    /// <summary>Right click: follow the live automation companion, or return to the real player.</summary>
    private static void ToggleCamera(Player player, CompanionPlayer data)
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
            Message("AutomationNotCreated", WarnColor);
            return;
        }

        if (CompanionAutomationCameraSystem.IsFollowing(player.whoAmI, profile.Id))
        {
            CompanionAutomationCameraSystem.StopFollowing(profile.Id);
            Message("AutomationCameraStopped", InfoColor);
        }
        else
        {
            CompanionAutomationCameraSystem.Follow(player.whoAmI, profile.Id, active.Entity.Center);
            Message("AutomationCameraStarted", InfoColor, profile.Name);
        }
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
