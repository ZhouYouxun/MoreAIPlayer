using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;

namespace MoreAIPlayer.Companion.Automation;

/// <summary>
/// Converts mined ore into the first self-earned loadout. Crafting always goes through
/// <see cref="AutoCraftingService"/>; this controller only chooses recipes and moves items between
/// the companion's own backpack/equipment slots, the same inventory organization a player does.
/// </summary>
public static class AutoGearController
{
    private const int BetterThanCopperPickPower = 40;
    private const int OreBatch = 60;

    private static readonly int[] Picks =
    [
        ItemID.PlatinumPickaxe, ItemID.GoldPickaxe, ItemID.TungstenPickaxe, ItemID.SilverPickaxe,
        ItemID.LeadPickaxe, ItemID.IronPickaxe
    ];

    private static readonly int[] Swords =
    [
        ItemID.PlatinumBroadsword, ItemID.GoldBroadsword, ItemID.TungstenBroadsword,
        ItemID.SilverBroadsword, ItemID.LeadBroadsword, ItemID.IronBroadsword,
        ItemID.TinBroadsword, ItemID.CopperBroadsword
    ];

    private static readonly int[][] ArmourBySlot =
    [
        [ItemID.PlatinumHelmet, ItemID.GoldHelmet, ItemID.TungstenHelmet, ItemID.SilverHelmet,
            ItemID.LeadHelmet, ItemID.IronHelmet, ItemID.TinHelmet, ItemID.CopperHelmet],
        [ItemID.PlatinumChainmail, ItemID.GoldChainmail, ItemID.TungstenChainmail, ItemID.SilverChainmail,
            ItemID.LeadChainmail, ItemID.IronChainmail, ItemID.TinChainmail, ItemID.CopperChainmail],
        [ItemID.PlatinumGreaves, ItemID.GoldGreaves, ItemID.TungstenGreaves, ItemID.SilverGreaves,
            ItemID.LeadGreaves, ItemID.IronGreaves, ItemID.TinGreaves, ItemID.CopperGreaves]
    ];

    public static bool HasBasicMetalLoadout(CompanionProfile profile)
    {
        bool pick = AutoInventory.BestPickaxe(profile).pick >= BetterThanCopperPickPower;
        bool weapon = AutoRiskPolicy.HasAdequateEarlyWeapon(profile);
        bool armour = !profile.Armor[0].IsAir && !profile.Armor[1].IsAir && !profile.Armor[2].IsAir;
        return pick && weapon && armour;
    }

    /// <summary>Consumes one legally owned permanent-stat item per tick and records the independent
    /// automation vitals. This bypasses no acquisition gate; it only replaces the UI click a human
    /// would perform after the item is already in the companion's own inventory.</summary>
    public static bool ConsumePermanentUpgrade(Player me, CompanionProfile profile)
    {
        AutoCompanionState state = profile.AutoState;
        if (state is null)
            return false;

        if (state.LifeCrystals < 15 && ConsumeOne(profile, ItemID.LifeCrystal))
        {
            state.LifeCrystals++;
            me.statLife = System.Math.Min(state.MaxLife(), me.statLife + 20);
            return true;
        }
        if (state.ManaCrystals < 9 && ConsumeOne(profile, ItemID.ManaCrystal))
        {
            state.ManaCrystals++;
            me.statMana = System.Math.Min(state.MaxMana(), me.statMana + 20);
            return true;
        }
        if (state.LifeCrystals >= 15 && state.LifeFruits < 20 && ConsumeOne(profile, ItemID.LifeFruit))
        {
            state.LifeFruits++;
            me.statLife = System.Math.Min(state.MaxLife(), me.statLife + 5);
            return true;
        }
        return false;
    }

    private static bool ConsumeOne(CompanionProfile profile, int itemType)
    {
        foreach (Item[] inventory in new[] { profile.Hotbar, profile.Backpack })
        {
            foreach (Item item in inventory)
            {
                if (item.IsAir || item.type != itemType)
                    continue;
                if (--item.stack <= 0)
                    item.TurnToAir();
                return true;
            }
        }
        return false;
    }

