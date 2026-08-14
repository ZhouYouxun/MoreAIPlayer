using System;
using Microsoft.Xna.Framework;
using ReLogic.Graphics;
using Terraria;
using Terraria.Audio;
using Terraria.GameContent;
using Terraria.ID;
using Terraria.Localization;
using MoreAIPlayer.Companion.Building;
using MoreAIPlayer.Companion.Content;

namespace MoreAIPlayer.Companion.UI;

/// <summary>
/// Hold right click with the builder book to select one construction tactic. A left click then
/// switches that tactic on or off. Selection (wheel) is deliberately kept separate from the
/// on/off toggle so cycling tactics never has to also stop and restart the work.
/// </summary>
public static class CompanionBuilderModeWheel
{
    private static readonly CompanionBuildMode[] ModeOrder =
    [
        CompanionBuildMode.SmartBuild,
        CompanionBuildMode.DefensiveBuild,
        CompanionBuildMode.BattlePlatform
    ];

    private const int PanelWidth = 216;
    private const int PanelPadding = 14;
    private const int TitleHeight = 24;
    private const int ButtonWidth = 188;
    private const int ButtonHeight = 30;
    private const int RowGap = 4;
    private const int CaptionHeight = 40;
    private const int MinimumHoldTicks = 10;

    private static bool active;
    private static Vector2 panelTopLeft;
    private static int hovered = -1;
    private static int lastHovered = -1;
    private static bool previousMouseRight;
    private static int heldTicks;

    public static bool Active => active;

    public static void Close()
    {
        active = false;
        hovered = -1;
        lastHovered = -1;
        previousMouseRight = false;
        heldTicks = 0;
    }

    public static void UpdateInput() =>
        CompanionFaultGuard.Run("builder mode menu input", UpdateInputCore);

    private static void UpdateInputCore()
    {
        bool freshRightPress = Main.mouseRight && !previousMouseRight;
        previousMouseRight = Main.mouseRight;

        if (Main.gameMenu || Main.dedServ || Main.netMode != NetmodeID.SinglePlayer)
        {
            Close();
            return;
        }

        Player owner = Main.LocalPlayer;
        bool holdingBook = owner.HeldItem?.ModItem is CompanionBuilderBook;

        if (!active)
        {
            if (freshRightPress && holdingBook && !Main.playerInventory && !owner.mouseInterface &&
                CompanionCommandBook.OwnedActiveCompanions(Main.myPlayer).Count > 0)
            {
                active = true;
                panelTopLeft = ClampToScreen(
                    Main.MouseScreen - new Vector2(PanelWidth * 0.5f, PanelHeight * 0.5f),
                    PanelWidth, PanelHeight);
                hovered = -1;
                lastHovered = -1;
                heldTicks = 0;
                owner.mouseInterface = true;
                SoundEngine.PlaySound(SoundID.MenuOpen with { Volume = 0.5f });
            }
            return;
        }

        if (!holdingBook || owner.dead)
        {
            Close();
            return;
        }

        heldTicks++;
        if (Main.mouseRight)
        {
            hovered = HoveredIndex();
            if (hovered != lastHovered && hovered >= 0)
                SoundEngine.PlaySound(SoundID.MenuTick with { Volume = 0.45f });
            lastHovered = hovered;
            owner.mouseInterface = true;
            return;
        }

        Commit(owner);
        Close();
    }

    private static void Commit(Player owner)
    {
        if (heldTicks < MinimumHoldTicks || hovered < 0 || hovered >= ModeOrder.Length)
        {
            SoundEngine.PlaySound(SoundID.MenuClose with { Volume = 0.4f });
            return;
        }

        CompanionBuildMode chosen = ModeOrder[hovered];
        CompanionPlayer data = owner.GetModPlayer<CompanionPlayer>();
        foreach (CompanionInstance companion in CompanionCommandBook.OwnedActiveCompanions(owner.whoAmI))
        {
            if (companion.TryGetContext(out _, out CompanionProfile profile))
                profile.WheelCommandsActive = false;
        }
        data.BuildMode = chosen;
        CompanionBuildingSystem.ResetSmartObservation(owner);
        string state = Language.GetTextValue(data.BuildTacticActive
            ? "Mods.MoreAIPlayer.CompanionCommand.BuilderStateOn"
            : "Mods.MoreAIPlayer.CompanionCommand.BuilderStateOff");
        Main.NewText(Language.GetTextValue(
            "Mods.MoreAIPlayer.CompanionCommand.BuilderModeChanged",
            CompanionBuilderBook.ModeName(chosen), state), 120, 255, 170);
        SoundEngine.PlaySound(SoundID.Item37 with { Volume = 0.55f });
    }

    private static int HoveredIndex()
    {
        Point mouse = Main.MouseScreen.ToPoint();
        for (int i = 0; i < ModeOrder.Length; i++)
        {
            if (GetModeBox(i).Contains(mouse))
                return i;
        }
        return -1;
    }

