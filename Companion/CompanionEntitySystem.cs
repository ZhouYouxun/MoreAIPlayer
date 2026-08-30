using System;
using System.Collections.Generic;
using System.Reflection;
using Microsoft.Xna.Framework;
using MoreAIPlayer.Companion.Automation;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;
using Terraria.Localization;
using Terraria.ModLoader;

namespace MoreAIPlayer.Companion;

/// <summary>
/// Registers companions as real active players in Main.player (singleplayer only).
/// Vanilla's player update loop runs their physics, item use and rendering. The AI writes
/// simulated controls each tick in PreUpdatePlayers. Projectile spawning and mining inside
/// Player.ItemCheck are local-player-gated in vanilla, so a detour temporarily makes the
/// companion the "local" player (with a virtual mouse) for the duration of its own ItemCheck.
/// </summary>
public sealed class CompanionEntitySystem : ModSystem
{
    private delegate void OrigItemCheck(Player self);
    private delegate void ItemCheckDetour(OrigItemCheck orig, Player self);
    private delegate void OrigProjectileUpdate(Projectile projectile, int index);
    private delegate void ProjectileUpdateDetour(OrigProjectileUpdate orig, Projectile projectile, int index);

    private static readonly Dictionary<int, CompanionInstance> BySlot = [];
    // Terraria asks every ModPlayer whether a Player is immune before entering Hurt. Ordinary
    // engine routing must always reject synthetic companions; only this tightly-scoped owned
    // evaluation is allowed through. The game loop is single-threaded, and the previous value is
    // still restored in finally so a foreign hook can never leave the gate open.
    private static Player ownedDamageEvaluationTarget;
    private static bool hooksInstalled;
    private static long leakedContextRecoveries;
    private static long projectileOwnerCorrections;

    public static int HoveredSlot { get; private set; } = -1;
    public static bool HooksInstalled => hooksInstalled;
    public static long LeakedContextRecoveries => leakedContextRecoveries;
    public static long ProjectileOwnerCorrections => projectileOwnerCorrections;

    public static IEnumerable<CompanionInstance> All => BySlot.Values;

    public static bool IsCompanionSlot(int slot) => BySlot.ContainsKey(slot);

    public static CompanionInstance GetBySlot(int slot) =>
        BySlot.TryGetValue(slot, out CompanionInstance instance) ? instance : null;

    /// <summary>
    /// The one authoritative test for "this Player object is a live companion". Every global
    /// hook that changes player behaviour must go through it, because those hooks also run
    /// for real network players: a slot number alone proves nothing, so this compares the
    /// entity's identity against the synthetic Player this mod created. Multiplayer can never
    /// reach true here — Summon refuses to run outside singleplayer, so no such entity exists.
    /// </summary>
    public static bool IsCompanion(Player player)
    {
        if (player is null || !CompanionRuntimePolicy.CanRunSyntheticPlayers)
            return false;

        CompanionInstance instance = GetBySlot(player.whoAmI);
        return instance is not null && ReferenceEquals(instance.Entity, player);
    }

    internal static bool IsOwnedDamageEvaluation(Player player) =>
        ReferenceEquals(ownedDamageEvaluationTarget, player);

    /// <summary>True when this Player object is the world's live automation companion. Used by the
    /// isolation hooks (no whip buff, no player-sourced interactions) that must treat the automation
    /// companion differently from an ordinary one.</summary>
    public static bool IsAutoCompanion(Player player)
    {
        if (player is null)
            return false;
        CompanionInstance instance = GetBySlot(player.whoAmI);
        return instance is not null && ReferenceEquals(instance.Entity, player) && instance.IsAutoIdentity;
    }

    /// <summary>The logical faction predicate for the opt-in entertainment rule. It checks the
    /// persistent profile origin cached on the live instance, not merely whether automation is
    /// running this tick.</summary>
    public static bool IsOrdinaryHostileCompanion(Player player)
    {
        if (!MoreAIPlayerServerConfig.OrdinaryHostilityEnabled || !IsCompanion(player))
            return false;
        CompanionInstance instance = GetBySlot(player.whoAmI);
        return instance is not null && instance.IsOrdinaryDebugIdentity;
    }

    public static bool AreCompanionFactionAllies(Player first, Player second) =>
        IsOrdinaryHostileCompanion(first) && IsOrdinaryHostileCompanion(second);

    public static bool CanPlayerDamageCompanion(Player attacker, CompanionInstance target) =>
        CompanionRuntimePolicy.IsAuthoritativeDamageSide &&
        attacker is not null && attacker.active && !attacker.dead && !IsCompanion(attacker) &&
        !Hunter.HunterSystem.IsHunter(attacker) &&
        target is not null && IsOrdinaryHostileCompanion(target.Entity);

    public static bool CanCompanionDamagePlayer(CompanionInstance attacker, Player target) =>
        CompanionRuntimePolicy.IsAuthoritativeDamageSide &&
        attacker is not null && IsOrdinaryHostileCompanion(attacker.Entity) &&
        target is not null && target.active && !target.dead && !IsCompanion(target) &&
        !Hunter.HunterSystem.IsHunter(target);

    /// <summary>Used by owner-side reactions so an incidental companion hit cannot be mistaken for
    /// an enemy attack and cause a rescue/retaliation command.</summary>
    public static bool IsCompanionDamageSource(PlayerDeathReason source) =>
        source.SourcePlayerIndex >= 0 && IsCompanionSlot(source.SourcePlayerIndex);

    public static CompanionInstance FindByProfile(int ownerWhoAmI, int profileId)
    {
        foreach (CompanionInstance instance in BySlot.Values)
        {
            if (instance.OwnerWhoAmI == ownerWhoAmI && instance.ProfileId == profileId)
                return instance;
        }
        return null;
    }

    /// <summary>Number of live ordinary companions a player currently has out. Each one occupies
    /// one of that player's minion slots, so this is also how many summon slots they have spent.
    /// The automation companion is independent and is deliberately excluded — it neither requires
    /// nor consumes an owner minion slot.</summary>
    public static int CountActiveOwnedBy(int ownerWhoAmI)
    {
        int count = 0;
        foreach (CompanionInstance instance in BySlot.Values)
        {
            if (instance.OwnerWhoAmI == ownerWhoAmI && instance.Entity.active && !instance.IsAutoIdentity)
                count++;
        }
        return count;
    }

    /// <summary>
    /// This companion's place in its owner's squad, in one pass: <paramref name="ordinal"/> is its
    /// stable 0-based index (how many active squadmates sit in a lower slot) and
    /// <paramref name="count"/> is the squad size. The follow behaviour uses the pair to fan
    /// companions out around the owner instead of stacking them on one point. Stable because slots
    /// are stable; cheap because a squad is only ever a handful of entries.
    /// </summary>
    public static void SquadPosition(CompanionInstance self, out int ordinal, out int count)
    {
        ordinal = 0;
        count = 0;
        foreach (CompanionInstance instance in BySlot.Values)
        {
            // The automation companion is never part of the owner's squad fan-out.
            if (instance.OwnerWhoAmI != self.OwnerWhoAmI || !instance.Entity.active || instance.IsAutoIdentity)
                continue;
            count++;
            if (instance.Slot < self.Slot)
                ordinal++;
        }
    }

    public override void Load()
    {
        // A dedicated server never hosts a companion (Summon is singleplayer-only), so the
        // detours below would do nothing there except add a delegate hop to every ItemCheck
        // and every projectile update, for every connected player, forever.
        if (CompanionRuntimePolicy.IsDedicatedServer)
            return;

        MethodInfo itemCheck = typeof(Player).GetMethod(nameof(Player.ItemCheck), BindingFlags.Instance | BindingFlags.Public)
            ?? throw new MissingMethodException(typeof(Player).FullName, nameof(Player.ItemCheck));
        MonoModHooks.Add(itemCheck, (ItemCheckDetour)Player_ItemCheck);

        // The whole projectile update (AI, movement, owner-side Damage, every mod hook)
        // runs under the companion's identity inside try/finally. Holdout projectiles need
        // the local-player context, and finally guarantees the swap can never leak even if
        // another mod's code throws mid-update (leaks were hijacking the camera).
        MethodInfo projectileUpdate = typeof(Projectile).GetMethod(nameof(Projectile.Update), BindingFlags.Instance | BindingFlags.Public)
            ?? throw new MissingMethodException(typeof(Projectile).FullName, nameof(Projectile.Update));
        MonoModHooks.Add(projectileUpdate, (ProjectileUpdateDetour)Projectile_Update);
        hooksInstalled = true;
    }

    public override void Unload()
    {
        ReleaseAll();
        CompanionModBridge.ClearCaches();
        CompanionWeaponOverrides.ClearCaches();
        ResetRuntimeState();
    }

    public override void OnWorldLoad()
    {
        ReleaseAll();
        ResetRuntimeState(keepHookState: true);
    }

    public override void OnWorldUnload()
    {
        ReleaseAll();
        ResetRuntimeState(keepHookState: true);
    }

