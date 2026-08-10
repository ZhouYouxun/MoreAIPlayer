using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.Audio;
using Terraria.GameContent;
using Terraria.ID;
using Terraria.Localization;

namespace MoreAIPlayer.Companion.UI;

/// <summary>
/// Screen-space companion manager opened by right-clicking the debug book. Visual and
/// interaction language follows the matrix-style debug panels the user likes: dark panel,
/// bordered grid slots, hover highlight with menu ticks, opacity fade, click-off to close.
/// Each card shows the companion's full animated body (drawn the same way vanilla
/// UICharacter renders player previews) and its name. Right-click a card to dismiss that
/// companion, left-click to open its backpack; a button dismisses everyone at once.
/// </summary>
public static class CompanionManagerPanel
{
    private const int CardWidth = 96;
    private const int CardHeight = 138;
    private const int CardNameStrip = 26;
    private const int CardGap = 8;
    private const int PanelPad = 14;
    private const int HeaderHeight = 40;
    private const int FooterHeight = 44;
    private const int MaxColumns = 5;
    private const int BorderThick = 2;

    private static readonly Dictionary<int, Player> PreviewsBySlot = [];
    private static bool open;
    private static bool fadeOut;
    private static float opacity;
    private static Vector2 panelTopLeft;
    private static bool positionInitialized;
    private static int lastHoveredSlot = -1;
    private static int lastQuickHoveredSlot = -1;

    public static bool IsOpen => open;

    public static void Toggle()
    {
        if (open && !fadeOut)
        {
            StartClose();
            return;
        }

        if (!open)
        {
            opacity = 0f;
            positionInitialized = false;
        }
        open = true;
        fadeOut = false;
        SoundEngine.PlaySound(SoundID.MenuOpen with { Volume = 0.6f });
    }

    public static void StartClose()
    {
        if (!open || fadeOut)
            return;
        fadeOut = true;
        SoundEngine.PlaySound(SoundID.MenuClose with { Volume = 0.55f });
    }

    public static void CloseInstant()
    {
        open = false;
        fadeOut = false;
        opacity = 0f;
        panelTopLeft = Vector2.Zero;
        positionInitialized = false;
        lastHoveredSlot = -1;
        lastQuickHoveredSlot = -1;
        PreviewsBySlot.Clear();
    }

    /// <summary>
    /// Persistent top-centre matrix. It gives every summoned companion a stable click target,
    /// so opening a loadout never depends on catching a moving character in the world.
    /// </summary>
    public static void DrawQuickAccessBar() =>
        CompanionFaultGuard.RunDraw("companion quick access bar", DrawQuickAccessBarCore);

