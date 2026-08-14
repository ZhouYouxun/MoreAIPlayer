using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using MoreAIPlayer.Companion.Commands;
using MoreAIPlayer.Companion.Content;
using Terraria;
using Terraria.Audio;
using Terraria.GameContent;
using Terraria.ID;
using Terraria.Localization;

namespace MoreAIPlayer.Companion.UI;

/// <summary>
/// A click-to-open, player-centred command ring. The ring is assembled from narrow radial strips
/// rather than a square texture or a handful of divider lines: 24 strips per sector gives a real
/// circular silhouette while keeping the draw count fixed and small on every resolution.
/// </summary>
public static class PhantomCommandWheel
{
    private const float DesignedOuterRadius = 15f * 16f;
    private const float DesignedInnerRadius = 6.2f * 16f;
    private const int SamplesPerSector = 24;
    private const int RecallHoldTicks = 75;
    private const int TopSectorCount = 8;

    private static bool active;
    private static bool workLayer;
    private static int hovered = -1;
    private static int lastHovered = -1;
    private static bool previousLeft;
    private static bool previousRight;
    private static int recallTicks;
    private static int targetSlot = -1;
    private static int targetProfileId = -1;
    private static float openProgress;
    private static readonly float[] HoverProgress = new float[TopSectorCount];

    public static bool Active => active;

    public static void Close()
    {
        active = false;
        workLayer = false;
        hovered = -1;
        lastHovered = -1;
        recallTicks = 0;
        targetSlot = -1;
        targetProfileId = -1;
        openProgress = 0f;
        Array.Clear(HoverProgress, 0, HoverProgress.Length);
        previousLeft = Main.mouseLeft;
        previousRight = Main.mouseRight;
    }

    public static void UpdateInput() =>
        CompanionFaultGuard.Run("phantom command wheel input", UpdateInputCore);

    private static void UpdateInputCore()
    {
        bool freshLeft = Main.mouseLeft && !previousLeft;
        bool freshRight = Main.mouseRight && !previousRight;
        previousLeft = Main.mouseLeft;
        previousRight = Main.mouseRight;

        if (Main.gameMenu || Main.dedServ || Main.netMode != NetmodeID.SinglePlayer)
        {
            Close();
            return;
        }

        Player owner = Main.LocalPlayer;
        if (MoreAIPlayer.CompanionWheelKeybind?.JustPressed == true)
        {
            if (active)
            {
                SoundEngine.PlaySound(SoundID.MenuClose with { Volume = 0.5f });
                Close();
            }
            else
            {
                TryOpen(owner);
            }
            return;
        }

        if (!active)
            return;

        CompanionInstance target = ResolveTarget(owner);
        if (target is null || owner.dead || Main.playerInventory || Main.mapFullscreen || Main.hideUI)
        {
            Close();
            return;
        }

        owner.mouseInterface = true;
        Main.blockMouse = true;
        owner.controlUseItem = false;
        owner.controlUseTile = false;
        openProgress = MathHelper.Clamp(openProgress + 0.14f, 0f, 1f);

        int sectorCount = workLayer ? CompanionCommandRules.WorkOrder.Length : TopSectorCount;
        hovered = HoveredSector(sectorCount);
        for (int i = 0; i < HoverProgress.Length; i++)
        {
            float targetHover = i == hovered && i < sectorCount ? 1f : 0f;
            HoverProgress[i] = MathHelper.Lerp(HoverProgress[i], targetHover, targetHover > 0f ? 0.34f : 0.22f);
        }
        if (hovered != lastHovered && hovered >= 0)
            SoundEngine.PlaySound(SoundID.MenuTick with { Volume = 0.32f, Pitch = 0.15f });
        lastHovered = hovered;

        if (freshRight)
        {
            SoundEngine.PlaySound(SoundID.MenuClose with { Volume = 0.45f });
            Close();
            return;
        }

        if (!workLayer && hovered == 0 && Main.mouseLeft)
        {
            recallTicks++;
            if (recallTicks >= RecallHoldTicks)
            {
                CompanionEntitySystem.Dismiss(target.Slot);
                SoundEngine.PlaySound(SoundID.Item6 with { Volume = 0.7f });
                Close();
            }
            return;
        }

        if (recallTicks > 0)
            recallTicks = 0;

        if (!freshLeft)
            return;

        if (hovered < 0)
        {
            Close();
            return;
        }

        if (workLayer)
        {
            SelectWorkMode(owner, target, hovered);
            workLayer = false;
            hovered = -1;
            lastHovered = -1;
            SoundEngine.PlaySound(SoundID.Item37 with { Volume = 0.55f });
            return;
        }

        HandleTopSector(owner, target, hovered);
    }

