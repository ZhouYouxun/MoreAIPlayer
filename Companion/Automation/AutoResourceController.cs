using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;

namespace MoreAIPlayer.Companion.Automation;

/// <summary>
/// Executes the gathering goals: chopping trees (with an opportunistic replant), mining stone, and
/// mining the basic pre-hardmode ores. All work goes through the real tool ItemCheck via the
/// actuator, and everything the companion knocks loose is collected by the auto-only pickup — no
/// dropped resource is ever synthesised into the inventory.
/// </summary>
public static class AutoResourceController
{
    private const int ScanRadiusTiles = 46;
    private const float PickupRangeTiles = 7f;

    public static readonly int[] BasicOreItems =
    [
        ItemID.CopperOre, ItemID.TinOre, ItemID.IronOre, ItemID.LeadOre,
        ItemID.SilverOre, ItemID.TungstenOre, ItemID.GoldOre, ItemID.PlatinumOre
    ];

    /// <summary>Chops the nearest tree, collecting wood and acorns and replanting where it can.</summary>
    public static AutoTaskResult GatherWood(AutoContext ctx, int target)
    {
        // Remember a root which existed on the previous tick and has now been felled. The pending
        // site lives in AutoCompanionState, so recall/death/reload cannot erase the ecological debt.
        if (ctx.TaskRuntime.ActiveTargetGoal == AutoGoalId.GatherWood &&
            ctx.TaskRuntime.ActiveTargetTile is Point previousRoot &&
            !AutoWorldKnowledge.IsChoppableTreeBase(previousRoot))
        {
            ctx.State.QueueReplant(previousRoot);
            ctx.TaskRuntime.ClearActiveTarget();
        }

        CollectWanted(ctx, item => item.type == ItemID.Wood || item.type == ItemID.Acorn);
        bool targetMet = AutoInventory.Count(ctx.Profile, ItemID.Wood) >= target;
        if (targetMet)
        {
            AutoTaskResult? replant = ServicePendingReplant(ctx, allowLongTravel: true);
            if (replant.HasValue)
                return replant.Value;
            if (ctx.State.PendingReplantCount == 0)
                return AutoTaskResult.Succeeded;

            // The starter plan explicitly restores the forest before mining. If a felled site is
            // still waiting only because no acorn is held, keep gathering until one is obtained;
            // a replantable site with an acorn would have been handled above, so a remaining debt
            // at this point is an actual alternate-route problem rather than a silent success.
            if (AutoInventory.Count(ctx.Profile, ItemID.Acorn) > 0)
                return AutoTaskResult.AlternativeRequired;
        }

        if (!AutoInventory.HasBackpackCapacityFor(ctx.Profile, ItemID.Wood))
            return AutoTaskResult.RetryLater; // full: let a deposit/tidy goal run first

        Point? tree = ResolveTarget(ctx, AutoGoalId.GatherWood,
            AutoWorldKnowledge.IsChoppableTreeBase,
            accept => AutoWorldKnowledge.FindNearestTreeBase(ctx.Me.Center, ScanRadiusTiles, accept) ??
                AutoWorldIndexSystem.FindNearestTreeBase(ctx.Me.Center, accept));
        if (tree is not Point treeTile)
        {
            AutoTaskResult? replant = ServicePendingReplant(ctx);
            if (replant.HasValue)
                return replant.Value;
            // Replant only after the local stand has been processed. Planting before every axe
            // swing spent the single item-use budget and delayed the resource job itself.
            if (ctx.State.PendingReplantCount == 0 && TryReplant(ctx))
                return AutoTaskResult.Running;
            // The frame-sliced index needs a few seconds on a large world. Productively scout in
            // one stable direction while it finishes instead of declaring failure and entering a
            // left/right watchdog oscillation. A completed empty index is an honest world-resource
            // block (normally possible only after extreme external deforestation).
            return AutoWorldIndexSystem.ScanComplete
                ? AutoTaskResult.BlockedMissingWorldResource
                : AutoExplorationController.Explore(ctx);
        }

        int axe = AutoInventory.BestToolIndex(ctx.Me, item => item.axe);
        if (axe < 0)
            return AutoTaskResult.AlternativeRequired;

        Vector2 baseCenter = new(treeTile.X * 16f + 8f, treeTile.Y * 16f + 8f);
        Vector2? chopPosition = AutoWorldKnowledge.FindTreeChopPosition(
            ctx.Me, ctx.Me.inventory[axe], treeTile);
        if (chopPosition is not Vector2 standing)
        {
            // Do not path at the solid trunk itself. Reject this enclosed root and let the next
            // tick choose another known tree with a real collision-free work position.
            ctx.TaskRuntime.RejectActiveTarget(8, 1800);
            return AutoTaskResult.AlternativeRequired;
        }

        float standingDistance = Vector2.Distance(ctx.Me.Center, standing);
        if (standingDistance > 18f * 16f)
            return AutoWorldTravelController.TravelTo(ctx, standing, 6f);
        if (standingDistance > 1.35f * 16f)
        {
            ctx.Navigator.Navigate(ctx.Me, standing);
            return AutoTaskResult.Running;
        }

        // Keep this exact root locked until it disappears. A crooked canopy can no longer steal
        // the target between swings merely because one branch is closer to the player.
        if (!AutoActuator.IsTileInReach(ctx.Me, ctx.Me.inventory[axe], treeTile.X, treeTile.Y))
        {
            ctx.Navigator.Navigate(ctx.Me, standing);
            return AutoTaskResult.Running;
        }
        ctx.Navigator.Idle(ctx.Me);
        // A finishing animation is normal. Preserve the root target and resume next tick instead
        // of rejecting the whole nearby forest as unreachable.
        ctx.Actuator.UseItemInSlot(ctx.Instance, ctx.Me, axe, baseCenter, treeTile);
        return AutoTaskResult.Running;
    }

