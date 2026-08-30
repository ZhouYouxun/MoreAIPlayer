using Terraria;
using Terraria.ID;

namespace MoreAIPlayer.Companion.Automation;

/// <summary>Vanilla birth-to-Moon-Lord milestone provider. World flags decide completion, so a
/// save/reload, external boss kill, or repeated death can never move the campaign backwards.</summary>
public static class AutoCampaignController
{
    private static AutoCampaignStage lastIndexedStage = AutoCampaignStage.Foundation;

    public static AutoCampaignStage DetermineStage(AutoContext ctx)
    {
        AutoCampaignStage worldStage = DetermineWorldStage(ctx.State.PreferredClass);

        // A pristine world still gets the ordinary personal bootstrap before the Eye. Once the
        // world itself has advanced, never lie about that fact merely because this independent
        // player arrived with weak gear: the parallel planner can pursue foundation capabilities
        // and the real current-world campaign opportunities at the same time.
        if (worldStage == AutoCampaignStage.EyeOfCthulhu && NeedsPersonalFoundation(ctx))
            return AutoCampaignStage.Foundation;
        return worldStage;
    }

    public static bool NeedsPersonalFoundation(AutoContext ctx) =>
        !ctx.State.ShelterCompleted || !AutoGearController.HasBasicMetalLoadout(ctx.Profile);

    /// <summary>An imported world's completion flag is only a world fact. This independent player
    /// is finished after it has proved the same durable capabilities expected at the end of a
    /// campaign; entering somebody else's completed save with an empty inventory is not a win.</summary>
    public static bool CanCompleteAutomation(AutoContext ctx) =>
        NPC.downedMoonlord && AutoEncounterReadiness.TerminalWorldCapabilityReady(ctx);

    public static bool HasEstablishedWorldProgress() =>
        Main.hardMode || NPC.downedBoss1 || NPC.downedBoss2 || NPC.downedBoss3 ||
        NPC.downedQueenBee || NPC.downedMechBossAny || NPC.downedPlantBoss ||
        NPC.downedGolemBoss || NPC.downedAncientCultist || NPC.downedMoonlord;

    internal static void ResetWorldSession()
    {
        lastIndexedStage = AutoCampaignStage.Foundation;
    }

    private static AutoCampaignStage DetermineWorldStage(AutoCombatClass preferredClass)
    {
        if (!NPC.downedBoss1)
            return AutoCampaignStage.EyeOfCthulhu;
        if (!NPC.downedBoss2)
            return AutoCampaignStage.EvilBoss;
        if (!NPC.downedBoss3)
            return AutoCampaignStage.Skeletron;
        if (!Main.hardMode)
            return AutoCampaignStage.WallOfFlesh;
        if (!NPC.downedMechBoss1 || !NPC.downedMechBoss2 || !NPC.downedMechBoss3)
            return AutoCampaignStage.MechanicalBosses;
        if (!NPC.downedPlantBoss)
            return AutoCampaignStage.Plantera;
        if (!NPC.downedGolemBoss)
            return AutoCampaignStage.Golem;
        if (!NPC.downedAncientCultist)
            return AutoCampaignStage.LunaticCultist;

        AutoCampaignStage preferredPillar = preferredClass switch
        {
            AutoCombatClass.Ranged => AutoCampaignStage.VortexPillar,
            AutoCombatClass.Magic => AutoCampaignStage.NebulaPillar,
            AutoCombatClass.Summoner => AutoCampaignStage.StardustPillar,
            _ => AutoCampaignStage.SolarPillar
        };
        if (!PillarDown(preferredPillar))
            return preferredPillar;
        if (!NPC.downedTowerSolar)
            return AutoCampaignStage.SolarPillar;
        if (!NPC.downedTowerVortex)
            return AutoCampaignStage.VortexPillar;
        if (!NPC.downedTowerNebula)
            return AutoCampaignStage.NebulaPillar;
        if (!NPC.downedTowerStardust)
            return AutoCampaignStage.StardustPillar;
        return NPC.downedMoonlord ? AutoCampaignStage.Completed : AutoCampaignStage.MoonLord;
    }

    private static bool PillarDown(AutoCampaignStage stage) => stage switch
    {
        AutoCampaignStage.SolarPillar => NPC.downedTowerSolar,
        AutoCampaignStage.VortexPillar => NPC.downedTowerVortex,
        AutoCampaignStage.NebulaPillar => NPC.downedTowerNebula,
        AutoCampaignStage.StardustPillar => NPC.downedTowerStardust,
        _ => true
    };

    public static void RefreshStage(AutoContext ctx)
    {
        AutoCampaignStage stage = DetermineStage(ctx);
        if (stage == ctx.State.CampaignStage)
            return;
        ctx.State.CampaignStage = stage;
        ctx.State.StageFailureCount = 0;
        ctx.State.StrategyVariant = 0;
        ctx.State.ArenaCompleted = false;
        ctx.State.ArenaAnchor = null;
        ctx.State.ArenaStage = stage;
        ctx.TaskRuntime.ClearActiveTarget();
        if (stage != lastIndexedStage)
        {
            lastIndexedStage = stage;
            AutoWorldIndexSystem.RestartScan();
        }
    }

