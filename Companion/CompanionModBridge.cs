using System.Collections.Generic;
using System.Reflection;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ModLoader;

namespace MoreAIPlayer.Companion;

/// <summary>
/// Reflection bridge to other mods' per-player state. Mods like Calamity only sync their
/// virtual mouse fields for the local player, so companion players must have those fields
/// fed manually every tick. Everything here is cached per ModPlayer type and fails soft.
/// </summary>
public static class CompanionModBridge
{
    private sealed class MouseMembers
    {
        public FieldInfo[] AbsoluteMouseWorld;
        public FieldInfo[] DeltaMouseWorld;
        public FieldInfo[] MouseRotation;
        public FieldInfo[] MouseRightHeld;
    }

    private static readonly Dictionary<System.Type, MouseMembers> MouseMembersByType = [];
    private static readonly Dictionary<System.Type, (FieldInfo Stealth, FieldInfo StealthMax)> StealthFieldsByType = [];

    public static void ClearCaches()
    {
        MouseMembersByType.Clear();
        StealthFieldsByType.Clear();
    }

    /// <summary>
    /// Writes the companion's virtual aim into any mod's per-player mouse fields:
    /// absolute "mouseWorld" vectors, "mouseWorldDelta..." vectors (Calamity convention,
    /// relative to MountedCenter), "mouseRotation..." floats and "mouseRight" booleans.
    /// </summary>
    public static void FeedMouseState(Player player, Vector2 aimWorld, bool rightHeld)
    {
        Vector2 delta = aimWorld - player.MountedCenter;
        float rotation = delta.ToRotation();

        foreach (ModPlayer modPlayer in player.ModPlayers)
        {
            MouseMembers members = GetMouseMembers(modPlayer.GetType());
            foreach (FieldInfo field in members.AbsoluteMouseWorld)
                TrySet(field, modPlayer, aimWorld);
            foreach (FieldInfo field in members.DeltaMouseWorld)
                TrySet(field, modPlayer, delta);
            foreach (FieldInfo field in members.MouseRotation)
                TrySet(field, modPlayer, rotation);
            foreach (FieldInfo field in members.MouseRightHeld)
                TrySet(field, modPlayer, rightHeld);
        }
    }

    private static MouseMembers GetMouseMembers(System.Type type)
    {
        if (MouseMembersByType.TryGetValue(type, out MouseMembers cached))
            return cached;

        List<FieldInfo> absolute = [];
        List<FieldInfo> delta = [];
        List<FieldInfo> rotation = [];
        List<FieldInfo> rightHeld = [];
        foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            string name = field.Name.ToLowerInvariant();
            // Never use broad Contains matching here. Calamity has both
            // mouseWorldDeltaFromPlayer and oldMouseWorldDeltaFromPlayer; writing the latter
            // makes its change detector believe the cursor never moved.
            if (IsHistoricalOrControlField(name))
                continue;

            if (field.FieldType == typeof(Vector2) && IsMouseDeltaField(name))
            {
                delta.Add(field);
            }
            else if (field.FieldType == typeof(Vector2) && IsAbsoluteMouseField(name))
            {
                absolute.Add(field);
            }
            else if (field.FieldType == typeof(float) && IsMouseRotationField(name))
            {
                rotation.Add(field);
            }
            else if (field.FieldType == typeof(bool) && name == "mouseright")
            {
                rightHeld.Add(field);
            }
        }

        MouseMembers members = new()
        {
            AbsoluteMouseWorld = [.. absolute],
            DeltaMouseWorld = [.. delta],
            MouseRotation = [.. rotation],
            MouseRightHeld = [.. rightHeld]
        };
        MouseMembersByType[type] = members;
        return members;
    }

    private static bool IsHistoricalOrControlField(string name) =>
        name.Contains("old") ||
        name.Contains("previous") ||
        name.Contains("last") ||
        name.Contains("cached") ||
        name.Contains("packet") ||
        name.Contains("listener");

    private static bool IsMouseDeltaField(string name) =>
        name is "mouseworlddeltafromplayer" or "mouseworlddelta" or "virtualmouseworlddelta";

    private static bool IsAbsoluteMouseField(string name) =>
        name is "mouseworld" or "mouseworldposition" or "virtualmouseworld";

    private static bool IsMouseRotationField(string name) =>
        name is "mouserotationfromplayer" or "mouserotation" or "virtualmouserotation";

    private static void TrySet(FieldInfo field, ModPlayer target, object value)
    {
        try
        {
            field.SetValue(target, value);
        }
        catch
        {
            // A hostile/readonly member on some mod must not break the companion update.
        }
    }

    /// <summary>Reads Calamity rogue stealth. Max is 0 when no rogue armor is worn.</summary>
    public static bool TryGetRogueStealth(Player player, out float stealth, out float stealthMax)
    {
        stealth = 0f;
        stealthMax = 0f;
        foreach (ModPlayer modPlayer in player.ModPlayers)
        {
            System.Type type = modPlayer.GetType();
            if (type.Name != "CalamityPlayer")
                continue;

            if (!StealthFieldsByType.TryGetValue(type, out (FieldInfo Stealth, FieldInfo StealthMax) fields))
            {
                fields = (
                    type.GetField("rogueStealth", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic),
                    type.GetField("rogueStealthMax", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
                StealthFieldsByType[type] = fields;
            }

            if (fields.Stealth is null || fields.StealthMax is null)
                return false;

            try
            {
                stealth = (float)fields.Stealth.GetValue(modPlayer);
                stealthMax = (float)fields.StealthMax.GetValue(modPlayer);
                return true;
            }
            catch
            {
                return false;
            }
        }
        return false;
    }

    /// <summary>True for Calamity rogue weapons (RogueDamageClass or subclasses).</summary>
    public static bool IsRogueWeapon(Item item)
    {
        string damageClass = item.DamageType?.GetType().FullName;
        return damageClass is not null && damageClass.Contains("Rogue");
    }
}
