using System;
using System.Buffers;
using Terraria;
using Terraria.ModLoader;

namespace MoreAIPlayer.Companion;

/// <summary>Removes companions from enemy perception only while enemy AI is running.</summary>
public sealed class CompanionTargetingSystem : ModSystem
{
    private delegate void OrigNpcAI(NPC npc);
    private delegate void NpcAIDetour(OrigNpcAI orig, NPC npc);
    private delegate void OrigProjectileAI(Projectile projectile);
    private delegate void ProjectileAIDetour(OrigProjectileAI orig, Projectile projectile);

    public override void Load()
    {
        if (Main.dedServ)
            return;
        MonoModHooks.Add(typeof(NPC).GetMethod(nameof(NPC.AI)), (NpcAIDetour)UpdateNpcAI);
        MonoModHooks.Add(typeof(Projectile).GetMethod(nameof(Projectile.AI)), (ProjectileAIDetour)UpdateProjectileAI);
    }

    private static bool Enabled => CompanionRuntimePolicy.CanRunSyntheticPlayers &&
        CompanionEntitySystem.HasCompanions &&
        MoreAIPlayerClientConfig.Current?.EnemiesIgnoreCompanions == true;

    private static void UpdateNpcAI(OrigNpcAI orig, NPC npc)
    {
        if (npc.friendly || !Enabled)
        {
            orig(npc);
            return;
        }

        using PerceptionScope scope = new();
        // An enemy may already be pursuing a companion when the option is switched on.
        if (npc.target >= 0 && npc.target < Main.maxPlayers &&
            CompanionEntitySystem.IsCompanion(Main.player[npc.target]))
        {
            npc.target = Main.maxPlayers;
            npc.TargetClosest(faceTarget: false);
        }
        orig(npc);
    }

    private static void UpdateProjectileAI(OrigProjectileAI orig, Projectile projectile)
    {
        if (!projectile.hostile || !Enabled)
        {
            orig(projectile);
            return;
        }

        using PerceptionScope scope = new();
        orig(projectile);
    }

    // TargetClosest, FindClosest and modded active-player scans see the same eligibility.
    // Do not lower aggro: that would still select a companion when every other player dies.
    // The scope ends before collision/damage and never changes immunity or the damage option.
    private readonly struct PerceptionScope : IDisposable
    {
        private readonly Player[] hidden;
        private readonly int count;

        public PerceptionScope()
        {
            hidden = null;
            count = 0;
            foreach (CompanionInstance instance in CompanionEntitySystem.LiveInstances)
            {
                Player player = instance.Entity;
                if (!player.active || !ReferenceEquals(Main.player[instance.Slot], player))
                    continue;
                hidden ??= ArrayPool<Player>.Shared.Rent(Main.maxPlayers);
                hidden[count++] = player;
                player.active = false;
            }
        }

        public void Dispose()
        {
            if (hidden is null)
                return;
            for (int i = 0; i < count; i++)
            {
                Player player = hidden[i];
                // A foreign callback may dismiss a companion; never resurrect its released slot.
                if (CompanionEntitySystem.IsCompanion(player) &&
                    ReferenceEquals(Main.player[player.whoAmI], player))
                    player.active = true;
                hidden[i] = null;
            }
            ArrayPool<Player>.Shared.Return(hidden);
        }
    }
}
