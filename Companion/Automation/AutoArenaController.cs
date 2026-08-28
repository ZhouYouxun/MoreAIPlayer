using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;

namespace MoreAIPlayer.Companion.Automation;

/// <summary>Builds persistent, revalidated boss arenas. Underground rooms are excavated with
/// dynamite when available and the pickaxe otherwise; downward blasting deliberately keeps normal
/// explosives while lateral/upper throws prefer sticky variants.</summary>
public static class AutoArenaController
{
    private const int BaseWidth = 120;
    private const int RowSpacing = 11;
    private const int DefaultRows = 3;
    private const int CampfireSpacing = 80;
    private static long nextExplosionTick;

    internal static void ResetWorldSession() => nextExplosionTick = 0;

    public static bool IsPrepared(AutoCompanionState state)
    {
        if (!state.ArenaCompleted || state.ArenaAnchor is not Point anchor ||
            state.ArenaStage != state.CampaignStage)
            return false;
        int width = Width(state);
        return Validate(anchor, width, Rows(state.CampaignStage), Underground(state.CampaignStage)) &&
            CampfiresComplete(anchor, width);
    }

    /// <summary>Platform count for the live stage/failure variant, used by the shared resource
    /// ledger before the arena starts crafting materials.</summary>
    public static int RequiredPlatformCount(AutoCompanionState state)
    {
        if (state is null)
            return BaseWidth * DefaultRows;
        return Width(state) * Rows(state.CampaignStage);
    }

    public static int RequiredCampfireCount(AutoCompanionState state) =>
        System.Math.Max(1, (Width(state) - 20 + CampfireSpacing - 1) / CampfireSpacing);

    public static bool NeedsDepositForNextSupply(AutoContext ctx)
    {
        if (AutoInventory.HasBackpackSpace(ctx.Profile) || ctx.State.ArenaAnchor is not Point anchor)
            return false;
        int width = Width(ctx.State);
        int rows = Rows(ctx.State.CampaignStage);
        if (!Validate(anchor, width, rows, Underground(ctx.State.CampaignStage)) &&
            AutoInventory.Count(ctx.Profile, ItemID.WoodPlatform) < width * rows)
            return !AutoInventory.HasBackpackCapacityFor(ctx.Profile, ItemID.WoodPlatform);
        if (FindMissingCampfire(anchor, width).HasValue &&
            !AutoInventory.Has(ctx.Profile, ItemID.Campfire))
        {
            if (AutoInventory.Count(ctx.Profile, ItemID.Torch) < 5)
                return !AutoInventory.HasBackpackCapacityFor(ctx.Profile, ItemID.Torch);
            return !AutoInventory.HasBackpackCapacityFor(ctx.Profile, ItemID.Campfire);
        }
        return false;
    }

    public static AutoTaskResult Prepare(AutoContext ctx)
    {
        AutoCompanionState state = ctx.State;
        if (state.ArenaStage != state.CampaignStage)
        {
            state.ArenaStage = state.CampaignStage;
            state.ArenaAnchor = null;
            state.ArenaCompleted = false;
        }
        if (state.ArenaAnchor is not Point anchor || !WorldGen.InWorld(anchor.X, anchor.Y, 80))
        {
            Point? selected = SelectAnchor(ctx);
            if (selected is not Point point)
                return AutoWorldIndexSystem.ScanComplete
                    ? AutoTaskResult.AlternativeRequired : AutoExplorationController.Explore(ctx);
            state.ArenaAnchor = point;
            anchor = point;
        }

        int width = Width(state);
        int rows = Rows(state.CampaignStage);
        if (Validate(anchor, width, rows, Underground(state.CampaignStage)))
        {
            AutoTaskResult buffs = PrepareCampfires(ctx, anchor, width);
            if (buffs == AutoTaskResult.Succeeded)
            {
                state.ArenaCompleted = true;
                return AutoTaskResult.Succeeded;
            }
            state.ArenaCompleted = false;
            return buffs;
        }
        state.ArenaCompleted = false;

        if (AutoInventory.Count(ctx.Profile, ItemID.WoodPlatform) < width * rows)
        {
            if (AutoInventory.Count(ctx.Profile, ItemID.Wood) < 2)
                return AutoTaskResult.AlternativeRequired;
            if (AutoCraftingService.TryCraftUntil(ctx.Instance, ctx.Profile,
                ItemID.WoodPlatform, width * rows))
                return AutoTaskResult.Running;
        }

        // Insurance runs only after the ordinary recipe has converted the conservative raw-wood
        // budget into the exact platform count. Running it before this point consumed one raw wood
        // per platform and could strand a half-built long bridge despite having budgeted 2:1.
        if (ctx.TaskRuntime.RecoveryLevel >= 7 && ForcePrepareInsurance(ctx, anchor, width, rows))
        {
            ctx.TaskRuntime.RecordWorldProgress();
            return AutoTaskResult.Running;
        }

        Point? obstruction = FindObstruction(anchor, width, rows, Underground(state.CampaignStage));
        if (obstruction is Point blocked)
            return Excavate(ctx, anchor, blocked);

        Point? missing = FindMissingPlatform(anchor, width, rows);
        if (missing is Point platform)
        {
            int slot = AutoInventory.FindInventoryIndex(ctx.Me, item => item.type == ItemID.WoodPlatform);
            if (slot < 0)
                return AutoTaskResult.AlternativeRequired;
            ctx.Actuator.MoveAndPlace(ctx.Instance, ctx.Me, ctx.Navigator, slot, platform);
            return AutoTaskResult.Running;
        }

        return AutoTaskResult.RetryLater;
    }

