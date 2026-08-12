using System.Collections.Generic;
using Microsoft.Xna.Framework;
using MoreAIPlayer.Companion.Automation;
using MoreAIPlayer.Companion.Building;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.ModLoader.IO;

namespace MoreAIPlayer.Companion;

public sealed class CompanionPlayer : ModPlayer
{
    public const int HotbarSize = 10;
    public const int AmmoSize = 6;
    public const int ArmorSize = 3;

    /// <summary>Number of accessory slots the profile stores. How many are actually usable is a
    /// world-dependent subset — see <see cref="CompanionSlotRules.UnlockedAccessorySlots"/>.</summary>
    public const int AccessorySize = 7;

    /// <summary>Maximum number of companion-only accessory slots exposed by the client slider.</summary>
    public const int ExtraAccessorySize = 7;

    /// <summary>The dedicated bug-net slot. One slot, its own inventory mapping.</summary>
    public const int NetSize = 1;

    /// <summary>The dedicated fishing-pole slot.</summary>
    public const int RodSize = 1;
    /// <summary>The dedicated quick-grapple equipment slot.</summary>
    public const int HookSize = 1;
    /// <summary>The dedicated mount equipment slot.</summary>
    public const int MountSize = 1;
    public const int VanitySize = 3;
    public const int DyeSize = 3;
    public const int BackpackSize = 40;
    private const int RescueSignalWindow = 10;

    public List<CompanionProfile> Profiles { get; private set; } = [];

    /// <summary>Per-world automation-companion bindings, keyed by the world's unique id. See
    /// <see cref="AutoWorldBinding"/>. Persists with the character, not the world.</summary>
    private readonly Dictionary<string, AutoWorldBinding> autoBindings = [];

    /// <summary>Which construction tactic the builder book has selected.</summary>
    public CompanionBuildMode BuildMode = CompanionBuildMode.SmartBuild;

    /// <summary>Whether the selected tactic is currently switched on. One left click toggles it;
    /// switching tactic keeps this flag, so turning the book on once lets you cycle tactics live.</summary>
    public bool BuildTacticActive;
    public int RequestedTarget = -1;
    public int CombatRequestTime;

    /// <summary>
    /// Advances whenever this companion deals contact damage. The brain snapshots the value
    /// before a deliberate attack, then checks whether it advanced when that use finishes;
    /// this works for both an item swing and its companion-owned projectile.
    /// </summary>
    public int CombatHitSerial;

    /// <summary>Ticks remaining on the "come guard me" signal. Companions read their owner's
    /// copy to decide whether to teleport in and guard (see CompanionBrain's rescue policy).
    /// Raised both automatically when this player is hurt and manually by a quick double
    /// rescue binding. The window spans a few ticks so every companion sees the same signal
    /// during its own update, rather than only the first one to run.</summary>
    public int RescueSignalTime;

    /// <summary>Ticks remaining on an explicit "come to me now" order (the double right-click).
    /// Unlike the automatic hurt signal this overrides Garrison mode — a manual recall pulls a
    /// posted companion off its spot, whereas a stray hit should not.</summary>
    public int ManualRescueTime;

    /// <summary>Raises the automatic escort signal (owner was hurt) so nearby-but-not-adjacent
    /// companions teleport to this player's side on their next update.</summary>
    public void SignalRescue() => RescueSignalTime = RescueSignalWindow;

    /// <summary>Raises the manual recall order (double rescue binding); stronger than <see cref="SignalRescue"/>.</summary>
    public void SignalManualRescue() => ManualRescueTime = RescueSignalWindow;

    public int ObservedTileX = -1;
    public int ObservedTileY = -1;
    public int ObservedTileType = -1;
    public int MiningObservationTime;
    public Point TunnelDirection;
    public int TunnelDirectionTime;
    public int TreeAssistTime;

    private Point lastAxeTile = new(-1, -1);
    private bool lastAxeTileExisted;
    private int recentTreesChopped;
    private int treeStreakTime;

    private bool buildSnapshotValid;
    private Point buildSnapshotPoint;
    private int buildSnapshotItemType;
    private int buildSnapshotCreateTile;
    private int buildSnapshotCreateWall;
    private int buildSnapshotStyle;
    private bool buildSnapshotHadTile;
    private ushort buildSnapshotTileType;
    private ushort buildSnapshotWallType;

