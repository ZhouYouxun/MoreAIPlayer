using System;
using Microsoft.Xna.Framework;
using MoreAIPlayer.Companion.AI;
using Terraria;
using Terraria.Audio;
using Terraria.DataStructures;
using Terraria.ID;
using Terraria.Localization;
using Terraria.ModLoader;

namespace MoreAIPlayer.Companion;

/// <summary>
/// Per-world-item provenance used by the companion backpack collector. A candidate is marked
/// once, when it appears near a real owner, rather than being inferred later from wherever the
/// item happened to roll. Player-thrown items and items deliberately dumped from a companion
/// backpack are never candidates.
/// </summary>
public sealed class CompanionPickupGlobalItem : GlobalItem
{
    public override bool InstancePerEntity => true;

    public int CandidateOwnerWhoAmI = -1;
    public int ReservedCompanionSlot = -1;

    public override void OnSpawn(Item item, IEntitySource source)
    {
        CandidateOwnerWhoAmI = -1;
        ReservedCompanionSlot = -1;

        // World drops are intentionally not pre-scanned or claimed. Companions have a manual
        // backpack, not an automatic-loot system; this also keeps high-volume boss drops out of
        // a per-item × per-companion scan on the frame they spawn.
    }
}

/// <summary>
/// Authoritative backpack collection and backpack-to-player transactions. Every mutating entry
/// point validates the live companion, its owner, and the singleplayer authority boundary.
/// </summary>
public static class CompanionLootSystem
{
    public const string BackpackDumpContext = "MoreAIPlayer_BackpackDump";
    public const float SpawnNearOwnerRange = 30f * 16f;

    /// <summary>
    /// World-item collection is opt-in. A virtual player touching arbitrary modded world items
    /// can invoke foreign pickup hooks that were written with only a real player in mind, so a
    /// new or migrated save must remain inert until its owner explicitly enables this feature.
    /// </summary>
    public static bool WorldItemCollectionEnabled =>
        MoreAIPlayerClientConfig.Instance?.CompanionsAutoCollectWorldItems == true;

    private const float PickupReach = 48f;
    private const int ScanInterval = 12;
    private const int ReservationLease = 120;
    private const int FirstBragDelay = 300;
    private const int MinimumBragDelay = 900;
    private const int MaximumBragDelayExclusive = 1501;

    public static bool TryCollectNearbyEquipment(
        CompanionInstance instance,
        Player owner,
        CompanionProfile profile,
        CompanionNavigator navigator)
    {
        // This must be the very first collection gate. In the disabled state do not enumerate
        // Main.ActiveItems, reserve a candidate, steer toward it, clone it, or remove it from
        // the world. Only the companion's local target index is forgotten here; even a legacy
        // target must not make the disabled path touch a world item.
        if (!WorldItemCollectionEnabled)
        {
            ForgetTarget(instance);
            return false;
        }

        if (Main.netMode != NetmodeID.SinglePlayer || instance is null || owner is null ||
            profile is null || navigator is null || !instance.Entity.active ||
            instance.Entity.dead || !owner.active)
        {
            ClearTarget(instance);
            return false;
        }

        if (!TryGetTarget(instance, owner, profile, out Item target))
        {
            ClearTarget(instance);
            if (instance.LootScanCooldown > 0)
            {
                instance.LootScanCooldown--;
                return false;
            }

            instance.LootScanCooldown = ScanInterval;
            target = FindAndReserveNearestCandidate(instance, owner, profile);
            if (target is null)
                return false;
        }

        instance.LootReservationTimeLeft = ReservationLease;
        Player companion = instance.Entity;
        if (Vector2.DistanceSquared(companion.Center, target.Center) <= PickupReach * PickupReach)
        {
            Item moving = target.Clone();
            if (!profile.TryAddToBackpack(moving))
            {
                ClearTarget(instance);
                return false;
            }

            CompanionPickupGlobalItem pickupData = target.GetGlobalItem<CompanionPickupGlobalItem>();
            pickupData.ReservedCompanionSlot = -1;
            target.active = false;
            target.TurnToAir();
            instance.LootTargetItemIndex = -1;
            instance.LootBragCooldown = Math.Min(instance.LootBragCooldown, FirstBragDelay);
            SoundEngine.PlaySound(SoundID.Grab, companion.Center);
            return true;
        }

        navigator.Navigate(companion, target.Center);
        return true;
    }

