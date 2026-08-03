using System.Collections.Generic;
using Microsoft.Xna.Framework;
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
    public CompanionBuildMode BuildMode = CompanionBuildMode.SmartAssist;
    public bool SmartBuildEnabled;
    public int RequestedTarget = -1;
    public int CombatRequestTime;

    /// <summary>Set true whenever this player's weapon connects; the brain reads and clears
    /// it to detect a whiffed melee swing (see CompanionBrain's miss-streak aggression).</summary>
    public bool MeleeHitLanded;

    /// <summary>Ticks remaining on the "come guard me" signal. Companions read their owner's
    /// copy to decide whether to teleport in and guard (see CompanionBrain's rescue policy).
    /// Raised both automatically when this player is hurt and manually by a quick double
    /// right-click. The window spans a few ticks so every companion sees the same signal
    /// during its own update, rather than only the first one to run.</summary>
    public int RescueSignalTime;

    /// <summary>Ticks remaining on an explicit "come to me now" order (the double right-click).
    /// Unlike the automatic hurt signal this overrides Garrison mode — a manual recall pulls a
    /// posted companion off its spot, whereas a stray hit should not.</summary>
    public int ManualRescueTime;

    /// <summary>Raises the automatic escort signal (owner was hurt) so nearby-but-not-adjacent
    /// companions teleport to this player's side on their next update.</summary>
    public void SignalRescue() => RescueSignalTime = RescueSignalWindow;

    /// <summary>Raises the manual recall order (double right-click); stronger than <see cref="SignalRescue"/>.</summary>
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
        BuildMode = CompanionBuildMode.SmartAssist;
        SmartBuildEnabled = false;
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

    public override void SaveData(TagCompound tag)
    {
        List<TagCompound> savedProfiles = new(Profiles.Count);
        foreach (CompanionProfile profile in Profiles)
            savedProfiles.Add(profile.Save());
        tag[nameof(Profiles)] = savedProfiles;
        tag[nameof(BuildMode)] = (int)BuildMode;
        tag[nameof(SmartBuildEnabled)] = SmartBuildEnabled;
    }

    public override void LoadData(TagCompound tag)
    {
        Profiles = [];
        int savedMode = tag.ContainsKey(nameof(BuildMode)) ? tag.GetInt(nameof(BuildMode)) : 0;
        BuildMode = System.Enum.IsDefined(typeof(CompanionBuildMode), savedMode)
            ? (CompanionBuildMode)savedMode
            : CompanionBuildMode.SmartAssist;
        SmartBuildEnabled = tag.ContainsKey(nameof(SmartBuildEnabled)) && tag.GetBool(nameof(SmartBuildEnabled));
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
            Player.statMana = Player.statManaMax2;
            if (!Player.dead && Player.potionDelay <= 0 && Player.statLife <= Player.statLifeMax2 - 100)
            {
                TryUseHealingPotion();
            }
        }

        ObserveMiningAndTrees();
    }

    private void TryUseHealingPotion()
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
        CompanionFaultGuard.RunCritical(CompanionFaultGuard.HealingPotionLane, Player.QuickHeal);
    }

    public override bool CanBeHitByProjectile(Projectile proj)
    {
        if (CompanionEntitySystem.IsCompanion(Player) &&
            IsWhipProjectile(proj) && proj.owner != Player.whoAmI)
            return true;
        return base.CanBeHitByProjectile(proj);
    }

    /// <summary>
    /// When survival is disabled, use a free dodge instead of only reducing damage: tML keeps a
    /// minimum of one damage after damage modifiers, while a dodge cleanly covers NPC attacks,
    /// projectiles, fall damage and environmental damage. The default remains false here, so
    /// companions normally take real damage and use the brain's existing death/revive flow.
    /// </summary>
    public override bool FreeDodge(Player.HurtInfo info) =>
        CompanionEntitySystem.IsCompanion(Player) &&
        MoreAIPlayerClientConfig.Instance?.CompanionsTakeDamage == false;

    public override void OnHurt(Player.HurtInfo info)
    {
        if (Main.netMode != NetmodeID.SinglePlayer)
            return;

        // Any damage counts, not just NPC contact — fall damage, lava and traps all mean the
        // owner is in trouble and wants the escort back at their side.
        SignalRescue();

        int sourceNpc = info.DamageSource.SourceNPCIndex;
        if (sourceNpc >= 0 && sourceNpc < Main.maxNPCs && Main.npc[sourceNpc].active)
        {
            RequestedTarget = sourceNpc;
            CombatRequestTime = 300;
        }

        if (CompanionEntitySystem.IsCompanion(Player))
        {
            int projIndex = info.DamageSource.SourceProjectileLocalIndex;
            if (projIndex >= 0 && projIndex < Main.maxProjectiles)
            {
                Projectile proj = Main.projectile[projIndex];
                if (IsWhipProjectile(proj) && proj.owner != Player.whoAmI)
                {
                    WhipBuffTimer = 600; // 10s buff
                    SpawnWhipSparkles(Player.Center);
                }
            }
        }
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

    private static bool IsWhipProjectile(Projectile proj)
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
    /// Gel is free for companions, always. The empty-inventory case is already covered by
    /// CompanionGlobalItem.NeedsAmmo, but this also stops a gel stack the player stocked
    /// from draining — a companion's flamethrower is never supposed to run out, whether or
    /// not anyone hands it fuel. Every other ammo type is consumed normally.
    /// </summary>
    public override bool CanConsumeAmmo(Item weapon, Item ammo) =>
        ammo.ammo != AmmoID.Gel || !CompanionEntitySystem.IsCompanion(Player);

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
            MeleeHitLanded = true;
    }

    public override void OnHitNPCWithProj(Projectile proj, NPC target, NPC.HitInfo hit, int damageDone)
    {
        if (Main.netMode == NetmodeID.SinglePlayer)
            MeleeHitLanded = true;
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
                MiningObservationTime = 20;

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
