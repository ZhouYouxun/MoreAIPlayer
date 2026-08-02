using Microsoft.Xna.Framework;
using Terraria.Localization;

namespace MoreAIPlayer.Companion;

/// <summary>
/// Everything the rest of the mod needs to know about a <see cref="CompanionMode"/>: what it is
/// called, what colour represents it, and whether the companion is currently equipped for it.
/// Kept in one place so the selection wheel, the command book and the AI cannot drift apart.
/// </summary>
public static class CompanionModeRules
{
    /// <summary>Wheel order, which is also the order the sectors are laid out clockwise from the top.</summary>
    public static readonly CompanionMode[] WheelOrder =
    [
        CompanionMode.Attack,
        CompanionMode.Escort,
        CompanionMode.Garrison,
        CompanionMode.Catch,
        CompanionMode.Fishing,
        CompanionMode.Mining
    ];

    public static string Name(CompanionMode mode) => Text(mode switch
    {
        CompanionMode.Escort => "ModeEscort",
        CompanionMode.Garrison => "ModeGarrison",
        CompanionMode.Catch => "ModeCatch",
        CompanionMode.Fishing => "ModeFishing",
        CompanionMode.Mining => "ModeMining",
        _ => "ModeAttack"
    });

    public static string Description(CompanionMode mode) => Text(mode switch
    {
        CompanionMode.Escort => "ModeEscortDescription",
        CompanionMode.Garrison => "ModeGarrisonDescription",
        CompanionMode.Catch => "ModeCatchDescription",
        CompanionMode.Fishing => "ModeFishingDescription",
        CompanionMode.Mining => "ModeMiningDescription",
        _ => "ModeAttackDescription"
    });

    public static Color Tint(CompanionMode mode) => mode switch
    {
        CompanionMode.Escort => new Color(120, 190, 255),
        CompanionMode.Garrison => new Color(255, 215, 110),
        CompanionMode.Catch => new Color(140, 235, 150),
        CompanionMode.Fishing => new Color(110, 200, 235),
        CompanionMode.Mining => new Color(215, 170, 120),
        _ => new Color(255, 130, 120)
    };

    /// <summary>The equipment a mode needs before it can be picked, or null when it needs none.</summary>
    public static string RequirementText(CompanionMode mode) => mode switch
    {
        CompanionMode.Catch => Text("ModeCatchRequirement"),
        CompanionMode.Fishing => Text("ModeFishingRequirement"),
        CompanionMode.Mining => Text("ModeMiningRequirement"),
        _ => null
    };

    private static string Text(string key) =>
        Language.GetTextValue($"Mods.MoreAIPlayer.CompanionUI.{key}");

    /// <summary>
    /// Whether this companion is equipped for the mode. Selection is refused for unavailable
    /// modes rather than silently doing nothing, and the AI falls back to Attack if a mode
    /// becomes unavailable later (the player emptied the slot from the backpack UI).
    /// </summary>
    public static bool IsAvailable(CompanionMode mode, CompanionProfile profile) => mode switch
    {
        CompanionMode.Catch => CompanionSlotRules.IsCatchingTool(profile.NetTool) && !profile.NetTool.IsAir,
        CompanionMode.Fishing => CompanionSlotRules.IsFishingPole(profile.RodTool) && !profile.RodTool.IsAir,
        CompanionMode.Mining => CompanionSlotRules.IsPickaxe(profile.PickTool) && !profile.PickTool.IsAir,
        _ => true
    };

    /// <summary>The mode to actually run this tick: the chosen one, or Attack when its gear is gone.</summary>
    public static CompanionMode Effective(CompanionProfile profile) =>
        IsAvailable(profile.Mode, profile) ? profile.Mode : CompanionMode.Attack;
}
