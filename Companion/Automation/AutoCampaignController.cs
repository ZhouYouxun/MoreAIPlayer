using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;

namespace MoreAIPlayer.Companion.Automation;

/// <summary>Vanilla birth-to-Moon-Lord milestone provider. World flags decide completion, so a
/// save/reload, external boss kill, or repeated death can never move the campaign backwards.</summary>
public static class AutoCampaignController
{
    private static long nextSummonTick;
    private static AutoCampaignStage lastIndexedStage = AutoCampaignStage.Foundation;

    public static AutoCampaignStage DetermineStage(AutoContext ctx)
    {
        if (!ctx.State.ShelterCompleted || !AutoGearController.HasBasicMetalLoadout(ctx.Profile))
            return AutoCampaignStage.Foundation;
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

        AutoCampaignStage preferredPillar = ctx.State.PreferredClass switch
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
        _ => 24
    };

    public static bool NeedsLoadoutWork(AutoContext ctx)
    {
        int required = RequiredWeaponScore(ctx.State.CampaignStage);
        int score = AutoWeaponEvaluator.BestUsableScore(ctx.Me, ctx.Profile);
        // Every two wipes widen the tactical fallback. This never discards preparation entirely:
        // maximum life and the physical arena remain hard requirements.
        int tolerance = System.Math.Min(35, ctx.State.StageFailureCount * 8);
        return score < required - tolerance;
    }

    public static bool RequiresNight(AutoCampaignStage stage) => stage is
        AutoCampaignStage.EyeOfCthulhu or AutoCampaignStage.Skeletron or
        AutoCampaignStage.MechanicalBosses;

    public static AutoTaskResult Summon(AutoContext ctx)
    {
        AutoCampaignStage stage = ctx.State.CampaignStage;
        if (HasLiveTarget())
            return AutoTaskResult.Succeeded;
        if (RequiresNight(stage) && Main.dayTime)
            return AutoTaskResult.RetryLater;
        if ((long)Main.GameUpdateCount < nextSummonTick)
            return AutoTaskResult.Running;

        Point anchor = ctx.State.ArenaAnchor ?? ctx.Me.Center.ToTileCoordinates();
        Vector2 arena = new(anchor.X * 16f + 8f, anchor.Y * 16f + 8f);
        AutoTaskResult travel = AutoWorldTravelController.TravelTo(ctx, arena, 10f);
        if (travel != AutoTaskResult.Succeeded)
            return travel;
        if (AutoCombatPreparationController.TryUseNextOwnedBuff(ctx))
            return AutoTaskResult.Running;

        int npcType = TargetNpcType(stage);
        if (npcType <= NPCID.None)
            return stage == AutoCampaignStage.Completed
                ? AutoTaskResult.Succeeded : AutoTaskResult.AlternativeRequired;

        AutoTaskResult spawned = SpawnStageTarget(ctx, stage, npcType);
        nextSummonTick = (long)Main.GameUpdateCount + 600;
        return spawned;
    }

    /// <summary>Uses the vanilla special entry points for composite/event bosses. A generic
    /// SpawnOnPlayer call is insufficient for Wall of Flesh, the second Twin, and a missing lunar
    /// apocalypse, even though all three compile and may appear to create one visible NPC.</summary>
    private static AutoTaskResult SpawnStageTarget(AutoContext ctx, AutoCampaignStage stage,
        int npcType)
    {
        if (Main.netMode != NetmodeID.SinglePlayer)
            return AutoTaskResult.BlockedUnsupportedWorld;

        if (stage == AutoCampaignStage.WallOfFlesh)
        {
            NPC.SpawnWOF(ctx.Me.Center);
            return AutoTaskResult.Running;
        }

        if (stage == AutoCampaignStage.MechanicalBosses && !NPC.downedMechBoss2 &&
            npcType == NPCID.Retinazer)
        {
            NPC.SpawnOnPlayer(ctx.Me.whoAmI, NPCID.Retinazer);
            NPC.SpawnOnPlayer(ctx.Me.whoAmI, NPCID.Spazmatism);
            return AutoTaskResult.Running;
        }

        if (IsPillarStage(stage))
        {
            if (!NPC.LunarApocalypseIsUp)
            {
                WorldGen.TriggerLunarApocalypse();
                return AutoTaskResult.Running;
            }
            // Normally TriggerLunarApocalypse owns all four towers. Only the last watchdog rung
            // repairs a missing live tower whose downed flag is still false.
            if (ctx.TaskRuntime.RecoveryLevel < 7)
                return AutoTaskResult.Running;
        }

        NPC.SpawnOnPlayer(ctx.Me.whoAmI, npcType);
        return AutoTaskResult.Running;
    }

    private static bool IsPillarStage(AutoCampaignStage stage) => stage is
        AutoCampaignStage.SolarPillar or AutoCampaignStage.VortexPillar or
        AutoCampaignStage.NebulaPillar or AutoCampaignStage.StardustPillar;
}
