using System;
using Terraria;
using Terraria.ID;
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
    // One automation decision may finish its entire requested craft immediately, but a malformed
    // zero-cost/modded recipe must never monopolise the main thread forever.
    private const int MaximumCraftsPerTransaction = 999;

    public static bool IsAvailable =>
        CompanionFaultGuard.IsAvailable(CompanionFaultGuard.AutomationCraftingLane);

    /// <summary>
    /// Attempts to craft one batch of <paramref name="targetItemType"/> at the companion's current
    /// position. Returns true only if a real craft happened (ingredients consumed, result added to
    /// the companion's backpack). There is no UI emulation, thinking animation or artificial wait.
    /// </summary>
    public static bool TryCraft(CompanionInstance instance, CompanionProfile profile, int targetItemType)
    {
        int target = AutoInventory.Count(profile, targetItemType) + 1;
        return TryCraftUntil(instance, profile, targetItemType, target);
    }

    /// <summary>Completes as many recipe batches as are needed to reach <paramref name="targetTotal"/>
    /// in one non-blocking transaction. It claims no movement or item-use input, so a companion may
    /// keep its current velocity while recipe hooks and legal material consumption run immediately.</summary>
    public static bool TryCraftUntil(CompanionInstance instance, CompanionProfile profile,
        int targetItemType, int targetTotal)
    {
        if (AutoInventory.Count(profile, targetItemType) >= targetTotal)
            return false;

        bool crafted = false;
        CompanionFaultGuard.RunCritical(CompanionFaultGuard.AutomationCraftingLane,
            () => crafted = TryCraftCore(instance.Entity, profile, targetItemType, targetTotal));
        return crafted;
    }

    /// <summary>Converts every currently craftable batch of one result type in this same tick.</summary>
    public static bool TryCraftAllAvailable(CompanionInstance instance, CompanionProfile profile,
        int targetItemType) => TryCraftUntil(instance, profile, targetItemType, int.MaxValue);

    private static bool TryCraftCore(Player me, CompanionProfile profile, int targetItemType,
        int targetTotal)
    {
        bool crafted = false;
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

            for (int operation = 0; operation < MaximumCraftsPerTransaction &&
                AutoInventory.Count(profile, targetItemType) < targetTotal; operation++)
            {
                // Refresh after every batch because ingredients, recipe groups and hook-controlled
                // availability may have changed. Main.LocalPlayer is still the companion in scope.
                me.AdjTiles();
                Recipe.FindRecipes();

                Recipe recipe = null;
                long bestCost = long.MaxValue;
                for (int i = 0; i < Main.numAvailableRecipes; i++)
                {
                    Recipe candidate = Main.recipe[Main.availableRecipe[i]];
                    if (candidate.createItem.type != targetItemType)
                        continue;
                    long cost = RecipeCost(candidate, profile);
                    if (cost >= bestCost)
                        continue;
                    bestCost = cost;
                    recipe = candidate;
                }
                if (recipe is null)
                    break;

                Item result = recipe.createItem.Clone();
                result.Prefix(-1);
                // Never consume ingredients unless the complete result has a legal destination.
                if (!AutoInventory.CanFit(profile, result))
                    break;

                int before = AutoInventory.Count(profile, targetItemType);
                recipe.Create();                              // ConsumeItem and recipe-group rules
                RecipeLoader.OnCraft(result, recipe, result); // mod OnCraft hooks
                if (!AutoInventory.Give(profile, result) ||
                    AutoInventory.Count(profile, targetItemType) <= before)
                    break;
                crafted = true;
            }
            return crafted;
        }
        finally
        {
            Main.numAvailableRecipes = savedNum;
            if (savedList is not null)
                Array.Copy(savedList, Main.availableRecipe, savedNum);
        }
    }

    /// <summary>Available recipes are not equivalent. Prefer the route which preserves the live
    /// campaign reserve and consumes common low-value inputs. A reserve penalty is deliberately
    /// finite: if the requested product is itself the current hard requirement, crafting may still
    /// use the only legal recipe instead of deadlocking forever.</summary>
    private static long RecipeCost(Recipe recipe, CompanionProfile profile)
    {
        long cost = 0;
        foreach (Item required in recipe.requiredItem)
        {
            if (required is null || required.IsAir || required.stack <= 0)
                continue;
            int held = AutoInventory.Count(profile, required.type);
            int keep = AutoStorageController.KeepThreshold(required.type, profile.AutoState);
            int reserveShortfall = System.Math.Max(0, keep - (held - required.stack));
            cost += reserveShortfall * 1_000_000L;
            cost += required.stack * (100L + System.Math.Max(0, required.value) / 100L);
            cost += required.rare > ItemRarityID.White
                ? required.stack * (long)required.rare * 10_000L : 0L;
        }
        return cost;
    }
}
