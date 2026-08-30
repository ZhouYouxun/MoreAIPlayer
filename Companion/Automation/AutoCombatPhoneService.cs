using Terraria;
using Terraria.ID;

namespace MoreAIPlayer.Companion.Automation;

/// <summary>Intrinsic Cell Phone services for the automation identity. It does not occupy an item
/// slot: perfect-position/depth knowledge is supplied by the world index, home recall is owned by
/// recovery, and this service supplies the DPS-meter/weapon-comparison part.</summary>
public static class AutoCombatPhoneService
{
    private const int InventorySlots = 58;
    private const int SwitchMarginPercent = 108;

    public static int SelectBestWeapon(AutoContext ctx, NPC target,
        out AutoWeaponAssessment selected)
    {
        Player me = ctx.Me;
        int bestSlot = -1;
        AutoWeaponAssessment best = default;
        for (int slot = 0; slot < me.inventory.Length && slot < InventorySlots; slot++)
        {
            if (!TryAssess(me, me.inventory[slot], target, ctx.State.CampaignStage,
                out AutoWeaponAssessment assessment))
                continue;
            if (assessment.DecisionScore <= best.DecisionScore)
                continue;
            best = assessment;
            bestSlot = slot;
        }

        int currentSlot = FindItem(me, ctx.State.LastSelectedWeaponItemType);
        if (currentSlot >= 0 && TryAssess(me, me.inventory[currentSlot], target,
            ctx.State.CampaignStage, out AutoWeaponAssessment current))
        {
            if (current.DecisionScore > 0 &&
                best.DecisionScore * 100 < current.DecisionScore * SwitchMarginPercent)
            {
                best = current;
                bestSlot = currentSlot;
            }
        }

        selected = best;
        if (bestSlot < 0)
            return -1;
        if (ctx.State.LastSelectedWeaponItemType != best.ItemType)
        {
            ctx.State.LastSelectedWeaponItemType = best.ItemType;
            ctx.State.LastSelectedWeaponScore = best.DecisionScore;
            ctx.State.LastSelectedWeaponReason = best.ReasonCode;
            AutoProgressReporter.ReportWeapon(ctx.Profile, me.inventory[bestSlot], best);
        }
        else
        {
            ctx.State.LastSelectedWeaponScore = best.DecisionScore;
            ctx.State.LastSelectedWeaponReason = best.ReasonCode;
        }
        return bestSlot;
    }

    public static AutoWeaponAssessment MeasureTheoreticalDps(Player user, Item weapon,
        AutoCampaignStage stage) => TryAssess(user, weapon, null, stage, out var assessment)
            ? assessment : default;

    private static bool TryAssess(Player user, Item weapon, NPC target, AutoCampaignStage stage,
        out AutoWeaponAssessment assessment)
    {
        try
        {
            assessment = AutoWeaponDecisionEngine.Assess(user, weapon, target, stage);
            if (assessment.DecisionScore > 0)
            {
                // Unknown custom AI remains eligible, but an uncertain paper-DPS estimate must
                // clear a slightly larger margin before replacing a proven vanilla weapon.
                int confidenceFactor = 60 + assessment.Confidence * 2 / 5;
                assessment = assessment with
                {
                    DecisionScore = System.Math.Max(1,
                        assessment.DecisionScore * confidenceFactor / 100)
                };
            }
            return true;
        }
        catch (System.Exception exception)
        {
            // A mod weapon with a throwing projectile/stat hook loses only this comparison.
            // Vanilla candidates and the rest of the automation tick remain available.
            CompanionFaultGuard.Report(CompanionFaultGuard.AutomationWeaponEvaluationLane,
                exception);
            assessment = default;
            return false;
        }
    }

    private static int FindItem(Player me, int itemType)
    {
        if (itemType <= ItemID.None)
            return -1;
        for (int slot = 0; slot < me.inventory.Length && slot < InventorySlots; slot++)
            if (me.inventory[slot].type == itemType && CompanionSlotRules.IsRegularWeapon(me.inventory[slot]))
                return slot;
        return -1;
    }
}
