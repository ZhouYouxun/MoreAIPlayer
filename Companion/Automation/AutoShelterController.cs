using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace MoreAIPlayer.Companion.Automation;

/// <summary>Real-time aerial town builder. Individually revalidated rooms sit about twenty tiles
/// above natural ground on climbable wooden-platform columns. An imported established world gets a
/// compact personal foothold instead of a duplicate home for all of that world's existing NPCs.</summary>
public static class AutoShelterController
{
    private const int RoomWidth = 12;
    private const int RoomHeight = 7;
    private const int Elevation = 20;
    private const int SiteRadius = 480;

    private readonly record struct ShelterLayout(int RoomCount)
    {
        public int TotalWidth => (RoomWidth - 1) * RoomCount + 1;
    }

    private static ShelterLayout Layout(AutoCompanionState state) =>
        new(AutoResourceBudget.RequiredHousingRooms(state));

    public static AutoTaskResult Build(AutoContext ctx)
    {
        ShelterLayout layout = Layout(ctx.State);
        // A site which repeatedly defeats real mining/placement is abandoned after the watchdog's
        // relocation rung. The partially built shell makes InitialSiteSafe reject the same spot,
        // while StrategyVariant changes the next search direction. This closes the old permanent
        // loop on an unmineable block or a structure placed through the template mid-build.
        if (ctx.TaskRuntime.RecoveryLevel >= 3 && ctx.State.HomeAnchor.HasValue &&
            !IsShelterComplete(ctx.State.HomeAnchor.Value, layout))
        {
            ctx.State.HomeAnchor = null;
            ctx.State.ShelterCompleted = false;
            ctx.State.StrategyVariant++;
            ctx.Navigator.Reset();
        }
        if (ctx.State.HomeAnchor is not Point anchor || !AnchorUsable(anchor, layout))
        {
            ctx.State.HomeAnchor = FindSite(ctx.State.StrategyVariant, layout);
            ctx.State.ShelterCompleted = false;
            if (ctx.State.HomeAnchor is not Point selected)
                return AutoTaskResult.AlternativeRequired;
            anchor = selected;
        }

        if (IsShelterComplete(anchor, layout))
        {
            ctx.State.ShelterCompleted = true;
            ctx.State.HousingRoomsCompleted = layout.RoomCount;
            CaptureFurnitureAnchors(ctx, anchor, layout);
            return AutoTaskResult.Succeeded;
        }
        ctx.State.ShelterCompleted = false;

        AutoTaskResult supplies = EnsureSupplies(ctx, anchor, layout);
        if (supplies != AutoTaskResult.Succeeded)
            return supplies;

        if (TryClearTemplate(ctx, anchor, layout) || TryPlaceSupportColumns(ctx, anchor, layout) ||
            TryPlaceShell(ctx, anchor, layout) || TryPlaceWalls(ctx, anchor, layout) ||
            TryPlaceFurniture(ctx, anchor, layout))
            return AutoTaskResult.Running;
        return AutoTaskResult.RetryLater;
    }

    public static bool IsShelterComplete(Point a, AutoCompanionState state) =>
        IsShelterComplete(a, Layout(state));

    private static bool IsShelterComplete(Point a, ShelterLayout layout)
    {
        if (!AnchorUsable(a, layout))
            return false;
        if (MissingSupport(a, layout) || !RoomHas(a, 0, TileID.Containers))
            return false;
        int floorY = a.Y + RoomHeight - 1;
        for (int x = a.X; x < a.X + layout.TotalWidth; x++)
        {
            if (!IsTile(x, a.Y, TileID.WoodBlock) || !IsTile(x, floorY, TileID.WoodBlock))
                return false;
        }

        for (int room = 0; room < layout.RoomCount; room++)
        {
            int left = a.X + room * (RoomWidth - 1);
            int right = left + RoomWidth - 1;
            for (int y = a.Y + 1; y < floorY; y++)
            {
                if (!IsTile(left, y, TileID.WoodBlock) && room == 0)
                    return false;
                if (y < floorY - 3 && !IsTile(right, y, TileID.WoodBlock))
                    return false;
            }
            for (int x = left + 1; x < right; x++)
            for (int y = a.Y + 1; y < floorY; y++)
                if (Main.tile[x, y].WallType != WallID.Wood)
                    return false;
            if (!RoomHas(a, room, TileID.WorkBenches) || !RoomHas(a, room, TileID.Chairs) ||
                !RoomHas(a, room, TileID.Torches) || !HasDoorAt(right, floorY))
                return false;
            if (!VanillaRoomIsValid(left, right, a.Y, floorY))
                return false;
        }
        return true;
    }