    public static int TargetNpcType(AutoCampaignStage stage)
    {
        return stage switch
        {
            AutoCampaignStage.EyeOfCthulhu => NPCID.EyeofCthulhu,
            AutoCampaignStage.EvilBoss => WorldGen.crimson ? NPCID.BrainofCthulhu : NPCID.EaterofWorldsHead,
            AutoCampaignStage.Skeletron => NPCID.SkeletronHead,
            AutoCampaignStage.WallOfFlesh => NPCID.WallofFlesh,
            AutoCampaignStage.MechanicalBosses when !NPC.downedMechBoss1 => NPCID.TheDestroyer,
            AutoCampaignStage.MechanicalBosses when !NPC.downedMechBoss2 => NPCID.Retinazer,
            AutoCampaignStage.MechanicalBosses => NPCID.SkeletronPrime,
            AutoCampaignStage.Plantera => NPCID.Plantera,
            AutoCampaignStage.Golem => NPCID.Golem,
            AutoCampaignStage.LunaticCultist => NPCID.CultistBoss,
            AutoCampaignStage.SolarPillar => NPCID.LunarTowerSolar,
            AutoCampaignStage.VortexPillar => NPCID.LunarTowerVortex,
            AutoCampaignStage.NebulaPillar => NPCID.LunarTowerNebula,
            AutoCampaignStage.StardustPillar => NPCID.LunarTowerStardust,
            AutoCampaignStage.MoonLord => NPCID.MoonLordCore,
            _ => NPCID.None
        };
    }

    public static bool IsCampaignTarget(NPC npc)
    {
        if (npc is null || !npc.active || npc.friendly || npc.life <= 0)
            return false;
        if (IsPillar(npc.type))
            return true;
        if (IsMoonLordPart(npc.type))
            return true;
        if (npc.dontTakeDamage)
            return false;
        if (npc.boss)
            return true;
        return false;
    }

    public static bool IsPillar(int npcType) => npcType is NPCID.LunarTowerSolar or
        NPCID.LunarTowerVortex or NPCID.LunarTowerNebula or NPCID.LunarTowerStardust;

    public static bool IsMoonLordPart(int npcType) => npcType is NPCID.MoonLordHead or
        NPCID.MoonLordHand or NPCID.MoonLordCore or NPCID.MoonLordFreeEye;

    public static bool HasLiveTarget()
    {
        foreach (NPC npc in Main.ActiveNPCs)
            if (IsCampaignTarget(npc))
                return true;
        return false;
    }

    public static int RequiredWeaponScore(AutoCampaignStage stage) => stage switch
    {
        AutoCampaignStage.EyeOfCthulhu => 30,
        AutoCampaignStage.EvilBoss => 38,
        AutoCampaignStage.Skeletron => 48,
        AutoCampaignStage.WallOfFlesh => 65,
        AutoCampaignStage.MechanicalBosses => 100,
        AutoCampaignStage.Plantera => 135,
        AutoCampaignStage.Golem => 155,
        AutoCampaignStage.LunaticCultist => 190,
        AutoCampaignStage.SolarPillar or AutoCampaignStage.VortexPillar or
            AutoCampaignStage.NebulaPillar or AutoCampaignStage.StardustPillar => 210,
        AutoCampaignStage.MoonLord => 240,
        AutoCampaignStage.Completed => 240,
        _ => 24
    };

    public static bool NeedsLoadoutWork(AutoContext ctx)
    {
        int required = RequiredWeaponScore(ctx.State.CampaignStage);
        int score = AutoWeaponEvaluator.BestUsableScore(ctx.Me, ctx.Profile);
        // Death raises the desired margin instead of silently accepting a weaker loadout. Upgrade
        // routes remain bounded, so an unlucky world still proceeds to a changed arena/strategy
        // rather than farming one prescribed drop forever.
        int safetyMargin = System.Math.Min(36, ctx.State.StageFailureCount * 6);
        return score < required + safetyMargin;
    }

    public static bool RequiresNight(AutoCampaignStage stage) => stage is
        AutoCampaignStage.EyeOfCthulhu or AutoCampaignStage.Skeletron or
        AutoCampaignStage.MechanicalBosses;

    public static AutoTaskResult Summon(AutoContext ctx)
    {
        AutoCampaignStage stage = ctx.State.CampaignStage;
        if (HasLiveTarget())
            return AutoTaskResult.Succeeded;

        AutoEncounterReadiness readiness = AutoEncounterReadiness.Evaluate(ctx);
        if (!readiness.ReadyExceptTiming)
            return AutoTaskResult.AlternativeRequired;
        if (!readiness.TimingReady)
            return AutoEncounterTriggerController.Prepare(ctx);
        if (AutoCombatPreparationController.TryUseNextOwnedBuff(ctx))
            return AutoTaskResult.Running;
        return AutoEncounterTriggerController.Start(ctx);
    }
}