    private static AutoTaskResult Excavate(AutoContext ctx, Point anchor, Point target)
    {
        Vector2 targetWorld = new(target.X * 16f + 8f, target.Y * 16f + 8f);
        if (Vector2.DistanceSquared(ctx.Me.Center, targetWorld) > 16f * 16f * 16f * 16f)
            return AutoWorldTravelController.TravelTo(ctx, targetWorld, 12f);

        if ((long)Main.GameUpdateCount >= nextExplosionTick && TryUseExplosive(ctx, anchor, target))
        {
            nextExplosionTick = (long)Main.GameUpdateCount + 150;
            return AutoTaskResult.Running;
        }

        int pick = AutoInventory.BestToolIndex(ctx.Me, item => item.pick);
        if (pick < 0)
            return AutoTaskResult.AlternativeRequired;
        ctx.Actuator.MoveAndWorkTile(ctx.Instance, ctx.Me, ctx.Navigator, pick, target);
        return AutoTaskResult.Running;
    }

    private static bool TryUseExplosive(AutoContext ctx, Point anchor, Point target)
    {
        bool downward = target.Y > ctx.Me.Center.Y / 16f + 3f;
        int desired = downward ? ItemID.Dynamite : ItemID.StickyDynamite;
        int slot = AutoInventory.FindInventoryIndex(ctx.Me, item => item.type == desired);
        if (slot < 0 && !downward && AutoInventory.Has(ctx.Profile, ItemID.Dynamite) &&
            AutoInventory.Has(ctx.Profile, ItemID.Gel) &&
            AutoCraftingService.TryCraft(ctx.Instance, ctx.Profile, ItemID.StickyDynamite))
            return true;
        if (slot < 0)
        {
            desired = downward ? ItemID.Bomb : ItemID.StickyBomb;
            slot = AutoInventory.FindInventoryIndex(ctx.Me, item => item.type == desired);
        }
        if (slot < 0)
            return false;

        Vector2 aim = new(target.X * 16f + 8f, target.Y * 16f + 8f);
        return ctx.Actuator.UseItemInSlot(ctx.Instance, ctx.Me, slot, aim);
    }

    private static Point? SelectAnchor(AutoContext ctx)
    {
        AutoCampaignStage stage = ctx.State.CampaignStage;
        int shift = ((ctx.State.StrategyVariant % 5) - 2) * 180;
        if (stage == AutoCampaignStage.WallOfFlesh)
            return new Point(ctx.State.StrategyVariant % 2 == 0 ? 550 : Main.maxTilesX - 550,
                Main.maxTilesY - 170);
        if (stage == AutoCampaignStage.Plantera)
        {
            Point? jungle = AutoWorldIndexSystem.FindJungleLandmark(ctx.Me.Center);
            if (jungle is Point j)
                return new Point(System.Math.Clamp(j.X + shift / 3, 100, Main.maxTilesX - 100),
                    System.Math.Clamp(j.Y, (int)Main.rockLayer + 60, Main.maxTilesY - 300));
            return null;
        }
        if (stage == AutoCampaignStage.Golem)
        {
            Point? temple = AutoWorldIndexSystem.FindTempleLandmark(ctx.Me.Center);
            return temple is Point t ? new Point(t.X, t.Y - 5) : null;
        }

        if (stage == AutoCampaignStage.EvilBoss)
        {
            Point? evil = AutoWorldIndexSystem.FindEvilLandmark(ctx.Me.Center);
            if (evil is not Point e)
                return null;
            int evilSurface = FindSurface(e.X);
            return new Point(e.X, evilSurface - 8);
        }

        int baseX = stage is AutoCampaignStage.Skeletron or AutoCampaignStage.LunaticCultist
            ? Main.dungeonX : Main.spawnTileX;
        int x = System.Math.Clamp(baseX + shift, 100, Main.maxTilesX - 100);
        int surface = FindSurface(x);
        return new Point(x, surface - 8);
    }