    /// <summary>Whether the next unfinished shelter operation must first create/collect an item
    /// type which cannot fit in the currently full backpack. Direct placements from already held
    /// stacks remain legal and must not be interrupted by storage.</summary>
    public static bool NeedsDepositForNextSupply(AutoContext ctx)
    {
        ShelterLayout layout = Layout(ctx.State);
        if (AutoInventory.HasBackpackSpace(ctx.Profile) ||
            ctx.State.HomeAnchor is not Point a || !AnchorUsable(a, layout))
            return false;
        if (MissingSupport(a, layout) && !AutoInventory.Has(ctx.Profile, ItemID.WoodPlatform))
            return !AutoInventory.HasBackpackCapacityFor(ctx.Profile, ItemID.WoodPlatform);
        if (MissingWall(a, layout) && !AutoInventory.Has(ctx.Profile, ItemID.WoodWall))
            return !AutoInventory.HasBackpackCapacityFor(ctx.Profile, ItemID.WoodWall);
        if (HasForeignWall(a, layout) && AutoInventory.BestHammer(ctx.Profile).hammer <= 0 &&
            !AutoInventory.Has(ctx.Profile, ItemID.WoodenHammer))
            return !AutoInventory.HasBackpackCapacityFor(ctx.Profile, ItemID.WoodenHammer);
        if (MissingFurniture(a, TileID.WorkBenches, layout) && !AutoInventory.Has(ctx.Profile, ItemID.WorkBench))
            return !AutoInventory.HasBackpackCapacityFor(ctx.Profile, ItemID.WorkBench);
        if (MissingFurniture(a, TileID.Chairs, layout) && !AutoInventory.Has(ctx.Profile, ItemID.WoodenChair))
            return !AutoInventory.HasBackpackCapacityFor(ctx.Profile, ItemID.WoodenChair);
        if (MissingDoor(a, layout) && !AutoInventory.Has(ctx.Profile, ItemID.WoodenDoor))
            return !AutoInventory.HasBackpackCapacityFor(ctx.Profile, ItemID.WoodenDoor);
        if (MissingFurniture(a, TileID.Torches, layout) && !AutoInventory.Has(ctx.Profile, ItemID.Torch))
            return !AutoInventory.HasBackpackCapacityFor(ctx.Profile, ItemID.Torch);
        if (!RoomHas(a, 0, TileID.Containers) && !AutoInventory.Has(ctx.Profile, ItemID.Chest))
        {
            int ironBars = AutoInventory.Count(ctx.Profile, ItemID.IronBar) +
                AutoInventory.Count(ctx.Profile, ItemID.LeadBar);
            return ironBars < 2
                ? !AutoInventory.HasBackpackCapacityFor(ctx.Profile, ItemID.IronOre, ItemID.LeadOre)
                : !AutoInventory.HasBackpackCapacityFor(ctx.Profile, ItemID.Chest);
        }
        return false;
    }

    /// <summary>Final authority is Terraria's own enclosure, furniture and home-spot scoring.
    /// Passing the visual template alone is not enough if a tiny wall leak or evil-biome score
    /// would still prevent a town NPC from using the room.</summary>
    private static bool VanillaRoomIsValid(int left, int right, int top, int bottom)
    {
        int interiorX = (left + right) / 2;
        int interiorY = (top + bottom) / 2;
        if (!WorldGen.StartRoomCheck(interiorX, interiorY) || !WorldGen.RoomNeeds(NPCID.Guide))
            return false;

        int occupant = -1;
        foreach (NPC npc in Main.ActiveNPCs)
        {
            if (!npc.active || !npc.townNPC || npc.homeless ||
                npc.homeTileX < left || npc.homeTileX > right ||
                npc.homeTileY < top || npc.homeTileY > bottom)
                continue;
            occupant = npc.whoAmI;
            break;
        }
        WorldGen.ScoreRoom(occupant, NPCID.Guide);
        return WorldGen.canSpawn && WorldGen.hiScore > 0;
    }

