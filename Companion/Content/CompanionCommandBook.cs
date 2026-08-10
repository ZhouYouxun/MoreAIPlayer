using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.Localization;
using Terraria.ModLoader;

namespace MoreAIPlayer.Companion.Content;

/// <summary>
/// Second companion book. Left click drops a movement waypoint at the cursor: companions path
/// to it, then hold there; clicking near an existing waypoint cancels it.
///
/// Right click is not an item use at all any more — holding it opens
/// <see cref="UI.CompanionModeWheel"/>, which owns mode switching now that there are six modes
/// to choose between.
/// </summary>
public sealed class CompanionCommandBook : ModItem
{
    // Clicking this close to a standing waypoint cancels it instead of moving it.
    private const float CancelRadius = 3f * 16f;

    public override string Texture => $"Terraria/Images/Item_{ItemID.WaterBolt}";

    public override void SetDefaults()
    {
        Item.width = 28;
        Item.height = 30;
        Item.useStyle = ItemUseStyleID.HoldUp;
        Item.useAnimation = 20;
        Item.useTime = 20;
        Item.rare = ItemRarityID.Green;
        Item.value = 0;
        Item.noUseGraphic = false;
        Item.autoReuse = false;
    }

    // Right click belongs to the mode wheel, which reads the raw button itself. Declining alt
    // use here keeps vanilla from also starting an item animation underneath the wheel.
    public override bool AltFunctionUse(Player player) => false;

    public override bool? UseItem(Player player)
    {
        if (player.whoAmI != Main.myPlayer)
            return true;

        if (UI.CompanionModeWheel.Active)
            return true;

        if (Main.netMode != NetmodeID.SinglePlayer)
        {
            Main.NewText(Language.GetTextValue("Mods.MoreAIPlayer.CompanionCommand.SingleplayerOnly"), 255, 160, 120);
            return true;
        }

        List<CompanionInstance> squad = OwnedActiveCompanions(player.whoAmI);
        if (squad.Count == 0)
        {
            Main.NewText(Language.GetTextValue("Mods.MoreAIPlayer.CompanionCommand.NoCompanion"), 255, 160, 120);
            return true;
        }

        PlaceOrCancelWaypoint(player, squad);
        return true;
    }

    private static void PlaceOrCancelWaypoint(Player player, List<CompanionInstance> squad)
    {
        Vector2 spot = Main.MouseWorld;

        // A click near a standing marker clears it rather than planting a second one.
        foreach (CompanionInstance instance in squad)
        {
            if (instance.Waypoint is Vector2 existing && Vector2.Distance(existing, spot) <= CancelRadius)
            {
                foreach (CompanionInstance member in squad)
                {
                    member.Waypoint = null;
                    member.WaypointTimeLeft = 0;
                }
                CompanionEntitySystem.SpawnRecallDustBurst(existing);
                return;
            }
        }

        foreach (CompanionInstance instance in squad)
        {
            instance.Waypoint = spot;
            instance.WaypointTimeLeft = CompanionInstance.WaypointLifetime;
        }
        CompanionEntitySystem.SpawnWaypointPing(spot);
    }

    public static List<CompanionInstance> OwnedActiveCompanions(int ownerWhoAmI)
    {
        List<CompanionInstance> owned = [];
        foreach (CompanionInstance instance in CompanionEntitySystem.All)
        {
            // The automation companion ignores the command book entirely — it never joins the
            // waypoint/mode squad. Excluding it here also keeps it out of the mode/builder wheels
            // and the builder book, which all read this list.
            if (instance.OwnerWhoAmI == ownerWhoAmI && instance.Entity.active && !instance.IsAutoIdentity)
                owned.Add(instance);
        }
        return owned;
    }

    public override void AddRecipes()
    {
        CreateRecipe()
            .AddIngredient(ItemID.DirtBlock)
            .Register();
    }
}