    public override void Initialize()
    {
        Profiles = [];
        autoBindings.Clear();
        BuildMode = CompanionBuildMode.SmartBuild;
        BuildTacticActive = false;
        buildSnapshotValid = false;
    }

    public CompanionProfile CreateProfile(bool mirrorOwner)
    {
        int nextId = 0;
        foreach (CompanionProfile profile in Profiles)
            nextId = System.Math.Max(nextId, profile.Id + 1);
        CompanionProfile created = CompanionProfile.Create(nextId, Player, mirrorOwner);
        Profiles.Add(created);
        return created;
    }

    public CompanionProfile GetProfile(int id)
    {
        foreach (CompanionProfile profile in Profiles)
        {
            if (profile.Id == id)
                return profile;
        }
        return null;
    }

    // ---- Automation companion, one per (character, world), keyed by world unique id (§4.1) ----

    private static string CurrentWorldKey() =>
        Main.ActiveWorldFileData?.UniqueId.ToString() ?? string.Empty;

    /// <summary>The automation binding for the world the player is currently in, or null.</summary>
    public AutoWorldBinding CurrentWorldAutoBinding() =>
        autoBindings.TryGetValue(CurrentWorldKey(), out AutoWorldBinding binding) ? binding : null;

    /// <summary>The automation companion profile bound to the current world, or null if none.</summary>
    public CompanionProfile GetAutomationProfileForCurrentWorld()
    {
        AutoWorldBinding binding = CurrentWorldAutoBinding();
        return binding is null ? null : GetProfile(binding.ProfileId);
    }

    /// <summary>Whether this world's automation run has already finished (Moon Lord defeated).</summary>
    public bool IsAutomationCompletedForCurrentWorld() => CurrentWorldAutoBinding()?.Completed == true;

    /// <summary>
    /// Creates the from-scratch automation companion for the current world and binds it. The
    /// appearance and name are randomized so it reads as a distinct new player, and it receives
    /// only its two starting tools — the whole flow builds up from there.
    /// </summary>
    public CompanionProfile CreateAutomationProfileForCurrentWorld()
    {
        CompanionProfile profile = CreateProfile(mirrorOwner: false);
        profile.BeginAutomation();
        autoBindings[CurrentWorldKey()] = new AutoWorldBinding { ProfileId = profile.Id, Completed = false };
        return profile;
    }

    /// <summary>Marks the current world's automation run finished and converts the profile back to
    /// a normal companion. Used by the Moon Lord completion path (later phase).</summary>
    public void MarkAutomationCompletedForCurrentWorld()
    {
        AutoWorldBinding binding = CurrentWorldAutoBinding();
        if (binding is null)
            return;
        binding.Completed = true;
        GetProfile(binding.ProfileId)?.EndAutomation();
    }

    public override void SaveData(TagCompound tag)
    {
        List<TagCompound> savedProfiles = new(Profiles.Count);
        foreach (CompanionProfile profile in Profiles)
            savedProfiles.Add(profile.Save());
        tag[nameof(Profiles)] = savedProfiles;
        tag[nameof(BuildMode)] = (int)BuildMode;
        tag[nameof(BuildTacticActive)] = BuildTacticActive;

        List<TagCompound> savedBindings = new(autoBindings.Count);
        foreach ((string world, AutoWorldBinding binding) in autoBindings)
        {
            TagCompound entry = binding.Save();
            entry["World"] = world;
            savedBindings.Add(entry);
        }
        tag["AutoBindings"] = savedBindings;
    }

    public override void LoadData(TagCompound tag)
    {
        Profiles = [];
        int savedMode = tag.ContainsKey(nameof(BuildMode)) ? tag.GetInt(nameof(BuildMode)) : 0;
        BuildMode = System.Enum.IsDefined(typeof(CompanionBuildMode), savedMode)
            ? (CompanionBuildMode)savedMode
            : CompanionBuildMode.SmartBuild;
        BuildTacticActive = tag.ContainsKey(nameof(BuildTacticActive)) && tag.GetBool(nameof(BuildTacticActive));

        autoBindings.Clear();
        if (tag.ContainsKey("AutoBindings"))
        {
            foreach (TagCompound entry in tag.GetList<TagCompound>("AutoBindings"))
            {
                string world = entry.GetString("World");
                if (!string.IsNullOrEmpty(world))
                    autoBindings[world] = AutoWorldBinding.Load(entry);
            }
        }

        if (!tag.ContainsKey(nameof(Profiles)))
        {
            if (tag.ContainsKey(nameof(CompanionProfile.Hotbar)))
                Profiles.Add(CompanionProfile.LoadLegacy(0, Player, tag));
            return;
        }

        foreach (TagCompound savedProfile in tag.GetList<TagCompound>(nameof(Profiles)))
            Profiles.Add(CompanionProfile.Load(savedProfile));
    }

