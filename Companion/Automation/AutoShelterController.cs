using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace MoreAIPlayer.Companion.Automation;

/// <summary>
/// Resumable starter-room builder. Every tile and wall is placed with a real inventory item through
/// <see cref="AutoActuator"/>; after reload it reconstructs progress from the live world and only
/// marks completion when the complete template is still present.
/// </summary>
public static class AutoShelterController
{
    public const int WoodReserve = 110;
    private const int Width = 12;
    private const int Height = 7;
    private const int SiteSearchRadius = 36;

    public static AutoTaskResult Build(AutoContext ctx)
    {
        if (ctx.State.HomeAnchor is not Point anchor || !AnchorStillUsable(anchor))
        {
            ctx.State.HomeAnchor = FindSite(ctx.Me);
            ctx.State.ShelterCompleted = false;
            if (ctx.State.HomeAnchor is not Point selected)
                return AutoTaskResult.AlternativeRequired;
            anchor = selected;
        }

        if (IsShelterComplete(anchor))
        {
            ctx.State.ShelterCompleted = true;
            return AutoTaskResult.Succeeded;
        }

        ctx.State.ShelterCompleted = false;

        AutoTaskResult supplies = EnsureSupplies(ctx, anchor);
        if (supplies != AutoTaskResult.Succeeded)
            return supplies;

        if (TryPlaceMissingShell(ctx, anchor) || TryPlaceMissingWalls(ctx, anchor) ||
            TryPlaceFurniture(ctx, anchor))
            return AutoTaskResult.Running;

        return AutoTaskResult.RetryLater;
    }

    private static AutoTaskResult EnsureSupplies(AutoContext ctx, Point anchor)
    {
        if (HasMissingInteriorWall(anchor) && !AutoInventory.Has(ctx.Profile, ItemID.WoodWall))
            return CraftNearBench(ctx, ItemID.WoodWall);
        if (!HasTileInRoom(anchor, TileID.WorkBenches) && !AutoInventory.Has(ctx.Profile, ItemID.WorkBench))
            return CraftNearBench(ctx, ItemID.WorkBench);
        if (!HasDoor(anchor) && !AutoInventory.Has(ctx.Profile, ItemID.WoodenDoor))
            return CraftNearBench(ctx, ItemID.WoodenDoor);
        if (!HasTileInRoom(anchor, TileID.Chairs) && !AutoInventory.Has(ctx.Profile, ItemID.WoodenChair))
            return CraftNearBench(ctx, ItemID.WoodenChair);
        if (!HasTileInRoom(anchor, TileID.Torches) && !AutoInventory.Has(ctx.Profile, ItemID.Torch))
        {
            if (ctx.Capabilities.Gel <= 0)
                return AutoResourceController.LootPots(ctx);
            return AutoCraftingService.TryCraft(ctx.Instance, ctx.Profile, ItemID.Torch)
                ? AutoTaskResult.Running : AutoTaskResult.RetryLater;
        }
        if (!HasTileInRoom(anchor, TileID.Containers) && !AutoInventory.Has(ctx.Profile, ItemID.Chest))
        {
            if (ctx.Capabilities.IronLeadBars < 2)
            {
                if (ctx.Capabilities.IronLeadOre > 0)
                    return AutoStationController.SmeltOre(ctx, ctx.State.Furnace,
                        AutoProgressionPlanner.SmeltableBarItems);
                return AutoResourceController.MineIronTierOre(ctx, 9);
            }
            return CraftNearBench(ctx, ItemID.Chest);
        }
        return AutoTaskResult.Succeeded;
    }

    private static AutoTaskResult CraftNearBench(AutoContext ctx, int itemType)
    {
        if (ctx.State.WorkBench is Point bench && !Near(ctx.Me, bench))
        {
            ctx.Navigator.Navigate(ctx.Me, TileCenter(bench));
            return AutoTaskResult.Running;
        }
        return AutoCraftingService.TryCraft(ctx.Instance, ctx.Profile, itemType)
            ? AutoTaskResult.Running : AutoTaskResult.RetryLater;
    }

