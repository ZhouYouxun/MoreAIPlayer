using Microsoft.Xna.Framework;
using Terraria;
using Terraria.Audio;
using Terraria.GameContent.UI.Elements;
using Terraria.ID;
using Terraria.Localization;
using Terraria.ModLoader;
using Terraria.UI;

namespace MoreAIPlayer.Companion.UI;

public sealed class CompanionInventoryUI : UIState
{
    // Layout grid. Slots are 52px, so a 56px step leaves a 4px seam: tight enough that a row
    // reads as one group, wide enough that the accent borders do not touch. Groups are then
    // separated by GroupGap, which is what actually communicates the structure.
    private const float SlotStep = 56f;
    private const float Margin = 18f;
    private const float LabelGap = 18f;   // label baseline to the top of the slots it names
    private const float GroupGap = 18f;   // bottom of a group to the next group's label
    private const float ColumnTwo = 210f; // right-hand column, clear of the 3-wide equip block

    private readonly CompanionProfile data;
    private readonly int companionSlot;
    private readonly Vector2? initialPanelPosition;
    private DraggableUIPanel panel = null!;
    private UIText statsText = null!;
    private UIText titleText = null!;
    private UIPanel renameOverlay;
    private UIPanel renameDialog;
    private CompanionNameTextInput renameInput;
    private UIText renameErrorText;

    public CompanionInventoryUI(CompanionProfile data, int companionSlot, Vector2? initialPanelPosition = null)
    {
        this.data = data;
        this.companionSlot = companionSlot;
        this.initialPanelPosition = initialPanelPosition;
    }

    public Vector2? PanelPosition => panel is null ? null : panel.Position;

