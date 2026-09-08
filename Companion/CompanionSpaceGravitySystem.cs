using System;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using Terraria;
using Terraria.ModLoader;

namespace MoreAIPlayer.Companion;

public sealed class CompanionSpaceGravitySystem : ModSystem
{
    public override void Load()
    {
        if (!Main.dedServ)
            MonoModHooks.Modify(typeof(Player).GetMethod(nameof(Player.Update)), RemoveSpaceMultiplier);
    }

    private static void RemoveSpaceMultiplier(ILContext il)
    {
        ILCursor cursor = new(il);
        // Only the altitude multiplier loads a local between gravity and mul. The honey,
        // shimmer, water, and later movement branches use constants or other instructions.
        if (!cursor.TryGotoNext(MoveType.Before,
            i => i.MatchLdfld<Player>(nameof(Player.gravity)),
            i => i.MatchLdloc(out _),
            i => i.MatchMul(),
            i => i.MatchStfld<Player>(nameof(Player.gravity))))
        {
            throw new InvalidOperationException("Could not find Player.Update's space gravity multiplier.");
        }
        cursor.Index += 2;
        cursor.Emit(OpCodes.Ldarg_0);
        cursor.EmitDelegate<Func<float, Player, float>>(AdjustSpaceMultiplier);
    }

    internal static float AdjustSpaceMultiplier(float multiplier, Player player) =>
        CompanionEntitySystem.IsCompanion(player) &&
        (Main.hardMode || MoreAIPlayerClientConfig.Current?.CompanionsAlwaysIgnoreSpaceGravity == true)
            ? 1f : multiplier;
}
