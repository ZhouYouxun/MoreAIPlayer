using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace MoreAIPlayer.Companion;

public sealed class CompanionWeaponPlan
{
    /// <summary>Profile hotbar slot, which is also the entity inventory index.</summary>
    public int Slot { get; }
    public Item Weapon { get; }
    public bool UsesProjectile { get; }

    public CompanionWeaponPlan(int slot, Item weapon, bool usesProjectile)
    {
        Slot = slot;
        Weapon = weapon;
        UsesProjectile = usesProjectile;
    }
}

/// <summary>
/// Chooses which hotbar weapon the companion holds. Actual weapon use is performed by the
/// vanilla Player.ItemCheck pipeline (the companion is a real player), so every use style,
/// shoot hook and special case behaves exactly like it does for a human player.
/// </summary>
public static class CompanionWeaponBehaviors
{
    private const float GenericProjectileRange = 760f;

    /// <summary>
    /// Picks the weapon to fight with.
    ///
    /// The invariant this method exists to guarantee: <b>if the companion is carrying anything
    /// it can fight with, it fights.</b> Reserved slots (guard, pick, summon) are carved out of
    /// the same ten-slot bar the player loads by hand, so a loadout that predates a new reserved
    /// slot — or a player who simply starts filling from the left — used to end up with every
    /// weapon outside the rotation and the companion standing there doing nothing at all: no
    /// weapon chosen means no attack, which in turn leaves the aim vector at its "160px straight
    /// ahead" idle default. Each new reserved slot re-introduced that failure, so the rule is
    /// now structural rather than a per-slot fix: the rotation is preferred, and the reserved
    /// slots are a last resort instead of a black hole.
    /// </summary>
    public static CompanionWeaponPlan SelectWeapon(Player companion, NPC target, CompanionProfile inventory,
        int excludedInventorySlot = -1)
    {
        CompanionWeaponPlan immediate = null;
        CompanionWeaponPlan fallback = null;

        // Tier 1: the ordinary rotation, in hotbar order.
        for (int slot = CompanionProfile.FirstCombatSlot; slot < CompanionProfile.EndCombatSlot; slot++)
            ConsiderSlot(companion, target, inventory, slot, excludedInventorySlot, ref immediate, ref fallback);

        if (immediate is not null)
            return immediate;
        if (fallback is not null)
            return fallback;

        // Tier 2: reserved slots, used only because nothing above could fight. The guard slot
        // keeps its swarm role (GuardPlan) and the summon slot its upkeep role — this is purely
        // about not standing idle while holding a perfectly good weapon. The pick slot is never
        // considered: swinging a pickaxe at an enemy is not an attack.
        return immediate ?? fallback;
    }

    /// <summary>
    /// Evaluates one hotbar slot. <paramref name="immediate"/> receives the first weapon that
    /// can attack the target right now; <paramref name="fallback"/> the first that could if the
    /// companion closed the distance. Both keep their first winner, so hotbar order decides
    /// priority.
    /// </summary>
    private static void ConsiderSlot(Player companion, NPC target, CompanionProfile inventory, int slot,
        int excludedInventorySlot,
        ref CompanionWeaponPlan immediate, ref CompanionWeaponPlan fallback)
    {
        if (immediate is not null)
            return;

        if (slot == excludedInventorySlot)
            return;

        Item weapon = inventory.Hotbar[slot];
        if (weapon.IsAir || !CompanionSlotRules.IsRegularWeapon(weapon))
            return;

        // Staffs are not attack weapons: the brain fires them separately to keep the summon
        // slots filled. Whips are deliberately not staffs — they attack normally.
        if (IsSummonStaff(weapon))
            return;

        // Channelled weapons without a projectile (rare) have nothing the AI can hold out.
        if (weapon.channel && weapon.shoot <= ProjectileID.None)
            return;

        if (weapon.useAmmo > AmmoID.None && !HasAmmo(inventory, weapon.useAmmo))
            return;

        float distance = Vector2.Distance(companion.Center, target.Center);
        bool usesProjectile = weapon.shoot > ProjectileID.None || weapon.useAmmo > AmmoID.None;
        CompanionWeaponPlan plan = new(slot, weapon, usesProjectile);

        if (usesProjectile)
        {
            bool hasLineOfSight = Collision.CanHitLine(companion.Center, 1, 1, target.Center, 1, 1);
            if (distance <= GenericProjectileRange && hasLineOfSight)
            {
                immediate = plan;
                return;
            }

            // A ranged weapon that is merely a bit too far or briefly blocked by terrain must
            // still be offered, or the brain's combat positioning — which does the approaching
            // — never runs and the companion just stands there.
            fallback ??= plan;
        }
        else
        {
            if (distance <= GetMeleeReach(companion, weapon))
            {
                immediate = plan;
                return;
            }

            fallback ??= plan;
        }
    }

