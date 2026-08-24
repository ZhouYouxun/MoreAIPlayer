using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ModLoader.IO;

namespace MoreAIPlayer.Companion.Automation;

/// <summary>
/// The persistent automation state carried by an <see cref="CompanionProfile"/> while it is the
/// world's one automation companion. It is intentionally a separate object from the profile's
/// cosmetic/inventory data so that finishing the run (defeating Moon Lord) can drop this whole
/// state and leave a perfectly ordinary companion profile behind.
///
/// Phase 1 only needs the identity marker (<see cref="Running"/>) and a data version so later
/// phases can migrate saved planner state without ever wiping the profile's gear. The goal graph,
/// world-knowledge cursor, home/chest coordinates and capability progress described in the design
/// document are added in later phases; when they are, a failed version migration must rebuild the
/// task/scan caches and keep every item.
/// </summary>
public sealed class AutoCompanionState
{
    /// <summary>Bumped whenever the saved automation layout changes shape.</summary>
    public const int CurrentVersion = 11;

    /// <summary>The version this state was written with; used for forward migration on load.</summary>
    public int Version = CurrentVersion;

    /// <summary>
    /// True while the automation flow is active. Cleared (not deleted) when the companion finishes
    /// the run and converts into a normal phantom companion, so <see cref="CompanionProfile.IsAutoRunning"/>
    /// reads false and every normal-companion system stops filtering it out.
    /// </summary>
    public bool Running = true;

    // ---- Independent vitals (§1.2, §4.3) ----
    // The automation companion grows its own maximum life/mana from crystals and fruit it collects,
    // instead of mirroring the owner's — otherwise an endgame owner would hand a brand-new
    // automation companion a 500 HP head start and trivialise early progression. Phase 1 leaves
    // these at zero (the empty brain collects nothing yet); later phases increment them when the
    // companion actually consumes a crystal/fruit. After the run converts, they are dropped and the
    // profile goes back to the owner-shared rule.

    /// <summary>Life Crystals the companion has consumed (0–15). Each adds 20 max life up to 400.</summary>
    public int LifeCrystals;

    /// <summary>Life Fruit the companion has consumed (0–20). Each adds 5 max life, 400→500.</summary>
    public int LifeFruits;

    /// <summary>Mana Crystals the companion has consumed (0–9). Each adds 20 max mana up to 200.</summary>
    public int ManaCrystals;

    private const int LifeCrystalCap = 15;
    private const int LifeFruitCap = 20;
    private const int ManaCrystalCap = 9;
    private const int BaseLife = 100;
    private const int BaseMana = 20;
    private const int MaxLifeHardCap = 500;
    private const int MaxManaHardCap = 200;

    /// <summary>The companion's own maximum life, mirroring vanilla's crystal-then-fruit rule.</summary>
    public int MaxLife()
    {
        int crystals = System.Math.Clamp(LifeCrystals, 0, LifeCrystalCap);
        int life = BaseLife + crystals * 20;               // ≤ 400
        if (crystals >= LifeCrystalCap)                    // fruit only counts once crystals are maxed
            life += System.Math.Clamp(LifeFruits, 0, LifeFruitCap) * 5;
        return System.Math.Min(MaxLifeHardCap, life);
    }

    /// <summary>The companion's own maximum mana.</summary>
    public int MaxMana() =>
        System.Math.Min(MaxManaHardCap, BaseMana + System.Math.Clamp(ManaCrystals, 0, ManaCrystalCap) * 20);

    // ---- Home / crafting-station anchors (§4.3) ----
    // Tile coordinates the companion has built and relies on. Null until built. Revalidated against
    // the real world before use, so a stale coordinate never authorises operating on a tile that a
    // player or another mod has since removed.

    /// <summary>The companion's chosen home/crafting area anchor tile.</summary>
    public Point? HomeAnchor;

    /// <summary>Placed Work Bench tile (left tile of the object).</summary>
    public Point? WorkBench;

    /// <summary>Placed Furnace tile.</summary>
    public Point? Furnace;

    /// <summary>Placed Anvil tile.</summary>
    public Point? Anvil;

