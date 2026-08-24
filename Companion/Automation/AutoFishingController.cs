using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;

namespace MoreAIPlayer.Companion.Automation;

/// <summary>Bounded opportunistic fishing. It reuses the real rod/bait/bobber path and caps each
/// scouting segment to a few casts, so a lucky fishing route can pay off without becoming an
/// infinite random-number wait that suppresses deterministic progress.</summary>
public sealed class AutoFishingController
{
    private const int MinimumWaterTiles = 75;
    private const int IdealWaterTiles = 300;
    private const int SearchRadiusTiles = 26;
    private const int VerticalSearchTiles = 18;
    private const int PoolHalfWidth = 36;
    private const int PoolDepthScan = 80;
    private const int ScanCooldownTicks = 180;
    private const int CastsPerExplorationSegment = 3;

    private FishingSpot? cachedSpot;
    private int scanCooldown;

    private readonly record struct FishingSpot(Vector2 AimWorld, int WaterTiles);

    public bool HasOpportunity(AutoContext ctx)
    {
        RefreshSession(ctx.State);
        if (FindOwnedBobber(ctx.Me) is not null)
            return true;
        if (ctx.State.FishingAttemptsThisSegment >= CastsPerExplorationSegment ||
            ctx.Profile.RodTool.IsAir || ctx.Profile.RodTool.fishingPole <= 0 ||
            !CompanionModeRules.HasFishingBait(ctx.Profile))
            return false;

        if (scanCooldown > 0)
            scanCooldown--;
        if (!cachedSpot.HasValue || scanCooldown <= 0 ||
            Vector2.DistanceSquared(ctx.Me.MountedCenter, cachedSpot.Value.AimWorld) >
            SearchRadiusTiles * 16f * (SearchRadiusTiles * 16f))
        {
            cachedSpot = FindBestFishingSpot(ctx.Me);
            scanCooldown = ScanCooldownTicks;
        }
        return cachedSpot.HasValue;
    }

    public AutoTaskResult Fish(AutoContext ctx)
    {
        RefreshSession(ctx.State);
        AutoInventory.TryCollectNearby(ctx.Instance, ctx.Profile, 8f,
            item => AutoLootController.OpportunityValue(ctx.Profile, item) > 0);

        Projectile bobber = FindOwnedBobber(ctx.Me);
        if (bobber is not null)
        {
            ctx.Navigator.Idle(ctx.Me);
            if (bobber.localAI[1] != 0f)
            {
                if (ctx.Actuator.UseItemInSlot(ctx.Instance, ctx.Me,
                    CompanionInstance.RodInventoryIndex, bobber.Center))
                {
                    ctx.TaskRuntime.RecordWorldProgress();
                }
            }
            return AutoTaskResult.Running;
        }

        if (ctx.State.FishingAttemptsThisSegment >= CastsPerExplorationSegment || !HasOpportunity(ctx))
            return AutoTaskResult.Succeeded;
        if (cachedSpot is not FishingSpot spot)
            return AutoTaskResult.AlternativeRequired;

        ctx.Navigator.Idle(ctx.Me);
        if (ctx.Actuator.UseItemInSlot(ctx.Instance, ctx.Me,
            CompanionInstance.RodInventoryIndex, spot.AimWorld))
        {
            ctx.State.FishingAttemptsThisSegment++;
        }
        return AutoTaskResult.Running;
    }

    private static void RefreshSession(AutoCompanionState state)
    {
        if (state.FishingSegmentMarker == state.ExplorationSegments)
            return;
        state.FishingSegmentMarker = state.ExplorationSegments;
        state.FishingAttemptsThisSegment = 0;
    }

    private static Projectile FindOwnedBobber(Player me)
    {
        foreach (Projectile projectile in Main.ActiveProjectiles)
            if (projectile.owner == me.whoAmI && projectile.bobber)
                return projectile;
        return null;
    }

    private static FishingSpot? FindBestFishingSpot(Player me)
    {
        Point origin = me.Center.ToTileCoordinates();
        FishingSpot? best = null;
        float bestScore = float.MinValue;
        float maxDistanceSquared = SearchRadiusTiles * 16f * (SearchRadiusTiles * 16f);
        for (int x = origin.X - SearchRadiusTiles; x <= origin.X + SearchRadiusTiles; x++)
        {
            for (int y = origin.Y - VerticalSearchTiles; y <= origin.Y + VerticalSearchTiles; y++)
            {
                if (!WorldGen.InWorld(x, y, 10) || !IsWaterSurface(x, y))
                    continue;
                Vector2 aim = new(x * 16f + 8f, y * 16f + 8f);
                float distanceSquared = Vector2.DistanceSquared(me.MountedCenter, aim);
                if (distanceSquared > maxDistanceSquared)
                    continue;
                int waterTiles = CountFishingWaterTiles(x, y);
                if (waterTiles < MinimumWaterTiles)
                    continue;
                float score = waterTiles * 10000f - distanceSquared;
                if (score > bestScore)
                {
                    bestScore = score;
                    best = new FishingSpot(aim, waterTiles);
                }
            }
        }
        return best;
    }

    private static bool IsWaterSurface(int x, int y) =>
        IsOpenWaterTile(x, y) && !IsOpenWaterTile(x, y - 1);

    private static bool IsOpenWaterTile(int x, int y)
    {
        if (!WorldGen.InWorld(x, y, 10))
            return false;
        Tile tile = Main.tile[x, y];
        return tile.LiquidAmount > 0 && tile.LiquidType == LiquidID.Water && !WorldGen.SolidTile(x, y);
    }

    private static int CountFishingWaterTiles(int startX, int surfaceY)
    {
        int minX = startX;
        int maxX = startX;
        for (int step = 1; step <= PoolHalfWidth && IsOpenWaterTile(minX - 1, surfaceY); step++)
            minX--;
        for (int step = 1; step <= PoolHalfWidth && IsOpenWaterTile(maxX + 1, surfaceY); step++)
            maxX++;
        int waterTiles = 0;
        for (int x = minX; x <= maxX; x++)
        {
            for (int y = surfaceY; y < surfaceY + PoolDepthScan && IsOpenWaterTile(x, y); y++)
            {
                if (++waterTiles >= IdealWaterTiles)
                    return IdealWaterTiles;
            }
        }
        return waterTiles;
    }
}
