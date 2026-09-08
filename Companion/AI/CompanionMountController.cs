using System;
using Terraria;
using Terraria.ID;

namespace MoreAIPlayer.Companion.AI;

/// <summary>Owns mount transitions independently of the real player's mount or input.</summary>
internal sealed class CompanionMountController
{
    private int travelTicks;
    private int idleTicks;
    private int retryTicks;
    private int ownedMount = MountID.None;

    internal static bool IsMountBuff(int buffType)
    {
        if (buffType <= 0)
            return false;
        if (buffType < BuffID.Sets.BasicMountData.Length && BuffID.Sets.BasicMountData[buffType] is not null)
            return true;

        // Mod mounts need not opt into BasicMountData; their registered MountData still owns
        // both the ordinary buff and (for minecarts) the alternate-direction buff.
        foreach (Mount.MountData data in Mount.mounts)
            if (data is not null && (data.buff == buffType || data.extraBuff == buffType))
                return true;
        return false;
    }

    internal void Update(CompanionInstance instance, CompanionProfile profile)
    {
        if (!CompanionRuntimePolicy.CanRunSyntheticPlayers)
            return;

        Player me = instance.Entity;
        me.controlMount = false;
        me.releaseMount = true;
        if (retryTicks > 0)
            retryTicks--;

        Item item = profile.MountTool;
        int mountType = item.IsAir ? MountID.None : item.mountType;
        bool validMount = mountType >= 0 && mountType < Mount.mounts.Length &&
            Mount.mounts[mountType] is not null && !Mount.mounts[mountType].Minecart;

        // Remove old copied mounts, unequipped mounts, and stale mount buffs before vanilla
        // can reapply them. A controller-owned mount alone is allowed to keep its buffs.
        bool keepMount = validMount && ownedMount == mountType && me.mount.Active && me.mount.Type == mountType;
        if (me.mount.Active && !keepMount)
            Dismount(me);
        for (int i = Player.MaxBuffs - 1; i >= 0; i--)
            if (IsMountBuff(me.buffType[i]) && (!keepMount || !me.mount.CheckBuff(me.buffType[i])))
                me.DelBuff(i);

        Item held = me.HeldItem;
        bool busyWithTools = instance.WorkTile.HasValue || (!held.IsAir &&
            (held.fishingPole > 0 || held.pick > 0 || held.axe > 0 ||
             held.hammer > 0 || held.createTile >= TileID.Dirt || held.createWall >= 0));
        bool blocked = !validMount || me.dead || !me.active || me.frozen || me.tongued || me.webbed ||
            me.stoned || me.noItems || me.gravDir != 1f || me.pulley || me.grapCount > 0 || me.controlHook;
        if (blocked || busyWithTools)
        {
            travelTicks = idleTicks = 0;
            Dismount(me);
            return;
        }

        Mount.MountData data = Mount.mounts[mountType];
        bool flying = data.flightTimeMax > 0 || data.usesHover;
        bool moving = me.controlLeft != me.controlRight || me.controlJump;
        travelTicks = moving ? Math.Min(travelTicks + 1, 60) : 0;
        idleTicks = moving ? 0 : Math.Min(idleTicks + 1, 120);

        if (me.mount.Active)
        {
            // Check ahead as well as overhead, so a tall mount yields to a low tunnel.
            int direction = me.controlLeft ? -1 : me.controlRight ? 1 : 0;
            bool cramped = !me.CanFitSpace(data.heightBoost) || (direction != 0 &&
                Collision.SolidCollision(me.position + new Microsoft.Xna.Framework.Vector2(direction * 24f, 0f),
                    me.width, me.height));
            bool grounded = idleTicks >= 45 && me.velocity.Y == 0f &&
                Collision.TileCollision(me.position, new Microsoft.Xna.Framework.Vector2(0f, 2f), me.width, me.height).Y < 2f;
            if (cramped || grounded)
                Dismount(me);
            else if (idleTicks >= 45 && flying)
                me.controlDown = true; // land before dismounting instead of dropping out of the sky
            return;
        }

        if (retryTicks > 0 || travelTicks < 30 || me.itemAnimation > 0 ||
            !me.mount.CanMount(mountType, me))
            return;

        // Keep vanilla/mod use restrictions and mount hooks. Verify the selected item so
        // QuickMount cannot silently choose another mount from the backpack.
        retryTicks = 120;
        using VirtualPlayerExecutionContext.Scope scope = VirtualPlayerExecutionContext.Enter(
            me, instance.AimWorld, instance.WantsUseItem, instance.WantsAltUse);
        if (!ReferenceEquals(me.QuickMount_GetItemToUse(), item))
            return;
        me.QuickMount();
        if (me.mount.Active && me.mount.Type == mountType)
            ownedMount = mountType;
        else
            Dismount(me); // QuickMount's rail fallback is not a navigation decision.
    }

    private void Dismount(Player me)
    {
        if (me.mount.Active)
        {
            me.mount.Dismount(me);
            retryTicks = 120;
        }
        ownedMount = MountID.None;
    }
}