    public static AutoTaskResult EnsureBasicMetalLoadout(AutoContext ctx)
    {
        OrganizeOwnedGear(ctx.Me, ctx.Profile);
        if (HasBasicMetalLoadout(ctx.Profile))
            return AutoTaskResult.Succeeded;

        // Smelt every real ore stack before deciding that another mining trip is necessary.
        if (AutoResourceController.CountBasicOre(ctx.Profile) > 0)
        {
            AutoTaskResult smelting = AutoStationController.SmeltOre(
                ctx, ctx.State.Furnace, AutoProgressionPlanner.SmeltableBarItems);
            if (smelting != AutoTaskResult.RetryLater)
                return smelting;
            // One or two leftover ore can be below every bar recipe's batch size. They are not a
            // reason to suppress crafting from the bars already earned.
        }

        if (ctx.State.Anvil is Point anvil)
        {
            if (!Near(ctx.Me, anvil))
            {
                ctx.Navigator.Navigate(ctx.Me, TileCenter(anvil));
                return AutoTaskResult.Running;
            }

            if (TryCraftNextMissing(ctx))
                return AutoTaskResult.Running;
            if (!AutoCraftingService.IsAvailable)
                return AutoTaskResult.Faulted;
        }

        // No currently available bar recipe could close the next loadout gap. Mine another real
        // batch instead of spinning forever beside the anvil with incompatible mixed bars.
        return AutoResourceController.MineBasicOre(ctx, OreBatch);
    }

    private static bool TryCraftNextMissing(AutoContext ctx)
    {
        if (AutoInventory.BestPickaxe(ctx.Profile).pick < BetterThanCopperPickPower && TryAny(ctx, Picks))
            return true;
        // Any equivalent world/mod drop closes this gap. Only craft a prescribed fallback when the
        // actual held loadout does not already meet the capability threshold.
        if (!AutoRiskPolicy.HasAdequateEarlyWeapon(ctx.Profile) && TryAny(ctx, Swords))
            return true;

        for (int slot = 0; slot < ArmourBySlot.Length; slot++)
        {
            if (ctx.Profile.Armor[slot].IsAir && TryAny(ctx, ArmourBySlot[slot]))
                return true;
        }
        return false;
    }

    private static bool TryAny(AutoContext ctx, int[] itemTypes)
    {
        foreach (int type in itemTypes)
        {
            if (AutoCraftingService.TryCraft(ctx.Instance, ctx.Profile, type))
                return true;
        }
        return false;
    }

    /// <summary>Continuously equips real upgrades, including lucky chest/drop weapons. Combat slots
    /// are ranked by the evaluator rather than item id, with a small diversity bonus so a close,
    /// ranged, magic or whip sidegrade can coexist when its value is genuinely competitive.</summary>
    public static void OrganizeOwnedGear(Player me, CompanionProfile profile)
    {
        MoveBestBackpackItem(profile, item => item.pick >= BetterThanCopperPickPower,
            CompanionProfile.PickSlot, compare: item => item.pick);
        MoveBestBackpackItem(profile, item => item.axe > 0,
            CompanionProfile.AxeSlot, compare: item => item.axe);
        MoveBestBackpackItem(profile, item => item.hammer > 0,
            CompanionProfile.HammerSlot, compare: item => item.hammer);
        MoveBestBackpackItem(profile, CompanionSlotRules.IsHealingPotion,
            CompanionProfile.HealingPotionSlot, compare: item => item.healLife);
        MoveBestBackpackItem(profile, CompanionSlotRules.IsManaPotion,
            CompanionProfile.ManaPotionSlot, compare: item => item.healMana);
        MoveBestBackpackItem(profile, CompanionWeaponBehaviors.IsMinionStaff,
            CompanionProfile.SummonSlot, compare: SummonWeaponScore);
        MoveBestBackpackItem(profile, CompanionWeaponBehaviors.IsSentryStaff,
            CompanionProfile.SentrySlot, compare: SummonWeaponScore);
        MoveBestBackpackItemToArray(profile, item => item.fishingPole > 0,
            profile.Rod, item => item.fishingPole);
        MoveBestBackpackItemToArray(profile, CompanionSlotRules.IsCatchingTool,
            profile.Net, item => item.rare * 1000 + item.value);
        MoveBestBackpackItemToArray(profile, CompanionSlotRules.IsGrapplingHook,
            profile.Hook, item => item.rare * 1000 + item.value);
        MoveBestBackpackItemToArray(profile, CompanionSlotRules.IsMount,
            profile.Mount, item => item.rare * 1000 + item.value);
        OptimizeCombatSlots(me, profile);
        DiscardOneObsoleteWeapon(profile);

        for (int slot = 0; slot < 3; slot++)
        {
            int armourSlot = slot;
            MoveBestArmour(profile, armourSlot, profile.AutoState?.PreferredClass ?? AutoCombatClass.Flexible);
        }
        OptimizeAccessories(profile);
    }

