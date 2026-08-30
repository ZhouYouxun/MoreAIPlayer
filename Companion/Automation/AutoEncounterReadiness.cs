using Terraria;

namespace MoreAIPlayer.Companion.Automation;

/// <summary>A fact vector for encounter preparation. Boss permission is intentionally conjunctive:
/// a rotating timer may decide when to revisit an upgrade route, but it may never turn an unmet
/// survival or equipment requirement into permission to start an encounter.</summary>
public readonly record struct AutoEncounterReadiness(
    bool VitalityReady,
    bool MiningAndArmourReady,
    bool WeaponReady,
    bool SuppliesReady,
    bool ArenaReady,
    bool TimingReady,
    bool ImmediateRiskReady)
{
    public bool ReadyExceptTiming => VitalityReady && MiningAndArmourReady && WeaponReady &&
        SuppliesReady && ArenaReady && ImmediateRiskReady;

    public bool ReadyToStart => VitalityReady && MiningAndArmourReady && WeaponReady &&
        SuppliesReady && ArenaReady && TimingReady && ImmediateRiskReady;

    public static AutoEncounterReadiness Evaluate(AutoContext ctx) => new(
        VitalityReady: !AutoVitalController.NeedsUpgrade(ctx),
        MiningAndArmourReady: !AutoCampaignGearController.NeedsMiningUpgrade(ctx),
        WeaponReady: !AutoCampaignController.NeedsLoadoutWork(ctx),
        SuppliesReady: !AutoCombatSupplyController.NeedsRestock(ctx),
        ArenaReady: !AutoArenaController.RequiresPreparedArena(ctx.State.CampaignStage) ||
            AutoArenaController.IsPrepared(ctx.State),
        TimingReady: !AutoCampaignController.RequiresNight(ctx.State.CampaignStage) || !Main.dayTime,
        ImmediateRiskReady: !AutoRecoveryController.IsOverwhelmed(ctx) &&
            !AutoWorldSurvivalController.NeedsFood(ctx));

    /// <summary>Finished imported worlds are usable context, not a free personal clear. The new
    /// profile must reach a terminal-world capability floor before its deployment may retire.</summary>
    public static bool TerminalWorldCapabilityReady(AutoContext ctx)
    {
        if (AutoCampaignController.NeedsPersonalFoundation(ctx) ||
            AutoVitalController.NeedsUpgrade(ctx) ||
            AutoCampaignGearController.NeedsMiningUpgrade(ctx) ||
            AutoCampaignController.NeedsLoadoutWork(ctx) ||
            AutoCombatSupplyController.NeedsRestock(ctx) ||
            AutoWorldSurvivalController.NeedsFood(ctx))
            return false;
        return ctx.State.MaxLife() >= 400 && !AutoRecoveryController.IsOverwhelmed(ctx);
    }
}