    public override void OnInitialize()
    {
        panel = new DraggableUIPanel();
        panel.Width.Set(630f, 0f);
        panel.Height.Set(752f, 0f);
        panel.HAlign = 0.5f;
        panel.VAlign = 0.5f;
        if (initialPanelPosition is Vector2 position)
            panel.SetPosition(position);
        panel.BackgroundColor = new Color(23, 31, 54, 238);
        panel.BorderColor = new Color(86, 162, 210, 255);
        Append(panel);

        string companionName = string.IsNullOrWhiteSpace(data.Name) ? T("DefaultName") : data.Name;
        titleText = new UIText(T("InventoryTitle", companionName), 1.05f, true);
        titleText.Left.Set(18f, 0f);
        titleText.Top.Set(12f, 0f);
        panel.Append(titleText);

        UITextPanel<string> closeButton = new(T("Close"), 0.8f, false);
        closeButton.Width.Set(64f, 0f);
        closeButton.Height.Set(30f, 0f);
        closeButton.Left.Set(-76f, 1f);
        closeButton.Top.Set(8f, 0f);
        closeButton.OnLeftClick += (_, _) => CompanionUISystem.Close();
        panel.Append(closeButton);

        UITextPanel<string> renameButton = new(T("Rename"), 0.8f, false);
        renameButton.Width.Set(76f, 0f);
        renameButton.Height.Set(30f, 0f);
        renameButton.Left.Set(-160f, 1f);
        renameButton.Top.Set(8f, 0f);
        renameButton.OnLeftClick += (_, _) => OpenRenameDialog();
        panel.Append(renameButton);

        // ── Group 1: worn equipment (left block) and accessories (right column) ──────────
        const float equipLabelY = 50f;
        const float equipTopY = equipLabelY + LabelGap;

        AddLabel(panel, T("Armor"), Margin, equipLabelY);
        AddVerticalSlots(panel, data.Armor, Margin, equipTopY,
            ItemSlot.Context.EquipArmor,
            (CompanionSlotRules.IsHeadArmor, T("HeadArmorOnly")),
            (CompanionSlotRules.IsBodyArmor, T("BodyArmorOnly")),
            (CompanionSlotRules.IsLegArmor, T("LegArmorOnly")));

        AddLabel(panel, T("Vanity"), Margin + SlotStep, equipLabelY);
        for (int i = 0; i < data.Vanity.Length; i++)
        {
            System.Func<Item, bool> rule;
            string rejection;
            switch (i)
            {
                case 0:
                    rule = CompanionSlotRules.IsHeadArmor;
                    rejection = T("HeadVanityOnly");
                    break;
                case 1:
                    rule = CompanionSlotRules.IsBodyArmor;
                    rejection = T("BodyVanityOnly");
                    break;
                default:
                    rule = CompanionSlotRules.IsLegArmor;
                    rejection = T("LegVanityOnly");
                    break;
            }
            panel.Append(CreateSlot(data.Vanity, i, rule, rejection, Margin + SlotStep, equipTopY + i * SlotStep,
                drawContext: ItemSlot.Context.EquipArmorVanity, emptySilhouetteIndex: 3 + i * 6));
        }

        AddLabel(panel, T("Dye"), Margin + SlotStep * 2f, equipLabelY);
        for (int i = 0; i < data.Dyes.Length; i++)
        {
            panel.Append(CreateSlot(data.Dyes, i, CompanionSlotRules.IsDye, T("DyeOnly"),
                Margin + SlotStep * 2f, equipTopY + i * SlotStep,
                drawContext: ItemSlot.Context.EquipDye));
        }

        AddLabel(panel, T("Accessories"), ColumnTwo, equipLabelY);
        for (int i = 0; i < data.Accessories.Length; i++)
        {
            int capturedIndex = i;
            panel.Append(CreateSlot(
                data.Accessories, i, CompanionSlotRules.IsAccessory, T("AccessoryOnly"),
                ColumnTwo + i * SlotStep, equipTopY,
                isLocked: () => capturedIndex >= CompanionSlotRules.UnlockedAccessorySlots(),
                lockedText: AccessoryLockReason(capturedIndex),
                drawContext: ItemSlot.Context.EquipAccessory));
        }

        // Tucked under the accessory row, filling the space beside the three equip columns.
        // Non-Chinese status labels are naturally wider; this scale keeps the six live rows
        // inside the accessory-side column without changing the panel's low-resolution fit.
        statsText = new UIText(string.Empty, 0.76f);
        statsText.Left.Set(ColumnTwo, 0f);
        statsText.Top.Set(equipTopY + SlotStep + 12f, 0f);
        panel.Append(statsText);

        // ── Group 2: weapon bar ──────────────────────────────────────────────────────────
        const float equipBottomY = equipTopY + SlotStep * 2f + CompanionItemSlot.SlotSize;
        const float weaponLabelY = equipBottomY + GroupGap;
        const float weaponTopY = weaponLabelY + LabelGap + 18f; // two label lines

        AddLabel(panel, T("WeaponBar"), Margin, weaponLabelY);
        AddLabel(panel, T("WeaponPriority"), Margin, weaponLabelY + 18f);
        for (int i = 0; i < CompanionPlayer.HotbarSize; i++)
        {
            int capturedIndex = i;
            bool isGuardSlot = i == CompanionProfile.GuardSlot;
            bool isSummonSlot = i == CompanionProfile.SummonSlot;
            bool isPickSlot = i == CompanionProfile.PickSlot;

            System.Func<Item, bool> rule = isGuardSlot ? CompanionSlotRules.IsGuardWeapon
                : isPickSlot ? CompanionSlotRules.IsPickaxe
                : isSummonSlot ? CompanionSlotRules.IsSummonWeapon
                : CompanionSlotRules.IsAllowedWeapon;
            string rejection = isGuardSlot ? T("GuardWeaponOnly")
                : isPickSlot ? T("PickaxeOnly")
                : isSummonSlot ? T("SummonWeaponOnly")
                : T("WeaponOnly");
            Color? accent = isGuardSlot ? CompanionItemSlot.GuardAccent
                : isPickSlot ? CompanionItemSlot.PickAccent
                : isSummonSlot ? CompanionItemSlot.SummonAccent
                : null;

            CompanionItemSlot slot = CreateSlot(
                data.Hotbar,
                i,
                rule,
                rejection,
                Margin + i * SlotStep,
                weaponTopY,
                () => data.ActiveWeaponSlot == capturedIndex,
                accent);
            panel.Append(slot);

            // 左/右 preference toggle, shown only while this slot holds a weapon that has
            // both a left and a right click and is not covered by a scripted special case.
            CompanionToggleButton altToggle = new(
                isVisible: () => CompanionWeaponOverrides.HasConfigurableAltUse(data.Hotbar[capturedIndex]),
                getLabel: () => data.PrefersAltUse(data.Hotbar[capturedIndex]) ? T("RightClick") : T("LeftClick"),
                onToggle: () => data.TogglePrefersAltUse(data.Hotbar[capturedIndex]),
                width: CompanionItemSlot.SlotSize,
                height: 22f,
                textScale: 0.68f);
            altToggle.Left.Set(Margin + i * SlotStep, 0f);
            altToggle.Top.Set(weaponTopY + CompanionItemSlot.SlotSize + 4f, 0f);
            panel.Append(altToggle);
        }

        // 潜伏/普攻 switch, visible while the companion wears Calamity rogue armor.
        CompanionToggleButton stealthToggle = new(
            isVisible: () =>
            {
                CompanionInstance companion = CompanionEntitySystem.GetBySlot(companionSlot);
                return companion is not null &&
                       CompanionModBridge.TryGetRogueStealth(companion.Entity, out _, out float max) && max > 0f;
            },
            getLabel: () => data.RogueStealthWait ? T("DecisionStealth") : T("DecisionNormal"),
            onToggle: () => data.RogueStealthWait = !data.RogueStealthWait,
            width: 132f,
            height: 30f,
            textScale: 0.78f);
        // Sits immediately left of the close button on the title row.
        stealthToggle.Left.Set(-300f, 1f);
        stealthToggle.Top.Set(8f, 0f);
        panel.Append(stealthToggle);

        // ── Group 3: ammo and the dedicated tool slots ───────────────────────────────────
        const float weaponBottomY = weaponTopY + CompanionItemSlot.SlotSize + 4f + 22f;
        const float ammoLabelY = weaponBottomY + GroupGap;
        const float ammoTopY = ammoLabelY + LabelGap;

        AddLabel(panel, T("Ammo"), Margin, ammoLabelY);
        for (int i = 0; i < data.Ammo.Length; i++)
        {
            panel.Append(CreateSlot(data.Ammo, i, CompanionSlotRules.IsAmmo,
                T("AmmoOnly"), Margin + i * SlotStep, ammoTopY));
        }

        // A half-slot seam separates tools from ammo while leaving room for all four dedicated
        // mobility/utility slots inside the panel.
        float toolColumn = Margin + (data.Ammo.Length + 0.5f) * SlotStep;
        AddLabel(panel, T("Tools"), toolColumn, ammoLabelY);
        panel.Append(CreateSlot(data.Net, 0, CompanionSlotRules.IsCatchingTool,
            T("NetOnly"), toolColumn, ammoTopY, emptyItemGhostType: ItemID.BugNet));
        panel.Append(CreateSlot(data.Rod, 0, CompanionSlotRules.IsFishingPole,
            T("RodOnly"), toolColumn + SlotStep, ammoTopY, emptyItemGhostType: ItemID.WoodFishingPole));
        panel.Append(CreateSlot(data.Hook, 0, CompanionSlotRules.IsGrapplingHook,
            T("HookOnly"), toolColumn + SlotStep * 2f, ammoTopY,
            drawContext: ItemSlot.Context.EquipGrapple));
        panel.Append(CreateSlot(data.Mount, 0, CompanionSlotRules.IsMount,
            T("MountOnly"), toolColumn + SlotStep * 3f, ammoTopY,
            drawContext: ItemSlot.Context.EquipMount));

        // ── Group 4: backpack ────────────────────────────────────────────────────────────
        const float backpackLabelY = ammoTopY + CompanionItemSlot.SlotSize + GroupGap;
        const float backpackTopY = backpackLabelY + LabelGap;

        AddLabel(panel, T("Backpack"), Margin, backpackLabelY);
        for (int i = 0; i < data.Backpack.Length; i++)
        {
            int column = i % 10;
            int row = i / 10;
            panel.Append(CreateSlot(data.Backpack, i, CompanionSlotRules.AcceptAnything, string.Empty,
                Margin + column * SlotStep, backpackTopY + row * SlotStep));
        }

        const float actionWidth = 126f;
        const float actionHeight = 28f;
        const float actionGap = 8f;
        const float backpackRows = 4f;
        const float actionTopY = backpackTopY + backpackRows * SlotStep + 4f;

        UITextPanel<string> dumpButton = new(
            Language.GetTextValue("Mods.MoreAIPlayer.CompanionCommand.DumpAll"), 0.74f, false);
        dumpButton.Width.Set(actionWidth, 0f);
        dumpButton.Height.Set(actionHeight, 0f);
        dumpButton.Left.Set(-Margin - actionWidth * 2f - actionGap, 1f);
        dumpButton.Top.Set(actionTopY, 0f);
        dumpButton.OnLeftClick += (_, _) =>
        {
            if (CompanionLootSystem.DumpBackpack(companionSlot, Main.LocalPlayer) > 0)
                SoundEngine.PlaySound(SoundID.Grab);
        };
        panel.Append(dumpButton);

        UITextPanel<string> transferButton = new(
            Language.GetTextValue("Mods.MoreAIPlayer.CompanionCommand.TransferAll"), 0.74f, false);
        transferButton.Width.Set(actionWidth, 0f);
        transferButton.Height.Set(actionHeight, 0f);
        transferButton.Left.Set(-Margin - actionWidth, 1f);
        transferButton.Top.Set(actionTopY, 0f);
        transferButton.OnLeftClick += (_, _) =>
        {
            if (CompanionLootSystem.TransferBackpack(companionSlot, Main.LocalPlayer) > 0)
                SoundEngine.PlaySound(SoundID.Grab);
        };
        panel.Append(transferButton);
    }

