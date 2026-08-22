using Microsoft.Xna.Framework;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;
using Terraria.ModLoader;

namespace MoreAIPlayer;

/// <summary>
/// A one-time welcome item which also serves as the player's starter melee weapon.
/// </summary>
public sealed class DevelopmentNotice : ModItem
{
    private const string CatDoodle = "      ╱|、\n    (˚ˎ 。7\n     |、˜〵\n     じしˍ,)ノ";

    // Borrow the vanilla Keybrand sprite until the mod has a dedicated icon for this notice.
    public override string Texture => $"Terraria/Images/Item_{ItemID.Keybrand}";

    public override void SetDefaults()
    {
        Item.width = 52;
        Item.height = 52;
        Item.maxStack = 1;
        Item.value = 0;
        Item.rare = ItemRarityID.White;
        Item.damage = 25;
        Item.DamageType = DamageClass.Melee;
        Item.crit = 30;
        Item.knockBack = 4f;
        Item.useTime = Item.useAnimation = 17;
        Item.useStyle = ItemUseStyleID.Swing;
        Item.UseSound = SoundID.Item1;
        Item.autoReuse = true;
        Item.noMelee = true; // The custom sword-light projectile owns the damage and collision.
        Item.noUseGraphic = false;
        Item.shoot = ModContent.ProjectileType<DevelopmentNoticeSwing>();
        Item.shootSpeed = 1f;
        Item.shootsEveryUse = true;
    }

    public override bool Shoot(Player player, EntitySource_ItemUse_WithAmmo source, Vector2 position, Vector2 velocity,
        int type, int damage, float knockback)
    {
        float adjustedItemScale = player.GetAdjustedItemScale(Item);
        Projectile.NewProjectile(source, player.MountedCenter, new Vector2(player.direction, 0f), type, damage,
            knockback, player.whoAmI, player.direction * player.gravDir, player.itemAnimationMax, adjustedItemScale);
        return false;
    }

    public override void ModifyTooltips(System.Collections.Generic.List<TooltipLine> tooltips)
    {
        tooltips.Add(new TooltipLine(Mod, "Development", GetText("Development"))
        {
            OverrideColor = new Color(255, 205, 92)
        });
        tooltips.Add(new TooltipLine(Mod, "SoloCreator", GetText("SoloCreator"))
        {
            OverrideColor = new Color(125, 205, 255)
        });
        tooltips.Add(new TooltipLine(Mod, "Updates", GetText("Updates"))
        {
            OverrideColor = new Color(140, 255, 175)
        });
        tooltips.Add(new TooltipLine(Mod, "Compatibility", GetText("Compatibility"))
        {
            OverrideColor = new Color(255, 165, 125)
        });
        tooltips.Add(new TooltipLine(Mod, "Complexity", GetText("Complexity"))
        {
            OverrideColor = new Color(205, 180, 255)
        });
        tooltips.Add(new TooltipLine(Mod, "BetaControlsFirstLine", GetText("BetaControlsFirstLine"))
        {
            OverrideColor = new Color(255, 125, 205)
        });
        tooltips.Add(new TooltipLine(Mod, "BetaControlsSecondLine", GetText("BetaControlsSecondLine"))
        {
            OverrideColor = new Color(255, 125, 205)
        });
        tooltips.Add(new TooltipLine(Mod, "ConfigSuggestion", GetText("ConfigSuggestion"))
        {
            OverrideColor = new Color(145, 225, 255)
        });
        tooltips.Add(new TooltipLine(Mod, "CatDoodle", CatDoodle)
        {
            OverrideColor = new Color(215, 215, 215)
        });
    }

    private string GetText(string key) =>
        Terraria.Localization.Language.GetTextValue($"Mods.MoreAIPlayer.Items.DevelopmentNotice.{key}");
}