    private static void MoveBestBackpackItemToArray(CompanionProfile profile,
        System.Func<Item, bool> match, Item[] destination, System.Func<Item, int> compare)
    {
        int best = -1;
        int score = compare(destination[0]);
        for (int i = 0; i < profile.Backpack.Length; i++)
        {
            Item item = profile.Backpack[i];
            if (item.IsAir || !match(item) || compare(item) <= score)
                continue;
            score = compare(item);
            best = i;
        }
        if (best >= 0)
            (destination[0], profile.Backpack[best]) = (profile.Backpack[best], destination[0]);
    }

    private static void OptimizeAccessories(CompanionProfile profile)
    {
        int unlocked = CompanionSlotRules.UnlockedAccessorySlots();
        System.Collections.Generic.HashSet<int> equippedTypes = [];
        for (int slot = 0; slot < unlocked && slot < profile.Accessories.Length; slot++)
        {
            Item current = profile.Accessories[slot];
            if (!current.IsAir)
                equippedTypes.Add(current.type);
        }

        for (int target = 0; target < unlocked && target < profile.Accessories.Length; target++)
        {
            Item current = profile.Accessories[target];
            int currentScore = AccessoryScore(current);
            int best = -1;
            int bestScore = currentScore;
            for (int i = 0; i < profile.Backpack.Length; i++)
            {
                Item candidate = profile.Backpack[i];
                if (candidate.IsAir || !candidate.accessory || equippedTypes.Contains(candidate.type))
                    continue;
                int score = AccessoryScore(candidate);
                if (score <= bestScore)
                    continue;
                bestScore = score;
                best = i;
            }
            if (best < 0)
                continue;
            if (!current.IsAir)
                equippedTypes.Remove(current.type);
            (profile.Accessories[target], profile.Backpack[best]) =
                (profile.Backpack[best], profile.Accessories[target]);
            equippedTypes.Add(profile.Accessories[target].type);
        }
    }

    private static int AccessoryScore(Item item)
    {
        if (item is null || item.IsAir || !item.accessory)
            return 0;
        int rarity = System.Math.Max(0, item.rare);
        int valueBand = item.value <= 0 ? 0 : (int)System.MathF.Log10(item.value + 1) * 8;
        int wingBonus = item.wingSlot > 0 ? 180 : 0;
        return item.defense * 30 + rarity * 18 + valueBand + wingBonus;
    }

    private static int SummonWeaponScore(Item item) => item is null || item.IsAir
        ? 0
        : item.damage * 100 + System.Math.Max(0, item.rare) * 10 + (item.shootSpeed > 0f ? 5 : 0);