    public Point? Hellforge;
    public Point? HardmodeAnvil;
    public Point? HardmodeForge;
    public Point? AncientManipulator;

    /// <summary>Top-left tile of the companion's own storage chest (design §4.4.1). Null until built;
    /// revalidated against the live chest array before use.</summary>
    public Point? Chest;

    /// <summary>Cross-session boss-defeat tally (design §4.1). Persisted so repeated wipes across a
    /// save/reload still escalate the boss recovery ladder. Boss combat itself is a later phase; this
    /// carries the counter now so the runtime never loses it.</summary>
    public int BossFailureCount;

    /// <summary>True only after the shelter controller revalidated the complete shell, background
    /// walls, entrance, light, comfort item, work surface and storage in the live world.</summary>
    public bool ShelterCompleted;

    // ---- Full campaign / recovery memory ----
    public AutoCampaignStage CampaignStage = AutoCampaignStage.Foundation;
    public AutoCombatClass PreferredClass = AutoCombatClass.Flexible;
    public int StrategyVariant;
    public int StageFailureCount;
    public int AltarsBroken;
    public Point? PendingAltar;
    public bool FirstEvilOrbBroken;
    public Point? PendingEvilOrb;
    public int LastSelectedWeaponItemType;
    public int LastSelectedWeaponScore;
    public string LastSelectedWeaponReason = string.Empty;
    public AutoWeaponRouteKind LastWeaponRoute = AutoWeaponRouteKind.None;
    public bool HasDeathReturnTarget;
    public Vector2 DeathReturnTarget;
    public int DeathReturnAttempts;
    public Point? ArenaAnchor;
    public AutoCampaignStage ArenaStage = AutoCampaignStage.Foundation;
    public bool ArenaCompleted;
    public int LastReportedProgressSerial;
    public string LastReportedStep = string.Empty;

    // ---- Recall / save memory ----
    // The live synthetic Player and its brain are deliberately disposable. These fields are the
    // compact, save-safe part of the run that must survive a book recall, character save, reload,
    // or synthetic-slot replacement. Large scan/path caches are rebuilt from the live world.

    public bool HasMemoryPosition;
    public Vector2 MemoryPosition;
    public AutoGoalId MemoryGoal = AutoGoalId.Idle;
    public AutoGoalId MemoryStep = AutoGoalId.Idle;
    public long MemoryPlanKey;
    public int MemoryAttemptCount;
    public int MemoryRecoveryLevel;
    public AutoBlockReason MemoryFailureReason = AutoBlockReason.None;
    public Point? MemoryTarget;
    public bool HasMemoryVitals;
    public int MemoryLife;
    public int MemoryMana;
    public bool MemoryWasDead;
    public int MemoryReviveTicks;

    /// <summary>Surface-scouting cursor. Segments count real completed sweeps and therefore provide
    /// a task metric that is more meaningful than raw left/right movement.</summary>
    public int ExplorationDirection = 1;
    public int ExplorationTargetTileX = -1;
    public int ExplorationSegments;
    public int FishingAttemptsThisSegment;
    public int FishingSegmentMarker = -1;

    private const int ReviewedChestCapacity = 256;
    private readonly List<Point> reviewedChests = [];
    public int ReviewedChestCount => reviewedChests.Count;

    public bool WasChestReviewed(Point chest)
    {
        foreach (Point known in reviewedChests)
            if (known == chest)
                return true;
        return false;
    }

    public void MarkChestReviewed(Point chest)
    {
        if (WasChestReviewed(chest))
            return;
        if (reviewedChests.Count >= ReviewedChestCapacity)
            reviewedChests.RemoveAt(0);
        reviewedChests.Add(chest);
    }

