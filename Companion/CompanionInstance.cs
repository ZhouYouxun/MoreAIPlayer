using Microsoft.Xna.Framework;
using MoreAIPlayer.Companion.AI;
using Terraria;
using Terraria.ID;
using Terraria.Localization;

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

    private Automation.AutoCompanionBrain autoBrain;

    /// <summary>The automation brain, created on demand only for the world's automation companion.
    /// Ordinary companions never allocate it.</summary>
    public Automation.AutoCompanionBrain AutoBrain => autoBrain ??= new();

    /// <summary>Cheap per-tick cache of <see cref="CompanionProfile.IsAutoRunning"/>, refreshed in
    /// <see cref="BindProfile"/>. Lets every enumeration hook (squad spread, command book, build
    /// material sourcing, manager panel, whip buff) exclude the automation companion without
    /// re-resolving its profile each time.</summary>
    public bool IsAutoIdentity;

    /// <summary>Cached persistent origin used by the optional player-hostility damage pass.</summary>
    public bool IsOrdinaryDebugIdentity;

    /// <summary>One-hit-per-swing guards for the custom PvP bridge. Single-player is the only
    /// synthetic-player runtime, so one real-player bit in each direction is sufficient.</summary>
    public bool HitByRealPlayerThisSwing;
    public bool HitRealPlayerThisSwing;

    /// <summary>Independent maximum life/mana for the automation companion, refreshed each tick in
    /// <see cref="BindProfile"/> from its own crystal/fruit counts. -1 means "not an automation
    /// companion — use the owner-shared maximum instead." The life-persistence machinery reads
    /// these so the companion grows from its own progression rather than mirroring the owner.</summary>
    public int AutoMaxLife = -1;
    public int AutoMaxMana = -1;

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

    /// <summary>Consecutive failures inside this companion's virtual ItemCheck only.</summary>
    public int ItemUseFaultStreak { get; private set; }

    /// <summary>Ticks left before this companion may attempt the faulting item use again.</summary>
    public int ItemUseBackoffTicks { get; private set; }

    /// <summary>Inventory slot that last faulted, retained only while the retry backoff is active.</summary>
    public int BlockedItemUseInventorySlot { get; private set; } = -1;

    public bool IsItemUseBlocked(int inventorySlot) => ItemUseBackoffTicks > 0 &&
        BlockedItemUseInventorySlot == inventorySlot;

    public void TickItemUseBackoff()
    {
        if (ItemUseBackoffTicks <= 0)
            return;
        if (--ItemUseBackoffTicks == 0)
            BlockedItemUseInventorySlot = -1;
    }

    public void RegisterItemUseFailure(int inventorySlot)
    {
        ItemUseFaultStreak = System.Math.Min(ItemUseFaultStreak + 1, 6);
        BlockedItemUseInventorySlot = inventorySlot;
        ItemUseBackoffTicks = System.Math.Min(1800, 60 << (ItemUseFaultStreak - 1));
    }

    public void RegisterItemUseSuccess()
    {
        ItemUseFaultStreak = 0;
        ItemUseBackoffTicks = 0;
        BlockedItemUseInventorySlot = -1;
    }

    /// <summary>Consecutive ticks this companion's own per-tick update threw. A clean tick resets
    /// it; reaching <see cref="MaxFaultStreak"/> retires the companion instead of letting it fault
    /// forever, which is the minimal-damage fallback — remove the one broken thing, keep the game.</summary>
    public int FaultStreak;

    /// <summary>How many consecutive faulting ticks a companion is given before it is retired.</summary>
    public const int MaxFaultStreak = 5;

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
    /// The companion's real current health, kept outside Player's vanilla life-crystal stat
    /// rebuild. Synthetic players intentionally do not inherit the owner's permanent crystal
    /// history, so vanilla resets their current life around 100 while rebuilding stats each
    /// update; this value preserves damage and healing across that rebuild.
    /// </summary>
    public int PersistentLife;

    /// <summary>Whether <see cref="PersistentLife"/> has been seeded for this summon.</summary>
    public bool PersistentLifeInitialized;

    /// <summary>The previous shared maximum, used to keep a fully healed companion full when
    /// the owner's maximum life changes.</summary>
    public int LastSharedLifeMax;

    /// <summary>
    /// Companion-owned general post-hit immunity. It deliberately does not reuse Player.immune:
    /// that field is maintained by the local-player update and can become permanently stale on
    /// a synthetic player.
    /// </summary>
    public int GeneralDamageImmunityTicks;

    /// <summary>
    /// Companion-owned immunity counters indexed like ImmunityCooldownID. Slots 0, 1, 3 and 4
    /// are independent in vanilla; keeping the same shape prevents a boss, spike or lava hit
    /// from incorrectly sharing the ordinary contact cooldown.
    /// </summary>
    public int[] DamageCooldownTicks { get; } = new int[5];

    /// <summary>Frame on which the companion-owned cooldowns were last advanced.</summary>
    public ulong DamageCooldownFrame = ulong.MaxValue;

    /// <summary>
    /// Extra guard around automatic healing. It mirrors the potion's vanilla cooldown when one
    /// exists and still prevents a nonstandard zero-cooldown healing item from being consumed
    /// every frame.
    /// </summary>
    public int AutoHealCooldownTicks;
    public int AutoManaCooldownTicks;

    /// <summary>Owner/identity of the last friendly whip that granted the companion buff. A whip
    /// can overlap for several update ticks; this pair makes one swing grant the effect once.</summary>
    public int LastWhipOwner = -1;
    public int LastWhipIdentity = -1;

    /// <summary>World item currently reserved for this companion's backpack pickup.</summary>
    public int LootTargetItemIndex = -1;

    /// <summary>Small scan throttle; world-item selection does not need to run every frame.</summary>
    public int LootScanCooldown;

    /// <summary>
    /// Reservation lease for the current world item. Combat and higher-priority work can
    /// interrupt collection; expiry lets another companion take over instead of locking the
    /// item forever.
    /// </summary>
    public int LootReservationTimeLeft;

    /// <summary>Ticks until the next "look what I found" overhead line.</summary>
    public int LootBragCooldown = 300;

    /// <summary>The construction cell currently reserved by this companion.</summary>
    public int BuildJobId = -1;

    /// <summary>Short delay between successful placements so construction remains readable
    /// and multiple builders can visibly divide the work.</summary>
    public int BuildActionCooldown;

    /// <summary>
    /// Item.type that started the currently-running itemAnimation cycle. Self-sustaining
    /// "holdout" weapons (Calamity's GrandDad and similar) keep itemAnimation pinned above 0
    /// indefinitely between swings, so this is how the brain notices the equipped item was
    /// swapped out from under a still-running use (via the backpack UI) instead of waiting
    /// forever for a natural itemAnimation == 0 that will never come.
    /// </summary>
    public int ActiveItemType = -1;

    /// <summary>The world-space screen rectangle and center captured when the wheel issues a
    /// patrol/free-work command. New specialist controllers use these without changing the old
    /// navigator or combat engine.</summary>
    public Rectangle CommandArea;
    public Vector2 CommandAnchor;
    public bool HasCommandArea;

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
        IsAutoIdentity = profile.IsAutoRunning;
        IsOrdinaryDebugIdentity = profile.Origin == CompanionOrigin.OrdinaryDebug;
        if (IsAutoIdentity)
        {
            AutoMaxLife = profile.AutoState.MaxLife();
            AutoMaxMana = profile.AutoState.MaxMana();
        }
        else
        {
            AutoMaxLife = -1;
            AutoMaxMana = -1;
        }
        me.active = true;
        me.whoAmI = Slot;
        me.name = string.IsNullOrWhiteSpace(profile.Name)
            ? Language.GetTextValue("Mods.MoreAIPlayer.CompanionUI.DefaultName")
            : profile.Name;
        me.team = owner.team;
        me.hostile = false;
        me.difficulty = 0;
        me.ghost = false;
        me.immuneAlpha = 0;
        me.outOfRange = false;

        MirrorOwnerBeneficialBuffs(owner, me);

        profile.ApplyAppearanceTo(me, owner);

        for (int i = 0; i < CompanionPlayer.HotbarSize; i++)
            AssignIfChanged(me.inventory, i, profile.Hotbar[i]);
        for (int i = 0; i < CompanionPlayer.BackpackSize; i++)
            AssignIfChanged(me.inventory, CompanionPlayer.HotbarSize + i, profile.Backpack[i]);
        for (int i = 0; i < CompanionPlayer.AmmoSize; i++)
            AssignIfChanged(me.inventory, AmmoInventoryIndex(i), profile.Ammo[i]);
        AssignIfChanged(me.inventory, NetInventoryIndex, profile.NetTool);
        AssignIfChanged(me.inventory, RodInventoryIndex, profile.RodTool);
        AssignIfChanged(me.miscEquips, 4, profile.HookTool);
        AssignIfChanged(me.miscEquips, 3, profile.MountTool);

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
        // into statLifeMax2), so the companion always shares the owner's maximum health — except the
        // automation companion, which grows its own base from crystals it collects. Accessories
        // still add on top because vanilla builds statLifeMax2 from this statLifeMax each update.
        me.statLifeMax = AutoMaxLife > 0 ? AutoMaxLife : System.Math.Max(100, owner.statLifeMax2);
        if (me.statLife > me.statLifeMax)
            me.statLife = me.statLifeMax;
        if (me.statLife < 1 && !me.dead)
        {
            // Vanilla Player.Hurt only calls KillMe for Main.myPlayer. A synthetic companion can
            // therefore reach zero life without vanilla setting dead; preserve that lethal state
            // so CompanionBrain can start its normal timed revive instead of refilling it here.
            me.dead = true;
        }

        // Mana Crystal progression belongs to the owner just like Life Crystal progression, again
        // except the automation companion, which uses its own consumed-crystal count. The brain
        // already keeps a companion's current mana full; copying the maximum here is what makes the
        // tooltip truthful instead of merely descriptive.
        me.statManaMax = AutoMaxMana > 0 ? AutoMaxMana : System.Math.Max(20, owner.statManaMax2);
        if (me.statMana > me.statManaMax)
            me.statMana = me.statManaMax;
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

    /// <summary>
    /// Mirrors the main player's active beneficial timed buffs every update. This covers food,
    /// combat potions and ordinary modded buff potions without copying debuffs, permanent pet/
    /// mount-style buffs, or potion sickness. The owner's remaining duration is authoritative,
    /// so the companion cannot extend a buff by existing.
    /// </summary>
    private static void MirrorOwnerBeneficialBuffs(Player owner, Player companion)
    {
        for (int i = 0; i < Player.MaxBuffs; i++)
        {
            int type = owner.buffType[i];
            int time = owner.buffTime[i];
            if (type <= 0 || time <= 0 || type >= Main.debuff.Length || Main.debuff[type] ||
                type >= BuffID.Sets.TimeLeftDoesNotDecrease.Length || BuffID.Sets.TimeLeftDoesNotDecrease[type])
                continue;
            companion.AddBuff(type, time, quiet: true);
        }
    }

    private static void AssignIfChanged(Item[] destination, int index, Item source)
    {
        if (!ReferenceEquals(destination[index], source))
            destination[index] = source;
    }
}