    public override void Update(GameTime gameTime)
    {
        // UserInterface.Update runs before vanilla's inventory layers are drawn. The panel must
        // claim normal UI focus before those layers so a click cannot be used in the world or on
        // a vanilla control behind it. Do not set Main.blockMouse here: it is a broader global
        // input gate, and keeping it live over this whole panel makes vanilla skip its item-hover
        // and transfer-feedback paths for an unpredictable number of later frames.
        if (renameDialog is not null || (panel is not null && panel.ContainsPoint(Main.MouseScreen)))
            Main.LocalPlayer.mouseInterface = true;

        base.Update(gameTime);

        Item activeWeapon = data.ActiveWeapon;
        string weaponName = activeWeapon.IsAir
            ? T("WaitingForTarget")
            : data.ActiveWeaponSlot switch
            {
                CompanionProfile.GuardSlot => T("GuardSlotWeapon", activeWeapon.Name),
                CompanionProfile.SummonSlot => T("SummonSlotWeapon", activeWeapon.Name),
                _ => T("NumberedSlotWeapon", data.ActiveWeaponSlot + 1, activeWeapon.Name)
            };

        // Surfaces the failure this panel used to hide: with every weapon parked in a reserved
        // slot the companion still fights (SelectWeapon falls back), but at the wrong priority.
        // Silence here is what let that state go unnoticed for so long.
        string warning = HasRotationWeapon() ? string.Empty : "\n" + T("NoRotationWeapon");

        // The companion is a real player entity, so its stats come straight from the
        // vanilla equipment pipeline instead of a hand-rolled approximation.
        CompanionInstance companion = CompanionEntitySystem.GetBySlot(companionSlot);
        if (companion is not null)
        {
            Player entity = companion.Entity;
            // Live decision readout. "The companion is not attacking" has had several different
            // causes that all look identical in game, so the brain reports which one applies
            // instead of leaving it to be guessed at from the outside.
            string state = AI.CompanionAttackStateText.Describe(companion.Brain.AttackState);

            // The active mode belongs here for the same reason. Modes changed behaviour
            // drastically and were switched by a wheel that left no lasting trace on screen, so
            // a mode picked by accident looked exactly like the companion being broken.
            CompanionMode effective = CompanionModeRules.Effective(data);
            string modeLine = effective == data.Mode
                ? CompanionModeRules.Name(effective)
                : T("ModeFallback",
                    CompanionModeRules.Name(effective),
                    CompanionModeRules.Name(data.Mode),
                    CompanionModeRules.RequirementText(data.Mode));

            statsText.SetText(
                T("StatsMode", modeLine) + "\n" +
                T("StatsWeapon", weaponName) + "\n" +
                T("StatsState", state) + "\n" +
                T("StatsAvoidance", companion.Brain.AvoidanceStateText,
                    companion.Brain.VisibleThreats, companion.Brain.AvoidanceRisk) + "\n" +
                T("StatsLife", entity.statLife, entity.statLifeMax2, (int)entity.statDefense) + "\n" +
                T("StatsDamage",
                    entity.GetTotalDamage(DamageClass.Melee).ApplyTo(1f),
                    entity.GetTotalDamage(DamageClass.Ranged).ApplyTo(1f),
                    entity.GetTotalDamage(DamageClass.Magic).ApplyTo(1f)) +
                warning);
        }
        else
        {
            statsText.SetText(T("StatsWeapon", weaponName) + warning);
        }
    }