    private static bool TryPlaceMissingShell(AutoContext ctx, Point a)
    {
        // Floor first. Start at any naturally supported cell, then extend from the wood already
        // placed; this works on ordinary uneven terrain and never relies on floating placement.
        for (int x = 0; x < Width; x++)
        {
            Point floor = new(a.X + x, a.Y + Height - 1);
            if (!IsTile(floor, TileID.WoodBlock) && HasSolidNeighbor(floor) && PlaceWoodIfMissing(ctx, floor))
                return true;
        }

        // Left post grows upward from the completed floor.
        for (int y = Height - 2; y >= 0; y--)
        {
            Point left = new(a.X, a.Y + y);
            if (!IsTile(left, TileID.WoodBlock) && HasSolidNeighbor(left) && PlaceWoodIfMissing(ctx, left))
                return true;
        }

        // Roof extends horizontally from the left post.
        for (int x = 1; x < Width; x++)
        {
            Point roof = new(a.X + x, a.Y);
            if (!IsTile(roof, TileID.WoodBlock) && HasSolidNeighbor(roof) && PlaceWoodIfMissing(ctx, roof))
                return true;
        }

        // Only the upper two right-post cells are solid; the lower three remain the doorway.
        for (int y = 1; y < Height - 4; y++)
        {
            Point right = new(a.X + Width - 1, a.Y + y);
            if (!IsTile(right, TileID.WoodBlock) && HasSolidNeighbor(right) && PlaceWoodIfMissing(ctx, right))
                return true;
        }
        return false;
    }

    private static bool TryPlaceMissingWalls(AutoContext ctx, Point a)
    {
        int wallSlot = AutoInventory.FindInventoryIndex(ctx.Me, item => item.type == ItemID.WoodWall);
        if (wallSlot < 0)
            return false;
        for (int x = 1; x < Width - 1; x++)
        {
            for (int y = 1; y < Height - 1; y++)
            {
                Point tile = new(a.X + x, a.Y + y);
                if (Main.tile[tile.X, tile.Y].WallType == WallID.Wood)
                    continue;
                ctx.Actuator.MoveAndPlace(ctx.Instance, ctx.Me, ctx.Navigator, wallSlot, tile);
                return true;
            }
        }
        return false;
    }

    private static bool TryPlaceFurniture(AutoContext ctx, Point a)
    {
        if (!HasTileInRoom(a, TileID.WorkBenches) && PlaceItem(ctx, ItemID.WorkBench, new(a.X + 2, a.Y + Height - 2)))
            return true;
        if (!HasTileInRoom(a, TileID.Containers) && PlaceItem(ctx, ItemID.Chest, new(a.X + 5, a.Y + Height - 2)))
            return true;
        if (!HasTileInRoom(a, TileID.Chairs) && PlaceItem(ctx, ItemID.WoodenChair, new(a.X + 8, a.Y + Height - 2)))
            return true;
        if (!HasTileInRoom(a, TileID.Torches) && PlaceItem(ctx, ItemID.Torch, new(a.X + 1, a.Y + 3)))
            return true;
        if (!HasDoor(a) && PlaceItem(ctx, ItemID.WoodenDoor, new(a.X + Width - 1, a.Y + Height - 2)))
            return true;
        return false;
    }

    private static bool PlaceWoodIfMissing(AutoContext ctx, Point tile)
    {
        if (IsTile(tile, TileID.WoodBlock))
            return false;
        if (Main.tile[tile.X, tile.Y].HasTile)
            return false;
        return PlaceItem(ctx, ItemID.Wood, tile);
    }

    private static bool PlaceItem(AutoContext ctx, int itemType, Point tile)
    {
        int slot = AutoInventory.FindInventoryIndex(ctx.Me, item => item.type == itemType);
        if (slot < 0)
            return false;
        ctx.Actuator.MoveAndPlace(ctx.Instance, ctx.Me, ctx.Navigator, slot, tile);
        return true;
    }

    private static Point? FindSite(Player me)
    {
        Point feet = me.Bottom.ToTileCoordinates();
        for (int distance = 5; distance <= SiteSearchRadius; distance++)
        {
            foreach (int sign in new[] { 1, -1 })
            {
                int startX = feet.X + distance * sign;
                for (int floorY = feet.Y - 4; floorY <= feet.Y + 5; floorY++)
                {
                    Point anchor = new(startX, floorY - Height + 1);
                    if (EmptyRoomWithGround(anchor))
                        return anchor;
                }
            }
        }
        return null;
    }