    public static bool Summon(Player owner, CompanionProfile profile, Vector2 worldPosition)
    {
        if (!CompanionRuntimePolicy.CanRunSyntheticPlayers || owner is null || profile is null ||
            !owner.active || owner.whoAmI != Main.myPlayer || FindByProfile(owner.whoAmI, profile.Id) is not null)
            return false;

        // Completed automation profiles remain owned by the Automation Tome forever. A damaged
        // binding or another summon item may not reinterpret one as an ordinary escort.
        if (profile.IsAutomationProfile && !profile.IsAutoRunning)
            return false;

        VirtualPlayerExecutionContext.RegisterRealPlayer(owner);

        // The automation companion is independent of the owner's minion economy: it does not need a
        // free minion slot to be summoned and never spends one while it runs.
        if (!profile.IsAutoRunning && owner.slotsMinions + 1.0f > owner.maxMinions)
        {
            Main.NewText(Language.GetTextValue("Mods.MoreAIPlayer.CompanionCommand.MinionSlotsFull"), 255, 160, 120);
            return false;
        }

        int slot = FindFreeSlot();
        if (slot < 0)
            return false;

        Player original = Main.player[slot];
        Player entity = new() { whoAmI = slot };
        Main.player[slot] = entity;

        CompanionInstance instance = new()
        {
            Slot = slot,
            OwnerWhoAmI = owner.whoAmI,
            ProfileId = profile.Id,
            Entity = entity,
            OriginalPlayer = original
        };
        BySlot[slot] = instance;

        try
        {
            instance.BindProfile(owner, profile);
            // A freshly constructed Player has no gameplay direction until vanilla updates it.
            // Give its very first frame a valid render/aim direction instead of allowing a
            // zero-direction half-spawn to reach foreign player hooks.
            entity.direction = owner.direction == 0 ? 1 : owner.direction;
            entity.Center = worldPosition;
            entity.velocity = Vector2.Zero;
            entity.fallStart = (int)(entity.position.Y / 16f);
            entity.statLife = entity.statLifeMax;
            entity.dead = false;
            if (profile.IsAutoRunning)
                profile.AutoState.RestoreVitals(entity, instance);
            instance.PersistentLife = entity.statLife;
            instance.PersistentLifeInitialized = true;
            instance.LastSharedLifeMax = entity.statLifeMax;
            ResetCompanionDamageState(instance);
            SpawnRecallDustBurst(entity.Center);
            AnnounceJoin(entity.name);
            return true;
        }
        catch (Exception exception)
        {
            // A modded Player/Item hook can fail during the first bind. Never leave its partly
            // initialized Player active in Main.player: that looks like an invisible, frozen
            // companion and may keep replaying a held-item sound every update.
            ModContent.GetInstance<MoreAIPlayer>().Logger.Error(
                $"Failed to initialize synthetic player slot {slot}; rolling it back.", exception);
            Release(slot);
            return false;
        }
    }

    // Identical to the multiplayer join/leave broadcasts in NetMessage.SyncOnePlayer:
    // NetworkText.FromKey(Lang.mp[19/20].Key, name) with Color(255, 240, 20).
    private static void AnnounceJoin(string name) =>
        Main.NewText(Lang.mp[19].Format(name), 255, 240, 20);

    private static void AnnounceLeave(string name) =>
        Main.NewText(Lang.mp[20].Format(name), 255, 240, 20);

    /// <summary>
    /// Dismisses every ordinary companion the player has out (the "Dismiss All" button). The
    /// automation companion is left alone: it is managed only through the automation book, is
    /// excluded from the manager panel, and holds live world/progression state a bulk squad
    /// dismissal must not throw away. Full teardown (world unload, mod unload, netMode change) goes
    /// through <see cref="ReleaseAll"/> instead, which does include it.
    /// </summary>
    public static void DismissOwnedBy(int ownerWhoAmI)
    {
        List<int> slots = [];
        foreach ((int slot, CompanionInstance instance) in BySlot)
        {
            if (instance.OwnerWhoAmI == ownerWhoAmI && !instance.IsAutoIdentity)
                slots.Add(slot);
        }

        foreach (int slot in slots)
            Dismiss(slot);
    }

    public static void Dismiss(int slot)
    {
        if (!BySlot.TryGetValue(slot, out CompanionInstance instance))
            return;

        // Cosmetic work must never be allowed to strand an active synthetic Player. In
        // particular, a projectile's Kill hook belongs to arbitrary mod code and may throw.
        // Release owns the non-negotiable cleanup in a finally block; the effects are best-effort.
        try
        {
            SpawnRecallDustBurst(instance.Entity.Center);
            AnnounceLeave(instance.Entity.name);
        }
        catch (Exception exception)
        {
            // Keep a failed visual/chat callback local to this companion. DismissOwnedBy loops
            // over this method, so letting it escape would make "dismiss all" appear to stop
            // after the first member.
            ModContent.GetInstance<MoreAIPlayer>().Logger.Error(
                $"Failed to play dismissal feedback for synthetic player slot {slot}.", exception);
        }
        finally
        {
            Release(slot);
        }
    }

    private static int FindFreeSlot()
    {
        for (int slot = Main.maxPlayers - 1; slot >= 0; slot--)
        {
            if (slot == Main.myPlayer || BySlot.ContainsKey(slot) || Main.player[slot].active)
                continue;

            bool ownsProjectile = false;
            foreach (Projectile projectile in Main.ActiveProjectiles)
            {
                if (projectile.owner == slot)
                {
                    ownsProjectile = true;
                    break;
                }
            }

            if (!ownsProjectile)
                return slot;
        }
        return -1;
    }

    private static void Release(int slot)
    {
        if (!BySlot.TryGetValue(slot, out CompanionInstance instance))
            return;

        // Restore while the slot is still registered. Foreign projectile OnKill hooks run below;
        // removing first made a leaked identity invisible to IsVirtualSlot during cleanup.
        RestoreHijackedLocalPlayer();

        try
        {
            // Reservations and projectiles are independent cleanup responsibilities. Do not
            // let a bad global-item hook prevent stale bullets from being removed, or vice
            // versa; either failure must still reach the final player-slot restoration.
            CompanionLootSystem.ReleaseReservations(slot);
        }
        catch (Exception exception)
        {
            ModContent.GetInstance<MoreAIPlayer>().Logger.Error(
                $"Failed to release item reservations for synthetic player slot {slot}.", exception);
        }

        try
        {
            KillOwnedProjectiles(slot);
        }
        catch (Exception exception)
        {
            ModContent.GetInstance<MoreAIPlayer>().Logger.Error(
                $"Failed to release projectiles for synthetic player slot {slot}.", exception);
        }
        finally
        {
            DeactivateAndRestoreSlot(slot, instance);
            // Keep the registration alive through every foreign OnKill hook, restore any identity
            // it leaked, and only then make the synthetic slot unrecognisable to the guard.
            RestoreHijackedLocalPlayer();
            BySlot.Remove(slot);
        }
    }

    /// <summary>
    /// Projectiles outlive their owner's slot: nothing in vanilla removes ordinary bullets,
    /// minions or sentries when a synthetic player is released. Left alone they keep an invalid
    /// owner identity and prevent the slot from being safely reused.
    ///
    /// Singleplayer only, and not merely because that is the only mode that has companions.
    /// ReleaseAll is deliberately also the netMode-changed escape hatch, so this can run with
    /// BySlot holding stale entries while the slot numbers in it now belong to real network
    /// players — killing their projectiles client-side would be a desync (and, from their side,
    /// griefing).
    /// </summary>
    internal static void KillOwnedProjectiles(int slot)
    {
        if (!CompanionRuntimePolicy.CanRunSyntheticPlayers)
            return;

        foreach (Projectile projectile in Main.ActiveProjectiles)
        {
            // Once the synthetic Player is removed there is no safe local-player identity under
            // which a leftover bullet can finish its damage update. Kill every owned projectile,
            // not only persistent summons, so a recycled slot can never inherit stale ordnance.
            if (projectile.owner != slot)
                continue;

            try
            {
                projectile.Kill();
            }
            catch (Exception exception)
            {
                // Foreign OnKill hooks are allowed to fail, but they may not leave an active
                // projectile bound to a player slot that is about to be recycled.
                projectile.active = false;
                ModContent.GetInstance<MoreAIPlayer>().Logger.Error(
                    $"Failed to kill projectile {projectile.whoAmI} owned by synthetic player slot {slot}; force-deactivated it.", exception);
            }
        }
    }

    private static void ReleaseAll()
    {
        // Release mutates BySlot, so snapshot the keys. This deliberately routes world unload,
        // multiplayer escape and mod unload through the same exception-safe cleanup as a normal
        // dismissal; one bad foreign projectile must not leave the remaining companions behind.
        List<int> slots = [.. BySlot.Keys];
        foreach (int slot in slots)
        {
            try
            {
                Release(slot);
            }
            catch (Exception exception)
            {
                // Release already isolates optional cleanup, but the bulk escape hatch must
                // remain complete even if a third-party hook throws from an unexpected path.
                ModContent.GetInstance<MoreAIPlayer>().Logger.Error(
                    $"Failed to bulk-release synthetic player slot {slot}; continuing cleanup.", exception);
            }
        }
        BySlot.Clear();
    }

    /// <summary>
    /// The only operation that removes a synthetic player from the live player array. It runs
    /// even when optional projectile/loot cleanup fails. Never overwrite an unrelated player
    /// that replaced this slot while the companion was being dismissed.
    /// </summary>
    private static void DeactivateAndRestoreSlot(int slot, CompanionInstance instance)
    {
        Player entity = instance.Entity;
        entity.controlUseItem = false;
        entity.controlUseTile = false;
        entity.channel = false;
        entity.active = false;

        if (Main.player is not null && slot >= 0 && slot < Main.player.Length &&
            ReferenceEquals(Main.player[slot], entity))
        {
            Main.player[slot] = instance.OriginalPlayer;
        }
    }