    public int WhipBuffTimer;

    public override void ResetEffects()
    {
        if (CompanionEntitySystem.IsCompanion(Player))
        {
            // Vanilla gates armor[8] (the 6th accessory) behind the Demon Heart flag, which it
            // recomputes in Player.ResetEffects just before this hook runs. Companions are not
            // meant to eat one, so the flag is granted here whenever the world's rules say the
            // slot should exist. armor[9] (the 7th) needs no flag — vanilla unlocks it from
            // Main.masterMode alone — and BindProfile blanks it while the world has not earned
            // it yet. Without this the 6th accessory silently did nothing.
            Player.extraAccessory = CompanionSlotRules.UnlockedAccessorySlots() >= 6;

            Player.ClearBuff(BuffID.ManaSickness);

            if (WhipBuffTimer > 0)
            {
                WhipBuffTimer--;
                Player.statDefense.FinalMultiplier *= 1.20f;
                Player.GetDamage(DamageClass.Generic) += 0.20f;

                if (Main.rand.NextBool(3))
                {
                    Dust d = Dust.NewDustPerfect(Player.Center + Main.rand.NextVector2Circular(16f, 24f), DustID.GoldFlame, new Vector2(0f, -0.8f), 100, default, 1.1f);
                    d.noGravity = true;
                }
            }
        }
        else
        {
            // A stale synthetic-slot registry can exist for one transition frame. Never let it
            // alter minion capacity for a real player on a client or dedicated server.
            if (Main.netMode == NetmodeID.SinglePlayer)
                Player.slotsMinions += CompanionEntitySystem.CountActiveOwnedBy(Player.whoAmI);
        }
    }

    public override void PostUpdateRunSpeeds()
    {
        // Preserve the movement speed produced by the companion's own armor and accessories.
        // The old owner-speed pin erased boots and other mobility equipment, which made giving
        // the companion movement gear meaningless.
        CompanionInstance instance = CompanionEntitySystem.GetBySlot(Player.whoAmI);
        if (instance is null || !ReferenceEquals(instance.Entity, Player))
            return;
        if (WhipBuffTimer > 0)
        {
            Player.maxRunSpeed *= 1.20f;
            Player.accRunSpeed *= 1.20f;
            Player.runAcceleration *= 1.20f;
        }
    }

    public override bool PreItemCheck()
    {
        buildSnapshotValid = false;
        if (Main.netMode != NetmodeID.SinglePlayer || Player.whoAmI != Main.myPlayer ||
            !Player.active || CompanionEntitySystem.IsCompanion(Player))
            return true;

        Item held = Player.HeldItem;
        if ((held.createTile == -1 && held.createWall <= WallID.None) ||
            !WorldGen.InWorld(Player.tileTargetX, Player.tileTargetY, 5))
            return true;

        buildSnapshotPoint = new Point(Player.tileTargetX, Player.tileTargetY);
        buildSnapshotItemType = held.type;
        buildSnapshotCreateTile = held.createTile;
        buildSnapshotCreateWall = held.createWall;
        buildSnapshotStyle = held.placeStyle;
        Tile before = Main.tile[buildSnapshotPoint.X, buildSnapshotPoint.Y];
        buildSnapshotHadTile = before.HasTile;
        buildSnapshotTileType = before.TileType;
        buildSnapshotWallType = before.WallType;
        buildSnapshotValid = true;
        return true;
    }

