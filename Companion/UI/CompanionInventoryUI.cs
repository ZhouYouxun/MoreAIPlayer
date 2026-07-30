using Microsoft.Xna.Framework;
using Terraria;
using Terraria.GameContent.UI.Elements;
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
    private const float LabelGap = 20f;   // label baseline to the top of the slots it names
    private const float GroupGap = 28f;   // bottom of a group to the next group's label
    private const float ColumnTwo = 210f; // right-hand column, clear of the 3-wide equip block

    private readonly CompanionProfile data;
    private readonly int companionSlot;
    private UIText statsText = null!;

    public CompanionInventoryUI(CompanionProfile data, int companionSlot)
    {
        this.data = data;
        this.companionSlot = companionSlot;
    }

    public override void OnInitialize()
    {
        DraggableUIPanel panel = new();
        panel.Width.Set(630f, 0f);
        panel.Height.Set(752f, 0f);
        panel.HAlign = 0.5f;
        panel.VAlign = 0.5f;
        panel.BackgroundColor = new Color(23, 31, 54, 238);
        panel.BorderColor = new Color(86, 162, 210, 255);
        Append(panel);

        string companionName = string.IsNullOrWhiteSpace(data.Name) ? "幻影伙伴" : data.Name;
        UIText title = new($"{companionName} · 装备与背包", 1.05f, true);
        title.Left.Set(18f, 0f);
        title.Top.Set(12f, 0f);
        panel.Append(title);

        UITextPanel<string> closeButton = new("关闭", 0.8f, false);
        closeButton.Width.Set(64f, 0f);
        closeButton.Height.Set(30f, 0f);
        closeButton.Left.Set(-76f, 1f);
        closeButton.Top.Set(8f, 0f);
        closeButton.OnLeftClick += (_, _) => CompanionUISystem.Close();
        panel.Append(closeButton);

        // ── Group 1: worn equipment (left block) and accessories (right column) ──────────
        const float equipLabelY = 50f;
        const float equipTopY = equipLabelY + LabelGap;

        AddLabel(panel, "盔甲", Margin, equipLabelY);
        AddVerticalSlots(panel, data.Armor, Margin, equipTopY,
            (CompanionSlotRules.IsHeadArmor, "这里只能放头部盔甲"),
            (CompanionSlotRules.IsBodyArmor, "这里只能放身体盔甲"),
            (CompanionSlotRules.IsLegArmor, "这里只能放腿部盔甲"));

        AddLabel(panel, "时装", Margin + SlotStep, equipLabelY);
        AddVerticalSlots(panel, data.Vanity, Margin + SlotStep, equipTopY,
            (CompanionSlotRules.IsHeadArmor, "这里只能放头部时装"),
            (CompanionSlotRules.IsBodyArmor, "这里只能放身体时装"),
            (CompanionSlotRules.IsLegArmor, "这里只能放腿部时装"));

        AddLabel(panel, "染料", Margin + SlotStep * 2f, equipLabelY);
        for (int i = 0; i < data.Dyes.Length; i++)
        {
            panel.Append(CreateSlot(data.Dyes, i, CompanionSlotRules.IsDye, "这里只能放染料",
                Margin + SlotStep * 2f, equipTopY + i * SlotStep));
        }

        AddLabel(panel, "饰品（栏位随难度与肉山进度自动解锁，无需恶魔之心）", ColumnTwo, equipLabelY);
        for (int i = 0; i < data.Accessories.Length; i++)
        {
            int capturedIndex = i;
            panel.Append(CreateSlot(
                data.Accessories, i, CompanionSlotRules.IsAccessory, "这里只能放饰品",
                ColumnTwo + i * SlotStep, equipTopY,
                isLocked: () => capturedIndex >= CompanionSlotRules.UnlockedAccessorySlots(),
                lockedText: AccessoryLockReason(capturedIndex)));
        }

        // Tucked under the accessory row, filling the space beside the three equip columns.
        statsText = new UIText(string.Empty, 0.82f);
        statsText.Left.Set(ColumnTwo, 0f);
        statsText.Top.Set(equipTopY + SlotStep + 12f, 0f);
        panel.Append(statsText);

        // ── Group 2: weapon bar ──────────────────────────────────────────────────────────
        const float equipBottomY = equipTopY + SlotStep * 2f + CompanionItemSlot.SlotSize;
        const float weaponLabelY = equipBottomY + GroupGap;
        const float weaponTopY = weaponLabelY + LabelGap + 18f; // two label lines

        AddLabel(panel, "武器栏　　蓝＝防身位　灰＝镐位　黄＝召唤位", Margin, weaponLabelY);
        AddLabel(panel, "中间 7 格为普通武器，优先级：左 → 右", Margin, weaponLabelY + 18f);
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
            string rejection = isGuardSlot ? "防身位：可放任意武器（镐、斧、锤除外）"
                : isPickSlot ? "镐位：只能放镐（兼具斧或锤的多功能工具不收）"
                : isSummonSlot ? "召唤位：只能放召唤杖或哨兵杖（鞭子不算）"
                : "这里可放武器（含召唤武器）、镐、斧或锤";
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
                getLabel: () => data.PrefersAltUse(data.Hotbar[capturedIndex]) ? "右键" : "左键",
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
            getLabel: () => data.RogueStealthWait ? "决策：潜伏" : "决策：普攻",
            onToggle: () => data.RogueStealthWait = !data.RogueStealthWait,
            width: 132f,
            height: 30f,
            textScale: 0.78f);
        // Sits immediately left of the close button on the title row.
        stealthToggle.Left.Set(-224f, 1f);
        stealthToggle.Top.Set(8f, 0f);
        panel.Append(stealthToggle);

        // ── Group 3: ammo and the dedicated tool slots ───────────────────────────────────
        const float weaponBottomY = weaponTopY + CompanionItemSlot.SlotSize + 4f + 22f;
        const float ammoLabelY = weaponBottomY + GroupGap;
        const float ammoTopY = ammoLabelY + LabelGap;

        AddLabel(panel, "弹药（前 4 格优先，后 2 格为扩展位）", Margin, ammoLabelY);
        for (int i = 0; i < data.Ammo.Length; i++)
        {
            panel.Append(CreateSlot(data.Ammo, i, CompanionSlotRules.IsAmmo,
                "这里只能放箭、子弹等弹药", Margin + i * SlotStep, ammoTopY));
        }

        // Set apart from the ammo row by a full slot of empty space: these are tool slots that
        // gate whole modes, not more ammo.
        float toolColumn = Margin + (data.Ammo.Length + 1) * SlotStep;
        AddLabel(panel, "捕虫网 / 鱼竿", toolColumn, ammoLabelY);
        panel.Append(CreateSlot(data.Net, 0, CompanionSlotRules.IsCatchingTool,
            "这里只能放捕虫网、地狱捕虫网或金虫网", toolColumn, ammoTopY));
        panel.Append(CreateSlot(data.Rod, 0, CompanionSlotRules.IsFishingPole,
            "这里只能放钓鱼竿", toolColumn + SlotStep, ammoTopY));

        // ── Group 4: backpack ────────────────────────────────────────────────────────────
        const float backpackLabelY = ammoTopY + CompanionItemSlot.SlotSize + GroupGap;
        const float backpackTopY = backpackLabelY + LabelGap;

        AddLabel(panel, "背包", Margin, backpackLabelY);
        for (int i = 0; i < data.Backpack.Length; i++)
        {
            int column = i % 10;
            int row = i / 10;
            panel.Append(CreateSlot(data.Backpack, i, CompanionSlotRules.AcceptAnything, string.Empty,
                Margin + column * SlotStep, backpackTopY + row * SlotStep));
        }
    }

    public override void Update(GameTime gameTime)
    {
        base.Update(gameTime);

        Item activeWeapon = data.ActiveWeapon;
        string weaponName = activeWeapon.IsAir
            ? "等待目标"
            : data.ActiveWeaponSlot switch
            {
                CompanionProfile.GuardSlot => $"防身位 · {activeWeapon.Name}",
                CompanionProfile.SummonSlot => $"召唤位 · {activeWeapon.Name}",
                _ => $"{data.ActiveWeaponSlot + 1} 号位 · {activeWeapon.Name}"
            };

        // Surfaces the failure this panel used to hide: with every weapon parked in a reserved
        // slot the companion still fights (SelectWeapon falls back), but at the wrong priority.
        // Silence here is what let that state go unnoticed for so long.
        string warning = HasRotationWeapon() ? string.Empty : "\n注意：3–9 号位没有普通武器，正在动用保留位应急";

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
                : $"{CompanionModeRules.Name(effective)}（已从「{CompanionModeRules.Name(data.Mode)}」回退：{CompanionModeRules.RequirementText(data.Mode)}）";

            statsText.SetText(
                $"模式：{modeLine}\n" +
                $"当前自动选择：{weaponName}\n" +
                $"状态：{state}\n" +
                $"生命：{entity.statLife}/{entity.statLifeMax2}  防御：{(int)entity.statDefense}\n" +
                $"近战：×{entity.GetTotalDamage(DamageClass.Melee).ApplyTo(1f):0.00}  远程：×{entity.GetTotalDamage(DamageClass.Ranged).ApplyTo(1f):0.00}  魔法：×{entity.GetTotalDamage(DamageClass.Magic).ApplyTo(1f):0.00}" +
                warning);
        }
        else
        {
            statsText.SetText($"当前自动选择：{weaponName}{warning}");
        }
    }

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
        5 => "第 6 格：专家模式击败肉山后解锁；大师模式默认开启",
        6 => "第 7 格：大师模式击败肉山后解锁",
        _ => "该饰品位在当前世界不可用"
    };

    private static CompanionItemSlot CreateSlot(Item[] items, int index, System.Func<Item, bool> rule, string rejectionText, float left, float top,
        System.Func<bool> isHighlighted = null, Color? accentColor = null, System.Func<bool> isLocked = null, string lockedText = null)
    {
        CompanionItemSlot slot = new(items, index, rule, rejectionText, isHighlighted, accentColor, isLocked, lockedText);
        slot.Left.Set(left, 0f);
        slot.Top.Set(top, 0f);
        return slot;
    }

    private static void AddVerticalSlots(UIElement panel, Item[] items, float left, float top, params (System.Func<Item, bool> Rule, string Rejection)[] rules)
    {
        for (int i = 0; i < items.Length; i++)
            panel.Append(CreateSlot(items, i, rules[i].Rule, rules[i].Rejection, left, top + i * SlotStep));
    }

    private static void AddLabel(UIElement panel, string text, float left, float top)
    {
        UIText label = new(text, 0.8f);
        label.Left.Set(left, 0f);
        label.Top.Set(top, 0f);
        panel.Append(label);
    }
}
