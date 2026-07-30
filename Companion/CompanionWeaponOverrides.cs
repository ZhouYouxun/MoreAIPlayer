using System.Collections.Generic;
using System.Reflection;
using Terraria;
using Terraria.ModLoader;

namespace MoreAIPlayer.Companion;

public enum CompanionAttackMode
{
    /// <summary>No special handling: left click, or right click if the profile toggled it.</summary>
    Default,
    /// <summary>Always left click; the right click must never be used (e.g. Exoblade's dash).</summary>
    ForceLeft,
    /// <summary>Always right click; the left click is unusable (e.g. DeathsAscension).</summary>
    ForceRight,
    /// <summary>弹金币: throw coins with right click, then shoot a coin with left click, repeat
    /// (Midas Prime / Crackshot Colt).</summary>
    CoinVolley
}

/// <summary>
/// Per-weapon special-case registry keyed by the item's full name ("ModName/ClassName").
/// These behaviors apply ONLY to the listed weapons and never leak onto anything else.
/// Add new entries to <see cref="OverridesByFullName"/> as more weapons need scripting.
/// </summary>
public static class CompanionWeaponOverrides
{
    private static readonly Dictionary<string, CompanionAttackMode> OverridesByFullName = new()
    {
        ["CalamityMod/MidasPrime"] = CompanionAttackMode.CoinVolley,
        ["CalamityMod/CrackshotColt"] = CompanionAttackMode.CoinVolley,
        ["CalamityMod/Exoblade"] = CompanionAttackMode.ForceLeft,
        ["CalamityMod/DeathsAscension"] = CompanionAttackMode.ForceRight,
    };

    private static readonly Dictionary<int, bool> HasAltFunctionByItemType = [];

    public static CompanionAttackMode GetMode(Item item) =>
        item.ModItem is not null && OverridesByFullName.TryGetValue(item.ModItem.FullName, out CompanionAttackMode mode)
            ? mode
            : CompanionAttackMode.Default;

    /// <summary>A stable identity for a weapon that survives reloads (used for saved toggles).</summary>
    public static string ItemKey(Item item) => item.ModItem?.FullName ?? $"Terraria/{item.type}";

    /// <summary>
    /// True when the weapon has both a left and a right click (a ModItem overriding
    /// AltFunctionUse) and is not covered by a scripted special case — these weapons get
    /// the 左/右 toggle in the companion UI.
    /// </summary>
    public static bool HasConfigurableAltUse(Item item)
    {
        if (item.IsAir || GetMode(item) != CompanionAttackMode.Default)
            return false;
        return OverridesAltFunctionUse(item);
    }

    public static bool OverridesAltFunctionUse(Item item)
    {
        if (item.ModItem is null)
            return false;

        if (HasAltFunctionByItemType.TryGetValue(item.type, out bool cached))
            return cached;

        MethodInfo method = item.ModItem.GetType().GetMethod(
            nameof(ModItem.AltFunctionUse),
            BindingFlags.Instance | BindingFlags.Public,
            [typeof(Player)]);
        bool overridden = method is not null && method.DeclaringType != typeof(ModItem);
        HasAltFunctionByItemType[item.type] = overridden;
        return overridden;
    }

    public static void ClearCaches() => HasAltFunctionByItemType.Clear();
}
