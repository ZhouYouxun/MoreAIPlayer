using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using ReLogic.Graphics;
using Terraria;
using Terraria.Audio;
using Terraria.GameContent;
using Terraria.ID;
using Terraria.Localization;
using MoreAIPlayer.Companion.Content;

namespace MoreAIPlayer.Companion.UI;

/// <summary>
/// Hold right click with the command book out to open a compact, vertical tactic menu; release
/// over a row to commit it. The old radial layout made the seven labels collide with the world
/// and offered no visual distinction between combat and utility work.
/// </summary>
public static class CompanionModeWheel
{
    private const int PanelWidth = 196;
    private const int PanelPadding = 14;
    private const int GroupLabelHeight = 18;
    private const int ButtonWidth = 168;
    private const int ButtonHeight = 26;
    private const int RowGap = 3;
    private const int GroupGap = 10;
    private const int CaptionHeight = 36;
    private const int CombatCount = 4;

    // A right click shorter than this is a click, not a deliberate menu gesture, and must not
    // change anything: switching mode silently is how a companion ends up in a mode its owner
    // never chose, which then looks exactly like the AI being broken.
    private const int MinimumHoldTicks = 12;

    private static bool active;
    private static Vector2 panelTopLeft;
    private static int hovered = -1;
    private static int lastHovered = -1;
    private static bool previousMouseRight;
    private static int heldTicks;

    /// <summary>True while the menu owns the mouse. Other systems check this to stay out of the
    /// way — in particular the double-right-click rescue gesture must not fire through it.</summary>
    public static bool Active => active;

    public static void Close()
    {
        active = false;
        hovered = -1;
        lastHovered = -1;
        previousMouseRight = false;
        heldTicks = 0;
    }

    /// <summary>Input half, driven from ModSystem.UpdateUI so it runs once per frame. Fenced so a
    /// fault in the menu's input handling can never propagate into the shared UpdateUI hook.</summary>
    public static void UpdateInput() =>
        CompanionFaultGuard.Run("companion mode menu input", UpdateInputCore);

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
        bool holdingBook = owner.HeldItem?.ModItem is CompanionCommandBook;

        if (!active)
        {
            // Opening requires a fresh press: releasing right click somewhere else and then
            // wandering onto the book must not spring the menu open already "held".
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

            // Keeps the held item from being used and the world from being clicked through.
            Main.blockMouse = true;
            owner.mouseInterface = true;
            return;
        }