    private static Rectangle GetModeBox(int index) => new(
        (int)(panelTopLeft.X + (PanelWidth - ButtonWidth) * 0.5f),
        (int)(panelTopLeft.Y + PanelPadding + TitleHeight + index * (ButtonHeight + RowGap)),
        ButtonWidth,
        ButtonHeight);

    private static int PanelHeight => PanelPadding * 2 + TitleHeight +
        ModeOrder.Length * ButtonHeight + (ModeOrder.Length - 1) * RowGap + CaptionHeight;

    private static Rectangle PanelArea =>
        new((int)panelTopLeft.X, (int)panelTopLeft.Y, PanelWidth, PanelHeight);

    public static void Draw() =>
        CompanionFaultGuard.RunDraw("builder mode menu", DrawCore);

    private static void DrawCore()
    {
        if (!active)
            return;

        CompanionPlayer data = Main.LocalPlayer.GetModPlayer<CompanionPlayer>();
        DynamicSpriteFont font = FontAssets.MouseText.Value;
        DrawRectangle(PanelArea, new Color(14, 22, 25, 239));
        DrawBorder(PanelArea, new Color(90, 137, 119), 2);
        DrawBorder(new Rectangle(PanelArea.X + 4, PanelArea.Y + 4,
            PanelArea.Width - 8, PanelArea.Height - 8), new Color(45, 73, 65, 220), 1);

        string title = T("BuilderWheelTitle");
        Vector2 titleSize = font.MeasureString(title) * 0.72f;
        Utils.DrawBorderString(Main.spriteBatch, title,
            new Vector2(PanelArea.Center.X - titleSize.X * 0.5f, panelTopLeft.Y + PanelPadding),
            new Color(150, 235, 190), 0.72f);

        for (int i = 0; i < ModeOrder.Length; i++)
        {
            CompanionBuildMode mode = ModeOrder[i];
            bool isHovered = i == hovered;
            bool isCurrent = data.BuildMode == mode;
            Rectangle box = GetModeBox(i);
            Color tint = ModeTint(mode);
            Color back = new(23, 34, 37, 238);
            Color border = Color.Lerp(new Color(78, 105, 101), tint, isCurrent ? 0.82f : 0.38f);
            if (isHovered)
            {
                back = Color.Lerp(back, tint, 0.32f);
                border = Color.Lerp(border, Color.White, 0.42f);
            }

            DrawRectangle(box, back);
            DrawBorder(box, border, isCurrent ? 3 : 2);
            string label = CompanionBuilderBook.ModeName(mode);
            if (isCurrent && data.BuildTacticActive)
                label = "● " + label;
            Vector2 size = font.MeasureString(label) * 0.86f;
            Utils.DrawBorderString(Main.spriteBatch, label,
                new Vector2(box.Center.X - size.X * 0.5f, box.Center.Y - size.Y * 0.5f),
                isHovered ? Color.White : Color.Lerp(Color.White, tint, 0.52f), 0.86f);
        }

        string caption = T(data.BuildTacticActive ? "BuilderWheelStateOn" : "BuilderWheelStateOff");
        Vector2 captionSize = font.MeasureString(caption) * 0.66f;
        Utils.DrawBorderString(Main.spriteBatch, caption,
            new Vector2(PanelArea.Center.X - captionSize.X * 0.5f,
                GetModeBox(ModeOrder.Length - 1).Bottom + 12f),
            new Color(210, 220, 216), 0.66f);
    }

    private static Color ModeTint(CompanionBuildMode mode) => mode switch
    {
        CompanionBuildMode.SmartBuild => new Color(105, 230, 155),
        CompanionBuildMode.DefensiveBuild => new Color(245, 190, 105),
        CompanionBuildMode.BattlePlatform => new Color(245, 125, 105),
        _ => Color.White
    };

    private static string T(string key) =>
        Language.GetTextValue($"Mods.MoreAIPlayer.CompanionCommand.{key}");

    private static Vector2 ClampToScreen(Vector2 topLeft, int width, int height)
    {
        const float margin = 10f;
        return new Vector2(
            MathHelper.Clamp(topLeft.X, margin, Math.Max(margin, Main.screenWidth - width - margin)),
            MathHelper.Clamp(topLeft.Y, margin, Math.Max(margin, Main.screenHeight - height - margin)));
    }

    private static void DrawRectangle(Rectangle area, Color color) =>
        Main.spriteBatch.Draw(TextureAssets.MagicPixel.Value, area, color);

    private static void DrawBorder(Rectangle area, Color color, int thickness)
    {
        DrawRectangle(new Rectangle(area.X, area.Y, area.Width, thickness), color);
        DrawRectangle(new Rectangle(area.X, area.Bottom - thickness, area.Width, thickness), color);
        DrawRectangle(new Rectangle(area.X, area.Y, thickness, area.Height), color);
        DrawRectangle(new Rectangle(area.Right - thickness, area.Y, thickness, area.Height), color);
    }
}