    private static AutoTaskResult EnsureSupplies(AutoContext ctx, Point a, ShelterLayout layout)
    {
        if (MissingSupport(a, layout) && !AutoInventory.Has(ctx.Profile, ItemID.WoodPlatform))
            return Craft(ctx, ItemID.WoodPlatform);
        if (MissingWall(a, layout) && !AutoInventory.Has(ctx.Profile, ItemID.WoodWall))
            return Craft(ctx, ItemID.WoodWall);
        if (HasForeignWall(a, layout) && AutoInventory.BestHammer(ctx.Profile).hammer <= 0 &&
            !AutoInventory.Has(ctx.Profile, ItemID.WoodenHammer))
            return Craft(ctx, ItemID.WoodenHammer);
        if (MissingFurniture(a, TileID.WorkBenches, layout) && !AutoInventory.Has(ctx.Profile, ItemID.WorkBench))
            return Craft(ctx, ItemID.WorkBench);
        if (MissingFurniture(a, TileID.Chairs, layout) && !AutoInventory.Has(ctx.Profile, ItemID.WoodenChair))
            return Craft(ctx, ItemID.WoodenChair);
        if (MissingDoor(a, layout) && !AutoInventory.Has(ctx.Profile, ItemID.WoodenDoor))
            return Craft(ctx, ItemID.WoodenDoor);
        if (MissingFurniture(a, TileID.Torches, layout) && !AutoInventory.Has(ctx.Profile, ItemID.Torch))
        {
            if (!AutoInventory.Has(ctx.Profile, ItemID.Gel))
                return AutoResourceController.LootPots(ctx);
            return Craft(ctx, ItemID.Torch);
        }
        if (!RoomHas(a, 0, TileID.Containers) && !AutoInventory.Has(ctx.Profile, ItemID.Chest))
        {
            if (AutoInventory.Count(ctx.Profile, ItemID.IronBar) + AutoInventory.Count(ctx.Profile, ItemID.LeadBar) < 2)
                return AutoResourceController.MineIronTierOre(ctx, 9);
            return Craft(ctx, ItemID.Chest);
        }
        return AutoTaskResult.Succeeded;
    }

    private static AutoTaskResult Craft(AutoContext ctx, int itemType)
    {
        if (ctx.State.WorkBench is Point bench && Vector2.DistanceSquared(ctx.Me.Center,
            Center(bench)) > 5f * 16f * 5f * 16f)
            return AutoWorldTravelController.TravelTo(ctx, Center(bench), 4f);
        bool made = AutoCraftingService.TryCraft(ctx.Instance, ctx.Profile, itemType);
        // Succeeded deliberately falls through to the physical build operation in this same
        // update. Crafting itself consumes no controls, so a separate "thinking" frame is noise.
        if (!made)
            return AutoCraftingService.IsAvailable
                ? AutoTaskResult.RetryLater : AutoTaskResult.Faulted;
        return AutoInventory.FindAndBindInventoryIndex(ctx.Me, ctx.Profile,
            item => item.type == itemType) >= 0
            ? AutoTaskResult.Succeeded : AutoTaskResult.Faulted;
    }

    private static bool TryClearTemplate(AutoContext ctx, Point a, ShelterLayout layout)
    {
        int floor = a.Y + RoomHeight - 1;
        for (int x = a.X; x < a.X + layout.TotalWidth; x++)
        for (int y = a.Y; y <= floor; y++)
        {
            bool shell = y == a.Y || y == floor || (x - a.X) % (RoomWidth - 1) == 0;
            Tile tile = Main.tile[x, y];
            if (!tile.HasTile || AllowedTemplateTile(a, x, y, floor, shell, tile))
                continue;
            if (!WorldGen.CanKillTile(x, y))
                continue;
            int tool = AutoWorldKnowledge.IsChoppableTreeTile(new Point(x, y))
                ? AutoInventory.BestToolIndex(ctx.Me, item => item.axe)
                : AutoInventory.BestToolIndex(ctx.Me, item => item.pick);
            if (tool < 0)
                return false;
            ctx.Actuator.MoveAndWorkTile(ctx.Instance, ctx.Me, ctx.Navigator, tool, new Point(x, y));
            return true;
        }
        return false;
    }

