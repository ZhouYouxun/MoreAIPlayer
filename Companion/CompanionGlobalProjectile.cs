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
    // One bounded, lazily allocated table instead of a GlobalProjectile object on every
    // projectile in the game. Only an observed companion attack writes an entry.
    private struct ChargeObservation
    {
        public Projectile Projectile;
        public CompanionInstance Companion;
        public int Identity;
        public int Session;
    }
    private static ChargeObservation[] chargeObservations;

    internal static void ClearChargeObservations() => chargeObservations = null;

    internal static void ForgetChargeObservations(CompanionInstance companion)
    {
        if (chargeObservations is null)
            return;
        // Dismissal is infrequent. Release references here instead of retaining an old
        // companion's inventory until these projectile slots happen to be reused.
        for (int i = 0; i < chargeObservations.Length; i++)
            if (ReferenceEquals(chargeObservations[i].Companion, companion))
                chargeObservations[i] = default;
    }

    private static int GetChargeSession(Projectile projectile, CompanionInstance companion)
    {
        if (chargeObservations is null || (uint)projectile.whoAmI >= chargeObservations.Length)
            return 0;
        ref ChargeObservation entry = ref chargeObservations[projectile.whoAmI];
        return ReferenceEquals(entry.Projectile, projectile) && ReferenceEquals(entry.Companion, companion) &&
            entry.Identity == projectile.identity && projectile.owner == companion.Slot ? entry.Session : 0;
    }

    public override void OnSpawn(Projectile projectile, IEntitySource source)
    {
        if (chargeObservations is not null && (uint)projectile.whoAmI < chargeObservations.Length)
            chargeObservations[projectile.whoAmI] = default;
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

        CompanionInstance companion = CompanionEntitySystem.GetBySlot(projectile.owner);
        if (companion is null || !companion.ChargeController.IsObserving)
            return;
        int chargeSession = 0;
        bool fromItem = source is EntitySource_ItemUse;
        if (source is EntitySource_ItemUse itemUse && ReferenceEquals(itemUse.Player, companion.Entity))
            chargeSession = companion.ChargeController.SourceSession(itemUse.Item);
        else if (source is EntitySource_Parent { Entity: Projectile sourceProjectile } &&
            sourceProjectile.owner == projectile.owner)
            chargeSession = GetChargeSession(sourceProjectile, companion);
        if (chargeSession > 0 && (uint)projectile.whoAmI < Main.maxProjectiles)
        {
            chargeObservations ??= new ChargeObservation[Main.maxProjectiles];
            chargeObservations[projectile.whoAmI] = new ChargeObservation
            {
                Projectile = projectile, Companion = companion,
                Identity = projectile.identity, Session = chargeSession
            };
        }
        companion.ChargeController.ObserveSpawn(projectile, chargeSession, fromItem);
    }

    public override void OnHitNPC(Projectile projectile, NPC target, NPC.HitInfo hit, int damageDone)
    {
        if (Main.netMode != Terraria.ID.NetmodeID.SinglePlayer)
            return;

        CompanionInstance instance = CompanionEntitySystem.GetBySlot(projectile.owner);
        if (instance is null || instance.OwnerWhoAmI < 0 || instance.OwnerWhoAmI >= Main.maxPlayers)
            return;

        if (instance.ChargeController.IsObserving)
            instance.ChargeController.ObserveHit(GetChargeSession(projectile, instance), damageDone);

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
