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

    public static void DrawAndInteract() =>
        CompanionFaultGuard.RunDraw("hunter config panel", DrawAndInteractCore);

    private static void DrawAndInteractCore()
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

        const int panelW = 500;
        const int panelH = 500;
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
        DrawFitted(sb, Text("HunterPanelTitle"), titlePos, panelW - 90,
            new Color(255, 120, 100) * opacity, 1.05f, 0.72f);

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

        int currY = panelRect.Y + 52;

        HunterGameStage visibleStage = HunterSystem.ActiveStage;
        if (!HunterSystem.HunterModeActive && !HunterSystem.TryGetCurrentEligibleStage(out visibleStage))
            visibleStage = HunterGameStage.None;
        string stageText = Text(HunterSystem.StageKey(visibleStage));
        Terraria.Utils.DrawBorderString(sb, Text("HunterStageStatus", stageText),
            new Vector2(panelRect.X + 20, currY), new Color(255, 210, 130) * opacity, 0.78f);
        currY += 26;

        // 1. Trial rule selector. Selecting during a live trial deliberately restarts it so a
        // nearly-complete objective can never be converted into another victory condition.
        Terraria.Utils.DrawBorderString(sb, Text("HunterTrialTypeTitle"), new Vector2(panelRect.X + 20, currY),
            new Color(230, 220, 235) * opacity, 0.85f);
        currY += 25;

        HunterTrialType[] trials = [HunterTrialType.Survival, HunterTrialType.Elimination, HunterTrialType.Breakout];
        const int trialGap = 8;
        int trialWidth = (panelW - 40 - trialGap * 2) / 3;
        for (int index = 0; index < trials.Length; index++)
        {
            HunterTrialType trial = trials[index];
            Rectangle trialRect = new(panelRect.X + 20 + index * (trialWidth + trialGap), currY, trialWidth, 34);
            bool hover = trialRect.Contains(Main.MouseScreen.ToPoint());
            bool selected = HunterSystem.SelectedTrial == trial;
            Color background = selected
                ? new Color(178, 72, 68)
                : hover ? new Color(86, 72, 100) : new Color(50, 44, 66);
            sb.Draw(pixel, trialRect, background * opacity);
            DrawCentered(sb, Text(HunterSystem.TrialKey(trial)), trialRect, Color.White * opacity, 0.82f);

            if (hover && Main.mouseLeft && Main.mouseLeftRelease)
            {
                Main.mouseLeftRelease = false;
                HunterSystem.SetTrialType(trial);
                SoundEngine.PlaySound(SoundID.MenuTick);
            }
        }

        currY += 42;
        string description = HunterSystem.SelectedTrial switch
        {
            HunterTrialType.Elimination => Text("HunterTrialEliminationDescription", HunterSystem.EliminationTarget),
            HunterTrialType.Breakout => Text("HunterTrialBreakoutDescription", HunterSystem.BreakoutDurationTicks / 60),
            _ => Text("HunterTrialSurvivalDescription", HunterSystem.SurvivalDurationTicks / 60)
        };
        DrawFitted(sb, description, new Vector2(panelRect.X + 22, currY), panelW - 44,
            new Color(190, 205, 220) * opacity, 0.72f, 0.56f);
        currY += 28;

        // 2. Hunter count selector
        Terraria.Utils.DrawBorderString(sb, Text("HunterCountTitle"), new Vector2(panelRect.X + 20, currY), new Color(220, 220, 220) * opacity, 0.85f);
        currY += 26;

        for (int i = 1; i <= 3; i++)
        {
            Rectangle countBtn = new(panelRect.X + 20 + (i - 1) * 150, currY, 140, 32);
            bool countHover = countBtn.Contains(Main.MouseScreen.ToPoint());
            bool active = HunterSystem.TargetHunterCount == i;

            Color btnBg = active
                ? new Color(200, 140, 40)
                : (countHover ? new Color(80, 80, 100) : new Color(50, 50, 70));

            sb.Draw(pixel, countBtn, btnBg * opacity);
            DrawCentered(sb, Text("HunterCount", i), countBtn, Color.White * opacity, 0.82f);

            int targetI = i;
            if (countHover && Main.mouseLeft && Main.mouseLeftRelease)
            {
                Main.mouseLeftRelease = false;
                HunterSystem.SetHunterCount(targetI);
                SoundEngine.PlaySound(SoundID.MenuTick);
            }
        }

        currY += 45;

        // 3. Start/stop remains a single obvious action even though the rules are deeper.
        Rectangle toggleRect = new(panelRect.X + 20, currY, panelW - 40, 40);
        bool toggleHover = toggleRect.Contains(Main.MouseScreen.ToPoint());
        Color toggleBg = HunterSystem.HunterModeActive
            ? (toggleHover ? new Color(180, 50, 50) : new Color(140, 30, 30))
            : (toggleHover ? new Color(50, 140, 60) : new Color(30, 90, 40));

        sb.Draw(pixel, toggleRect, toggleBg * opacity);
        string toggleText = Text(HunterSystem.HunterModeActive ? "HunterPanelEnabled" : "HunterPanelDisabled");
        DrawCentered(sb, toggleText, toggleRect, Color.White * opacity, 0.9f);
        if (toggleHover && Main.mouseLeft && Main.mouseLeftRelease)
        {
            Main.mouseLeftRelease = false;
            HunterSystem.ToggleMode();
            SoundEngine.PlaySound(SoundID.MenuTick);
        }

        currY += 52;

        // 4. Objective and hunter status
        Terraria.Utils.DrawBorderString(sb, Text("HunterObjectiveStatusTitle"), new Vector2(panelRect.X + 20, currY),
            new Color(255, 210, 130) * opacity, 0.82f);
        string objective = ObjectiveText();
        DrawFitted(sb, objective, new Vector2(panelRect.X + 175, currY), panelW - 195,
            new Color(225, 235, 245) * opacity, 0.78f, 0.56f);
        currY += 24;

        Terraria.Utils.DrawBorderString(sb, Text("HunterStatusTitle"), new Vector2(panelRect.X + 20, currY), new Color(180, 200, 230) * opacity, 0.82f);
        currY += 22;

        if (!HunterSystem.HunterModeActive)
        {
            Terraria.Utils.DrawBorderString(sb, Text("HunterInactive"), new Vector2(panelRect.X + 30, currY), Color.Gray * opacity, 0.8f);
            currY += 20;
        }
        else
        {
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
            }
        }

        // 5. Explicitly restart the selected trial from zero.
        Rectangle refreshBtn = new(panelRect.X + 20, panelRect.Bottom - 45, panelW - 40, 32);
        bool refreshHover = refreshBtn.Contains(Main.MouseScreen.ToPoint());
        sb.Draw(pixel, refreshBtn, (refreshHover ? new Color(100, 100, 140) : new Color(60, 60, 90)) * opacity);
        DrawCentered(sb, Text("HunterRefresh"), refreshBtn, Color.White * opacity, 0.82f);

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

    private static string ObjectiveText()
    {
        if (!HunterSystem.HunterModeActive)
            return Text("HunterObjectiveNotStarted");
        if (HunterSystem.IsPreparing)
            return UiText("HunterTrackerPreparing", HunterSystem.PreparationSeconds);

        return HunterSystem.ActiveTrial switch
        {
            HunterTrialType.Elimination => UiText("HunterTrackerObjectiveElimination", HunterSystem.DefeatedHunters, HunterSystem.EliminationTarget),
            HunterTrialType.Breakout when HunterSystem.TryGetBreakoutDestination(out Vector2 destination) =>
                UiText("HunterTrackerObjectiveBreakout",
                    System.Math.Max(0, (int)System.MathF.Ceiling(Vector2.Distance(Main.LocalPlayer.Center, destination) / 16f)),
                    HunterSystem.ObjectiveSecondsRemaining),
            _ => UiText("HunterTrackerObjectiveSurvival", HunterSystem.ObjectiveSecondsRemaining)
        };
    }

    private static void DrawCentered(SpriteBatch sb, string text, Rectangle bounds, Color color, float scale)
    {
        Vector2 rawSize = FontAssets.MouseText.Value.MeasureString(text);
        if (rawSize.X * scale > bounds.Width - 10f)
            scale = System.MathF.Max(0.56f, (bounds.Width - 10f) / System.MathF.Max(1f, rawSize.X));
        Vector2 size = rawSize * scale;
        Vector2 position = new(bounds.Center.X - size.X * 0.5f, bounds.Center.Y - size.Y * 0.5f - 1f);
        Terraria.Utils.DrawBorderString(sb, text, position, color, scale);
    }

    private static void DrawFitted(SpriteBatch sb, string text, Vector2 position, float maxWidth,
        Color color, float preferredScale, float minimumScale)
    {
        float rawWidth = System.MathF.Max(1f, FontAssets.MouseText.Value.MeasureString(text).X);
        float scale = System.MathF.Min(preferredScale, maxWidth / rawWidth);
        Terraria.Utils.DrawBorderString(sb, text, position, color, System.MathF.Max(minimumScale, scale));
    }

    private static string Text(string key, params object[] args) =>
        Language.GetTextValue($"Mods.MoreAIPlayer.CompanionCommand.{key}", args);

    private static string UiText(string key, params object[] args) =>
        Language.GetTextValue($"Mods.MoreAIPlayer.CompanionUI.{key}", args);
}