    private static bool AllowedTemplateTile(Point a, int x, int y, int floor, bool shell, Tile tile)
    {
        int type = tile.TileType;
        if (shell)
        {
            bool doorOpening = x > a.X && (x - a.X) % (RoomWidth - 1) == 0 &&
                y >= floor - 3 && y < floor;
            return doorOpening
                ? type == TileID.ClosedDoor || TileLoader.CloseDoorID(tile) >= 0
                : type == TileID.WoodBlock;
        }
        return type is TileID.WorkBenches or TileID.Chairs or TileID.Torches or
            TileID.Containers or TileID.Containers2;
    }

    private static bool TryPlaceSupportColumns(AutoContext ctx, Point a, ShelterLayout layout)
    {
        int floor = a.Y + RoomHeight - 1;
        for (int room = 0; room <= layout.RoomCount; room++)
        {
            int x = a.X + room * (RoomWidth - 1);
            int ground = FindGround(x);
            for (int y = ground - 1; y > floor; y--)
            {
                if (IsTile(x, y, TileID.Platforms))
                    continue;
                if (Main.tile[x, y].HasTile)
                    continue;
                return Place(ctx, ItemID.WoodPlatform, new Point(x, y));
            }
        }
        return false;
    }

    private static bool TryPlaceShell(AutoContext ctx, Point a, ShelterLayout layout)
    {
        int floor = a.Y + RoomHeight - 1;
        for (int x = a.X; x < a.X + layout.TotalWidth; x++)
        {
            if (!IsTile(x, floor, TileID.WoodBlock) && !Main.tile[x, floor].HasTile)
                return Place(ctx, ItemID.Wood, new Point(x, floor));
            if (!IsTile(x, a.Y, TileID.WoodBlock) && !Main.tile[x, a.Y].HasTile)
                return Place(ctx, ItemID.Wood, new Point(x, a.Y));
        }
        for (int room = 0; room <= layout.RoomCount; room++)
        {
            int x = a.X + room * (RoomWidth - 1);
            int startY = room == 0 ? floor - 1 : floor - 4;
            for (int y = startY; y > a.Y; y--)
                if (!IsTile(x, y, TileID.WoodBlock) && !Main.tile[x, y].HasTile)
                    return Place(ctx, ItemID.Wood, new Point(x, y));
        }
        return false;
    }

    private static bool TryPlaceWalls(AutoContext ctx, Point a, ShelterLayout layout)
    {
        int slot = AutoInventory.FindInventoryIndex(ctx.Me, item => item.type == ItemID.WoodWall);
        if (slot < 0)
            return false;
        int floor = a.Y + RoomHeight - 1;
        for (int room = 0; room < layout.RoomCount; room++)
        {
            int left = a.X + room * (RoomWidth - 1);
            for (int x = left + 1; x < left + RoomWidth - 1; x++)
            for (int y = a.Y + 1; y < floor; y++)
            {
                if (Main.tile[x, y].WallType == WallID.Wood)
                    continue;
                if (Main.tile[x, y].WallType != WallID.None)
                {
                    int hammer = AutoInventory.BestToolIndex(ctx.Me, item => item.hammer);
                    if (hammer < 0)
                        return false;
                    ctx.Actuator.MoveAndWorkWall(ctx.Instance, ctx.Me, ctx.Navigator, hammer,
                        new Point(x, y));
                    return true;
                }
                ctx.Actuator.MoveAndPlace(ctx.Instance, ctx.Me, ctx.Navigator, slot, new Point(x, y));
                return true;
            }
        }
        return false;
    }

