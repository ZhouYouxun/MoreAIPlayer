using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using MoreAIPlayer.Companion.Automation;
using MoreAIPlayer.Companion.Hunter;
using Terraria;
using Terraria.ID;
using Terraria.Localization;
using Terraria.ModLoader;
using Terraria.UI;

namespace MoreAIPlayer.Companion.UI;

public sealed class CompanionUISystem : ModSystem
{
    private static CompanionUISystem instance;

    private UserInterface userInterface;
    private UIState inventoryUI;
    private int targetSlot = -1;
    private Vector2? inventoryPanelPosition;
    private bool pendingPanelRecovery;
    private bool recoverAppearancePage;
    private string pendingRecoverySource;
    private Exception pendingRecoveryException;

    public static bool IsOpen => instance?.inventoryUI is not null;
    public static int TargetSlot => instance?.targetSlot ?? -1;

    public override void Load()
    {
        instance = this;
        if (!Main.dedServ)
            userInterface = new UserInterface();
        Mod.Logger.Info($"MoreAIPlayer build stamp {BuildStamp}");
    }

    public override void Unload()
    {
        userInterface?.SetState(null);
        inventoryUI?.Deactivate();
        CompanionModeWheel.Close();
        CompanionBuilderModeWheel.Close();
        PhantomCommandWheel.Close();
        CompanionManagerPanel.CloseInstant();
        HunterConfigPanel.CloseInstant();
        PlayerScaleMatrixPanel.CloseInstant();
        instance = null;
        userInterface = null;
        inventoryUI = null;
        targetSlot = -1;
        inventoryPanelPosition = null;
        pendingPanelRecovery = false;
        pendingRecoverySource = null;
        pendingRecoveryException = null;
    }

    public override void OnWorldUnload()
    {
        Close();
        CompanionModeWheel.Close();
        CompanionBuilderModeWheel.Close();
        PhantomCommandWheel.Close();
        CompanionManagerPanel.CloseInstant();
        HunterConfigPanel.CloseInstant();
        PlayerScaleMatrixPanel.CloseInstant();
    }

    public static void Open(int companionSlot)
    {
        if (instance?.userInterface is null || Main.gameMenu)
            return;

        CompanionInstance companion = CompanionEntitySystem.GetBySlot(companionSlot);
        if (companion is null || !companion.TryGetContext(out _, out CompanionProfile profile))
            return;

        // Channelled "holdout" weapons (e.g. Calamity's GrandDad) keep swinging until the
        // channel is explicitly released; opening the backpack is the user's cue that gear
        // might change, so release it now instead of leaving the companion locked onto
        // whatever it was holding the moment the panel opened.
        companion.ForceReleaseChanneledWeapon();

        instance.pendingPanelRecovery = false;
        instance.ShowPanel(profile, companionSlot, appearancePage: false, recoveryAttempt: false);
    }

    public static void Close()
    {
        if (instance?.userInterface is null)
            return;

        instance.userInterface.SetState(null);
        instance.inventoryPanelPosition = (instance.inventoryUI as ICompanionPanelState)?.PanelPosition ?? instance.inventoryPanelPosition;
        instance.inventoryUI = null;
        instance.targetSlot = -1;
        instance.pendingPanelRecovery = false;
        instance.pendingRecoverySource = null;
        instance.pendingRecoveryException = null;
    }

    /// <summary>Replaces the equipment/backpack page with the companion's dresser page.</summary>
    internal static void OpenAppearance(int companionSlot, Vector2? panelPosition)
    {
        if (instance?.userInterface is null || Main.gameMenu)
            return;

        CompanionInstance companion = CompanionEntitySystem.GetBySlot(companionSlot);
        if (companion is null || !companion.TryGetContext(out _, out CompanionProfile profile))
            return;

        instance.inventoryPanelPosition = panelPosition ?? (instance.inventoryUI as ICompanionPanelState)?.PanelPosition;
        instance.pendingPanelRecovery = false;
        instance.ShowPanel(profile, companionSlot, appearancePage: true, recoveryAttempt: false);
    }

    public override void UpdateUI(GameTime gameTime)
    {
        CompanionModeWheel.UpdateInput();
        CompanionBuilderModeWheel.UpdateInput();
        PhantomCommandWheel.UpdateInput();

        if (pendingPanelRecovery)
        {
            RecoverPanel();
            return;
        }

        if (inventoryUI is null || userInterface is null)
            return;

        if (CompanionEntitySystem.GetBySlot(targetSlot) is null)
        {
            Close();
            return;
        }

        if (!Main.playerInventory)
        {
            Close();
            return;
        }

        try
        {
            userInterface.Update(gameTime);
        }
        catch (Exception exception)
        {
            QueuePanelRecovery("UI update", exception);
        }
    }