    public override void PreUpdatePlayers()
    {
        VirtualPlayerExecutionContext.CaptureRealState();
        RestoreHijackedLocalPlayer();
        if (BySlot.Count == 0)
            return;

        // Entering multiplayer (or a host/server) with companions still registered from a previous
        // singleplayer world: release them before any AI runs. ReleaseAll is the netMode-changed
        // escape hatch and only clears this mod's own slots — see KillOwnedProjectiles.
        if (!CompanionRuntimePolicy.CanRunSyntheticPlayers)
        {
            ReleaseAll();
            return;
        }

        List<int> expired = null;
        foreach ((int slot, CompanionInstance instance) in BySlot)
        {
            if (!instance.TryGetContext(out Player owner, out CompanionProfile profile))
            {
                (expired ??= []).Add(slot);
                continue;
            }

            // This is deliberately resolved before any AI work. With the default enabled,
            // companions stay in the world and the existing owner-dead Attack path lets them
            // finish a boss fight. Turning it off is an explicit "dismiss on owner death"
            // preference, so every companion bound to that owner is released this same update.
            // The automation companion is independent of its owner and is never dismissed by the
            // owner dying, whatever this preference says.
            if (owner.dead && !instance.IsAutoIdentity &&
                MoreAIPlayerClientConfig.Current?.CompanionsFightAfterOwnerDeath == false)
            {
                (expired ??= []).Add(slot);
                continue;
            }

            if (!ReferenceEquals(Main.player[slot], instance.Entity))
            {
                // Independent mods occasionally rebuild inactive Main.player entries during
                // world/bootstrap work. Reclaim only an inactive slot; an active occupant is a
                // real conflict and must never be overwritten.
                if (Main.player[slot].active)
                {
                    Mod.Logger.Error($"Synthetic player slot {slot} was taken by active player '{Main.player[slot].name}'. Companion released safely.");
                    (expired ??= []).Add(slot);
                    continue;
                }

                Main.player[slot] = instance.Entity;
                instance.SlotRepairs++;
            }

            if (instance.TicksSinceItemCheck < int.MaxValue / 2)
                instance.TicksSinceItemCheck++;

            bool updated = CompanionFaultGuard.Run($"companion update #{slot}", () =>
            {
                instance.BindProfile(owner, profile);
                ClearUnsupportedGravityBuff(instance.Entity);
                if (instance.IsAutoIdentity)
                {
                    // An automation slot must fail closed. A missing state is corrupted binding
                    // data, never permission to run the ordinary follow/escort brain.
                    if (profile.AutoState is null)
                    {
                        instance.DismissRequested = true;
                        return;
                    }
                    instance.AutoBrain.Update(instance, owner, profile);
                }
                else
                    instance.Brain.Update(instance, owner, profile);
                instance.AimWorld = SanitizeAim(instance.Entity, instance.AimWorld, instance);
                CompanionModBridge.FeedMouseState(instance.Entity, instance.AimWorld, instance.WantsAltUse);
                instance.ItemCheckPreparedFrame = Main.GameUpdateCount;
            });

            if (updated)
            {
                instance.FaultStreak = 0;
                if (instance.DismissRequested)
                    (expired ??= []).Add(slot);
                continue;
            }

            // Do not let a faulting decision leave its last held input alive for another tick.
            // Five isolated failures retire only this companion; the rest of the squad and the
            // real player continue as normal.
            HaltSyntheticInput(instance.Entity);
            instance.FaultStreak++;
            if (instance.FaultStreak >= CompanionInstance.MaxFaultStreak)
                (expired ??= []).Add(slot);
        }

        if (expired is not null)
        {
            foreach (int slot in expired)
            {
                if (BySlot.TryGetValue(slot, out CompanionInstance retiring) &&
                    retiring.FaultStreak >= CompanionInstance.MaxFaultStreak)
                {
                    CompanionFaultGuard.AnnounceRetired("CompanionDisabled");
                }
                Dismiss(slot);
            }
        }
    }

    private static void HaltSyntheticInput(Player player)
    {
        player.controlLeft = false;
        player.controlRight = false;
        player.controlUp = false;
        player.controlDown = false;
        player.controlJump = false;
        player.controlUseItem = false;
        player.controlUseTile = false;
        player.controlHook = false;
        player.controlMount = false;
        player.controlThrow = false;
        player.controlInv = false;
        player.controlTorch = false;
        player.controlMap = false;
        player.controlSmart = false;
        player.controlQuickHeal = false;
        player.controlQuickMana = false;
        player.controlCreativeMenu = false;
        player.controlDownHold = false;
        player.channel = false;
        player.releaseUseItem = true;
        player.releaseUseTile = true;
        player.releaseMount = true;
    }

    public override void PostUpdatePlayers()
    {
        RecoverMissedItemChecks();
        RecoverMissedHunterItemChecks();

        if (BySlot.Count == 0)
        {
            RestoreHijackedLocalPlayer();
            return;
        }

        // Companions never sleep in beds. Without this correction, having a companion out
        // would permanently disable the "all players sleeping" time fast-forward.
        int companions = 0;
        foreach (CompanionInstance instance in BySlot.Values)
        {
            // A copied/accessory-applied Gravitation buff makes a synthetic player keep drifting
            // upward after its movement brain has already selected a grounded route. Companions
            // have no player-facing gravity controls, so remove this unsupported buff on both
            // sides of the vanilla player update rather than letting it persist for a frame.
            ClearUnsupportedGravityBuff(instance.Entity);
            if (instance.Entity.active)
                companions++;
        }
        Main.CurrentFrameFlags.ActivePlayersCount =
            System.Math.Max(0, Main.CurrentFrameFlags.ActivePlayersCount - companions);
        RestoreHijackedLocalPlayer();
    }

    private static void ClearUnsupportedGravityBuff(Player companion)
    {
        if (companion is not null && companion.active && companion.HasBuff(BuffID.Gravitation))
            companion.ClearBuff(BuffID.Gravitation);
    }

    private static bool previousMouseRight;

    // A second right-press landing within this many 60 Hz ticks of the first is a double
    // click. 0.3s * 60 = 18 ticks. rescueDoubleClickWindow counts down the time left to catch
    // the second press.
    private const int DoubleClickTicks = 18;
    private static int rescueDoubleClickWindow;

    public override void UpdateUI(GameTime gameTime)
    {
        // UI and camera consumers must never inherit a virtual identity from a later-loaded
        // update hook, even if every item/projectile scope restored correctly on our side.
        RestoreHijackedLocalPlayer();
        // Edge detection is tracked every frame so a fresh press is never missed even when
        // another system already consumed Main.mouseRightRelease this frame.
        bool freshRightPress = Main.mouseRight && !previousMouseRight;
        previousMouseRight = Main.mouseRight;

        HoveredSlot = -1;
        if (Main.gameMenu || !CompanionRuntimePolicy.CanRunCompanionUI || BySlot.Count == 0)
            return;
        if (Main.LocalPlayer.mouseInterface)
            return;
        // While the Automation Tome is held, right click belongs exclusively to its camera
        // toggle. Do not also interpret the same press as hover-open or squad rescue.
        if (Main.LocalPlayer.HeldItem?.ModItem is Content.CompanionAutomationBook)
        {
            rescueDoubleClickWindow = 0;
            return;
        }
        // The mode wheel is driven by held right click; without this, opening it would also read
        // as the double-right-click recall gesture and yank the whole squad to the owner.
        if (UI.CompanionModeWheel.Active)
            return;

        // A player binding takes over the gesture completely. If it is explicitly cleared,
        // retain the old double-right-click input so existing players are never left without a
        // quick rescue command.
        bool freshRescuePress = MoreAIPlayer.HasCompanionRescueKeybind
            ? MoreAIPlayer.CompanionRescueKeybind.JustPressed
            : freshRightPress;
        DetectRescueDoubleClick(freshRescuePress);

        // UpdateUI runs between SetZoom_UI and SetZoom_Unscaled, so Main.MouseWorld would be
        // computed from UI-space mouse coordinates and miss the companion whenever the game
        // zoom or UI scale is not 100%. Switch to the world-space mouse for the hit test.
        Terraria.GameInput.PlayerInput.SetZoom_MouseInWorld();
        try
        {
            foreach ((int slot, CompanionInstance instance) in BySlot)
            {
                if (instance.OwnerWhoAmI != Main.myPlayer || !instance.Entity.active)
                    continue;

                Rectangle hitbox = instance.Entity.Hitbox;
                hitbox.Inflate(18, 12);
                if (!hitbox.Contains(Main.MouseWorld.ToPoint()))
                    continue;

                // Right-click open is opt-in. When disabled the backpack is reachable only
                // through the top-of-screen matrix panel, and hovering a companion advertises
                // nothing clickable.
                if (MoreAIPlayerClientConfig.Current?.AllowRightClickOpenBackpack != true)
                    break;

                HoveredSlot = slot;
                if (freshRightPress || Main.mouseRight && Main.mouseRightRelease)
                {
                    Main.mouseRightRelease = false;
                    // Swallow this click for the held item too, so right-click alt uses
                    // (e.g. the debug book) do not trigger through the companion.
                    Main.LocalPlayer.controlUseTile = false;
                    Main.LocalPlayer.mouseInterface = true;
                    UI.CompanionUISystem.Open(slot);
                }
                break;
            }
        }
        finally
        {
            Terraria.GameInput.PlayerInput.SetZoom_UI();
        }
    }

    /// <summary>
    /// A quick double press of the configured rescue binding (or right click when it is empty)
    /// is a manual "come guard me" command:
    /// it raises the same rescue signal a hit would, so companions farther than a few tiles
    /// teleport to the owner. Runs only past UpdateUI's guards (singleplayer, in world,
    /// companions out, cursor not over UI) so it never hijacks an inventory right-click.
    /// </summary>
    private static void DetectRescueDoubleClick(bool freshRightPress)
    {
        if (rescueDoubleClickWindow > 0)
            rescueDoubleClickWindow--;

        if (!freshRightPress)
            return;

        if (rescueDoubleClickWindow > 0)
        {
            rescueDoubleClickWindow = 0;
            Main.LocalPlayer.GetModPlayer<CompanionPlayer>().SignalManualRescue();
        }
        else
        {
            rescueDoubleClickWindow = DoubleClickTicks;
        }
    }

