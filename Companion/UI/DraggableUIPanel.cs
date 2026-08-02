using Microsoft.Xna.Framework;
using Terraria;
using Terraria.GameContent.UI.Elements;
using Terraria.UI;

namespace MoreAIPlayer.Companion.UI;

public sealed class DraggableUIPanel : UIPanel
{
    private bool dragging;
    private Vector2 dragOffset;

    /// <summary>Current UI-space top-left. This is read before a panel is rebuilt so reopening
    /// the same UI does not put it back over the vanilla inventory.</summary>
    public Vector2 Position
    {
        get
        {
            CalculatedStyle dimensions = GetDimensions();
            return new Vector2(dimensions.X, dimensions.Y);
        }
    }

    public void SetPosition(Vector2 position)
    {
        HAlign = 0f;
        VAlign = 0f;
        Left.Set(position.X, 0f);
        Top.Set(position.Y, 0f);
    }

    public override void LeftMouseDown(UIMouseEvent evt)
    {
        base.LeftMouseDown(evt);
        CalculatedStyle dimensions = GetDimensions();
        bool inTitleBar = Main.MouseScreen.Y <= dimensions.Y + 44f;
        bool overCloseButtonArea = Main.MouseScreen.X >= dimensions.X + dimensions.Width - 90f;
        if (!inTitleBar || overCloseButtonArea)
            return;

        dragging = true;
        dragOffset = Main.MouseScreen - new Vector2(dimensions.X, dimensions.Y);
    }

    public override void LeftMouseUp(UIMouseEvent evt)
    {
        base.LeftMouseUp(evt);
        dragging = false;
    }

    public override void Update(GameTime gameTime)
    {
        base.Update(gameTime);
        if (!dragging)
            return;

        Vector2 desired = Main.MouseScreen - dragOffset;
        float maxX = System.Math.Max(0f, Main.screenWidth - GetDimensions().Width);
        float maxY = System.Math.Max(0f, Main.screenHeight - GetDimensions().Height);
        desired.X = MathHelper.Clamp(desired.X, 0f, maxX);
        desired.Y = MathHelper.Clamp(desired.Y, 0f, maxY);

        HAlign = 0f;
        VAlign = 0f;
        Left.Set(desired.X, 0f);
        Top.Set(desired.Y, 0f);
        Recalculate();
    }
}