    /// <summary>
    /// Identifies exactly which build is running. Bumped by hand on every change so that
    /// "nothing I changed had any effect" can be told apart from "the change did not help" —
    /// three rounds of fixes were evaluated without that distinction being available.
    /// </summary>
    public const string BuildStamp = "2026-08-13 #39 command-wheel-line-render-hotfix";

    /// <summary>Draws a live decision readout over each companion only when the player has
    /// explicitly enabled the client-side diagnostic configuration.</summary>
    public static bool ShowDiagnostics => MoreAIPlayerClientConfig.Current?.EnablePhantomPlayerDebugText == true;

    public override void OnWorldLoad()
    {
        CompanionManagerPanel.CloseInstant();
        if (!Main.dedServ)
            Main.NewText(T("BuildLoaded", BuildStamp), 130, 220, 160);
    }

    /// <summary>
    /// Live per-companion readout drawn in the world, so diagnosing an idle companion needs no
    /// panel open and no log file. The ItemCheck line is the important one: vanilla gates
    /// projectile spawning and aiming behind the local-player check, so a companion whose
    /// detour is not running animates and makes noise while firing nothing and aiming at the
    /// real player's cursor.
    /// </summary>
    private static void DrawDiagnostics()
    {
        if (!ShowDiagnostics || Main.gameMenu || Main.netMode != NetmodeID.SinglePlayer)
            return;

        var font = Terraria.GameContent.FontAssets.MouseText.Value;
        foreach (CompanionInstance instance in CompanionEntitySystem.All)
        {
            if (instance.OwnerWhoAmI != Main.myPlayer || !instance.Entity.active)
                continue;

            bool detourLive = instance.TicksSinceItemCheck <= 5;
            string itemCheckLine = detourLive
                ? T("DiagnosticsItemCheckTaken", instance.ItemCheckTakeovers)
                : T("DiagnosticsItemCheckMissing", instance.ItemCheckTakeovers);

            CompanionProfile profile = instance.TryGetContext(out _, out CompanionProfile found) ? found : null;
            string modeLine = profile is null
                ? T("DiagnosticsModeNone")
                : T("DiagnosticsMode", CompanionModeRules.Name(CompanionModeRules.Effective(profile)));
            string stateLine = AI.CompanionAttackStateText.Describe(instance.Brain.AttackState);
            string aimLine = T("DiagnosticsAim",
                (int)(instance.AimWorld.X - instance.Entity.Center.X),
                (int)(instance.AimWorld.Y - instance.Entity.Center.Y));
            string recoveryLine = instance.RecoveredItemChecks == 0 && instance.AimCorrections == 0 &&
                instance.SlotRepairs == 0 &&
                CompanionEntitySystem.HooksInstalled
                ? T("DiagnosticsPathOk")
                : T("DiagnosticsRecovery",
                    instance.RecoveredItemChecks,
                    instance.SlotRepairs,
                    instance.AimCorrections,
                    instance.ConsecutiveItemCheckMisses);
            string ownershipLine = T("DiagnosticsOwnership",
                CompanionEntitySystem.ProjectileOwnerCorrections,
                CompanionEntitySystem.LeakedContextRecoveries);

            List<string> lines = [modeLine, stateLine, itemCheckLine, aimLine, recoveryLine, ownershipLine];
            List<Color> colors =
            [
                new Color(200, 210, 235),
                new Color(255, 225, 150),
                detourLive ? new Color(150, 235, 160) : new Color(255, 110, 100),
                new Color(180, 200, 230),
                instance.ConsecutiveItemCheckMisses == 0 ? new Color(150, 235, 160) : new Color(255, 180, 100),
                new Color(160, 205, 235)
            ];

            if (instance.IsAutoIdentity)
            {
                foreach (string line in AutoDiagnostics.BuildLines(instance.AutoBrain.TaskRuntime))
                {
                    lines.Add(line);
                    colors.Add(new Color(120, 220, 255));
                }
            }

            Vector2 anchor = instance.Entity.Top - Main.screenPosition + new Vector2(0f, -70f);
            for (int i = 0; i < lines.Count; i++)
            {
                Vector2 size = font.MeasureString(lines[i]) * 0.75f;
                Terraria.Utils.DrawBorderString(Main.spriteBatch, lines[i],
                    anchor + new Vector2(-size.X * 0.5f, i * 16f), colors[i], 0.75f);
            }
        }
    }