    public void CaptureMemory(AutoTaskRuntime runtime, Player player, int reviveTicksLeft)
    {
        if (player is not null && float.IsFinite(player.Center.X) && float.IsFinite(player.Center.Y))
        {
            HasMemoryPosition = true;
            MemoryPosition = player.Center;
            HasMemoryVitals = true;
            MemoryLife = player.statLife;
            MemoryMana = player.statMana;
            MemoryWasDead = player.dead || player.statLife <= 0;
            MemoryReviveTicks = System.Math.Max(0, reviveTicksLeft);
        }

        if (runtime is null)
            return;
        MemoryGoal = runtime.CurrentGoal;
        MemoryStep = runtime.CurrentStep;
        MemoryPlanKey = runtime.CurrentPlanKey;
        MemoryAttemptCount = runtime.AttemptCount;
        MemoryRecoveryLevel = runtime.RecoveryLevel;
        MemoryFailureReason = runtime.LastFailureReason;
        MemoryTarget = runtime.ActiveTargetTile;
    }

    public void RestoreVitals(Player player, CompanionInstance instance)
    {
        if (!HasMemoryVitals || player is null || instance is null)
            return;
        player.statMana = System.Math.Clamp(MemoryMana, 0, System.Math.Max(0, player.statManaMax));
        if (MemoryWasDead || MemoryLife <= 0)
        {
            player.statLife = 0;
            player.dead = true;
            instance.ReviveTicksLeft = System.Math.Max(0, MemoryReviveTicks);
        }
        else
        {
            player.statLife = System.Math.Clamp(MemoryLife, 1, System.Math.Max(1, player.statLifeMax));
            player.dead = false;
            instance.ReviveTicksLeft = 0;
        }
    }

    public void RestoreMemory(AutoTaskRuntime runtime)
    {
        if (runtime is null)
            return;
        runtime.Reset();
        runtime.CurrentGoal = MemoryGoal;
        runtime.CurrentStep = MemoryStep;
        runtime.CurrentPlanKey = MemoryPlanKey;
        runtime.AttemptCount = System.Math.Max(0, MemoryAttemptCount);
        runtime.RecoveryLevel = System.Math.Max(0, MemoryRecoveryLevel);
        runtime.RememberRecovery(MemoryPlanKey, runtime.RecoveryLevel);
        runtime.LastFailureReason = MemoryFailureReason;
        if (HasMemoryPosition)
        {
            runtime.LastKnownPosition = MemoryPosition;
            runtime.WindowStartPosition = MemoryPosition;
        }
        if (MemoryTarget is Point target)
            runtime.SetActiveTarget(MemoryGoal, target);
    }

    public bool TryGetResumePosition(out Vector2 position)
    {
        position = MemoryPosition;
        if (!HasMemoryPosition || !float.IsFinite(position.X) || !float.IsFinite(position.Y))
            return false;
        Point tile = position.ToTileCoordinates();
        return WorldGen.InWorld(tile.X, tile.Y, 10);
    }

