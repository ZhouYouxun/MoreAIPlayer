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
    private delegate void OrigPostUpdate();
    private delegate void PostUpdateDetour(OrigPostUpdate orig);
    private static TeamScope frameScope;
    private static bool frameStarted;
    private static bool inPlayerPhase;
    private static ulong frame;

    public override void Load()
    {
        if (!Main.dedServ)
        {
            MonoModHooks.Add(typeof(Player).GetMethod(nameof(Player.Update)), (UpdateDetour)UpdatePlayer);
            MonoModHooks.Add(typeof(SystemLoader).GetMethod(nameof(SystemLoader.PreUpdatePlayers)),
                (PostUpdateDetour)StartPlayers);
            MonoModHooks.Add(typeof(SystemLoader).GetMethod(nameof(SystemLoader.PostUpdatePlayers)),
                (PostUpdateDetour)FinishPlayers);
        }
    }

    public override void OnWorldUnload() => EndFrame();
    public override void Unload() => EndFrame();

    private static void StartPlayers(OrigPostUpdate orig)
    {
        EndFrame();
        orig();
        inPlayerPhase = true;
    }

    private static void FinishPlayers(OrigPostUpdate orig)
    {
        EndFrame(); // Restore before any mod's post-player hooks or enemy AI run.
        orig();
    }

    private static void EndFrame()
    {
        frameScope.Dispose();
        frameScope = default;
        frameStarted = false;
        inPlayerPhase = false;
    }

    private static void UpdatePlayer(OrigUpdate orig, Player player, int index)
    {
        if (frameStarted && frame != Main.GameUpdateCount)
            EndFrame();
        if (!CompanionRuntimePolicy.CanRunSyntheticPlayers || !CompanionEntitySystem.HasCompanions)
        {
            EndFrame();
            orig(player, index);
            return;
        }
        if (!player.active || player.dead)
        {
            try { orig(player, index); }
            catch { EndFrame(); throw; }
            return;
        }

        // All players are updated sequentially in one engine phase. Establish the shared
        // team once for that phase, not once per teammate (which scanned the squad N times).
        if (!frameStarted)
        {
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
            else if (player.whoAmI != Main.myPlayer)
            {
                // Other synthetic actors must not start a batch for an unrelated owner.
                orig(player, index);
                return;
            }
            if (!inPlayerPhase)
            {
                // Foreign mods may call Player.Update outside the main player loop.
                // Such standalone calls must still restore before returning to their caller.
                using TeamScope scope = new(owner);
                orig(player, index);
                return;
            }
            frameScope = new TeamScope(owner);
            frame = Main.GameUpdateCount;
            frameStarted = true;
        }
        try { orig(player, index); }
        catch
        {
            EndFrame(); // Main may abort the frame before PostUpdatePlayers on a mod error.
            throw;
        }
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

            foreach (CompanionInstance instance in CompanionEntitySystem.LiveInstances)
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
            {
                if (members[i].team == 1)
                    members[i].team = 0;
                members[i] = null;
            }
            ArrayPool<Player>.Shared.Return(members);
        }
    }
}
