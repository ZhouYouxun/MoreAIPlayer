using Microsoft.Xna.Framework;
using MoreAIPlayer.Companion.AI;
using Terraria;

namespace MoreAIPlayer.Companion;

/// <summary>
/// A live companion occupying a real slot in Main.player. Because the entity is an active
/// Player, vanilla runs its full update for it: movement physics, platform drop-through,
/// wings, item use styles, equipment effects and rendering all work unmodified.
/// </summary>
public sealed class CompanionInstance
{
    public int Slot { get; init; }
    public int OwnerWhoAmI { get; init; }
    public int ProfileId { get; init; }
    public Player Entity { get; init; }
    public Player OriginalPlayer { get; init; }
    public CompanionBrain Brain { get; } = new();

    /// <summary>World position the companion aims at while its ItemCheck runs (virtual mouse).</summary>
    public Vector2 AimWorld;

    /// <summary>Tile the companion mines/chops this tick. Overrides Player.tileTargetX/Y during ItemCheck.</summary>
    public Point? WorkTile;

    /// <summary>True while the brain is holding the virtual right mouse button (alt weapon use).</summary>
    public bool WantsAltUse;

    /// <summary>True while the brain is holding either virtual attack button this tick.</summary>
    public bool WantsUseItem;

    /// <summary>
    /// How many times the ItemCheck detour has actually taken over for this companion.
    ///
    /// Everything about projectile spawning and aiming depends on that detour running: vanilla
    /// gates both behind <c>whoAmI == Main.myPlayer</c>, so if the detour is ever bypassed the
    /// companion animates and plays its sounds but fires nothing and aims at the real player's
    /// cursor instead of its target. That failure is indistinguishable in game from a dozen
    /// unrelated causes, so the count is displayed live rather than inferred.
    /// </summary>
    public long ItemCheckTakeovers;

    /// <summary>Ticks since the detour last took over, so a detour that stops firing is obvious.</summary>
    public int TicksSinceItemCheck = int.MaxValue / 2;

    /// <summary>The update tick for which the brain prepared virtual input.</summary>
    public ulong ItemCheckPreparedFrame = ulong.MaxValue;

    /// <summary>The most recent update tick on which the ItemCheck detour actually ran.</summary>
    public ulong ItemCheckDetourFrame = ulong.MaxValue;

    /// <summary>Number of frames whose normal player update skipped ItemCheck and needed recovery.</summary>
    public long RecoveredItemChecks;

    /// <summary>Consecutive normal-update misses; reset as soon as the ordinary path returns.</summary>
    public int ConsecutiveItemCheckMisses;

    /// <summary>Invalid/default aim vectors repaired before they reached weapon code.</summary>
    public long AimCorrections;

    /// <summary>Times another system replaced Main.player[Slot] and the inactive slot was reclaimed.</summary>
    public long SlotRepairs;

    /// <summary>
    /// A "go here" marker placed by the command book. While set, the companion paths to it
    /// (particles mark the spot). The brain owns its lifetime: it clears on arrival, on the
    /// timer running out, or when the book cancels/replaces it.
    /// </summary>
    public Vector2? Waypoint;

    /// <summary>Ticks left before an unreached <see cref="Waypoint"/> gives up and vanishes.</summary>
    public int WaypointTimeLeft;

    /// <summary>How long a freshly placed waypoint stands if the companion never reaches it (5s).</summary>
    public const int WaypointLifetime = 300;

    /// <summary>Ticks left before a dead companion revives at the owner's side. 0 means alive.
    /// Read by the UI to draw the revive countdown over the owner's head.</summary>
    public int ReviveTicksLeft;

    /// <summary>
    /// Item.type that started the currently-running itemAnimation cycle. Self-sustaining
    /// "holdout" weapons (Calamity's GrandDad and similar) keep itemAnimation pinned above 0
    /// indefinitely between swings, so this is how the brain notices the equipped item was
    /// swapped out from under a still-running use (via the backpack UI) instead of waiting
    /// forever for a natural itemAnimation == 0 that will never come.
    /// </summary>
    public int ActiveItemType = -1;

    public Player Owner => OwnerWhoAmI >= 0 && OwnerWhoAmI < Main.maxPlayers
        ? Main.player[OwnerWhoAmI]
        : Main.LocalPlayer;

    /// <summary>
    /// Immediately stops a channelled/holdout weapon use in progress: releases channel,
    /// zeroes the animation/time counters and kills the player's own held projectile (the
    /// vanilla Player.heldProj convention holdouts use). Called when the backpack UI opens
    /// (the user is about to look at or swap gear) and whenever the brain notices the held
    /// item no longer matches what started the current use.
    /// </summary>
    public void ForceReleaseChanneledWeapon()
    {
        Player me = Entity;
        if (!me.active || !me.HeldItem.channel)
            return;

        me.channel = false;
        me.controlUseItem = false;
        me.controlUseTile = false;
        me.itemAnimation = 0;
        me.itemTime = 0;
        me.altFunctionUse = 0;
        WantsUseItem = false;
        WantsAltUse = false;
        ActiveItemType = -1;

        if (me.heldProj >= 0 && me.heldProj < Main.maxProjectiles)
        {
            Projectile heldProjectile = Main.projectile[me.heldProj];
            if (heldProjectile.active && heldProjectile.owner == me.whoAmI)
                heldProjectile.Kill();
        }
    }

