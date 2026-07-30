using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.Localization;
using Terraria.ModLoader;

namespace MoreAIPlayer.Companion.Hunter;

public sealed class HunterSystem : ModSystem
{
    private static readonly Dictionary<int, HunterInstance> BySlot = [];
    private static bool previousHostileState;
    private static bool previousHostileStateValid;

    public static bool HunterModeActive { get; set; } = false;
    public static int TargetHunterCount { get; set; } = 1;

    public static IEnumerable<HunterInstance> All => BySlot.Values;

    public static bool IsHunterSlot(int slot) => BySlot.ContainsKey(slot);

    public static bool IsHunter(Player player)
    {
        if (player is null || Main.netMode != NetmodeID.SinglePlayer)
            return false;

        HunterInstance instance = GetBySlot(player.whoAmI);
        return instance is not null && ReferenceEquals(instance.Entity, player);
    }

    public static HunterInstance GetBySlot(int slot) =>
        BySlot.TryGetValue(slot, out HunterInstance instance) ? instance : null;

    public static void ToggleMode()
    {
        if (Main.netMode != NetmodeID.SinglePlayer)
        {
            ResetModeAndClear();
            return;
        }

        HunterModeActive = !HunterModeActive;
        if (HunterModeActive)
        {
            Player localPlayer = Main.LocalPlayer;
            if (localPlayer is not null && !previousHostileStateValid)
            {
                previousHostileState = localPlayer.hostile;
                previousHostileStateValid = true;
            }
            Main.NewText(Language.GetTextValue("Mods.MoreAIPlayer.CompanionCommand.HunterModeEnabled"), 255, 80, 80);
        }
        else
        {
            ClearAllHunters();
            Main.NewText(Language.GetTextValue("Mods.MoreAIPlayer.CompanionCommand.HunterModeDisabled"), 120, 255, 120);
        }
    }

    public static void SetHunterCount(int count)
    {
        if (Main.netMode != NetmodeID.SinglePlayer)
        {
            ResetModeAndClear();
            return;
        }

        TargetHunterCount = System.Math.Clamp(count, 1, 3);
        if (HunterModeActive)
        {
            ClearAllHunters();
        }
    }

    public static bool SpawnHunter(Player targetPlayer, int index)
    {
        if (Main.netMode != NetmodeID.SinglePlayer)
            return false;

        int slot = FindFreeSlot();
        if (slot < 0)
            return false;

        Player original = Main.player[slot];
        Player entity = new() { whoAmI = slot };
        Main.player[slot] = entity;

        string[] names = ["猎人·Alpha", "猎人·Bravo", "猎人·Charlie"];
        string hunterName = index < names.Length ? names[index] : $"猎人 #{index + 1}";

        HunterInstance instance = new()
        {
            Slot = slot,
            Entity = entity,
            OriginalPlayer = original
        };
        BySlot[slot] = instance;

        instance.BindLoadout(targetPlayer, hunterName);

        // Spawn position: outside screen near player
        Vector2 spawnOffset = new(Main.rand.NextBool() ? -900f : 900f, Main.rand.NextFloat(-200f, 200f));
        entity.Center = targetPlayer.Center + spawnOffset;
        entity.velocity = Vector2.Zero;
        entity.fallStart = (int)(entity.position.Y / 16f);

        CompanionEntitySystem.SpawnRecallDustBurst(entity.Center);
        Main.NewText(Language.GetTextValue("Mods.MoreAIPlayer.CompanionCommand.HunterSpawned", entity.name), 255, 100, 100);
        return true;
    }

