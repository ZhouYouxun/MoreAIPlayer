using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace MoreAIPlayer.Companion.Automation;

/// <summary>
/// Local view-only follow for the Automation Tome. It never changes <see cref="Main.myPlayer"/>,
/// the controlled Player, or the synthetic-player execution context; it only supplies a late camera
/// position while the requested automation profile is still live.
/// </summary>
public sealed class CompanionAutomationCameraSystem : ModSystem
{
    private static int followedOwner = -1;
    private static int followedProfile = -1;
    private static Vector2 cameraPosition;
    private static bool cameraPositionInitialized;

    public static bool IsFollowing(int ownerWhoAmI, int profileId) =>
        followedOwner == ownerWhoAmI && followedProfile == profileId;

    public static void Follow(int ownerWhoAmI, int profileId, Vector2 targetCenter)
    {
        followedOwner = ownerWhoAmI;
        followedProfile = profileId;
        cameraPosition = CenteredScreenPosition(targetCenter);
        cameraPositionInitialized = true;
    }

    public static void StopFollowing(int profileId = -1)
    {
        if (profileId >= 0 && followedProfile != profileId)
            return;
        followedOwner = -1;
        followedProfile = -1;
        cameraPositionInitialized = false;
    }

    public override void OnWorldLoad() => StopFollowing();

    public override void OnWorldUnload() => StopFollowing();

    public override void Unload() => StopFollowing();

    public override void ModifyScreenPosition()
    {
        if (followedOwner < 0 || followedProfile < 0)
            return;
        if (Main.gameMenu || Main.netMode != NetmodeID.SinglePlayer || followedOwner != Main.myPlayer)
        {
            StopFollowing();
            return;
        }

        CompanionInstance active = CompanionEntitySystem.FindByProfile(followedOwner, followedProfile);
        if (active is null || !active.Entity.active)
        {
            StopFollowing();
            return;
        }

        Vector2 target = CenteredScreenPosition(active.Entity.Center);
        cameraPosition = cameraPositionInitialized
            ? Vector2.Lerp(cameraPosition, target, 0.24f)
            : target;
        cameraPositionInitialized = true;
        Main.screenPosition = cameraPosition;
    }

    private static Vector2 CenteredScreenPosition(Vector2 targetCenter)
    {
        float maxX = System.Math.Max(0f, Main.maxTilesX * 16f - Main.screenWidth);
        float maxY = System.Math.Max(0f, Main.maxTilesY * 16f - Main.screenHeight);
        return new Vector2(
            MathHelper.Clamp(targetCenter.X - Main.screenWidth * 0.5f, 0f, maxX),
            MathHelper.Clamp(targetCenter.Y - Main.screenHeight * 0.5f, 0f, maxY));
    }
}