    private static int FindSurface(int x)
    {
        int start = System.Math.Max(80, (int)Main.worldSurface - 180);
        int end = System.Math.Min(Main.maxTilesY - 200, (int)Main.worldSurface + 240);
        for (int y = start; y < end; y++)
        {
            Tile tile = Main.tile[x, y];
            if (tile.HasTile && Main.tileSolid[tile.TileType] && !Main.tileSolidTop[tile.TileType])
                return y;
        }
        return Main.spawnTileY;
    }

    private static Point? FindObstruction(Point anchor, int width, int rows, bool underground)
    {
        int half = width / 2;
        int top = anchor.Y - (rows - 1) * RowSpacing - 5;
        int bottom = anchor.Y + 3;
        for (int y = top; y <= bottom; y++)
        for (int x = anchor.X - half + 1; x < anchor.X + half; x++)
        {
            if (!WorldGen.InWorld(x, y, 8))
                continue;
            bool platformRow = IsPlatformRow(anchor, y, rows);
            if (!underground && !platformRow)
                continue;
            Tile tile = Main.tile[x, y];
            if (tile.HasTile && Main.tileSolid[tile.TileType] && !Main.tileSolidTop[tile.TileType])
                return new Point(x, y);
        }
        return null;
    }

    private static Point? FindMissingPlatform(Point anchor, int width, int rows)
    {
        int half = width / 2;
        for (int row = 0; row < rows; row++)
        {
            int y = anchor.Y - row * RowSpacing;
            for (int x = anchor.X - half + 1; x < anchor.X + half; x++)
            {
                Tile tile = Main.tile[x, y];
                if (!tile.HasTile)
                    return new Point(x, y);
                if (tile.TileType != TileID.Platforms)
                    return null;
            }
        }
        return null;
    }

    private static bool Validate(Point anchor, int width, int rows, bool underground)
    {
        int half = width / 2;
        int present = 0;
        int required = (width - 2) * rows;
        for (int row = 0; row < rows; row++)
        {
            int y = anchor.Y - row * RowSpacing;
            for (int x = anchor.X - half + 1; x < anchor.X + half; x++)
            {
                if (!WorldGen.InWorld(x, y, 8))
                    return false;
                Tile tile = Main.tile[x, y];
                if (tile.HasTile && tile.TileType == TileID.Platforms)
                    present++;
            }
        }
        if (present < required)
            return false;
        return !underground || FindObstruction(anchor, width, rows, true) is null;
    }

    private static AutoTaskResult PrepareCampfires(AutoContext ctx, Point anchor, int width)
    {
        Point? missing = FindMissingCampfire(anchor, width);
        if (missing is not Point target)
            return AutoTaskResult.Succeeded;

        int slot = AutoInventory.FindInventoryIndex(ctx.Me, item => item.type == ItemID.Campfire);
        if (slot < 0)
        {
            if (AutoInventory.Count(ctx.Profile, ItemID.Torch) < 5)
            {
                if (AutoInventory.Count(ctx.Profile, ItemID.Gel) > 0 &&
                    AutoInventory.Count(ctx.Profile, ItemID.Wood) >
                        AutoResourceBudget.OperationalReserve(ctx.State) &&
                    AutoCraftingService.TryCraftUntil(ctx.Instance, ctx.Profile, ItemID.Torch, 5))
                    return AutoTaskResult.Running;
                return AutoResourceController.LootPots(ctx);
            }
            if (AutoInventory.Count(ctx.Profile, ItemID.Wood) < 10)
                return AutoTaskResult.AlternativeRequired;
            return AutoCraftingService.TryCraft(ctx.Instance, ctx.Profile, ItemID.Campfire)
                ? AutoTaskResult.Running : AutoTaskResult.RetryLater;
        }

        ctx.Actuator.MoveAndPlace(ctx.Instance, ctx.Me, ctx.Navigator, slot, target);
        return AutoTaskResult.Running;
    }

    private static bool CampfiresComplete(Point anchor, int width) =>
        FindMissingCampfire(anchor, width) is null;