    public override void PostItemCheck()
    {
        if (!buildSnapshotValid)
            return;
        buildSnapshotValid = false;

        Tile after = Main.tile[buildSnapshotPoint.X, buildSnapshotPoint.Y];
        bool placedTile = buildSnapshotCreateTile >= 0 && after.HasTile &&
            after.TileType == buildSnapshotCreateTile &&
            (!buildSnapshotHadTile || buildSnapshotTileType != after.TileType);
        bool placedWall = buildSnapshotCreateWall > WallID.None &&
            after.WallType == buildSnapshotCreateWall && buildSnapshotWallType != after.WallType;
        if (!placedTile && !placedWall)
            return;

        CompanionBuildingSystem.ObserveOwnerPlacement(
            Player,
            this,
            buildSnapshotPoint,
            buildSnapshotItemType,
            placedTile ? buildSnapshotCreateTile : -1,
            placedWall ? buildSnapshotCreateWall : -1,
            buildSnapshotStyle);
    }

    public override void PostUpdateEquips()
    {
        if (Main.netMode != NetmodeID.SinglePlayer || !CompanionEntitySystem.IsCompanion(Player))
            return;

        CompanionInstance instance = CompanionEntitySystem.GetBySlot(Player.whoAmI);
        if (instance is null || !instance.TryGetContext(out Player owner, out CompanionProfile profile))
            return;

        ApplyExtraAccessoryEffects(profile);
        PrepareCompanionLifeForVanillaUpdate(instance, owner);
    }

    /// <summary>
    /// Applies the companion's optional accessory row through the same public vanilla/mod-loader
    /// equipment pipeline used by a real modded accessory slot. These items deliberately do not
    /// occupy Player.armor: that array has only the seven native functional accessory positions.
    /// </summary>
    private void ApplyExtraAccessoryEffects(CompanionProfile profile)
    {
        int activeSlots = MoreAIPlayerClientConfig.ExtraCompanionAccessorySlots;
        for (int i = 0; i < activeSlots; i++)
        {
            Item accessory = profile.ExtraAccessoryForSlot(i);
            if (accessory.IsAir)
                continue;

            Player.ApplyEquipFunctional(accessory, false);
        }
    }

    public override void PostUpdate()
    {
        // Every field maintained below exists only to feed companion AI, which is
        // singleplayer-only. In multiplayer this hook runs for each connected player on both
        // the server and every client, so the tile sampling in ObserveMiningAndTrees would be
        // pure waste — and it reads tileTarget coordinates that are meaningless for a remote
        // player anyway.
        if (Main.netMode != NetmodeID.SinglePlayer)
            return;

        if (CombatRequestTime > 0)
            CombatRequestTime--;
        else
            RequestedTarget = -1;

        if (RescueSignalTime > 0)
            RescueSignalTime--;
        if (ManualRescueTime > 0)
            ManualRescueTime--;

        if (CompanionEntitySystem.IsCompanion(Player))
        {
            CompanionInstance instance = CompanionEntitySystem.GetBySlot(Player.whoAmI);
            if (instance is not null)
            {
                if (instance.TryGetContext(out Player owner, out _))
                    CaptureCompanionLifeAfterVanillaUpdate(instance, owner);

                Player.statMana = Player.statManaMax2;
                if (instance.AutoHealCooldownTicks > 0)
                    instance.AutoHealCooldownTicks--;

                // Do not turn a helper into a panic-drinking machine. It waits until it is
                // genuinely hurt, consumes at most one potion, then observes that potion's own
                // cooldown. The fallback also covers nonstandard zero-delay healing items.
                if (!Player.dead && instance.AutoHealCooldownTicks <= 0 && Player.potionDelay <= 0 &&
                    Player.statLife * 2 <= Player.statLifeMax2)
                {
                    int lifeBeforeHealing = Player.statLife;
                    TryUseHealingPotion(instance);
                    if (Player.statLife > lifeBeforeHealing)
                    {
                        instance.PersistentLife = System.Math.Clamp(Player.statLife, 1, Player.statLifeMax2);
                        instance.AutoHealCooldownTicks = System.Math.Max(60, Player.potionDelay);
                    }
                }
            }
        }

        ObserveMiningAndTrees();
    }

