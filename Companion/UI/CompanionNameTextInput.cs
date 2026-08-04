using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.GameContent;
using Terraria.UI;

namespace MoreAIPlayer.Companion.UI;

/// <summary>
/// A compact text field backed by Terraria's platform text-input pipeline. In particular,
/// <see cref="Main.GetInputText(string, bool)"/> keeps IME and Unicode input working instead of
/// treating the field as a collection of physical keyboard keys.
/// </summary>
public sealed class CompanionNameTextInput : UIElement
{
    public string Text { get; private set; }

    public CompanionNameTextInput(string initialText)
    {
        Text = initialText ?? string.Empty;
        Main.clrInput();
    }

    public override void Update(GameTime gameTime)
    {
        // The rename dialog is modal, so this field owns text input for its full lifetime.
        // Clearing the input buffer when it was created prevents the click that opened it
        // from leaking into the first typed character.
        Text = Main.GetInputText(Text, false);
        Main.LocalPlayer.mouseInterface = true;
        base.Update(gameTime);
    }

    protected override void DrawSelf(SpriteBatch spriteBatch)
    {
        Rectangle area = GetDimensions().ToRectangle();
        bool hovered = area.Contains(Main.MouseScreen.ToPoint());
        Color background = hovered ? new Color(33, 46, 68, 245) : new Color(22, 30, 46, 242);
        Color border = hovered ? new Color(143, 205, 255) : new Color(84, 145, 202);
        spriteBatch.Draw(TextureAssets.MagicPixel.Value, area, background);
        DrawBorder(spriteBatch, area, border);

        string shown = Text;
        const float scale = 0.82f;
        float maxWidth = area.Width - 18f;
        while (shown.Length > 0 && FontAssets.MouseText.Value.MeasureString(shown).X * scale > maxWidth)
            shown = shown[..^1];
        if (shown.Length < Text.Length)
            shown += "…";

        Vector2 position = new(area.X + 9f, area.Y + (area.Height - FontAssets.MouseText.Value.LineSpacing * scale) * 0.5f);
        Utils.DrawBorderString(spriteBatch, shown, position, new Color(232, 240, 255), scale);

        if ((int)(Main.GlobalTimeWrappedHourly * 2f) % 2 == 0)
        {
            float cursorX = position.X + FontAssets.MouseText.Value.MeasureString(shown).X * scale + 1f;
            spriteBatch.Draw(TextureAssets.MagicPixel.Value,
                new Rectangle((int)cursorX, area.Y + 8, 2, area.Height - 16), new Color(220, 238, 255));
        }
    }

    private static void DrawBorder(SpriteBatch spriteBatch, Rectangle area, Color color)
    {
        const int thickness = 2;
        spriteBatch.Draw(TextureAssets.MagicPixel.Value, new Rectangle(area.X, area.Y, area.Width, thickness), color);
        spriteBatch.Draw(TextureAssets.MagicPixel.Value, new Rectangle(area.X, area.Bottom - thickness, area.Width, thickness), color);
        spriteBatch.Draw(TextureAssets.MagicPixel.Value, new Rectangle(area.X, area.Y, thickness, area.Height), color);
        spriteBatch.Draw(TextureAssets.MagicPixel.Value, new Rectangle(area.Right - thickness, area.Y, thickness, area.Height), color);
    }
}
