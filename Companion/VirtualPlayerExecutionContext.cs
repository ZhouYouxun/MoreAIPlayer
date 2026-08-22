using Microsoft.Xna.Framework;
using Terraria;

namespace MoreAIPlayer.Companion;

/// <summary>
/// Owns every temporary global-state change needed to run item/projectile code as a synthetic
/// player. Scopes are deliberately nestable: another detour or the ItemCheck recovery path may
/// re-enter the same virtual player, and each layer restores exactly the state it observed.
/// </summary>
internal static class VirtualPlayerExecutionContext
{
    internal readonly struct Scope : System.IDisposable
    {
        private readonly SavedState saved;
        private readonly bool active;

        internal Scope(Player player, Vector2 aimWorld, bool wantsUseItem, bool wantsAltUse, Point? workTile)
        {
            // The Main.myPlayer swap below is only ever legitimate for a synthetic companion/hunter,
            // which exist in singleplayer alone. Gating it on the runtime policy means that even if a
            // future caller forgets to check, a multiplayer client can never have its local-player
            // identity or virtual mouse hijacked.
            active = player is not null && player.whoAmI >= 0 && player.whoAmI < Main.maxPlayers &&
                CompanionRuntimePolicy.CanRunSyntheticPlayers;
            saved = active ? SavedState.Capture() : default;
            if (!active)
                return;

            if (depth == 0)
            {
                rootState = saved;
                rootStateValid = true;
            }
            depth++;

            Vector2 safeAim = SanitizeAim(player, aimWorld, out _);
            Main.myPlayer = player.whoAmI;
            Main.mouseLeft = wantsUseItem && !wantsAltUse;
            Main.mouseRight = wantsUseItem && wantsAltUse;

            SetMouseForWorldAim(player, safeAim);

            Point tile = workTile ?? new Point((int)(safeAim.X / 16f), (int)(safeAim.Y / 16f));
            Player.tileTargetX = (int)MathHelper.Clamp(tile.X, 5, Main.maxTilesX - 5);
            Player.tileTargetY = (int)MathHelper.Clamp(tile.Y, 5, Main.maxTilesY - 5);

            // Refresh other mods' per-player mouse mirrors at the exact execution boundary.
            // PreUpdatePlayers also feeds them, but a later ModPlayer reset must not be able to
            // leave ItemCheck or a holdout projectile reading stale data.
            try
            {
                FeedModMouseStateOncePerFrame(player, safeAim, wantsAltUse);
            }
            catch
            {
                // Compatibility reflection is supplemental. A foreign ModPlayer with a broken
                // ModPlayers enumerator must not prevent the guaranteed global state restore.
            }
        }

        public void Dispose()
        {
            if (!active)
                return;

            saved.Restore();
            if (depth > 0)
                depth--;
            if (depth == 0)
                rootStateValid = false;
        }
    }

    private readonly struct SavedState
    {
        private readonly int myPlayer;
        private readonly int mouseX;
        private readonly int mouseY;
        private readonly bool mouseLeft;
        private readonly bool mouseRight;
        private readonly int tileTargetX;
        private readonly int tileTargetY;

        private SavedState(int myPlayer, int mouseX, int mouseY, bool mouseLeft, bool mouseRight,
            int tileTargetX, int tileTargetY)
        {
            this.myPlayer = myPlayer;
            this.mouseX = mouseX;
            this.mouseY = mouseY;
            this.mouseLeft = mouseLeft;
            this.mouseRight = mouseRight;
            this.tileTargetX = tileTargetX;
            this.tileTargetY = tileTargetY;
        }

        internal static SavedState Capture() => new(
            Main.myPlayer,
            Main.mouseX,
            Main.mouseY,
            Main.mouseLeft,
            Main.mouseRight,
            Player.tileTargetX,
            Player.tileTargetY);

        internal void Restore()
        {
            Main.myPlayer = myPlayer;
            Main.mouseX = mouseX;
            Main.mouseY = mouseY;
            Main.mouseLeft = mouseLeft;
            Main.mouseRight = mouseRight;
            Player.tileTargetX = tileTargetX;
            Player.tileTargetY = tileTargetY;
        }
    }

    private static int depth;
    private static SavedState rootState;
    private static bool rootStateValid;
    private static SavedState lastRealState;
    private static bool lastRealStateValid;
    private static int fallbackLocalPlayerSlot = -1;
    private static MouseCacheEntry[] mouseCache;
    private static ModMouseFeedEntry[] modMouseFeedCache;