    /// <summary>
    /// Runs after equipment/max-life effects but before vanilla UpdateLifeRegen. Restoring the
    /// persistent value here lets natural regeneration and negative regen (poison, burning,
    /// suffocation and modded DoT) settle normally instead of being overwritten afterward.
    /// </summary>
    private void PrepareCompanionLifeForVanillaUpdate(CompanionInstance instance, Player owner)
    {
        int sharedMaxLife = instance.AutoMaxLife > 0
            ? instance.AutoMaxLife
            : System.Math.Max(100, owner.statLifeMax2);
        Player.statLifeMax = sharedMaxLife;
        Player.statLifeMax2 = sharedMaxLife;

        if (!instance.PersistentLifeInitialized)
        {
            instance.PersistentLife = sharedMaxLife;
            instance.PersistentLifeInitialized = true;
        }
        if (Player.dead || instance.PersistentLife <= 0)
        {
            Player.statLife = 0;
            instance.PersistentLife = 0;
            instance.LastSharedLifeMax = sharedMaxLife;
            return;
        }

        instance.PersistentLife = System.Math.Clamp(instance.PersistentLife, 1, sharedMaxLife);
        instance.LastSharedLifeMax = sharedMaxLife;
        Player.statLife = instance.PersistentLife;
    }

    /// <summary>Captures vanilla regen/DoT after it has actually changed current life.</summary>
    internal void CaptureCompanionLifeAfterVanillaUpdate(CompanionInstance instance, Player owner)
    {
        int sharedMaxLife = instance.AutoMaxLife > 0
            ? instance.AutoMaxLife
            : System.Math.Max(100, owner.statLifeMax2);
        Player.statLifeMax = sharedMaxLife;
        Player.statLifeMax2 = sharedMaxLife;

        if (!MoreAIPlayerClientConfig.CompanionDamageEnabled &&
            instance.PersistentLifeInitialized && instance.PersistentLife > 0)
        {
            // This protection must run before the lethal check. Otherwise the final poison/fire
            // tick could set life to zero and kill an allegedly invulnerable companion even
            // though every non-lethal tick was correctly rejected.
            int protectedLife = System.Math.Clamp(instance.PersistentLife, 1, sharedMaxLife);
            Player.statLife = System.Math.Max(Player.statLife, protectedLife);
            Player.dead = false;
        }

        if (Player.dead || Player.statLife <= 0)
        {
            Player.statLife = 0;
            Player.dead = true;
            instance.PersistentLife = 0;
            instance.PersistentLifeInitialized = true;
            instance.LastSharedLifeMax = sharedMaxLife;
            return;
        }

        int currentLife = System.Math.Clamp(Player.statLife, 1, sharedMaxLife);
        Player.statLife = currentLife;
        instance.PersistentLife = currentLife;
        instance.PersistentLifeInitialized = true;
        instance.LastSharedLifeMax = sharedMaxLife;
    }

    private void TryUseHealingPotion(CompanionInstance instance)
    {
        // Do not maintain a mod-name/item-ID table here. QuickHeal is vanilla's generic
        // healing-potion path: it recognises Item.potion and Item.healLife, runs dynamic
        // GetHealLife hooks, respects ConsumeItem, invokes OnConsumeItem (for example,
        // Calamity Bloodfin's buff), and applies the item's own modified potion delay.
        // That makes this work for normal modded healing potions without pretending every
        // healing item shares one fixed 30-second cooldown.
        // A third-party potion hook can still assume that its consumer is a real player.
        // Contain that compatibility failure to auto-healing instead of letting one item
        // repeatedly fault the main player-update loop.
        // QuickHeal's ordinary implementation is local-player gated. Enter the same tightly
        // restored virtual-player scope used for companion ItemCheck so it consumes from this
        // companion's own backpack/hotbar and applies the normal healing result to this player.
        Vector2 healAim = instance.AimWorld;
        if (healAim == Vector2.Zero)
            healAim = Player.Center + new Vector2(Player.direction * 160f, 0f);

        using VirtualPlayerExecutionContext.Scope scope =
            VirtualPlayerExecutionContext.Enter(Player, healAim, false, false, null);

        if (HasStoredHealingPotion())
        {
            CompanionFaultGuard.RunCritical(CompanionFaultGuard.HealingPotionLane, Player.QuickHeal);
            return;
        }

        // A companion begins with an inexhaustible, progression-appropriate emergency potion.
        // It is inserted only for QuickHeal's vanilla use path and restored immediately after,
        // so it plays/behaves like the real item without creating an item or consuming a slot.
        const int virtualPotionSlot = CompanionPlayer.HotbarSize + CompanionPlayer.BackpackSize - 1;
        Item displaced = Player.inventory[virtualPotionSlot];
        Player.inventory[virtualPotionSlot] = new Item(DefaultHealingPotionType());
        try
        {
            CompanionFaultGuard.RunCritical(CompanionFaultGuard.HealingPotionLane, Player.QuickHeal);
        }
        finally
        {
            Player.inventory[virtualPotionSlot] = displaced;
        }
    }

