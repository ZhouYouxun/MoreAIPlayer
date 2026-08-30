using Microsoft.Xna.Framework;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;

namespace MoreAIPlayer.Companion.Automation;

/// <summary>
/// Starts vanilla progression encounters through their real gameplay contracts. Ordinary summoning
/// items are crafted/looted and used through Player.ItemCheck; world encounters are reached and
/// interacted with physically. The only direct NPC entry point used here is NPC.SpawnSkeletron,
/// which is vanilla's Old Man curse routine and itself requires a live Old Man/Clothier target.
/// </summary>
public static class AutoEncounterTriggerController
{
    private const float PickupRangeTiles = 14f;
    private const int UnderworldPatrolRowOffset = 170;
    private static long nextTriggerAttemptTick;

    internal static void ResetWorldSession() => nextTriggerAttemptTick = 0;

    /// <summary>Uses otherwise-idle timing windows to acquire a real summon item. World-interaction
    /// encounters cannot be pre-triggered, so they keep exploring until their legal time arrives.</summary>
    public static AutoTaskResult Prepare(AutoContext ctx) => ctx.State.CampaignStage switch
    {
        AutoCampaignStage.EyeOfCthulhu => UseCraftedSummon(ctx,
            ItemID.SuspiciousLookingEye, MaterialRoute.Eye, allowUse: false),
        AutoCampaignStage.EvilBoss => UseCraftedSummon(ctx,
            WorldGen.crimson ? ItemID.BloodySpine : ItemID.WormFood,
            MaterialRoute.Evil, allowUse: false),
        AutoCampaignStage.MechanicalBosses => UseCraftedSummon(ctx,
            !NPC.downedMechBoss1 ? ItemID.MechanicalWorm :
            !NPC.downedMechBoss2 ? ItemID.MechanicalEye : ItemID.MechanicalSkull,
            MaterialRoute.Mechanical, allowUse: false),
        AutoCampaignStage.MoonLord => UseCraftedSummon(ctx,
            ItemID.CelestialSigil, MaterialRoute.Celestial, allowUse: false),
        _ => AutoExplorationController.Explore(ctx)
    };

    public static AutoTaskResult Start(AutoContext ctx)
    {
        if (AutoCampaignController.HasLiveTarget())
            return AutoTaskResult.Succeeded;
        if (Main.netMode != NetmodeID.SinglePlayer)
            return AutoTaskResult.BlockedUnsupportedWorld;

        return ctx.State.CampaignStage switch
        {
            AutoCampaignStage.EyeOfCthulhu => UseCraftedSummon(ctx,
                ItemID.SuspiciousLookingEye, MaterialRoute.Eye),
            AutoCampaignStage.EvilBoss => UseCraftedSummon(ctx,
                WorldGen.crimson ? ItemID.BloodySpine : ItemID.WormFood, MaterialRoute.Evil),
            AutoCampaignStage.Skeletron => CurseOldMan(ctx),
            AutoCampaignStage.WallOfFlesh => SacrificeGuideDoll(ctx),
            AutoCampaignStage.MechanicalBosses => StartCurrentMechanicalBoss(ctx),
            AutoCampaignStage.Plantera => BreakPlanteraBulb(ctx),
            AutoCampaignStage.Golem => ActivateGolemAltar(ctx),
            AutoCampaignStage.LunaticCultist => StartCultistEncounter(ctx),
            AutoCampaignStage.SolarPillar or AutoCampaignStage.VortexPillar or
                AutoCampaignStage.NebulaPillar or AutoCampaignStage.StardustPillar =>
                NPC.LunarApocalypseIsUp ? AutoTaskResult.RetryLater : StartCultistEncounter(ctx),
            AutoCampaignStage.MoonLord => StartMoonLord(ctx),
            AutoCampaignStage.Completed => AutoTaskResult.Succeeded,
            _ => AutoTaskResult.AlternativeRequired
        };
    }