    /// <summary>Shows a live health bar below companions when they can take damage.</summary>
    private static void DrawCompanionHealthBars()
    {
        if (!CanDrawCompanionHealthBars())
            return;

        foreach (CompanionInstance instance in CompanionEntitySystem.All)
        {
            Player companion = instance.Entity;
            if (instance.OwnerWhoAmI != Main.myPlayer || !companion.active || companion.dead || companion.statLife <= 0)
                continue;

            int maxLife = Math.Max(1, companion.statLifeMax2 > 0 ? companion.statLifeMax2 : companion.statLifeMax);
            float ratio = MathHelper.Clamp(companion.statLife / (float)maxLife, 0f, 1f);
            Vector2 anchor = companion.Bottom - Main.screenPosition + new Vector2(0f, 8f);
            const int barWidth = 46;
            const int barHeight = 6;
            Rectangle border = new((int)(anchor.X - barWidth * 0.5f) - 1, (int)anchor.Y - 1, barWidth + 2, barHeight + 2);
            Rectangle background = new(border.X + 1, border.Y + 1, barWidth, barHeight);
            Rectangle fill = new(background.X, background.Y, (int)(barWidth * ratio), barHeight);

            Color fillColor = ratio > 0.5f
                ? Color.Lerp(new Color(245, 190, 65), new Color(80, 220, 105), (ratio - 0.5f) * 2f)
                : Color.Lerp(new Color(220, 55, 55), new Color(245, 190, 65), ratio * 2f);
            Main.spriteBatch.Draw(Terraria.GameContent.TextureAssets.MagicPixel.Value, border, new Color(10, 12, 16, 215));
            Main.spriteBatch.Draw(Terraria.GameContent.TextureAssets.MagicPixel.Value, background, new Color(95, 28, 30, 210));
            if (fill.Width > 0)
                Main.spriteBatch.Draw(Terraria.GameContent.TextureAssets.MagicPixel.Value, fill, fillColor);
        }
    }

    private static bool CanDrawCompanionHealthBars() =>
        !Main.gameMenu && Main.netMode == NetmodeID.SinglePlayer &&
        MoreAIPlayerClientConfig.Current?.ShowCompanionHealthBars == true;

    /// <summary>Small Calamity-style stealth meter under each companion wearing rogue armor.</summary>
    private static void DrawStealthBars()
    {
        if (Main.gameMenu || Main.netMode != Terraria.ID.NetmodeID.SinglePlayer)
            return;

        foreach (CompanionInstance instance in CompanionEntitySystem.All)
        {
            if (instance.OwnerWhoAmI != Main.myPlayer || !instance.Entity.active ||
                instance.Entity.dead || instance.Entity.statLife <= 0)
                continue;
            if (!CompanionModBridge.TryGetRogueStealth(instance.Entity, out float stealth, out float stealthMax) || stealthMax <= 0f)
                continue;

            float ratio = MathHelper.Clamp(stealth / stealthMax, 0f, 1f);
            // The health bar reserves the first row below a damageable companion. Rogue
            // stealth stays readable instead of painting directly over it.
            float verticalOffset = CanDrawCompanionHealthBars() ? 17f : 8f;
            Vector2 anchor = instance.Entity.Bottom - Main.screenPosition + new Vector2(0f, verticalOffset);
            const int barWidth = 42;
            const int barHeight = 6;
            Rectangle background = new((int)(anchor.X - barWidth * 0.5f) - 1, (int)anchor.Y - 1, barWidth + 2, barHeight + 2);
            Rectangle fill = new(background.X + 1, background.Y + 1, (int)(barWidth * ratio), barHeight);

            Color fillColor = ratio >= 0.995f
                ? new Color(255, 240, 120)
                : Color.Lerp(new Color(110, 90, 40), new Color(230, 195, 80), ratio);
            Main.spriteBatch.Draw(Terraria.GameContent.TextureAssets.MagicPixel.Value, background, new Color(12, 14, 20, 200));
            Main.spriteBatch.Draw(Terraria.GameContent.TextureAssets.MagicPixel.Value, fill, fillColor);
        }
    }

