using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.Audio;
using Terraria.GameContent.UI.Elements;
using Terraria.ID;
using Terraria.Localization;
using Terraria.ModLoader;
using Terraria.UI;

namespace MoreAIPlayer.Companion.UI;

/// <summary>
/// A companion-local recreation of the dresser's character page. Vanilla's dresser edits
/// Main.LocalPlayer directly, which would be unsafe here because the companion is a separate,
/// synthetic Player. This page instead writes the exact same visual fields to its saved profile.
/// </summary>
public sealed class CompanionAppearanceUI : UIState, ICompanionPanelState
{
    private static readonly Color[] Palette =
    [
        new(31, 31, 38), new(91, 56, 41), new(151, 86, 49), new(222, 153, 77),
        new(245, 219, 176), new(245, 242, 232), new(118, 163, 216), new(88, 194, 154),
        new(218, 102, 106), new(166, 107, 202), new(94, 137, 211), new(226, 89, 157)
    ];

    private readonly CompanionProfile profile;
    private readonly int companionSlot;
    private readonly Vector2? initialPanelPosition;
    private DraggableUIPanel panel = null!;
    private UIText sexValue = null!;
    private UIText variantValue = null!;
    private UIText hairValue = null!;
    private CompanionProfile.AppearanceColor selectedColor = CompanionProfile.AppearanceColor.Hair;
    private UIText selectedColorText = null!;

    public CompanionAppearanceUI(CompanionProfile profile, int companionSlot, Vector2? initialPanelPosition)
    {
        this.profile = profile;
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

        UIText title = new(T("AppearanceTitle", DisplayName()), 1.05f, true);
        title.Left.Set(18f, 0f);
        title.Top.Set(12f, 0f);
        panel.Append(title);

        UITextPanel<string> close = CreateButton(T("Close"), 64f, 30f, 0.8f);
        close.Left.Set(-76f, 1f);
        close.Top.Set(8f, 0f);
        close.OnLeftClick += (_, _) => CompanionUISystem.Close();
        panel.Append(close);

        UITextPanel<string> back = CreateButton(T("AppearanceBack"), 88f, 30f, 0.76f);
        back.Left.Set(-172f, 1f);
        back.Top.Set(8f, 0f);
        back.OnLeftClick += (_, _) => CompanionUISystem.Open(companionSlot);
        panel.Append(back);

        UIText hint = new(T("AppearanceHint"), 0.7f, false);
        hint.Left.Set(18f, 0f);
        hint.Top.Set(50f, 0f);
        hint.Width.Set(-36f, 1f);
        panel.Append(hint);

        CompanionAppearancePreview preview = new(companionSlot);
        preview.Left.Set(62f, 0f);
        preview.Top.Set(110f, 0f);
        preview.Width.Set(178f, 0f);
        preview.Height.Set(430f, 0f);
        panel.Append(preview);

        AddSelectionRow(T("AppearanceGender"), 270f, 104f, out sexValue,
            () => SetGender(!profile.Male), increment: null, singleActionWidth: 94f);
        AddSelectionRow(T("AppearanceSkin"), 270f, 153f, out variantValue,
            () => profile.SetSkinVariant(profile.SkinVariant - 1),
            () => profile.SetSkinVariant(profile.SkinVariant + 1));
        AddSelectionRow(T("AppearanceHair"), 270f, 202f, out hairValue,
            () => profile.SetHair(profile.Hair - 1),
            () => profile.SetHair(profile.Hair + 1));

        float colorTop = 272f;
        AddColorRow(CompanionProfile.AppearanceColor.Hair, T("AppearanceHairColor"), colorTop);
        AddColorRow(CompanionProfile.AppearanceColor.Skin, T("AppearanceSkinColor"), colorTop + 55f);
        AddColorRow(CompanionProfile.AppearanceColor.Eyes, T("AppearanceEyeColor"), colorTop + 110f);
        AddColorRow(CompanionProfile.AppearanceColor.Shirt, T("AppearanceShirtColor"), colorTop + 165f);
        AddColorRow(CompanionProfile.AppearanceColor.UnderShirt, T("AppearanceUnderShirtColor"), colorTop + 220f);
        AddColorRow(CompanionProfile.AppearanceColor.Pants, T("AppearancePantsColor"), colorTop + 275f);
        AddColorRow(CompanionProfile.AppearanceColor.Shoes, T("AppearanceShoeColor"), colorTop + 330f);
        AddFineColorEditor();

        ApplyAndRefresh();
    }

    public override void Update(GameTime gameTime)
    {
        if (panel.ContainsPoint(Main.MouseScreen))
            Main.LocalPlayer.mouseInterface = true;
        base.Update(gameTime);
        RefreshLabels();
    }