    public static AutoTaskResult StartMechanicalRecovery(AutoContext ctx, int missingBoss)
    {
        if (AutoCampaignController.HasLiveTarget())
            return AutoTaskResult.Running;
        if (Main.dayTime)
            return AutoExplorationController.Explore(ctx);
        if (AutoRecoveryController.IsOverwhelmed(ctx) ||
            AutoCombatSupplyController.NeedsRestock(ctx) ||
            !AutoArenaController.IsPrepared(ctx.State))
            return AutoTaskResult.AlternativeRequired;
        int itemType = missingBoss switch
        {
            NPCID.TheDestroyer => ItemID.MechanicalWorm,
            NPCID.Retinazer or NPCID.Spazmatism => ItemID.MechanicalEye,
            NPCID.SkeletronPrime => ItemID.MechanicalSkull,
            _ => ItemID.None
        };
        return itemType <= ItemID.None
            ? AutoTaskResult.AlternativeRequired
            : UseCraftedSummon(ctx, itemType, MaterialRoute.Mechanical);
    }

    /// <summary>Changes only for a real encounter prerequisite/result, never merely because the
    /// companion walked another exploration segment.</summary>
    public static long ProgressSignature(AutoContext ctx)
    {
        long signature = AutoCampaignController.HasLiveTarget() ? 1_000_000_000_000L : 0L;
        signature += NPC.MoonLordCountdown;
        signature += AutoInventory.Count(ctx.Profile, TriggerItem(ctx.State.CampaignStage)) * 1_000_000L;
        signature += AutoCombatPreparationController.OwnedBuffStackCount(ctx.Profile) * 100L;
        signature += ctx.State.FirstEvilOrbBroken ? 100_000L : 0L;
        return signature;
    }

    private static AutoTaskResult StartCurrentMechanicalBoss(AutoContext ctx)
    {
        int itemType = !NPC.downedMechBoss1 ? ItemID.MechanicalWorm :
            !NPC.downedMechBoss2 ? ItemID.MechanicalEye : ItemID.MechanicalSkull;
        return UseCraftedSummon(ctx, itemType, MaterialRoute.Mechanical);
    }

    private static AutoTaskResult UseCraftedSummon(AutoContext ctx, int itemType,
        MaterialRoute route, bool allowUse = true)
    {
        AutoInventory.TryCollectNearby(ctx.Instance, ctx.Profile, PickupRangeTiles,
            item => item.type == itemType || IsTriggerMaterial(item));

        int slot = AutoInventory.FindInventoryIndex(ctx.Me, item => item.type == itemType);
        if (slot >= 0)
        {
            if (!allowUse)
                return AutoExplorationController.Explore(ctx);
            if (ctx.State.ArenaAnchor is Point anchor)
            {
                Vector2 arena = TileCenter(anchor);
                if (Vector2.DistanceSquared(ctx.Me.Center, arena) > 9f * 16f * (9f * 16f))
                    return AutoWorldTravelController.TravelTo(ctx, arena, 8f);
            }
            if ((long)Main.GameUpdateCount < nextTriggerAttemptTick)
                return AutoTaskResult.Running;
            bool used = ctx.Actuator.UseItemInSlot(ctx.Instance, ctx.Me, slot, ctx.Me.Center);
            if (used)
                nextTriggerAttemptTick = (long)Main.GameUpdateCount + 180;
            return used ? AutoTaskResult.Running : AutoTaskResult.RetryLater;
        }

        AutoTaskResult? stationTravel = TravelToRequiredCraftingStation(ctx, route);
        if (stationTravel.HasValue)
            return stationTravel.Value;
        if (AutoCraftingService.TryCraft(ctx.Instance, ctx.Profile, itemType))
        {
            ctx.TaskRuntime.RecordWorldProgress();
            return AutoTaskResult.Running;
        }
        if (!AutoCraftingService.IsAvailable)
            return AutoTaskResult.Faulted;

        AutoTaskResult chest = AutoLootController.LootBestWorldItem(ctx,
            item => item.type == itemType || IsTriggerMaterial(item), AutoGoalId.SummonBoss);
        if (chest != AutoTaskResult.AlternativeRequired)
            return chest;
        return GatherTriggerMaterials(ctx, route, itemType);
    }