    /// <summary>Revive countdown floating over the owner's head while any of their companions
    /// are waiting to come back. One line per reviving companion.</summary>
    private static void DrawReviveTimers()
    {
        if (Main.gameMenu || Main.netMode != Terraria.ID.NetmodeID.SinglePlayer ||
            MoreAIPlayerClientConfig.Current?.UseChatForCompanionReviveTimer == true)
            return;

        Player owner = Main.LocalPlayer;
        var font = Terraria.GameContent.FontAssets.MouseText.Value;
        const float scale = 0.9f;
        int row = 0;
        foreach (CompanionInstance companion in CompanionEntitySystem.All)
        {
            if (companion.OwnerWhoAmI != Main.myPlayer || companion.ReviveTicksLeft <= 0)
                continue;

            int seconds = (int)Math.Ceiling(companion.ReviveTicksLeft / 60f);
            string name = string.IsNullOrWhiteSpace(companion.Entity?.name) ? T("DefaultName") : companion.Entity.name;
            string text = Terraria.Localization.Language.GetTextValue("Mods.MoreAIPlayer.CompanionCommand.Reviving", name, seconds);

            Vector2 size = font.MeasureString(text) * scale;
            Vector2 top = owner.Top - Main.screenPosition + new Vector2(0f, -38f - row * 22f);
            Terraria.Utils.DrawBorderString(Main.spriteBatch, text, top - new Vector2(size.X * 0.5f, 0f), new Color(255, 120, 110), scale);
            row++;
        }
    }

    /// <summary>
    /// A compact diegetic tracker: every trial states its win condition first, then the nearest
    /// threat. Breakout additionally gets a code-drawn edge marker, requiring no new asset.
    /// </summary>
    private static void DrawHunterTracker()
    {
        if (Main.gameMenu || Main.netMode != NetmodeID.SinglePlayer || !HunterSystem.HunterModeActive)
            return;

        Player owner = Main.LocalPlayer;
        if (owner is null || !owner.active)
            return;

        var font = Terraria.GameContent.FontAssets.MouseText.Value;
        const float scale = 0.82f;
        string trialName = Language.GetTextValue(
            $"Mods.MoreAIPlayer.CompanionCommand.{HunterSystem.TrialKey(HunterSystem.ActiveTrial)}");
        string title = T("HunterTrackerTitle", trialName);
        string objective;
        string detail;
        Color detailColor;

        if (HunterSystem.IsPreparing)
        {
            objective = T("HunterTrackerPreparing", HunterSystem.PreparationSeconds);
            detail = T("HunterTrackerSearching");
            detailColor = new Color(255, 205, 110);
        }
        else
        {
            objective = HunterSystem.ActiveTrial switch
            {
                HunterTrialType.Elimination => T("HunterTrackerObjectiveElimination",
                    HunterSystem.DefeatedHunters, HunterSystem.EliminationTarget),
                HunterTrialType.Breakout when HunterSystem.TryGetBreakoutDestination(out Vector2 destination) =>
                    T("HunterTrackerObjectiveBreakout",
                        System.Math.Max(0, (int)System.MathF.Ceiling(Vector2.Distance(owner.Center, destination) / 16f)),
                        HunterSystem.ObjectiveSecondsRemaining),
                _ => T("HunterTrackerObjectiveSurvival", HunterSystem.ObjectiveSecondsRemaining)
            };
        }

        if (HunterSystem.TryGetNearestHunter(owner, out HunterInstance hunter, out float distance))
        {
            int tiles = System.Math.Max(1, (int)System.MathF.Ceiling(distance / 16f));
            if (hunter.Entity.dead || hunter.Entity.statLife <= 0)
            {
                int seconds = System.Math.Max(1, (int)System.Math.Ceiling(hunter.ReviveTicksLeft / 60f));
                detail = T("HunterTrackerReviving", seconds);
                detailColor = new Color(255, 150, 110);
            }
            else if (hunter.Brain.State == HunterState.Resting)
            {
                detail = T("HunterTrackerResting", tiles);
                detailColor = new Color(255, 215, 120);
            }
            else
            {
                detail = T("HunterTrackerNearest", tiles);
                detailColor = new Color(255, 120, 110);
            }
        }
        else
        {
            detail = T("HunterTrackerSearching");
            detailColor = new Color(220, 210, 180);
        }

        Vector2 anchor = owner.Top - Main.screenPosition + new Vector2(0f, -66f);
        Vector2 titleSize = font.MeasureString(title) * scale;
        Vector2 objectiveSize = font.MeasureString(objective) * scale;
        Vector2 detailSize = font.MeasureString(detail) * scale;
        Terraria.Utils.DrawBorderString(Main.spriteBatch, title,
            anchor - new Vector2(titleSize.X * 0.5f, 0f), new Color(255, 95, 95), scale);
        Terraria.Utils.DrawBorderString(Main.spriteBatch, objective,
            anchor + new Vector2(-objectiveSize.X * 0.5f, 17f), new Color(255, 225, 145), scale);
        Terraria.Utils.DrawBorderString(Main.spriteBatch, detail,
            anchor + new Vector2(-detailSize.X * 0.5f, 34f), detailColor, scale);

        DrawBreakoutMarker(owner);
    }