    private static void DrawQuickAccessBarCore()
    {
        if (Main.gameMenu || Main.dedServ || Main.netMode != NetmodeID.SinglePlayer ||
            Main.hideUI || Main.mapFullscreen)
        {
            lastQuickHoveredSlot = -1;
            return;
        }

        List<CompanionInstance> entries = CollectEntries();
        if (entries.Count == 0)
        {
            lastQuickHoveredSlot = -1;
            return;
        }
        PruneStalePreviews(entries);

        const int cellSize = 54;
        const int gap = 6;
        const int top = 18;
        const int sideMargin = 24;
        int maxColumns = System.Math.Max(1, (Main.screenWidth - sideMargin * 2 + gap) / (cellSize + gap));
        int columns = System.Math.Min(entries.Count, maxColumns);
        Rectangle mouseRect = new((int)Main.MouseScreen.X, (int)Main.MouseScreen.Y, 2, 2);
        bool leftClick = Main.mouseLeft && Main.mouseLeftRelease;
        int hoveredSlot = -1;
        var previewDraws = new List<(Rectangle Area, CompanionInstance Instance)>(entries.Count);

        for (int i = 0; i < entries.Count; i++)
        {
            int row = i / columns;
            int column = i % columns;
            int entriesInRow = System.Math.Min(columns, entries.Count - row * columns);
            int rowWidth = entriesInRow * cellSize + (entriesInRow - 1) * gap;
            int rowLeft = (Main.screenWidth - rowWidth) / 2;
            Rectangle cell = new(
                rowLeft + column * (cellSize + gap),
                top + row * (cellSize + gap),
                cellSize,
                cellSize);

            CompanionInstance companion = entries[i];
            bool hovered = cell.Intersects(mouseRect);
            bool selected = CompanionUISystem.IsOpen && CompanionUISystem.TargetSlot == companion.Slot;
            Color back = hovered ? new Color(56, 72, 90, 235) : new Color(24, 31, 42, 220);
            Color border = selected ? new Color(100, 255, 170)
                : hovered ? new Color(145, 215, 255)
                : new Color(76, 94, 116);
            DrawRect(cell, back);
            DrawBorder(cell, border, selected ? 3 : 2);

            int maxLife = System.Math.Max(1, companion.Entity.statLifeMax2);
            float lifeRatio = MathHelper.Clamp(companion.Entity.statLife / (float)maxLife, 0f, 1f);
            Rectangle lifeBack = new(cell.X + 4, cell.Bottom - 6, cell.Width - 8, 3);
            Rectangle lifeFill = new(lifeBack.X, lifeBack.Y, (int)(lifeBack.Width * lifeRatio), lifeBack.Height);
            DrawRect(lifeBack, new Color(10, 12, 16, 230));
            DrawRect(lifeFill, Color.Lerp(new Color(220, 65, 65), new Color(75, 220, 120), lifeRatio));

            previewDraws.Add((cell, companion));
            if (!hovered)
                continue;

            hoveredSlot = companion.Slot;
            Main.blockMouse = true;
            Main.LocalPlayer.mouseInterface = true;

            if (leftClick)
            {
                Main.mouseLeftRelease = false;
                Main.LocalPlayer.controlUseItem = false;
                CompanionUISystem.Open(companion.Slot);
                SoundEngine.PlaySound(SoundID.MenuOpen with { Volume = 0.55f, Pitch = 0.08f });
            }
        }

        DrawQuickPreviews(previewDraws);

        if (hoveredSlot >= 0)
        {
            CompanionInstance hovered = CompanionEntitySystem.GetBySlot(hoveredSlot);
            if (hovered is not null)
            {
                string text = Language.GetTextValue(
                    "Mods.MoreAIPlayer.CompanionCommand.QuickAccess", hovered.Entity.name);
                Main.instance.MouseText(text);
            }
        }

        if (hoveredSlot >= 0 && hoveredSlot != lastQuickHoveredSlot)
            SoundEngine.PlaySound(SoundID.MenuTick with { Volume = 0.32f, Pitch = 0.22f });
        lastQuickHoveredSlot = hoveredSlot;
    }

    /// <summary>Runs inside an interface layer (UI scale batch): draws the panel and handles clicks.
    /// Fenced so a fault in panel drawing or click handling degrades to a missing panel for the
    /// frame instead of aborting the shared HUD interface layer.</summary>
    public static void DrawAndInteract() =>
        CompanionFaultGuard.RunDraw("companion manager panel", DrawAndInteractCore);