    private static AutoTaskResult? TravelToRequiredCraftingStation(AutoContext ctx,
        MaterialRoute route)
    {
        Point? station = route switch
        {
            MaterialRoute.Eye => AutoWorldIndexSystem.FindNearest(ctx.Me.Center,
                [TileID.DemonAltar]),
            MaterialRoute.Evil => AutoWorldIndexSystem.FindNearest(ctx.Me.Center,
                [TileID.DemonAltar]),
            MaterialRoute.Mechanical => ctx.State.HardmodeAnvil,
            MaterialRoute.Celestial => ctx.State.AncientManipulator,
            _ => null
        };
        if (station is not Point target)
            return null;
        Vector2 center = TileCenter(target);
        return Vector2.DistanceSquared(ctx.Me.Center, center) > 5f * 16f * (5f * 16f)
            ? AutoWorldTravelController.TravelTo(ctx, center, 4f)
            : null;
    }

    private static AutoTaskResult GatherTriggerMaterials(AutoContext ctx, MaterialRoute route,
        int requestedItemType = ItemID.None)
    {
        if (AutoThreatController.TryDefendWhileExploring(ctx))
            return AutoTaskResult.Running;
        return route switch
        {
            MaterialRoute.Eye => Main.dayTime
                ? AutoExplorationController.Explore(ctx)
                : HuntNpc(ctx, npc => npc.aiStyle == NPCAIStyleID.DemonEye,
                    SurfacePatrolPoint(ctx)),
            MaterialRoute.Evil => PatrolEvilMaterials(ctx),
            MaterialRoute.Mechanical => PatrolMechanicalMaterials(ctx, requestedItemType),
            MaterialRoute.VoodooDoll => HuntNpc(ctx, npc => npc.type == NPCID.VoodooDemon,
                UnderworldPatrolPoint(ctx)),
            MaterialRoute.GolemCell => HuntNpc(ctx,
                npc => npc.type is NPCID.Lihzahrd or NPCID.FlyingSnake,
                TemplePatrolPoint(ctx)),
            MaterialRoute.Celestial => StartCultistEncounter(ctx),
            _ => AutoExplorationController.Explore(ctx)
        };
    }

    private static AutoTaskResult PatrolEvilMaterials(AutoContext ctx)
    {
        int powderType = WorldGen.crimson ? ItemID.ViciousPowder : ItemID.VilePowder;
        int mushroomType = WorldGen.crimson ? ItemID.ViciousMushroom : ItemID.VileMushroom;
        if (AutoInventory.Count(ctx.Profile, mushroomType) > 0 &&
            AutoInventory.Count(ctx.Profile, powderType) < 30 &&
            AutoCraftingService.TryCraftAllAvailable(ctx.Instance, ctx.Profile, powderType))
        {
            ctx.TaskRuntime.RecordWorldProgress();
            return AutoTaskResult.Running;
        }

        NPC target = FindNpc(npc => npc.type is NPCID.EaterofSouls or NPCID.DevourerHead or
            NPCID.Crimera or NPCID.FaceMonster or NPCID.BloodCrawler or NPCID.BloodCrawlerWall);
        if (target is not null)
            return EngageNpc(ctx, target);

        int plantTile = WorldGen.crimson ? TileID.CrimsonPlants : TileID.CorruptPlants;
        Point? plant = AutoWorldIndexSystem.FindNearest(ctx.Me.Center, [plantTile],
            p => !ctx.TaskRuntime.IsTargetRejected(AutoGoalId.SummonBoss, p));
        if (plant is Point harvest)
        {
            int tool = AutoInventory.BestToolIndex(ctx.Me, item =>
                System.Math.Max(item.axe, item.pick));
            if (tool < 0)
                tool = AutoInventory.FindInventoryIndex(ctx.Me,
                    CompanionSlotRules.IsRegularWeapon);
            if (tool >= 0)
            {
                ctx.TaskRuntime.SetActiveTarget(AutoGoalId.SummonBoss, harvest);
                return ctx.Actuator.MoveAndWorkTile(ctx.Instance, ctx.Me, ctx.Navigator,
                    tool, harvest) ? AutoTaskResult.Running : AutoTaskResult.AlternativeRequired;
            }
        }
        Point? landmark = AutoWorldIndexSystem.FindEvilLandmark(ctx.Me.Center);
        if (landmark is not Point evil)
            return AutoWorldIndexSystem.ScanComplete
                ? BreakNextEvilOrb(ctx) : AutoExplorationController.Explore(ctx);
        Vector2 patrol = new(evil.X * 16f + 8f,
            AutoWorldTopology.TraversalCenterY(evil.X, ctx.Me));
        AutoTaskResult travel = AutoWorldTravelController.TravelTo(ctx, patrol, 18f);
        return travel == AutoTaskResult.Succeeded
            ? AutoExplorationController.Explore(ctx) : travel;
    }