    private struct MouseCacheEntry
    {
        internal bool Valid;
        internal ulong Frame;
        internal Vector2 AimWorld;
        internal Vector2 ScreenPosition;
        internal float GravityDirection;
        internal int MouseX;
        internal int MouseY;
    }

    private struct ModMouseFeedEntry
    {
        internal bool Valid;
        internal ulong Frame;
        internal Vector2 AimWorld;
        internal bool RightHeld;
    }

    internal static int Depth => depth;

    internal static Scope Enter(Player player, Vector2 aimWorld, bool wantsUseItem, bool wantsAltUse,
        Point? workTile = null) =>
        new(player, aimWorld, wantsUseItem, wantsAltUse, workTile);

    /// <summary>
    /// Records the real player's state before synthetic players begin their frame. This is a
    /// second recovery layer beyond normal Dispose/finally restoration.
    /// </summary>
    internal static void CaptureRealState()
    {
        if (depth == 0 && !IsVirtualSlot(Main.myPlayer))
        {
            lastRealState = SavedState.Capture();
            lastRealStateValid = true;
            if (IsUsableRealSlot(Main.myPlayer))
                fallbackLocalPlayerSlot = Main.myPlayer;
        }
    }

    /// <summary>Seeds recovery from the verified owner before a synthetic slot is created.</summary>
    internal static void RegisterRealPlayer(Player player)
    {
        if (player is null || depth != 0 || player.whoAmI != Main.myPlayer || !IsUsableRealSlot(player.whoAmI))
            return;

        fallbackLocalPlayerSlot = player.whoAmI;
        lastRealState = SavedState.Capture();
        lastRealStateValid = true;
    }

    /// <summary>
    /// Repairs a leaked scope or a leaked Main.myPlayer before camera/UI code can observe it.
    /// A correctly disposed scope never reaches either branch.
    /// </summary>
    internal static bool ForceRestoreIfLeaked()
    {
        bool leakedScope = depth != 0;
        bool leakedIdentity = IsVirtualSlot(Main.myPlayer);
        if (!leakedScope && !leakedIdentity)
            return false;

        if (leakedScope && rootStateValid)
            rootState.Restore();

        // A root scope can itself have been entered after foreign code already leaked a virtual
        // identity. Never accept that virtual snapshot as the final restoration target.
        if (IsVirtualSlot(Main.myPlayer) && lastRealStateValid)
            lastRealState.Restore();
        if (IsVirtualSlot(Main.myPlayer) && IsUsableRealSlot(fallbackLocalPlayerSlot))
            Main.myPlayer = fallbackLocalPlayerSlot;

        depth = 0;
        rootStateValid = false;
        return true;
    }

    internal static Vector2 SanitizeAim(Player player, Vector2 aimWorld, out bool corrected)
    {
        corrected = false;
        Vector2 fallback = player.MountedCenter + new Vector2((player.direction == 0 ? 1 : player.direction) * 160f, 0f);
        if (!float.IsFinite(aimWorld.X) || !float.IsFinite(aimWorld.Y))
        {
            corrected = true;
            return fallback;
        }

        // Vector2.Zero is the CLR default and points most worlds toward the upper-left corner.
        // It is never a useful combat cursor once the player has left the spawn corner.
        if (aimWorld == Vector2.Zero && player.MountedCenter.LengthSquared() > 512f * 512f)
        {
            corrected = true;
            return fallback;
        }

        return aimWorld;
    }