    private void AddSelectionRow(string label, float left, float top, out UIText value, Action decrement, Action increment = null, float singleActionWidth = 0f)
    {
        UIText rowLabel = new(label, 0.78f, true);
        rowLabel.Left.Set(left, 0f);
        rowLabel.Top.Set(top, 0f);
        panel.Append(rowLabel);

        value = new UIText(string.Empty, 0.72f);
        value.Left.Set(left, 0f);
        value.Top.Set(top + 22f, 0f);
        panel.Append(value);

        if (increment is null)
        {
            UITextPanel<string> change = CreateButton(T("AppearanceChange"), singleActionWidth, 30f, 0.7f);
            change.Left.Set(left + 155f, 0f);
            change.Top.Set(top + 12f, 0f);
            change.OnLeftClick += (_, _) => { decrement(); ApplyAndRefresh(); };
            panel.Append(change);
            return;
        }

        UITextPanel<string> previous = CreateButton("<", 30f, 30f, 0.85f);
        previous.Left.Set(left + 125f, 0f);
        previous.Top.Set(top + 12f, 0f);
        previous.OnLeftClick += (_, _) => { decrement(); ApplyAndRefresh(); };
        panel.Append(previous);

        UITextPanel<string> next = CreateButton(">", 30f, 30f, 0.85f);
        next.Left.Set(left + 287f, 0f);
        next.Top.Set(top + 12f, 0f);
        next.OnLeftClick += (_, _) => { increment(); ApplyAndRefresh(); };
        panel.Append(next);
    }

    private void AddColorRow(CompanionProfile.AppearanceColor channel, string label, float top)
    {
        UIText colorLabel = new(label, 0.68f, true);
        colorLabel.Left.Set(270f, 0f);
        colorLabel.Top.Set(top, 0f);
        colorLabel.OnLeftClick += (_, _) => SelectColor(channel);
        panel.Append(colorLabel);

        for (int i = 0; i < Palette.Length; i++)
        {
            Color picked = Palette[i];
            UIPanel swatch = new();
            swatch.Width.Set(22f, 0f);
            swatch.Height.Set(22f, 0f);
            swatch.Left.Set(270f + i * 27f, 0f);
            swatch.Top.Set(top + 22f, 0f);
            swatch.BackgroundColor = picked;
            swatch.BorderColor = Color.Lerp(picked, Color.White, 0.38f);
            swatch.OnLeftClick += (_, _) => { SelectColor(channel); profile.SetAppearanceColor(channel, picked); ApplyAndRefresh(); };
            swatch.OnMouseOver += (_, _) => Main.instance.MouseText(T("AppearanceColorHint"));
            panel.Append(swatch);
        }
    }

    private void AddFineColorEditor()
    {
        selectedColorText = new UIText(string.Empty, 0.76f, true);
        selectedColorText.Left.Set(34f, 0f);
        selectedColorText.Top.Set(555f, 0f);
        panel.Append(selectedColorText);

        AddRgbSlider(T("AppearanceRed"), 34f, 583f, color => color.R, (color, value) => new Color(value, color.G, color.B));
        AddRgbSlider(T("AppearanceGreen"), 34f, 617f, color => color.G, (color, value) => new Color(color.R, value, color.B));
        AddRgbSlider(T("AppearanceBlue"), 34f, 651f, color => color.B, (color, value) => new Color(color.R, color.G, value));
    }

    private void AddRgbSlider(string label, float left, float top, Func<Color, byte> read, Func<Color, byte, Color> write)
    {
        UIText channelLabel = new(label, 0.7f, true);
        channelLabel.Left.Set(left, 0f);
        channelLabel.Top.Set(top, 0f);
        panel.Append(channelLabel);

        CompanionRgbSlider slider = new(
            getValue: () => read(GetSelectedColor()),
            setValue: value => { profile.SetAppearanceColor(selectedColor, write(GetSelectedColor(), value)); ApplyAndRefresh(); },
            channelColor: value => write(new Color(0, 0, 0), value));
        slider.Left.Set(left + 28f, 0f);
        slider.Top.Set(top + 1f, 0f);
        slider.Width.Set(170f, 0f);
        slider.Height.Set(16f, 0f);
        panel.Append(slider);
    }

    private void SelectColor(CompanionProfile.AppearanceColor channel)
    {
        selectedColor = channel;
        RefreshLabels();
    }

    private Color GetSelectedColor() => selectedColor switch
    {
        CompanionProfile.AppearanceColor.Hair => profile.HairColor,
        CompanionProfile.AppearanceColor.Skin => profile.SkinColor,
        CompanionProfile.AppearanceColor.Eyes => profile.EyeColor,
        CompanionProfile.AppearanceColor.Shirt => profile.ShirtColor,
        CompanionProfile.AppearanceColor.UnderShirt => profile.UnderShirtColor,
        CompanionProfile.AppearanceColor.Pants => profile.PantsColor,
        _ => profile.ShoeColor
    };

    private string SelectedColorName() => selectedColor switch
    {
        CompanionProfile.AppearanceColor.Hair => T("AppearanceHairColor"),
        CompanionProfile.AppearanceColor.Skin => T("AppearanceSkinColor"),
        CompanionProfile.AppearanceColor.Eyes => T("AppearanceEyeColor"),
        CompanionProfile.AppearanceColor.Shirt => T("AppearanceShirtColor"),
        CompanionProfile.AppearanceColor.UnderShirt => T("AppearanceUnderShirtColor"),
        CompanionProfile.AppearanceColor.Pants => T("AppearancePantsColor"),
        _ => T("AppearanceShoeColor")
    };

