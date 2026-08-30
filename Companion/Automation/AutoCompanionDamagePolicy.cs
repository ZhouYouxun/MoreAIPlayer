using Terraria;

namespace MoreAIPlayer.Companion.Automation;

/// <summary>Automation-only survivability. The synthetic body still receives the same owned
/// Terraria Hurt calculation as a player; these are explicit built-in stats, not invulnerability.</summary>
public static class AutoCompanionDamagePolicy
{
    public const int BuiltInDefense = 10;
    public const float IncomingDamageMultiplier = 0.8f;

    public static void ApplyBuiltInDefense(Player player)
    {
        if (CompanionEntitySystem.IsAutoCompanion(player))
            player.statDefense += BuiltInDefense;
    }

    /// <summary>Applies after Terraria/mod hooks have assembled their modifiers, so it is an honest
    /// final 20% hit reduction. The fallback damage route calls the same method explicitly.</summary>
    public static void ApplyFinalDamageReduction(Player player, ref Player.HurtModifiers modifiers)
    {
        if (CompanionEntitySystem.IsAutoCompanion(player))
            modifiers.FinalDamage *= IncomingDamageMultiplier;
    }
}