    private bool HasStoredHealingPotion()
    {
        for (int i = 0; i < Player.inventory.Length; i++)
        {
            Item item = Player.inventory[i];
            if (item is not null && !item.IsAir && item.potion && item.healLife > 0)
                return true;
        }

        return false;
    }

    private static int DefaultHealingPotionType()
    {
        if (NPC.downedMoonlord)
            return ItemID.SuperHealingPotion;
        return Main.hardMode ? ItemID.GreaterHealingPotion : ItemID.HealingPotion;
    }

    public override bool CanBeHitByProjectile(Projectile proj)
    {
        if (CompanionEntitySystem.IsCompanion(Player) && !CompanionEntitySystem.IsAutoCompanion(Player) &&
            IsWhipProjectile(proj) && proj.owner != Player.whoAmI)
            return true;
        return base.CanBeHitByProjectile(proj);
    }

    /// <summary>
    /// Reject damage entering through Terraria's ordinary local-player routing. The one exception
    /// is CompanionEntitySystem's tightly scoped owned evaluation: that pass has already selected
    /// a real collision and uses the normal Hurt calculation exactly once. Other ModPlayer
    /// ImmuneTo hooks still run during that evaluation, so third-party immunity remains valid.
    /// </summary>
    public override bool ImmuneTo(PlayerDeathReason damageSource, int cooldownCounter, bool dodgeable) =>
        CompanionEntitySystem.IsCompanion(Player) &&
        !CompanionEntitySystem.IsOwnedDamageEvaluation(Player);

    public override void OnHurt(Player.HurtInfo info)
    {
        if (Main.netMode != NetmodeID.SinglePlayer)
            return;

        // Any damage counts, not just NPC contact — fall damage, lava and traps all mean the
        // owner is in trouble and wants the escort back at their side.
        // This is opt-in: a hit normally must not reposition any companion. Companion damage
        // also must never trigger a rescue signal of its own.
        if (!CompanionEntitySystem.IsCompanion(Player) &&
            !CompanionEntitySystem.IsCompanionDamageSource(info.DamageSource) &&
            MoreAIPlayerClientConfig.CompanionsRescueOnOwnerHurt)
        {
            SignalRescue();
        }

        int sourceNpc = info.DamageSource.SourceNPCIndex;
        if (sourceNpc >= 0 && sourceNpc < Main.maxNPCs && Main.npc[sourceNpc].active)
        {
            RequestedTarget = sourceNpc;
            CombatRequestTime = 300;
        }

        if (CompanionEntitySystem.IsCompanion(Player) && !CompanionEntitySystem.IsAutoCompanion(Player))
        {
            int projIndex = info.DamageSource.SourceProjectileLocalIndex;
            if (projIndex >= 0 && projIndex < Main.maxProjectiles)
            {
                Projectile proj = Main.projectile[projIndex];
                if (IsWhipProjectile(proj) && proj.owner != Player.whoAmI)
                    GrantWhipBuff();
            }
        }
    }

    internal void GrantWhipBuff()
    {
        WhipBuffTimer = 600; // 10s buff
        SpawnWhipSparkles(Player.Center);
    }

    private static void SpawnWhipSparkles(Vector2 center)
    {
        for (int i = 0; i < 24; i++)
        {
            Vector2 speed = Main.rand.NextVector2Circular(5f, 5f);
            Dust d = Dust.NewDustPerfect(center, DustID.YellowStarDust, speed, 100, default, Main.rand.NextFloat(1.2f, 1.8f));
            d.noGravity = true;
        }
    }

    internal static bool IsWhipProjectile(Projectile proj)
    {
        if (proj is null || !proj.active)
            return false;
        if (ProjectileID.Sets.IsAWhip.Length > proj.type && ProjectileID.Sets.IsAWhip[proj.type])
            return true;
        if (proj.aiStyle == ProjAIStyleID.Whip)
            return true;
        return false;
    }