    /// <summary>Mines the nearest stone, collecting stone blocks.</summary>
    public static AutoTaskResult GatherStone(AutoContext ctx, int target)
    {
        CollectWanted(ctx, item => item.type == ItemID.StoneBlock);
        if (AutoInventory.Count(ctx.Profile, ItemID.StoneBlock) >= target)
            return AutoTaskResult.Succeeded;

        if (!AutoInventory.HasBackpackCapacityFor(ctx.Profile, ItemID.StoneBlock))
            return AutoTaskResult.RetryLater;

        Point? stone = ResolveTarget(ctx, AutoGoalId.GatherStone,
            point => AutoWorldKnowledge.IsMineableTile(point, TileID.Stone),
            accept => AutoWorldKnowledge.FindNearestMineable(
                ctx.Me.Center, ScanRadiusTiles, TileID.Stone, accept));
        return MineTile(ctx, AutoGoalId.GatherStone, stone);
    }

    /// <summary>Mines the nearest basic ore the current pick can break, collecting the ore.</summary>
    public static AutoTaskResult MineBasicOre(AutoContext ctx, int target)
    {
        CollectWanted(ctx, IsBasicOreItem);
        if (CountBasicOre(ctx.Profile) >= target)
            return AutoTaskResult.Succeeded;

        if (!AutoInventory.HasBackpackCapacityFor(ctx.Profile, BasicOreItems))
            return AutoTaskResult.RetryLater;

        Point? ore = ResolveTarget(ctx, AutoGoalId.MineOre,
            point => AutoWorldKnowledge.IsMineableTileOfAny(point, AutoWorldKnowledge.BasicOreTiles),
            accept => AutoWorldKnowledge.FindNearestMineableOfAny(
                ctx.Me.Center, ScanRadiusTiles, AutoWorldKnowledge.BasicOreTiles, accept));
        return ore.HasValue ? MineTile(ctx, AutoGoalId.MineOre, ore) : ProspectDownward(ctx, AutoGoalId.MineOre);
    }