    private static void Player_ItemCheck(OrigItemCheck orig, Player self)
    {
        if (!CompanionRuntimePolicy.CanRunSyntheticPlayers)
        {
            orig(self);
            return;
        }

        Vector2 aimWorld;
        bool wantsUseItem;
        bool wantsAltUse;

        if (BySlot.TryGetValue(self.whoAmI, out CompanionInstance instance) && ReferenceEquals(instance.Entity, self))
        {
            instance.ItemCheckTakeovers++;
            instance.TicksSinceItemCheck = 0;
            instance.ItemCheckDetourFrame = Main.GameUpdateCount;
            aimWorld = instance.AimWorld;
            wantsUseItem = instance.WantsUseItem;
            wantsAltUse = instance.WantsAltUse;
        }
        else if (Hunter.HunterSystem.GetBySlot(self.whoAmI) is Hunter.HunterInstance hunter && ReferenceEquals(hunter.Entity, self))
        {
            hunter.ItemCheckDetourFrame = Main.GameUpdateCount;
            aimWorld = hunter.AimWorld;
            wantsUseItem = hunter.WantsUseItem;
            wantsAltUse = hunter.WantsAltUse;
        }
        else
        {
            orig(self);
            return;
        }

        if (wantsAltUse)
            self.altFunctionUse = 1;

        if (IsVirtualItemUseBackedOff(self))
        {
            HaltSyntheticInput(self);
            return;
        }

        try
        {
            using VirtualPlayerExecutionContext.Scope scope = VirtualPlayerExecutionContext.Enter(
                self, aimWorld, wantsUseItem, wantsAltUse, instance?.WorkTile);
            orig(self);
            RegisterVirtualItemUseSuccess(self);
        }
        catch (Exception exception)
        {
            HaltSyntheticInput(self);
            RegisterVirtualItemUseFailure(self, exception);
        }
        finally
        {
            RestoreHijackedLocalPlayer();
        }
    }

    private static void Projectile_Update(OrigProjectileUpdate orig, Projectile projectile, int index)
    {
        if (!CompanionRuntimePolicy.CanRunSyntheticPlayers)
        {
            orig(projectile, index);
            return;
        }

        Vector2 aimWorld;
        bool wantsUseItem;
        bool wantsAltUse;

        Player virtualOwner;
        if (BySlot.TryGetValue(projectile.owner, out CompanionInstance instance))
        {
            aimWorld = instance.AimWorld;
            wantsUseItem = instance.WantsUseItem;
            wantsAltUse = instance.WantsAltUse;
            virtualOwner = instance.Entity;
        }
        else if (Hunter.HunterSystem.GetBySlot(projectile.owner) is Hunter.HunterInstance hunter)
        {
            aimWorld = hunter.AimWorld;
            wantsUseItem = hunter.WantsUseItem;
            wantsAltUse = hunter.WantsAltUse;
            virtualOwner = hunter.Entity;
        }
        else
        {
            orig(projectile, index);
            return;
        }

        if (!CompanionFaultGuard.IsAvailable(CompanionFaultGuard.VirtualProjectileLane))
        {
            projectile.active = false;
            return;
        }

        try
        {
            using VirtualPlayerExecutionContext.Scope scope = VirtualPlayerExecutionContext.Enter(
                virtualOwner, aimWorld, wantsUseItem, wantsAltUse);
            orig(projectile, index);
        }
        catch (Exception exception)
        {
            // Avoid Kill(): it dispatches arbitrary projectile hooks again. Marking inactive
            // keeps a broken virtual-owned projectile from re-entering update next frame.
            projectile.active = false;
            CompanionFaultGuard.Trip(CompanionFaultGuard.VirtualProjectileLane, exception);
        }
        finally
        {
            RestoreHijackedLocalPlayer();
        }
    }

    // Last line of defense: if any code path still manages to leave Main.myPlayer pointing
    // at a companion slot, snap it back before the frame is drawn. A leaked myPlayer makes
    // the camera follow the companion and routes real input into it.
    /// <summary>
    /// A foreign item hook must never disable virtual item use for the entire world. The
    /// companion (or hunter) that hit the fault rests briefly, then retries; everyone else keeps
    /// using their own weapons normally.
    /// </summary>
    private static bool IsVirtualItemUseBackedOff(Player player)
    {
        if (BySlot.TryGetValue(player.whoAmI, out CompanionInstance companion) &&
            ReferenceEquals(companion.Entity, player))
            return companion.ItemUseBackoffTicks > 0;
        if (Hunter.HunterSystem.GetBySlot(player.whoAmI) is Hunter.HunterInstance hunter &&
            ReferenceEquals(hunter.Entity, player))
        {
            hunter.TickItemUseBackoff();
            return hunter.ItemUseBackoffTicks > 0;
        }
        return false;
    }

    private static void RegisterVirtualItemUseFailure(Player player, Exception exception)
    {
        if (BySlot.TryGetValue(player.whoAmI, out CompanionInstance companion) &&
            ReferenceEquals(companion.Entity, player))
        {
            companion.RegisterItemUseFailure(player.selectedItem);
        }
        else if (Hunter.HunterSystem.GetBySlot(player.whoAmI) is Hunter.HunterInstance hunter &&
                 ReferenceEquals(hunter.Entity, player))
        {
            hunter.RegisterItemUseFailure();
        }
        CompanionFaultGuard.Report(CompanionFaultGuard.VirtualItemCheckLane, exception);
    }

    private static void RegisterVirtualItemUseSuccess(Player player)
    {
        if (BySlot.TryGetValue(player.whoAmI, out CompanionInstance companion) &&
            ReferenceEquals(companion.Entity, player))
            companion.RegisterItemUseSuccess();
        else if (Hunter.HunterSystem.GetBySlot(player.whoAmI) is Hunter.HunterInstance hunter &&
                 ReferenceEquals(hunter.Entity, player))
            hunter.RegisterItemUseSuccess();
    }

    private static void RestoreHijackedLocalPlayer()
    {
        if (VirtualPlayerExecutionContext.ForceRestoreIfLeaked())
            leakedContextRecoveries++;
    }

    public override void PostUpdateProjectiles() => RestoreHijackedLocalPlayer();

    public override void ModifyScreenPosition() => RestoreHijackedLocalPlayer();

    public override void PostUpdateEverything()
    {
        RunCompanionDamagePass();
        RestoreHijackedLocalPlayer();
    }

    /// <summary>
    /// The companion damage implementation deliberately owns the whole routing decision instead
    /// of invoking Player.Update_NPCCollision or Projectile.Damage and hoping their local-player
    /// conditions select a synthetic slot. It scans the three player-facing vanilla sources
    /// directly: hostile NPC contact, hostile projectiles and immediate dangerous tiles.
    /// </summary>
    private static void RunCompanionDamagePass()
    {
        if (!CompanionRuntimePolicy.IsAuthoritativeDamageSide || BySlot.Count == 0)
            return;

        bool configuredDamageEnabled = MoreAIPlayerClientConfig.CompanionDamageEnabled;
        foreach (CompanionInstance instance in BySlot.Values)
        {
            Player companion = instance.Entity;
            if (!companion.active)
                continue;

            // Ordinary companions retain the user-facing invulnerability option. The automation
            // identity is always a real combatant and therefore never inherits that toggle.
            bool damageEnabled = instance.IsAutoIdentity || configuredDamageEnabled;

            // ModPlayer.PostUpdate order is mod-dependent. Capture once more here, after every
            // player hook and projectile update, so a later-loaded mod's direct heal/DoT cannot
            // be overwritten by PersistentLife on the next frame.
            if (instance.TryGetContext(out Player owner, out _))
            {
                companion.GetModPlayer<CompanionPlayer>()
                    .CaptureCompanionLifeAfterVanillaUpdate(instance, owner);
            }

            bool playerHostilityEnabled = IsOrdinaryHostileCompanion(companion);

            // Environmental survivability and opt-in player hostility are independent. Turning
            // ordinary damage off still clears its state, but real-player attacks retain the
            // companion-owned cooldown channel while the hostility rule is enabled.
            if (!damageEnabled && !playerHostilityEnabled)
            {
                ResetCompanionDamageState(instance);
                ResetDisabledEnvironmentalDamageState(companion);
                continue;
            }
            if (companion.dead || companion.statLife <= 0)
            {
                ResetCompanionDamageState(instance);
                continue;
            }

            // Cooldowns are harmless state and always advance. Foreign hooks are isolated for
            // this frame only: one bad projectile/NPC can never permanently open a world-wide
            // circuit breaker and reproduce "one hit, then invincible forever".
            AdvanceCompanionDamageCooldowns(instance);
            if (damageEnabled)
            {
                CompanionFaultGuard.Run("companion NPC damage", () => ResolveNpcContactDamage(instance));
                CompanionFaultGuard.Run("companion projectile damage", () => ResolveProjectileDamage(instance));
                CompanionFaultGuard.Run("companion environment damage", () => ResolveEnvironmentalDamage(instance));
            }
            else
            {
                ResetDisabledEnvironmentalDamageState(companion);
            }

            if (playerHostilityEnabled)
            {
                CompanionFaultGuard.Run("player to companion PvP bridge",
                    () => ResolveRealPlayerDamageToCompanion(instance));
                if (!companion.dead && companion.statLife > 0)
                {
                    CompanionFaultGuard.Run("companion to player PvP bridge",
                        () => ResolveCompanionDamageToRealPlayers(instance));
                }
            }
        }
    }