    private static void TryOpen(Player owner)
    {
        if (Main.playerInventory || Main.mapFullscreen)
            return;
        if (!PhantomStaff.TryFindFirstActive(owner, out _, out CompanionInstance target))
        {
            Main.NewText(T("WheelNoActiveStaff"), 255, 170, 120);
            SoundEngine.PlaySound(SoundID.MenuClose with { Volume = 0.35f });
            return;
        }

        active = true;
        workLayer = false;
        targetSlot = target.Slot;
        targetProfileId = target.ProfileId;
        hovered = -1;
        lastHovered = -1;
        recallTicks = 0;
        openProgress = 0.18f;
        Array.Clear(HoverProgress, 0, HoverProgress.Length);
        previousLeft = Main.mouseLeft;
        previousRight = Main.mouseRight;
        SoundEngine.PlaySound(SoundID.MenuOpen with { Volume = 0.6f });
    }

    private static CompanionInstance ResolveTarget(Player owner)
    {
        CompanionInstance bySlot = CompanionEntitySystem.GetBySlot(targetSlot);
        if (bySlot is not null && bySlot.OwnerWhoAmI == owner.whoAmI && bySlot.ProfileId == targetProfileId)
            return bySlot;
        return CompanionEntitySystem.FindByProfile(owner.whoAmI, targetProfileId);
    }

    private static void HandleTopSector(Player owner, CompanionInstance target, int sector)
    {
        if (!target.TryGetContext(out _, out CompanionProfile profile))
        {
            Close();
            return;
        }

        switch (sector)
        {
            case 0: // a short click never recalls
                Close();
                return;
            case 1:
                Close();
                CompanionUISystem.Open(target.Slot);
                return;
            case 2: // work mode
                workLayer = true;
                hovered = -1;
                lastHovered = -1;
                Array.Clear(HoverProgress, 0, HoverProgress.Length);
                return;
            case >= 3 and <= 7:
                if (CompanionCommandRules.ApplyCommand(profile, sector - 3, owner, target))
                {
                    string command = CompanionCommandRules.CommandName(profile, sector - 3);
                    Main.NewText(T("WheelCommandChanged", profile.Name, command),
                        CompanionCommandRules.WorkTint(profile.WorkMode));
                }
                Close();
                return;
        }
    }

    private static void SelectWorkMode(Player owner, CompanionInstance target, int sector)
    {
        if (sector < 0 || sector >= CompanionCommandRules.WorkOrder.Length ||
            !target.TryGetContext(out _, out CompanionProfile profile))
            return;
        CompanionWorkMode mode = CompanionCommandRules.WorkOrder[sector];
        CompanionCommandRules.SelectWorkMode(profile, mode, owner, target);
        Main.NewText(T("WheelWorkChanged", profile.Name, CompanionCommandRules.WorkName(mode)),
            CompanionCommandRules.WorkTint(mode));
    }

    private static int HoveredSector(int count)
    {
        Vector2 delta = Main.MouseScreen - WheelCenter();
        float distance = delta.Length();
        float outer = OuterRadius();
        float inner = InnerRadius();
        if (distance < inner || distance > outer)
            return -1;

        // Rotate so sector zero is centred at twelve o'clock, then walk clockwise.
        float angle = MathF.Atan2(delta.Y, delta.X) + MathHelper.PiOver2;
        if (angle < 0f)
            angle += MathHelper.TwoPi;
        float sectorAngle = MathHelper.TwoPi / count;
        return (int)MathF.Floor((angle + sectorAngle * 0.5f) / sectorAngle) % count;
    }

    public static void Draw() => CompanionFaultGuard.RunDraw("phantom command wheel", DrawCore);