    private void SetGender(bool male)
    {
        int count = Math.Max(1, PlayerVariantID.Sets.Male.Length);
        for (int offset = 1; offset <= count; offset++)
        {
            int index = (profile.SkinVariant + offset) % count;
            if (PlayerVariantID.Sets.Male[index] == male)
            {
                profile.SetSkinVariant(index);
                return;
            }
        }
    }

    private void ApplyAndRefresh()
    {
        CompanionInstance companion = CompanionEntitySystem.GetBySlot(companionSlot);
        if (companion is not null)
            profile.ApplyAppearanceTo(companion.Entity, Main.LocalPlayer);
        SoundEngine.PlaySound(SoundID.MenuTick with { Volume = 0.55f });
        RefreshLabels();
    }

    private void RefreshLabels()
    {
        if (sexValue is null)
            return;
        sexValue.SetText(profile.Male ? T("AppearanceMale") : T("AppearanceFemale"));
        variantValue.SetText(T("AppearanceVariantValue", profile.SkinVariant + 1));
        hairValue.SetText(T("AppearanceHairValue", profile.Hair + 1));
        selectedColorText?.SetText(T("AppearanceFineColor", SelectedColorName()));
    }

    private string DisplayName() => string.IsNullOrWhiteSpace(profile.Name) ? T("DefaultName") : profile.Name;

    private static UITextPanel<string> CreateButton(string text, float width, float height, float scale)
    {
        UITextPanel<string> button = new(text, scale, false);
        button.Width.Set(width, 0f);
        button.Height.Set(height, 0f);
        return button;
    }

    private static string T(string key, params object[] args) =>
        Language.GetTextValue($"Mods.MoreAIPlayer.CompanionUI.{key}", args);
}

/// <summary>Compact 0-255 RGB slider. The palette provides fast choices; this keeps every
/// vanilla RGB value reachable instead of limiting a dresser to a handful of presets.</summary>
internal sealed class CompanionRgbSlider : UIElement
{
    private readonly Func<byte> getValue;
    private readonly Action<byte> setValue;
    private readonly Func<byte, Color> channelColor;

    public CompanionRgbSlider(Func<byte> getValue, Action<byte> setValue, Func<byte, Color> channelColor)
    {
        this.getValue = getValue;
        this.setValue = setValue;
        this.channelColor = channelColor;
        OnLeftClick += (_, _) => ApplyMouseValue();
        OnLeftMouseDown += (_, _) => ApplyMouseValue();
    }

    public override void Update(GameTime gameTime)
    {
        if (ContainsPoint(Main.MouseScreen))
            Main.LocalPlayer.mouseInterface = true;
        if (IsMouseHovering && Main.mouseLeft)
            ApplyMouseValue();
        base.Update(gameTime);
    }

    protected override void DrawSelf(SpriteBatch spriteBatch)
    {
        CalculatedStyle area = GetDimensions();
        int width = Math.Max(1, (int)area.Width);
        for (int x = 0; x < width; x++)
        {
            byte value = (byte)Math.Clamp(x * 255 / Math.Max(1, width - 1), 0, 255);
            spriteBatch.Draw(Terraria.GameContent.TextureAssets.MagicPixel.Value,
                new Rectangle((int)area.X + x, (int)area.Y, 1, (int)area.Height), channelColor(value));
        }

        int marker = (int)Math.Round(getValue() / 255f * (width - 1));
        spriteBatch.Draw(Terraria.GameContent.TextureAssets.MagicPixel.Value,
            new Rectangle((int)area.X + marker - 1, (int)area.Y - 2, 3, (int)area.Height + 4), Color.White);
    }

    private void ApplyMouseValue()
    {
        CalculatedStyle area = GetDimensions();
        float ratio = MathHelper.Clamp((Main.MouseScreen.X - area.X) / Math.Max(1f, area.Width), 0f, 1f);
        setValue((byte)Math.Round(ratio * 255f));
    }
}

/// <summary>Full vanilla player renderer preview, isolated from world lighting like the game's UI dolls.</summary>
internal sealed class CompanionAppearancePreview : UIElement
{
    private readonly int companionSlot;

    public CompanionAppearancePreview(int companionSlot) => this.companionSlot = companionSlot;

    protected override void DrawSelf(SpriteBatch spriteBatch)
    {
        CompanionInstance companion = CompanionEntitySystem.GetBySlot(companionSlot);
        if (companion is null)
            return;

        Player entity = companion.Entity;
        CalculatedStyle area = GetDimensions();
        bool previousDisplayDoll = entity.isDisplayDollOrInanimate;
        entity.isDisplayDollOrInanimate = true;
        try
        {
            Vector2 position = new Vector2(area.X + area.Width * 0.5f, area.Y + area.Height * 0.72f) + Main.screenPosition;
            Main.PlayerRenderer.DrawPlayer(Main.Camera, entity, position, 0f, entity.Size * 0.5f, scale: 2.4f);
        }
        finally
        {
            entity.isDisplayDollOrInanimate = previousDisplayDoll;
        }
    }
}
