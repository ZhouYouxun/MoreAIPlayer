namespace MoreAIPlayer.Companion.Automation;

/// <summary>The one-way vanilla campaign milestones owned by the automation companion. The live
/// world flags are authoritative; the saved value is only a resume/report hint.</summary>
public enum AutoCampaignStage : byte
{
    Foundation,
    EyeOfCthulhu,
    EvilBoss,
    Skeletron,
    WallOfFlesh,
    MechanicalBosses,
    Plantera,
    Golem,
    LunaticCultist,
    SolarPillar,
    VortexPillar,
    NebulaPillar,
    StardustPillar,
    MoonLord,
    Completed
}

/// <summary>Combat direction inferred from the strongest weapon the companion really owns.</summary>
public enum AutoCombatClass : byte
{
    Flexible,
    Melee,
    Ranged,
    Magic,
    Summoner
}
