using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using MoreAIPlayer.Companion.Hunter;
using Terraria;
using Terraria.Audio;
using Terraria.GameContent;
using Terraria.ID;
using Terraria.Localization;

namespace MoreAIPlayer.Companion.UI;

public static class HunterConfigPanel
{
    private static bool open;
    private static bool fadeOut;
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
        SoundEngine.PlaySound(SoundID.MenuClose with { Volume = 0.55f });
    }

    public static void CloseInstant()
    {
        open = false;
        fadeOut = false;
        opacity = 0f;
    }

    public static void DrawAndInteract()
    {
        if (!open)
            return;

        if (Main.gameMenu || Main.dedServ || Main.netMode != NetmodeID.SinglePlayer)
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

        SpriteBatch sb = Main.spriteBatch;
        Texture2D pixel = TextureAssets.MagicPixel.Value;

        const int panelW = 380;
        const int panelH = 368;
        Vector2 center = new(Main.screenWidth * 0.5f, Main.screenHeight * 0.5f);
        Rectangle panelRect = new((int)(center.X - panelW * 0.5f), (int)(center.Y - panelH * 0.5f), panelW, panelH);

        // Hover over panel suppresses item usage
        if (panelRect.Contains(Main.MouseScreen.ToPoint()))
        {
            Main.LocalPlayer.mouseInterface = true;
        }

        // Click outside panel to close
        if (Main.mouseLeft && Main.mouseLeftRelease && !panelRect.Contains(Main.MouseScreen.ToPoint()))
        {
            StartClose();
            return;
        }

        // Draw Panel Background & Border
        Rectangle border = panelRect;
        border.Inflate(2, 2);
        sb.Draw(pixel, border, new Color(220, 60, 60) * opacity);
        sb.Draw(pixel, panelRect, new Color(18, 14, 28, 240) * opacity);

        // Draw Header Title
        Vector2 titlePos = new(panelRect.X + 16, panelRect.Y + 14);
        Terraria.Utils.DrawBorderString(sb, Text("HunterPanelTitle"), titlePos, new Color(255, 120, 100) * opacity, 1.05f);

        // Close Button
        Rectangle closeRect = new(panelRect.Right - 54, panelRect.Y + 12, 40, 24);
        bool closeHover = closeRect.Contains(Main.MouseScreen.ToPoint());
        sb.Draw(pixel, closeRect, (closeHover ? new Color(220, 60, 60) : new Color(120, 40, 40)) * opacity);
        Terraria.Utils.DrawBorderString(sb, Text("Close"), new Vector2(closeRect.X + 6, closeRect.Y + 3), Color.White * opacity, 0.8f);
        if (closeHover && Main.mouseLeft && Main.mouseLeftRelease)
        {
            StartClose();
            return;
        }

        int currY = panelRect.Y + 54;

        string stageText = HunterSystem.HunterModeActive
            ? Text(HunterSystem.ActiveStage == HunterGameStage.PreHardmode ? "HunterStagePreHardmode" : "HunterStageEarlyHardmode")
            : Text("HunterStageUnavailable");
        Terraria.Utils.DrawBorderString(sb, Text("HunterStageStatus", stageText),
            new Vector2(panelRect.X + 20, currY), new Color(255, 210, 130) * opacity, 0.78f);
        currY += 24;

        // 1. Master Mode Toggle Button
        Rectangle toggleRect = new(panelRect.X + 20, currY, panelW - 40, 40);
        bool toggleHover = toggleRect.Contains(Main.MouseScreen.ToPoint());
        Color toggleBg = HunterSystem.HunterModeActive
            ? (toggleHover ? new Color(180, 50, 50) : new Color(140, 30, 30))
            : (toggleHover ? new Color(50, 140, 60) : new Color(30, 90, 40));

        sb.Draw(pixel, toggleRect, toggleBg * opacity);
        string toggleText = Text(HunterSystem.HunterModeActive ? "HunterPanelEnabled" : "HunterPanelDisabled");
        Terraria.Utils.DrawBorderString(sb, toggleText, new Vector2(toggleRect.X + 16, toggleRect.Y + 10), Color.White * opacity, 0.95f);

        if (toggleHover && Main.mouseLeft && Main.mouseLeftRelease)
        {
            Main.mouseLeftRelease = false;
            HunterSystem.ToggleMode();
            SoundEngine.PlaySound(SoundID.MenuTick);
        }

        currY += 56;

        // 2. Hunter Count Selector
        Terraria.Utils.DrawBorderString(sb, Text("HunterCountTitle"), new Vector2(panelRect.X + 20, currY), new Color(220, 220, 220) * opacity, 0.85f);
        currY += 26;

        for (int i = 1; i <= 3; i++)
        {
            Rectangle countBtn = new(panelRect.X + 20 + (i - 1) * 110, currY, 100, 32);
            bool countHover = countBtn.Contains(Main.MouseScreen.ToPoint());
            bool active = HunterSystem.TargetHunterCount == i;

            Color btnBg = active
                ? new Color(200, 140, 40)
                : (countHover ? new Color(80, 80, 100) : new Color(50, 50, 70));

            sb.Draw(pixel, countBtn, btnBg * opacity);
            Terraria.Utils.DrawBorderString(sb, Text("HunterCount", i), new Vector2(countBtn.X + 18, countBtn.Y + 6), Color.White * opacity, 0.85f);

            int targetI = i;
            if (countHover && Main.mouseLeft && Main.mouseLeftRelease)
            {
                Main.mouseLeftRelease = false;
                HunterSystem.SetHunterCount(targetI);
                SoundEngine.PlaySound(SoundID.MenuTick);
            }
        }

        currY += 46;

        // 3. Status Display
        Terraria.Utils.DrawBorderString(sb, Text("HunterStatusTitle"), new Vector2(panelRect.X + 20, currY), new Color(180, 200, 230) * opacity, 0.85f);
        currY += 24;

        if (!HunterSystem.HunterModeActive)
        {
            Terraria.Utils.DrawBorderString(sb, Text("HunterInactive"), new Vector2(panelRect.X + 30, currY), Color.Gray * opacity, 0.8f);
            currY += 20;
        }
        else
        {
            int index = 1;
            foreach (HunterInstance hunter in HunterSystem.All)
            {
                string statusStr;
                Color statusColor;
                if (hunter.Entity.dead || hunter.Entity.statLife <= 0)
                {
                    statusStr = Text("HunterPanelReviving", hunter.Entity.name, hunter.ReviveTicksLeft / 60);
                    statusColor = new Color(255, 100, 100);
                }
                else if (hunter.Brain.State == HunterState.Resting)
                {
                    statusStr = Text("HunterPanelResting", hunter.Entity.name);
                    statusColor = new Color(255, 200, 100);
                }
                else
                {
                    statusStr = Text("HunterPanelHunting", hunter.Entity.name);
                    statusColor = new Color(120, 255, 120);
                }

                Terraria.Utils.DrawBorderString(sb, statusStr, new Vector2(panelRect.X + 30, currY), statusColor * opacity, 0.8f);
                currY += 20;
                index++;
            }
        }

        currY += 10;

        // 4. Action buttons
        Rectangle refreshBtn = new(panelRect.X + 20, panelRect.Bottom - 45, panelW - 40, 32);
        bool refreshHover = refreshBtn.Contains(Main.MouseScreen.ToPoint());
        sb.Draw(pixel, refreshBtn, (refreshHover ? new Color(100, 100, 140) : new Color(60, 60, 90)) * opacity);
        Terraria.Utils.DrawBorderString(sb, Text("HunterRefresh"), new Vector2(refreshBtn.X + 50, refreshBtn.Y + 6), Color.White * opacity, 0.85f);

        if (refreshHover && Main.mouseLeft && Main.mouseLeftRelease)
        {
            Main.mouseLeftRelease = false;
            if (HunterSystem.HunterModeActive)
            {
                HunterSystem.RestartHunt();
            }
            else
            {
                HunterSystem.ToggleMode();
            }
            SoundEngine.PlaySound(SoundID.MenuTick);
        }
    }

    private static string Text(string key, params object[] args) =>
        Language.GetTextValue($"Mods.MoreAIPlayer.CompanionCommand.{key}", args);
}