    private static void ResolveRealPlayerDamageToCompanion(CompanionInstance instance)
    {
        Player target = instance.Entity;
        for (int playerIndex = 0; playerIndex < Main.maxPlayers; playerIndex++)
        {
            Player attacker = Main.player[playerIndex];
            if (!CanPlayerDamageCompanion(attacker, instance))
                continue;

            ResolvePlayerMeleeAgainstCompanion(attacker, instance);
            if (target.dead || target.statLife <= 0)
                return;
        }

        for (int projectileIndex = 0; projectileIndex < Main.maxProjectiles; projectileIndex++)
        {
            Projectile projectile = Main.projectile[projectileIndex];
            if (!projectile.active || !projectile.friendly || projectile.hostile || projectile.damage <= 0 ||
                projectile.owner < 0 || projectile.owner >= Main.maxPlayers)
                continue;

            Player attacker = Main.player[projectile.owner];
            if (!CanPlayerDamageCompanion(attacker, instance) ||
                (projectile.ownerHitCheck && !projectile.CanHitWithMeleeWeapon(target)) ||
                !projectile.Colliding(GetProjectileDamageHitbox(projectile), target.Hitbox) ||
                !CombinedHooks.CanHitPvpWithProj(projectile, target))
                continue;

            int direction = projectile.direction != 0
                ? projectile.direction
                : (projectile.Center.X < target.Center.X ? 1 : -1);
            int damage = Main.DamageVar(projectile.damage, attacker.luck);
            if (TryApplyCompanionDamage(instance,
                    PlayerDeathReason.ByProjectile(attacker.whoAmI, projectileIndex), damage, direction,
                    ImmunityCooldownID.General, projectile.IsDamageDodgable(), projectile.ArmorPenetration,
                    out _, pvp: true))
            {
                projectile.StatusPvP(target.whoAmI);
                ConsumeFriendlyProjectileHit(projectile);
            }

            if (target.dead || target.statLife <= 0)
                return;
        }
    }

    private static void ResolvePlayerMeleeAgainstCompanion(Player attacker, CompanionInstance targetInstance)
    {
        if (attacker.itemAnimation <= 0)
        {
            targetInstance.HitByRealPlayerThisSwing = false;
            return;
        }
        if (targetInstance.HitByRealPlayerThisSwing)
            return;

        Item item = attacker.HeldItem;
        if (item.IsAir || item.damage <= 0 || item.noMelee || !attacker.CanHit(targetInstance.Entity) ||
            !CombinedHooks.CanHitPvp(attacker, item, targetInstance.Entity))
            return;

        if (!TryGetPlayerMeleeHitbox(attacker, item, out Rectangle hitbox) ||
            !hitbox.Intersects(targetInstance.Entity.Hitbox))
            return;

        int damage = Main.DamageVar(attacker.GetWeaponDamage(item), attacker.luck);
        if (TryApplyCompanionDamage(targetInstance,
                PlayerDeathReason.ByPlayerItem(attacker.whoAmI, item), damage, attacker.direction,
                ImmunityCooldownID.General, true, 0f, out _, pvp: true))
        {
            targetInstance.HitByRealPlayerThisSwing = true;
            attacker.StatusToPlayerPvP(item.type, targetInstance.Entity.whoAmI);
            attacker.OnHit(targetInstance.Entity.Center.X, targetInstance.Entity.Center.Y, targetInstance.Entity);
        }
    }

    private static void ResolveCompanionDamageToRealPlayers(CompanionInstance instance)
    {
        Player attacker = instance.Entity;
        if (attacker.itemAnimation <= 0)
            instance.HitRealPlayerThisSwing = false;

        for (int playerIndex = 0; playerIndex < Main.maxPlayers; playerIndex++)
        {
            Player target = Main.player[playerIndex];
            if (!CanCompanionDamagePlayer(instance, target))
                continue;

            if (!instance.HitRealPlayerThisSwing && attacker.itemAnimation > 0)
            {
                Item item = attacker.HeldItem;
                if (!item.IsAir && item.damage > 0 && !item.noMelee && !target.immune &&
                    attacker.CanHit(target) && CombinedHooks.CanHitPvp(attacker, item, target))
                {
                    if (TryGetPlayerMeleeHitbox(attacker, item, out Rectangle hitbox) &&
                        hitbox.Intersects(target.Hitbox))
                    {
                        int damage = Main.DamageVar(attacker.GetWeaponDamage(item), attacker.luck);
                        PlayerDeathReason reason = PlayerDeathReason.ByPlayerItem(attacker.whoAmI, item);
                        if (target.Hurt(reason, damage, attacker.direction, out _, pvp: true) > 0.0)
                        {
                            instance.HitRealPlayerThisSwing = true;
                            attacker.StatusToPlayerPvP(item.type, target.whoAmI);
                            attacker.OnHit(target.Center.X, target.Center.Y, target);
                        }
                    }
                }
            }

            if (target.dead)
                continue;

            for (int projectileIndex = 0; projectileIndex < Main.maxProjectiles; projectileIndex++)
            {
                Projectile projectile = Main.projectile[projectileIndex];
                if (!projectile.active || projectile.owner != instance.Slot || !projectile.friendly ||
                    projectile.hostile || projectile.damage <= 0 || target.immune ||
                    (projectile.ownerHitCheck && !projectile.CanHitWithMeleeWeapon(target)) ||
                    !projectile.Colliding(GetProjectileDamageHitbox(projectile), target.Hitbox) ||
                    !CombinedHooks.CanHitPvpWithProj(projectile, target))
                    continue;

                int direction = projectile.direction != 0
                    ? projectile.direction
                    : (projectile.Center.X < target.Center.X ? 1 : -1);
                int damage = Main.DamageVar(projectile.damage, attacker.luck);
                PlayerDeathReason reason = PlayerDeathReason.ByProjectile(attacker.whoAmI, projectileIndex);
                if (target.Hurt(reason, damage, direction, out _, pvp: true,
                        dodgeable: projectile.IsDamageDodgable()) > 0.0)
                {
                    projectile.StatusPvP(target.whoAmI);
                    ConsumeFriendlyProjectileHit(projectile);
                }
            }
        }
    }

    private static void ConsumeFriendlyProjectileHit(Projectile projectile)
    {
        if (!projectile.active || projectile.penetrate < 0)
            return;
        projectile.penetrate--;
        if (projectile.penetrate <= 0)
            projectile.Kill();
    }

    /// <summary>Public mod APIs do not expose Player.ItemCheck_GetMeleeHitbox, so reproduce its
    /// ordinary swing/thrust geometry and then run the official modded hitbox hook. This covers
    /// vanilla melee and well-behaved modded use styles without invoking ItemCheck a second time.</summary>
    private static bool TryGetPlayerMeleeHitbox(Player player, Item item, out Rectangle hitbox)
    {
        Rectangle frame = Main.dedServ
            ? new Rectangle(0, 0, 32, 32)
            : Terraria.GameContent.TextureAssets.Item[item.type].Value.Frame();
        hitbox = new Rectangle((int)player.itemLocation.X, (int)player.itemLocation.Y,
            frame.Width, frame.Height);

        float scale = player.GetAdjustedItemScale(item);
        hitbox.Width = (int)(hitbox.Width * scale);
        hitbox.Height = (int)(hitbox.Height * scale);
        if (player.direction == -1)
            hitbox.X -= hitbox.Width;
        if (player.gravDir == 1f)
            hitbox.Y -= hitbox.Height;

        bool noHitbox = false;
        float animationRatio = player.itemAnimationMax > 0
            ? player.itemAnimation / (float)player.itemAnimationMax
            : 0f;
        if (item.useStyle == ItemUseStyleID.Swing)
        {
            if (animationRatio < 0.333f)
            {
                if (player.direction == -1)
                    hitbox.X -= (int)(hitbox.Width * 0.4f);
                hitbox.Width = (int)(hitbox.Width * 1.4f);
                hitbox.Y += (int)(hitbox.Height * 0.5f * player.gravDir);
                hitbox.Height = (int)(hitbox.Height * 1.1f);
            }
            else if (animationRatio >= 0.666f)
            {
                if (player.direction == 1)
                    hitbox.X -= (int)(hitbox.Width * 1.2f);
                hitbox.Width *= 2;
                hitbox.Y -= (int)(hitbox.Height * 0.4f * player.gravDir);
                hitbox.Height = (int)(hitbox.Height * 1.4f);
            }
        }
        else if (item.useStyle == ItemUseStyleID.Thrust)
        {
            if (animationRatio > 0.666f)
            {
                noHitbox = true;
            }
            else
            {
                if (player.direction == -1)
                    hitbox.X -= (int)(hitbox.Width * 0.4f);
                hitbox.Width = (int)(hitbox.Width * 1.4f);
                hitbox.Y += (int)(hitbox.Height * 0.6f);
                hitbox.Height = (int)(hitbox.Height * 0.6f);
            }
        }

        ItemLoader.UseItemHitbox(item, player, ref hitbox, ref noHitbox);
        return !noHitbox && hitbox.Width > 0 && hitbox.Height > 0;
    }

    private static void ResetDisabledEnvironmentalDamageState(Player companion)
    {
        companion.fallStart = (int)(companion.position.Y / 16f);
        companion.breath = companion.breathMax;
        companion.breathCD = 0;
        companion.lavaTime = companion.lavaMax;
    }

    /// <summary>
    /// Owns vanilla's general and special-channel iframes outside Player.immune, whose local-
    /// player update assumptions are unsafe for a synthetic slot. Native fields remain mirrors
    /// for blinking and for equipment/mod hooks that use Player.AddImmuneTime.
    /// </summary>
    private static void AdvanceCompanionDamageCooldowns(CompanionInstance instance)
    {
        if (instance.DamageCooldownFrame != Main.GameUpdateCount)
        {
            instance.DamageCooldownFrame = Main.GameUpdateCount;
            if (instance.GeneralDamageImmunityTicks > 0)
                instance.GeneralDamageImmunityTicks--;
            for (int i = 0; i < instance.DamageCooldownTicks.Length; i++)
            {
                if (instance.DamageCooldownTicks[i] > 0)
                    instance.DamageCooldownTicks[i]--;
            }
        }

        // Vanilla already decremented the mirrored fields earlier this update. Absorb a longer
        // value added by equipment/another mod, then mirror the authoritative counters back.
        AbsorbNativeDamageCooldowns(instance);
        MirrorCompanionDamageCooldowns(instance);
    }

