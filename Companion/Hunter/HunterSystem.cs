using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.Localization;
using Terraria.ModLoader;

namespace MoreAIPlayer.Companion.Hunter;

/// <summary>Owns the bounded Hunter Trial lifecycle and its virtual-player hunters.</summary>
public sealed class HunterSystem : ModSystem
{
    private const int InitialApproachDelayTicks = 210;
    private const int ReinforcementDelayTicks = 105;
    private const float MinimumRemoteSpawnDistance = 2400f;
    private const float MaximumRemoteSpawnDistance = 3000f;
    private const float MinimumRecoverySpawnDistance = 720f;
    private const float MaximumRecoverySpawnDistance = 1040f;
    private const float MinimumBreakoutDistance = 2000f;
    private const float MaximumBreakoutDistance = 2800f;
    private const float BreakoutArrivalRadius = 88f;

    public const int SurvivalDurationTicks = 10800;
    public const int BreakoutDurationTicks = 9000;

    public const float CatchUpTeleportDistance = 5600f;

    private static readonly Dictionary<int, HunterInstance> BySlot = [];
    private static bool previousHostileState;
    private static bool previousHostileStateValid;
    private static int pendingSpawnCount;
    private static int nextSpawnIndex;
    private static int nextSpawnDelay;
    private static int objectiveTicksLeft;
    private static int objectiveTicksElapsed;
    private static int defeatedHunters;
    private static int eliminationTarget;
    private static Vector2 breakoutDestination;
    private static bool hasBreakoutDestination;

    public static bool HunterModeActive { get; private set; }
    public static int TargetHunterCount { get; private set; } = 1;
    public static HunterTrialType SelectedTrial { get; private set; } = HunterTrialType.Survival;
    public static HunterTrialType ActiveTrial { get; private set; }
    public static HunterGameStage ActiveStage { get; private set; }
    public static bool IsPreparing => HunterModeActive && pendingSpawnCount > 0;
    public static int PreparationSeconds => IsPreparing ? (int)System.Math.Ceiling(nextSpawnDelay / 60f) : 0;
    public static int ObjectiveSecondsRemaining => System.Math.Max(0, (int)System.Math.Ceiling(objectiveTicksLeft / 60f));
    public static int ObjectiveSecondsElapsed => System.Math.Max(0, objectiveTicksElapsed / 60);
    public static int DefeatedHunters => defeatedHunters;
    public static int EliminationTarget => HunterModeActive && eliminationTarget > 0
        ? eliminationTarget
        : CalculateEliminationTarget(TargetHunterCount);
    public static bool HuntersMayRest => ActiveTrial != HunterTrialType.Breakout;

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

        if (HunterModeActive)
        {
            EndGame("HunterTrialAbandoned", new Color(180, 210, 180));
            return;
        }

        if (!TryGetEligibleStage(out HunterGameStage stage, out string failureKey))
        {
            Main.NewText(Text(failureKey), 255, 170, 110);
            return;
        }

        Player localPlayer = Main.LocalPlayer;
        if (localPlayer is null || !localPlayer.active || localPlayer.dead || localPlayer.statLife <= 0)
        {
            Main.NewText(Text("HunterCannotStartDead"), 255, 170, 110);
            return;
        }

