using System;
using System.Collections.Generic;
using Terraria;

namespace MoreAIPlayer.Companion.Automation;

/// <summary>
/// Capability gaps which may be closed by several interchangeable routes. These are deliberately
/// not campaign steps: one chest, trip or lucky drop may cover several needs, while satisfying a
/// need removes every now-redundant provider from the next portfolio rebuild.
/// </summary>
[Flags]
public enum AutoNeed : ushort
{
    None = 0,
    ImmediateSafety = 1 << 0,
    Recovery = 1 << 1,
    InventorySpace = 1 << 2,
    WorldGate = 1 << 3,
    CraftingAccess = 1 << 4,
    Materials = 1 << 5,
    Equipment = 1 << 6,
    WeaponPower = 1 << 7,
    Vitality = 1 << 8,
    Ammunition = 1 << 9,
    Housing = 1 << 10,
    Arena = 1 << 11,
    Exploration = 1 << 12,
    Opportunity = 1 << 13,
    Encounter = 1 << 14
}

/// <summary>One currently actionable provider in the parallel plan portfolio.</summary>
public readonly record struct AutoPlanCandidate(
    AutoPlan Plan,
    AutoNeed Coverage,
    int Utility,
    int OpportunityBonus,
    int CompletionBonus);

/// <summary>
/// Rebuilt from live facts every tick. It keeps many useful routes eligible at once, but commits
/// the one physical player to exactly one action. A switch margin and a short healthy commitment
/// window prevent equal alternatives from making the player pace between two destinations.
/// </summary>
public sealed class AutoPlanPortfolio
{
    private const int SwitchMargin = 34;
    private const int MinimumHealthyCommitmentTicks = 180;
    private readonly Dictionary<long, AutoPlanCandidate> candidates = [];
    private static readonly AutoNeed[] ScoredNeeds =
    [
        AutoNeed.ImmediateSafety, AutoNeed.Recovery, AutoNeed.InventorySpace,
        AutoNeed.WorldGate, AutoNeed.CraftingAccess, AutoNeed.Materials,
        AutoNeed.Equipment, AutoNeed.WeaponPower, AutoNeed.Vitality,
        AutoNeed.Ammunition, AutoNeed.Housing, AutoNeed.Arena,
        AutoNeed.Exploration, AutoNeed.Opportunity, AutoNeed.Encounter
    ];

    public int Count => candidates.Count;
    public int LastAvailableCount { get; private set; }
    public int LastSelectedScore { get; private set; }
    public AutoNeed LastSelectedCoverage { get; private set; }

    public void Clear()
    {
        candidates.Clear();
        LastAvailableCount = 0;
        LastSelectedScore = 0;
        LastSelectedCoverage = AutoNeed.None;
    }

    /// <summary>Adds or merges an equivalent concrete plan. Coverage is combined because one real
    /// action may advance several independently discovered needs.</summary>
    public void Add(AutoPlan plan, AutoNeed coverage, int utility,
        int opportunityBonus = 0, int completionBonus = 0)
    {
        AutoPlanCandidate incoming = new(plan, coverage, utility,
            opportunityBonus, completionBonus);
        if (!candidates.TryGetValue(plan.StableKey, out AutoPlanCandidate existing))
        {
            candidates.Add(plan.StableKey, incoming);
            return;
        }

        candidates[plan.StableKey] = new AutoPlanCandidate(
            plan,
            existing.Coverage | coverage,
            System.Math.Max(existing.Utility, utility),
            System.Math.Max(existing.OpportunityBonus, opportunityBonus),
            System.Math.Max(existing.CompletionBonus, completionBonus));
    }

