using Microsoft.Xna.Framework;
using MoreAIPlayer.Companion.Building;
using Terraria;
using Terraria.Localization;

namespace MoreAIPlayer.Companion.Commands;

/// <summary>
/// Translates the new wheel's vocabulary into the existing, battle-tested companion modes.
/// The translation is intentionally one-way: using an old book never damages or deletes the
/// wheel state, and selecting a wheel command never removes the old book UI.
/// </summary>
public static class CompanionCommandRules
{
    public static readonly CompanionWorkMode[] WorkOrder =
    [
        CompanionWorkMode.Idle,
        CompanionWorkMode.Combat,
        CompanionWorkMode.Exploration,
        CompanionWorkMode.Building,
        CompanionWorkMode.Farming,
        CompanionWorkMode.Fishing,
        CompanionWorkMode.Capturing
    ];

    public static string WorkName(CompanionWorkMode mode) => T($"WheelWork{mode}");

    public static Color WorkTint(CompanionWorkMode mode) => mode switch
    {
        CompanionWorkMode.Idle => new Color(170, 185, 210),
        CompanionWorkMode.Combat => new Color(245, 105, 100),
        CompanionWorkMode.Exploration => new Color(120, 205, 245),
        CompanionWorkMode.Building => new Color(240, 185, 95),
        CompanionWorkMode.Farming => new Color(115, 220, 125),
        CompanionWorkMode.Fishing => new Color(90, 175, 245),
        CompanionWorkMode.Capturing => new Color(205, 140, 245),
        _ => Color.White
    };

    public static int CommandCount(CompanionWorkMode mode) => mode switch
    {
        CompanionWorkMode.Idle => 3,
        CompanionWorkMode.Combat => 5,
        CompanionWorkMode.Exploration => 5,
        CompanionWorkMode.Building => 3,
        CompanionWorkMode.Farming => 4,
        CompanionWorkMode.Fishing => 2,
        CompanionWorkMode.Capturing => 3,
        _ => 0
    };

    public static string CommandName(CompanionProfile profile, int index)
    {
        if (index < 0 || index >= 5)
            return string.Empty;

        string letter = ((char)('A' + index)).ToString();
        string suffix = profile.WorkMode switch
        {
            CompanionWorkMode.Idle => index switch { 0 => "IdleStay", 1 => "IdleFollow", 2 => "IdleFree", _ => null },
            CompanionWorkMode.Combat => index switch { 0 => "CombatSelf", 1 => "CombatProtect", 2 => "CombatShared", 3 => "CombatPatrol", 4 => "CombatGarrison", _ => null },
            CompanionWorkMode.Exploration => index switch { 0 => "ExploreFollow", 1 => "ExploreFree", 2 => "ExploreLight", 3 => "ExploreSafety", 4 => "ExploreGather", _ => null },
            CompanionWorkMode.Building => index switch { 0 => "BuildPlatform", 1 => "BuildDefensive", 2 => "BuildDestroy", _ => null },
            CompanionWorkMode.Farming => index switch { 0 => "FarmTree", 1 => "FarmHerb", 2 => "FarmPumpkin", 3 => "FarmCombined", _ => null },
            CompanionWorkMode.Fishing => index switch { 0 => "FishingFish", 1 => "FishingWater", _ => null },
            CompanionWorkMode.Capturing => index switch { 0 => "CaptureFixed", 1 => "CaptureFree", 2 => "CaptureBreak", _ => null },
            _ => null
        };
        if (suffix is null)
            return letter;

        string label = T($"WheelCommand{suffix}");
        if (IsToggle(profile.WorkMode, index))
            label = $"{label} [{(ToggleValue(profile, index) ? T("WheelOn") : T("WheelOff"))}]";
        return $"{letter}  {label}";
    }

    public static bool IsToggle(CompanionWorkMode mode, int index) =>
        mode == CompanionWorkMode.Exploration && index >= 2 ||
        mode == CompanionWorkMode.Capturing && index == 2;

    public static bool ToggleValue(CompanionProfile profile, int index) => profile.WorkMode switch
    {
        CompanionWorkMode.Exploration when index == 2 => profile.ExplorationLighting,
        CompanionWorkMode.Exploration when index == 3 => profile.ExplorationHazardRemoval,
        CompanionWorkMode.Exploration when index == 4 => profile.ExplorationGathering,
        CompanionWorkMode.Capturing when index == 2 => profile.CapturingBreakDebris,
        _ => false
    };

