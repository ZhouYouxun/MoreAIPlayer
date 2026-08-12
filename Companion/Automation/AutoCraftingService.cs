using System;
using Terraria;
using Terraria.ModLoader;

namespace MoreAIPlayer.Companion.Automation;

/// <summary>
/// Crafts for the automation companion using vanilla's own recipe machinery, so material
/// consumption, recipe groups (wood/iron variants…), station and world conditions, and every
/// RecipeLoader hook (ConsumeItem, OnCraft) run exactly as they would for a human (§7.4). Nothing
/// is synthesised: it enters the companion's virtual-player scope, lets <see cref="Recipe.FindRecipes"/>
/// decide what is actually craftable where the companion stands, and consumes the real ingredients.
///
/// The real player's recipe listing is snapshotted and restored around the call so their crafting
/// UI is never left showing the companion's recipes.
/// </summary>
public static class AutoCraftingService
{
    public static bool IsAvailable =>
        CompanionFaultGuard.IsAvailable(CompanionFaultGuard.AutomationCraftingLane);

    /// <summary>
    /// Attempts to craft one batch of <paramref name="targetItemType"/> at the companion's current
    /// position. Returns true only if a real craft happened (ingredients consumed, result added to
    /// the companion's backpack). Safe to call every so often; it is throttled by its caller.
    /// </summary>
    public static bool TryCraft(CompanionInstance instance, CompanionProfile profile, int targetItemType)
    {
        bool crafted = false;
        // Crafting consumes real materials and invokes arbitrary RecipeLoader hooks. A fault must
        // trip only this lane for the session; repeatedly re-entering the same half-mutating hook is
        // riskier than cooling the plan down and surfacing a blocked reason.
        CompanionFaultGuard.RunCritical(CompanionFaultGuard.AutomationCraftingLane,
            () => crafted = TryCraftCore(instance.Entity, profile, targetItemType));
        return crafted;
    }

    private static bool TryCraftCore(Player me, CompanionProfile profile, int targetItemType)
    {
        // Snapshot the local player's available-recipe listing so the crafting UI is untouched.
        int savedNum = Main.numAvailableRecipes;
        int[] savedList = null;
        if (savedNum > 0)
        {
            savedList = new int[savedNum];
            Array.Copy(Main.availableRecipe, savedList, savedNum);
        }

        try
        {
            using VirtualPlayerExecutionContext.Scope scope =
                VirtualPlayerExecutionContext.Enter(me, me.Center, false, false);

            // FindRecipes reads Main.LocalPlayer (== the companion in scope): its inventory, its
            // adjacent stations and the world conditions. That is the authoritative "can I craft
            // this here, right now" test.
            me.AdjTiles();
            Recipe.FindRecipes();

            for (int i = 0; i < Main.numAvailableRecipes; i++)
            {
                Recipe recipe = Main.recipe[Main.availableRecipe[i]];
                if (recipe.createItem.type != targetItemType)
                    continue;

                Item result = recipe.createItem.Clone();
                result.Prefix(-1);
                // Never consume ingredients unless the complete result has a legal destination.
                // The old path could report success after TryAddToBackpack failed and silently lose
                // the crafted item — exactly the kind of partial transition hardening must forbid.
                if (!AutoInventory.CanFit(profile, result))
                    return false;
                recipe.Create();                            // consume ingredients + ConsumeItem hooks
                RecipeLoader.OnCraft(result, recipe, result); // OnCraft hooks (mod effects)

                return AutoInventory.Give(profile, result);
            }

            return false;
        }
        finally
        {
            Main.numAvailableRecipes = savedNum;
            if (savedList is not null)
                Array.Copy(savedList, Main.availableRecipe, savedNum);
        }
    }
}