    private static void DrawCore()
    {
        if (!active)
            return;
        Player owner = Main.LocalPlayer;
        CompanionInstance target = ResolveTarget(owner);
        if (target is null || !target.TryGetContext(out _, out CompanionProfile profile))
            return;

        int count = workLayer ? CompanionCommandRules.WorkOrder.Length : TopSectorCount;
        Vector2 center = WheelCenter();
        float reveal = MathHelper.SmoothStep(0f, 1f, openProgress);
        float revealScale = MathHelper.Lerp(0.86f, 1f, reveal);
        float opacity = MathHelper.Lerp(0.25f, 1f, reveal);
        float inner = InnerRadius() * revealScale;
        float outer = OuterRadius() * revealScale;
        float sectorAngle = MathHelper.TwoPi / count;

        DrawCircle(center, outer + 7f, new Color(45, 145, 190, 75) * opacity, 5f, 192);

        for (int sector = 0; sector < count; sector++)
        {
            Color tint = SectorTint(profile, sector, workLayer);
            float hover = HoverProgress[sector];
            Color fill = Color.Lerp(new Color(13, 20, 34, 215), tint, 0.20f + hover * 0.40f) * opacity;
            float sectorInner = inner - hover * 4f;
            float sectorOuter = outer + hover * 8f;
            const float angularGap = 0.012f;
            DrawFilledSector(center, sectorInner, sectorOuter,
                -MathHelper.PiOver2 + sector * sectorAngle - sectorAngle * 0.5f + angularGap,
                sectorAngle - angularGap * 2f, fill);
            if (hover > 0.02f)
            {
                DrawFilledSector(center, sectorOuter - 5f, sectorOuter,
                    -MathHelper.PiOver2 + sector * sectorAngle - sectorAngle * 0.5f + angularGap,
                    sectorAngle - angularGap * 2f, Color.Lerp(tint, Color.White, 0.28f) * (hover * opacity));
            }
        }

        DrawCircle(center, outer, new Color(155, 220, 245, 230) * opacity, 3f, 192);
        DrawCircle(center, inner, new Color(85, 135, 175, 235) * opacity, 3f, 128);
        for (int i = 0; i < count; i++)
        {
            float angle = -MathHelper.PiOver2 + i * sectorAngle - sectorAngle * 0.5f;
            Vector2 direction = angle.ToRotationVector2();
            DrawLine(center + direction * inner, center + direction * outer,
                new Color(92, 126, 160, 170) * opacity, 1.5f);

            float centerAngle = -MathHelper.PiOver2 + i * sectorAngle;
            Vector2 tickDirection = centerAngle.ToRotationVector2();
            DrawLine(center + tickDirection * (outer + 3f), center + tickDirection * (outer + 10f),
                SectorTint(profile, i, workLayer) * opacity, 2.5f);
        }

        for (int sector = 0; sector < count; sector++)
        {
            float angle = -MathHelper.PiOver2 + sector * sectorAngle;
            float hover = HoverProgress[sector];
            Vector2 labelPosition = center + angle.ToRotationVector2() *
                ((inner + outer) * 0.5f + hover * 4f);
            string label = SectorLabel(profile, sector, workLayer);
            float maxWidth = MathF.Max(72f, 2f * ((inner + outer) * 0.5f) * MathF.Sin(sectorAngle * 0.37f));
            DrawCenteredFit(label, labelPosition,
                Color.Lerp(Color.White, SectorTint(profile, sector, workLayer), 0.42f - hover * 0.30f) * opacity,
                0.84f + hover * 0.05f, maxWidth);
        }

        DrawFilledSector(center, 0f, inner - 5f, 0f, MathHelper.TwoPi,
            new Color(8, 14, 25, 246) * opacity, 144);
        DrawCircle(center, inner - 5f, CompanionCommandRules.WorkTint(profile.WorkMode) * (0.55f * opacity), 2.5f, 128);
        Texture2D staffTexture = TextureAssets.Item[ItemID.MagicMissile].Value;
        Main.spriteBatch.Draw(staffTexture, center + new Vector2(0f, -39f), null, Color.White * opacity,
            0f, staffTexture.Size() * 0.5f, 0.82f, SpriteEffects.None, 0f);
        string title = workLayer ? T("WheelChooseWork") : profile.Name;
        string subtitle = workLayer
            ? T("WheelChooseWorkHint")
            : T("WheelCurrentWork", CompanionCommandRules.WorkName(profile.WorkMode));
        DrawCenteredFit(title, center + new Vector2(0f, -5f), Color.White * opacity, 0.9f, (inner - 18f) * 1.7f);
        DrawCenteredFit(subtitle, center + new Vector2(0f, 20f), new Color(145, 220, 225) * opacity,
            0.67f, (inner - 16f) * 1.72f);

        if (!workLayer && hovered == 0)
        {
            float progress = MathHelper.Clamp(recallTicks / (float)RecallHoldTicks, 0f, 1f);
            Rectangle back = new((int)(center.X - 58f), (int)(center.Y + 36f), 116, 8);
            Rectangle fill = new(back.X + 1, back.Y + 1, (int)((back.Width - 2) * progress), back.Height - 2);
            Main.spriteBatch.Draw(TextureAssets.MagicPixel.Value, back, new Color(8, 12, 20, 230) * opacity);
            Main.spriteBatch.Draw(TextureAssets.MagicPixel.Value, fill, new Color(255, 105, 105, 240) * opacity);
            DrawCenteredFit(T("WheelHoldRecall"), center + new Vector2(0f, 51f),
                new Color(255, 175, 165) * opacity, 0.62f, inner * 1.55f);
        }
    }