    private static bool TryPlaceFurniture(AutoContext ctx, Point a, ShelterLayout layout)
    {
        int floor = a.Y + RoomHeight - 1;
        for (int room = 0; room < layout.RoomCount; room++)
        {
            int left = a.X + room * (RoomWidth - 1);
            int right = left + RoomWidth - 1;
            if (!RoomHas(a, room, TileID.WorkBenches) &&
                Place(ctx, ItemID.WorkBench, new Point(left + 2, floor - 1)))
                return true;
            if (!RoomHas(a, room, TileID.Chairs) &&
                Place(ctx, ItemID.WoodenChair, new Point(left + 7, floor - 1)))
                return true;
            if (!RoomHas(a, room, TileID.Torches) &&
                Place(ctx, ItemID.Torch, new Point(left + 1, a.Y + 2)))
                return true;
            if (!HasDoorAt(right, floor) &&
                Place(ctx, ItemID.WoodenDoor, new Point(right, floor - 1)))
                return true;
        }
        if (!RoomHas(a, 0, TileID.Containers) &&
            Place(ctx, ItemID.Chest, new Point(a.X + 5, floor - 1)))
            return true;
        return false;
    }

    private static bool Place(AutoContext ctx, int itemType, Point tile)
    {
        int slot = AutoInventory.FindInventoryIndex(ctx.Me, item => item.type == itemType);
        if (slot < 0)
            return false;
        ctx.Actuator.MoveAndPlace(ctx.Instance, ctx.Me, ctx.Navigator, slot, tile);
        return true;
    }

    private static Point? FindSite(int variant, ShelterLayout layout)
    {
        int preferredSign = variant % 2 == 0 ? 1 : -1;
        int initialDistance = System.Math.Min(SiteRadius, (variant / 2 % 8) * 16);
        for (int pass = 0; pass < 2; pass++)
        for (int distance = pass == 0 ? initialDistance : 0; distance <= SiteRadius; distance += 8)
        foreach (int sign in distance == 0 ? new[] { preferredSign } :
            new[] { preferredSign, -preferredSign })
        {
            if (pass == 1 && distance >= initialDistance)
                continue;
            int center = System.Math.Clamp(Main.spawnTileX + distance * sign, 80, Main.maxTilesX - 80);
            int ground = FindGround(center);
            Point anchor = new(center - layout.TotalWidth / 2, ground - Elevation - RoomHeight + 1);
            if (AnchorUsable(anchor, layout) && InitialSiteSafe(anchor, layout))
                return anchor;
        }
        return null;
    }

    private static int FindGround(int x)
    {
        int radius = AutoWorldTopology.UsesInvertedProgression ? 360 : 400;
        return AutoWorldTopology.FindStandingFloorRow(x,
            AutoWorldTopology.PreferredHabitableRow, radius) ?? Main.spawnTileY;
    }

    private static bool AnchorUsable(Point a, ShelterLayout layout) =>
        WorldGen.InWorld(a.X, a.Y, 70) &&
        WorldGen.InWorld(a.X + layout.TotalWidth - 1, a.Y + RoomHeight - 1, 70);

    private static bool MissingSupport(Point a, ShelterLayout layout)
    {
        int floor = a.Y + RoomHeight - 1;
        for (int room = 0; room <= layout.RoomCount; room++)
        {
            int x = a.X + room * (RoomWidth - 1);
            int ground = FindGround(x);
            for (int y = ground - 1; y > floor; y--)
                if (!IsTile(x, y, TileID.Platforms))
                    return true;
        }
        return false;
    }

    private static bool HasForeignWall(Point a, ShelterLayout layout)
    {
        int floor = a.Y + RoomHeight - 1;
        for (int x = a.X + 1; x < a.X + layout.TotalWidth - 1; x++)
        for (int y = a.Y + 1; y < floor; y++)
            if (Main.tile[x, y].WallType is not WallID.None and not WallID.Wood)
                return true;
        return false;
    }

