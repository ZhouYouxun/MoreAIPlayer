using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.GameContent;
using Terraria.ID;
using Terraria.Localization;
using Terraria.ModLoader;

namespace MoreAIPlayer.Companion;

/// <summary>
/// Explains that permanent life and mana progression belongs to the real player. Companions
/// already derive those maxima from their owner, so consuming a second crystal for them would
/// be both unnecessary and misleading.
/// </summary>
public sealed class CompanionVitalCrystalTooltip : GlobalItem
{
    private const string LifeLineName = "PhantomLifeCrystalHint";
    private const string ManaLineName = "PhantomManaCrystalHint";

    public override void ModifyTooltips(Item item, List<TooltipLine> tooltips)
    {
        string key;
        string lineName;
        if (item.type == ItemID.LifeCrystal)
        {
            key = "LifeCrystal";
            lineName = LifeLineName;
        }
        else if (item.type == ItemID.ManaCrystal)
        {
            key = "ManaCrystal";
            lineName = ManaLineName;
        }
        else
        {
            return;
        }

        tooltips.Add(new TooltipLine(
            Mod,
            lineName,
            Language.GetTextValue($"Mods.MoreAIPlayer.VanillaItemHints.{key}"))
        {
            OverrideColor = new Color(196, 204, 211)
        });
    }

    public override bool PreDrawTooltipLine(Item item, DrawableTooltipLine line, ref int yOffset)
    {
        if (line.Mod != Mod.Name || line.Name is not (LifeLineName or ManaLineName))
            return true;

        Vector2 textSize = line.Font.MeasureString(line.Text) * line.BaseScale;
        Rectangle area = new(
            line.X - 7,
            line.Y - 4,
            (int)System.Math.Ceiling(textSize.X) + 14,
            (int)System.Math.Ceiling(textSize.Y) + 8);

        float pulse = (System.MathF.Sin(Main.GlobalTimeWrappedHourly * 2.4f) + 1f) * 0.5f;
        Color edge = Color.Lerp(new Color(112, 120, 128, 112), new Color(176, 184, 192, 142), pulse);

        DrawRectangle(area, new Color(24, 27, 31, 178));
        DrawRectangle(new Rectangle(area.X + 2, area.Y + 2, area.Width - 4, area.Height - 4),
            new Color(90, 96, 104, 34));

        DrawRectangle(new Rectangle(area.X, area.Y, area.Width, 1), edge);
        DrawRectangle(new Rectangle(area.X, area.Bottom - 1, area.Width, 1), edge * 0.65f);
        DrawRectangle(new Rectangle(area.X, area.Y, 1, 7), edge);
        DrawRectangle(new Rectangle(area.Right - 1, area.Y, 1, 7), edge);
        DrawRectangle(new Rectangle(area.X, area.Bottom - 7, 1, 7), edge * 0.65f);
        DrawRectangle(new Rectangle(area.Right - 1, area.Bottom - 7, 1, 7), edge * 0.65f);
        return true;
    }

    private static void DrawRectangle(Rectangle rectangle, Color color) =>
        Main.spriteBatch.Draw(TextureAssets.MagicPixel.Value, rectangle, color);
}