    private void OpenRenameDialog()
    {
        if (renameDialog is not null)
            return;

        // The dimmer is a real full-screen UI element, not just a draw effect: it keeps clicks
        // on the loadout below from changing gear while the player is naming the companion.
        renameOverlay = new UIPanel();
        renameOverlay.Width.Set(0f, 1f);
        renameOverlay.Height.Set(0f, 1f);
        renameOverlay.BackgroundColor = new Color(0, 0, 0, 126);
        renameOverlay.BorderColor = Color.Transparent;
        renameOverlay.OnLeftClick += (_, _) => Main.LocalPlayer.mouseInterface = true;
        Append(renameOverlay);

        renameDialog = new UIPanel();
        renameDialog.Width.Set(392f, 0f);
        renameDialog.Height.Set(162f, 0f);
        renameDialog.HAlign = 0.5f;
        renameDialog.VAlign = 0.5f;
        renameDialog.BackgroundColor = new Color(17, 25, 43, 250);
        renameDialog.BorderColor = new Color(104, 180, 235);
        Append(renameDialog);

        UIText heading = new(T("RenameTitle", DisplayName()), 0.94f, true);
        heading.Left.Set(18f, 0f);
        heading.Top.Set(14f, 0f);
        renameDialog.Append(heading);

        UIText prompt = new(T("RenamePrompt"), 0.72f);
        prompt.Left.Set(18f, 0f);
        prompt.Top.Set(42f, 0f);
        renameDialog.Append(prompt);

        renameInput = new CompanionNameTextInput(data.Name);
        renameInput.Width.Set(-36f, 1f);
        renameInput.Height.Set(36f, 0f);
        renameInput.Left.Set(18f, 0f);
        renameInput.Top.Set(62f, 0f);
        renameDialog.Append(renameInput);

        renameErrorText = new UIText(string.Empty, 0.62f);
        renameErrorText.Left.Set(18f, 0f);
        renameErrorText.Top.Set(102f, 0f);
        renameDialog.Append(renameErrorText);

        UITextPanel<string> cancelButton = new(T("RenameCancel"), 0.7f, false);
        cancelButton.Width.Set(82f, 0f);
        cancelButton.Height.Set(28f, 0f);
        cancelButton.Left.Set(-188f, 1f);
        cancelButton.Top.Set(-38f, 1f);
        cancelButton.OnLeftClick += (_, _) => CloseRenameDialog();
        renameDialog.Append(cancelButton);

        UITextPanel<string> saveButton = new(T("RenameSave"), 0.7f, false);
        saveButton.Width.Set(82f, 0f);
        saveButton.Height.Set(28f, 0f);
        saveButton.Left.Set(-98f, 1f);
        saveButton.Top.Set(-38f, 1f);
        saveButton.OnLeftClick += (_, _) => SaveRename();
        renameDialog.Append(saveButton);
    }

