using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;

namespace MoreAIPlayer.Companion.Automation;

/// <summary>Deterministic mining/crafting ladder for class-directed armour. It first secures the
/// pick/station chain, then crafts the head variant matching the strongest owned weapon.</summary>
public static class AutoCampaignGearController
{
    private static long nextMechanicalRecoverySummonTick;
    private static readonly int[] HellstoneTiles = [TileID.Hellstone];
    private static readonly int[] HellstoneItems = [ItemID.Hellstone];
    private static readonly int[] ObsidianTiles = [TileID.Obsidian];
    private static readonly int[] ObsidianItems = [ItemID.Obsidian];
    private static readonly int[] Tier1Tiles = [TileID.Cobalt, TileID.Palladium];
    private static readonly int[] Tier1Ore = [ItemID.CobaltOre, ItemID.PalladiumOre];
    private static readonly int[] Tier1Bars = [ItemID.CobaltBar, ItemID.PalladiumBar];
    private static readonly int[] Tier2Tiles = [TileID.Mythril, TileID.Orichalcum];
    private static readonly int[] Tier2Ore = [ItemID.MythrilOre, ItemID.OrichalcumOre];
    private static readonly int[] Tier2Bars = [ItemID.MythrilBar, ItemID.OrichalcumBar];
    private static readonly int[] Tier3Tiles = [TileID.Adamantite, TileID.Titanium];
    private static readonly int[] Tier3Ore = [ItemID.AdamantiteOre, ItemID.TitaniumOre];
    private static readonly int[] Tier3Bars = [ItemID.AdamantiteBar, ItemID.TitaniumBar];
    private static readonly int[] ChlorophyteTiles = [TileID.Chlorophyte];
    private static readonly int[] ChlorophyteOre = [ItemID.ChlorophyteOre];
    private static readonly int[] ChlorophyteBars = [ItemID.ChlorophyteBar];

    public static bool NeedsMiningUpgrade(AutoContext ctx)
    {
        if (!Main.hardMode)
        {
            if (ctx.State.CampaignStage != AutoCampaignStage.WallOfFlesh)
                return false;
            int preHardmodeDefense = ctx.Profile.Armor[0].defense + ctx.Profile.Armor[1].defense +
                ctx.Profile.Armor[2].defense;
            bool needsWeaponReserve = AutoCampaignController.NeedsLoadoutWork(ctx) &&
                AutoInventory.Count(ctx.Profile, ItemID.HellstoneBar) < 15;
            return AutoInventory.BestPickaxe(ctx.Profile).pick < 100 ||
                preHardmodeDefense < 20 || needsWeaponReserve;
        }
        int lateDefense = ctx.State.PreferredClass switch
        {
            AutoCombatClass.Melee => 42,
            AutoCombatClass.Ranged => 36,
            _ => 30
        };
        int defenseTarget = ctx.State.CampaignStage >= AutoCampaignStage.Plantera ? lateDefense : 28;
        // A 200%-power Drax/Pickaxe Axe consumes drops from all three mechanical bosses. Requiring
        // it while the campaign is still trying to kill those bosses is a circular hard gate.
        int pickTarget = ctx.State.CampaignStage >= AutoCampaignStage.Plantera ? 200 : 150;
        int defense = ctx.Profile.Armor[0].defense + ctx.Profile.Armor[1].defense + ctx.Profile.Armor[2].defense;
        return AutoInventory.BestPickaxe(ctx.Profile).pick < pickTarget || defense < defenseTarget;
    }