    public TagCompound Save()
    {
        TagCompound tag = new()
        {
            [nameof(Version)] = Version,
            [nameof(Running)] = Running,
            [nameof(LifeCrystals)] = LifeCrystals,
            [nameof(LifeFruits)] = LifeFruits,
            [nameof(ManaCrystals)] = ManaCrystals,
            [nameof(BossFailureCount)] = BossFailureCount,
            [nameof(ShelterCompleted)] = ShelterCompleted,
            [nameof(CampaignStage)] = (int)CampaignStage,
            [nameof(PreferredClass)] = (int)PreferredClass,
            [nameof(StrategyVariant)] = StrategyVariant,
            [nameof(StageFailureCount)] = StageFailureCount,
            [nameof(AltarsBroken)] = AltarsBroken,
            [nameof(FirstEvilOrbBroken)] = FirstEvilOrbBroken,
            [nameof(LastSelectedWeaponItemType)] = LastSelectedWeaponItemType,
            [nameof(LastSelectedWeaponScore)] = LastSelectedWeaponScore,
            [nameof(LastSelectedWeaponReason)] = LastSelectedWeaponReason ?? string.Empty,
            [nameof(LastWeaponRoute)] = (int)LastWeaponRoute,
            [nameof(HasDeathReturnTarget)] = HasDeathReturnTarget,
            [nameof(DeathReturnTarget) + "X"] = DeathReturnTarget.X,
            [nameof(DeathReturnTarget) + "Y"] = DeathReturnTarget.Y,
            [nameof(DeathReturnAttempts)] = DeathReturnAttempts,
            [nameof(ArenaStage)] = (int)ArenaStage,
            [nameof(ArenaCompleted)] = ArenaCompleted,
            [nameof(LastReportedProgressSerial)] = LastReportedProgressSerial,
            [nameof(LastReportedStep)] = LastReportedStep ?? string.Empty,
            [nameof(HasMemoryPosition)] = HasMemoryPosition,
            [nameof(MemoryPosition) + "X"] = MemoryPosition.X,
            [nameof(MemoryPosition) + "Y"] = MemoryPosition.Y,
            [nameof(MemoryGoal)] = (int)MemoryGoal,
            [nameof(MemoryStep)] = (int)MemoryStep,
            [nameof(MemoryPlanKey)] = MemoryPlanKey,
            [nameof(MemoryAttemptCount)] = MemoryAttemptCount,
            [nameof(MemoryRecoveryLevel)] = MemoryRecoveryLevel,
            [nameof(MemoryFailureReason)] = (int)MemoryFailureReason,
            [nameof(HasMemoryVitals)] = HasMemoryVitals,
            [nameof(MemoryLife)] = MemoryLife,
            [nameof(MemoryMana)] = MemoryMana,
            [nameof(MemoryWasDead)] = MemoryWasDead,
            [nameof(MemoryReviveTicks)] = MemoryReviveTicks,
            [nameof(ExplorationDirection)] = ExplorationDirection,
            [nameof(ExplorationTargetTileX)] = ExplorationTargetTileX,
            [nameof(ExplorationSegments)] = ExplorationSegments,
            [nameof(FishingAttemptsThisSegment)] = FishingAttemptsThisSegment,
            [nameof(FishingSegmentMarker)] = FishingSegmentMarker
        };
        SavePoint(tag, nameof(HomeAnchor), HomeAnchor);
        SavePoint(tag, nameof(WorkBench), WorkBench);
        SavePoint(tag, nameof(Furnace), Furnace);
        SavePoint(tag, nameof(Anvil), Anvil);
        SavePoint(tag, nameof(Hellforge), Hellforge);
        SavePoint(tag, nameof(HardmodeAnvil), HardmodeAnvil);
        SavePoint(tag, nameof(HardmodeForge), HardmodeForge);
        SavePoint(tag, nameof(AncientManipulator), AncientManipulator);
        SavePoint(tag, nameof(Chest), Chest);
        SavePoint(tag, nameof(ArenaAnchor), ArenaAnchor);
        SavePoint(tag, nameof(PendingAltar), PendingAltar);
        SavePoint(tag, nameof(PendingEvilOrb), PendingEvilOrb);
        SavePoint(tag, nameof(MemoryTarget), MemoryTarget);
        List<TagCompound> savedChests = new(reviewedChests.Count);
        foreach (Point chest in reviewedChests)
            savedChests.Add(new TagCompound { ["X"] = chest.X, ["Y"] = chest.Y });
        tag["ReviewedChests"] = savedChests;
        return tag;
    }