    private static void DrawBreakoutMarker(Player owner)
    {
        if (!HunterSystem.TryGetBreakoutDestination(out Vector2 destination))
            return;

        Vector2 screenPoint = destination - Main.screenPosition;
        const float margin = 42f;
        bool onScreen = screenPoint.X >= margin && screenPoint.X <= Main.screenWidth - margin &&
                        screenPoint.Y >= margin && screenPoint.Y <= Main.screenHeight - margin;
        Vector2 marker = new(
            MathHelper.Clamp(screenPoint.X, margin, Main.screenWidth - margin),
            MathHelper.Clamp(screenPoint.Y, margin, Main.screenHeight - margin));
        int tiles = System.Math.Max(0, (int)System.MathF.Ceiling(Vector2.Distance(owner.Center, destination) / 16f));
        string text = T(onScreen ? "HunterBreakoutMarkerHere" : "HunterBreakoutMarker", tiles);
        float pulse = 0.72f + 0.28f * System.MathF.Sin(Main.GlobalTimeWrappedHourly * 5f);
        Color color = new Color(110, 255, 190) * pulse;
        Vector2 size = Terraria.GameContent.FontAssets.MouseText.Value.MeasureString(text) * 0.8f;
        Vector2 position = marker - new Vector2(size.X * 0.5f, size.Y * 0.5f);
        Terraria.Utils.DrawBorderString(Main.spriteBatch, text, position, color, 0.8f);
    }

    public override void ModifyInterfaceLayers(List<GameInterfaceLayer> layers)
    {
        int mouseTextIndex = layers.FindIndex(layer => layer.Name.Equals("Vanilla: Mouse Text", StringComparison.Ordinal));
        if (mouseTextIndex < 0)
            return;

        layers.Insert(mouseTextIndex, new LegacyGameInterfaceLayer(
            "MoreAIPlayer: Companion Overlays",
            () =>
            {
                return CompanionFaultGuard.RunDraw("companion overlays", () =>
                {
                    DrawCompanionHealthBars();
                    DrawStealthBars();
                    DrawReviveTimers();
                    DrawDiagnostics();
                    DrawHunterTracker();
                });
            },
            InterfaceScaleType.Game));

        layers.Insert(mouseTextIndex, new LegacyGameInterfaceLayer(
            "MoreAIPlayer: Phantom Command Wheel",
            () =>
            {
                PhantomCommandWheel.Draw();
                return true;
            },
            InterfaceScaleType.UI));

        layers.Insert(mouseTextIndex, new LegacyGameInterfaceLayer(
            "MoreAIPlayer: Companion Mode Wheel",
            () =>
            {
                CompanionModeWheel.Draw();
                return true;
            },
            InterfaceScaleType.UI));

        layers.Insert(mouseTextIndex, new LegacyGameInterfaceLayer(
            "MoreAIPlayer: Companion Builder Wheel",
            () =>
            {
                CompanionBuilderModeWheel.Draw();
                return true;
            },
            InterfaceScaleType.UI));

        layers.Insert(mouseTextIndex, new LegacyGameInterfaceLayer(
            "MoreAIPlayer: Companion Manager",
            () =>
            {
                CompanionManagerPanel.DrawAndInteract();
                return true;
            },
            InterfaceScaleType.UI));

        layers.Insert(mouseTextIndex, new LegacyGameInterfaceLayer(
            "MoreAIPlayer: Companion Quick Access",
            () =>
            {
                CompanionManagerPanel.DrawQuickAccessBar();
                return true;
            },
            InterfaceScaleType.UI));

        layers.Insert(mouseTextIndex, new LegacyGameInterfaceLayer(
            "MoreAIPlayer: Hunter Config",
            () =>
            {
                HunterConfigPanel.DrawAndInteract();
                return true;
            },
            InterfaceScaleType.UI));

        layers.Insert(mouseTextIndex, new LegacyGameInterfaceLayer(
            "MoreAIPlayer: Player Scale Matrix",
            () =>
            {
                PlayerScaleMatrixPanel.DrawAndInteract();
                return true;
            },
            InterfaceScaleType.UI));

        layers.Insert(mouseTextIndex, new LegacyGameInterfaceLayer(
            "MoreAIPlayer: Companion Hover",
            () =>
            {
                if (CompanionEntitySystem.HoveredSlot >= 0 && !Main.LocalPlayer.mouseInterface)
                {
                    Main.LocalPlayer.cursorItemIconEnabled = true;
                    Main.LocalPlayer.cursorItemIconID = ItemID.Chest;
                    string name = Main.player[CompanionEntitySystem.HoveredSlot].name;
                    Main.instance.MouseText(T("RightClickOpen", name));
                }
                return true;
            },
            InterfaceScaleType.UI));

        if (inventoryUI is null || userInterface is null)
            return;

        layers.Insert(mouseTextIndex, new LegacyGameInterfaceLayer(
            "MoreAIPlayer: Companion Inventory",
            () =>
            {
                try
                {
                    userInterface.Draw(Main.spriteBatch, new GameTime());
                }
                catch (Exception exception)
                {
                    QueuePanelRecovery("UI draw", exception);
                }
                return true;
            },
            InterfaceScaleType.UI));
    }