    private static int FindFreeSlot()
    {
        for (int slot = Main.maxPlayers - 1; slot >= 0; slot--)
        {
            if (slot == Main.myPlayer || CompanionEntitySystem.IsCompanionSlot(slot) || BySlot.ContainsKey(slot) || Main.player[slot].active)
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

    public static void ClearAllHunters()
    {
        if (Main.player is not null)
        {
            foreach ((int slot, HunterInstance instance) in BySlot)
            {
                CompanionEntitySystem.KillOwnedProjectiles(slot);
                instance.Entity.active = false;
                if (slot >= 0 && slot < Main.player.Length && Main.player[slot] == instance.Entity)
                    Main.player[slot] = instance.OriginalPlayer;
            }
            // Do not change a real player's PvP choice during a multiplayer transition. In
            // single-player, restore exactly the setting that existed before Manhunt began.
            if (Main.netMode == NetmodeID.SinglePlayer && Main.LocalPlayer is not null && previousHostileStateValid)
            {
                Main.LocalPlayer.hostile = previousHostileState;
            }
        }
        BySlot.Clear();
        previousHostileStateValid = false;
    }

    private static void ResetModeAndClear()
    {
        HunterModeActive = false;
        ClearAllHunters();
    }

    public override void Unload() => ResetModeAndClear();
    public override void OnWorldLoad() => ResetModeAndClear();
    public override void OnWorldUnload() => ResetModeAndClear();

    public override void PreUpdatePlayers()
    {
        if (Main.netMode != NetmodeID.SinglePlayer)
        {
            ResetModeAndClear();
            return;
        }

        if (!HunterModeActive)
        {
            if (BySlot.Count > 0)
                ClearAllHunters();
            return;
        }

        Player localPlayer = Main.LocalPlayer;
        if (localPlayer is null || !localPlayer.active || localPlayer.dead)
            return;

        // Force PvP enabled on local player when Hunter mode is active
        if (!previousHostileStateValid)
        {
            previousHostileState = localPlayer.hostile;
            previousHostileStateValid = true;
        }
        localPlayer.hostile = true;

        // Maintain target count of active hunters
        int currentActiveCount = 0;
        foreach (HunterInstance instance in BySlot.Values)
        {
            if (instance.Entity.active && !instance.Entity.dead)
                currentActiveCount++;
        }

        if (currentActiveCount < TargetHunterCount && BySlot.Count < TargetHunterCount)
        {
            SpawnHunter(localPlayer, BySlot.Count);
        }

        // Update brain for each hunter
        List<int> conflictedSlots = null;
        foreach ((int slot, HunterInstance instance) in BySlot)
        {
            Player me = instance.Entity;
            if (!ReferenceEquals(Main.player[slot], me))
            {
                if (Main.player[slot].active)
                {
                    Mod.Logger.Error($"Hunter slot {slot} was taken by active player '{Main.player[slot].name}'. Hunter released safely.");
                    (conflictedSlots ??= []).Add(slot);
                    continue;
                }

                Main.player[slot] = me;
                instance.SlotRepairs++;
            }

            if (me.dead || me.statLife <= 0)
            {
                if (instance.ReviveTicksLeft <= 0)
                {
                    instance.ReviveTicksLeft = 3600; // 60s respawn
                    Main.NewText(Language.GetTextValue("Mods.MoreAIPlayer.CompanionCommand.HunterDefeated", me.name), 255, 240, 100);
                }

                instance.ReviveTicksLeft--;
                if (instance.ReviveTicksLeft <= 0)
                {
                    // Respawn hunter with fresh loadout
                    instance.ReviveTicksLeft = 0;
                    instance.BindLoadout(localPlayer, me.name);
                    Vector2 spawnOffset = new(Main.rand.NextBool() ? -900f : 900f, Main.rand.NextFloat(-200f, 200f));
                    me.Center = localPlayer.Center + spawnOffset;
                    CompanionEntitySystem.SpawnRecallDustBurst(me.Center);
                    Main.NewText(Language.GetTextValue("Mods.MoreAIPlayer.CompanionCommand.HunterRespawned", me.name), 255, 100, 100);
                }
                else
                {
                    // Hold corpse state cleanly during revive countdown
                    me.dead = true;
                    me.statLife = 0;
                    me.velocity = Vector2.Zero;
                    me.respawnTimer = instance.ReviveTicksLeft + 60;
                    ResetCorpseGibs(me);
                }
                continue;
            }

            instance.Brain.Update(instance, localPlayer);
            instance.AimWorld = CompanionEntitySystem.SanitizeHunterAim(me, instance.AimWorld, instance);
            CompanionModBridge.FeedMouseState(me, instance.AimWorld, instance.WantsAltUse);
            instance.ItemCheckPreparedFrame = Main.GameUpdateCount;
        }

        if (conflictedSlots is not null)
        {
            foreach (int slot in conflictedSlots)
            {
                if (BySlot.Remove(slot, out HunterInstance conflicted))
                    conflicted.Entity.active = false;
            }
        }
    }

    private static void ResetCorpseGibs(Player me)
    {
        me.headPosition = Vector2.Zero;
        me.bodyPosition = Vector2.Zero;
        me.legPosition = Vector2.Zero;
        me.headRotation = 0f;
        me.bodyRotation = 0f;
        me.legRotation = 0f;
        me.headVelocity = Vector2.Zero;
        me.bodyVelocity = Vector2.Zero;
        me.legVelocity = Vector2.Zero;
    }
}
