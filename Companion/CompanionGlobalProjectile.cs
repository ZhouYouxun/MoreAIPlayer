using Terraria;
using Terraria.DataStructures;
using Terraria.ModLoader;

namespace MoreAIPlayer.Companion;

/// <summary>
/// Light bookkeeping for projectiles fired by companion players. The local-player context
/// swap for their updates lives in CompanionEntitySystem's Projectile.Update detour, which
/// wraps the whole update in try/finally so an exception inside any mod's projectile code
/// can never leave Main.myPlayer pointing at a companion (that was the view-hijack bug).
/// </summary>
public sealed class CompanionGlobalProjectile : GlobalProjectile
{
    public override void OnSpawn(Projectile projectile, IEntitySource source)
    {
        // Synthetic slots are a strictly single-player construct. A stale dictionary during a
        // world transition must never rewrite an owner on a real multiplayer projectile.
        if (Main.netMode != Terraria.ID.NetmodeID.SinglePlayer)
            return;

        int expectedOwner = -1;

        if (source is EntitySource_Parent { Entity: Player player } &&
            (CompanionEntitySystem.IsCompanion(player) || Hunter.HunterSystem.IsHunter(player)))
        {
            expectedOwner = player.whoAmI;
        }
        else if (source is EntitySource_Parent { Entity: Projectile parent } &&
            parent.owner >= 0 && parent.owner < Main.maxPlayers &&
            (CompanionEntitySystem.IsCompanionSlot(parent.owner) || Hunter.HunterSystem.IsHunterSlot(parent.owner)) &&
            (projectile.friendly || parent.friendly) && !projectile.hostile)
        {
            // Friendly child projectiles should inherit their synthetic parent's owner. This
            // repairs mod weapons that used Main.myPlayer while spawning a secondary projectile
            // just outside the virtual update context.
            expectedOwner = parent.owner;
        }

        if (expectedOwner >= 0 && projectile.owner != expectedOwner)
        {
            projectile.owner = expectedOwner;
            CompanionEntitySystem.RegisterProjectileOwnerCorrection();
        }
    }

    public override void OnHitNPC(Projectile projectile, NPC target, NPC.HitInfo hit, int damageDone)
    {
        if (Main.netMode != Terraria.ID.NetmodeID.SinglePlayer)
            return;

        CompanionInstance instance = CompanionEntitySystem.GetBySlot(projectile.owner);
        if (instance is null || instance.OwnerWhoAmI < 0 || instance.OwnerWhoAmI >= Main.maxPlayers)
            return;

        // Credit the real owner so loot, bestiary interaction and banner logic behave.
        target.playerInteraction[instance.OwnerWhoAmI] = true;
        target.lastInteraction = instance.OwnerWhoAmI;
    }

    public override bool? CanHitNPC(Projectile projectile, NPC target)
    {
        if (Main.netMode != Terraria.ID.NetmodeID.SinglePlayer)
            return null;

        if (target.catchItem <= 0)
            return null;

        CompanionInstance instance = CompanionEntitySystem.GetBySlot(projectile.owner);
        if (instance is not null && instance.Brain.ProtectCritters)
            return false;
        return null;
    }
}