    /// <summary>Mines only the iron-tier ore required by the first anvil. Other metal bars cannot
    /// legally substitute for iron/lead in that recipe, so keeping this target separate prevents
    /// the planner from looping forever on a pile of copper, silver or gold bars.</summary>
    public static AutoTaskResult MineIronTierOre(AutoContext ctx, int target)
    {
        CollectWanted(ctx, item => item.type == ItemID.IronOre || item.type == ItemID.LeadOre);
        int held = AutoInventory.Count(ctx.Profile, ItemID.IronOre) + AutoInventory.Count(ctx.Profile, ItemID.LeadOre);
        if (held >= target)
            return AutoTaskResult.Succeeded;
        if (!AutoInventory.HasBackpackCapacityFor(ctx.Profile, ItemID.IronOre, ItemID.LeadOre))
            return AutoTaskResult.RetryLater;

        int[] tiles = [TileID.Iron, TileID.Lead];
        Point? ore = ResolveTarget(ctx, AutoGoalId.MineIronTierOre,
            point => AutoWorldKnowledge.IsMineableTileOfAny(point, tiles),
            accept => AutoWorldKnowledge.FindNearestMineableOfAny(
                ctx.Me.Center, ScanRadiusTiles, tiles, accept));
        return ore.HasValue
            ? MineTile(ctx, AutoGoalId.MineIronTierOre, ore)
            : ProspectDownward(ctx, AutoGoalId.MineIronTierOre);
    }

    /// <summary>
    /// Breaks the nearest pot with the pickaxe and collects its torches and coins. This is the
    /// non-combat torch/coin source the early flow relies on (§11 stage A) so the companion is not
    /// forced into slime fights just to make a Furnace. Returns AlternativeRequired when no pot is
    /// within range.
    /// </summary>
    public static AutoTaskResult LootPots(AutoContext ctx)
    {
        CollectWanted(ctx, IsTorchOrCoin);

        if (!AutoInventory.HasBackpackCapacityFor(ctx.Profile, ItemID.Torch))
            return AutoTaskResult.RetryLater;

        Point? pot = ResolveTarget(ctx, AutoGoalId.GatherGel,
            point => IsTileType(point, TileID.Pots),
            accept => AutoWorldKnowledge.FindNearestTileOfType(ctx.Me.Center, ScanRadiusTiles, TileID.Pots, accept));
        if (pot is not Point potTile)
            return AutoTaskResult.AlternativeRequired;

        int pick = AutoInventory.BestToolIndex(ctx.Me, item => item.pick);
        if (pick < 0)
            return AutoTaskResult.AlternativeRequired;

        if (!ctx.Actuator.MoveAndWorkTile(ctx.Instance, ctx.Me, ctx.Navigator, pick, potTile))
        {
            ctx.TaskRuntime.RejectActiveTarget(2, 1800);
            return AutoTaskResult.AlternativeRequired;
        }
        return AutoTaskResult.Running;
    }

    public static bool IsTorchOrCoin(Item item) =>
        item.type == ItemID.Torch ||
        item.type == ItemID.CopperCoin || item.type == ItemID.SilverCoin ||
        item.type == ItemID.GoldCoin || item.type == ItemID.PlatinumCoin;

    private static AutoTaskResult MineTile(AutoContext ctx, AutoGoalId goal, Point? target)
    {
        if (target is not Point tile)
            return AutoTaskResult.AlternativeRequired;

        int pick = AutoInventory.BestToolIndex(ctx.Me, item => item.pick);
        if (pick < 0)
            return AutoTaskResult.AlternativeRequired;

        if (!ctx.Actuator.MoveAndWorkTile(ctx.Instance, ctx.Me, ctx.Navigator, pick, tile))
        {
            ctx.TaskRuntime.RejectActiveTarget(3, 1800);
            return AutoTaskResult.AlternativeRequired;
        }
        return AutoTaskResult.Running;
    }