    public static AutoCompanionState Load(TagCompound tag)
    {
        AutoCompanionState state = new()
        {
            // Older versions had no recall memory. Missing fields default safely; write the
            // current version on the next save instead of carrying a stale layout marker forever.
            Version = CurrentVersion,
            Running = !tag.ContainsKey(nameof(Running)) || tag.GetBool(nameof(Running)),
            LifeCrystals = tag.ContainsKey(nameof(LifeCrystals)) ? tag.GetInt(nameof(LifeCrystals)) : 0,
            LifeFruits = tag.ContainsKey(nameof(LifeFruits)) ? tag.GetInt(nameof(LifeFruits)) : 0,
            ManaCrystals = tag.ContainsKey(nameof(ManaCrystals)) ? tag.GetInt(nameof(ManaCrystals)) : 0,
            BossFailureCount = tag.ContainsKey(nameof(BossFailureCount)) ? tag.GetInt(nameof(BossFailureCount)) : 0,
            ShelterCompleted = tag.ContainsKey(nameof(ShelterCompleted)) && tag.GetBool(nameof(ShelterCompleted)),
            CampaignStage = ReadEnum(tag, nameof(CampaignStage), AutoCampaignStage.Foundation),
            PreferredClass = ReadEnum(tag, nameof(PreferredClass), AutoCombatClass.Flexible),
            StrategyVariant = tag.ContainsKey(nameof(StrategyVariant)) ? System.Math.Max(0, tag.GetInt(nameof(StrategyVariant))) : 0,
            StageFailureCount = tag.ContainsKey(nameof(StageFailureCount)) ? System.Math.Max(0, tag.GetInt(nameof(StageFailureCount))) : 0,
            AltarsBroken = tag.ContainsKey(nameof(AltarsBroken)) ? System.Math.Max(0, tag.GetInt(nameof(AltarsBroken))) : 0,
            PendingAltar = LoadPoint(tag, nameof(PendingAltar)),
            FirstEvilOrbBroken = tag.ContainsKey(nameof(FirstEvilOrbBroken)) && tag.GetBool(nameof(FirstEvilOrbBroken)),
            PendingEvilOrb = LoadPoint(tag, nameof(PendingEvilOrb)),
            LastSelectedWeaponItemType = tag.ContainsKey(nameof(LastSelectedWeaponItemType)) ? tag.GetInt(nameof(LastSelectedWeaponItemType)) : 0,
            LastSelectedWeaponScore = tag.ContainsKey(nameof(LastSelectedWeaponScore)) ? System.Math.Max(0, tag.GetInt(nameof(LastSelectedWeaponScore))) : 0,
            LastSelectedWeaponReason = tag.ContainsKey(nameof(LastSelectedWeaponReason)) ? tag.GetString(nameof(LastSelectedWeaponReason)) : string.Empty,
            LastWeaponRoute = ReadEnum(tag, nameof(LastWeaponRoute), AutoWeaponRouteKind.None),
            HasDeathReturnTarget = tag.ContainsKey(nameof(HasDeathReturnTarget)) && tag.GetBool(nameof(HasDeathReturnTarget)),
            DeathReturnTarget = new Vector2(
                tag.ContainsKey(nameof(DeathReturnTarget) + "X") ? tag.GetFloat(nameof(DeathReturnTarget) + "X") : 0f,
                tag.ContainsKey(nameof(DeathReturnTarget) + "Y") ? tag.GetFloat(nameof(DeathReturnTarget) + "Y") : 0f),
            DeathReturnAttempts = tag.ContainsKey(nameof(DeathReturnAttempts)) ? System.Math.Max(0, tag.GetInt(nameof(DeathReturnAttempts))) : 0,
            ArenaAnchor = LoadPoint(tag, nameof(ArenaAnchor)),
            ArenaStage = ReadEnum(tag, nameof(ArenaStage), AutoCampaignStage.Foundation),
            ArenaCompleted = tag.ContainsKey(nameof(ArenaCompleted)) && tag.GetBool(nameof(ArenaCompleted)),
            LastReportedProgressSerial = tag.ContainsKey(nameof(LastReportedProgressSerial)) ? System.Math.Max(0, tag.GetInt(nameof(LastReportedProgressSerial))) : 0,
            LastReportedStep = tag.ContainsKey(nameof(LastReportedStep)) ? tag.GetString(nameof(LastReportedStep)) : string.Empty,
            HomeAnchor = LoadPoint(tag, nameof(HomeAnchor)),
            WorkBench = LoadPoint(tag, nameof(WorkBench)),
            Furnace = LoadPoint(tag, nameof(Furnace)),
            Anvil = LoadPoint(tag, nameof(Anvil)),
            Hellforge = LoadPoint(tag, nameof(Hellforge)),
            HardmodeAnvil = LoadPoint(tag, nameof(HardmodeAnvil)),
            HardmodeForge = LoadPoint(tag, nameof(HardmodeForge)),
            AncientManipulator = LoadPoint(tag, nameof(AncientManipulator)),
            Chest = LoadPoint(tag, nameof(Chest)),
            HasMemoryPosition = tag.ContainsKey(nameof(HasMemoryPosition)) && tag.GetBool(nameof(HasMemoryPosition)),
            MemoryPosition = new Vector2(
                tag.ContainsKey(nameof(MemoryPosition) + "X") ? tag.GetFloat(nameof(MemoryPosition) + "X") : 0f,
                tag.ContainsKey(nameof(MemoryPosition) + "Y") ? tag.GetFloat(nameof(MemoryPosition) + "Y") : 0f),
            MemoryGoal = ReadEnum(tag, nameof(MemoryGoal), AutoGoalId.Idle),
            MemoryStep = ReadEnum(tag, nameof(MemoryStep), AutoGoalId.Idle),
            MemoryPlanKey = tag.ContainsKey(nameof(MemoryPlanKey)) ? tag.GetLong(nameof(MemoryPlanKey)) : 0L,
            MemoryAttemptCount = tag.ContainsKey(nameof(MemoryAttemptCount)) ? tag.GetInt(nameof(MemoryAttemptCount)) : 0,
            MemoryRecoveryLevel = tag.ContainsKey(nameof(MemoryRecoveryLevel)) ? tag.GetInt(nameof(MemoryRecoveryLevel)) : 0,
            MemoryFailureReason = ReadEnum(tag, nameof(MemoryFailureReason), AutoBlockReason.None),
            MemoryTarget = LoadPoint(tag, nameof(MemoryTarget)),
            HasMemoryVitals = tag.ContainsKey(nameof(HasMemoryVitals)) && tag.GetBool(nameof(HasMemoryVitals)),
            MemoryLife = tag.ContainsKey(nameof(MemoryLife)) ? tag.GetInt(nameof(MemoryLife)) : 0,
            MemoryMana = tag.ContainsKey(nameof(MemoryMana)) ? tag.GetInt(nameof(MemoryMana)) : 0,
            MemoryWasDead = tag.ContainsKey(nameof(MemoryWasDead)) && tag.GetBool(nameof(MemoryWasDead)),
            MemoryReviveTicks = tag.ContainsKey(nameof(MemoryReviveTicks)) ? tag.GetInt(nameof(MemoryReviveTicks)) : 0,
            ExplorationDirection = tag.ContainsKey(nameof(ExplorationDirection)) && tag.GetInt(nameof(ExplorationDirection)) < 0 ? -1 : 1,
            ExplorationTargetTileX = tag.ContainsKey(nameof(ExplorationTargetTileX)) ? tag.GetInt(nameof(ExplorationTargetTileX)) : -1,
            ExplorationSegments = tag.ContainsKey(nameof(ExplorationSegments)) ? System.Math.Max(0, tag.GetInt(nameof(ExplorationSegments))) : 0,
            FishingAttemptsThisSegment = tag.ContainsKey(nameof(FishingAttemptsThisSegment)) ? System.Math.Max(0, tag.GetInt(nameof(FishingAttemptsThisSegment))) : 0,
            FishingSegmentMarker = tag.ContainsKey(nameof(FishingSegmentMarker)) ? tag.GetInt(nameof(FishingSegmentMarker)) : -1
        };
        if (tag.ContainsKey("ReviewedChests"))
        {
            foreach (TagCompound saved in tag.GetList<TagCompound>("ReviewedChests"))
            {
                if (state.reviewedChests.Count >= ReviewedChestCapacity)
                    break;
                state.reviewedChests.Add(new Point(saved.GetInt("X"), saved.GetInt("Y")));
            }
        }
        return state;
    }

    private static void SavePoint(TagCompound tag, string key, Point? point)
    {
        if (point is Point p)
        {
            tag[key + "X"] = p.X;
            tag[key + "Y"] = p.Y;
        }
    }

    private static Point? LoadPoint(TagCompound tag, string key) =>
        tag.ContainsKey(key + "X") && tag.ContainsKey(key + "Y")
            ? new Point(tag.GetInt(key + "X"), tag.GetInt(key + "Y"))
            : null;

    private static T ReadEnum<T>(TagCompound tag, string key, T fallback) where T : struct, System.Enum
    {
        if (!tag.ContainsKey(key))
            return fallback;
        int value = tag.GetInt(key);
        return System.Enum.IsDefined(typeof(T), value) ? (T)(object)value : fallback;
    }
}