    private static Point? FindMissingCampfire(Point anchor, int width)
    {
        int count = System.Math.Max(1, (width - 20 + CampfireSpacing - 1) / CampfireSpacing);
        int left = anchor.X - width / 2 + 10;
        int span = System.Math.Max(0, width - 20);
        for (int i = 0; i < count; i++)
        {
            int x = count == 1 ? anchor.X : left + i * span / (count - 1);
            bool present = false;
            for (int dx = -3; dx <= 3 && !present; dx++)
            for (int dy = -3; dy <= 1 && !present; dy++)
                present = IsTile(x + dx, anchor.Y + dy, TileID.Campfire);
            if (!present)
                return new Point(x, anchor.Y - 1);
        }
        return null;
    }

    private static int Width(AutoCompanionState state) => state.CampaignStage switch
    {
        AutoCampaignStage.WallOfFlesh => 1000 + System.Math.Min(600, state.StageFailureCount * 100),
        AutoCampaignStage.Golem => 60,
        _ => BaseWidth + System.Math.Min(90, state.StageFailureCount * 15)
    };

    private static int Rows(AutoCampaignStage stage) => stage switch
    {
        AutoCampaignStage.WallOfFlesh => 1,
        AutoCampaignStage.Golem => 2,
        _ => DefaultRows
    };

    private static bool Underground(AutoCampaignStage stage) => stage is
        AutoCampaignStage.WallOfFlesh or AutoCampaignStage.Plantera or AutoCampaignStage.Golem;

    private static bool IsPlatformRow(Point anchor, int y, int rows)
    {
        for (int row = 0; row < rows; row++)
            if (y == anchor.Y - row * RowSpacing)
                return true;
        return false;
    }

    private static bool ForcePrepareInsurance(AutoContext ctx, Point anchor, int width, int rows)
    {
        bool singlePlayerAuthority = Main.netMode == NetmodeID.SinglePlayer;
        if (!singlePlayerAuthority)
            return false;
        // Last rung only: a protected/modded tile layout defeated actual pick and explosive use.
        // Consume the same wood budget, then force the minimum safe geometry so the campaign can
        // continue instead of reporting a permanent unsupported world.
        int needed = 0;
        int half = width / 2;
        for (int row = 0; row < rows; row++)
        for (int x = anchor.X - half + 1; x < anchor.X + half; x++)
            if (!IsTile(x, anchor.Y - row * RowSpacing, TileID.Platforms))
                needed++;
        int availablePlatforms = AutoInventory.Count(ctx.Profile, ItemID.WoodPlatform);
        int availableWood = AutoInventory.Count(ctx.Profile, ItemID.Wood);
        if (availablePlatforms + availableWood * 2 < needed)
            return false;

        if (Underground(ctx.State.CampaignStage))
        {
            int top = anchor.Y - (rows - 1) * RowSpacing - 5;
            int bottom = anchor.Y + 3;
            for (int y = top; y <= bottom; y++)
            for (int x = anchor.X - half + 1; x < anchor.X + half; x++)
            {
                Tile tile = Main.tile[x, y];
                if (!tile.HasTile)
                    continue;
                WorldGen.KillTile(x, y, false, false, true);
                if (Main.tile[x, y].HasTile)
                    Main.tile[x, y].ClearTile();
            }
        }

        for (int row = 0; row < rows; row++)
        {
            int y = anchor.Y - row * RowSpacing;
            for (int x = anchor.X - half + 1; x < anchor.X + half; x++)
            {
                if (IsTile(x, y, TileID.Platforms))
                    continue;
                if (!ConsumePlacementMaterial(ctx.Profile))
                    return false;
                if (Main.tile[x, y].HasTile)
                    WorldGen.KillTile(x, y, false, false, true);
                WorldGen.PlaceTile(x, y, TileID.Platforms, true, true);
            }
        }
        return Validate(anchor, width, rows, false);
    }

    private static bool ConsumePlacementMaterial(CompanionProfile profile)
    {
        foreach (Item[] items in new[] { profile.Hotbar, profile.Backpack })
        foreach (Item item in items)
        {
            if (item.type != ItemID.WoodPlatform || item.stack <= 0)
                continue;
            if (--item.stack <= 0)
                item.TurnToAir();
            return true;
        }
        foreach (Item[] items in new[] { profile.Hotbar, profile.Backpack })
        foreach (Item item in items)
        {
            if (item.type != ItemID.Wood || item.stack <= 0)
                continue;
            if (--item.stack <= 0)
                item.TurnToAir();
            return true; // one wood crafts two platforms; consuming one is conservative
        }
        return false;
    }

    private static bool IsTile(int x, int y, int type) => WorldGen.InWorld(x, y, 5) &&
        Main.tile[x, y].HasTile && Main.tile[x, y].TileType == type;
}
