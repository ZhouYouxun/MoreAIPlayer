using Microsoft.Xna.Framework;
using Terraria;

namespace MoreAIPlayer.Companion.Hunter;

public sealed class HunterInstance
{
    public int Slot { get; init; }
    public Player Entity { get; init; }
    public Player OriginalPlayer { get; init; }
    public HunterBrain Brain { get; } = new();
    public int HuntIndex { get; init; }
    public HunterGearPool.HunterLoadout Loadout { get; private set; }

    public Vector2 AimWorld;
    public bool WantsUseItem;
    public bool WantsAltUse;
    public int ReviveTicksLeft;
    public ulong ItemCheckPreparedFrame = ulong.MaxValue;
    public ulong ItemCheckDetourFrame = ulong.MaxValue;
    public long RecoveredItemChecks;
    public int ConsecutiveItemCheckMisses;
    public long AimCorrections;
    public long SlotRepairs;

    /// <summary>Consecutive ticks this hunter's own per-tick update threw. A clean tick resets it;
    /// reaching <see cref="MaxFaultStreak"/> retires the hunter rather than letting it fault
    /// forever (the minimal-damage fallback).</summary>
    public int FaultStreak;

    /// <summary>How many consecutive faulting ticks a hunter is given before it is retired.</summary>
    public const int MaxFaultStreak = 5;

    public void BindLoadout(Player playerTarget, string hunterName, HunterGameStage stage, int hunterCount)
    {
        Player me = Entity;
        me.active = true;
        me.whoAmI = Slot;
        me.name = hunterName;
        me.dead = false;
        me.respawnTimer = 0;

        // PvP hostile setup
        me.hostile = true;
        me.team = 0; // Independent team or neutral
        me.difficulty = 0;
        me.ghost = false;
        me.immuneAlpha = 0;

        // A hunter's health is derived from the target's life only; its explicit game-stage and
        // group multiplier keeps three hunters from being three full-strength copies.
        int targetHp = playerTarget.statLifeMax2 > 0 ? playerTarget.statLifeMax2 : playerTarget.statLifeMax;
        Loadout = HunterGearPool.GenerateLoadout(stage, HuntIndex, hunterCount);
        me.statLifeMax = System.Math.Max(140, (int)System.MathF.Round(targetHp * Loadout.LifeMultiplier));
        me.statLife = me.statLifeMax;
        HunterGearPool.ApplyLoadout(me, Loadout);
    }
}