    public bool TryGetContext(out Player owner, out CompanionProfile profile)
    {
        owner = null;
        profile = null;
        if (OwnerWhoAmI < 0 || OwnerWhoAmI >= Main.maxPlayers)
            return false;

        Player candidate = Main.player[OwnerWhoAmI];
        if (!candidate.active)
            return false;

        profile = candidate.GetModPlayer<CompanionPlayer>().GetProfile(ProfileId);
        if (profile is null)
            return false;

        owner = candidate;
        return true;
    }

    /// <summary>
    /// Rebinds the profile onto the player entity. Inventory and equipment slots alias the
    /// profile's Item instances directly, so gameplay mutations (ammo use, durability-like
    /// stack changes) flow into the saved profile and UI edits appear on the entity at once.
    ///
    /// The profile normally keeps the same Item objects for its whole lifetime. Reassigning
    /// every one of those references once per tick is redundant; the per-slot guards below
    /// still pick up an ItemSlot/UI replacement immediately, while leaving in-place gameplay
    /// changes (stack use, prefixes, etc.) shared exactly as before.
    /// </summary>
    public void BindProfile(Player owner, CompanionProfile profile)
    {
        Player me = Entity;
        me.active = true;
        me.whoAmI = Slot;
        me.name = string.IsNullOrWhiteSpace(profile.Name) ? "幻影伙伴" : profile.Name;
        me.team = owner.team;
        me.hostile = false;
        me.difficulty = 0;
        me.ghost = false;
        me.immuneAlpha = 0;
        me.outOfRange = false;

        profile.ApplyAppearanceTo(me, owner);

        for (int i = 0; i < CompanionPlayer.HotbarSize; i++)
            AssignIfChanged(me.inventory, i, profile.Hotbar[i]);
        for (int i = 0; i < CompanionPlayer.BackpackSize; i++)
            AssignIfChanged(me.inventory, CompanionPlayer.HotbarSize + i, profile.Backpack[i]);
        for (int i = 0; i < CompanionPlayer.AmmoSize; i++)
            AssignIfChanged(me.inventory, AmmoInventoryIndex(i), profile.Ammo[i]);
        AssignIfChanged(me.inventory, NetInventoryIndex, profile.NetTool);
        AssignIfChanged(me.inventory, RodInventoryIndex, profile.RodTool);

        for (int i = 0; i < CompanionPlayer.ArmorSize; i++)
        {
            AssignIfChanged(me.armor, i, profile.Armor[i]);
            AssignIfChanged(me.armor, 10 + i, profile.Vanity[i]);
            AssignIfChanged(me.dye, i, profile.Dyes[i]);
        }

        // Accessories past the world's limit are swapped for air, so a slot the player filled
        // before hardmode simply stops applying until the world catches up.
        int unlockedAccessories = CompanionSlotRules.UnlockedAccessorySlots();
        for (int i = 0; i < CompanionPlayer.AccessorySize; i++)
            AssignIfChanged(me.armor, 3 + i, profile.AccessoryForSlot(i, unlockedAccessories));

        // Max HP tracks the owner's effective max (Life Fruit, accessories, buffs all folded
        // into statLifeMax2), so the companion always shares the owner's health pool.
        me.statLifeMax = System.Math.Max(100, owner.statLifeMax2);
        if (me.statLife > me.statLifeMax)
            me.statLife = me.statLifeMax;
        if (me.statLife < 1 && !me.dead)
            me.statLife = me.statLifeMax;
    }

    /// <summary>Maps a profile hotbar/backpack position to the entity's inventory index.</summary>
    public static int HotbarToInventoryIndex(int hotbarSlot) => hotbarSlot;

    public static int BackpackToInventoryIndex(int backpackSlot) => CompanionPlayer.HotbarSize + backpackSlot;

    /// <summary>
    /// Where an ammo slot lands in the entity's inventory. Vanilla's layout only has four real
    /// ammo slots (54-57), and Player.ChooseAmmo scans those first and then indices 0-53, so
    /// the two extra slots are mapped onto the coin slots (50-51): still found by ChooseAmmo,
    /// just ranked after the four native ones. Coin slots are free real estate here — a
    /// companion never picks up, spends or drops money (it never runs KillMe, so DropCoins
    /// never fires either).
    /// </summary>
    public static int AmmoInventoryIndex(int ammoSlot) =>
        ammoSlot < NativeAmmoSlots ? 54 + ammoSlot : 50 + (ammoSlot - NativeAmmoSlots);

    /// <summary>Ammo slots vanilla itself treats as ammo slots; the rest are the overflow above.</summary>
    public const int NativeAmmoSlots = 4;

    /// <summary>The bug net lives in the last free coin slot, for the same reason the overflow
    /// ammo does. The brain selects it by index when it goes for a critter.</summary>
    public const int NetInventoryIndex = 52;

    /// <summary>The fishing pole takes the fourth and final coin slot. That exhausts the coin
    /// row — any further dedicated tool slot will have to borrow from the backpack instead.</summary>
    public const int RodInventoryIndex = 53;

    private static void AssignIfChanged(Item[] destination, int index, Item source)
    {
        if (!ReferenceEquals(destination[index], source))
            destination[index] = source;
    }
}
