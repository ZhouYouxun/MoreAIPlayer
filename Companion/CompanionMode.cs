namespace MoreAIPlayer.Companion;

/// <summary>
/// How a companion behaves moment to moment.
///
/// The numeric values are persisted in the profile, so existing entries must keep their order:
/// Attack/Escort/Garrison were the original three-mode cycle and stay at 0/1/2.
///
/// The last three are gated on equipment — a mode whose tool slot is empty cannot be selected
/// (see <see cref="CompanionModeRules.IsAvailable"/>) and falls back to Attack if it somehow is.
/// </summary>
public enum CompanionMode
{
    /// <summary>综合进攻: roams freely, follows the owner, chases and attacks threats.</summary>
    Attack = 0,

    /// <summary>护卫: sticks tight to the owner as a bodyguard. Following, not killing, is the
    /// goal — it does not seek enemies out, it only answers what reaches the owner.</summary>
    Escort = 1,

    /// <summary>镇守: holds a fixed spot, marked the moment the mode is entered, and defends
    /// itself from there. Returns to the spot when knocked away and never follows the owner.</summary>
    Garrison = 2,

    /// <summary>捕捉: prioritises netting critters over fighting, and stops hurting critters
    /// entirely. A genuinely threatening enemy at close range still takes priority.
    /// Requires a bug net in the net slot.</summary>
    Catch = 3,

    /// <summary>钓鱼: casts into nearby water and stays put like Garrison, moving only when it
    /// cannot find water to cast into. Requires a fishing pole in the rod slot.</summary>
    Fishing = 4,

    /// <summary>挖矿: digs alongside the owner, harvests nearby ore and lights the tunnel.
    /// Still defends itself when something closes in. Requires a pickaxe in the pick slot.</summary>
    Mining = 5
}
