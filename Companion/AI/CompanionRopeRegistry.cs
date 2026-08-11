using System.Collections.Generic;
using Terraria;
using Terraria.ID;

namespace MoreAIPlayer.Companion.AI;

/// <summary>
/// Single source of truth for "is this tile something a companion can pulley-climb".
///
/// Vanilla already keeps that answer in <see cref="Main.tileRope"/> (which
/// <c>TileLoader</c> resizes so registered mod ropes are included) and folds the chain / rope-
/// sandwiched-platform special cases into <see cref="WorldGen.IsRope"/> — the exact predicate the
/// player's own climb code checks every tick. This registry therefore defers to
/// <see cref="WorldGen.IsRope"/> so the pathfinder's idea of a rope can never drift from what the
/// engine will actually let the companion grab, while still exposing a small opt-in hook for the
/// rare mod tile that is climbable but, for whatever reason, is not flagged in
/// <see cref="Main.tileRope"/>.
///
/// Deliberately does NOT guess: an arbitrary non-solid vertical piece of furniture is not treated
/// as a rope just because it forms a column.
/// </summary>
public static class CompanionRopeRegistry
{
    // Extra climbable tile types a compatibility layer can register when a mod rope is not caught
    // by Main.tileRope / WorldGen.IsRope. Empty by default; nothing is assumed.
    private static readonly HashSet<int> extraRopeTypes = [];

    /// <summary>Registers an additional climbable tile type. Safe to call from a mod's load.</summary>
    public static void RegisterRope(int tileType)
    {
        if (tileType >= 0)
            extraRopeTypes.Add(tileType);
    }

    /// <summary>Whether the tile at (x, y) is a climbable rope. Bounds-checked before touching the
    /// tile array, because <see cref="WorldGen.IsRope"/> itself dereferences without an InWorld
    /// guard.</summary>
    public static bool IsRope(int x, int y)
    {
        if (!WorldGen.InWorld(x, y, 2))
            return false;

        Tile tile = Main.tile[x, y];
        if (!tile.HasTile || tile.IsActuated)
            return false;

        if (WorldGen.IsRope(x, y))
            return true;

        return extraRopeTypes.Count > 0 && extraRopeTypes.Contains(tile.TileType);
    }

    /// <summary>Whether a body centred on column <paramref name="x"/> spanning rows
    /// <paramref name="topRow"/>..<paramref name="bottomRow"/> overlaps a rope in that column —
    /// the grab condition the vanilla <c>FindPulley</c> uses (center column tile is a rope).</summary>
    public static bool ColumnHasRope(int x, int topRow, int bottomRow)
    {
        for (int y = topRow; y <= bottomRow; y++)
        {
            if (IsRope(x, y))
                return true;
        }
        return false;
    }
}