    /// <summary>Applies one A-E sector and returns false when that sector is unused.</summary>
    public static bool ApplyCommand(CompanionProfile profile, int index, Player owner, CompanionInstance instance)
    {
        if (index < 0 || index >= CommandCount(profile.WorkMode))
            return false;

        profile.WheelCommandsActive = true;
        if (IsToggle(profile.WorkMode, index))
        {
            if (profile.WorkMode == CompanionWorkMode.Exploration)
            {
                if (index == 2) profile.ExplorationLighting = !profile.ExplorationLighting;
                if (index == 3) profile.ExplorationHazardRemoval = !profile.ExplorationHazardRemoval;
                if (index == 4) profile.ExplorationGathering = !profile.ExplorationGathering;
            }
            else if (profile.WorkMode == CompanionWorkMode.Capturing)
            {
                profile.CapturingBreakDebris = !profile.CapturingBreakDebris;
            }
        }
        else
        {
            switch (profile.WorkMode)
            {
                case CompanionWorkMode.Idle: profile.IdleCommand = (IdleCommand)index; break;
                case CompanionWorkMode.Combat: profile.CombatCommand = (CombatCommand)index; break;
                case CompanionWorkMode.Exploration: profile.ExplorationCommand = (ExplorationCommand)index; break;
                case CompanionWorkMode.Building: profile.BuildingCommand = (BuildingCommand)index; break;
                case CompanionWorkMode.Farming: profile.FarmingCommand = (FarmingCommand)index; break;
                case CompanionWorkMode.Fishing: profile.FishingCommand = (FishingCommand)index; break;
                case CompanionWorkMode.Capturing: profile.CapturingCommand = (CapturingCommand)index; break;
            }
        }

        CaptureCommandArea(instance);
        ApplyToLegacySystems(profile, owner);
        return true;
    }

    public static void SelectWorkMode(CompanionProfile profile, CompanionWorkMode mode, Player owner, CompanionInstance instance)
    {
        profile.WorkMode = mode;
        profile.WheelCommandsActive = true;
        CaptureCommandArea(instance);
        ApplyToLegacySystems(profile, owner);
    }

    /// <summary>
    /// Keeps the old AI as the engine. Commands that already have an exact historical equivalent
    /// select it here; genuinely new jobs retain their own state for their dedicated controller.
    /// </summary>
    public static void ApplyToLegacySystems(CompanionProfile profile, Player owner)
    {
        CompanionMode mode = profile.WorkMode switch
        {
            CompanionWorkMode.Idle => profile.IdleCommand switch
            {
                IdleCommand.Stay => CompanionMode.Garrison,
                IdleCommand.Follow => CompanionMode.Escort,
                _ => CompanionMode.FreeRoam
            },
            CompanionWorkMode.Combat => profile.CombatCommand switch
            {
                CombatCommand.ProtectOwner => CompanionMode.Escort,
                CombatCommand.Patrol => CompanionMode.FreeRoam,
                CombatCommand.Garrison => CompanionMode.Garrison,
                _ => CompanionMode.Attack
            },
            CompanionWorkMode.Exploration => profile.ExplorationGathering
                ? CompanionMode.Mining
                : profile.ExplorationCommand == ExplorationCommand.Follow
                    ? CompanionMode.Escort : CompanionMode.FreeRoam,
            CompanionWorkMode.Building => profile.BuildingCommand == BuildingCommand.Destruction
                ? CompanionMode.Mining : CompanionMode.Escort,
            CompanionWorkMode.Farming => CompanionMode.FreeRoam,
            CompanionWorkMode.Fishing => profile.FishingCommand == FishingCommand.Fish
                ? CompanionMode.Fishing : CompanionMode.Garrison,
            CompanionWorkMode.Capturing => profile.CapturingCommand == CapturingCommand.FixedArea
                ? CompanionMode.Catch : CompanionMode.Catch,
            _ => CompanionMode.Attack
        };
        profile.Mode = mode;

        CompanionPlayer data = owner.GetModPlayer<CompanionPlayer>();
        if (profile.WorkMode == CompanionWorkMode.Building)
        {
            data.BuildTacticActive = profile.BuildingCommand != BuildingCommand.Destruction;
            data.BuildMode = profile.BuildingCommand == BuildingCommand.Defensive
                ? CompanionBuildMode.DefensiveBuild
                : CompanionBuildMode.SmartBuild;
        }
    }

    private static void CaptureCommandArea(CompanionInstance instance)
    {
        if (instance is null)
            return;
        instance.CommandAnchor = instance.Entity.Center;
        instance.CommandArea = new Rectangle(
            (int)Main.screenPosition.X,
            (int)Main.screenPosition.Y,
            Main.screenWidth,
            Main.screenHeight);
        instance.HasCommandArea = true;
    }

    private static string T(string key) =>
        Language.GetTextValue($"Mods.MoreAIPlayer.CompanionCommand.{key}");
}
