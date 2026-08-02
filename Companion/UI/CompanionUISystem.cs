using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
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
    private CompanionInventoryUI inventoryUI;
    private int targetSlot = -1;
    private Vector2? inventoryPanelPosition;

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
        CompanionManagerPanel.CloseInstant();
        HunterConfigPanel.CloseInstant();
        PlayerScaleMatrixPanel.CloseInstant();
        instance = null;
        userInterface = null;
        inventoryUI = null;
        targetSlot = -1;
        inventoryPanelPosition = null;
    }

    public override void OnWorldUnload()
    {
        Close();
        CompanionModeWheel.Close();
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

        instance.inventoryPanelPosition = instance.inventoryUI?.PanelPosition ?? instance.inventoryPanelPosition;
        instance.inventoryUI = new CompanionInventoryUI(profile, companionSlot, instance.inventoryPanelPosition);
        instance.inventoryUI.Activate();
        instance.userInterface.SetState(instance.inventoryUI);
        instance.targetSlot = companionSlot;
        Main.playerInventory = true;
    }

    public static void Close()
    {
        if (instance?.userInterface is null)
            return;

        instance.userInterface.SetState(null);
        instance.inventoryPanelPosition = instance.inventoryUI?.PanelPosition ?? instance.inventoryPanelPosition;
        instance.inventoryUI = null;
        instance.targetSlot = -1;
    }

    public override void UpdateUI(GameTime gameTime)
    {
        CompanionModeWheel.UpdateInput();

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

        userInterface.Update(gameTime);
    }

    /// <summary>
    /// Identifies exactly which build is running. Bumped by hand on every change so that
    /// "nothing I changed had any effect" can be told apart from "the change did not help" —
    /// three rounds of fixes were evaluated without that distinction being available.
    /// </summary>
    public const string BuildStamp = "2026-08-02 #22 world-item-pickup-gate";

    /// <summary>Draws a live decision readout over each companion only when the player has
    /// explicitly enabled the client-side diagnostic configuration.</summary>
    public static bool ShowDiagnostics => MoreAIPlayerClientConfig.Instance?.EnablePhantomPlayerDebugText == true;

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

            string[] lines = [modeLine, stateLine, itemCheckLine, aimLine, recoveryLine, ownershipLine];
            Color[] colors =
            [
                new Color(200, 210, 235),
                new Color(255, 225, 150),
                detourLive ? new Color(150, 235, 160) : new Color(255, 110, 100),
                new Color(180, 200, 230),
                instance.ConsecutiveItemCheckMisses == 0 ? new Color(150, 235, 160) : new Color(255, 180, 100),
                new Color(160, 205, 235)
            ];

            Vector2 anchor = instance.Entity.Top - Main.screenPosition + new Vector2(0f, -70f);
            for (int i = 0; i < lines.Length; i++)
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
        MoreAIPlayerClientConfig.Instance?.CompanionsTakeDamage != false;

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
        if (Main.gameMenu || Main.netMode != Terraria.ID.NetmodeID.SinglePlayer)
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
    /// A compact diegetic tracker: Hunter Game begins with a distant warning rather than an
    /// instant ambush, then keeps the nearest threat's distance visible beside the player.
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
        string stageKey = HunterSystem.ActiveStage == HunterGameStage.PreHardmode
            ? "HunterStagePreHardmode"
            : "HunterStageEarlyHardmode";
        string stageName = Language.GetTextValue($"Mods.MoreAIPlayer.CompanionCommand.{stageKey}");
        string title = T("HunterTrackerTitle", stageName);
        string detail;
        Color detailColor;

        if (HunterSystem.IsPreparing)
        {
            detail = T("HunterTrackerPreparing", HunterSystem.PreparationSeconds);
            detailColor = new Color(255, 205, 110);
        }
        else if (HunterSystem.TryGetNearestHunter(owner, out HunterInstance hunter, out float distance))
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
        Vector2 detailSize = font.MeasureString(detail) * scale;
        Terraria.Utils.DrawBorderString(Main.spriteBatch, title,
            anchor - new Vector2(titleSize.X * 0.5f, 0f), new Color(255, 95, 95), scale);
        Terraria.Utils.DrawBorderString(Main.spriteBatch, detail,
            anchor + new Vector2(-detailSize.X * 0.5f, 17f), detailColor, scale);
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
                DrawCompanionHealthBars();
                DrawStealthBars();
                DrawReviveTimers();
                DrawDiagnostics();
                DrawHunterTracker();
                return true;
            },
            InterfaceScaleType.Game));

        layers.Insert(mouseTextIndex, new LegacyGameInterfaceLayer(
            "MoreAIPlayer: Companion Mode Wheel",
            () =>
            {
                CompanionModeWheel.Draw();
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
                userInterface.Draw(Main.spriteBatch, new GameTime());
                return true;
            },
            InterfaceScaleType.UI));
    }

    private static string T(string key, params object[] args) =>
        Language.GetTextValue($"Mods.MoreAIPlayer.CompanionUI.{key}", args);
}
