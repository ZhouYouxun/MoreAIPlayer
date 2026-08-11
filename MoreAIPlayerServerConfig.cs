using System.ComponentModel;
using Terraria.ModLoader;
using Terraria.ModLoader.Config;

namespace MoreAIPlayer;

/// <summary>Authoritative gameplay rules. These are deliberately separate from client display
/// preferences so damage cannot depend on which client opened the configuration menu.</summary>
[BackgroundColor(130, 48, 58, 235)]
public sealed class MoreAIPlayerServerConfig : ModConfig
{
    public static MoreAIPlayerServerConfig Current => ModContent.GetInstance<MoreAIPlayerServerConfig>();

    public static bool OrdinaryHostilityEnabled => Current?.OrdinaryCompanionsHostileToPlayers == true;

    public override ConfigScope Mode => ConfigScope.ServerSide;

    [DefaultValue(false)]
    public bool OrdinaryCompanionsHostileToPlayers;
}
