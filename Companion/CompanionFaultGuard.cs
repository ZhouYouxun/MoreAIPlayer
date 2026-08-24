using System;
using System.Collections.Generic;
using Terraria;
using Terraria.Localization;

namespace MoreAIPlayer.Companion;

/// <summary>
/// The mod's single fault barrier, built around one rule: <b>minimal damage</b>. Every one of
/// this mod's subsystems runs inside vanilla's own per-tick and per-frame loops, so any
/// exception one of them throws would otherwise propagate straight into the game loop and take
/// the whole session down — a crash, a frozen frame, or a torn-up render. That is never an
/// acceptable outcome for an optional companion feature.
///
/// Instead, each execution boundary catches its own faults here. The offending subsystem simply
/// does not run <i>this</i> tick; the game, and every other subsystem, continues untouched. The
/// player is told with a throttled bottom-left notice and the full exception is logged. Think of
/// it as the independent circuit breaker on one subway line: one line tripping never derails the
/// others, and never stops the station.
///
/// When a single companion or hunter faults tick after tick, the systems that own it escalate to
/// retiring that one entity (see <see cref="AnnounceRetired"/>) rather than letting it fault
/// forever — the deliberate "fall back to the next-best outcome" step.
/// </summary>
internal static class CompanionFaultGuard
{
    internal const string WorldItemCollectionLane = "world-item collection";
    internal const string HealingPotionLane = "healing-potion consumption";
    internal const string BackpackDropLane = "backpack world drop";
    internal const string BackpackTransferLane = "backpack transfer";
    internal const string BackpackRecoveryLane = "companion backpack recovery";
    internal const string VirtualItemCheckLane = "virtual item use";
    internal const string VirtualProjectileLane = "virtual projectile update";
    internal const string AutomationLane = "automation companion";
    internal const string AutomationCraftingLane = "automation crafting";
    internal const string AutomationBagOpeningLane = "automation bag opening";
    internal const string AutomationWeaponEvaluationLane = "automation weapon evaluation";

    // Throttle windows, in 60 Hz update ticks. A persistent fault therefore surfaces at most one
    // on-screen line every few seconds and one log line every couple of seconds per lane, rather
    // than sixty of each per second.
    private const ulong MessageThrottleTicks = 300; // ~5s between on-screen notices per lane
    private const ulong LogThrottleTicks = 120;     // ~2s between log lines per lane

    private static readonly Dictionary<string, ulong> lastMessageFrame = new();
    private static readonly Dictionary<string, ulong> lastLogFrame = new();
    private static readonly HashSet<string> trippedLanes = [];

    /// <summary>
    /// A tripped lane is deliberately fail-closed for the rest of this world session. A caller
    /// may still run its harmless cleanup, but it must not enter the same foreign/modded hook
    /// again until a reload gives the player a clean, explicit retry point.
    /// </summary>
    public static bool IsAvailable(string lane) => !trippedLanes.Contains(lane);

    /// <summary>
    /// Runs a drawing callback and always reports success to the interface-layer stack. A throwing
    /// overlay must never abort the layer that draws the rest of the game's HUD, so a fault here is
    /// swallowed after being reported: the overlay is simply missing for the frame.
    /// </summary>
    public static bool RunDraw(string lane, Action body)
    {
        if (body is null)
            return true;
        try
        {
            body();
        }
        catch (Exception exception)
        {
            Report(lane, exception);
        }
        return true;
    }

    /// <summary>
    /// Same rendering isolation as <see cref="RunDraw"/>, but for optional visual polish where
    /// repeatedly surfacing a chat warning is worse than temporarily omitting that one layer.
    /// The full exception is still throttled and written to the mod log for diagnosis.
    /// </summary>
    public static bool RunDrawQuiet(string lane, Action body)
    {
        if (body is null)
            return true;
        try
        {
            body();
        }
        catch (Exception exception)
        {
            ulong now = Main.GameUpdateCount;
            if (ShouldFire(lastLogFrame, lane, now, LogThrottleTicks))
                SafeLog($"[MoreAIPlayer] optional visual subsystem '{lane}' faulted and was hidden for this frame.", exception);
        }
        return true;
    }

    /// <summary>
    /// Runs a subsystem body, reporting and isolating any fault. Returns true when it completed
    /// cleanly. Callers that track a per-entity fault streak use the result to decide when to
    /// retire a persistently faulting entity.
    /// </summary>
    public static bool Run(string lane, Action body)
    {
        if (body is null)
            return true;
        try
        {
            body();
            return true;
        }
        catch (Exception exception)
        {
            Report(lane, exception);
            try
            {
                VirtualPlayerExecutionContext.ForceRestoreIfLeaked();
            }
            catch
            {
                // Reporting one fault must never cascade into another one while recovering.
            }
            return false;
        }
    }

    /// <summary>
    /// Runs an operation which changes items, synthetic-player context, or world state. An
    /// exception trips only this lane; all unrelated companion systems stay available. This is
    /// intentionally not a timeout: Terraria runs these callbacks on its main thread and cannot
    /// safely interrupt foreign code that has already stopped returning.
    /// </summary>
    public static bool RunCritical(string lane, Action body)
    {
        if (!EnsureAvailable(lane))
            return false;

        try
        {
            body?.Invoke();
            return true;
        }
        catch (Exception exception)
        {
            Trip(lane, exception);
            return false;
        }
    }