    internal static void ResetCompanionDamageState(CompanionInstance instance)
    {
        if (instance is null)
            return;
        instance.GeneralDamageImmunityTicks = 0;
        Array.Clear(instance.DamageCooldownTicks, 0, instance.DamageCooldownTicks.Length);
        instance.DamageCooldownFrame = Main.GameUpdateCount;
        instance.LastWhipOwner = -1;
        instance.LastWhipIdentity = -1;
        instance.HitByRealPlayerThisSwing = false;
        instance.HitRealPlayerThisSwing = false;
        if (instance.Entity is null)
            return;
        instance.Entity.immune = false;
        instance.Entity.immuneTime = 0;
        Array.Clear(instance.Entity.hurtCooldowns, 0, instance.Entity.hurtCooldowns.Length);
    }

    private static void AbsorbNativeDamageCooldowns(CompanionInstance instance)
    {
        Player companion = instance.Entity;
        instance.GeneralDamageImmunityTicks = System.Math.Max(
            instance.GeneralDamageImmunityTicks, companion.immuneTime);
        int count = System.Math.Min(instance.DamageCooldownTicks.Length, companion.hurtCooldowns.Length);
        for (int i = 0; i < count; i++)
        {
            instance.DamageCooldownTicks[i] = System.Math.Max(
                instance.DamageCooldownTicks[i], companion.hurtCooldowns[i]);
        }
    }

    private static void MirrorCompanionDamageCooldowns(CompanionInstance instance)
    {
        Player companion = instance.Entity;
        companion.immune = instance.GeneralDamageImmunityTicks > 0;
        companion.immuneTime = instance.GeneralDamageImmunityTicks;
        Array.Clear(companion.hurtCooldowns, 0, companion.hurtCooldowns.Length);
        int count = System.Math.Min(instance.DamageCooldownTicks.Length, companion.hurtCooldowns.Length);
        for (int i = 0; i < count; i++)
            companion.hurtCooldowns[i] = instance.DamageCooldownTicks[i];
    }

    private static void ResolveNpcContactDamage(CompanionInstance instance)
    {
        Player companion = instance.Entity;
        Rectangle companionHitbox = companion.Hitbox;
        for (int npcIndex = 0; npcIndex < Main.maxNPCs; npcIndex++)
        {
            NPC npc = Main.npc[npcIndex];
            if (!npc.active || npc.friendly || npc.damage <= 0)
                continue;

            try
            {
                int cooldownSlot = GetNpcCooldownSlot(npc);
                const float OgreInactiveState = 0f;
                const float OgreRecoveringState = 10f;
                if (npc.type == NPCID.HallowBoss &&
                    (npc.ai[0] == OgreInactiveState || npc.ai[0] == OgreRecoveringState))
                    continue;
                if (!CombinedHooks.CanNPCHitPlayer(npc, companion, ref cooldownSlot))
                    continue;
                if (npc.type < companion.npcTypeNoAggro.Length &&
                    companion.npcTypeNoAggro[npc.type])
                    continue;

                float damageMultiplier = 1f;
                Rectangle npcHitbox = new((int)(npc.position.X + npc.netOffset.X),
                    (int)(npc.position.Y + npc.netOffset.Y), npc.width, npc.height);
                NPC.GetMeleeCollisionData(companionHitbox, npcIndex, ref cooldownSlot,
                    ref damageMultiplier, ref npcHitbox);
                if (!companionHitbox.Intersects(npcHitbox))
                    continue;

                if (companion.CanParryAgainst(companionHitbox, npcHitbox, npc.velocity))
                {
                    companion.GiveImmuneTimeForCollisionAttack(companion.longInvince ? 60 : 30);
                    companion.AddBuff(BuffID.ParryDamageBuff, 300, quiet: false);
                    AbsorbNativeDamageCooldowns(instance);
                    MirrorCompanionDamageCooldowns(instance);
                    continue;
                }

                int direction = npc.Center.X < companion.Center.X ? 1 : -1;
                int sourceDamage = Main.DamageVar(npc.damage * damageMultiplier, -companion.luck);
                if (TryApplyCompanionDamage(instance, PlayerDeathReason.ByNPC(npcIndex), sourceDamage,
                        direction, cooldownSlot, npc.IsDamageDodgeable(), 0f, out Player.HurtInfo hurtInfo))
                {
                    CombinedHooks.OnHitByNPC(companion, npc, hurtInfo);
                    if (!companion.dead && companion.statLife > 0)
                        companion.StatusFromNPC(npc);
                }
            }
            catch (Exception exception)
            {
                CompanionFaultGuard.Report("companion NPC damage", exception);
            }
        }
    }

    private static void ResolveProjectileDamage(CompanionInstance instance)
    {
        Player companion = instance.Entity;
        for (int projectileIndex = 0; projectileIndex < Main.maxProjectiles; projectileIndex++)
        {
            Projectile projectile = Main.projectile[projectileIndex];
            if (!projectile.active)
                continue;

            try
            {
                if (!projectile.hostile)
                {
                    TryGrantFriendlyWhipBuff(instance, projectile);
                    continue;
                }
                if (projectile.damage <= 0)
                    continue;

                if (projectile.type == ProjectileID.ExplosiveBunny && !Main.getGoodWorld &&
                    projectile.ai[1] - 1f != companion.whoAmI)
                    continue;

                Rectangle projectileHitbox = GetProjectileDamageHitbox(projectile);
                if (!projectile.Colliding(projectileHitbox, companion.Hitbox) ||
                    !CombinedHooks.CanBeHitByProjectile(companion, projectile))
                    continue;
                if (companion.CanParryAgainst(companion.Hitbox, projectile.Hitbox, projectile.velocity))
                    continue;

                int cooldownSlot = GetProjectileCooldownSlot(projectile);
                int direction = projectile.Center.X >= companion.Center.X ? -1 : 1;
                float sourceLuck = projectile.owner >= 0 && projectile.owner < Main.maxPlayers &&
                    Main.player[projectile.owner].active
                        ? Main.player[projectile.owner].luck
                        : companion.luck;
                int sourceDamage = Main.DamageVar(projectile.damage, -sourceLuck) * 2;
                int sourcePlayer = ProjectileID.Sets.IsAGravestone[projectile.type]
                    ? (int)projectile.ai[0]
                    : -1;
                if (TryApplyCompanionDamage(instance,
                        PlayerDeathReason.ByProjectile(sourcePlayer, projectileIndex), sourceDamage, direction,
                        cooldownSlot, projectile.IsDamageDodgable(), projectile.ArmorPenetration,
                        out Player.HurtInfo hurtInfo))
                {
                    CombinedHooks.OnHitByProjectile(companion, projectile, hurtInfo);
                    if (!companion.dead && companion.statLife > 0)
                        projectile.StatusPlayer(companion.whoAmI);
                    if (projectile.trap)
                        companion.trapDebuffSource = true;
                    ApplyProjectilePostHitState(projectile);
                }
            }
            catch (Exception exception)
            {
                CompanionFaultGuard.Report("companion projectile damage", exception);
            }
        }
    }

    private static void TryGrantFriendlyWhipBuff(CompanionInstance instance, Projectile projectile)
    {
        Player companion = instance.Entity;
        // The automation companion is isolated from the owner's whips: it grants no buff.
        if (instance.IsAutoIdentity || IsOrdinaryHostileCompanion(companion))
            return;
        if (projectile.owner == companion.whoAmI ||
            !CompanionPlayer.IsWhipProjectile(projectile) ||
            (instance.LastWhipOwner == projectile.owner &&
             instance.LastWhipIdentity == projectile.identity) ||
            !projectile.Colliding(projectile.Hitbox, companion.Hitbox))
            return;

        instance.LastWhipOwner = projectile.owner;
        instance.LastWhipIdentity = projectile.identity;
        companion.GetModPlayer<CompanionPlayer>().GrantWhipBuff();
    }

    private static Rectangle GetProjectileDamageHitbox(Projectile projectile)
    {
        Rectangle hitbox = projectile.Hitbox;
        if (projectile.type == ProjectileID.EyeFire)
            hitbox.Inflate(30, 30);
        if (projectile.type == ProjectileID.Flames)
        {
            int inflation = (int)Utils.Remap(projectile.localAI[0], 0f, 72f, 10f, 40f);
            hitbox.Inflate(inflation, inflation);
        }
        if (projectile.type == ProjectileID.FlamesTrap)
            hitbox.Inflate(20, 20);
        if (projectile.aiStyle == ProjAIStyleID.GemStaffBolt)
            hitbox.Inflate(4, 4);
        if (projectile.type == ProjectileID.HoundiusShootiusFireball)
            hitbox.Inflate(10, 10);
        ProjectileLoader.ModifyDamageHitbox(projectile, ref hitbox);
        return hitbox;
    }

    private static void ApplyProjectilePostHitState(Projectile projectile)
    {
        if (projectile.type != ProjectileID.DeerclopsIceSpike)
            return;
        if (projectile.penetrate == 1)
        {
            projectile.damage = 0;
            projectile.penetrate = -1;
        }
        else
        {
            projectile.damage = (int)(projectile.damage * 0.7);
        }
    }

    private static void ResolveEnvironmentalDamage(CompanionInstance instance)
    {
        ResolveTouchTileDamage(instance);
        ResolveLavaDamage(instance);
        ResolveFallDamage(instance);
        ResolveDrowningDamage(instance);
    }

