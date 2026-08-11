namespace MoreAIPlayer.Companion;

/// <summary>Persistent creation history for a profile. A completed automation run remains an
/// automation profile even after its special brain stops, so entertainment combat settings cannot
/// silently change the meaning of an old save.</summary>
public enum CompanionOrigin : byte
{
    OrdinaryDebug,
    Automation
}
