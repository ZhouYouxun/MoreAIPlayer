using MoreAIPlayer.Companion.AI;
using Terraria;

namespace MoreAIPlayer.Companion.Automation;

/// <summary>
/// The per-tick bundle handed to the planner and controllers so they share one navigator, actuator
/// and capability snapshot instead of each re-resolving state. Owned and refreshed by
/// <see cref="AutoCompanionBrain"/>; the fields are stable references that live as long as the
/// companion does.
/// </summary>
public sealed class AutoContext
{
    public CompanionInstance Instance;
    public Player Me;
    public CompanionProfile Profile;
    public AutoCompanionState State;
    public CompanionNavigator Navigator;
    public AutoActuator Actuator;
    public AutoCapabilities Capabilities;
}