    private static void ResolveTouchTileDamage(CompanionInstance instance)
    {
        Player companion = instance.Entity;
        Collision.HurtTile hurtTile = GetCompanionHurtTile(companion);
        if (hurtTile.type < 0)
            return;

        int tileType = hurtTile.type;
        // Player.Update already runs ApplyTouchDamage for every active Player, including a
        // synthetic slot. It has therefore applied Burning/Bleeding/Suffocation and destroyed
        // fragile hazard tiles once. Only Hurt itself was rejected by our ordinary-route gate;
        // repeat the missing life damage here and do not duplicate those side effects.
        int sourceDamage = TileID.Sets.TouchDamageImmediate[tileType];
        if (sourceDamage > 0)
        {
            sourceDamage = Main.DamageVar(sourceDamage, -companion.luck);
            TryApplyCompanionDamage(instance, PlayerDeathReason.ByOther(3), sourceDamage, 0,
                ImmunityCooldownID.TileContactDamage, true, 0f, out _);
        }
    }

    private static Collision.HurtTile GetCompanionHurtTile(Player companion)
    {
        Collision.HurtTile result = Collision.HurtTiles(companion.position, companion.width,
            (!companion.mount.Active || !companion.mount.Cart) ? companion.height : companion.height - 16,
            companion);
        if (result.type >= 0)
            return result;

        foreach (Point touched in companion.TouchedTiles)
        {
            if (!WorldGen.InWorld(touched.X, touched.Y, 1))
                continue;
            Tile tile = Main.tile[touched.X, touched.Y];
            if (tile.HasTile && !tile.IsActuated &&
                !TileID.Sets.Suffocate[tile.TileType] &&
                Collision.CanTileHurt(tile.TileType, touched.X, touched.Y, companion))
            {
                result.type = tile.TileType;
                result.x = touched.X;
                result.y = touched.Y;
                return result;
            }
        }
        return result;
    }

    private static void ResolveLavaDamage(CompanionInstance instance)
    {
        Player companion = instance.Entity;
        int collisionHeight = companion.waterWalk ? companion.height - 6 : companion.height;
        bool touchingLava = !companion.shimmering &&
            Collision.LavaCollision(companion.position, companion.width, collisionHeight);
        if (!touchingLava || companion.lavaImmune ||
            IsCompanionDamageOnCooldown(instance, ImmunityCooldownID.Lava))
            return;

        if (companion.lavaTime > 0)
        {
            companion.lavaTime--;
            return;
        }

        int sourceDamage = Main.remixWorld ? 200 : 80;
        int burningTime = Main.remixWorld ? 630 : 420;
        if (companion.ashWoodBonus)
        {
            if (Main.remixWorld)
                sourceDamage = 145;
            sourceDamage /= 2;
            burningTime -= 210;
        }
        if (companion.lavaRose)
        {
            sourceDamage -= 45;
            burningTime -= 210;
        }

        if (sourceDamage > 0)
        {
            TryApplyCompanionDamage(instance, PlayerDeathReason.ByOther(2), sourceDamage, 0,
                ImmunityCooldownID.Lava, true, 0f, out _);
        }
        if (burningTime > 0)
            companion.AddBuff(BuffID.OnFire, burningTime);
    }

    private static void ResolveFallDamage(CompanionInstance instance)
    {
        Player companion = instance.Entity;
        int currentTileY = (int)(companion.position.Y / 16f);
        if (companion.velocity.Y != 0f)
        {
            if (companion.velocity.Y * companion.gravDir < 0f || companion.jump > 0 ||
                companion.rocketDelay > 0 ||
                companion.wet || companion.slowFall || companion.tongued)
                companion.fallStart = currentTileY;
            return;
        }

        int fallDistance = currentTileY - companion.fallStart;
        int safeDistance = 25 + companion.extraFall;
        if (companion.gravDir == -1f)
            fallDistance = -fallDistance;

        if (fallDistance > 0)
        {
            int tileXStart = (int)(companion.position.X / 16f);
            int tileXEnd = (int)((companion.position.X + companion.width) / 16f);
            int tileY = companion.gravDir == -1f
                ? (int)((companion.position.Y - 1f) / 16f)
                : (int)((companion.position.Y + companion.height + 1f) / 16f);
            for (int x = tileXStart; x <= tileXEnd; x++)
            {
                if (!WorldGen.InWorld(x, tileY, 1))
                    continue;
                Tile tile = Main.tile[x, tileY];
                if (tile.HasTile &&
                    tile.TileType is 189 or 196 or 460 or 666)
                {
                    fallDistance = 0;
                    break;
                }
            }
        }

        if (companion.stoned && fallDistance > 2)
        {
            TryApplyCompanionDamage(instance, PlayerDeathReason.ByOther(5), (fallDistance - 2) * 20,
                0, ImmunityCooldownID.General, true, 0f, out _);
        }
        else if (fallDistance > safeDistance && !companion.noFallDmg && companion.wingsLogic <= 0 &&
            !companion.mount.CanFly())
        {
            int sourceDamage = (fallDistance - safeDistance) * 10;
            if (companion.mount.Active)
                sourceDamage = (int)(sourceDamage * companion.mount.FallDamage);
            TryApplyCompanionDamage(instance, PlayerDeathReason.ByOther(0), sourceDamage, 0,
                ImmunityCooldownID.General, true, 0f, out _);
        }
        companion.fallStart = currentTileY;
    }

    private static void ResolveDrowningDamage(CompanionInstance instance)
    {
        Player companion = instance.Entity;
        bool drowning = Collision.DrownCollision(companion.position, companion.width,
            companion.height, companion.gravDir);
        if (companion.armor[0].type is 250 or 4275)
            drowning = true;
        if (companion.inventory[companion.selectedItem].type == ItemID.BreathingReed &&
            companion.itemAnimation == 0)
        {
            int sampleX = (int)((companion.position.X + companion.width / 2f +
                6f * companion.direction) / 16f);
            int gravityOffset = companion.gravDir == -1f ? companion.height : 0;
            int sampleY = (int)((companion.position.Y + gravityOffset -
                44f * companion.gravDir) / 16f);
            if (WorldGen.InWorld(sampleX, sampleY, 1))
            {
                Tile headTile = Main.tile[sampleX, sampleY];
                if (headTile.LiquidAmount < 128 &&
                    (!headTile.HasTile || !Main.tileSolid[headTile.TileType] ||
                     Main.tileSolidTop[headTile.TileType]))
                    drowning = false;
            }
        }
        if (companion.gills)
            drowning = Main.getGoodWorld && !drowning;
        if (companion.shimmering || companion.accMerman ||
            (companion.mount.Active && companion.mount.Type == MountID.Turtle))
            drowning = false;

        if (!drowning)
        {
            companion.breath = System.Math.Min(companion.breathMax, companion.breath + 3);
            companion.breathCD = 0;
            return;
        }

        companion.breathCD++;
        if (companion.breathCD < companion.breathCDMax)
            return;
        companion.breathCD = 0;
        companion.breath = System.Math.Max(0, companion.breath - 1);
        if (companion.breath > 0)
            return;

        // Vanilla drowning bypasses defence and iframes. Its Main.myPlayer gate excludes a
        // synthetic slot, so settle the same two life here and persist it immediately.
        companion.lifeRegenTime = 0f;
        companion.statLife = System.Math.Max(0, companion.statLife - 2);
        instance.PersistentLife = companion.statLife;
        instance.PersistentLifeInitialized = true;
    }

    private static bool TryApplyCompanionDamage(CompanionInstance instance, PlayerDeathReason source,
        int sourceDamage, int hitDirection, int cooldownSlot, bool dodgeable, float armorPenetration,
        out Player.HurtInfo hurtInfo, bool pvp = false)
    {
        hurtInfo = default;
        Player companion = instance.Entity;
        if (sourceDamage <= 0 || companion.dead || companion.statLife <= 0 ||
            IsCompanionDamageOnCooldown(instance, cooldownSlot))
            return false;

        int lifeBefore = companion.statLife;
        Player previousEvaluationTarget = ownedDamageEvaluationTarget;
        ownedDamageEvaluationTarget = companion;
        bool applied;
        try
        {
            try
            {
                double damageDone = companion.Hurt(source, sourceDamage, hitDirection, out hurtInfo,
                    pvp: pvp, quiet: false, cooldownCounter: cooldownSlot, dodgeable: dodgeable,
                    armorPenetration: armorPenetration);
                applied = damageDone > 0.0;
            }
            catch (Exception exception)
            {
                // OnHurt runs before health subtraction. A bad foreign callback is reported, but
                // it cannot make this source a permanent immunity. If subtraction already ran,
                // preserve it; otherwise apply one conservative vanilla-defence fallback.
                CompanionFaultGuard.Report("companion hurt callback", exception);
                if (companion.statLife >= lifeBefore)
                {
                    hurtInfo = BuildFallbackHurtInfo(companion, source, sourceDamage, hitDirection,
                        cooldownSlot, dodgeable, armorPenetration, pvp);
                    applied = ApplyFallbackCompanionHurt(companion, hurtInfo, lifeBefore);
                }
                else
                {
                    applied = true;
                }
            }
        }
        finally
        {
            ownedDamageEvaluationTarget = previousEvaluationTarget;
        }

        if (!applied)
            return false;

        // ModPlayer.OnHurt is called before Player.Hurt subtracts statLife. This post-return
        // point is the only authoritative snapshot; writing it in OnHurt was the exact bug that
        // restored pre-hit life next frame and looked like permanent invulnerability.
        instance.PersistentLife = System.Math.Max(0, companion.statLife);
        instance.PersistentLifeInitialized = true;
        AbsorbNativeDamageCooldowns(instance);
        MirrorCompanionDamageCooldowns(instance);
        return true;
    }