    private static AutoTaskResult PatrolMechanicalMaterials(AutoContext ctx, int summonItemType)
    {
        int ironBars = AutoInventory.Count(ctx.Profile, ItemID.IronBar) +
            AutoInventory.Count(ctx.Profile, ItemID.LeadBar);
        if (ironBars < 5)
        {
            int ore = AutoInventory.Count(ctx.Profile, ItemID.IronOre) +
                AutoInventory.Count(ctx.Profile, ItemID.LeadOre);
            if (ore < 15)
                return AutoResourceController.MineIronTierOre(ctx, 15);
            if (ctx.State.Furnace is Point furnace)
            {
                Vector2 station = TileCenter(furnace);
                if (Vector2.DistanceSquared(ctx.Me.Center, station) > 5f * 16f * (5f * 16f))
                    return AutoWorldTravelController.TravelTo(ctx, station, 4f);
                bool smelted = AutoCraftingService.TryCraftAllAvailable(ctx.Instance,
                    ctx.Profile, ItemID.IronBar);
                smelted |= AutoCraftingService.TryCraftAllAvailable(ctx.Instance,
                    ctx.Profile, ItemID.LeadBar);
                if (smelted)
                {
                    ctx.TaskRuntime.RecordWorldProgress();
                    return AutoTaskResult.Running;
                }
            }
        }

        if (summonItemType == ItemID.MechanicalEye &&
            AutoInventory.Count(ctx.Profile, ItemID.Lens) < 3)
        {
            return Main.dayTime ? AutoExplorationController.Explore(ctx) :
                HuntNpc(ctx, npc => npc.aiStyle == NPCAIStyleID.DemonEye,
                    SurfacePatrolPoint(ctx));
        }
        if (summonItemType == ItemID.MechanicalSkull &&
            AutoInventory.Count(ctx.Profile, ItemID.Bone) < 30)
        {
            Vector2 dungeon = new(Main.dungeonX * 16f + 8f,
                System.Math.Clamp((int)Main.rockLayer + 80, 120, Main.maxTilesY - 240) * 16f);
            return HuntNpc(ctx, npc => npc.type is NPCID.AngryBones or NPCID.AngryBonesBig or
                NPCID.AngryBonesBigHelmet or NPCID.AngryBonesBigMuscle or NPCID.DarkCaster or
                NPCID.CursedSkull, dungeon);
        }

        bool needLight = summonItemType is ItemID.MechanicalEye or ItemID.MechanicalSkull &&
            AutoInventory.Count(ctx.Profile, ItemID.SoulofLight) < 6;
        bool needNight = summonItemType is ItemID.MechanicalWorm or ItemID.MechanicalSkull &&
            AutoInventory.Count(ctx.Profile, ItemID.SoulofNight) < 6;
        Point? landmark = needLight ? AutoWorldIndexSystem.FindHallowLandmark(ctx.Me.Center) :
            needNight ? AutoWorldIndexSystem.FindEvilLandmark(ctx.Me.Center) :
            AutoWorldIndexSystem.FindHallowLandmark(ctx.Me.Center) ??
                AutoWorldIndexSystem.FindEvilLandmark(ctx.Me.Center);
        int x = landmark?.X ?? Main.spawnTileX + ctx.State.ExplorationDirection * 300;
        int y = System.Math.Clamp((int)Main.rockLayer + 180, 120, Main.maxTilesY - 240);
        int lane = (ctx.State.StrategyVariant % 5 - 2) * 90;
        Vector2 destination = new((x + lane) * 16f + 8f, y * 16f + 8f);
        AutoTaskResult travel = AutoWorldTravelController.TravelTo(ctx, destination, 16f);
        if (travel == AutoTaskResult.Succeeded)
        {
            ctx.State.StrategyVariant++;
            return AutoExplorationController.Explore(ctx);
        }
        return travel;
    }