    /// <summary>
    /// Legal fallback when no ore is visible in the bounded scan: dig a two-tile-wide shaft with
    /// the held pickaxe until a cavern or ore enters the local scan. This is intentionally slow and
    /// physical; it does not reveal or manufacture a remote vein.
    /// </summary>
    private static AutoTaskResult ProspectDownward(AutoContext ctx, AutoGoalId goal)
    {
        int pick = AutoInventory.BestToolIndex(ctx.Me, item => item.pick);
        if (pick < 0)
            return AutoTaskResult.AlternativeRequired;

        Point feet = ctx.Me.Bottom.ToTileCoordinates();
        int centerX = (int)(ctx.Me.Center.X / 16f);
        for (int y = feet.Y; y <= feet.Y + 5; y++)
        {
            for (int offset = 0; offset <= 1; offset++)
            {
                int x = centerX + (ctx.Me.direction >= 0 ? offset : -offset);
                if (!WorldGen.InWorld(x, y, 10))
                    continue;
                Tile tile = Main.tile[x, y];
                if (!tile.HasTile || Main.tileFrameImportant[tile.TileType] || !WorldGen.CanKillTile(x, y))
                    continue;
                Point target = new(x, y);
                ctx.TaskRuntime.SetActiveTarget(goal, target);
                if (!ctx.Actuator.MoveAndWorkTile(ctx.Instance, ctx.Me, ctx.Navigator, pick, target))
                    return AutoTaskResult.AlternativeRequired;
                return AutoTaskResult.Running;
            }
        }

        // Standing over an already-open shaft/cavern: ordinary gravity handles the descent. A
        // small lateral input avoids balancing forever on a single remaining lip.
        ctx.Navigator.Navigate(ctx.Me, ctx.Me.Center + new Vector2(ctx.Me.direction * 20f, 96f));
        return AutoTaskResult.Running;
    }

    private static Point? ResolveTarget(AutoContext ctx, AutoGoalId goal,
        System.Func<Point, bool> valid, System.Func<System.Func<Point, bool>, Point?> find)
    {
        AutoTaskRuntime runtime = ctx.TaskRuntime;
        if (runtime.TryGetActiveTarget(goal, valid, out Point active))
            return active;

        Point? found = find(point => !runtime.IsTargetRejected(goal, point));
        if (found is Point target)
            runtime.SetActiveTarget(goal, target);
        return found;
    }

    private static bool IsTileType(Point point, int tileType) =>
        WorldGen.InWorld(point.X, point.Y, 4) && Main.tile[point.X, point.Y].HasTile &&
        Main.tile[point.X, point.Y].TileType == tileType;

    /// <summary>Picks up nearby drops matching the current goal (its own knocked-loose resources).</summary>
    private static void CollectWanted(AutoContext ctx, System.Func<Item, bool> wanted) =>
        AutoInventory.TryCollectNearby(ctx.Instance, ctx.Profile, PickupRangeTiles, wanted);

    /// <summary>
    /// Best-effort replanting (§7.2, §17.4): if the companion is holding an acorn and there is bare
    /// plantable grass with headroom right next to it, plant one so the forest is not stripped bare.
    /// </summary>
    private static bool TryReplant(AutoContext ctx)
    {
        if (ctx.Actuator.ItemUseSpent || AutoInventory.Count(ctx.Profile, ItemID.Acorn) <= 0)
            return false;

        int acorn = AutoInventory.FindInventoryIndex(ctx.Me, item => item.type == ItemID.Acorn);
        if (acorn < 0)
            return false;

        Point feet = new((int)(ctx.Me.Bottom.X / 16f), (int)(ctx.Me.Bottom.Y / 16f));
        for (int dx = -2; dx <= 2; dx++)
        {
            int x = feet.X + dx;
            int groundY = feet.Y;
            if (!WorldGen.InWorld(x, groundY, 5))
                continue;

            Tile ground = Main.tile[x, groundY];
            Tile above = Main.tile[x, groundY - 1];
            bool grass = ground.HasTile && IsTreePlantingGround(ground.TileType);
            bool clearAbove = !above.HasTile;
            if (grass && clearAbove && AutoActuator.IsTileInReach(ctx.Me, ctx.Me.inventory[acorn], x, groundY - 1))
            {
                return ctx.Actuator.UseItemInSlot(ctx.Instance, ctx.Me, acorn,
                    new Vector2(x * 16f + 8f, (groundY - 1) * 16f + 8f), new Point(x, groundY - 1));
            }
        }
        return false;
    }

