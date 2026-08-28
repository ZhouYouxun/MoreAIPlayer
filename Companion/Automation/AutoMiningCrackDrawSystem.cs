using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.ModLoader;

namespace MoreAIPlayer.Companion.Automation;

/// <summary>
/// Vanilla records mining damage on the acting Player's HitTile, but TileDrawing renders only
/// Main.player[Main.myPlayer].hitTile. Synthetic mining therefore broke real tiles while showing
/// no progressive crack overlay. Draw the automation player's own vanilla cache after tiles using
/// the same Main.DrawTileCracks routine; no duplicate damage state or cosmetic fake is introduced.
/// </summary>
public sealed class AutoMiningCrackDrawSystem : ModSystem
{
    public override void PostDrawTiles()
    {
        if (Main.dedServ || Main.gameMenu || Main.netMode != Terraria.ID.NetmodeID.SinglePlayer)
            return;

        bool began = false;
        foreach (CompanionInstance instance in CompanionEntitySystem.All)
        {
            Player player = instance.Entity;
            if (!instance.IsAutoIdentity || player is null || !player.active ||
                player.whoAmI == Main.myPlayer || !HasCrack(player.hitTile) && !HasCrack(player.hitReplace))
                continue;

            if (!began)
            {
                Main.spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend,
                    Main.DefaultSamplerState, DepthStencilState.None, Main.Rasterizer, null,
                    Main.GameViewMatrix.TransformationMatrix);
                began = true;
            }

            Main.instance.DrawTileCracks(1, player.hitReplace);
            Main.instance.DrawTileCracks(1, player.hitTile);
        }

        if (began)
            Main.spriteBatch.End();
    }

    private static bool HasCrack(HitTile hitTile)
    {
        if (hitTile?.data is null)
            return false;
        foreach (HitTile.HitTileObject entry in hitTile.data)
            if (entry is not null && entry.type == 1 && entry.damage >= 20)
                return true;
        return false;
    }
}