    private static void DrawAndInteractCore()
    {
        if (!open)
            return;

        if (Main.gameMenu || Main.dedServ)
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

        List<CompanionInstance> entries = CollectEntries();
        PruneStalePreviews(entries);

        int columns = System.Math.Clamp(entries.Count, 1, MaxColumns);
        int rows = entries.Count == 0 ? 0 : (entries.Count + columns - 1) / columns;
        // The translated instruction line is wider than Chinese in several cultures.
        int contentWidth = System.Math.Max(columns * CardWidth + (columns - 1) * CardGap, 440);
        int contentHeight = rows == 0 ? 46 : rows * CardHeight + (rows - 1) * CardGap;
        int panelWidth = PanelPad * 2 + contentWidth;
        int panelHeight = PanelPad * 2 + HeaderHeight + 8 + contentHeight + 10 + FooterHeight;

        if (!positionInitialized)
        {
            panelTopLeft = Main.MouseScreen - new Vector2(panelWidth, panelHeight) * 0.5f;
            positionInitialized = true;
        }
        panelTopLeft = ClampToScreen(panelTopLeft, panelWidth, panelHeight);

        Rectangle panelArea = new((int)panelTopLeft.X, (int)panelTopLeft.Y, panelWidth, panelHeight);
        Rectangle mouseRect = new((int)Main.MouseScreen.X, (int)Main.MouseScreen.Y, 2, 2);
        bool mouseOverPanel = panelArea.Intersects(mouseRect);
        bool leftClick = Main.mouseLeft && Main.mouseLeftRelease;
        bool rightClick = Main.mouseRight && Main.mouseRightRelease;
        bool interactive = opacity >= 0.95f && !fadeOut;
        float op = opacity;

        DrawPanelBackground(panelArea, op);

        Utils.DrawBorderString(Main.spriteBatch, T("ManagerTitle"),
            new Vector2(panelArea.X + PanelPad + 2, panelArea.Y + PanelPad), new Color(210, 228, 255) * op, 0.95f);
        Utils.DrawBorderString(Main.spriteBatch, T("ManagerInstructions"),
            new Vector2(panelArea.X + PanelPad + 2, panelArea.Y + PanelPad + 22), new Color(140, 158, 182) * op, 0.68f);

        int contentX = panelArea.X + PanelPad;
        int contentY = panelArea.Y + PanelPad + HeaderHeight + 8;

        int hoveredSlotThisFrame = -1;
        var previewDraws = new List<(Rectangle Area, CompanionInstance Instance)>(entries.Count);

        if (entries.Count == 0)
        {
            Utils.DrawBorderString(Main.spriteBatch, T("ManagerEmpty"),
                new Vector2(contentX + 8, contentY + 12), new Color(150, 164, 186) * op, 0.8f);
        }

        for (int i = 0; i < entries.Count; i++)
        {
            CompanionInstance instance = entries[i];
            int col = i % columns;
            int row = i / columns;
            Rectangle card = new(
                contentX + col * (CardWidth + CardGap),
                contentY + row * (CardHeight + CardGap),
                CardWidth, CardHeight);

            bool hovered = card.Intersects(mouseRect);
            if (hovered)
                hoveredSlotThisFrame = instance.Slot;

            DrawCard(card, instance, hovered, op);
            previewDraws.Add((card, instance));

            if (hovered && interactive)
            {
                if (rightClick)
                {
                    Main.mouseRightRelease = false;
                    CompanionEntitySystem.Dismiss(instance.Slot);
                    SoundEngine.PlaySound(SoundID.MenuClose with { Volume = 0.5f, Pitch = -0.1f });
                }
                else if (leftClick)
                {
                    Main.mouseLeftRelease = false;
                    CompanionUISystem.Open(instance.Slot);
                    StartClose();
                }
            }
        }

        Rectangle dismissAllButton = new(
            panelArea.Right - PanelPad - 132,
            panelArea.Bottom - PanelPad - FooterHeight + 8,
            132, 28);
        bool dismissHovered = dismissAllButton.Intersects(mouseRect);
        DrawButton(dismissAllButton, T("DismissAll"), dismissHovered, entries.Count > 0, op);
        if (dismissHovered && interactive && leftClick && entries.Count > 0)
        {
            Main.mouseLeftRelease = false;
            CompanionEntitySystem.DismissOwnedBy(Main.myPlayer);
            StartClose();
        }

        Utils.DrawBorderString(Main.spriteBatch, T("OnlineCount", entries.Count),
            new Vector2(panelArea.X + PanelPad + 2, dismissAllButton.Y + 4), new Color(150, 168, 192) * op, 0.78f);

        if (op > 0.6f)
            DrawPreviews(previewDraws, op);

        if (hoveredSlotThisFrame >= 0 && hoveredSlotThisFrame != lastHoveredSlot && interactive)
            SoundEngine.PlaySound(SoundID.MenuTick with { Volume = 0.38f, Pitch = 0.18f });
        lastHoveredSlot = hoveredSlotThisFrame;

        if (mouseOverPanel)
        {
            Main.blockMouse = true;
            Main.LocalPlayer.mouseInterface = true;
        }
        else if (interactive && (leftClick || rightClick))
        {
            StartClose();
        }
    }

    private static string T(string key, params object[] args) =>
        Language.GetTextValue($"Mods.MoreAIPlayer.CompanionUI.{key}", args);

    private static List<CompanionInstance> CollectEntries()
    {
        List<CompanionInstance> entries = [];
        foreach (CompanionInstance instance in CompanionEntitySystem.All)
        {
            // The automation companion is managed only through the automation book, never the
            // normal manager panel (no dismiss button, no backpack card here).
            if (instance.OwnerWhoAmI == Main.myPlayer && instance.Entity.active && !instance.IsAutoIdentity)
                entries.Add(instance);
        }
        entries.Sort((a, b) => a.ProfileId.CompareTo(b.ProfileId));
        return entries;
    }

    private static void PruneStalePreviews(List<CompanionInstance> entries)
    {
        if (PreviewsBySlot.Count == 0)
            return;
        List<int> stale = [];
        foreach (int slot in PreviewsBySlot.Keys)
        {
            bool alive = false;
            foreach (CompanionInstance instance in entries)
            {
                if (instance.Slot == slot)
                {
                    alive = true;
                    break;
                }
            }
            if (!alive)
                stale.Add(slot);
        }
        foreach (int slot in stale)
            PreviewsBySlot.Remove(slot);
    }