    /// <summary>A new tower never overwrites an existing player structure. Natural foreground
    /// terrain can be excavated, but placed walls, containers and frame-important objects select
    /// a different site; every support also needs actual space below the raised floor.</summary>
    private static bool InitialSiteSafe(Point a, ShelterLayout layout)
    {
        int floor = a.Y + RoomHeight - 1;
        for (int room = 0; room <= layout.RoomCount; room++)
        {
            int x = a.X + room * (RoomWidth - 1);
            int ground = FindGround(x);
            if (ground <= floor + 2)
                return false;
            for (int y = floor + 1; y < ground; y++)
                if (Main.tile[x, y].WallType != WallID.None ||
                    Main.tile[x, y].HasTile && Main.tileFrameImportant[Main.tile[x, y].TileType])
                    return false;
        }
        for (int x = a.X; x < a.X + layout.TotalWidth; x++)
        for (int y = a.Y; y <= floor; y++)
        {
            Tile tile = Main.tile[x, y];
            if (tile.WallType != WallID.None || tile.HasTile &&
                (Main.tileFrameImportant[tile.TileType] || tile.TileType is TileID.Containers or TileID.Containers2))
                return false;
        }
        return true;
    }

    private static bool MissingWall(Point a, ShelterLayout layout)
    {
        int floor = a.Y + RoomHeight - 1;
        for (int room = 0; room < layout.RoomCount; room++)
        {
            int left = a.X + room * (RoomWidth - 1);
            for (int x = left + 1; x < left + RoomWidth - 1; x++)
            for (int y = a.Y + 1; y < floor; y++)
                if (Main.tile[x, y].WallType != WallID.Wood)
                    return true;
        }
        return false;
    }

    private static bool MissingFurniture(Point a, int tileType, ShelterLayout layout)
    {
        for (int room = 0; room < layout.RoomCount; room++)
            if (!RoomHas(a, room, tileType))
                return true;
        return false;
    }

    private static bool MissingDoor(Point a, ShelterLayout layout)
    {
        int floor = a.Y + RoomHeight - 1;
        for (int room = 0; room < layout.RoomCount; room++)
            if (!HasDoorAt(a.X + (room + 1) * (RoomWidth - 1), floor))
                return true;
        return false;
    }

    private static bool RoomHas(Point a, int room, int tileType)
    {
        int left = a.X + room * (RoomWidth - 1);
        for (int x = left; x <= left + RoomWidth - 1; x++)
        for (int y = a.Y; y <= a.Y + RoomHeight - 1; y++)
            if (IsTile(x, y, tileType) || tileType == TileID.Containers &&
                IsTile(x, y, TileID.Containers2))
                return true;
        return false;
    }

    private static bool HasDoorAt(int x, int floorY)
    {
        for (int y = floorY - 3; y < floorY; y++)
        {
            if (!WorldGen.InWorld(x, y, 5))
                continue;
            Tile tile = Main.tile[x, y];
            if (tile.HasTile && (tile.TileType == TileID.ClosedDoor || TileLoader.CloseDoorID(tile) >= 0))
                return true;
        }
        return false;
    }

    private static void CaptureFurnitureAnchors(AutoContext ctx, Point a, ShelterLayout layout)
    {
        int floor = a.Y + RoomHeight - 1;
        Point? bench = FindTile(a, TileID.WorkBenches, layout);
        if (bench.HasValue)
            ctx.State.WorkBench = bench;
        for (int i = 0; i < Main.maxChests; i++)
        {
            Chest chest = Main.chest[i];
            if (chest is null || chest.x < a.X || chest.x >= a.X + layout.TotalWidth ||
                chest.y < a.Y || chest.y > floor ||
                !AutoChestProvenanceSystem.IsAutomationOwnedChest(chest))
                continue;
            ctx.State.Chest = new Point(chest.x, chest.y);
            break;
        }
    }

    private static Point? FindTile(Point a, int type, ShelterLayout layout)
    {
        for (int x = a.X; x < a.X + layout.TotalWidth; x++)
        for (int y = a.Y; y < a.Y + RoomHeight; y++)
            if (IsTile(x, y, type))
                return new Point(x, y);
        return null;
    }

    private static bool IsTile(int x, int y, int type) => WorldGen.InWorld(x, y, 4) &&
        Main.tile[x, y].HasTile && Main.tile[x, y].TileType == type;

    private static Vector2 Center(Point tile) => new(tile.X * 16f + 8f, tile.Y * 16f + 8f);
}
