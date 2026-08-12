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
        CollectWanted(ctx, item => item.type == ItemID.Wood || item.type == ItemID.Acorn);
        if (AutoInventory.Count(ctx.Profile, ItemID.Wood) >= target)
            return AutoTaskResult.Succeeded;

        if (!AutoInventory.HasBackpackSpace(ctx.Profile))
            return AutoTaskResult.RetryLater; // full: let a deposit/tidy goal run first

        Point? tree = ResolveTarget(ctx, AutoGoalId.GatherWood,
            AutoWorldKnowledge.IsChoppableTree,
            accept => AutoWorldKnowledge.FindNearestTree(ctx.Me.Center, ScanRadiusTiles, accept));
        TryReplant(ctx);
        if (tree is not Point treeTile)
            return AutoTaskResult.AlternativeRequired; // nothing local; relocate

        int axe = AutoInventory.BestToolIndex(ctx.Me, item => item.axe);
        if (axe < 0)
            return AutoTaskResult.AlternativeRequired;

        if (!ctx.Actuator.MoveAndWorkTile(ctx.Instance, ctx.Me, ctx.Navigator, axe, treeTile))
        {
            ctx.TaskRuntime.RejectActiveTarget(16, 1800);
            return AutoTaskResult.AlternativeRequired;
        }
        return AutoTaskResult.Running;
    }

    /// <summary>Mines the nearest stone, collecting stone blocks.</summary>
    public static AutoTaskResult GatherStone(AutoContext ctx, int target)
    {
        CollectWanted(ctx, item => item.type == ItemID.StoneBlock);
        if (AutoInventory.Count(ctx.Profile, ItemID.StoneBlock) >= target)
            return AutoTaskResult.Succeeded;

        if (!AutoInventory.HasBackpackSpace(ctx.Profile))
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

        if (!AutoInventory.HasBackpackSpace(ctx.Profile))
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
        if (!AutoInventory.HasBackpackSpace(ctx.Profile))
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

        if (!AutoInventory.HasBackpackSpace(ctx.Profile))
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
    private static void TryReplant(AutoContext ctx)
    {
        if (ctx.Actuator.ItemUseSpent || AutoInventory.Count(ctx.Profile, ItemID.Acorn) <= 0)
            return;

        int acorn = AutoInventory.FindInventoryIndex(ctx.Me, item => item.type == ItemID.Acorn);
        if (acorn < 0)
            return;

        Point feet = new((int)(ctx.Me.Bottom.X / 16f), (int)(ctx.Me.Bottom.Y / 16f));
        for (int dx = -2; dx <= 2; dx++)
        {
            int x = feet.X + dx;
            int groundY = feet.Y;
            if (!WorldGen.InWorld(x, groundY, 5))
                continue;

            Tile ground = Main.tile[x, groundY];
            Tile above = Main.tile[x, groundY - 1];
            bool grass = ground.HasTile && (ground.TileType == TileID.Grass || ground.TileType == TileID.HallowedGrass);
            bool clearAbove = !above.HasTile;
            if (grass && clearAbove && AutoActuator.IsTileInReach(ctx.Me, ctx.Me.inventory[acorn], x, groundY - 1))
            {
                ctx.Actuator.UseItemInSlot(ctx.Instance, ctx.Me, acorn,
                    new Vector2(x * 16f + 8f, (groundY - 1) * 16f + 8f), new Point(x, groundY - 1));
                return;
            }
        }
    }

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
}