    private static AutoTaskResult BreakNextEvilOrb(AutoContext ctx)
    {
        AutoInventory.TryCollectNearby(ctx.Instance, ctx.Profile, PickupRangeTiles, null);
        int hammer = AutoInventory.BestToolIndex(ctx.Me, item => item.hammer);
        if (hammer < 0)
        {
            if (AutoCraftingService.TryCraft(ctx.Instance, ctx.Profile, ItemID.WoodenHammer))
                return AutoTaskResult.Running;
            return AutoTaskResult.RetryLater;
        }

        bool Valid(Point p) => WorldGen.InWorld(p.X, p.Y, 5) && Main.tile[p.X, p.Y].HasTile &&
            Main.tile[p.X, p.Y].TileType == TileID.ShadowOrbs;
        Point target;
        if (!ctx.TaskRuntime.TryGetActiveTarget(AutoGoalId.SummonBoss, Valid, out target))
        {
            Point? found = AutoWorldIndexSystem.FindNearest(ctx.Me.Center, [TileID.ShadowOrbs],
                p => !ctx.TaskRuntime.IsTargetRejected(AutoGoalId.SummonBoss, p));
            if (found is not Point indexed)
                return AutoWorldIndexSystem.ScanComplete
                    ? AutoTaskResult.AlternativeRequired : AutoExplorationController.Explore(ctx);
            target = indexed;
            ctx.TaskRuntime.SetActiveTarget(AutoGoalId.SummonBoss, target);
        }

        Vector2 targetWorld = TileCenter(target);
        if (!AutoActuator.IsTileInReach(ctx.Me, ctx.Me.inventory[hammer], target.X, target.Y))
        {
            if (Vector2.DistanceSquared(ctx.Me.Center, targetWorld) <= 36f * 16f * (36f * 16f))
            {
                int explosive = AutoInventory.FindInventoryIndex(ctx.Me, item => item.type is
                    ItemID.Bomb or ItemID.StickyBomb or ItemID.Dynamite or ItemID.StickyDynamite);
                if (explosive >= 0 && ctx.Actuator.UseItemInSlot(ctx.Instance, ctx.Me,
                    explosive, targetWorld))
                    return AutoTaskResult.Running;
            }
            if (AutoExplosiveController.Count(ctx.Profile) <= 0)
            {
                AutoTaskResult acquire = AutoExplosiveController.Acquire(ctx);
                if (acquire != AutoTaskResult.AlternativeRequired)
                    return acquire;
                AutoTaskResult loot = AutoLootController.LootBestWorldItem(ctx, item => item.type is
                    ItemID.Bomb or ItemID.StickyBomb or ItemID.Dynamite or ItemID.StickyDynamite,
                    AutoGoalId.SummonBoss);
                if (loot != AutoTaskResult.AlternativeRequired)
                    return loot;
            }
            return AutoWorldTravelController.TravelTo(ctx, targetWorld, 5f);
        }

        return ctx.Actuator.MoveAndWorkTile(ctx.Instance, ctx.Me, ctx.Navigator, hammer, target)
            ? AutoTaskResult.Running : AutoTaskResult.AlternativeRequired;
    }

    private static AutoTaskResult CurseOldMan(AutoContext ctx)
    {
        if (Main.dayTime)
            return AutoExplorationController.Explore(ctx);
        NPC oldMan = FindNpc(npc => npc.type == NPCID.OldMan);
        if (oldMan is null)
            return AutoTaskResult.RetryLater;
        if (Vector2.DistanceSquared(ctx.Me.Center, oldMan.Center) > 6f * 16f * (6f * 16f))
            return AutoWorldTravelController.TravelTo(ctx, oldMan.Center, 5f);
        NPC.SpawnSkeletron(ctx.Me.whoAmI);
        nextTriggerAttemptTick = (long)Main.GameUpdateCount + 180;
        ctx.TaskRuntime.RecordWorldProgress();
        return AutoTaskResult.Running;
    }

