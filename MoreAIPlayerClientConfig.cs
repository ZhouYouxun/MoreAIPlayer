using System.ComponentModel;
using Terraria.ModLoader;
using Terraria.ModLoader.Config;

namespace MoreAIPlayer;

/// <summary>Client-only display preferences. Diagnostic overlays are intentionally opt-in so
/// normal play never has development text floating above companions.</summary>
[BackgroundColor(88, 60, 130, 235)]
public sealed class MoreAIPlayerClientConfig : ModConfig
{
    /// <summary>
    /// Always resolves tModLoader's currently applied config object. Do not cache this at a
    /// gameplay call site: users are allowed to change every option from the in-game menu.
    /// </summary>
    public static MoreAIPlayerClientConfig Current => ModContent.GetInstance<MoreAIPlayerClientConfig>();

    public static bool CompanionDamageEnabled => Current?.CompanionsTakeDamage == true;

    public override ConfigScope Mode => ConfigScope.ClientSide;

    // Values are intentionally read through Current at the point of use. OnChanged needs no
    // cache refresh because the in-game menu's newly applied object is immediately the one
    // returned by ModContent.GetInstance above.

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

    // Survivability is opt-in. A new companion is a helper, not another health bar the player
    // must babysit; enabling this turns on the full vanilla damage-and-revive loop.
    [DefaultValue(false)]
    public bool CompanionsTakeDamage;

    [DefaultValue(false)]
    public bool CompanionsAutoCollectWorldItems;

    [DefaultValue(false)]
    public bool CompanionsAllowBackpackWorldDrops;
}