        HunterModeActive = true;
        ActiveStage = stage;
        RememberHostileState();
        InitializeTrial(localPlayer);
        QueueNewHunt();
        AnnounceTrialStart(stage);
    }

    public static void SetHunterCount(int count)
    {
        if (Main.netMode != NetmodeID.SinglePlayer)
        {
            ResetModeAndClear();
            return;
        }

        int clamped = System.Math.Clamp(count, 1, 3);
        if (TargetHunterCount == clamped)
            return;

        TargetHunterCount = clamped;
        if (HunterModeActive)
            RestartHunt();
    }

    public static void SetTrialType(HunterTrialType trial)
    {
        if (Main.netMode != NetmodeID.SinglePlayer)
        {
            ResetModeAndClear();
            return;
        }

        if (trial is HunterTrialType.None || SelectedTrial == trial)
            return;

        SelectedTrial = trial;
        if (HunterModeActive)
            RestartHunt();
    }

    /// <summary>Dismisses the current wave and gives the player the same distant warning as a
    /// fresh game. Used by the configuration panel after count/loadout changes.</summary>
    public static void RestartHunt()
    {
        if (!HunterModeActive)
        {
            ToggleMode();
            return;
        }

        ClearHunters(restoreHostileState: false);
        InitializeTrial(Main.LocalPlayer);
        QueueNewHunt();
        Main.NewText(Text("HunterTrialRestarting", Text(TrialKey(ActiveTrial))), 255, 200, 100);
    }

    public static bool TryGetBreakoutDestination(out Vector2 destination)
    {
        destination = breakoutDestination;
        return HunterModeActive && ActiveTrial == HunterTrialType.Breakout && hasBreakoutDestination;
    }

    /// <summary>Breakout hunters do not merely tail the player. They run ahead along the route
    /// and spread across it, turning the destination into an actual interception problem.</summary>
    public static Vector2 GetPursuitAnchor(HunterInstance hunter, Player target)
    {
        if (target is null || hunter is null || !TryGetBreakoutDestination(out Vector2 destination))
            return target?.Center ?? Vector2.Zero;

        Vector2 route = destination - target.Center;
        float routeLength = route.Length();
        if (routeLength < 160f)
            return target.Center;

        Vector2 forward = route / routeLength;
        Vector2 perpendicular = new(-forward.Y, forward.X);
        float lead = MathHelper.Clamp(routeLength * 0.28f, 180f, 520f);
        float centerIndex = (TargetHunterCount - 1) * 0.5f;
        float laneOffset = (hunter.HuntIndex - centerIndex) * 150f;
        return target.Center + forward * lead + perpendicular * laneOffset;
    }

    public static bool TryGetNearestHunter(Player target, out HunterInstance nearest, out float distance)
    {
        nearest = null;
        distance = 0f;
        if (target is null)
            return false;

        float bestDistanceSquared = float.MaxValue;
        foreach (HunterInstance hunter in BySlot.Values)
        {
            if (hunter.Entity is null || !hunter.Entity.active)
                continue;

            float candidate = Vector2.DistanceSquared(target.Center, hunter.Entity.Center);
            if (candidate >= bestDistanceSquared)
                continue;

            bestDistanceSquared = candidate;
            nearest = hunter;
        }

        if (nearest is null)
            return false;

        distance = System.MathF.Sqrt(bestDistanceSquared);
        return true;
    }

    public static bool SpawnHunter(Player targetPlayer, int index)
    {
        if (Main.netMode != NetmodeID.SinglePlayer || ActiveStage == HunterGameStage.None)
            return false;

        int slot = FindFreeSlot();
        if (slot < 0)
            return false;

        Player original = Main.player[slot];
        Player entity = new() { whoAmI = slot };
        Main.player[slot] = entity;

        string[] nameKeys = ["HunterNameAlpha", "HunterNameBravo", "HunterNameCharlie"];
        string hunterName = index >= 0 && index < nameKeys.Length
            ? Text(nameKeys[index])
            : Text("HunterNameFallback", index + 1);

        HunterInstance instance = new()
        {
            Slot = slot,
            HuntIndex = index,
            Entity = entity,
            OriginalPlayer = original
        };
        BySlot[slot] = instance;
        try
        {
            instance.BindLoadout(targetPlayer, hunterName, ActiveStage, TargetHunterCount);
            entity.Center = FindRemoteSpawnPosition(targetPlayer, entity);
            entity.velocity = Vector2.Zero;
            entity.fallStart = (int)(entity.position.Y / 16f);
        }
        catch (Exception exception)
        {
            // Spawning replaces one Main.player slot before the loadout and terrain search run.
            // Roll that reservation back atomically if either step faults so an invisible hunter
            // can never remain in the player array.
            BySlot.Remove(slot);
            entity.active = false;
            if (ReferenceEquals(Main.player[slot], entity))
                Main.player[slot] = original;
            CompanionFaultGuard.Report($"hunter spawn #{slot}", exception);
            return false;
        }

        CompanionEntitySystem.SpawnRecallDustBurst(entity.Center);
        Main.NewText(Text("HunterSpawned", entity.name), 255, 100, 100);
        return true;
    }

    public static Vector2 FindRemoteSpawnPosition(Player targetPlayer, Player hunter = null) =>
        FindSafeSpawnPosition(targetPlayer, hunter, MinimumRemoteSpawnDistance, MaximumRemoteSpawnDistance);

    public static Vector2 FindRecoverySpawnPosition(Player targetPlayer, Player hunter) =>
        FindSafeSpawnPosition(targetPlayer, hunter, MinimumRecoverySpawnDistance, MaximumRecoverySpawnDistance);

    private static Vector2 FindSafeSpawnPosition(Player targetPlayer, Player hunter, float minimumDistance, float maximumDistance)
    {
        int width = System.Math.Max(20, hunter?.width ?? 20);
        int height = System.Math.Max(42, hunter?.height ?? 42);
        int preferredDirection = Main.rand.NextBool() ? -1 : 1;
        const float worldMargin = 240f;
        float worldRight = Main.maxTilesX * 16f - worldMargin;
        bool hasRoomLeft = targetPlayer.Center.X - minimumDistance >= worldMargin;
        bool hasRoomRight = targetPlayer.Center.X + minimumDistance <= worldRight;
        if (!hasRoomLeft && hasRoomRight)
            preferredDirection = 1;
        else if (hasRoomLeft && !hasRoomRight)
            preferredDirection = -1;
        int targetTileY = System.Math.Clamp((int)(targetPlayer.Center.Y / 16f), 20, Main.maxTilesY - 20);

        // Probe several columns on both sides. At each column descend from slightly above the
        // target's elevation until a real, hazard-free standing surface is found.
        for (int attempt = 0; attempt < 24; attempt++)
        {
            int direction = attempt % 4 == 3 ? -preferredDirection : preferredDirection;
            float distance = Main.rand.NextFloat(minimumDistance, maximumDistance);
            float worldX = MathHelper.Clamp(
                targetPlayer.Center.X + direction * distance,
                worldMargin,
                worldRight);
            if (System.MathF.Abs(worldX - targetPlayer.Center.X) < minimumDistance * 0.8f)
                continue;
            int tileX = System.Math.Clamp((int)(worldX / 16f), 20, Main.maxTilesX - 20);
            int firstFloorRow = System.Math.Max(20, targetTileY - 24);
            int lastFloorRow = System.Math.Min(Main.maxTilesY - 20, targetTileY + 96);

            for (int floorRow = firstFloorRow; floorRow <= lastFloorRow; floorRow++)
            {
                Vector2 center = new(tileX * 16f + 8f, floorRow * 16f - height * 0.5f);
                if (IsSafeLanding(center, width, height))
                    return center;
            }
        }

        // A heavily modified world may not have a usable floor in the search band. Keep the
        // promised horizontal separation, but at least choose open, dry, non-hurting space so
        // the hunter can fall onto terrain instead of materialising inside it.
        float fallbackDirection = preferredDirection;
        float fallbackX = MathHelper.Clamp(
            targetPlayer.Center.X + fallbackDirection * minimumDistance,
            worldMargin,
            worldRight);
        for (int offset = 0; offset <= 120; offset++)
        {
            int signedOffset = offset == 0 ? 0 : (offset % 2 == 0 ? offset / 2 : -(offset + 1) / 2);
            float y = MathHelper.Clamp(targetPlayer.Center.Y + signedOffset * 16f, 240f, Main.maxTilesY * 16f - 240f);
            Vector2 center = new(fallbackX, y);
            if (IsSafeOpenSpace(center, width, height))
                return center;
        }

        return new Vector2(
            fallbackX,
            MathHelper.Clamp(targetPlayer.Center.Y - 160f, 240f, Main.maxTilesY * 16f - 240f));
    }

    private static bool IsSafeLanding(Vector2 center, int width, int height)
    {
        if (!IsSafeOpenSpace(center, width, height))
            return false;

        Vector2 topLeft = center - new Vector2(width * 0.5f, height * 0.5f);
        return Collision.SolidCollision(topLeft + new Vector2(0f, 4f), width, height, acceptTopSurfaces: true);
    }

    private static bool IsSafeOpenSpace(Vector2 center, int width, int height)
    {
        Vector2 topLeft = center - new Vector2(width * 0.5f, height * 0.5f);
        return !Collision.SolidCollision(topLeft, width, height) &&
               !Collision.WetCollision(topLeft, width, height) &&
               !Collision.AnyHurtingTiles(topLeft, width, height);
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

    public static void ClearAllHunters() => ClearHunters(restoreHostileState: true);

    private static void ClearHunters(bool restoreHostileState)
    {
        if (Main.player is not null)
        {
            foreach ((int slot, HunterInstance instance) in BySlot)
            {
                try
                {
                    CompanionEntitySystem.KillOwnedProjectiles(slot);
                }
                catch (Exception exception)
                {
                    // One modded projectile must not prevent the remaining hunters or the
                    // local player's hostile flag from being restored.
                    CompanionFaultGuard.Report($"hunter cleanup #{slot}", exception);
                }
                finally
                {
                    HaltHunterInput(instance.Entity);
                    instance.Entity.active = false;
                    if (slot >= 0 && slot < Main.player.Length && Main.player[slot] == instance.Entity)
                        Main.player[slot] = instance.OriginalPlayer;
                }
            }

            if (restoreHostileState && Main.netMode == NetmodeID.SinglePlayer && Main.LocalPlayer is not null && previousHostileStateValid)
                Main.LocalPlayer.hostile = previousHostileState;
        }
        BySlot.Clear();
        if (restoreHostileState)
            previousHostileStateValid = false;
    }

    private static void QueueNewHunt()
    {
        pendingSpawnCount = TargetHunterCount;
        nextSpawnIndex = 0;
        nextSpawnDelay = InitialApproachDelayTicks;
    }

    private static void InitializeTrial(Player localPlayer)
    {
        ActiveTrial = SelectedTrial;
        objectiveTicksElapsed = 0;
        defeatedHunters = 0;
        eliminationTarget = CalculateEliminationTarget(TargetHunterCount);
        objectiveTicksLeft = ActiveTrial switch
        {
            HunterTrialType.Survival => SurvivalDurationTicks,
            HunterTrialType.Breakout => BreakoutDurationTicks,
            _ => 0
        };

        hasBreakoutDestination = ActiveTrial == HunterTrialType.Breakout && localPlayer is not null;
        breakoutDestination = hasBreakoutDestination
            ? FindSafeSpawnPosition(localPlayer, null, MinimumBreakoutDistance, MaximumBreakoutDistance)
            : Vector2.Zero;
    }

    private static int CalculateEliminationTarget(int hunterCount) =>
        3 + (System.Math.Clamp(hunterCount, 1, 3) - 1) * 2;

    private static void AnnounceTrialStart(HunterGameStage stage)
    {
        string stageName = Text(StageKey(stage));
        string message = ActiveTrial switch
        {
            HunterTrialType.Elimination => Text("HunterTrialStartElimination", eliminationTarget, stageName),
            HunterTrialType.Breakout => Text("HunterTrialStartBreakout", BreakoutDurationTicks / 60, stageName),
            _ => Text("HunterTrialStartSurvival", SurvivalDurationTicks / 60, stageName)
        };
        Main.NewText(message, 255, 80, 80);
    }

    /// <summary>Returns true when the current trial resolved and callers must stop touching the
    /// hunter collection for this tick.</summary>
    private static bool UpdateTrialObjective(Player localPlayer)
    {
        // The clock begins only after the whole squad has entered. The distant assembly warning
        // remains setup time, not hidden time taken away from the stated objective.
        if (pendingSpawnCount > 0 || BySlot.Count == 0)
            return false;

        objectiveTicksElapsed++;
        switch (ActiveTrial)
        {
            case HunterTrialType.Survival:
                objectiveTicksLeft--;
                if (objectiveTicksLeft <= 0)
                {
                    CompleteTrial();
                    return true;
                }
                break;

            case HunterTrialType.Elimination:
                if (defeatedHunters >= eliminationTarget)
                {
                    CompleteTrial();
                    return true;
                }
                break;

            case HunterTrialType.Breakout:
                objectiveTicksLeft--;
                if (hasBreakoutDestination &&
                    Vector2.DistanceSquared(localPlayer.Center, breakoutDestination) <= BreakoutArrivalRadius * BreakoutArrivalRadius)
                {
                    CompleteTrial();
                    return true;
                }
                if (objectiveTicksLeft <= 0)
                {
                    EndGame("HunterTrialFailedTimeout", new Color(255, 120, 100));
                    return true;
                }
                break;
        }

        return false;
    }

    private static void CompleteTrial()
    {
        int elapsedSeconds = System.Math.Max(1, (int)System.Math.Ceiling(objectiveTicksElapsed / 60f));
        switch (ActiveTrial)
        {
            case HunterTrialType.Elimination:
                EndGame("HunterTrialVictoryElimination", new Color(120, 255, 150), defeatedHunters, elapsedSeconds);
                break;
            case HunterTrialType.Breakout:
                EndGame("HunterTrialVictoryBreakout", new Color(120, 255, 150), elapsedSeconds, defeatedHunters);
                break;
            default:
                EndGame("HunterTrialVictorySurvival", new Color(120, 255, 150), SurvivalDurationTicks / 60, defeatedHunters);
                break;
        }
    }

    private static void UpdateScheduledSpawns(Player localPlayer)
    {
        if (pendingSpawnCount <= 0)
            return;

        if (nextSpawnDelay > 0)
        {
            nextSpawnDelay--;
            return;
        }

        if (SpawnHunter(localPlayer, nextSpawnIndex))
        {
            pendingSpawnCount--;
            nextSpawnIndex++;
            nextSpawnDelay = pendingSpawnCount > 0 ? ReinforcementDelayTicks : 0;
        }
        else
        {
            // A transient slot conflict must not silently cancel the game wave.
            nextSpawnDelay = ReinforcementDelayTicks;
        }
    }

    private static void RememberHostileState()
    {
        Player localPlayer = Main.LocalPlayer;
        if (localPlayer is not null && !previousHostileStateValid)
        {
            previousHostileState = localPlayer.hostile;
            previousHostileStateValid = true;
        }
    }

    private static bool TryGetEligibleStage(out HunterGameStage stage, out string failureKey)
    {
        stage = HunterGameStage.None;
        failureKey = string.Empty;

        if (NPC.downedPlantBoss)
        {
            failureKey = "HunterUnavailablePostPlantera";
            return false;
        }

        if (Main.hardMode)
        {
            stage = HunterGameStage.EarlyHardmode;
            return true;
        }

        if (NPC.downedSlimeKing || NPC.downedBoss1 || NPC.downedBoss2 || NPC.downedBoss3 ||
            NPC.downedQueenBee || NPC.downedDeerclops)
        {
            stage = HunterGameStage.PreHardmode;
            return true;
        }

        failureKey = "HunterRequiresBoss";
        return false;
    }

    public static bool TryGetCurrentEligibleStage(out HunterGameStage stage) =>
        TryGetEligibleStage(out stage, out _);

    public static string StageKey(HunterGameStage stage) => stage switch
    {
        HunterGameStage.PreHardmode => "HunterStagePreHardmode",
        HunterGameStage.EarlyHardmode => "HunterStageEarlyHardmode",
        _ => "HunterStageUnavailable"
    };

    public static string TrialKey(HunterTrialType trial) => trial switch
    {
        HunterTrialType.Elimination => "HunterTrialElimination",
        HunterTrialType.Breakout => "HunterTrialBreakout",
        _ => "HunterTrialSurvival"
    };

    private static string Text(string key, params object[] args) =>
        Language.GetTextValue($"Mods.MoreAIPlayer.CompanionCommand.{key}", args);

    private static void EndGame(string messageKey, Color color, params object[] args)
    {
        HunterModeActive = false;
        pendingSpawnCount = 0;
        nextSpawnDelay = 0;
        ActiveStage = HunterGameStage.None;
        ActiveTrial = HunterTrialType.None;
        objectiveTicksLeft = 0;
        objectiveTicksElapsed = 0;
        eliminationTarget = 0;
        hasBreakoutDestination = false;
        breakoutDestination = Vector2.Zero;
        ClearAllHunters();
        Main.NewText(Text(messageKey, args), color);
        defeatedHunters = 0;
    }

    private static void ResetModeAndClear()
    {
        HunterModeActive = false;
        pendingSpawnCount = 0;
        nextSpawnDelay = 0;
        ActiveStage = HunterGameStage.None;
        ActiveTrial = HunterTrialType.None;
        objectiveTicksLeft = 0;
        objectiveTicksElapsed = 0;
        defeatedHunters = 0;
        eliminationTarget = 0;
        hasBreakoutDestination = false;
        breakoutDestination = Vector2.Zero;
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

        if (!TryGetEligibleStage(out HunterGameStage stageNow, out _))
        {
            EndGame("HunterStageEndedPlantera", new Color(255, 190, 90));
            return;
        }

        if (stageNow != ActiveStage)
        {
            EndGame("HunterStageEndedHardmode", new Color(255, 190, 90));
            return;
        }

        Player localPlayer = Main.LocalPlayer;
        if (localPlayer is null || !localPlayer.active)
        {
            foreach (HunterInstance hunter in BySlot.Values)
            {
                hunter.WantsUseItem = false;
                hunter.WantsAltUse = false;
                hunter.ItemCheckPreparedFrame = ulong.MaxValue;
                HaltHunterInput(hunter.Entity);
            }
            return;
        }

        if (localPlayer.dead || localPlayer.statLife <= 0)
        {
            EndGame("HunterTrialFailedDeath", new Color(255, 120, 100));
            return;
        }

        RememberHostileState();
        localPlayer.hostile = true;
        UpdateScheduledSpawns(localPlayer);
        if (UpdateTrialObjective(localPlayer))
            return;

        List<int> conflictedSlots = null;
        List<int> faultedSlots = null;
        bool eliminationCompleted = false;
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
                    if (!instance.DefeatCounted)
                    {
                        instance.DefeatCounted = true;
                        defeatedHunters++;
                    }

                    if (ActiveTrial == HunterTrialType.Elimination && defeatedHunters >= eliminationTarget)
                    {
                        eliminationCompleted = true;
                        continue;
                    }

                    instance.ReviveTicksLeft = ActiveTrial == HunterTrialType.Elimination ? 900 : 1800;
                    if (ActiveTrial == HunterTrialType.Elimination)
                    {
                        Main.NewText(Text("HunterDefeatedProgress", me.name, defeatedHunters,
                            eliminationTarget, instance.ReviveTicksLeft / 60), 255, 240, 100);
                    }
                    else
                    {
                        Main.NewText(Text("HunterDefeated", me.name, instance.ReviveTicksLeft / 60), 255, 240, 100);
                    }
                }

                instance.ReviveTicksLeft--;
                if (instance.ReviveTicksLeft <= 0)
                {
                    instance.ReviveTicksLeft = 0;
                    instance.BindLoadout(localPlayer, me.name, ActiveStage, TargetHunterCount);
                    me.Center = FindRemoteSpawnPosition(localPlayer, me);
                    CompanionEntitySystem.SpawnRecallDustBurst(me.Center);
                    Main.NewText(Text("HunterRespawned", me.name), 255, 100, 100);
                }
                else
                {
                    me.dead = true;
                    me.statLife = 0;
                    me.velocity = Vector2.Zero;
                    me.respawnTimer = instance.ReviveTicksLeft + 60;
                    ResetCorpseGibs(me);
                }
                continue;
            }

            AdvanceHunterBuffTimers(instance, me);

            bool updated = CompanionFaultGuard.Run($"hunter update #{slot}", () =>
            {
                instance.Brain.Update(instance, localPlayer);
                instance.AimWorld = CompanionEntitySystem.SanitizeHunterAim(me, instance.AimWorld, instance);
                CompanionModBridge.FeedMouseState(me, instance.AimWorld, instance.WantsAltUse);
                instance.ItemCheckPreparedFrame = Main.GameUpdateCount;
            });

            if (updated)
            {
                instance.FaultStreak = 0;
                continue;
            }

            HaltHunterInput(me);
            instance.FaultStreak++;
            if (instance.FaultStreak >= HunterInstance.MaxFaultStreak)
                (faultedSlots ??= []).Add(slot);
        }

        if (eliminationCompleted)
        {
            CompleteTrial();
            return;
        }

        if (conflictedSlots is not null)
        {
            foreach (int slot in conflictedSlots)
                RetireHunter(slot, announceFault: false);
        }

        if (faultedSlots is not null)
        {
            foreach (int slot in faultedSlots)
                RetireHunter(slot, announceFault: true);
        }
    }

    /// <summary>
    /// Player.UpdateBuffs only decrements buff time for Main.myPlayer. Hunters deliberately do
    /// not occupy that global slot, so without this mirror poison, burning and every other timed
    /// debuff last forever. A newly applied expert-mode debuff also receives the same world
    /// multiplier that Player.AddBuff normally reserves for the local player.
    /// </summary>
    private static void AdvanceHunterBuffTimers(HunterInstance instance, Player hunter)
    {
        System.Array.Clear(instance.NextObservedBuffTypes, 0, instance.NextObservedBuffTypes.Length);
        System.Array.Clear(instance.NextObservedBuffTimes, 0, instance.NextObservedBuffTimes.Length);
        for (int i = 0; i < Player.MaxBuffs; i++)
        {
            int type = hunter.buffType[i];
            int time = hunter.buffTime[i];
            if (type <= 0 || time <= 0)
                continue;

            int previousTime = 0;
            for (int previousIndex = 0; previousIndex < Player.MaxBuffs; previousIndex++)
            {
                if (instance.ObservedBuffTypes[previousIndex] == type)
                {
                    previousTime = instance.ObservedBuffTimes[previousIndex];
                    break;
                }
            }
            bool newlyApplied = previousTime <= 0 || time > previousTime + 1;
            if (newlyApplied && Main.expertMode && BuffID.Sets.LongerExpertDebuff[type])
            {
                float multiplier = Main.GameModeInfo.DebuffTimeMultiplier;
                if (Main.GameModeInfo.IsJourneyMode)
                {
                    multiplier = Main.masterMode
                        ? Main.RegisteredGameModes[2].DebuffTimeMultiplier
                        : Main.RegisteredGameModes[1].DebuffTimeMultiplier;
                }
                time = System.Math.Max(time, (int)(time * multiplier));
            }

            if (!BuffID.Sets.TimeLeftDoesNotDecrease[type])
                time--;

            hunter.buffTime[i] = System.Math.Max(0, time);
            instance.NextObservedBuffTypes[i] = type;
            instance.NextObservedBuffTimes[i] = hunter.buffTime[i];
        }

        System.Array.Copy(instance.NextObservedBuffTypes, instance.ObservedBuffTypes, Player.MaxBuffs);
        System.Array.Copy(instance.NextObservedBuffTimes, instance.ObservedBuffTimes, Player.MaxBuffs);
    }

    private static void HaltHunterInput(Player player)
    {
        if (player is null)
            return;

        player.controlLeft = false;
        player.controlRight = false;
        player.controlUp = false;
        player.controlDown = false;
        player.controlJump = false;
        player.controlUseItem = false;
        player.controlUseTile = false;
        player.controlHook = false;
        player.channel = false;
        player.releaseUseItem = true;
        player.releaseUseTile = true;
    }

    private static void RetireHunter(int slot, bool announceFault)
    {
        if (!BySlot.Remove(slot, out HunterInstance instance))
            return;

        try
        {
            CompanionEntitySystem.KillOwnedProjectiles(slot);
        }
        catch (Exception exception)
        {
            CompanionFaultGuard.Report($"hunter cleanup #{slot}", exception);
        }
        finally
        {
            HaltHunterInput(instance.Entity);
            instance.Entity.active = false;
            if (Main.player is not null && slot >= 0 && slot < Main.player.Length &&
                ReferenceEquals(Main.player[slot], instance.Entity))
            {
                Main.player[slot] = instance.OriginalPlayer;
            }
        }

        if (announceFault)
            CompanionFaultGuard.AnnounceRetired("HunterDisabled");
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
