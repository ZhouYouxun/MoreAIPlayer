using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.Localization;
using Terraria.ModLoader;

namespace MoreAIPlayer.Companion.Content;

/// <summary>
/// A paced, disposable companion integration test launched by <see cref="CompanionDevelopmentBook"/>.
/// It uses the same public spawn, profile, inventory-binding, mode and waypoint paths as normal play,
/// but owns a temporary profile and removes it at the end. World building, Hunter Game and player-scale
/// tests are intentionally excluded: those mutate the world or the real player and must stay explicit.
/// </summary>
public sealed class CompanionDevelopmentTestSystem : ModSystem
{
    private const int StepInterval = 30;
    private static ActiveRun run;

    private sealed class ActiveRun
    {
        public int OwnerWhoAmI;
        public int ProfileId;
        public int Step;
        public int TicksUntilNextStep = 1;
    }

    /// <summary>Starts a run, or stops and cleans up the currently running one.</summary>
    public static bool Toggle(Player owner)
    {
        if (run is not null)
        {
            Stop(clean: true);
            return false;
        }

        if (!ValidOwner(owner))
            return false;

        CompanionPlayer data = owner.GetModPlayer<CompanionPlayer>();
        CompanionProfile profile = data.CreateProfile(mirrorOwner: true);
        PrepareTestLoadout(profile);
        run = new ActiveRun
        {
            OwnerWhoAmI = owner.whoAmI,
            ProfileId = profile.Id
        };
        return true;
    }

    public override void PostUpdatePlayers()
    {
        if (run is null)
            return;

        if (!TryGetRunContext(out Player owner, out CompanionPlayer data, out CompanionProfile profile))
        {
            Stop(clean: true);
            return;
        }

        if (run.TicksUntilNextStep > 0)
        {
            run.TicksUntilNextStep--;
            return;
        }

        if (!RunStep(owner, profile))
        {
            if (run.Step <= 8)
            {
                Main.NewText(Language.GetTextValue("Mods.MoreAIPlayer.Items.CompanionDevelopmentBook.Failed"),
                    new Color(255, 150, 110));
            }
            Stop(clean: true);
            return;
        }

        AnnounceStep(run.Step);
        run.Step++;
        run.TicksUntilNextStep = StepInterval;
    }

    public override void OnWorldUnload() => Stop(clean: true);

    public override void Unload()
    {
        Stop(clean: true);
        run = null;
    }

    private static bool RunStep(Player owner, CompanionProfile profile)
    {
        switch (run.Step)
        {
            case 0:
                return CompanionEntitySystem.Summon(owner, profile,
                    owner.Center + new Vector2(owner.direction == 0 ? 48f : owner.direction * 48f, -16f));

            case 1:
                return SetMode(owner, profile, CompanionMode.Attack);
            case 2:
                return SetMode(owner, profile, CompanionMode.Escort);
            case 3:
                return SetMode(owner, profile, CompanionMode.Garrison);
            case 4:
                return SetMode(owner, profile, CompanionMode.Catch);
            case 5:
                return SetMode(owner, profile, CompanionMode.Fishing);
            case 6:
                return SetMode(owner, profile, CompanionMode.Mining);

            case 7:
                CompanionInstance instance = CompanionEntitySystem.FindByProfile(owner.whoAmI, profile.Id);
                if (instance is null || !instance.Entity.active)
                    return false;
                instance.Waypoint = owner.Center + new Vector2(owner.direction == 0 ? 128f : owner.direction * 128f, 0f);
                instance.WaypointTimeLeft = CompanionInstance.WaypointLifetime;
                CompanionEntitySystem.SpawnWaypointPing(instance.Waypoint.Value);
                return true;

            case 8:
                // This is the same backpack mutation normal transfers and the companion's own
                // pickup route rely on, without spawning a persistent world item into a save.
                return profile.TryAddToBackpack(new Item(ItemID.LesserHealingPotion, 3));

            default:
                Main.NewText(Language.GetTextValue("Mods.MoreAIPlayer.Items.CompanionDevelopmentBook.Completed"),
                    new Color(130, 255, 175));
                return false;
        }
    }

    private static bool SetMode(Player owner, CompanionProfile profile, CompanionMode mode)
    {
        if (CompanionEntitySystem.FindByProfile(owner.whoAmI, profile.Id) is not { Entity.active: true })
            return false;

        profile.Mode = mode;
        return true;
    }

    private static void AnnounceStep(int step)
    {
        string key = step switch
        {
            0 => "StepSummon",
            1 => "StepAttack",
            2 => "StepEscort",
            3 => "StepGarrison",
            4 => "StepCatch",
            5 => "StepFishing",
            6 => "StepMining",
            7 => "StepWaypoint",
            8 => "StepBackpack",
            _ => null
        };

        if (key is not null)
        {
            Main.NewText(Language.GetTextValue($"Mods.MoreAIPlayer.Items.CompanionDevelopmentBook.{key}"),
                new Color(130, 220, 255));
        }
    }

    private static bool TryGetRunContext(out Player owner, out CompanionPlayer data, out CompanionProfile profile)
    {
        owner = null;
        data = null;
        profile = null;
        if (run is null || run.OwnerWhoAmI < 0 || run.OwnerWhoAmI >= Main.maxPlayers)
            return false;

        owner = Main.player[run.OwnerWhoAmI];
        if (!ValidOwner(owner))
            return false;

        data = owner.GetModPlayer<CompanionPlayer>();
        profile = data.GetProfile(run.ProfileId);
        return profile is not null;
    }

    private static void Stop(bool clean)
    {
        if (run is null)
            return;

        if (clean && TryGetRunContext(out Player owner, out CompanionPlayer data, out CompanionProfile profile))
        {
            CompanionInstance instance = CompanionEntitySystem.FindByProfile(owner.whoAmI, profile.Id);
            if (instance is not null)
                CompanionEntitySystem.Dismiss(instance.Slot);
            data.Profiles.Remove(profile);
        }

        run = null;
    }

    private static bool ValidOwner(Player owner) =>
        Main.netMode == NetmodeID.SinglePlayer && owner is not null && owner.active &&
        owner.whoAmI == Main.myPlayer && !CompanionEntitySystem.IsCompanion(owner);

    private static void PrepareTestLoadout(CompanionProfile profile)
    {
        profile.Hotbar[CompanionProfile.GuardSlot] = new Item(ItemID.CopperBroadsword);
        profile.Hotbar[CompanionProfile.PickSlot] = new Item(ItemID.CopperPickaxe);
        profile.Hotbar[CompanionProfile.FirstCombatSlot] = new Item(ItemID.WoodenBow);
        profile.Ammo[0] = new Item(ItemID.WoodenArrow, 200);
        profile.Net[0] = new Item(ItemID.BugNet);
        profile.Rod[0] = new Item(ItemID.WoodFishingPole);
        profile.Backpack[0] = new Item(ItemID.LesserHealingPotion, 5);
        profile.Backpack[1] = new Item(ItemID.DirtBlock, 100);
        profile.Backpack[2] = new Item(ItemID.Torch, 25);
    }
}
