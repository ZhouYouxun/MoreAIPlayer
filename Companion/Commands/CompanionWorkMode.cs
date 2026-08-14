namespace MoreAIPlayer.Companion.Commands;

/// <summary>
/// The new command-wheel shell. These values are persisted independently from the historical
/// <see cref="CompanionMode"/> values so the books remain a complete, working fallback.
/// </summary>
public enum CompanionWorkMode : byte
{
    Idle,
    Combat,
    Exploration,
    Building,
    Farming,
    Fishing,
    Capturing
}

public enum IdleCommand : byte
{
    Stay,
    Follow,
    FreeActivity
}

public enum CombatCommand : byte
{
    SelfDefense,
    ProtectOwner,
    SharedTarget,
    Patrol,
    Garrison
}

public enum ExplorationCommand : byte
{
    Follow,
    FreeExplore
}

public enum BuildingCommand : byte
{
    Platform,
    Defensive,
    Destruction
}

public enum FarmingCommand : byte
{
    TreeFarm,
    HerbGarden,
    PumpkinFarm,
    Combined
}

public enum FishingCommand : byte
{
    Fish,
    FillWater
}

public enum CapturingCommand : byte
{
    FixedArea,
    FreeCapture
}