    /// <summary>
    /// The weapon to answer a swarm with: the guard slot when the player filled it, otherwise
    /// the first ordinary weapon in the bar.
    ///
    /// Every special slot is an optional upgrade, never a requirement. Leaving the guard slot
    /// empty means the companion keeps swinging what it was already swinging when it gets
    /// swarmed — the fallback resolves to the same weapon, so there is simply no weapon switch
    /// and therefore no guard behaviour, which is the correct outcome for someone who never
    /// asked for one. A companion holding a single gun and nothing else must work perfectly.
    ///
    /// Note what this method does not do: no range check, no line-of-sight check, no ammo
    /// check, no damage-class check, no "is this suitable for close quarters" judgement of any
    /// kind. That guesswork is exactly what the guard slot replaces — whatever the player put
    /// here is what gets swung, and the standoff/approach logic in CompanionBrain then treats
    /// it like any other weapon of its shape.
    /// </summary>
    public static CompanionWeaponPlan GuardPlan(CompanionProfile inventory, int excludedInventorySlot = -1)
    {
        for (int slot = CompanionProfile.FirstCombatSlot; slot < CompanionProfile.EndCombatSlot; slot++)
        {
            if (slot == excludedInventorySlot)
                continue;
            Item candidate = inventory.Hotbar[slot];
            if (candidate.IsAir || !CompanionSlotRules.IsRegularWeapon(candidate))
                continue;
            return MakePlan(slot, candidate);
        }
        return null;
    }

    private static CompanionWeaponPlan MakePlan(int slot, Item weapon) =>
        new(slot, weapon, weapon.shoot > ProjectileID.None || weapon.useAmmo > AmmoID.None);

    /// <summary>
    /// A weapon whose whole job is to place a persistent summon: minion staffs and sentry
    /// staffs. Whips share the summon damage class but are swung at enemies like any other
    /// weapon, so they are excluded here and stay part of the normal attack rotation.
    /// </summary>
    public static bool IsSummonStaff(Item item) =>
        !item.IsAir &&
        item.shoot > ProjectileID.None &&
        item.CountsAsClass(DamageClass.Summon) &&
        !ProjectileID.Sets.IsAWhip[item.shoot];

    /// <summary>True for the subset of summon staffs that place a stationary sentry.</summary>
    public static bool IsSentryStaff(Item item) => IsSummonStaff(item) && item.sentry;

    /// <summary>True for ordinary minion staffs, excluding the separately maintained sentry role.</summary>
    public static bool IsMinionStaff(Item item) => IsSummonStaff(item) && !item.sentry;

    /// <summary>
    /// Generic flail detection shared by companions and hunters. Vanilla flails and modded
    /// flails following tModLoader's standard contract advertise the Flail projectile AI style,
    /// so this does not need a brittle list of item names.
    /// </summary>
    public static bool IsFlailWeapon(Item item) =>
        item is not null && !item.IsAir && item.shoot > ProjectileID.None &&
        ContentSamples.ProjectilesByType.TryGetValue(item.shoot, out Projectile sample) &&
        sample.aiStyle == ProjAIStyleID.Flail;

    /// <summary>Whether this player still owns a live flail, including its thrown/returning phases.</summary>
    public static bool HasActiveFlailProjectile(Player player)
    {
        if (player is null)
            return false;

        foreach (Projectile projectile in Main.ActiveProjectiles)
        {
            if (projectile.owner == player.whoAmI && projectile.aiStyle == ProjAIStyleID.Flail)
                return true;
        }
        return false;
    }