    public static AutoTaskResult Upgrade(AutoContext ctx)
    {
        AutoInventory.TryCollectNearby(ctx.Instance, ctx.Profile, 10f, null);
        AutoClassController.Refresh(ctx.Me, ctx.Profile);
        AutoGearController.OrganizeOwnedGear(ctx.Me, ctx.Profile);

        int pick = AutoInventory.BestPickaxe(ctx.Profile).pick;
        if (pick < 65)
        {
            if (ctx.State.Anvil is Point anvil && !Near(ctx.Me, anvil))
                return AutoWorldTravelController.TravelTo(ctx, TileCenter(anvil), 4f);
            if (TryCraftNear(ctx, ctx.State.Anvil,
                WorldGen.crimson ? ItemID.DeathbringerPickaxe : ItemID.NightmarePickaxe))
                return AutoTaskResult.Running;
            return AutoExplorationController.Explore(ctx);
        }
        if (pick < 100)
            return BuildMoltenPick(ctx);
        if (!Main.hardMode)
            return ctx.State.CampaignStage == AutoCampaignStage.WallOfFlesh
                ? BuildMoltenPreparation(ctx) : AutoTaskResult.Succeeded;

        AutoTaskResult altars = EnsureHardmodeOreSeeded(ctx);
        if (altars != AutoTaskResult.Succeeded)
            return altars;

        if (pick < 110)
            return MineSmeltCraftTier(ctx, Tier1Tiles, Tier1Ore, Tier1Bars, 80,
                [ItemID.CobaltPickaxe, ItemID.PalladiumPickaxe], ctx.State.Anvil, Tier1Armour(ctx.State.PreferredClass));

        AutoTaskResult hardAnvil = EnsureHardmodeAnvil(ctx);
        if (hardAnvil != AutoTaskResult.Succeeded)
            return hardAnvil;
        if (pick < 150)
            return MineSmeltCraftTier(ctx, Tier2Tiles, Tier2Ore, Tier2Bars, 100,
                [ItemID.MythrilPickaxe, ItemID.OrichalcumPickaxe], ctx.State.HardmodeAnvil,
                Tier2Armour(ctx.State.PreferredClass));

        AutoTaskResult hardForge = EnsureHardmodeForge(ctx);
        if (hardForge != AutoTaskResult.Succeeded)
            return hardForge;
        if (pick < 200)
        {
            if (AllMechanicalBossesDown())
                return BuildMechanicalPick(ctx);
            return MineSmeltCraftTier(ctx, Tier3Tiles, Tier3Ore, Tier3Bars, 150,
                [ItemID.AdamantitePickaxe, ItemID.TitaniumPickaxe], ctx.State.HardmodeAnvil,
                Tier3Armour(ctx.State.PreferredClass), ctx.State.HardmodeForge);
        }

        if (ctx.State.CampaignStage >= AutoCampaignStage.Plantera)
            return MineSmeltCraftTier(ctx, ChlorophyteTiles, ChlorophyteOre, ChlorophyteBars, 180,
                [], ctx.State.HardmodeAnvil, ChlorophyteArmour(ctx.State.PreferredClass),
                ctx.State.HardmodeForge);
        return MineSmeltCraftTier(ctx, Tier3Tiles, Tier3Ore, Tier3Bars, 180,
            [], ctx.State.HardmodeAnvil, Tier3Armour(ctx.State.PreferredClass),
            ctx.State.HardmodeForge);
    }

    /// <summary>Uses the real Pwnhammer on three live evil altars so a fresh Hardmode world
    /// receives all three ore tiers. The pending coordinate makes the operation idempotent across
    /// recall, death and save/reload: progress is counted only after the altar tile is gone.</summary>
    private static AutoTaskResult EnsureHardmodeOreSeeded(AutoContext ctx)
    {
        if (ctx.State.AltarsBroken >= 3)
            return AutoTaskResult.Succeeded;

        if (ctx.State.PendingAltar is Point pending)
        {
            if (!WorldGen.InWorld(pending.X, pending.Y, 4) ||
                !Main.tile[pending.X, pending.Y].HasTile ||
                Main.tile[pending.X, pending.Y].TileType != TileID.DemonAltar)
            {
                ctx.State.PendingAltar = null;
                ctx.State.AltarsBroken++;
                AutoWorldIndexSystem.RestartScan();
                return ctx.State.AltarsBroken >= 3
                    ? AutoTaskResult.Succeeded : AutoTaskResult.Running;
            }
        }

        int hammer = AutoInventory.BestToolIndex(ctx.Me, item => item.hammer);
        if (hammer < 0 || ctx.Me.inventory[hammer].hammer < 80)
            return AutoExplorationController.Explore(ctx);

        Point? altar = ctx.State.PendingAltar ?? AutoWorldIndexSystem.FindNearest(
            ctx.Me.Center, [TileID.DemonAltar]);
        if (altar is not Point target)
            return AutoTaskResult.RetryLater;

        ctx.State.PendingAltar = target;
        ctx.TaskRuntime.SetActiveTarget(AutoGoalId.UpgradeClassLoadout, target);
        if (!ctx.Actuator.MoveAndWorkTile(ctx.Instance, ctx.Me, ctx.Navigator, hammer, target))
        {
            ctx.State.PendingAltar = null;
            ctx.TaskRuntime.RejectActiveTarget(41, 1800);
            return AutoTaskResult.AlternativeRequired;
        }
        return AutoTaskResult.Running;
    }

