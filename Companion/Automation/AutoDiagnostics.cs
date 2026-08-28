using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.Localization;

namespace MoreAIPlayer.Companion.Automation;

/// <summary>
/// Formats the automation companion's runtime into the on-screen development readout (design §4.3).
/// It is drawn only when the client-side debug config is on, so an ordinary player never sees it,
/// and it never writes to disk or chat — the ring log is written on transitions only, inside
/// <see cref="AutoTaskRuntime"/>. This class is pure formatting.
/// </summary>
public static class AutoDiagnostics
{
    /// <summary>Compact multi-line readout for the current runtime. Most-recent first for the log.</summary>
    public static string[] BuildLines(AutoTaskRuntime runtime, AutoCompanionState state = null)
    {
        if (runtime is null)
            return [];

        List<string> lines =
        [
            $"AUTO stage: {WorldStage()}",
            $"goal: {runtime.CurrentGoal}  step: {runtime.CurrentStep}",
            $"result: {runtime.LastResult}  reason: {runtime.LastFailureReason}",
            $"noProgress: {runtime.TicksWithoutProgress}t  noMetric: {runtime.TicksWithoutMetricChange}t",
            $"attempts: {runtime.AttemptCount}  recoveryLv: {runtime.RecoveryLevel}",
            $"self-rescue: L{runtime.SelfRescueLevel}  count: {runtime.SelfRescueCount}  " +
                $"{runtime.LastSelfRescueReason}/{runtime.LastSelfRescueAction}",
            $"portfolio: {runtime.PortfolioAvailableCount}/{runtime.PortfolioCandidateCount}  " +
                $"score: {runtime.PortfolioSelectedScore}  covers: {runtime.PortfolioSelectedCoverage}",
            $"switch pressure: {runtime.PortfolioSwitchesWithoutProgress}",
            $"rejected targets: {runtime.RejectedTargetCount}  deferred plans: {runtime.DeferredPlanCount}",
        ];

        if (runtime.LastTargetTile.X >= 0 && runtime.LastTargetTile.Y >= 0)
        {
            Vector2 targetWorld = new(runtime.LastTargetTile.X * 16f + 8f,
                runtime.LastTargetTile.Y * 16f + 8f);
            int distanceTiles = (int)(Vector2.Distance(runtime.LastKnownPosition, targetWorld) / 16f);
            lines.Add($"target: {runtime.LastTargetTile.X},{runtime.LastTargetTile.Y}  delta: {distanceTiles} tiles");
        }

        long now = (long)Main.GameUpdateCount;
        if (runtime.BlockedUntilTick > now)
            lines.Add($"retry in: {runtime.BlockedUntilTick - now}t");
        if (runtime.BossFailureCount > 0)
            lines.Add($"boss fails: {runtime.BossFailureCount}");
        if (state is not null)
        {
            lines.Add($"campaign: {state.CampaignStage}  class: {state.PreferredClass}");
            int eligibleTownNpcs = AutoResourceBudget.ForecastEligibleTownNpcCount();
            lines.Add($"housing: {state.HousingRoomsCompleted}/{AutoResourceBudget.RequiredHousingRooms(state)} rooms  " +
                $"eligible NPCs: {eligibleTownNpcs}");
            lines.Add($"replant debt: {state.PendingReplantCount}");
            lines.Add($"weapon route: {state.LastWeaponRoute}");
            if (state.LastSelectedWeaponItemType > ItemID.None)
                lines.Add($"weapon: {Language.GetTextValue(Lang.GetItemName(state.LastSelectedWeaponItemType).Key)}  " +
                    $"score: {state.LastSelectedWeaponScore}  why: {state.LastSelectedWeaponReason}");
        }

        int shown = 0;
        foreach (string entry in runtime.RecentLog())
        {
            lines.Add($"· {entry}");
            if (++shown >= 4)
                break;
        }

        return [.. lines];
    }

    /// <summary>A one-word summary of how far the world's progression has advanced, for the header
    /// line. Cheap flag reads; safe to compute every draw frame.</summary>
    private static string WorldStage()
    {
        if (NPC.downedMoonlord)
            return "MoonLord-cleared";
        if (Main.hardMode)
            return "Hardmode";
        if (NPC.downedBoss3 || NPC.downedQueenBee || NPC.downedBoss2 || NPC.downedBoss1)
            return "PreHardmode-bossed";
        return "PreHardmode-early";
    }
}