    /// <summary>
    /// How close the companion must be for a direct melee swing to connect. Pure swing
    /// weapons only cover roughly their own sprite size (scaled by melee size bonuses), so
    /// the companion walks in until the gap is at most the blade's reach.
    /// </summary>
    public static float GetMeleeReach(Player companion, Item weapon)
    {
        float scale = companion?.GetAdjustedItemScale(weapon) ?? weapon.scale;
        float reach = System.Math.Max(44f, System.Math.Max(weapon.width, weapon.height) * scale + 6f);

        // Spears deal their actual hit through a short-lived projectile. Their item sprite is
        // tiny, so sizing the engagement range from the sprite alone makes a companion walk
        // inside the enemy before trying to stab. Estimate the thrust from the launch speed,
        // bounded so an unusual modded spear cannot turn into a gun-range weapon.
        if (IsCloseCombatWeapon(weapon) && weapon.noMelee && weapon.shoot > ProjectileID.None)
            reach = System.Math.Max(reach, MathHelper.Clamp(weapon.shootSpeed * 12f + 52f, 96f, 180f));

        return reach;
    }

    /// <summary>
    /// Whether a weapon needs a close striking position even though it may spawn a projectile.
    /// A spear is the important case: its projectile is the thrust hitbox, not a bullet. Direct
    /// melee weapons and whips belong to the same close-combat family; yoyos stay ranged because
    /// their own range set explicitly describes a persistent remote hitbox.
    /// </summary>
    public static bool IsCloseCombatWeapon(Item weapon)
    {
        if (weapon.IsAir || !weapon.CountsAsClass(DamageClass.Melee))
            return false;
        if (weapon.shoot <= ProjectileID.None || !weapon.noMelee)
            return true;

        if (ProjectileID.Sets.IsAWhip.Length > weapon.shoot && ProjectileID.Sets.IsAWhip[weapon.shoot])
            return true;
        if (ProjectileID.Sets.YoyosMaximumRange.Length > weapon.shoot &&
            ProjectileID.Sets.YoyosMaximumRange[weapon.shoot] > 0f)
        {
            return false;
        }

        // A flail can spin as a close guard weapon, but releasing it is also a real ranged
        // attack. Its dedicated combat path chooses between those phases from target distance.
        if (IsFlailWeapon(weapon))
            return false;

        if (ContentSamples.ProjectilesByType.TryGetValue(weapon.shoot, out Projectile sample) &&
            (sample.aiStyle == ProjAIStyleID.Spear || sample.aiStyle == ProjAIStyleID.Flail ||
             sample.aiStyle == ProjAIStyleID.ShortSword || sample.aiStyle == ProjAIStyleID.HeldProjectile))
        {
            return true;
        }

        // A custom spear may not borrow vanilla Spear AI. Slow launch speed plus a deliberate
        // swing cadence is the conservative generic signature for that case; fast thrown blades
        // and beam-like melee projectiles remain ranged until a mod explicitly gives them a
        // close-combat AI style.
        return weapon.shootSpeed <= 6f && weapon.useTime >= 10;
    }

    private static bool HasAmmo(CompanionProfile inventory, int ammoType)
    {
        foreach (Item item in inventory.Ammo)
        {
            if (!item.IsAir && item.ammo == ammoType && item.stack > 0)
                return true;
        }

        foreach (Item item in inventory.Backpack)
        {
            if (!item.IsAir && item.ammo == ammoType && item.stack > 0)
                return true;
        }

        // Nothing stocked is not a reason to leave the weapon holstered: companions fall back
        // to the ammo class's default round for free (see CompanionGlobalItem.NeedsAmmo). The
        // test mirrors vanilla PickAmmo's own — an AmmoID is the item id of that default, and
        // a modded class without a usable one would still shoot nothing.
        return ContentSamples.ItemsByType.TryGetValue(ammoType, out Item defaultAmmo) &&
            defaultAmmo.ammo == ammoType;
    }
}
