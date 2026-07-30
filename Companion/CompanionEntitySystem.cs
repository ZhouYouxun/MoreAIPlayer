using System;
using System.Collections.Generic;
using System.Reflection;
using Microsoft.Xna.Framework;
using Terraria;
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
        if (player is null || Main.netMode != NetmodeID.SinglePlayer)
            return false;

        CompanionInstance instance = GetBySlot(player.whoAmI);
        return instance is not null && ReferenceEquals(instance.Entity, player);
    }

    public static CompanionInstance FindByProfile(int ownerWhoAmI, int profileId)
    {
        foreach (CompanionInstance instance in BySlot.Values)
        {
            if (instance.OwnerWhoAmI == ownerWhoAmI && instance.ProfileId == profileId)
                return instance;
        }
        return null;
    }

    /// <summary>Number of live companions a player currently has out. Each one occupies one of
    /// that player's minion slots, so this is also how many summon slots they have spent.</summary>
    public static int CountActiveOwnedBy(int ownerWhoAmI)
    {
        int count = 0;
        foreach (CompanionInstance instance in BySlot.Values)
        {
            if (instance.OwnerWhoAmI == ownerWhoAmI && instance.Entity.active)
                count++;
        }
        return count;
    }

    public override void Load()
    {
        // A dedicated server never hosts a companion (Summon is singleplayer-only), so the
        // detours below would do nothing there except add a delegate hop to every ItemCheck
        // and every projectile update, for every connected player, forever.
        if (Main.dedServ)
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
        if (Main.netMode != NetmodeID.SinglePlayer || owner is null || profile is null ||
            !owner.active || owner.whoAmI != Main.myPlayer || FindByProfile(owner.whoAmI, profile.Id) is not null)
            return false;

        if (owner.slotsMinions + 1.0f > owner.maxMinions)
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

        instance.BindProfile(owner, profile);
        entity.Center = worldPosition;
        entity.velocity = Vector2.Zero;
        entity.fallStart = (int)(entity.position.Y / 16f);
        entity.statLife = entity.statLifeMax;
        entity.dead = false;
        SpawnRecallDustBurst(entity.Center);
        AnnounceJoin(entity.name);
        return true;
    }

    // Identical to the multiplayer join/leave broadcasts in NetMessage.SyncOnePlayer:
    // NetworkText.FromKey(Lang.mp[19/20].Key, name) with Color(255, 240, 20).
    private static void AnnounceJoin(string name) =>
        Main.NewText(Lang.mp[19].Format(name), 255, 240, 20);

    private static void AnnounceLeave(string name) =>
        Main.NewText(Lang.mp[20].Format(name), 255, 240, 20);

    public static void DismissOwnedBy(int ownerWhoAmI)
    {
        List<int> slots = [];
        foreach ((int slot, CompanionInstance instance) in BySlot)
        {
            if (instance.OwnerWhoAmI == ownerWhoAmI)
                slots.Add(slot);
        }

        foreach (int slot in slots)
            Dismiss(slot);
    }

    public static void Dismiss(int slot)
    {
        if (!BySlot.ContainsKey(slot))
            return;

        SpawnRecallDustBurst(Main.player[slot].Center);
        AnnounceLeave(Main.player[slot].name);
        Release(slot);
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
        if (!BySlot.Remove(slot, out CompanionInstance instance))
            return;

        KillOwnedProjectiles(slot);
        instance.Entity.active = false;
        if (Main.player[slot] == instance.Entity)
            Main.player[slot] = instance.OriginalPlayer;
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
        if (Main.netMode != NetmodeID.SinglePlayer)
            return;

        foreach (Projectile projectile in Main.ActiveProjectiles)
        {
            // Once the synthetic Player is removed there is no safe local-player identity under
            // which a leftover bullet can finish its damage update. Kill every owned projectile,
            // not only persistent summons, so a recycled slot can never inherit stale ordnance.
            if (projectile.owner == slot)
                projectile.Kill();
        }
    }

    private static void ReleaseAll()
    {
        if (Main.player is not null)
        {
            foreach ((int slot, CompanionInstance instance) in BySlot)
            {
                KillOwnedProjectiles(slot);
                instance.Entity.active = false;
                if (slot >= 0 && slot < Main.player.Length && Main.player[slot] == instance.Entity)
                    Main.player[slot] = instance.OriginalPlayer;
            }
        }
        BySlot.Clear();
    }

    public override void PreUpdatePlayers()
    {
        VirtualPlayerExecutionContext.CaptureRealState();
        RestoreHijackedLocalPlayer();
        if (BySlot.Count == 0)
            return;

        if (Main.netMode != NetmodeID.SinglePlayer)
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

            instance.BindProfile(owner, profile);
            instance.Brain.Update(instance, owner, profile);
            instance.AimWorld = SanitizeAim(instance.Entity, instance.AimWorld, instance);
            CompanionModBridge.FeedMouseState(instance.Entity, instance.AimWorld, instance.WantsAltUse);
            instance.ItemCheckPreparedFrame = Main.GameUpdateCount;
        }

        if (expired is not null)
        {
            foreach (int slot in expired)
            {
                if (BySlot.TryGetValue(slot, out CompanionInstance expiredInstance))
                    AnnounceLeave(expiredInstance.Entity.name);
                Release(slot);
            }
        }
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
            if (instance.Entity.active)
                companions++;
        }
        Main.CurrentFrameFlags.ActivePlayersCount =
            System.Math.Max(0, Main.CurrentFrameFlags.ActivePlayersCount - companions);
        RestoreHijackedLocalPlayer();
    }

    private static bool previousMouseRight;

    // A second right-press landing within this many 60 Hz ticks of the first is a double
    // click. 0.3s * 60 = 18 ticks. rescueDoubleClickWindow counts down the time left to catch
    // the second press.
    private const int DoubleClickTicks = 18;
    private static int rescueDoubleClickWindow;

    public override void UpdateUI(GameTime gameTime)
    {
        // Edge detection is tracked every frame so a fresh press is never missed even when
        // another system already consumed Main.mouseRightRelease this frame.
        bool freshRightPress = Main.mouseRight && !previousMouseRight;
        previousMouseRight = Main.mouseRight;

        HoveredSlot = -1;
        if (Main.gameMenu || Main.dedServ || Main.netMode != NetmodeID.SinglePlayer || BySlot.Count == 0)
            return;
        if (Main.LocalPlayer.mouseInterface)
            return;
        // The mode wheel is driven by held right click; without this, opening it would also read
        // as the double-right-click recall gesture and yank the whole squad to the owner.
        if (UI.CompanionModeWheel.Active)
            return;

        DetectRescueDoubleClick(freshRightPress);

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
    /// A quick double right-click anywhere in the world is a manual "come guard me" command:
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
        if (Main.netMode != NetmodeID.SinglePlayer)
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

        using VirtualPlayerExecutionContext.Scope scope = VirtualPlayerExecutionContext.Enter(
            self, aimWorld, wantsUseItem, wantsAltUse, instance?.WorkTile);
        orig(self);
    }

    private static void Projectile_Update(OrigProjectileUpdate orig, Projectile projectile, int index)
    {
        if (Main.netMode != NetmodeID.SinglePlayer)
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

        using VirtualPlayerExecutionContext.Scope scope = VirtualPlayerExecutionContext.Enter(
            virtualOwner, aimWorld, wantsUseItem, wantsAltUse);
        orig(projectile, index);
    }

    // Last line of defense: if any code path still manages to leave Main.myPlayer pointing
    // at a companion slot, snap it back before the frame is drawn. A leaked myPlayer makes
    // the camera follow the companion and routes real input into it.
    private static void RestoreHijackedLocalPlayer()
    {
        if (VirtualPlayerExecutionContext.ForceRestoreIfLeaked())
            leakedContextRecoveries++;
    }

    public override void PostUpdateProjectiles() => RestoreHijackedLocalPlayer();

    public override void PostUpdateEverything() => RestoreHijackedLocalPlayer();

    internal static void RegisterProjectileOwnerCorrection() => projectileOwnerCorrections++;

    private void RecoverMissedItemChecks()
    {
        if (Main.netMode != NetmodeID.SinglePlayer || BySlot.Count == 0)
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
        if (Main.netMode != NetmodeID.SinglePlayer)
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
            Mod.Logger.Error($"Recovered ItemCheck failed for synthetic player {player.whoAmI}", exception);
            player.controlUseItem = false;
            player.channel = false;
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