    private static AutoTaskResult SacrificeGuideDoll(AutoContext ctx)
    {
        if (!NPC.AnyNPCs(NPCID.Guide))
            return AutoTaskResult.RetryLater;
        int slot = AutoInventory.FindInventoryIndex(ctx.Me,
            item => item.type == ItemID.GuideVoodooDoll);
        if (slot < 0)
        {
            AutoTaskResult chest = AutoLootController.LootBestWorldItem(ctx,
                item => item.type == ItemID.GuideVoodooDoll, AutoGoalId.SummonBoss);
            return chest != AutoTaskResult.AlternativeRequired
                ? chest : GatherTriggerMaterials(ctx, MaterialRoute.VoodooDoll);
        }

        if (ctx.State.ArenaAnchor is Point anchor)
        {
            Vector2 arena = TileCenter(anchor);
            if (Vector2.DistanceSquared(ctx.Me.Center, arena) > 18f * 16f * (18f * 16f))
                return AutoWorldTravelController.TravelTo(ctx, arena, 16f);
        }

        bool ValidLava(Point p) => WorldGen.InWorld(p.X, p.Y, 10) &&
            Main.tile[p.X, p.Y].LiquidAmount >= 160 &&
            Main.tile[p.X, p.Y].LiquidType == LiquidID.Lava && !WorldGen.SolidTile(p.X, p.Y);
        Point pool;
        if (!ctx.TaskRuntime.TryGetActiveTarget(AutoGoalId.SummonBoss, ValidLava, out pool))
        {
            Point origin = ctx.State.ArenaAnchor ?? ctx.Me.Center.ToTileCoordinates();
            Point? lava = FindNearbyLava(origin, 520);
            if (lava is not Point found)
                return AutoExplorationController.Explore(ctx);
            pool = found;
            ctx.TaskRuntime.SetActiveTarget(AutoGoalId.SummonBoss, pool);
        }
        Vector2 lavaCenter = TileCenter(pool);
        if (Vector2.DistanceSquared(ctx.Me.Center, lavaCenter) > 8f * 16f * (8f * 16f))
            return AutoWorldTravelController.TravelTo(ctx, lavaCenter, 6f);

        Item held = ctx.Me.inventory[slot];
        Item offering = held.Clone();
        offering.stack = 1;
        int index = Item.NewItem(new EntitySource_Misc("MoreAIPlayer_LegalGuideDoll"),
            new Rectangle(pool.X * 16, pool.Y * 16, 16, 16), offering,
            noBroadcast: false, noGrabDelay: true);
        if (index < 0 || index >= Main.maxItems)
            return AutoTaskResult.RetryLater;
        Main.item[index].noGrabDelay = System.Math.Max(Main.item[index].noGrabDelay, 120);
        if (--held.stack <= 0)
            held.TurnToAir();
        ctx.TaskRuntime.ClearActiveTarget();
        ctx.TaskRuntime.RecordWorldProgress();
        nextTriggerAttemptTick = (long)Main.GameUpdateCount + 180;
        return AutoTaskResult.Running;
    }

    private static AutoTaskResult BreakPlanteraBulb(AutoContext ctx)
    {
        bool Valid(Point p) => WorldGen.InWorld(p.X, p.Y, 5) && Main.tile[p.X, p.Y].HasTile &&
            Main.tile[p.X, p.Y].TileType == TileID.PlanteraBulb && WorldGen.CanKillTile(p.X, p.Y);
        Point target;
        if (!ctx.TaskRuntime.TryGetActiveTarget(AutoGoalId.SummonBoss, Valid, out target))
        {
            Vector2 searchCenter = ctx.State.ArenaAnchor is Point arena
                ? TileCenter(arena) : ctx.Me.Center;
            Point? found = AutoWorldIndexSystem.FindNearest(searchCenter, [TileID.PlanteraBulb],
                p => !ctx.TaskRuntime.IsTargetRejected(AutoGoalId.SummonBoss, p));
            if (found is not Point indexed)
                return AutoWorldIndexSystem.ScanComplete
                    ? AutoTaskResult.AlternativeRequired : AutoExplorationController.Explore(ctx);
            target = indexed;
            ctx.TaskRuntime.SetActiveTarget(AutoGoalId.SummonBoss, target);
        }
        Vector2 center = TileCenter(target);
        if (Vector2.DistanceSquared(ctx.Me.Center, center) > 7f * 16f * (7f * 16f))
            return AutoWorldTravelController.TravelTo(ctx, center, 5f);
        int pick = AutoInventory.BestToolIndex(ctx.Me, item => item.pick);
        return pick >= 0 && ctx.Actuator.MoveAndWorkTile(ctx.Instance, ctx.Me,
            ctx.Navigator, pick, target) ? AutoTaskResult.Running : AutoTaskResult.AlternativeRequired;
    }

