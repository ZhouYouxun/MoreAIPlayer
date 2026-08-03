using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.Audio;
using Terraria.GameContent;
using Terraria.ID;
using Terraria.Localization;

namespace MoreAIPlayer.Companion.UI;

/// <summary>A compact six-stop matrix slider. The cursor can be dragged freely, but selection
/// always snaps to the six supported gameplay-safe scales.</summary>
public static class PlayerScaleMatrixPanel
{
    private static bool open;
    private static bool fadeOut;
    private static bool dragging;
    private static float opacity;

    public static bool IsOpen => open;

    public static void Toggle()
    {
        if (open && !fadeOut)
        {
            StartClose();
            return;
        }

        open = true;
        fadeOut = false;
        opacity = 0f;
        SoundEngine.PlaySound(SoundID.MenuOpen with { Volume = 0.6f });
    }

    public static void StartClose()
    {
        if (!open || fadeOut)
            return;
        fadeOut = true;
        dragging = false;
        SoundEngine.PlaySound(SoundID.MenuClose with { Volume = 0.55f });
    }

    public static void CloseInstant()
    {
        open = false;
        fadeOut = false;
        dragging = false;
        opacity = 0f;
    }

    public static void DrawAndInteract() =>
        CompanionFaultGuard.RunDraw("player scale matrix panel", DrawAndInteractCore);