    private void SaveRename()
    {
        if (renameInput is null || string.IsNullOrWhiteSpace(renameInput.Text))
        {
            renameErrorText?.SetText(T("RenameEmpty"));
            return;
        }

        data.Rename(renameInput.Text);
        CompanionInstance companion = CompanionEntitySystem.GetBySlot(companionSlot);
        if (companion is not null)
            companion.Entity.name = data.Name;
        titleText?.SetText(T("InventoryTitle", DisplayName()));
        SoundEngine.PlaySound(SoundID.MenuTick with { Volume = 0.6f, Pitch = 0.16f });
        CloseRenameDialog();
    }

    private void CloseRenameDialog()
    {
        if (renameDialog is null)
            return;

        RemoveChild(renameDialog);
        RemoveChild(renameOverlay);
        renameOverlay = null;
        renameDialog = null;
        renameInput = null;
        renameErrorText = null;
    }

    private string DisplayName() => string.IsNullOrWhiteSpace(data.Name) ? T("DefaultName") : data.Name;

    /// <summary>True when at least one ordinary rotation slot holds something usable as a weapon.</summary>
    private bool HasRotationWeapon()
    {
        for (int slot = CompanionProfile.FirstCombatSlot; slot < CompanionProfile.EndCombatSlot; slot++)
        {
            Item candidate = data.Hotbar[slot];
            if (!candidate.IsAir && candidate.damage > 0 && candidate.pick <= 0 && candidate.axe <= 0 && candidate.hammer <= 0)
                return true;
        }
        return false;
    }