    public static bool RunCritical<T>(string lane, Func<T> body, out T result)
    {
        result = default;
        if (!EnsureAvailable(lane))
            return false;

        try
        {
            result = body is null ? default : body();
            return true;
        }
        catch (Exception exception)
        {
            Trip(lane, exception);
            return false;
        }
    }

    /// <summary>Shows a throttled safety explanation when a feature is disabled before it starts.</summary>
    public static void AnnounceSafetyBlock(string faultKey)
    {
        if (!Main.dedServ && ShouldFire(lastMessageFrame, faultKey, Main.GameUpdateCount, MessageThrottleTicks))
            SafeNewText(FaultText(faultKey), 255, 190, 100);
    }

    /// <summary>
    /// Records a fault caught by an inline try/catch. Used on the hot paths (the ItemCheck and
    /// Projectile.Update detours, the per-player ModPlayer hooks) where wrapping the body in a
    /// delegate would allocate every tick and where a manual catch also controls the exact order
    /// of the surrounding using/finally state restore.
    /// </summary>
    public static void Report(string lane, Exception exception)
    {
        ulong now = Main.GameUpdateCount;

        if (ShouldFire(lastLogFrame, lane, now, LogThrottleTicks))
            SafeLog($"[MoreAIPlayer] subsystem '{lane}' faulted and was isolated to keep the game running.", exception);

        if (!Main.dedServ && ShouldFire(lastMessageFrame, lane, now, MessageThrottleTicks))
            SafeNewText(FaultText("SubsystemError", lane), 255, 150, 90);
    }

    /// <summary>
    /// Records a fault for which the owning subsystem has already discarded only the damaged
    /// state and rebuilt itself. Unlike <see cref="Trip"/>, a successful recovery does not disable
    /// the lane for the rest of the session. The player sees this only after a real exception;
    /// healthy sessions remain completely silent.
    /// </summary>
    public static void ReportRecovered(string source, Exception exception, bool recoverySucceeded)
    {
        string lane = $"{BackpackRecoveryLane}: {source}";
        ulong now = Main.GameUpdateCount;

        if (ShouldFire(lastLogFrame, lane, now, LogThrottleTicks))
        {
            string outcome = recoverySucceeded ? "recovered" : "could not be rebuilt";
            SafeLog($"[MoreAIPlayer] {lane} faulted and {outcome}. Only the affected companion state was isolated.", exception);
        }

        string messageLane = recoverySucceeded
            ? $"{BackpackRecoveryLane}: recovered"
            : $"{BackpackRecoveryLane}: failed";
        if (!Main.dedServ && ShouldFire(lastMessageFrame, messageLane, now, MessageThrottleTicks))
        {
            string key = recoverySucceeded ? "BackpackRecovered" : "BackpackRecoveryFailed";
            SafeNewText(FaultText(key, source), 255,
                recoverySucceeded ? (byte)190 : (byte)120, recoverySucceeded ? (byte)100 : (byte)90);
        }
    }

    /// <summary>
    /// Opens a circuit after a stateful operation faulted. Restore any leaked virtual-player
    /// context before notifying the player, then prevent future calls into this lane.
    /// </summary>
    public static void Trip(string lane, Exception exception)
    {
        bool firstTrip = trippedLanes.Add(lane);
        Report(lane, exception);

        try
        {
            VirtualPlayerExecutionContext.ForceRestoreIfLeaked();
        }
        catch
        {
            // State restoration is best-effort here; Report must remain non-throwing even if a
            // different mod corrupted an unrelated global while the original exception escaped.
        }

        if (firstTrip && !Main.dedServ)
            SafeNewText(FaultText("CircuitOpened", lane), 255, 120, 90);
    }

    /// <summary>Announces that an entity was retired after repeated faults. The owning system has
    /// already removed it safely; this is only the player-facing explanation.</summary>
    public static void AnnounceRetired(string faultKey)
    {
        if (Main.dedServ)
            return;
        SafeNewText(FaultText(faultKey), 255, 120, 90);
    }

    internal static void Reset()
    {
        lastMessageFrame.Clear();
        lastLogFrame.Clear();
        trippedLanes.Clear();
    }

    private static bool EnsureAvailable(string lane)
    {
        if (!trippedLanes.Contains(lane))
            return true;

        AnnounceSafetyBlock("CircuitStillOpen");
        return false;
    }

    private static bool ShouldFire(Dictionary<string, ulong> map, string lane, ulong now, ulong window)
    {
        // A world reload resets Main.GameUpdateCount, so now < last is treated as "fire again".
        if (map.TryGetValue(lane, out ulong last) && now >= last && now - last < window)
            return false;
        map[lane] = now;
        return true;
    }

    private static string FaultText(string key, params object[] args)
    {
        try
        {
            return Language.GetTextValue($"Mods.MoreAIPlayer.Fault.{key}", args);
        }
        catch
        {
            // Localization must never be the reason the fault barrier itself throws.
            return $"[MoreAIPlayer] internal error ({key})";
        }
    }

    private static void SafeNewText(string text, byte r, byte g, byte b)
    {
        try
        {
            Main.NewText(text, r, g, b);
        }
        catch
        {
            // If even the on-screen notice fails there is nothing more to do; the log line, sent
            // first, is the durable record.
        }
    }

    private static void SafeLog(string message, Exception exception)
    {
        try
        {
            MoreAIPlayer.Instance?.Logger?.Error(message, exception);
        }
        catch
        {
            // Never let logging failure escalate a fault we are in the middle of containing.
        }
    }
}