    private static void DrawCard(Rectangle card, CompanionInstance instance, bool hovered, float op)
    {
        Color back = hovered ? new Color(64, 74, 88) : new Color(32, 38, 48);
        Color border = hovered ? new Color(150, 205, 255) : new Color(80, 92, 108);
        DrawRect(card, back * (op * 0.92f));
        DrawBorder(card, border * op, BorderThick);

        Rectangle nameStrip = new(card.X, card.Bottom - CardNameStrip, card.Width, CardNameStrip);
        DrawRect(nameStrip, new Color(20, 25, 34) * (op * 0.9f));

        string name = instance.Entity.name;
        Vector2 size = FontAssets.MouseText.Value.MeasureString(name);
        float scale = System.Math.Min(0.8f, (card.Width - 10f) / System.Math.Max(1f, size.X));
        Vector2 textPos = new(card.X + card.Width * 0.5f - size.X * scale * 0.5f, nameStrip.Y + (CardNameStrip - size.Y * scale) * 0.5f);
        Utils.DrawBorderString(Main.spriteBatch, name, textPos, new Color(226, 234, 248) * op, scale);
    }

    private static void DrawButton(Rectangle button, string label, bool hovered, bool enabled, float op)
    {
        Color back = !enabled ? new Color(38, 40, 46) : hovered ? new Color(112, 54, 54) : new Color(70, 40, 44);
        Color border = !enabled ? new Color(70, 74, 84) : hovered ? new Color(255, 150, 140) : new Color(150, 96, 100);
        DrawRect(button, back * (op * 0.94f));
        DrawBorder(button, border * op, BorderThick);

        Vector2 size = FontAssets.MouseText.Value.MeasureString(label) * 0.8f;
        Vector2 pos = new(button.X + (button.Width - size.X) * 0.5f, button.Y + (button.Height - size.Y) * 0.5f);
        Color textColor = !enabled ? new Color(120, 124, 134) : new Color(255, 214, 210);
        Utils.DrawBorderString(Main.spriteBatch, label, pos, textColor * op, 0.8f);
    }

