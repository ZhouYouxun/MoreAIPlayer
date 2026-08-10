namespace MoreAIPlayer.Companion.Building;

/// <summary>
/// The three persistent construction tactics exposed by the builder book. Each is toggled on or
/// off (left click) while one is selected (right-click wheel); a tactic keeps acting on its own
/// while enabled rather than running once from a cursor position.
/// </summary>
public enum CompanionBuildMode
{
    // Value 0 was the old "Smart Assist"; it is the same tactic, kept on 0 so existing saves load
    // straight into it. Values 1 (removed Bridge) and 4 (removed Rope Shaft) are intentionally
    // left unused so an old save never silently maps onto a different tactic.
    SmartBuild = 0,

    // Value 2 was "Shelter"; it now carries Defensive Build.
    DefensiveBuild = 2,

    // Value 3 was "Arena"; it now carries Battle Platform (same intent, refined rules).
    BattlePlatform = 3
}