    /// <summary>Restores one recorded felled-tree site. A nullable result distinguishes “nothing
    /// actionable this tick” from active travel/item use; the caller may then chop another tree to
    /// obtain a missing acorn without forgetting the old site.</summary>
    public static AutoTaskResult? ServicePendingReplant(AutoContext ctx, bool allowLongTravel = false)
    {
        if (!ctx.State.TryPeekReplant(out Point recorded))
            return null;

        if (TreeOrSaplingAt(recorded))
        {
            ctx.State.CompleteReplant(recorded);
            ctx.TaskRuntime.RecordWorldProgress();
            return AutoTaskResult.Running;
        }

        Point? plant = FindPlantableTreeSite(recorded);
        if (plant is not Point site)
        {
            // New player construction or transformed terrain can make the exact site impossible.
            // Keep the debt queued: a later world change or seed source may make it plantable.
            return null;
        }
        if (site != recorded)
        {
            ctx.State.RelocateReplant(recorded, site);
            recorded = site;
        }

        int acorn = AutoInventory.FindInventoryIndex(ctx.Me, item => item.type == ItemID.Acorn);
        if (acorn < 0)
            return null;

        Vector2 target = new(site.X * 16f + 8f, site.Y * 16f + 8f);
        float distance = Vector2.Distance(ctx.Me.Center, target);
        // Opportunistic maintenance called outside the watched GatherWood plan must stay local.
        // Otherwise one old planting debt can silently own movement forever with no watchdog.
        if (!allowLongTravel && distance > 12f * 16f)
            return null;
        if (distance > 6f * 16f)
            return AutoWorldTravelController.TravelTo(ctx, target, 4f);
        if (!AutoActuator.IsTileInReach(ctx.Me, ctx.Me.inventory[acorn], site.X, site.Y))
        {
            ctx.Navigator.Navigate(ctx.Me, target);
            return AutoTaskResult.Running;
        }

        ctx.Navigator.Idle(ctx.Me);
        ctx.Actuator.UseItemInSlot(ctx.Instance, ctx.Me, acorn, target, site);
        return AutoTaskResult.Running;
    }

    private static Point? FindPlantableTreeSite(Point root)
    {
        if (IsPlantableTreeSite(root))
            return root;
        for (int radius = 1; radius <= 24; radius++)
        for (int side = 0; side < 2; side++)
        {
            int dx = side == 0 ? -radius : radius;
            for (int vertical = 0; vertical <= 8; vertical++)
            for (int verticalSide = 0; verticalSide < (vertical == 0 ? 1 : 2); verticalSide++)
            {
                int dy = vertical == 0 ? 0 : verticalSide == 0 ? -vertical : vertical;
                Point site = new(root.X + dx, root.Y + dy);
                if (IsPlantableTreeSite(site))
                    return site;
            }
        }
        return null;
    }

    private static bool IsPlantableTreeSite(Point site)
    {
        if (!WorldGen.InWorld(site.X, site.Y, 5))
            return false;
        Tile ground = Main.tile[site.X, site.Y + 1];
        if (!ground.HasTile || ground.IsActuated || !IsTreePlantingGround(ground.TileType))
            return false;
        for (int y = site.Y - 2; y <= site.Y; y++)
            if (Main.tile[site.X, y].HasTile)
                return false;
        for (int x = site.X - 3; x <= site.X + 3; x++)
        for (int y = site.Y - 3; y <= site.Y + 1; y++)
        {
            if (!WorldGen.InWorld(x, y, 5))
                continue;
            Tile tile = Main.tile[x, y];
            if (tile.HasTile && (tile.TileType == TileID.Saplings ||
                tile.TileType < Main.tileAxe.Length && Main.tileAxe[tile.TileType]))
                return false;
        }
        return true;
    }

