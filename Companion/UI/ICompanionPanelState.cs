using Microsoft.Xna.Framework;

namespace MoreAIPlayer.Companion.UI;

/// <summary>Lets the two pages preserve one draggable window position while swapping views.</summary>
internal interface ICompanionPanelState
{
    Vector2? PanelPosition { get; }
}