    public static void UpdateBackpackBrag(CompanionInstance instance, CompanionProfile profile)
    {
        if (!WorldItemCollectionEnabled)
        {
            ForgetTarget(instance);
            return;
        }

        if (Main.netMode != NetmodeID.SinglePlayer || instance is null || profile is null ||
            !instance.Entity.active || instance.Entity.dead)
        {
            return;
        }

        if (instance.LootTargetItemIndex >= 0 && instance.LootReservationTimeLeft > 0)
        {
            instance.LootReservationTimeLeft--;
            if (instance.LootReservationTimeLeft == 0)
                ClearTarget(instance);
        }

        if (!BackpackHasItems(profile))
        {
            instance.LootBragCooldown = FirstBragDelay;
            return;
        }

        if (instance.LootBragCooldown > 0)
        {
            instance.LootBragCooldown--;
            return;
        }

        CombatText.NewText(
            instance.Entity.Hitbox,
            new Color(100, 255, 135),
            Language.GetTextValue("Mods.MoreAIPlayer.CompanionCommand.LootBrag"));
        instance.LootBragCooldown = Main.rand.Next(MinimumBragDelay, MaximumBragDelayExclusive);
    }

    /// <summary>Drops only the forty backpack slots; worn gear and every dedicated slot stay put.</summary>
    public static int DumpBackpack(int companionSlot, Player requester)
    {
        if (!TryGetOwnedContext(companionSlot, requester, out CompanionInstance instance, out CompanionProfile profile))
            return 0;

        int dropped = 0;
        foreach (Item stored in profile.Backpack)
        {
            if (stored.IsAir)
                continue;

            // Vanilla recycles an existing world item when all 400 slots are occupied.
            // A bulk button must not silently delete unrelated loot to make room.
            if (CountActiveWorldItems() >= Main.maxItems)
                break;

            int worldItemIndex = Item.NewItem(
                new EntitySource_Misc(BackpackDumpContext),
                instance.Entity.Hitbox,
                stored.Clone(),
                noBroadcast: false,
                noGrabDelay: false);
            if (worldItemIndex < 0 || worldItemIndex >= Main.maxItems)
                break;

            stored.TurnToAir();
            dropped++;
        }

        instance.LootBragCooldown = FirstBragDelay;
        return dropped;
    }

    /// <summary>
    /// Moves backpack slots in order. Once an item cannot fit completely, its remainder stays
    /// in the same slot and later entries are intentionally left untouched.
    /// </summary>
    public static int TransferBackpack(int companionSlot, Player requester)
    {
        if (!TryGetOwnedContext(companionSlot, requester, out CompanionInstance instance, out CompanionProfile profile))
            return 0;

        int transferred = 0;
        for (int i = 0; i < profile.Backpack.Length; i++)
        {
            Item stored = profile.Backpack[i];
            if (stored.IsAir)
                continue;

            Item remainder = requester.GetItem(
                requester.whoAmI,
                stored.Clone(),
                GetItemSettings.InventoryEntityToPlayerInventorySettings);
            if (!remainder.IsAir)
            {
                profile.Backpack[i] = remainder.Clone();
                break;
            }

            stored.TurnToAir();
            transferred++;
        }

        if (!BackpackHasItems(profile))
            instance.LootBragCooldown = FirstBragDelay;
        return transferred;
    }

    public static void ReleaseReservations(int companionSlot)
    {
        // With collection disabled there can be no new reservation. Avoid a full world-item
        // walk during companion cleanup as well, so migrated saves remain completely inert.
        if (!WorldItemCollectionEnabled || Main.item is null)
            return;

        foreach (Item item in Main.ActiveItems)
        {
            CompanionPickupGlobalItem pickupData = item.GetGlobalItem<CompanionPickupGlobalItem>();
            if (pickupData.ReservedCompanionSlot == companionSlot)
                pickupData.ReservedCompanionSlot = -1;
        }
    }

    private static Item FindAndReserveNearestCandidate(
        CompanionInstance instance,
        Player owner,
        CompanionProfile profile)
    {
        Item best = null;
        float bestDistanceSquared = float.MaxValue;
        foreach (Item item in Main.ActiveItems)
        {
            if (!CanCollect(instance, owner, profile, item, allowUnreserved: true))
                continue;
            if (!IsNearestEligibleCompanion(instance, owner, item))
                continue;

            float distanceSquared = Vector2.DistanceSquared(instance.Entity.Center, item.Center);
            if (distanceSquared >= bestDistanceSquared)
                continue;

            best = item;
            bestDistanceSquared = distanceSquared;
        }

        if (best is null)
            return null;

        best.GetGlobalItem<CompanionPickupGlobalItem>().ReservedCompanionSlot = instance.Slot;
        instance.LootTargetItemIndex = best.whoAmI;
        instance.LootReservationTimeLeft = ReservationLease;
        return best;
    }