    private static AutoTaskResult ActivateGolemAltar(AutoContext ctx)
    {
        if (!AutoInventory.Has(ctx.Profile, ItemID.LihzahrdPowerCell))
        {
            AutoTaskResult chest = AutoLootController.LootBestWorldItem(ctx,
                item => item.type == ItemID.LihzahrdPowerCell, AutoGoalId.SummonBoss);
            return chest != AutoTaskResult.AlternativeRequired
                ? chest : GatherTriggerMaterials(ctx, MaterialRoute.GolemCell);
        }
        Point? altar = AutoWorldIndexSystem.FindNearest(ctx.Me.Center, [TileID.LihzahrdAltar]);
        if (altar is not Point target)
            return AutoWorldIndexSystem.ScanComplete
                ? AutoTaskResult.AlternativeRequired : AutoExplorationController.Explore(ctx);
        Vector2 center = TileCenter(target);
        if (Vector2.DistanceSquared(ctx.Me.Center, center) > 5f * 16f * (5f * 16f))
            return AutoWorldTravelController.TravelTo(ctx, center, 4f);
        bool used = ctx.Actuator.UseTile(ctx.Instance, ctx.Me, target);
        if (used)
            nextTriggerAttemptTick = (long)Main.GameUpdateCount + 180;
        return used ? AutoTaskResult.Running : AutoTaskResult.RetryLater;
    }

    private static AutoTaskResult StartCultistEncounter(AutoContext ctx)
    {
        NPC cultist = FindNpc(npc => npc.type is NPCID.CultistDevote or NPCID.CultistArcherBlue);
        if (cultist is not null)
            return EngageNpc(ctx, cultist);
        Vector2 dungeon = new(Main.dungeonX * 16f + 8f,
            (float)System.Math.Clamp(Main.worldSurface - 20, 80, Main.maxTilesY - 240) * 16f);
        AutoTaskResult travel = AutoWorldTravelController.TravelTo(ctx, dungeon, 28f);
        return travel == AutoTaskResult.Succeeded ? AutoTaskResult.RetryLater : travel;
    }

    private static AutoTaskResult StartMoonLord(AutoContext ctx)
    {
        if (NPC.MoonLordCountdown > 0 || NPC.LunarApocalypseIsUp)
            return AutoExplorationController.Explore(ctx);
        AutoTaskResult sigil = UseCraftedSummon(ctx, ItemID.CelestialSigil,
            MaterialRoute.Celestial);
        return sigil;
    }

    private static AutoTaskResult HuntNpc(AutoContext ctx, System.Func<NPC, bool> match,
        Vector2 patrol)
    {
        NPC target = FindNpc(match);
        if (target is not null)
            return EngageNpc(ctx, target);
        AutoTaskResult travel = AutoWorldTravelController.TravelTo(ctx, patrol, 18f);
        if (travel == AutoTaskResult.Succeeded)
        {
            ctx.State.StrategyVariant++;
            return AutoExplorationController.Explore(ctx);
        }
        return travel;
    }

    private static AutoTaskResult EngageNpc(AutoContext ctx, NPC target)
    {
        ctx.TaskRuntime.SetActiveTarget(AutoGoalId.SummonBoss,
            target.Center.ToTileCoordinates());
        int weapon = AutoCombatController.BestCombatInventoryIndex(ctx, target, out _);
        if (weapon < 0)
            weapon = AutoInventory.BestToolIndex(ctx.Me, item => System.Math.Max(item.axe, item.pick));
        if (weapon < 0)
            return AutoTaskResult.AlternativeRequired;
        float distance = Vector2.Distance(ctx.Me.Center, target.Center);
        if (distance > 7f * 16f)
            return AutoWorldTravelController.TravelTo(ctx, target.Center, 6f);
        ctx.Navigator.Idle(ctx.Me);
        ctx.Actuator.UseItemInSlot(ctx.Instance, ctx.Me, weapon, target.Center);
        return AutoTaskResult.Running;
    }