    private static AutoTaskResult BuildMoltenPick(AutoContext ctx)
    {
        if (AutoInventory.Count(ctx.Profile, ItemID.HellstoneBar) >= 20 &&
            TryCraftNear(ctx, ctx.State.Anvil, ItemID.MoltenPickaxe))
            return AutoTaskResult.Running;

        AutoTaskResult forge = EnsureHellforge(ctx);
        if (forge != AutoTaskResult.Succeeded)
            return forge;
        if (AutoInventory.Count(ctx.Profile, ItemID.Obsidian) < 20)
            return AutoResourceController.MineIndexedResource(ctx, ObsidianTiles, ObsidianItems, 20);
        if (AutoInventory.Count(ctx.Profile, ItemID.Hellstone) < 80 &&
            AutoInventory.Count(ctx.Profile, ItemID.HellstoneBar) < 20)
            return AutoResourceController.MineIndexedResource(ctx, HellstoneTiles, HellstoneItems, 80);

        AutoTaskResult smelt = AutoStationController.SmeltOre(ctx, ctx.State.Hellforge,
            [ItemID.HellstoneBar]);
        if (smelt == AutoTaskResult.Running)
            return smelt;
        return TryCraftNear(ctx, ctx.State.Anvil, ItemID.MoltenPickaxe)
            ? AutoTaskResult.Running : AutoTaskResult.RetryLater;
    }

    private static AutoTaskResult BuildMoltenPreparation(AutoContext ctx)
    {
        int defense = ctx.Profile.Armor[0].defense + ctx.Profile.Armor[1].defense +
            ctx.Profile.Armor[2].defense;
        bool needsArmour = defense < 20;
        bool needsWeaponReserve = AutoCampaignController.NeedsLoadoutWork(ctx) &&
            AutoInventory.Count(ctx.Profile, ItemID.HellstoneBar) < 15;
        if (!needsArmour && !needsWeaponReserve)
            return AutoTaskResult.Succeeded;

        int barsForArmourAndWeaponReserve = needsArmour ? 65 : 20;
        AutoTaskResult forge = EnsureHellforge(ctx);
        if (forge != AutoTaskResult.Succeeded)
            return forge;
        if (AutoInventory.Count(ctx.Profile, ItemID.Obsidian) < barsForArmourAndWeaponReserve)
            return AutoResourceController.MineIndexedResource(ctx, ObsidianTiles, ObsidianItems,
                barsForArmourAndWeaponReserve);
        if (AutoInventory.Count(ctx.Profile, ItemID.Hellstone) < barsForArmourAndWeaponReserve * 3 &&
            AutoInventory.Count(ctx.Profile, ItemID.HellstoneBar) < barsForArmourAndWeaponReserve)
            return AutoResourceController.MineIndexedResource(ctx, HellstoneTiles, HellstoneItems,
                barsForArmourAndWeaponReserve * 3);

        if (AutoInventory.Count(ctx.Profile, ItemID.HellstoneBar) < barsForArmourAndWeaponReserve)
        {
            AutoTaskResult smelt = AutoStationController.SmeltOre(ctx, ctx.State.Hellforge,
                [ItemID.HellstoneBar]);
            if (smelt != AutoTaskResult.RetryLater)
                return smelt;
        }
        return needsArmour
            ? CraftArmourNear(ctx, ctx.State.Anvil,
                [ItemID.MoltenHelmet, ItemID.MoltenBreastplate, ItemID.MoltenGreaves])
            : AutoTaskResult.Succeeded;
    }

    private static AutoTaskResult EnsureHellforge(AutoContext ctx)
    {
        if (LiveStation(ctx.State.Hellforge, TileID.Hellforge))
            return AutoTaskResult.Succeeded;
        if (!AutoInventory.Has(ctx.Profile, ItemID.Hellforge))
        {
            AutoTaskResult mine = AutoResourceController.MineIndexedResource(ctx,
                [TileID.Hellforge], [ItemID.Hellforge], 1);
            if (mine != AutoTaskResult.Succeeded)
                return mine;
        }
        return AutoStationController.EnsureStation(ctx, ItemID.Hellforge, TileID.Hellforge,
            3, ctx.State.WorkBench, ref ctx.State.Hellforge);
    }

