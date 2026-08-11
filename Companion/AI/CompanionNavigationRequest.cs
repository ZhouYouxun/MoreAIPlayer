using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;

namespace MoreAIPlayer.Companion.AI;

/// <summary>
/// Why a companion wants to travel. The intent — not the raw sign of the vertical distance to the
/// goal — decides whether the navigator is even allowed to consider dropping through a platform.
/// See <see cref="NavigationRequest"/> and <see cref="CompanionPlatformDrop"/>.
/// </summary>
public enum NavigationIntent : byte
{
    /// <summary>Following the owner. Only drops when the owner is clearly on a lower layer.</summary>
    Follow,
    /// <summary>Positioning for a fight. Keeps the current combat layer unless melee truly must
    /// close on an enemy below and a verified landing exists.</summary>
    Combat,
    /// <summary>Fleeing danger. May fall fast, but still never into lava/shimmer/lethal drops.</summary>
    Escape,
    /// <summary>Laying platforms / structures.</summary>
    Build,
    /// <summary>Digging toward a specific lower tile.</summary>
    Mine,
    /// <summary>A destination the user explicitly marked. Allowed to be below, still safety-checked.</summary>
    Waypoint,
    /// <summary>Idle wandering. A platform drop is a costed path edge here, never a steering fallback.</summary>
    Explore
}

/// <summary>
/// A travel order for <see cref="CompanionNavigator"/>. Replaces the old
/// <c>Navigate(Player, Vector2)</c>-only contract, where a goal that merely sat lower than the
/// companion was enough to make it hold Down and fall through the platform under its feet. The
/// permission to pass through a platform is now an explicit, per-call decision.
/// </summary>
public readonly struct NavigationRequest
{
    /// <summary>Ordinary movement should never fall more than this far through platforms in one
    /// drop even with fall-damage protection; the approval clamps the requested cap to this.</summary>
    public const int DefaultSafeDropTiles = 14;
    public const int HardMaxSafeDropTiles = 24;

    public readonly Vector2 GoalWorld;
    public readonly NavigationIntent Intent;

    /// <summary>Whether this order is permitted to drop through a platform under the feet at all.
    /// Even when true, every drop still passes <see cref="CompanionPlatformDrop.TryApprove"/>.</summary>
    public readonly bool AllowPlatformDrop;

    /// <summary>Whether walking off the edge of solid ground into a fall toward a lower goal is
    /// allowed. Distinct from a platform drop; disabled for callers that must never leave a ledge.</summary>
    public readonly bool AllowLedgeFall;

    /// <summary>Bias the search toward staying at the current elevation when a same-layer route
    /// exists (combat / hold posts).</summary>
    public readonly bool PreferKeepElevation;

    /// <summary>Requested ceiling on a single platform-drop distance, in tiles.</summary>
    public readonly int MaximumSafeDropTiles;

    public NavigationRequest(
        Vector2 goalWorld,
        NavigationIntent intent,
        bool allowPlatformDrop = false,
        bool allowLedgeFall = true,
        bool preferKeepElevation = false,
        int maximumSafeDropTiles = DefaultSafeDropTiles)
    {
        GoalWorld = goalWorld;
        Intent = intent;
        AllowPlatformDrop = allowPlatformDrop;
        AllowLedgeFall = allowLedgeFall;
        PreferKeepElevation = preferKeepElevation;
        MaximumSafeDropTiles = System.Math.Clamp(maximumSafeDropTiles, 4, HardMaxSafeDropTiles);
    }

    /// <summary>
    /// The safe default the parameterless <c>Navigate(Player, Vector2)</c> overload maps to:
    /// go to the goal, but never actively drop through a platform. Every legacy call site inherits
    /// this, which is what removes the erroneous combat/follow drops without touching each one.
    /// </summary>
    public static NavigationRequest Safe(Vector2 goal, NavigationIntent intent = NavigationIntent.Follow)
        => new(goal, intent, allowPlatformDrop: false);
}

/// <summary>Outcome of a platform-drop request, surfaced for diagnostics.</summary>
public enum DropDenialReason : byte
{
    Approved,
    NotAllowedByIntent,
    NotOnPlatform,
    OnCooldown,
    NoLanding,
    LandingHazard,
    ChannelBlocked
}