    public bool TryChoose(AutoContext ctx, out AutoPlan chosen)
    {
        AutoPlanCandidate best = default;
        AutoPlanCandidate current = default;
        int bestScore = int.MinValue;
        int currentScore = int.MinValue;
        int available = 0;

        foreach (AutoPlanCandidate candidate in candidates.Values)
        {
            // Immediate danger cannot be cooled down into chopping wood while a boss or fatal
            // condition is still live. Its own controller/recovery ladder remains responsible.
            if (!CoversEmergency(candidate.Coverage) &&
                ctx.TaskRuntime.IsPlanCoolingDown(candidate.Plan))
                continue;
            available++;
            int score = Score(ctx, candidate);
            if (candidate.Plan.StableKey == ctx.TaskRuntime.CurrentPlanKey)
            {
                current = candidate;
                currentScore = score;
            }
            if (score > bestScore || score == bestScore &&
                candidate.Plan.StableKey < best.Plan.StableKey)
            {
                best = candidate;
                bestScore = score;
            }
        }

        if (available <= 0)
        {
            chosen = default;
            LastAvailableCount = 0;
            LastSelectedScore = 0;
            LastSelectedCoverage = AutoNeed.None;
            return false;
        }

        // Keep a healthy route long enough to produce a meaningful result. Emergencies naturally
        // exceed this guard because their large coverage score clears the switch margin.
        if (currentScore != int.MinValue && best.Plan.StableKey != current.Plan.StableKey)
        {
            long age = (long)Main.GameUpdateCount - ctx.TaskRuntime.GoalStartedTick;
            bool healthy = ctx.TaskRuntime.LastResult == AutoTaskResult.Running &&
                ctx.TaskRuntime.TicksWithoutProgress < 120;
            bool protectedCommitment = healthy && age >= 0 &&
                age < MinimumHealthyCommitmentTicks;
            int dynamicSwitchMargin = SwitchMargin + System.Math.Min(100,
                ctx.TaskRuntime.PortfolioSwitchesWithoutProgress * 16);
            if (!CoversEmergency(best.Coverage) &&
                (protectedCommitment || bestScore < currentScore + dynamicSwitchMargin))
            {
                best = current;
                bestScore = currentScore;
            }
        }

        chosen = best.Plan;
        LastAvailableCount = available;
        LastSelectedScore = bestScore;
        LastSelectedCoverage = best.Coverage;
        return true;
    }

    private static int Score(AutoContext ctx, AutoPlanCandidate candidate)
    {
        int score = candidate.Utility + candidate.OpportunityBonus + candidate.CompletionBonus;
        score += CoverageScore(candidate.Coverage);
        score -= AutoRiskPolicy.EstimateTaskRisk(ctx, candidate.Plan);

        if (candidate.Plan.StableKey == ctx.TaskRuntime.CurrentPlanKey)
        {
            score += 44;
            // A plan which is neither accomplishing its task nor approaching a target gradually
            // loses its continuity privilege before the watchdog has to escalate it.
            score -= System.Math.Min(100,
                System.Math.Max(0, ctx.TaskRuntime.TicksWithoutProgress - 90) / 3);
        }
        if (ctx.TaskRuntime.ActiveTargetGoal == candidate.Plan.Goal &&
            ctx.TaskRuntime.ActiveTargetTile.HasValue)
            score += 18;

        score -= ctx.TaskRuntime.RecoveryFor(candidate.Plan.StableKey) * 14;
        return score;
    }

    /// <summary>Use the most important covered need plus a bounded multi-need bonus. Summing every
    /// weight directly would make vague exploration beat a nearly finished concrete task.</summary>
    private static int CoverageScore(AutoNeed coverage)
    {
        int largest = 0;
        int remainder = 0;
        foreach (AutoNeed need in ScoredNeeds)
        {
            if ((coverage & need) == 0)
                continue;
            int weight = NeedWeight(need);
            if (weight > largest)
            {
                remainder += largest;
                largest = weight;
            }
            else
                remainder += weight;
        }
        return largest + System.Math.Min(100, remainder / 4);
    }

    private static int NeedWeight(AutoNeed need) => need switch
    {
        AutoNeed.ImmediateSafety => 620,
        AutoNeed.Recovery => 470,
        AutoNeed.InventorySpace => 240,
        AutoNeed.WorldGate => 300,
        AutoNeed.CraftingAccess => 120,
        AutoNeed.Materials => 90,
        AutoNeed.Equipment => 180,
        AutoNeed.WeaponPower => 200,
        AutoNeed.Vitality => 210,
        AutoNeed.Ammunition => 140,
        AutoNeed.Housing => 130,
        AutoNeed.Arena => 190,
        AutoNeed.Exploration => 75,
        AutoNeed.Opportunity => 130,
        AutoNeed.Encounter => 330,
        _ => 0
    };

    private static bool CoversEmergency(AutoNeed coverage) =>
        (coverage & (AutoNeed.ImmediateSafety | AutoNeed.Recovery)) != 0;
}