    private static Player.HurtInfo BuildFallbackHurtInfo(Player companion, PlayerDeathReason source,
        int sourceDamage, int hitDirection, int cooldownSlot, bool dodgeable, float armorPenetration,
        bool pvp)
    {
        int effectiveCooldown = NormalizeCooldownSlot(cooldownSlot);
        Player.HurtModifiers modifiers = new()
        {
            DamageSource = source,
            PvP = pvp,
            CooldownCounter = effectiveCooldown,
            Dodgeable = dodgeable,
            HitDirection = hitDirection
        };
        modifiers.ArmorPenetration += armorPenetration;
        if (cooldownSlot == ImmunityCooldownID.DD2OgreKnockback)
        {
            modifiers.Knockback.Base += 6f;
            modifiers.KnockbackImmunityEffectiveness *= 0.8f;
        }
        modifiers.FinalDamage *= System.Math.Max(1f - companion.endurance, 0f);
        if (companion.setSolar && companion.solarShields > 0)
            modifiers.FinalDamage *= 0.8f;
        if (companion.beetleDefense && companion.beetleOrbs > 0)
            modifiers.FinalDamage *= 1f - 0.15f * companion.beetleOrbs;
        AutoCompanionDamagePolicy.ApplyFinalDamageReduction(companion, ref modifiers);
        return modifiers.ToHurtInfo(sourceDamage, companion.statDefense,
            companion.DefenseEffectiveness.Value, 4.5f, companion.noKnockback);
    }

    private static bool ApplyFallbackCompanionHurt(Player companion, Player.HurtInfo hurtInfo, int lifeBefore)
    {
        if (hurtInfo.Cancelled || hurtInfo.Damage <= 0)
            return false;

        companion.statLife = System.Math.Max(0, lifeBefore - hurtInfo.Damage);
        companion.lifeRegenTime = 0f;
        if (hurtInfo.Knockback != 0f && hurtInfo.HitDirection != 0 &&
            (!companion.mount.Active || !companion.mount.Cart))
        {
            companion.velocity.X = hurtInfo.Knockback * hurtInfo.HitDirection;
            companion.velocity.Y = hurtInfo.Knockback * -7f / 9f;
            companion.fallStart = (int)(companion.position.Y / 16f);
        }

        int duration = hurtInfo.Damage == 1
            ? (companion.longInvince ? 40 : 20)
            : (companion.longInvince ? 80 : 40);
        int cooldownSlot = NormalizeCooldownSlot(hurtInfo.CooldownCounter);
        if (cooldownSlot >= 0 && cooldownSlot < companion.hurtCooldowns.Length)
        {
            companion.hurtCooldowns[cooldownSlot] = System.Math.Max(
                companion.hurtCooldowns[cooldownSlot], duration);
        }
        else
        {
            companion.immune = true;
            companion.immuneTime = System.Math.Max(companion.immuneTime, duration);
        }
        return true;
    }

    private static int NormalizeCooldownSlot(int cooldownSlot) =>
        cooldownSlot == ImmunityCooldownID.DD2OgreKnockback
            ? ImmunityCooldownID.General
            : cooldownSlot;

    private static bool IsCompanionDamageOnCooldown(CompanionInstance instance, int cooldownSlot)
    {
        cooldownSlot = NormalizeCooldownSlot(cooldownSlot);
        return cooldownSlot >= 0 && cooldownSlot < instance.DamageCooldownTicks.Length
            ? instance.DamageCooldownTicks[cooldownSlot] > 0
            : instance.GeneralDamageImmunityTicks > 0;
    }

    private static int GetNpcCooldownSlot(NPC npc) => npc.type is 396 or 397 or 398 or 400 or 401 or 636
        ? ImmunityCooldownID.Bosses
        : ImmunityCooldownID.General;

    private static int GetProjectileCooldownSlot(Projectile projectile)
    {
        int cooldownSlot = projectile.ModProjectile?.CooldownSlot ?? ImmunityCooldownID.General;
        if (projectile.ModProjectile is null && (projectile.type is 452 or 454 or 455 or 462 or
            871 or 872 or 873 or 874 or 919 or 923 or 924))
            cooldownSlot = ImmunityCooldownID.Bosses;
        return cooldownSlot;
    }

    internal static void RegisterProjectileOwnerCorrection() => projectileOwnerCorrections++;

    private void RecoverMissedItemChecks()
    {
        if (!CompanionRuntimePolicy.CanRunSyntheticPlayers || BySlot.Count == 0)
            return;

        ulong frame = Main.GameUpdateCount;
        foreach (CompanionInstance instance in BySlot.Values)
        {
            if (!instance.Entity.active || instance.ItemCheckPreparedFrame != frame)
                continue;

            if (instance.ItemCheckDetourFrame == frame)
            {
                instance.ConsecutiveItemCheckMisses = 0;
                continue;
            }

            instance.ConsecutiveItemCheckMisses++;
            instance.RecoveredItemChecks++;
            RunRecoveredItemCheck(instance.Entity, instance.AimWorld, instance.WantsUseItem,
                instance.WantsAltUse, instance.WorkTile);
        }
    }

    private void RecoverMissedHunterItemChecks()
    {
        if (!CompanionRuntimePolicy.CanRunSyntheticPlayers)
            return;

        ulong frame = Main.GameUpdateCount;
        foreach (Hunter.HunterInstance hunter in Hunter.HunterSystem.All)
        {
            if (!hunter.Entity.active || hunter.ItemCheckPreparedFrame != frame)
                continue;

            if (hunter.ItemCheckDetourFrame == frame)
            {
                hunter.ConsecutiveItemCheckMisses = 0;
                continue;
            }

            hunter.ConsecutiveItemCheckMisses++;
            hunter.RecoveredItemChecks++;
            RunRecoveredItemCheck(hunter.Entity, hunter.AimWorld, hunter.WantsUseItem,
                hunter.WantsAltUse, workTile: null);
        }
    }

    private void RunRecoveredItemCheck(Player player, Vector2 aimWorld, bool wantsUseItem,
        bool wantsAltUse, Point? workTile)
    {
        if (IsVirtualItemUseBackedOff(player))
        {
            HaltSyntheticInput(player);
            return;
        }

        try
        {
            if (wantsAltUse && player.itemAnimation == 0)
                player.altFunctionUse = 1;

            // The explicit scope makes this recovery useful even if a different Player.Update
            // hook was what skipped the ordinary ItemCheck call. If our detour is still in the
            // call chain it simply nests and restores the same state safely.
            using VirtualPlayerExecutionContext.Scope scope = VirtualPlayerExecutionContext.Enter(
                player, aimWorld, wantsUseItem, wantsAltUse, workTile);
            player.ItemCheck();
        }
        catch (Exception exception)
        {
            HaltSyntheticInput(player);
            RegisterVirtualItemUseFailure(player, exception);
        }
        finally
        {
            RestoreHijackedLocalPlayer();
        }
    }

    private static Vector2 SanitizeAim(Player player, Vector2 aimWorld, CompanionInstance instance)
    {
        Vector2 safe = VirtualPlayerExecutionContext.SanitizeAim(player, aimWorld, out bool corrected);
        if (corrected)
            instance.AimCorrections++;
        return safe;
    }

    internal static Vector2 SanitizeHunterAim(Player player, Vector2 aimWorld, Hunter.HunterInstance instance)
    {
        Vector2 safe = VirtualPlayerExecutionContext.SanitizeAim(player, aimWorld, out bool corrected);
        if (corrected)
            instance.AimCorrections++;
        return safe;
    }

    private static void ResetRuntimeState(bool keepHookState = false)
    {
        HoveredSlot = -1;
        previousMouseRight = false;
        rescueDoubleClickWindow = 0;
        leakedContextRecoveries = 0;
        projectileOwnerCorrections = 0;
        VirtualPlayerExecutionContext.Reset();
        CompanionFaultGuard.Reset();
        if (!keepHookState)
            hooksInstalled = false;
    }

    public static void SpawnRecallDustBurst(Vector2 center)
    {
        for (int i = 0; i < 48; i++)
        {
            Vector2 direction = Main.rand.NextVector2Unit();
            float radius = Main.rand.NextFloat(4f, 24f);
            float speed = Main.rand.NextFloat(2.2f, 7.5f);
            Dust dust = Dust.NewDustPerfect(center + direction * radius, DustID.MagicMirror, direction * speed, 90, default, Main.rand.NextFloat(0.9f, 1.45f));
            dust.noGravity = true;
        }
    }

    /// <summary>Loud one-shot ring, thrown when a command-book waypoint is placed or reached.</summary>
    public static void SpawnWaypointPing(Vector2 center)
    {
        for (int i = 0; i < 28; i++)
        {
            Vector2 direction = Main.rand.NextVector2Unit();
            Dust dust = Dust.NewDustPerfect(center, DustID.GoldFlame, direction * Main.rand.NextFloat(2.5f, 6.5f), 100, default, Main.rand.NextFloat(1.1f, 1.7f));
            dust.noGravity = true;
        }
    }

    /// <summary>Quiet drifting sparkle drawn each tick while a waypoint is standing.</summary>
    public static void SpawnWaypointSparkle(Vector2 center)
    {
        if (!Main.rand.NextBool(3))
            return;

        Vector2 spot = center + new Vector2(Main.rand.NextFloat(-10f, 10f), Main.rand.NextFloat(-2f, 8f));
        Dust dust = Dust.NewDustPerfect(spot, DustID.YellowStarDust, new Vector2(0f, Main.rand.NextFloat(-1.6f, -0.6f)), 120, default, Main.rand.NextFloat(0.8f, 1.2f));
        dust.noGravity = true;
    }
}
