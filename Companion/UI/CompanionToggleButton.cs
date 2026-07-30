using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.GameContent;
using Terraria.UI;

namespace MoreAIPlayer.Companion.UI;

/// <summary>
/// Minimal rectangle toggle in the matrix-panel visual style. Visibility, label and click
/// behaviour are delegates so the same element serves the stealth-mode switch and the
/// per-weapon 左/右 toggles (which appear only while the slot holds a dual-use weapon).
/// </summary>
public sealed class CompanionToggleButton : UIElement
{
    private readonly Func<bool> isVisible;
    private readonly Func<string> getLabel;
    private readonly Action onToggle;
    private readonly float textScale;

    public CompanionToggleButton(Func<bool> isVisible, Func<string> getLabel, Action onToggle, float width, float height, float textScale = 0.75f)
    {
        this.isVisible = isVisible;
        this.getLabel = getLabel;
        this.onToggle = onToggle;
        this.textScale = textScale;
        Width.Set(width, 0f);
        Height.Set(height, 0f);
    }

    public override void LeftClick(UIMouseEvent evt)
    {
        base.LeftClick(evt);
        if (isVisible())
            onToggle();
    }

    public override void Update(GameTime gameTime)
    {
        base.Update(gameTime);
        if (isVisible() && ContainsPoint(Main.MouseScreen))
            Main.LocalPlayer.mouseInterface = true;
    }

    protected override void DrawSelf(SpriteBatch spriteBatch)
    {
        if (!isVisible())
            return;

        CalculatedStyle dimensions = GetDimensions();
        Rectangle area = dimensions.ToRectangle();
        bool hovered = ContainsPoint(Main.MouseScreen);

        Color back = hovered ? new Color(64, 74, 88) : new Color(32, 38, 48);
        Color border = hovered ? new Color(150, 205, 255) : new Color(80, 92, 108);
        spriteBatch.Draw(TextureAssets.MagicPixel.Value, area, back);
        DrawBorder(spriteBatch, area, border);

        string label = getLabel();
        Vector2 size = FontAssets.MouseText.Value.MeasureString(label) * textScale;
        Vector2 position = new(area.X + (area.Width - size.X) * 0.5f, area.Y + (area.Height - size.Y) * 0.5f);
        Utils.DrawBorderString(spriteBatch, label, position, new Color(226, 234, 248), textScale);
    }

    private static void DrawBorder(SpriteBatch spriteBatch, Rectangle area, Color color)
    {
        spriteBatch.Draw(TextureAssets.MagicPixel.Value, new Rectangle(area.X, area.Y, area.Width, 2), color);
        spriteBatch.Draw(TextureAssets.MagicPixel.Value, new Rectangle(area.X, area.Bottom - 2, area.Width, 2), color);
        spriteBatch.Draw(TextureAssets.MagicPixel.Value, new Rectangle(area.X, area.Y, 2, area.Height), color);
        spriteBatch.Draw(TextureAssets.MagicPixel.Value, new Rectangle(area.Right - 2, area.Y, 2, area.Height), color);
    }
}
