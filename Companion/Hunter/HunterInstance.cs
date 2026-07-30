using Microsoft.Xna.Framework;
using Terraria;

namespace MoreAIPlayer.Companion.Hunter;

public sealed class HunterInstance
{
    public int Slot { get; init; }
    public Player Entity { get; init; }
    public Player OriginalPlayer { get; init; }
    public HunterBrain Brain { get; } = new();

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

    public void BindLoadout(Player playerTarget, string hunterName)
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

        // Scale max health with target player (at least 200)
        int targetHp = playerTarget.statLifeMax2 > 0 ? playerTarget.statLifeMax2 : playerTarget.statLifeMax;
        me.statLifeMax = System.Math.Max(200, targetHp);
        me.statLife = me.statLifeMax;

        HunterGearPool.HunterLoadout loadout = HunterGearPool.GenerateLoadout(playerTarget);
        HunterGearPool.ApplyLoadout(me, loadout);
    }
}
