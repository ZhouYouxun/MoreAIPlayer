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
    public bool DefeatCounted;
    public ulong ItemCheckPreparedFrame = ulong.MaxValue;
    public ulong ItemCheckDetourFrame = ulong.MaxValue;
    public long RecoveredItemChecks;
    public int ConsecutiveItemCheckMisses;
    public long AimCorrections;
    public long SlotRepairs;
    public int ItemUseFaultStreak { get; private set; }
    public int ItemUseBackoffTicks { get; private set; }

    public void TickItemUseBackoff()
    {
        if (ItemUseBackoffTicks > 0)
            ItemUseBackoffTicks--;
    }

    public void RegisterItemUseFailure()
    {
        ItemUseFaultStreak = System.Math.Min(ItemUseFaultStreak + 1, 6);
        ItemUseBackoffTicks = System.Math.Min(1800, 60 << (ItemUseFaultStreak - 1));
    }

    public void RegisterItemUseSuccess()
    {
        ItemUseFaultStreak = 0;
        ItemUseBackoffTicks = 0;
    }

    // Vanilla advances buff timers only for Main.myPlayer. Hunters are synthetic players, so
    // their timers are mirrored here and advanced explicitly by HunterSystem.
    public int[] ObservedBuffTypes { get; } = new int[Player.MaxBuffs];
    public int[] ObservedBuffTimes { get; } = new int[Player.MaxBuffs];
    public int[] NextObservedBuffTypes { get; } = new int[Player.MaxBuffs];
    public int[] NextObservedBuffTimes { get; } = new int[Player.MaxBuffs];

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
        me.immune = false;
        me.immuneNoBlink = false;
        me.immuneTime = 0;
        me.immuneAlpha = 0;
        me.potionDelay = 0;
        me.itemAnimation = 0;
        me.itemAnimationMax = 0;
        me.itemTime = 0;
        me.itemTimeMax = 0;
        me.channel = false;
        System.Array.Clear(me.hurtCooldowns, 0, me.hurtCooldowns.Length);

        // A hunter's health is derived from the target's life only; its explicit game-stage and
        // group multiplier keeps three hunters from being three full-strength copies.
        int targetHp = playerTarget.statLifeMax2 > 0 ? playerTarget.statLifeMax2 : playerTarget.statLifeMax;
        Loadout = HunterGearPool.GenerateLoadout(stage, HuntIndex, hunterCount);
        me.statLifeMax = System.Math.Max(140, (int)System.MathF.Round(targetHp * Loadout.LifeMultiplier));
        me.statLifeMax2 = me.statLifeMax;
        me.statLife = me.statLifeMax;
        HunterGearPool.ApplyLoadout(me, Loadout);

        WantsUseItem = false;
        WantsAltUse = false;
        DefeatCounted = false;
        ItemCheckPreparedFrame = ulong.MaxValue;
        ItemCheckDetourFrame = ulong.MaxValue;
        ConsecutiveItemCheckMisses = 0;
        ItemUseFaultStreak = 0;
        ItemUseBackoffTicks = 0;
        System.Array.Clear(ObservedBuffTypes, 0, ObservedBuffTypes.Length);
        System.Array.Clear(ObservedBuffTimes, 0, ObservedBuffTimes.Length);
        System.Array.Clear(NextObservedBuffTypes, 0, NextObservedBuffTypes.Length);
        System.Array.Clear(NextObservedBuffTimes, 0, NextObservedBuffTimes.Length);
        Brain.ResetForNewLife(me);
    }
}