    /// <summary>Explains why a given accessory slot is greyed out, in terms of what would unlock it.</summary>
    private static string AccessoryLockReason(int index) => index switch
    {
        5 => T("AccessorySlotSixLocked"),
        6 => T("AccessorySlotSevenLocked"),
        _ => T("AccessorySlotLocked")
    };

    private static string T(string key, params object[] args) =>
        Language.GetTextValue($"Mods.MoreAIPlayer.CompanionUI.{key}", args);

    private static CompanionItemSlot CreateSlot(Item[] items, int index, System.Func<Item, bool> rule, string rejectionText, float left, float top,
        System.Func<bool> isHighlighted = null, Color? accentColor = null, System.Func<bool> isLocked = null, string lockedText = null,
        int drawContext = ItemSlot.Context.BankItem, int emptyItemGhostType = ItemID.None, int emptySilhouetteIndex = -1)
    {
        CompanionItemSlot slot = new(items, index, rule, rejectionText, isHighlighted, accentColor, isLocked, lockedText,
            drawContext, emptyItemGhostType, emptySilhouetteIndex);
        slot.Left.Set(left, 0f);
        slot.Top.Set(top, 0f);
        return slot;
    }

    private static void AddVerticalSlots(UIElement panel, Item[] items, float left, float top, int drawContext,
        params (System.Func<Item, bool> Rule, string Rejection)[] rules)
    {
        for (int i = 0; i < items.Length; i++)
            panel.Append(CreateSlot(items, i, rules[i].Rule, rules[i].Rejection, left, top + i * SlotStep,
                drawContext: drawContext));
    }

    private static void AddLabel(UIElement panel, string text, float left, float top)
    {
        UIText label = new(text, 0.8f);
        label.Left.Set(left, 0f);
        label.Top.Set(top, 0f);
        panel.Append(label);
    }
}