    private static NPC FindNpc(System.Func<NPC, bool> match)
    {
        NPC best = null;
        foreach (NPC npc in Main.ActiveNPCs)
        {
            if (!npc.active || npc.life <= 0 || !match(npc))
                continue;
            if (best is null)
                best = npc;
        }
        return best;
    }

    private static Point? FindNearbyLava(Point origin, int radius)
    {
        int minY = System.Math.Max((int)Main.rockLayer, origin.Y - radius);
        int maxY = System.Math.Min(Main.maxTilesY - 20, origin.Y + radius);
        for (int ring = 0; ring <= radius; ring += 3)
        for (int x = origin.X - ring; x <= origin.X + ring; x += System.Math.Max(1, ring * 2))
        for (int y = minY; y <= maxY; y++)
        {
            if (!WorldGen.InWorld(x, y, 10))
                continue;
            Tile tile = Main.tile[x, y];
            if (tile.LiquidAmount >= 160 && tile.LiquidType == LiquidID.Lava &&
                !WorldGen.SolidTile(x, y))
                return new Point(x, y);
        }
        return null;
    }

    private static Vector2 SurfacePatrolPoint(AutoContext ctx)
    {
        int direction = ctx.State.ExplorationDirection == 0 ? 1 : ctx.State.ExplorationDirection;
        int x = System.Math.Clamp(ctx.Me.Center.ToTileCoordinates().X + direction * 140,
            120, Main.maxTilesX - 120);
        return new Vector2(x * 16f + 8f, Main.spawnTileY * 16f);
    }

    private static Vector2 UnderworldPatrolPoint(AutoContext ctx)
    {
        int direction = ctx.State.ExplorationDirection == 0 ? 1 : ctx.State.ExplorationDirection;
        int x = System.Math.Clamp(ctx.Me.Center.ToTileCoordinates().X + direction * 160,
            120, Main.maxTilesX - 120);
        int y = Main.maxTilesY - UnderworldPatrolRowOffset;
        return new Vector2(x * 16f + 8f, y * 16f + 8f);
    }

    private static Vector2 TemplePatrolPoint(AutoContext ctx)
    {
        Point? temple = AutoWorldIndexSystem.FindTempleLandmark(ctx.Me.Center);
        Point target = temple ?? new Point(Main.spawnTileX, (int)Main.rockLayer + 300);
        return TileCenter(target);
    }

    private static int TriggerItem(AutoCampaignStage stage) => stage switch
    {
        AutoCampaignStage.EyeOfCthulhu => ItemID.SuspiciousLookingEye,
        AutoCampaignStage.EvilBoss => WorldGen.crimson ? ItemID.BloodySpine : ItemID.WormFood,
        AutoCampaignStage.WallOfFlesh => ItemID.GuideVoodooDoll,
        AutoCampaignStage.MechanicalBosses when !NPC.downedMechBoss1 => ItemID.MechanicalWorm,
        AutoCampaignStage.MechanicalBosses when !NPC.downedMechBoss2 => ItemID.MechanicalEye,
        AutoCampaignStage.MechanicalBosses => ItemID.MechanicalSkull,
        AutoCampaignStage.Golem => ItemID.LihzahrdPowerCell,
        AutoCampaignStage.MoonLord => ItemID.CelestialSigil,
        _ => ItemID.None
    };

    private static bool IsTriggerMaterial(Item item) => item.type is
        ItemID.Lens or ItemID.BlackLens or ItemID.RottenChunk or ItemID.Vertebrae or
        ItemID.VileMushroom or ItemID.ViciousMushroom or ItemID.VilePowder or ItemID.ViciousPowder or
        ItemID.Bone or ItemID.SoulofLight or ItemID.SoulofNight or
        ItemID.FragmentSolar or ItemID.FragmentVortex or ItemID.FragmentNebula or
        ItemID.FragmentStardust or ItemID.GuideVoodooDoll or ItemID.LihzahrdPowerCell;

    private static Vector2 TileCenter(Point tile) =>
        new(tile.X * 16f + 8f, tile.Y * 16f + 8f);

    private enum MaterialRoute : byte
    {
        Eye,
        Evil,
        Mechanical,
        VoodooDoll,
        GolemCell,
        Celestial
    }
}
