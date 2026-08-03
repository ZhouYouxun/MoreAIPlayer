using System.ComponentModel;
using Terraria.ModLoader.Config;

namespace MoreAIPlayer;

/// <summary>Client-only display preferences. Diagnostic overlays are intentionally opt-in so
/// normal play never has development text floating above companions.</summary>
[BackgroundColor(88, 60, 130, 235)]
public sealed class MoreAIPlayerClientConfig : ModConfig
{
    public static MoreAIPlayerClientConfig Instance;

    public override ConfigScope Mode => ConfigScope.ClientSide;

    public override void OnLoaded() => Instance = this;

    [DefaultValue(false)]
    public bool EnablePhantomPlayerDebugText;

    // Companions occupy real Main.player slots, so vanilla already draws a health bar over
    // them. Ours is off by default to avoid the duplicate bar; opt in to show it.
    [DefaultValue(false)]
    public bool ShowCompanionHealthBars;

    // Off by default: right-clicking a companion in the world does nothing, and the backpack
    // is opened only through the top-of-screen matrix panel. Enable to restore right-click open.
    [DefaultValue(false)]
    public bool AllowRightClickOpenBackpack;

    [DefaultValue(true)]
    public bool CompanionsFightAfterOwnerDeath;

    [DefaultValue(true)]
    public bool CompanionsTakeDamage;

    [DefaultValue(false)]
    public bool CompanionsAutoCollectWorldItems;

    [DefaultValue(false)]
    public bool CompanionsAllowBackpackWorldDrops;
}
