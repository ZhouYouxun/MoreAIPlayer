using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.Audio;
using Terraria.GameContent;
using Terraria.ID;
using Terraria.Localization;
using MoreAIPlayer.Companion.Content;

namespace MoreAIPlayer.Companion.UI;

/// <summary>
/// Hold right click with the command book out to open a radial mode picker centred on the
/// cursor; release to commit whichever sector the cursor is pointing at.
///
/// This replaced a right-click cycle. With six modes a cycle meant up to five clicks to reach
/// the one you wanted, and no way to see what was available without walking the whole ring.
///
/// Drawn directly into an interface layer rather than as a UIState: it is a transient overlay
/// anchored to wherever the cursor happened to be, so it has no layout to speak of, and it
/// needs to read raw mouse state on the same frame it draws.
/// </summary>
public static class CompanionModeWheel
{
    private const float Radius = 78f;
    private const float DeadZone = 18f;
    private const int SlotWidth = 92;
    private const int SlotHeight = 30;

    // A right click shorter than this is a click, not a deliberate wheel gesture, and must not
    // change anything: switching mode silently is how a companion ends up in a mode its owner
    // never chose, which then looks exactly like the AI being broken.
    private const int MinimumHoldTicks = 12;

    private static bool active;
    private static Vector2 center;
    private static int hovered = -1;
    private static int lastHovered = -1;
    private static bool previousMouseRight;
    private static int heldTicks;

    /// <summary>True while the wheel owns the mouse. Other systems check this to stay out of the
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

    /// <summary>Input half, driven from ModSystem.UpdateUI so it runs once per frame.</summary>
    public static void UpdateInput()
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
            // wandering onto the book must not spring the wheel open already "held".
            if (freshRightPress && holdingBook && !Main.playerInventory && !owner.mouseInterface &&
                CompanionCommandBook.OwnedActiveCompanions(Main.myPlayer).Count > 0)
            {
                active = true;
                center = Main.MouseScreen;
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
        // Too brief to be a wheel gesture, or released in the dead zone: cancel, change nothing.
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
        // Also written to chat: the floating text is gone in a second, and a mode change that
        // leaves no record is one the player cannot later rule out as the cause of odd behaviour.
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
        Vector2 offset = Main.MouseScreen - center;
        if (offset.LengthSquared() < DeadZone * DeadZone)
            return -1;

        int count = CompanionModeRules.WheelOrder.Length;
        float sector = MathHelper.TwoPi / count;

        // Sector 0 points straight up; +PiOver2 rotates the maths angle into that frame, and the
        // half-sector bias makes each sector centred on its label rather than starting at it.
        float angle = offset.ToRotation() + MathHelper.PiOver2;
        angle = (angle % MathHelper.TwoPi + MathHelper.TwoPi) % MathHelper.TwoPi;
        return (int)((angle + sector * 0.5f) / sector) % count;
    }

    private static Vector2 SectorDirection(int index)
    {
        float sector = MathHelper.TwoPi / CompanionModeRules.WheelOrder.Length;
        return (-MathHelper.PiOver2 + index * sector).ToRotationVector2();
    }

    /// <summary>Draw half, called from the interface layer.</summary>
    public static void Draw()
    {
        if (!active)
            return;

        // Availability is read off the first companion: a squad shares one mode anyway, and the
        // wheel would be unreadable if every member greyed out different sectors.
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

        for (int i = 0; i < CompanionModeRules.WheelOrder.Length; i++)
        {
            CompanionMode mode = CompanionModeRules.WheelOrder[i];
            bool available = reference is null || CompanionModeRules.IsAvailable(mode, reference);
            bool isHovered = i == hovered;
            bool isCurrent = reference is not null && reference.Mode == mode;

            Vector2 slotCenter = center + SectorDirection(i) * Radius;
            Rectangle box = Utils.CenteredRectangle(slotCenter, new Vector2(SlotWidth, SlotHeight));

            Color tint = CompanionModeRules.Tint(mode);
            Color back = available ? new Color(20, 24, 40, 235) : new Color(26, 26, 30, 235);
            Color border = available ? Color.Lerp(new Color(80, 90, 120), tint, isCurrent ? 0.75f : 0.35f)
                                     : new Color(70, 70, 78);
            if (isHovered)
            {
                back = Color.Lerp(back, tint, available ? 0.30f : 0.10f);
                border = Color.Lerp(border, Color.White, 0.45f);
            }

            DrawRectangle(box, back);
            DrawBorder(box, border, 2);

            Color textColor = available ? (isHovered ? Color.White : Color.Lerp(Color.White, tint, 0.55f))
                                        : new Color(120, 120, 128);
            string label = CompanionModeRules.Name(mode);
            Vector2 labelSize = font.MeasureString(label) * 0.9f;
            Utils.DrawBorderString(Main.spriteBatch, label,
                slotCenter - labelSize * 0.5f, textColor, 0.9f);
        }

        // Hovered sector's description (or why it is unavailable) under the ring.
        if (hovered >= 0 && hovered < CompanionModeRules.WheelOrder.Length)
        {
            CompanionMode mode = CompanionModeRules.WheelOrder[hovered];
            bool available = reference is null || CompanionModeRules.IsAvailable(mode, reference);
            string caption = available
                ? CompanionModeRules.Description(mode)
                : CompanionModeRules.RequirementText(mode);
            Color captionColor = available ? new Color(215, 220, 235) : new Color(255, 160, 120);

            Vector2 size = font.MeasureString(caption) * 0.85f;
            Vector2 spot = center + new Vector2(-size.X * 0.5f, Radius + SlotHeight);
            Utils.DrawBorderString(Main.spriteBatch, caption, spot, captionColor, 0.85f);
        }
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