        Commit();
        Close();
    }

    private static void Commit()
    {
        // Too brief to be a menu gesture, or released outside a row: cancel, change nothing.
        if (heldTicks < MinimumHoldTicks || hovered < 0 || hovered >= CompanionModeRules.WheelOrder.Length)
        {
            SoundEngine.PlaySound(SoundID.MenuClose with { Volume = 0.4f });
            return;
        }

        CompanionMode chosen = CompanionModeRules.WheelOrder[hovered];
        List<CompanionInstance> squad = CompanionCommandBook.OwnedActiveCompanions(Main.myPlayer);
        if (squad.Count == 0)
            return;

        // A mode whose tool slot is empty is refused outright rather than silently selected and
        // then ignored by the AI, so the player finds out immediately and knows what to equip.
        foreach (CompanionInstance instance in squad)
        {
            if (!instance.TryGetContext(out _, out CompanionProfile profile))
                continue;

            if (!CompanionModeRules.IsAvailable(chosen, profile))
            {
                Main.NewText(
                    T("ModeUnavailable",
                        profile.Name,
                        CompanionModeRules.Name(chosen),
                        CompanionModeRules.RequirementText(chosen)),
                    255, 160, 120);
                continue;
            }

            profile.Mode = chosen;
        }

        Player owner = Main.LocalPlayer;
        Rectangle overHead = new((int)owner.position.X, (int)owner.position.Y - 36, owner.width, owner.height);
        CombatText.NewText(overHead, CompanionModeRules.Tint(chosen), CompanionModeRules.Name(chosen));
        Color tint = CompanionModeRules.Tint(chosen);
        Main.NewText(T("ModeChanged",
            CompanionModeRules.Name(chosen),
            CompanionModeRules.Description(chosen)), tint.R, tint.G, tint.B);
        SoundEngine.PlaySound(SoundID.Item37 with { Volume = 0.6f });
    }

    private static string T(string key, params object[] args) =>
        Language.GetTextValue($"Mods.MoreAIPlayer.CompanionUI.{key}", args);

    private static int HoveredIndex()
    {
        Point mouse = Main.MouseScreen.ToPoint();
        for (int i = 0; i < CompanionModeRules.WheelOrder.Length; i++)
        {
            if (GetModeBox(i).Contains(mouse))
                return i;
        }
        return -1;
    }

    private static Rectangle GetModeBox(int index)
    {
        float y = panelTopLeft.Y + PanelPadding + GroupLabelHeight + index * (ButtonHeight + RowGap);
        if (index >= CombatCount)
            y += GroupGap + GroupLabelHeight;
        return new Rectangle(
            (int)(panelTopLeft.X + (PanelWidth - ButtonWidth) * 0.5f),
            (int)y,
            ButtonWidth,
            ButtonHeight);
    }

    private static int PanelHeight => PanelPadding * 2 +
        GroupLabelHeight + CombatCount * ButtonHeight + (CombatCount - 1) * RowGap +
        GroupGap + GroupLabelHeight +
        (CompanionModeRules.WheelOrder.Length - CombatCount) * ButtonHeight +
        (CompanionModeRules.WheelOrder.Length - CombatCount - 1) * RowGap + CaptionHeight;

    private static Rectangle PanelArea => new((int)panelTopLeft.X, (int)panelTopLeft.Y, PanelWidth, PanelHeight);

    /// <summary>Draw half, called from the interface layer. Fenced so a fault here degrades to a
    /// missing menu for the frame instead of aborting the whole HUD interface layer.</summary>
    public static void Draw() =>
        CompanionFaultGuard.RunDraw("companion mode menu", DrawCore);

    private static void DrawCore()
    {
        if (!active)
            return;

        List<CompanionInstance> squad = CompanionCommandBook.OwnedActiveCompanions(Main.myPlayer);
        CompanionProfile reference = null;
        foreach (CompanionInstance instance in squad)
        {
            if (instance.TryGetContext(out _, out CompanionProfile profile))
            {
                reference = profile;
                break;
            }
        }

        var font = FontAssets.MouseText.Value;
        DrawRectangle(PanelArea, new Color(14, 19, 31, 239));
        DrawBorder(PanelArea, new Color(80, 105, 142), 2);
        DrawBorder(new Rectangle(PanelArea.X + 4, PanelArea.Y + 4, PanelArea.Width - 8, PanelArea.Height - 8),
            new Color(40, 55, 79, 220), 1);

        DrawGroupLabel(font, T("ModeCombatGroup"),
            panelTopLeft.Y + PanelPadding, new Color(255, 170, 145));
        float utilityLabelY = GetModeBox(CombatCount).Y - GroupGap - GroupLabelHeight;
        DrawGroupLabel(font, T("ModeUtilityGroup"), utilityLabelY, new Color(135, 220, 190));

        for (int i = 0; i < CompanionModeRules.WheelOrder.Length; i++)
        {
            CompanionMode mode = CompanionModeRules.WheelOrder[i];
            bool available = reference is null || CompanionModeRules.IsAvailable(mode, reference);
            bool isHovered = i == hovered;
            bool isCurrent = reference is not null && reference.Mode == mode;
            Rectangle box = GetModeBox(i);

            Color tint = CompanionModeRules.Tint(mode);
            Color back = available ? new Color(22, 29, 46, 238) : new Color(31, 31, 36, 235);
            Color border = available ? Color.Lerp(new Color(74, 92, 123), tint, isCurrent ? 0.82f : 0.38f)
                                     : new Color(70, 70, 78);
            if (isHovered)
            {
                back = Color.Lerp(back, tint, available ? 0.34f : 0.10f);
                border = Color.Lerp(border, Color.White, 0.45f);
            }

            DrawRectangle(box, back);
            DrawBorder(box, border, isCurrent ? 3 : 2);

            Color textColor = available ? (isHovered ? Color.White : Color.Lerp(Color.White, tint, 0.55f))
                                        : new Color(120, 120, 128);
            string label = CompanionModeRules.Name(mode);
            Vector2 labelSize = font.MeasureString(label) * 0.84f;
            Utils.DrawBorderString(Main.spriteBatch, label,
                new Vector2(box.Center.X - labelSize.X * 0.5f, box.Center.Y - labelSize.Y * 0.5f), textColor, 0.84f);
        }

        if (hovered >= 0 && hovered < CompanionModeRules.WheelOrder.Length)
        {
            CompanionMode mode = CompanionModeRules.WheelOrder[hovered];
            bool available = reference is null || CompanionModeRules.IsAvailable(mode, reference);
            string caption = available ? CompanionModeRules.Description(mode) : CompanionModeRules.RequirementText(mode);
            Color captionColor = available ? new Color(215, 220, 235) : new Color(255, 160, 120);
            Vector2 size = font.MeasureString(caption) * 0.68f;
            Vector2 spot = new(
                PanelArea.Center.X - size.X * 0.5f,
                GetModeBox(CompanionModeRules.WheelOrder.Length - 1).Bottom + 11f);
            Utils.DrawBorderString(Main.spriteBatch, caption, spot, captionColor, 0.68f);
        }
    }

    private static void DrawGroupLabel(DynamicSpriteFont font, string label, float y, Color color)
    {
        Vector2 size = font.MeasureString(label) * 0.68f;
        Utils.DrawBorderString(Main.spriteBatch, label,
            new Vector2(PanelArea.Center.X - size.X * 0.5f, y), color, 0.68f);
    }

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
