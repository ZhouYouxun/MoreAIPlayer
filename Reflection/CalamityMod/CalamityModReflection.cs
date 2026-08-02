using System;
using System.Reflection;
using Microsoft.Xna.Framework;
using MoreAIPlayer.Companion;
using Terraria;
using Terraria.ModLoader;

namespace MoreAIPlayer.Reflection.CalamityMod;

/// <summary>
/// Optional Calamity lookup surface. No Calamity type is referenced at compile time: when the
/// player has not enabled Calamity, every caller simply receives false and uses normal behavior.
/// </summary>
internal static class CalamityModReflection
{
    internal const string ModName = "CalamityMod";

    internal static bool TryGetItemType(string internalName, out int type)
    {
        type = 0;
        return ModLoader.TryGetMod(ModName, out Mod calamity) &&
            calamity.TryFind<ModItem>(internalName, out ModItem item) &&
            (type = item.Type) > 0;
    }

    internal static bool TryGetProjectileType(string internalName, out int type)
    {
        type = 0;
        return ModLoader.TryGetMod(ModName, out Mod calamity) &&
            calamity.TryFind<ModProjectile>(internalName, out ModProjectile projectile) &&
            (type = projectile.Type) > 0;
    }

    internal static bool TryGetEquippedDashInfo(Player player, out bool omnidirectional)
    {
        omnidirectional = false;
        if (player is null ||
            !ModLoader.TryGetMod(ModName, out Mod calamity) ||
            !calamity.TryFind<ModPlayer>("CalamityPlayer", out ModPlayer prototype))
        {
            return false;
        }

        ModPlayer calamityPlayer = player.GetModPlayer(prototype);
        Type playerType = calamityPlayer.GetType();
        PropertyInfo dashIdProperty = playerType.GetProperty("DashID", BindingFlags.Instance | BindingFlags.Public);
        PropertyInfo usedDashProperty = playerType.GetProperty("UsedDash", BindingFlags.Instance | BindingFlags.Public);
        if (dashIdProperty?.GetValue(calamityPlayer) is not string dashId ||
            string.IsNullOrEmpty(dashId) ||
            usedDashProperty?.GetValue(calamityPlayer) is not object usedDash)
        {
            return false;
        }

        PropertyInfo omnidirectionalProperty = usedDash.GetType().GetProperty(
            "IsOmnidirectional", BindingFlags.Instance | BindingFlags.Public);
        omnidirectional = omnidirectionalProperty?.GetValue(usedDash) is true ||
            dashId.Contains("Godslayer", StringComparison.OrdinalIgnoreCase);
        return true;
    }

    /// <summary>
    /// Starts whichever Calamity dash the synthetic player currently has equipped without a
    /// compile-time Calamity reference. Calamity normally refuses to update a non-local
    /// Player's dash, so merely pressing left/right on a companion is insufficient. This
    /// narrow adapter enters the companion's local-player context, asks Calamity's own dash
    /// object for its speed, and invokes the same DoADash/OnDashEffects pair Calamity uses.
    /// </summary>
    internal static bool TryStartEquippedDash(Player player, Vector2 direction, Vector2 aimWorld)
    {
        if (player is null || player.dashDelay != 0 ||
            !ModLoader.TryGetMod(ModName, out Mod calamity) ||
            !calamity.TryFind<ModPlayer>("CalamityPlayer", out ModPlayer prototype))
        {
            return false;
        }

        ModPlayer calamityPlayer = player.GetModPlayer(prototype);
        Type playerType = calamityPlayer.GetType();
        PropertyInfo dashIdProperty = playerType.GetProperty("DashID", BindingFlags.Instance | BindingFlags.Public);
        PropertyInfo usedDashProperty = playerType.GetProperty("UsedDash", BindingFlags.Instance | BindingFlags.Public);
        MethodInfo doDashMethod = playerType.GetMethod("DoADash", BindingFlags.Instance | BindingFlags.Public,
            binder: null, types: [typeof(float), typeof(bool)], modifiers: null);

        if (dashIdProperty?.GetValue(calamityPlayer) is not string dashId ||
            string.IsNullOrEmpty(dashId) ||
            usedDashProperty?.GetValue(calamityPlayer) is not object usedDash ||
            doDashMethod is null)
        {
            return false;
        }

        Type dashType = usedDash.GetType();
        MethodInfo calculateSpeed = dashType.GetMethod("CalculateDashSpeed",
            BindingFlags.Instance | BindingFlags.Public, binder: null, types: [typeof(Player)], modifiers: null);
        MethodInfo onDashEffects = dashType.GetMethod("OnDashEffects",
            BindingFlags.Instance | BindingFlags.Public, binder: null, types: [typeof(Player)], modifiers: null);
        if (calculateSpeed is null || onDashEffects is null)
            return false;

        float speed;
        try
        {
            speed = Convert.ToSingle(calculateSpeed.Invoke(usedDash, [player]));
        }
        catch
        {
            return false;
        }

        direction = direction.SafeNormalize(new Vector2(player.direction == 0 ? 1 : player.direction, 0f));
        player.controlLeft = direction.X < -0.25f;
        player.controlRight = direction.X > 0.25f;
        player.controlUp = direction.Y < -0.25f;
        player.controlDown = direction.Y > 0.25f;

        try
        {
            using VirtualPlayerExecutionContext.Scope scope =
                VirtualPlayerExecutionContext.Enter(player, aimWorld, wantsUseItem: false, wantsAltUse: false);

            // Horizontal, omnidirectional and God Slayer dashes keep their own double-tap
            // timer. Two calls prime and consume that timer while forceDash also covers users
            // who bound Calamity's manual dash hotkey.
            bool dashed = Convert.ToBoolean(doDashMethod.Invoke(calamityPlayer, [speed, true]));
            if (!dashed)
                dashed = Convert.ToBoolean(doDashMethod.Invoke(calamityPlayer, [speed, true]));
            if (!dashed)
                return false;

            onDashEffects.Invoke(usedDash, [player]);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Advances an already-started Calamity dash. Calamity's normal implementation returns
    /// immediately for every Player except Main.myPlayer; companions therefore need the same
    /// temporary local-player context on every active dash frame, not only on the start frame.
    /// </summary>
    internal static void UpdateActiveDash(Player player, Vector2 aimWorld)
    {
        if (player is null || player.dashDelay == 0 ||
            !ModLoader.TryGetMod(ModName, out Mod calamity) ||
            !calamity.TryFind<ModPlayer>("CalamityPlayer", out ModPlayer prototype))
        {
            return;
        }

        ModPlayer calamityPlayer = player.GetModPlayer(prototype);
        Type playerType = calamityPlayer.GetType();
        PropertyInfo dashIdProperty = playerType.GetProperty("DashID", BindingFlags.Instance | BindingFlags.Public);
        if (dashIdProperty?.GetValue(calamityPlayer) is not string dashId || string.IsNullOrEmpty(dashId))
            return;

        MethodInfo updateDash = playerType.GetMethod("ModDashMovement",
            BindingFlags.Instance | BindingFlags.Public, binder: null, types: Type.EmptyTypes, modifiers: null);
        if (updateDash is null)
            return;

        try
        {
            using VirtualPlayerExecutionContext.Scope scope =
                VirtualPlayerExecutionContext.Enter(player, aimWorld, wantsUseItem: false, wantsAltUse: false);
            updateDash.Invoke(calamityPlayer, null);
        }
        catch
        {
            // Optional compatibility must fail closed; vanilla movement remains available.
        }
    }
}
