using System.Collections.Generic;
using Microsoft.Xna.Framework;
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
    public const int VanitySize = 3;
    public const int DyeSize = 3;
    public const int BackpackSize = 40;
    private const int RescueSignalWindow = 10;

    public List<CompanionProfile> Profiles { get; private set; } = [];
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

    public override void Initialize() => Profiles = [];

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
    }

    public override void LoadData(TagCompound tag)
    {
        Profiles = [];
        if (!tag.ContainsKey(nameof(Profiles)))
        {
            if (tag.ContainsKey(nameof(CompanionProfile.Hotbar)))
                Profiles.Add(CompanionProfile.LoadLegacy(0, Player, tag));
            return;
        }

        foreach (TagCompound savedProfile in tag.GetList<TagCompound>(nameof(Profiles)))
            Profiles.Add(CompanionProfile.Load(savedProfile));
    }

    public int CustomPotionCooldown;
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

            Player.statDefense.AdditiveBonus += 20f;
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
        // Companion movement speed is pinned to 95% of the owner's CURRENT effective speed.
        // This runs after vanilla computed speeds from the companion's own gear, so mobility
        // accessories on the companion cannot raise (or lower) it — a direct set, not a base.
        CompanionInstance instance = CompanionEntitySystem.GetBySlot(Player.whoAmI);
        if (instance is null || !ReferenceEquals(instance.Entity, Player))
            return;

        Player owner = instance.Owner;
        if (!owner.active)
            return;

        float speedMultiplier = WhipBuffTimer > 0 ? 1.20f : 1.0f;
        float speed = System.Math.Max(1f, System.Math.Max(owner.maxRunSpeed, owner.accRunSpeed) * 0.95f * speedMultiplier);
        Player.maxRunSpeed = speed;
        Player.accRunSpeed = speed;
        Player.runAcceleration = System.Math.Max(0.05f, owner.runAcceleration * 0.95f * speedMultiplier);
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

        if (CustomPotionCooldown > 0)
            CustomPotionCooldown--;

        if (CompanionEntitySystem.IsCompanion(Player))
        {
            Player.statMana = Player.statManaMax2;
            if (!Player.dead && CustomPotionCooldown <= 0 && Player.statLife <= Player.statLifeMax2 - 100)
            {
                TryUseHealingPotion();
            }
        }

        ObserveMiningAndTrees();
    }

    private void TryUseHealingPotion()
    {
        int bestSlot = -1;
        int maxHeal = 100;

        for (int i = 0; i < Player.inventory.Length; i++)
        {
            Item item = Player.inventory[i];
            if (!item.IsAir && item.stack > 0 && item.healLife > maxHeal)
            {
                maxHeal = item.healLife;
                bestSlot = i;
            }
        }

        int healAmount;
        if (bestSlot >= 0)
        {
            Item item = Player.inventory[bestSlot];
            healAmount = item.healLife;
            item.stack--;
            if (item.stack <= 0)
                item.TurnToAir();
        }
        else
        {
            healAmount = 100;
        }

        Player.statLife = System.Math.Min(Player.statLifeMax2, Player.statLife + healAmount);
        CombatText.NewText(Player.Hitbox, Color.Green, healAmount.ToString());
        Terraria.Audio.SoundEngine.PlaySound(SoundID.Item3, Player.Center);
        CustomPotionCooldown = 1800; // 30s CD
        Player.potionDelay = 1800;
    }

    public override bool CanBeHitByProjectile(Projectile proj)
    {
        if (CompanionEntitySystem.IsCompanion(Player))
        {
            if (IsWhipProjectile(proj) && proj.owner != Player.whoAmI)
                return true;

            if (IsExplosiveOrTrapProjectile(proj))
                return false;
        }
        return base.CanBeHitByProjectile(proj);
    }

    public override void ModifyHurt(ref Player.HurtModifiers modifiers)
    {
        if (!CompanionEntitySystem.IsCompanion(Player))
            return;

        // Block Spike tile damage or trap/explosive projectile damage
        if (modifiers.DamageSource.SourceOtherIndex == 3)
        {
            modifiers.Cancel();
            return;
        }

        int projIndex = modifiers.DamageSource.SourceProjectileLocalIndex;
        if (projIndex >= 0 && projIndex < Main.maxProjectiles)
        {
            Projectile proj = Main.projectile[projIndex];
            if (IsExplosiveOrTrapProjectile(proj))
            {
                modifiers.Cancel();
                return;
            }

            if (IsWhipProjectile(proj) && proj.owner != Player.whoAmI)
            {
                // Force whip hit damage to exactly 1 point
                modifiers.SetMaxDamage(1);
                return;
            }
        }

        // 20% damage reduction for all other damage
        modifiers.FinalDamage *= 0.8f;
    }

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

    private static bool IsExplosiveOrTrapProjectile(Projectile proj)
    {
        if (proj is null || !proj.active)
            return false;
        if (proj.trap)
            return true;

        int type = proj.type;
        if (type == ProjectileID.Boulder || type == ProjectileID.RollingCactus ||
            type == ProjectileID.GeyserTrap || type == ProjectileID.SpearTrap ||
            type == ProjectileID.SpikyBallTrap || type == ProjectileID.FlamesTrap ||
            type == ProjectileID.PoisonDart)
            return true;

        if (proj.aiStyle == ProjAIStyleID.Explosive ||
            type == ProjectileID.Grenade || type == ProjectileID.StickyGrenade || type == ProjectileID.BouncyGrenade ||
            type == ProjectileID.Bomb || type == ProjectileID.StickyBomb || type == ProjectileID.BouncyBomb ||
            type == ProjectileID.Dynamite || type == ProjectileID.StickyDynamite || type == ProjectileID.BouncyDynamite ||
            type == ProjectileID.Explosives || type == ProjectileID.Landmine || type == ProjectileID.BombFish ||
            type == ProjectileID.DryBomb || type == ProjectileID.WetBomb || type == ProjectileID.LavaBomb || type == ProjectileID.HoneyBomb ||
            type == ProjectileID.DirtBomb || type == ProjectileID.ScarabBomb)
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