/// <summary>
/// The one place that decides whether a companion may fall through the platform under its feet.
/// Every brain / navigator path routes through here instead of re-deriving half a rule from a
/// coordinate difference, which was the root cause of companions dropping off battle platforms.
/// </summary>
public static class CompanionPlatformDrop
{
    /// <summary>
    /// Validates a platform drop from where <paramref name="me"/> currently stands.
    /// </summary>
    /// <param name="me">The companion player.</param>
    /// <param name="request">The active travel order (its intent gates whether a drop is allowed).</param>
    /// <param name="dropCooldownTicksRemaining">Navigator's post-drop cooldown; a fresh drop is
    /// refused while this is positive so the companion cannot chain through several layers at once.</param>
    /// <param name="landingTile">The verified landing tile (feet row) when approved.</param>
    /// <param name="reason">Why the drop was approved or refused.</param>
    public static bool TryApprove(
        Player me,
        in NavigationRequest request,
        int dropCooldownTicksRemaining,
        out Point landingTile,
        out DropDenialReason reason)
    {
        landingTile = new Point(-1, -1);

        if (!request.AllowPlatformDrop)
        {
            reason = DropDenialReason.NotAllowedByIntent;
            return false;
        }

        if (dropCooldownTicksRemaining > 0)
        {
            reason = DropDenialReason.OnCooldown;
            return false;
        }

        // 1. Standing on a genuine, droppable platform (not a slope/door/half-brick misread).
        int footColumn = (int)(me.Bottom.X / 16f);
        int footRow = (int)((me.Bottom.Y + 1f) / 16f); // the row the feet rest on
        if (!IsOnDroppablePlatform(me, footColumn, out int platformRow))
        {
            reason = DropDenialReason.NotOnPlatform;
            return false;
        }

        // 2. A body-sized landing exists within a bounded distance below the source platform.
        int maxDrop = request.MaximumSafeDropTiles;
        int startRow = platformRow + 1;
        for (int landRow = startRow + 1; landRow <= startRow + maxDrop; landRow++)
        {
            // 4. The vertical channel the body falls through must stay clear of solids.
            if (!CompanionPathfinder.BodyFits(footColumn, landRow))
            {
                // A solid ceiling above the candidate ends the reachable column; stop searching.
                if (CompanionPathfinder.IsSolid(footColumn, landRow) ||
                    (CompanionPathfinder.IsSolid(footColumn - 1, landRow) && CompanionPathfinder.IsSolid(footColumn + 1, landRow)))
                    break;
                continue;
            }

            if (!CompanionPathfinder.HasSupport(footColumn, landRow))
                continue;

            // Skip the source platform itself and anything flush against it — a real drop lands
            // at least two tiles below the platform we are leaving.
            if (landRow <= startRow)
                continue;

            // 3. The landing spot must not be lethal or under imminent fire.
            if (!IsLandingSafe(me, footColumn, landRow))
            {
                reason = DropDenialReason.LandingHazard;
                return false; // a hazardous nearest landing is a refusal, not "keep looking lower"
            }

            landingTile = new Point(footColumn, landRow);
            reason = DropDenialReason.Approved;
            return true;
        }

        reason = DropDenialReason.NoLanding;
        return false;
    }

    /// <summary>True when the companion is standing on a platform it can legitimately fall through.
    /// Scans the short contact band under the feet — a hammered/sloped platform shifts the contact
    /// height inside its tile — and requires the platform not be pinned by solids beside it.</summary>
    private static bool IsOnDroppablePlatform(Player me, int footColumn, out int platformRow)
    {
        platformRow = -1;
        if (me.velocity.Y != 0f)
            return false; // must actually be resting on it

        int firstRow = (int)((me.Bottom.Y - 2f) / 16f);
        int lastRow = (int)((me.Bottom.Y + 4f) / 16f);
        for (int y = firstRow; y <= lastRow; y++)
        {
            if (CompanionPathfinder.SupportIsDroppablePlatform(footColumn, y - 1))
            {
                platformRow = y;
                return true;
            }
            // SupportIsDroppablePlatform checks the platform under a feet row; also accept the
            // direct case where this row itself is the platform the feet rest on.
            if (CompanionPathfinder.IsPlatform(footColumn, y) &&
                !CompanionPathfinder.IsSolid(footColumn - 1, y) &&
                !CompanionPathfinder.IsSolid(footColumn + 1, y))
            {
                platformRow = y;
                return true;
            }
        }
        return false;
    }

    /// <summary>Landing hitbox is clear of lava, shimmer, hurt tiles and any immediate hostile
    /// projectile. Deliberately strict — a bad landing is worse than staying up on the platform.</summary>
    private static bool IsLandingSafe(Player me, int column, int feetRow)
    {
        int width = System.Math.Max(20, me.width);
        int height = System.Math.Max(42, me.height);
        Vector2 feet = new(column * 16f + 8f, (feetRow + 1) * 16f);
        Vector2 topLeft = new(feet.X - width * 0.5f, feet.Y - height);

        if (!me.lavaImmune && Collision.LavaCollision(topLeft, width, height))
            return false;
        if (Collision.AnyHurtingTiles(topLeft, width, height))
            return false;
        if (HasShimmer(column, feetRow))
            return false;

        // Any hostile projectile already sitting on the landing means dropping onto it. Kept to a
        // present-position test rather than a full prediction: the drop itself is fast, and threat
        // avoidance owns the longer-horizon dodging once the companion is airborne.
        Vector2 center = new(feet.X, feet.Y - height * 0.5f);
        foreach (Projectile projectile in Main.ActiveProjectiles)
        {
            if (projectile.hostile && projectile.damage > 0 &&
                Vector2.DistanceSquared(projectile.Center, center) < 112f * 112f)
                return false;
        }
        return true;
    }

    private static bool HasShimmer(int column, int feetRow)
    {
        for (int y = feetRow - 2; y <= feetRow; y++)
        {
            if (!WorldGen.InWorld(column, y, 2))
                continue;
            Tile tile = Main.tile[column, y];
            if (tile.LiquidAmount > 0 && tile.LiquidType == LiquidID.Shimmer)
                return true;
        }
        return false;
    }
}
