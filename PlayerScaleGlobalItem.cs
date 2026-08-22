using Terraria;
using Terraria.ModLoader;

namespace MoreAIPlayer;

/// <summary>
/// Wing stats are owned by the equipped wing item rather than ModPlayer. A global hook is the
/// only way to make every vanilla and modded wing obey the same body-scale movement rule.
/// </summary>
public sealed class PlayerScaleGlobalItem : GlobalItem
{
    public override void HorizontalWingSpeeds(Item item, Player player, ref float speed, ref float acceleration)
    {
        float factor = MovementFactor(player);
        speed *= factor;
        acceleration *= factor;
    }

    public override void VerticalWingSpeeds(Item item, Player player, ref float ascentWhenFalling,
        ref float ascentWhenRising, ref float maxCanAscendMultiplier, ref float maxAscentMultiplier,
        ref float constantAscend)
    {
        float factor = MovementFactor(player);
        ascentWhenFalling *= factor;
        ascentWhenRising *= factor;
        maxCanAscendMultiplier *= factor;
        maxAscentMultiplier *= factor;
        constantAscend *= factor;
    }

    private static float MovementFactor(Player player)
    {
        if (player is null || !player.active)
            return 1f;
        return player.GetModPlayer<PlayerScalePlayer>().RuntimeScale;
    }
}