    private static bool TryGetTarget(
        CompanionInstance instance,
        Player owner,
        CompanionProfile profile,
        out Item target)
    {
        target = null;
        int index = instance.LootTargetItemIndex;
        if (index < 0 || index >= Main.maxItems)
            return false;

        Item candidate = Main.item[index];
        if (!CanCollect(instance, owner, profile, candidate, allowUnreserved: false))
            return false;

        target = candidate;
        return true;
    }

    private static bool CanCollect(
        CompanionInstance instance,
        Player owner,
        CompanionProfile profile,
        Item item,
        bool allowUnreserved)
    {
        if (item is null || !item.active || item.IsAir || item.maxStack != 1 ||
            BackpackContainsType(profile, item.type) || !HasEmptyBackpackSlot(profile))
        {
            return false;
        }

        CompanionPickupGlobalItem pickupData = item.GetGlobalItem<CompanionPickupGlobalItem>();
        if (pickupData.CandidateOwnerWhoAmI != owner.whoAmI)
            return false;

        return pickupData.ReservedCompanionSlot == instance.Slot ||
            (allowUnreserved && pickupData.ReservedCompanionSlot < 0);
    }

    private static bool IsNearestEligibleCompanion(
        CompanionInstance claimant,
        Player owner,
        Item item)
    {
        float claimantDistance = Vector2.DistanceSquared(claimant.Entity.Center, item.Center);
        foreach (CompanionInstance other in CompanionEntitySystem.All)
        {
            if (other.Slot == claimant.Slot || other.OwnerWhoAmI != owner.whoAmI ||
                !other.Entity.active || other.Entity.dead ||
                !other.TryGetContext(out _, out CompanionProfile otherProfile))
            {
                continue;
            }

            CompanionMode mode = owner.dead
                ? CompanionMode.Attack
                : CompanionModeRules.Effective(otherProfile);
            if (mode is CompanionMode.Garrison or CompanionMode.Fishing ||
                BackpackContainsType(otherProfile, item.type) || !HasEmptyBackpackSlot(otherProfile))
            {
                continue;
            }

            float otherDistance = Vector2.DistanceSquared(other.Entity.Center, item.Center);
            if (otherDistance < claimantDistance ||
                (otherDistance == claimantDistance && other.Slot < claimant.Slot))
            {
                return false;
            }
        }

        return true;
    }

    private static void ClearTarget(CompanionInstance instance)
    {
        if (instance is null)
            return;

        int index = instance.LootTargetItemIndex;
        if (Main.item is not null && index >= 0 && index < Main.maxItems)
        {
            Item item = Main.item[index];
            CompanionPickupGlobalItem pickupData = item.GetGlobalItem<CompanionPickupGlobalItem>();
            if (pickupData.ReservedCompanionSlot == instance.Slot)
                pickupData.ReservedCompanionSlot = -1;
        }

        instance.LootTargetItemIndex = -1;
        instance.LootReservationTimeLeft = 0;
    }

    private static void ForgetTarget(CompanionInstance instance)
    {
        if (instance is null)
            return;

        instance.LootTargetItemIndex = -1;
        instance.LootReservationTimeLeft = 0;
    }

    private static bool TryGetOwnedContext(
        int companionSlot,
        Player requester,
        out CompanionInstance instance,
        out CompanionProfile profile)
    {
        instance = null;
        profile = null;
        if (Main.netMode != NetmodeID.SinglePlayer || requester is null || !requester.active ||
            requester.whoAmI != Main.myPlayer)
        {
            return false;
        }

        CompanionInstance candidate = CompanionEntitySystem.GetBySlot(companionSlot);
        if (candidate is null || candidate.OwnerWhoAmI != requester.whoAmI ||
            !candidate.TryGetContext(out Player owner, out CompanionProfile candidateProfile) ||
            !ReferenceEquals(owner, requester))
        {
            return false;
        }

        instance = candidate;
        profile = candidateProfile;
        return true;
    }

    private static bool BackpackContainsType(CompanionProfile profile, int itemType)
    {
        foreach (Item stored in profile.Backpack)
        {
            if (!stored.IsAir && stored.type == itemType)
                return true;
        }
        return false;
    }

    private static bool HasEmptyBackpackSlot(CompanionProfile profile)
    {
        foreach (Item stored in profile.Backpack)
        {
            if (stored.IsAir)
                return true;
        }
        return false;
    }

    private static bool BackpackHasItems(CompanionProfile profile)
    {
        foreach (Item stored in profile.Backpack)
        {
            if (!stored.IsAir)
                return true;
        }
        return false;
    }

    private static int CountActiveWorldItems()
    {
        int count = 0;
        foreach (Item _ in Main.ActiveItems)
            count++;
        return count;
    }
}