    private static AutoTaskResult EnsureHardmodeAnvil(AutoContext ctx)
    {
        if (LiveStation(ctx.State.HardmodeAnvil, TileID.MythrilAnvil))
            return AutoTaskResult.Succeeded;
        if (!AutoInventory.Has(ctx.Profile, ItemID.MythrilAnvil) &&
            !AutoInventory.Has(ctx.Profile, ItemID.OrichalcumAnvil))
        {
            if (AutoResourceController.CountAny(ctx.Profile, Tier2Bars) < 10)
                return MineSmeltCraftTier(ctx, Tier2Tiles, Tier2Ore, Tier2Bars, 60, [],
                    ctx.State.Anvil, []);
            if (!TryCraftNear(ctx, ctx.State.Anvil, ItemID.MythrilAnvil) &&
                !TryCraftNear(ctx, ctx.State.Anvil, ItemID.OrichalcumAnvil))
                return AutoTaskResult.RetryLater;
        }
        int item = AutoInventory.Has(ctx.Profile, ItemID.MythrilAnvil)
            ? ItemID.MythrilAnvil : ItemID.OrichalcumAnvil;
        return AutoStationController.EnsureStation(ctx, item, TileID.MythrilAnvil,
            2, ctx.State.Anvil, ref ctx.State.HardmodeAnvil);
    }

    private static AutoTaskResult EnsureHardmodeForge(AutoContext ctx)
    {
        if (LiveStation(ctx.State.HardmodeForge, TileID.AdamantiteForge))
            return AutoTaskResult.Succeeded;
        if (!AutoInventory.Has(ctx.Profile, ItemID.AdamantiteForge) &&
            !AutoInventory.Has(ctx.Profile, ItemID.TitaniumForge))
        {
            if (!AutoInventory.Has(ctx.Profile, ItemID.Hellforge))
            {
                AutoTaskResult hellforge = EnsureHellforge(ctx);
                if (hellforge != AutoTaskResult.Succeeded)
                    return hellforge;
                // Mine the placed forge back only when it is needed as the recipe ingredient.
                if (ctx.State.Hellforge is Point placed)
                {
                    int pick = AutoInventory.BestToolIndex(ctx.Me, item => item.pick);
                    if (pick >= 0)
                    {
                        ctx.Actuator.MoveAndWorkTile(ctx.Instance, ctx.Me, ctx.Navigator, pick, placed);
                        ctx.State.Hellforge = null;
                        return AutoTaskResult.Running;
                    }
                }
            }
            if (AutoResourceController.CountAny(ctx.Profile, Tier3Ore) < 30)
                return AutoResourceController.MineIndexedResource(ctx, Tier3Tiles, Tier3Ore, 30);
            if (!TryCraftNear(ctx, ctx.State.HardmodeAnvil, ItemID.AdamantiteForge) &&
                !TryCraftNear(ctx, ctx.State.HardmodeAnvil, ItemID.TitaniumForge))
                return AutoTaskResult.RetryLater;
        }
        int itemType = AutoInventory.Has(ctx.Profile, ItemID.AdamantiteForge)
            ? ItemID.AdamantiteForge : ItemID.TitaniumForge;
        return AutoStationController.EnsureStation(ctx, itemType, TileID.AdamantiteForge,
            3, ctx.State.HardmodeAnvil, ref ctx.State.HardmodeForge);
    }