    private static void OptimizeCombatSlots(Player me, CompanionProfile profile)
    {
        System.Collections.Generic.HashSet<AutoWeaponRole> selectedRoles = [];
        for (int targetSlot = CompanionProfile.FirstCombatSlot;
            targetSlot < CompanionProfile.EndCombatSlot;
            targetSlot++)
        {
            int bestHotbar = -1;
            int bestBackpack = -1;
            int bestAdjustedScore = 0;

            for (int slot = targetSlot; slot < CompanionProfile.EndCombatSlot; slot++)
                Consider(profile.Hotbar[slot], slot, -1);
            for (int slot = 0; slot < profile.Backpack.Length; slot++)
                Consider(profile.Backpack[slot], -1, slot);

            if (bestHotbar >= 0 && bestHotbar != targetSlot)
                (profile.Hotbar[targetSlot], profile.Hotbar[bestHotbar]) =
                    (profile.Hotbar[bestHotbar], profile.Hotbar[targetSlot]);
            else if (bestBackpack >= 0)
                (profile.Hotbar[targetSlot], profile.Backpack[bestBackpack]) =
                    (profile.Backpack[bestBackpack], profile.Hotbar[targetSlot]);

            AutoWeaponRole equippedRole = AutoWeaponEvaluator.Role(profile.Hotbar[targetSlot]);
            if (equippedRole != AutoWeaponRole.None)
                selectedRoles.Add(equippedRole);

            void Consider(Item item, int hotbar, int backpack)
            {
                if (!IsWeapon(item))
                    return;
                int score = AutoWeaponEvaluator.CombatScore(me, item);
                AutoWeaponRole role = AutoWeaponEvaluator.Role(item);
                int adjusted = selectedRoles.Contains(role) ? score : score * 108 / 100;
                if (adjusted <= bestAdjustedScore)
                    return;
                bestAdjustedScore = adjusted;
                bestHotbar = hotbar;
                bestBackpack = backpack;
            }
        }
    }

    /// <summary>The automation profile has finite long-run storage. Once the three combat slots
    /// contain the best owned options, remove one clearly dominated, unfavourited backpack weapon
    /// per tick instead of allowing decades of obsolete drops to deadlock progression.</summary>
    private static void DiscardOneObsoleteWeapon(CompanionProfile profile)
    {
        int discard = -1;
        int weakest = int.MaxValue;
        for (int i = 0; i < profile.Backpack.Length; i++)
        {
            Item item = profile.Backpack[i];
            if (!AutoWeaponEvaluator.IsObsolete(profile, item))
                continue;
            int score = AutoWeaponEvaluator.CombatScore(item);
            if (score >= weakest)
                continue;
            weakest = score;
            discard = i;
        }
        if (discard >= 0)
            profile.Backpack[discard].TurnToAir();
    }

    private static void MoveBestBackpackItem(CompanionProfile profile, System.Func<Item, bool> match,
        int hotbarSlot, System.Func<Item, int> compare)
    {
        int best = -1;
        int score = compare(profile.Hotbar[hotbarSlot]);
        for (int i = 0; i < profile.Backpack.Length; i++)
        {
            Item item = profile.Backpack[i];
            if (item.IsAir || !match(item) || compare(item) <= score)
                continue;
            score = compare(item);
            best = i;
        }
        if (best < 0)
            return;

        (profile.Hotbar[hotbarSlot], profile.Backpack[best]) =
            (profile.Backpack[best], profile.Hotbar[hotbarSlot]);
    }

    private static void MoveBestArmour(CompanionProfile profile, int slot, AutoCombatClass preferred)
    {
        int best = -1;
        int bestScore = AutoClassController.ArmourScore(profile.Armor[slot], preferred);
        for (int i = 0; i < profile.Backpack.Length; i++)
        {
            Item item = profile.Backpack[i];
            int score = AutoClassController.ArmourScore(item, preferred);
            if (item.IsAir || !FitsArmourSlot(item, slot) || score <= bestScore)
                continue;
            bestScore = score;
            best = i;
        }
        if (best >= 0)
            (profile.Armor[slot], profile.Backpack[best]) = (profile.Backpack[best], profile.Armor[slot]);
    }

    private static bool IsWeapon(Item item) => item is not null && !item.IsAir &&
        CompanionSlotRules.IsRegularWeapon(item);

    private static bool FitsArmourSlot(Item item, int slot) => slot switch
    {
        0 => item.headSlot >= 0,
        1 => item.bodySlot >= 0,
        2 => item.legSlot >= 0,
        _ => false
    };

    private static bool Near(Player me, Point tile) =>
        Vector2.DistanceSquared(me.Center, TileCenter(tile)) <= 48f * 48f;

    private static Vector2 TileCenter(Point tile) => new(tile.X * 16f + 8f, tile.Y * 16f + 8f);
}
