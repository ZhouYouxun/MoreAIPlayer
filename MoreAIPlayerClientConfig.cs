using System.ComponentModel;
using Terraria.ModLoader.Config;

namespace MoreAIPlayer;

/// <summary>Client-only display preferences. Diagnostic overlays are intentionally opt-in so
/// normal play never has development text floating above companions.</summary>
[BackgroundColor(10, 42, 26, 235)]
public sealed class MoreAIPlayerClientConfig : ModConfig
{
    public static MoreAIPlayerClientConfig Instance;

    public override ConfigScope Mode => ConfigScope.ClientSide;

    public override void OnLoaded() => Instance = this;

    [Header("CompanionDiagnostics")]
    [BackgroundColor(42, 255, 126, 220)]
    [DefaultValue(false)]
    public bool EnablePhantomPlayerDebugText;
}