    /// <summary>Closes the post-mechanical mining loop with the real vanilla 200%-pick recipe.
    /// If its unique boss materials were lost to a full backpack/death, re-fight only the missing
    /// material's boss at night instead of permanently blocking the campaign.</summary>
    private static AutoTaskResult BuildMechanicalPick(AutoContext ctx)
    {
        if (AutoInventory.BestPickaxe(ctx.Profile).pick >= 200)
            return AutoTaskResult.Succeeded;
        if (ctx.State.HardmodeAnvil is Point anvil && !Near(ctx.Me, anvil))
            return AutoWorldTravelController.TravelTo(ctx, TileCenter(anvil), 4f);

        if (TryCraftNear(ctx, ctx.State.HardmodeAnvil, ItemID.PickaxeAxe) ||
            TryCraftNear(ctx, ctx.State.HardmodeAnvil, ItemID.Drax))
        {
            ctx.TaskRuntime.RecordWorldProgress();
            return AutoTaskResult.Running;
        }
        if (!AutoCraftingService.IsAvailable)
            return AutoTaskResult.Faulted;

        int missingBoss = MissingMechanicalMaterialBoss(ctx.Profile);
        if (missingBoss == NPCID.None)
            return AutoTaskResult.RetryLater;
        if (AutoCampaignController.HasLiveTarget())
            return AutoTaskResult.Running;
        if (Main.dayTime)
            return AutoExplorationController.Explore(ctx);
        if ((long)Main.GameUpdateCount < nextMechanicalRecoverySummonTick)
            return AutoTaskResult.Running;
        if (Main.netMode != NetmodeID.SinglePlayer)
            return AutoTaskResult.BlockedUnsupportedWorld;

        if (missingBoss == NPCID.Retinazer)
        {
            NPC.SpawnOnPlayer(ctx.Me.whoAmI, NPCID.Retinazer);
            NPC.SpawnOnPlayer(ctx.Me.whoAmI, NPCID.Spazmatism);
        }
        else
            NPC.SpawnOnPlayer(ctx.Me.whoAmI, missingBoss);
        nextMechanicalRecoverySummonTick = (long)Main.GameUpdateCount + 600;
        return AutoTaskResult.Running;
    }

    private static int MissingMechanicalMaterialBoss(CompanionProfile profile)
    {
        if (AutoInventory.Count(profile, ItemID.SoulofMight) < 1)
            return NPCID.TheDestroyer;
        if (AutoInventory.Count(profile, ItemID.SoulofSight) < 1)
            return NPCID.Retinazer;
        if (AutoInventory.Count(profile, ItemID.SoulofFright) < 1)
            return NPCID.SkeletronPrime;
        return AutoInventory.Count(profile, ItemID.HallowedBar) < 18
            ? NPCID.TheDestroyer : NPCID.None;
    }

    private static bool AllMechanicalBossesDown() => NPC.downedMechBoss1 &&
        NPC.downedMechBoss2 && NPC.downedMechBoss3;

    private static AutoTaskResult MineSmeltCraftTier(AutoContext ctx, int[] tiles, int[] ores,
        int[] bars, int oreTarget, int[] picks, Point? craftingAnchor, int[] armour,
        Point? smelter = null)
    {
        int requiredBars = picks.Length > 0 ? 22 : 60;
        if (AutoResourceController.CountAny(ctx.Profile, bars) < requiredBars)
        {
            if (AutoResourceController.CountAny(ctx.Profile, ores) < oreTarget)
                return AutoResourceController.MineIndexedResource(ctx, tiles, ores, oreTarget);
            AutoTaskResult smelt = AutoStationController.SmeltOre(ctx,
                smelter ?? ctx.State.Furnace, bars);
            if (smelt != AutoTaskResult.RetryLater)
                return smelt;
        }
        foreach (int pick in picks)
            if (!Owned(ctx.Profile, pick) && TryCraftNear(ctx, craftingAnchor, pick))
                return AutoTaskResult.Running;
        return CraftArmourNear(ctx, craftingAnchor, armour);
    }

    private static AutoTaskResult CraftArmourNear(AutoContext ctx, Point? anchor, int[] targets)
    {
        if (targets.Length == 6)
        {
            for (int i = 0; i < targets.Length; i += 2)
            {
                if (Owned(ctx.Profile, targets[i]) || Owned(ctx.Profile, targets[i + 1]))
                    continue;
                if (TryCraftNear(ctx, anchor, targets[i]) || TryCraftNear(ctx, anchor, targets[i + 1]))
                    return AutoTaskResult.Running;
                return AutoTaskResult.RetryLater;
            }
        }
        else
        {
            foreach (int itemType in targets)
            {
                if (Owned(ctx.Profile, itemType))
                    continue;
                if (TryCraftNear(ctx, anchor, itemType))
                    return AutoTaskResult.Running;
                return AutoTaskResult.RetryLater;
            }
        }
        AutoGearController.OrganizeOwnedGear(ctx.Me, ctx.Profile);
        return AutoTaskResult.Succeeded;
    }