    private static bool EmptyRoomWithGround(Point a)
    {
        for (int x = 0; x < Width; x++)
        {
            int worldX = a.X + x;
            int floorY = a.Y + Height - 1;
            if (!WorldGen.InWorld(worldX, floorY, 10))
                return false;
            for (int y = 0; y < Height; y++)
            {
                Tile tile = Main.tile[worldX, a.Y + y];
                if (tile.HasTile && Main.tileSolid[tile.TileType])
                    return false;
            }
        }
        // One real ground contact is enough: the floor is extended from it one adjacent block at a
        // time. Requiring twelve perfectly level natural tiles made normal rolling terrain fail.
        for (int x = 0; x < Width; x++)
        {
            Tile support = Main.tile[a.X + x, a.Y + Height];
            if (support.HasTile && Main.tileSolid[support.TileType] && !Main.tileSolidTop[support.TileType])
                return true;
        }
        return false;
    }

    private static bool AnchorStillUsable(Point a) => WorldGen.InWorld(a.X, a.Y, 10) &&
        WorldGen.InWorld(a.X + Width - 1, a.Y + Height - 1, 10);

    public static bool IsShelterComplete(Point a)
    {
        for (int x = 0; x < Width; x++)
        {
            if (!IsTile(new(a.X + x, a.Y), TileID.WoodBlock) ||
                !IsTile(new(a.X + x, a.Y + Height - 1), TileID.WoodBlock))
                return false;
        }
        for (int y = 1; y < Height - 1; y++)
        {
            if (!IsTile(new(a.X, a.Y + y), TileID.WoodBlock))
                return false;
            if (y < Height - 4 && !IsTile(new(a.X + Width - 1, a.Y + y), TileID.WoodBlock))
                return false;
        }
        for (int x = 1; x < Width - 1; x++)
        for (int y = 1; y < Height - 1; y++)
        {
            if (Main.tile[a.X + x, a.Y + y].WallType != WallID.Wood)
                return false;
        }
        return HasTileInRoom(a, TileID.WorkBenches) && HasTileInRoom(a, TileID.Containers) &&
            HasTileInRoom(a, TileID.Chairs) && HasTileInRoom(a, TileID.Torches) && HasDoor(a);
    }

    private static bool HasTileInRoom(Point a, int tileType)
    {
        for (int x = a.X; x < a.X + Width; x++)
        for (int y = a.Y; y < a.Y + Height; y++)
        {
            if (IsTile(new(x, y), tileType))
                return true;
        }
        return false;
    }

    private static bool HasMissingInteriorWall(Point a)
    {
        for (int x = 1; x < Width - 1; x++)
        for (int y = 1; y < Height - 1; y++)
        {
            if (Main.tile[a.X + x, a.Y + y].WallType != WallID.Wood)
                return true;
        }
        return false;
    }

    private static bool HasDoor(Point a)
    {
        int x = a.X + Width - 1;
        for (int y = a.Y + 1; y < a.Y + Height - 1; y++)
        {
            Tile tile = Main.tile[x, y];
            if (tile.HasTile && (tile.TileType == TileID.ClosedDoor || TileLoader.CloseDoorID(tile) >= 0))
                return true;
        }
        return false;
    }

    private static bool IsTile(Point p, int type) => WorldGen.InWorld(p.X, p.Y, 2) &&
        Main.tile[p.X, p.Y].HasTile && Main.tile[p.X, p.Y].TileType == type;

    private static bool HasSolidNeighbor(Point p)
    {
        Point[] neighbors = [new(p.X - 1, p.Y), new(p.X + 1, p.Y), new(p.X, p.Y - 1), new(p.X, p.Y + 1)];
        foreach (Point n in neighbors)
        {
            if (!WorldGen.InWorld(n.X, n.Y, 2))
                continue;
            Tile tile = Main.tile[n.X, n.Y];
            if (tile.HasTile && Main.tileSolid[tile.TileType])
                return true;
        }
        return false;
    }

    private static bool Near(Player me, Point tile) =>
        Vector2.DistanceSquared(me.Center, TileCenter(tile)) <= 48f * 48f;

    private static Vector2 TileCenter(Point tile) => new(tile.X * 16f + 8f, tile.Y * 16f + 8f);
}