    private bool ShowPanel(CompanionProfile profile, int companionSlot, bool appearancePage, bool recoveryAttempt)
    {
        try
        {
            inventoryPanelPosition = (inventoryUI as ICompanionPanelState)?.PanelPosition ?? inventoryPanelPosition;
            inventoryUI?.Deactivate();
            inventoryUI = appearancePage
                ? new CompanionAppearanceUI(profile, companionSlot, inventoryPanelPosition)
                : new CompanionInventoryUI(profile, companionSlot, inventoryPanelPosition);
            inventoryUI.Activate();
            userInterface.SetState(inventoryUI);
            targetSlot = companionSlot;
            Main.playerInventory = true;
            return true;
        }
        catch (Exception exception)
        {
            if (recoveryAttempt)
            {
                SafeClearPanel();
                CompanionFaultGuard.ReportRecovered($"profile {profile.Id}, UI rebuild", exception, false);
            }
            else
            {
                targetSlot = companionSlot;
                recoverAppearancePage = appearancePage;
                QueuePanelRecovery($"profile {profile.Id}, UI open", exception);
            }
            return false;
        }
    }

    private void QueuePanelRecovery(string source, Exception exception)
    {
        if (pendingPanelRecovery)
            return;
        recoverAppearancePage = inventoryUI is CompanionAppearanceUI || recoverAppearancePage;
        pendingPanelRecovery = true;
        pendingRecoverySource = source;
        pendingRecoveryException = exception;
    }

    private void RecoverPanel()
    {
        pendingPanelRecovery = false;
        int slot = targetSlot;
        bool appearancePage = recoverAppearancePage;
        string source = pendingRecoverySource ?? $"slot {slot}, UI";
        Exception originalException = pendingRecoveryException;
        pendingRecoverySource = null;
        pendingRecoveryException = null;
        SafeClearPanel(keepTarget: true);

        CompanionInstance companion = CompanionEntitySystem.GetBySlot(slot);
        if (companion is null || !companion.TryGetContext(out Player owner, out CompanionProfile profile))
        {
            targetSlot = -1;
            if (originalException is not null)
                CompanionFaultGuard.ReportRecovered(source, originalException, false);
            return;
        }

        try
        {
            profile.RepairRuntimeState();
            companion.BindProfile(owner, profile);
            bool rebuilt = ShowPanel(profile, slot, appearancePage, recoveryAttempt: true);
            if (originalException is not null)
                CompanionFaultGuard.ReportRecovered(source, originalException, rebuilt);
        }
        catch (Exception exception)
        {
            SafeClearPanel();
            CompanionFaultGuard.ReportRecovered($"profile {profile.Id}, state rebind", exception, false);
        }
    }

    private void SafeClearPanel(bool keepTarget = false)
    {
        try { userInterface?.SetState(null); } catch { }
        try { inventoryUI?.Deactivate(); } catch { }
        inventoryUI = null;
        if (!keepTarget)
            targetSlot = -1;
    }

    private static string T(string key, params object[] args) =>
        Language.GetTextValue($"Mods.MoreAIPlayer.CompanionUI.{key}", args);
}