    private static bool TreeOrSaplingAt(Point site)
    {
        if (!WorldGen.InWorld(site.X, site.Y, 5))
            return false;
        Tile tile = Main.tile[site.X, site.Y];
        return tile.HasTile && (tile.TileType == TileID.Saplings ||
            tile.TileType < Main.tileAxe.Length && Main.tileAxe[tile.TileType]);
    }

    private static bool IsTreePlantingGround(int type) => type is
        TileID.Grass or TileID.CorruptGrass or TileID.CrimsonGrass or TileID.HallowedGrass or
        TileID.JungleGrass or TileID.AshGrass or TileID.SnowBlock or
        TileID.Sand or TileID.Ebonsand or TileID.Crimsand or TileID.Pearlsand;

    public static bool IsBasicOreItem(Item item)
    {
        foreach (int type in BasicOreItems)
        {
            if (item.type == type)
                return true;
        }
        return false;
    }

    public static int CountBasicOre(CompanionProfile profile)
    {
        int total = 0;
        foreach (int type in BasicOreItems)
            total += AutoInventory.Count(profile, type);
        return total;
    }

    /// <summary>Perfect-information campaign mining. The target comes from the frame-sliced world
    /// index, but travel, pick power, tile hooks and collection remain ordinary gameplay actions.</summary>
    public static AutoTaskResult MineIndexedResource(AutoContext ctx, int[] tileTypes,
        int[] itemTypes, int targetAmount)
    {
        CollectWanted(ctx, item => Contains(itemTypes, item.type));
        if (CountAny(ctx.Profile, itemTypes) >= targetAmount)
            return AutoTaskResult.Succeeded;
        if (!AutoInventory.HasBackpackCapacityFor(ctx.Profile, itemTypes))
            return AutoTaskResult.RetryLater;

        bool Valid(Point point) => WorldGen.InWorld(point.X, point.Y, 5) &&
            Main.tile[point.X, point.Y].HasTile && Contains(tileTypes, Main.tile[point.X, point.Y].TileType) &&
            WorldGen.CanKillTile(point.X, point.Y);

        Point target;
        if (!ctx.TaskRuntime.TryGetActiveTarget(AutoGoalId.UpgradeClassLoadout, Valid, out target))
        {
            Point? indexed = AutoWorldIndexSystem.FindNearest(ctx.Me.Center, tileTypes,
                p => !ctx.TaskRuntime.IsTargetRejected(AutoGoalId.UpgradeClassLoadout, p));
            if (indexed is not Point found)
                return AutoWorldIndexSystem.ScanComplete
                    ? AutoTaskResult.AlternativeRequired : AutoExplorationController.Explore(ctx);
            target = found;
            ctx.TaskRuntime.SetActiveTarget(AutoGoalId.UpgradeClassLoadout, target);
        }

        Vector2 targetWorld = new(target.X * 16f + 8f, target.Y * 16f + 8f);
        if (Vector2.DistanceSquared(ctx.Me.Center, targetWorld) > 7f * 16f * 7f * 16f)
            return AutoWorldTravelController.TravelTo(ctx, targetWorld, 6f);
        int pick = AutoInventory.BestToolIndex(ctx.Me, item => item.pick);
        if (pick < 0 || !ctx.Actuator.MoveAndWorkTile(ctx.Instance, ctx.Me, ctx.Navigator, pick, target))
        {
            ctx.TaskRuntime.RejectActiveTarget(4, 3600);
            return AutoTaskResult.AlternativeRequired;
        }
        return AutoTaskResult.Running;
    }

    public static int CountAny(CompanionProfile profile, int[] itemTypes)
    {
        int total = 0;
        foreach (int type in itemTypes)
            total += AutoInventory.Count(profile, type);
        return total;
    }

    private static bool Contains(int[] values, int value)
    {
        foreach (int candidate in values)
            if (candidate == value)
                return true;
        return false;
    }
}