    private static string SectorLabel(CompanionProfile profile, int sector, bool lowerLayer)
    {
        if (lowerLayer)
            return CompanionCommandRules.WorkName(CompanionCommandRules.WorkOrder[sector]);
        return sector switch
        {
            0 => T("WheelRecall"),
            1 => T("WheelBackpack"),
            2 => T("WheelSwitchWork", CompanionCommandRules.WorkName(profile.WorkMode)),
            >= 3 and <= 7 => CompanionCommandRules.CommandName(profile, sector - 3),
            _ => string.Empty
        };
    }

    private static Color SectorTint(CompanionProfile profile, int sector, bool lowerLayer)
    {
        if (lowerLayer)
            return CompanionCommandRules.WorkTint(CompanionCommandRules.WorkOrder[sector]);
        return sector switch
        {
            0 => new Color(245, 100, 100),
            1 => new Color(120, 205, 245),
            2 => CompanionCommandRules.WorkTint(profile.WorkMode),
            _ => new Color(175, 155, 235)
        };
    }

    private static void DrawFilledSector(Vector2 center, float inner, float outer,
        float startAngle, float angleSpan, Color color, int samples = SamplesPerSector)
    {
        float step = angleSpan / samples;
        float width = outer * step + 1.5f;
        for (int i = 0; i < samples; i++)
        {
            float angle = startAngle + (i + 0.5f) * step;
            Vector2 direction = angle.ToRotationVector2();
            DrawLine(center + direction * inner, center + direction * outer, color, width);
        }
    }

    private static void DrawCircle(Vector2 center, float radius, Color color, float width, int samples)
    {
        Vector2 previous = center + new Vector2(radius, 0f);
        for (int i = 1; i <= samples; i++)
        {
            Vector2 next = center + (MathHelper.TwoPi * i / samples).ToRotationVector2() * radius;
            DrawLine(previous, next, color, width);
            previous = next;
        }
    }

    private static void DrawLine(Vector2 start, Vector2 end, Color color, float width)
    {
        Vector2 delta = end - start;
        float length = delta.Length();
        if (length <= 0.01f || width <= 0f)
            return;

        // MagicPixel is not guaranteed to have a native size of exactly 1x1. Scaling it by the
        // desired pixel length therefore multiplies the result by the texture's own dimensions,
        // which was what produced full-screen rays. A destination rectangle gives an absolute
        // on-screen length and thickness regardless of the backing texture size.
        Rectangle destination = new(
            (int)MathF.Round(start.X),
            (int)MathF.Round(start.Y),
            Math.Max(1, (int)MathF.Ceiling(length)),
            Math.Max(1, (int)MathF.Ceiling(width)));
        Main.spriteBatch.Draw(TextureAssets.MagicPixel.Value, destination, null, color,
            delta.ToRotation(), Vector2.Zero, SpriteEffects.None, 0f);
    }

    private static void DrawCenteredFit(string text, Vector2 center, Color color, float desiredScale, float maxWidth)
    {
        Vector2 rawSize = FontAssets.MouseText.Value.MeasureString(text);
        float scale = MathF.Min(desiredScale, maxWidth / MathF.Max(1f, rawSize.X));
        Vector2 size = rawSize * scale;
        Utils.DrawBorderString(Main.spriteBatch, text, center - size * 0.5f, color, scale);
    }

    private static Vector2 WheelCenter()
    {
        return Main.LocalPlayer.Center - Main.screenPosition;
    }

    private static float RadiusScale() => MathHelper.Clamp(
        MathF.Min(Main.screenWidth, Main.screenHeight) / (DesignedOuterRadius * 2f + 36f), 0.62f, 1f);
    private static float OuterRadius() => DesignedOuterRadius * RadiusScale();
    private static float InnerRadius() => DesignedInnerRadius * RadiusScale();
    private static string T(string key, params object[] args) =>
        Language.GetTextValue($"Mods.MoreAIPlayer.CompanionCommand.{key}", args);
}
