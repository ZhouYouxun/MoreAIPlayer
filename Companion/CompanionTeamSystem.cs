using System;
using System.Buffers;
using Terraria;
using Terraria.ModLoader;

namespace MoreAIPlayer.Companion;

/// <summary>Exposes the owner's ordinary companions as teammates to normal player-effect hooks.</summary>
public sealed class CompanionTeamSystem : ModSystem
{
    private delegate void OrigUpdate(Player player, int index);
    private delegate void UpdateDetour(OrigUpdate orig, Player player, int index);

    public override void Load()
    {
        if (!Main.dedServ)
            MonoModHooks.Add(typeof(Player).GetMethod(nameof(Player.Update)), (UpdateDetour)UpdatePlayer);
    }

    private static void UpdatePlayer(OrigUpdate orig, Player player, int index)
    {
        if (!CompanionRuntimePolicy.CanRunSyntheticPlayers || !player.active || player.dead)
        {
            orig(player, index);
            return;
        }

        Player owner = player;
        if (CompanionEntitySystem.IsCompanion(player))
        {
            CompanionInstance instance = CompanionEntitySystem.GetBySlot(player.whoAmI);
            if (!CanShare(instance) || !instance.TryGetContext(out owner, out _))
            {
                orig(player, index);
                return;
            }
        }

        // Existing teams keep their exact meaning. Singleplayer's default team 0 means
        // "no team" even for two allies, so team-based auras otherwise cannot flow back.
        using TeamScope scope = new(owner);
        orig(player, index);
    }

    private static bool CanShare(CompanionInstance instance) => !instance.IsAutoIdentity &&
        !CompanionEntitySystem.IsOrdinaryHostileCompanion(instance.Entity);

    private readonly struct TeamScope : IDisposable
    {
        private readonly Player[] members;
        private readonly int count;

        public TeamScope(Player owner)
        {
            members = null;
            count = 0;
            if (owner.team != 0 || !owner.active)
                return;

            foreach (CompanionInstance instance in CompanionEntitySystem.All)
            {
                Player companion = instance.Entity;
                if (instance.OwnerWhoAmI != owner.whoAmI || !CanShare(instance) ||
                    !companion.active || companion.dead || companion.team != 0)
                    continue;
                if (members is null)
                {
                    members = ArrayPool<Player>.Shared.Rent(Main.maxPlayers);
                    members[count++] = owner;
                    owner.team = 1;
                }
                members[count++] = companion;
                companion.team = 1;
            }
        }

        public void Dispose()
        {
            if (members is null)
                return;
            for (int i = 0; i < count; i++)
                if (members[i].team == 1)
                    members[i].team = 0;
            ArrayPool<Player>.Shared.Return(members, clearArray: true);
        }
    }
}