    /// <summary>
    /// Full-body previews are drawn the way vanilla UICharacter does it: an immediate-mode
    /// UI batch and PlayerRenderer.DrawPlayer at "screen position + Main.screenPosition".
    /// </summary>
    private static void DrawPreviews(List<(Rectangle Area, CompanionInstance Instance)> previewDraws, float op)
    {
        if (previewDraws.Count == 0)
            return;

        SpriteBatch spriteBatch = Main.spriteBatch;
        spriteBatch.End();
        spriteBatch.Begin(SpriteSortMode.Immediate, BlendState.AlphaBlend, SamplerState.PointClamp,
            DepthStencilState.None, RasterizerState.CullCounterClockwise, null, Main.UIScaleMatrix);

        foreach ((Rectangle card, CompanionInstance instance) in previewDraws)
        {
            if (!instance.TryGetContext(out Player owner, out CompanionProfile profile))
                continue;

            Player preview = GetOrPreparePreview(instance, owner, profile);
            Rectangle previewArea = new(card.X, card.Y, card.Width, card.Height - CardNameStrip);
            Vector2 drawTopLeft = new(
                previewArea.X + previewArea.Width * 0.5f - preview.width * 0.5f,
                previewArea.Y + previewArea.Height * 0.5f - preview.height * 0.5f + 4f);

            using (new Main.CurrentPlayerOverride(preview))
            {
                Main.PlayerRenderer.DrawPlayer(Main.Camera, preview, drawTopLeft + Main.screenPosition, 0f, Vector2.Zero, 0f, 1f);
            }
        }

        spriteBatch.End();
        spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, null, null, null, null, Main.UIScaleMatrix);
    }

    private static void DrawQuickPreviews(List<(Rectangle Area, CompanionInstance Instance)> previewDraws)
    {
        if (previewDraws.Count == 0)
            return;

        SpriteBatch spriteBatch = Main.spriteBatch;
        spriteBatch.End();
        spriteBatch.Begin(SpriteSortMode.Immediate, BlendState.AlphaBlend, SamplerState.PointClamp,
            DepthStencilState.None, RasterizerState.CullCounterClockwise, null, Main.UIScaleMatrix);

        foreach ((Rectangle cell, CompanionInstance companion) in previewDraws)
        {
            if (!companion.TryGetContext(out Player owner, out CompanionProfile profile))
                continue;

            Player preview = GetOrPreparePreview(companion, owner, profile);
            Vector2 drawTopLeft = new(
                cell.Center.X - preview.width * 0.5f,
                cell.Center.Y - preview.height * 0.5f - 1f);

            using (new Main.CurrentPlayerOverride(preview))
            {
                Main.PlayerRenderer.DrawPlayer(Main.Camera, preview,
                    drawTopLeft + Main.screenPosition, 0f, Vector2.Zero, 0f, 0.85f);
            }
        }

        spriteBatch.End();
        spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, null, null, null, null, Main.UIScaleMatrix);
    }

    private static Player GetOrPreparePreview(CompanionInstance instance, Player owner, CompanionProfile profile)
    {
        if (!PreviewsBySlot.TryGetValue(instance.Slot, out Player preview))
        {
            preview = new Player();
            PreviewsBySlot[instance.Slot] = preview;
        }

        using (new Main.CurrentPlayerOverride(preview))
        {
            preview.whoAmI = -1;
            preview.name = instance.Entity.name;
            preview.dead = false;
            preview.direction = 1;
            preview.gravDir = 1f;
            preview.selectedItem = 0;
            preview.inventory[0].TurnToAir();
            profile.ApplyAppearanceTo(preview, owner);

            for (int i = 0; i < CompanionPlayer.ArmorSize; i++)
            {
                preview.armor[i] = profile.Armor[i].Clone();
                preview.armor[10 + i] = profile.Vanity[i].Clone();
                preview.dye[i] = profile.Dyes[i].Clone();
            }

            preview.ResetEffects();
            preview.ResetVisibleAccessories();
            // Mirrors CompanionInstance.BindProfile: a locked accessory slot contributes nothing,
            // so the preview shows the same silhouette the live companion actually has.
            int unlockedAccessories = CompanionSlotRules.UnlockedAccessorySlots();
            for (int i = 0; i < CompanionPlayer.AccessorySize; i++)
            {
                Item accessory = profile.AccessoryForSlot(i, unlockedAccessories);
                preview.armor[3 + i] = accessory.Clone();
                if (!accessory.IsAir)
                    preview.UpdateVisibleAccessory(3 + i, accessory);
            }

            preview.UpdateMiscCounter();
            preview.UpdateDyes();
            preview.PlayerFrame();

            // Same idle-walk cycle vanilla UICharacter uses for animated previews.
            int frame = (int)(Main.GlobalTimeWrappedHourly / 0.07f) % 14 + 6;
            preview.bodyFrame.Y = preview.legFrame.Y = preview.headFrame.Y = frame * 56;
            preview.WingFrame(wingFlap: false);
        }

        return preview;
    }

    private static void DrawPanelBackground(Rectangle area, float op)
    {
        DrawRect(area, new Color(14, 18, 26, 238) * op);
        DrawBorder(area, new Color(80, 96, 118) * op, BorderThick);
        DrawBorder(new Rectangle(area.X + 5, area.Y + 5, area.Width - 10, area.Height - 10),
            new Color(36, 46, 62, 200) * op, 1);
    }

    private static void DrawRect(Rectangle rectangle, Color color) =>
        Main.spriteBatch.Draw(TextureAssets.MagicPixel.Value, rectangle, color);

    private static void DrawBorder(Rectangle rectangle, Color color, int thickness)
    {
        DrawRect(new Rectangle(rectangle.X, rectangle.Y, rectangle.Width, thickness), color);
        DrawRect(new Rectangle(rectangle.X, rectangle.Bottom - thickness, rectangle.Width, thickness), color);
        DrawRect(new Rectangle(rectangle.X, rectangle.Y, thickness, rectangle.Height), color);
        DrawRect(new Rectangle(rectangle.Right - thickness, rectangle.Y, thickness, rectangle.Height), color);
    }

    private static Vector2 ClampToScreen(Vector2 topLeft, int width, int height)
    {
        const float margin = 12f;
        return new Vector2(
            MathHelper.Clamp(topLeft.X, margin, System.Math.Max(margin, Main.screenWidth - width - margin)),
            MathHelper.Clamp(topLeft.Y, margin, System.Math.Max(margin, Main.screenHeight - height - margin)));
    }
}
