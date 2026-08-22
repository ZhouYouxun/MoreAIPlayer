using Terraria;
using Terraria.ID;

namespace MoreAIPlayer.Companion;

/// <summary>
/// The single authority on "what is this mod allowed to do on the current network side". Before
/// this existed, every entry point re-interpreted <see cref="Main.netMode"/> and
/// <see cref="Main.dedServ"/> on its own, so a new feature could quietly forget one of the rules.
/// Key entry points now ask this class; the historical local checks are kept as a second line of
/// defence.
///
/// The public contract is unchanged: companions remain singleplayer-only and are never synced to a
/// server. The value here is a stable set of names to gate against, and a clear seam where a future
/// server-authoritative implementation would relax individual permissions (world mutation and
/// damage) without touching the singleplayer-only synthetic-player gate.
/// </summary>
public static class CompanionRuntimePolicy
{
    /// <summary>Whether synthetic companion/hunter players (real entries in <see cref="Main.player"/>)
    /// may exist and update. Singleplayer only — a client cannot fabricate a server-assigned slot,
    /// so this is the master gate everything companion-related hangs off.</summary>
    public static bool CanRunSyntheticPlayers => Main.netMode == NetmodeID.SinglePlayer;

    /// <summary>Whether this side may perform authoritative world edits (tile/wall placement, door
    /// toggling, mining). Singleplayer today; a server-authoritative path would also qualify. A
    /// multiplayer client must never place tiles optimistically.</summary>
    public static bool CanMutateWorld => Main.netMode == NetmodeID.SinglePlayer;

    /// <summary>Whether local-client UI (panels, wheels, hover, hotkeys) may run. Never on a
    /// dedicated server; callers still add the per-owner "this is my companion" check.</summary>
    public static bool CanRunLocalUI => !Main.dedServ;

    /// <summary>Whether companion-specific panels, wheels, overlays and hover input may run.
    /// These controls have no useful multiplayer target while synthetic players are disabled,
    /// so do not register or update them merely because this machine happens to be a client.</summary>
    public static bool CanRunCompanionUI => CanRunSyntheticPlayers && CanRunLocalUI;

    /// <summary>Whether it is safe to run this mod's own state cleanup (registries, leaked-context
    /// recovery, releasing synthetic slots). Safe on every side because it only ever touches this
    /// mod's own bookkeeping — it must never kill a real connected player's projectiles or overwrite
    /// a slot that now belongs to a network player.</summary>
    public static bool CanRunNetworkCleanup => true;

    /// <summary>Whether this side settles companion damage. Singleplayer today; a real multiplayer
    /// build would move authoritative damage to the server.</summary>
    public static bool IsAuthoritativeDamageSide => Main.netMode == NetmodeID.SinglePlayer;

    /// <summary>Convenience mirror of <see cref="Main.dedServ"/> for readability at call sites that
    /// skip all client-only work on a dedicated server.</summary>
    public static bool IsDedicatedServer => Main.dedServ;
}