    /// <summary>
    /// Gel is free for ordinary companions, always. The empty-inventory case is already covered by
    /// CompanionGlobalItem.NeedsAmmo, but this also stops a gel stack the player stocked
    /// from draining — a companion's flamethrower is never supposed to run out, whether or
    /// not anyone hands it fuel. Every other ammo type is consumed normally.
    ///
    /// The automation companion is excluded (it consumes gel like a real player) because the
    /// design forbids free ammo/materials for it (§1.3).
    /// </summary>
    public override bool CanConsumeAmmo(Item weapon, Item ammo) =>
        ammo.ammo != AmmoID.Gel || !CompanionEntitySystem.IsCompanion(Player) ||
        CompanionEntitySystem.IsAutoCompanion(Player);

    public override void OnHitNPC(NPC target, NPC.HitInfo hit, int damageDone)
    {
        if (Main.netMode != NetmodeID.SinglePlayer)
            return;

        if (target.active && !target.friendly)
        {
            RequestedTarget = target.whoAmI;
            CombatRequestTime = 300;
        }
    }

    public override void OnHitNPCWithItem(Item item, NPC target, NPC.HitInfo hit, int damageDone)
    {
        if (Main.netMode == NetmodeID.SinglePlayer)
            CombatHitSerial++;
    }

    public override void OnHitNPCWithProj(Projectile proj, NPC target, NPC.HitInfo hit, int damageDone)
    {
        if (Main.netMode == NetmodeID.SinglePlayer)
            CombatHitSerial++;
    }



    private void ObserveMiningAndTrees()
    {
        if (MiningObservationTime > 0)
            MiningObservationTime--;
        if (TreeAssistTime > 0)
            TreeAssistTime--;
        if (treeStreakTime > 0)
            treeStreakTime--;
        else
            recentTreesChopped = 0;

        Item heldItem = Player.HeldItem;
        bool activelyUsingTool = Player.controlUseItem && Player.itemAnimation > 0;
        if (activelyUsingTool && heldItem.pick > 0 && WorldGen.InWorld(Player.tileTargetX, Player.tileTargetY, 2))
        {
            Tile tile = Main.tile[Player.tileTargetX, Player.tileTargetY];
            if (tile.HasTile)
            {
                ObservedTileX = Player.tileTargetX;
                ObservedTileY = Player.tileTargetY;
                ObservedTileType = tile.TileType;
                // tileTarget already contains vanilla Smart Cursor's resolved left-click tile,
                // not merely the raw mouse position. Tunnel assistance therefore follows the
                // same down/left/right target the owner is actually mining with auto cursor.
                // Long enough that a brief pause between the owner's swings does not make the
                // companion down tools and walk off; short enough that it stops once the owner
                // genuinely moves on to something else.
                MiningObservationTime = 40;

                Point direction = CardinalDirection(Player.Center, new Vector2(ObservedTileX * 16f + 8f, ObservedTileY * 16f + 8f));
                if (direction == TunnelDirection)
                    TunnelDirectionTime++;
                else
                {
                    TunnelDirection = direction;
                    TunnelDirectionTime = 1;
                }
            }
        }
        else if (MiningObservationTime <= 0)
        {
            TunnelDirectionTime = System.Math.Max(0, TunnelDirectionTime - 4);
        }

        if (lastAxeTileExisted && WorldGen.InWorld(lastAxeTile.X, lastAxeTile.Y, 2) && !Main.tile[lastAxeTile.X, lastAxeTile.Y].HasTile)
        {
            recentTreesChopped++;
            treeStreakTime = 600;
            if (recentTreesChopped >= 2)
                TreeAssistTime = 600;
            lastAxeTileExisted = false;
        }

        if (activelyUsingTool && heldItem.axe > 0 && WorldGen.InWorld(Player.tileTargetX, Player.tileTargetY, 2))
        {
            Tile tile = Main.tile[Player.tileTargetX, Player.tileTargetY];
            if (tile.HasTile && Main.tileAxe[tile.TileType])
            {
                lastAxeTile = new Point(Player.tileTargetX, Player.tileTargetY);
                lastAxeTileExisted = true;
            }
        }
    }

    private static Point CardinalDirection(Vector2 origin, Vector2 target)
    {
        Vector2 offset = target - origin;
        if (System.Math.Abs(offset.X) >= System.Math.Abs(offset.Y))
            return new Point(System.Math.Sign(offset.X), 0);
        return new Point(0, System.Math.Sign(offset.Y));
    }
}