    private static bool TryCraftNear(AutoContext ctx, Point? anchor, int itemType)
    {
        if (anchor is Point station && Vector2.DistanceSquared(ctx.Me.Center,
            new Vector2(station.X * 16f + 8f, station.Y * 16f + 8f)) > 5f * 16f * 5f * 16f)
        {
            AutoWorldTravelController.TravelTo(ctx,
                new Vector2(station.X * 16f + 8f, station.Y * 16f + 8f), 4f);
            return false;
        }
        return AutoCraftingService.TryCraft(ctx.Instance, ctx.Profile, itemType);
    }

    private static bool LiveStation(Point? point, int tileType) => point is Point p &&
        WorldGen.InWorld(p.X, p.Y, 5) && Main.tile[p.X, p.Y].HasTile &&
        Main.tile[p.X, p.Y].TileType == tileType;

    private static bool Owned(CompanionProfile profile, int type)
    {
        if (AutoInventory.Has(profile, type))
            return true;
        foreach (Item item in profile.Armor)
            if (item.type == type)
                return true;
        return false;
    }

    private static bool Near(Player me, Point tile) => Vector2.DistanceSquared(me.Center,
        TileCenter(tile)) <= 5f * 16f * 5f * 16f;

    private static Vector2 TileCenter(Point tile) =>
        new(tile.X * 16f + 8f, tile.Y * 16f + 8f);

    private static int[] Tier1Armour(AutoCombatClass cls) => cls switch
    {
        AutoCombatClass.Melee => [ItemID.PalladiumMask, ItemID.CobaltMask, ItemID.PalladiumBreastplate, ItemID.CobaltBreastplate, ItemID.PalladiumLeggings, ItemID.CobaltLeggings],
        AutoCombatClass.Ranged => [ItemID.PalladiumHelmet, ItemID.CobaltHelmet, ItemID.PalladiumBreastplate, ItemID.CobaltBreastplate, ItemID.PalladiumLeggings, ItemID.CobaltLeggings],
        _ => [ItemID.PalladiumHeadgear, ItemID.CobaltHat, ItemID.PalladiumBreastplate, ItemID.CobaltBreastplate, ItemID.PalladiumLeggings, ItemID.CobaltLeggings]
    };

    private static int[] Tier2Armour(AutoCombatClass cls) => cls switch
    {
        AutoCombatClass.Melee => [ItemID.OrichalcumMask, ItemID.MythrilHelmet, ItemID.OrichalcumBreastplate, ItemID.MythrilChainmail, ItemID.OrichalcumLeggings, ItemID.MythrilGreaves],
        AutoCombatClass.Ranged => [ItemID.OrichalcumHelmet, ItemID.MythrilHat, ItemID.OrichalcumBreastplate, ItemID.MythrilChainmail, ItemID.OrichalcumLeggings, ItemID.MythrilGreaves],
        _ => [ItemID.OrichalcumHeadgear, ItemID.MythrilHood, ItemID.OrichalcumBreastplate, ItemID.MythrilChainmail, ItemID.OrichalcumLeggings, ItemID.MythrilGreaves]
    };

    private static int[] Tier3Armour(AutoCombatClass cls) => cls switch
    {
        AutoCombatClass.Melee => [ItemID.TitaniumMask, ItemID.AdamantiteHelmet, ItemID.TitaniumBreastplate, ItemID.AdamantiteBreastplate, ItemID.TitaniumLeggings, ItemID.AdamantiteLeggings],
        AutoCombatClass.Ranged => [ItemID.TitaniumHelmet, ItemID.AdamantiteMask, ItemID.TitaniumBreastplate, ItemID.AdamantiteBreastplate, ItemID.TitaniumLeggings, ItemID.AdamantiteLeggings],
        _ => [ItemID.TitaniumHeadgear, ItemID.AdamantiteHeadgear, ItemID.TitaniumBreastplate, ItemID.AdamantiteBreastplate, ItemID.TitaniumLeggings, ItemID.AdamantiteLeggings]
    };

    private static int[] ChlorophyteArmour(AutoCombatClass cls) => cls switch
    {
        AutoCombatClass.Melee => [ItemID.ChlorophyteMask, ItemID.ChlorophytePlateMail, ItemID.ChlorophyteGreaves],
        AutoCombatClass.Ranged => [ItemID.ChlorophyteHelmet, ItemID.ChlorophytePlateMail, ItemID.ChlorophyteGreaves],
        _ => [ItemID.ChlorophyteHeadgear, ItemID.ChlorophytePlateMail, ItemID.ChlorophyteGreaves]
    };
}
