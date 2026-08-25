using Terraria;
using Terraria.ID;
using Terraria.Localization;

namespace MoreAIPlayer.Companion.Automation;

/// <summary>Transition-only chat reporting for the opt-in automation progress setting.</summary>
public static class AutoProgressReporter
{
    public static void ReportPlan(CompanionProfile profile, AutoCampaignStage stage, AutoPlan plan)
    {
        AutoCompanionState state = profile?.AutoState;
        if (state is null)
            return;

        string token = $"{stage}:{plan.Goal}:{state.PreferredClass}:{plan.ItemType}:{plan.TileType}:{plan.Amount}";
        if (state.LastReportedStep == token)
            return;
        state.LastReportedStep = token;
        state.LastReportedProgressSerial++;

        if (!MoreAIPlayerClientConfig.AutomationProgressChatEnabled)
            return;

        string stageName = LocalizedEnum("AutomationStage", stage.ToString());
        string goalName = LocalizedEnum("AutomationGoal", plan.Goal.ToString());
        string className = LocalizedEnum("AutomationClass", state.PreferredClass.ToString());
        Main.NewText(Language.GetTextValue(
            "Mods.MoreAIPlayer.CompanionCommand.AutomationProgressStep",
            profile.Name, stageName, goalName, className), 126, 220, 255);
    }

    public static void ReportRecovery(CompanionProfile profile, string key, params object[] args)
    {
        if (!MoreAIPlayerClientConfig.AutomationProgressChatEnabled)
            return;
        Main.NewText(Language.GetTextValue(
            $"Mods.MoreAIPlayer.CompanionCommand.{key}", args), 255, 190, 105);
    }

    public static void ReportSelfRescue(CompanionProfile profile, AutoSelfRescueReason reason,
        int level, AutoSelfRescueAction action)
    {
        if (!MoreAIPlayerClientConfig.AutomationProgressChatEnabled || profile is null)
            return;
        Main.NewText(Language.GetTextValue(
            "Mods.MoreAIPlayer.CompanionCommand.AutomationSelfRescue",
            profile.Name,
            LocalizedEnum("AutomationSelfRescueReason", reason.ToString()),
            level,
            LocalizedEnum("AutomationSelfRescueAction", action.ToString())), 255, 175, 95);
    }

    public static void ReportWeapon(CompanionProfile profile, Item item,
        AutoWeaponAssessment assessment)
    {
        if (!MoreAIPlayerClientConfig.AutomationProgressChatEnabled || profile is null ||
            item is null || item.type <= ItemID.None)
            return;
        Main.NewText(Language.GetTextValue(
            "Mods.MoreAIPlayer.CompanionCommand.AutomationWeaponSelected",
            profile.Name, Lang.GetItemNameValue(item.type), assessment.DecisionScore,
            (int)System.MathF.Round(assessment.TheoreticalDps)), 132, 225, 170);
    }

    private static string LocalizedEnum(string group, string value)
    {
        string key = $"Mods.MoreAIPlayer.CompanionCommand.{group}.{value}";
        string localized = Language.GetTextValue(key);
        return localized == key ? value : localized;
    }
}
