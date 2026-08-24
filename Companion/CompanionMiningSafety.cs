using Microsoft.Xna.Framework;
using Terraria;
using Terraria.Localization;

namespace MoreAIPlayer.Companion;

/// <summary>Shared safety boundary for ordinary-companion terrain destruction.</summary>
public static class CompanionMiningSafety
{
    public const int BaseProtectionRadiusTiles = 200;

    /// <summary>
    /// Whether this mining target is inside the protected circle around the world's generation
    /// spawn. Automation companions are the one explicit exception and may progress normally.
    /// </summary>
    public static bool IsMiningBlocked(CompanionInstance instance, int tileX, int tileY)
    {
        if (instance?.IsAutoIdentity == true)
            return false;
        if (MoreAIPlayerClientConfig.Current?.ProtectBaseFromCompanionMining == false)
            return false;

        long offsetX = tileX - Main.spawnTileX;
        long offsetY = tileY - Main.spawnTileY;
        return offsetX * offsetX + offsetY * offsetY <=
            (long)BaseProtectionRadiusTiles * BaseProtectionRadiusTiles;
    }

    /// <summary>Shows the localized safety notice once per character, on their first mining tactic.</summary>
    public static void NotifyFirstMiningTactic(Player owner)
    {
        if (owner is null || !owner.active || owner.whoAmI != Main.myPlayer)
            return;

        CompanionPlayer data = owner.GetModPlayer<CompanionPlayer>();
        if (data.BaseProtectionMiningTipShown)
            return;

        data.BaseProtectionMiningTipShown = true;
        Main.NewText(Language.GetTextValue(
            "Mods.MoreAIPlayer.CompanionCommand.BaseProtectionMiningTip",
            BaseProtectionRadiusTiles), new Color(255, 215, 120));
    }
}