    /// <summary>
    /// Converts a world aim to mouse coordinates, then verifies the result through Main.MouseWorld.
    /// The common camera path succeeds immediately. If another mod changed zoom/gravity/camera
    /// transforms, a tiny sampled affine solve corrects the cursor instead of assuming 1:1 pixels.
    /// </summary>
    private static void SetMouseForWorldAim(Player player, Vector2 aimWorld)
    {
        if (!CompanionRuntimePolicy.CanRunSyntheticPlayers)
            return;

        EnsureCaches();
        ref MouseCacheEntry cache = ref mouseCache[player.whoAmI];
        if (cache.Valid &&
            cache.Frame == Main.GameUpdateCount &&
            cache.AimWorld == aimWorld &&
            cache.ScreenPosition == Main.screenPosition &&
            cache.GravityDirection == player.gravDir)
        {
            Main.mouseX = cache.MouseX;
            Main.mouseY = cache.MouseY;
            return;
        }

        Vector2 approximate = aimWorld - Main.screenPosition;
        Main.mouseX = (int)System.MathF.Round(approximate.X);
        Main.mouseY = (int)System.MathF.Round(approximate.Y);

        Vector2 observed = Main.MouseWorld;
        Vector2 error = aimWorld - observed;
        if (!IsFinite(error) || error.LengthSquared() <= 2.25f)
        {
            StoreMouseCache(ref cache, player, aimWorld);
            return;
        }

        const int samplePixels = 32;
        int baseMouseX = Main.mouseX;
        int baseMouseY = Main.mouseY;
        Vector2 baseWorld = observed;

        Main.mouseX = baseMouseX + samplePixels;
        Vector2 xWorld = Main.MouseWorld;
        Main.mouseX = baseMouseX;
        Main.mouseY = baseMouseY + samplePixels;
        Vector2 yWorld = Main.MouseWorld;
        Main.mouseY = baseMouseY;

        Vector2 xAxis = (xWorld - baseWorld) / samplePixels;
        Vector2 yAxis = (yWorld - baseWorld) / samplePixels;
        float determinant = xAxis.X * yAxis.Y - xAxis.Y * yAxis.X;
        if (!IsFinite(xAxis) || !IsFinite(yAxis) || !float.IsFinite(determinant) ||
            System.MathF.Abs(determinant) < 0.0001f)
        {
            StoreMouseCache(ref cache, player, aimWorld);
            return;
        }

        float deltaX = (error.X * yAxis.Y - error.Y * yAxis.X) / determinant;
        float deltaY = (xAxis.X * error.Y - xAxis.Y * error.X) / determinant;
        if (!float.IsFinite(deltaX) || !float.IsFinite(deltaY))
        {
            StoreMouseCache(ref cache, player, aimWorld);
            return;
        }

        Main.mouseX = baseMouseX + (int)System.MathF.Round(deltaX);
        Main.mouseY = baseMouseY + (int)System.MathF.Round(deltaY);
        StoreMouseCache(ref cache, player, aimWorld);
    }

    private static bool IsFinite(Vector2 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y);

    private static void StoreMouseCache(ref MouseCacheEntry cache, Player player, Vector2 aimWorld)
    {
        cache.Valid = true;
        cache.Frame = Main.GameUpdateCount;
        cache.AimWorld = aimWorld;
        cache.ScreenPosition = Main.screenPosition;
        cache.GravityDirection = player.gravDir;
        cache.MouseX = Main.mouseX;
        cache.MouseY = Main.mouseY;
    }

    private static void FeedModMouseStateOncePerFrame(Player player, Vector2 aimWorld, bool rightHeld)
    {
        EnsureCaches();
        ref ModMouseFeedEntry cache = ref modMouseFeedCache[player.whoAmI];
        if (cache.Valid && cache.Frame == Main.GameUpdateCount &&
            cache.AimWorld == aimWorld && cache.RightHeld == rightHeld)
        {
            return;
        }

        CompanionModBridge.FeedMouseState(player, aimWorld, rightHeld);
        cache.Valid = true;
        cache.Frame = Main.GameUpdateCount;
        cache.AimWorld = aimWorld;
        cache.RightHeld = rightHeld;
    }

    private static void EnsureCaches()
    {
        if (mouseCache is null || mouseCache.Length != Main.maxPlayers)
            mouseCache = new MouseCacheEntry[Main.maxPlayers];
        if (modMouseFeedCache is null || modMouseFeedCache.Length != Main.maxPlayers)
            modMouseFeedCache = new ModMouseFeedEntry[Main.maxPlayers];
    }

    internal static void Reset()
    {
        depth = 0;
        rootState = default;
        rootStateValid = false;
        lastRealState = default;
        lastRealStateValid = false;
        fallbackLocalPlayerSlot = IsUsableRealSlot(Main.myPlayer) ? Main.myPlayer : -1;
        mouseCache = null;
        modMouseFeedCache = null;
    }

    private static bool IsVirtualSlot(int slot) =>
        slot >= 0 &&
        (CompanionEntitySystem.IsCompanionSlot(slot) || Hunter.HunterSystem.IsHunterSlot(slot));

    private static bool IsUsableRealSlot(int slot) =>
        slot >= 0 && slot < Main.maxPlayers && Main.player is not null && slot < Main.player.Length &&
        Main.player[slot] is not null && Main.player[slot].active && !IsVirtualSlot(slot);
}