    private static void DrawAndInteractCore()
    {
        if (!open)
            return;
        if (Main.gameMenu || Main.dedServ || Main.netMode != NetmodeID.SinglePlayer || Main.hideUI || Main.mapFullscreen)
        {
            CloseInstant();
            return;
        }

        opacity = MathHelper.Clamp(opacity + (fadeOut ? -0.14f : 0.18f), 0f, 1f);
        if (fadeOut && opacity <= 0f)
        {
            CloseInstant();
            return;
        }

        const int panelWidth = 420;
        const int panelHeight = 192;
        Vector2 center = new(Main.screenWidth * 0.5f, Main.screenHeight * 0.5f);
        Rectangle panel = new((int)(center.X - panelWidth * 0.5f), (int)(center.Y - panelHeight * 0.5f), panelWidth, panelHeight);
        Point mouse = Main.MouseScreen.ToPoint();
        bool inside = panel.Contains(mouse);
        if (inside)
            Main.LocalPlayer.mouseInterface = true;

        if (Main.mouseLeft && Main.mouseLeftRelease && !inside)
        {
            StartClose();
            return;
        }

        SpriteBatch sb = Main.spriteBatch;
        Texture2D pixel = TextureAssets.MagicPixel.Value;
        Rectangle border = panel;
        border.Inflate(2, 2);
        sb.Draw(pixel, border, new Color(88, 235, 255) * opacity);
        sb.Draw(pixel, panel, new Color(9, 18, 32, 242) * opacity);

        // Matrix cells are decoration as well as a clear interaction boundary.
        for (int y = 0; y < 5; y++)
        for (int x = 0; x < 12; x++)
        {
            Rectangle cell = new(panel.X + 16 + x * 32, panel.Y + 50 + y * 16, 25, 10);
            sb.Draw(pixel, cell, new Color(24, 74, 104, 96) * opacity);
        }

        string title = Text("ScalePanelTitle");
        Terraria.Utils.DrawBorderString(sb, title, new Vector2(panel.X + 16, panel.Y + 13),
            new Color(135, 245, 255) * opacity, 1f);
        Rectangle close = new(panel.Right - 48, panel.Y + 12, 30, 22);
        bool closeHovered = close.Contains(mouse);
        sb.Draw(pixel, close, (closeHovered ? new Color(180, 70, 85) : new Color(82, 42, 62)) * opacity);
        Terraria.Utils.DrawBorderString(sb, Text("Close"), new Vector2(close.X + 6, close.Y + 3), Color.White * opacity, 0.7f);
        if (closeHovered && Main.mouseLeft && Main.mouseLeftRelease)
        {
            Main.mouseLeftRelease = false;
            StartClose();
            return;
        }

        PlayerScalePlayer scalePlayer = Main.LocalPlayer.GetModPlayer<PlayerScalePlayer>();
        float[] values = PlayerScalePlayer.AllowedScales;
        const int sliderLeftPadding = 34;
        const int sliderWidth = 352;
        int sliderY = panel.Y + 122;
        Rectangle sliderHitbox = new(panel.X + sliderLeftPadding - 10, sliderY - 18, sliderWidth + 20, 44);
        float selectedT = IndexOf(scalePlayer.SelectedScale) / (float)(values.Length - 1);
        float knobX = panel.X + sliderLeftPadding + sliderWidth * selectedT;

        Rectangle track = new(panel.X + sliderLeftPadding, sliderY, sliderWidth, 5);
        sb.Draw(pixel, track, new Color(38, 100, 133) * opacity);
        sb.Draw(pixel, new Rectangle(track.X, track.Y, (int)(knobX - track.X), track.Height), new Color(100, 238, 255) * opacity);

        for (int i = 0; i < values.Length; i++)
        {
            float x = panel.X + sliderLeftPadding + sliderWidth * i / (values.Length - 1f);
            Rectangle stop = new((int)x - 6, sliderY - 6, 12, 17);
            bool selected = i == IndexOf(scalePlayer.SelectedScale);
            Rectangle hoverArea = stop;
            hoverArea.Inflate(8, 8);
            bool hovered = hoverArea.Contains(mouse);
            sb.Draw(pixel, stop, (selected ? new Color(120, 245, 255) : hovered ? new Color(112, 158, 190) : new Color(50, 85, 112)) * opacity);
            string label = $"{values[i]:0.##}×";
            Vector2 labelSize = FontAssets.MouseText.Value.MeasureString(label) * 0.72f;
            Terraria.Utils.DrawBorderString(sb, label, new Vector2(x - labelSize.X * 0.5f, sliderY + 18),
                selected ? new Color(170, 250, 255) * opacity : Color.LightGray * opacity, 0.72f);
        }

        HandleSlider(scalePlayer, sliderHitbox, panel.X + sliderLeftPadding, sliderWidth);

        string current = Text("ScalePanelCurrent", scalePlayer.SelectedScale, scalePlayer.ActiveScale);
        Terraria.Utils.DrawBorderString(sb, current, new Vector2(panel.X + 18, panel.Bottom - 30),
            new Color(225, 235, 255) * opacity, 0.78f);
    }

    private static void HandleSlider(PlayerScalePlayer scalePlayer, Rectangle hitbox, int left, int width)
    {
        if (Main.mouseLeft && Main.mouseLeftRelease && hitbox.Contains(Main.MouseScreen.ToPoint()))
        {
            dragging = true;
            Main.mouseLeftRelease = false;
        }

        if (!Main.mouseLeft)
            dragging = false;
        if (!dragging)
            return;

        float ratio = MathHelper.Clamp((Main.MouseScreen.X - left) / (float)width, 0f, 1f);
        int index = (int)System.MathF.Round(ratio * (PlayerScalePlayer.AllowedScales.Length - 1));
        float before = scalePlayer.SelectedScale;
        scalePlayer.SelectScale(PlayerScalePlayer.AllowedScales[index]);
        if (before != scalePlayer.SelectedScale)
            SoundEngine.PlaySound(SoundID.MenuTick with { Volume = 0.55f });
    }

    private static int IndexOf(float value)
    {
        for (int i = 0; i < PlayerScalePlayer.AllowedScales.Length; i++)
            if (PlayerScalePlayer.AllowedScales[i] == value)
                return i;
        return 1;
    }

    private static string Text(string key, params object[] args) =>
        Language.GetTextValue($"Mods.MoreAIPlayer.CompanionCommand.{key}", args);
}
