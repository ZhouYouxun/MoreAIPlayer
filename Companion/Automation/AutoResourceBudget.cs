using System.Collections.Generic;
using Terraria;
using Terraria.ID;

namespace MoreAIPlayer.Companion.Automation;

/// <summary>
/// One shared, conservative wood ledger for housing, the current boss arena, routine exploration,
/// emergency barricades and ordinary crafting. Keeping this calculation in one place prevents the
/// house, arena, storage and ammunition controllers from each spending the same nominal reserve.
/// Vanilla permanent town NPCs are forecast from the live world and vanilla spawn conditions;
/// travelling/skeleton merchants, the Old Man, pets and town slimes do not consume a private room.
/// </summary>
public static class AutoResourceBudget
{
    public const float SafetyMultiplier = 1.5f;
    private const int MinimumRooms = 4;
    private const int SpareRooms = 1;
    private const int ForecastCacheTicks = 300;
    private static long lastForecastTick = -ForecastCacheTicks;
    private static int cachedEligibleTownNpcCount = 1;

    private static readonly int[] PermanentHousingNpcTypes =
    [
        NPCID.Guide, NPCID.Merchant, NPCID.Nurse, NPCID.ArmsDealer, NPCID.Dryad,
        NPCID.Demolitionist, NPCID.Clothier, NPCID.GoblinTinkerer, NPCID.Wizard,
        NPCID.Mechanic, NPCID.SantaClaus, NPCID.Truffle, NPCID.Steampunker,
        NPCID.DyeTrader, NPCID.PartyGirl, NPCID.Cyborg, NPCID.Painter,
        NPCID.WitchDoctor, NPCID.Pirate, NPCID.Stylist, NPCID.Angler,
        NPCID.TaxCollector, NPCID.DD2Bartender, NPCID.Golfer, NPCID.BestiaryGirl,
        NPCID.Princess
    ];

    public readonly record struct WoodBudget(
        int EligibleTownNpcs,
        int HousingRooms,
        int HousingWood,
        int ArenaWood,
        int ExplorationWood,
        int DefenseWood,
        int MaintenanceWood,
        int RawWoodTarget);

    public static WoodBudget Evaluate(AutoContext ctx)
    {
        AutoCompanionState state = ctx.State;
        int eligible = ForecastEligibleTownNpcCount();
        int rooms = System.Math.Max(MinimumRooms, eligible + SpareRooms);
        int completedRooms = System.Math.Clamp(state?.HousingRoomsCompleted ?? 0, 0, rooms);
        int housing = System.Math.Max(0,
            EstimateHousingWood(rooms) - EstimateHousingWood(completedRooms));

        int arenaPlatforms = state is null || state.ArenaCompleted && state.ArenaStage == state.CampaignStage
            ? 0 : AutoArenaController.RequiredPlatformCount(state);
        int heldPlatforms = AutoInventory.Count(ctx.Profile, ItemID.WoodPlatform);
        int campfires = state is null || state.ArenaCompleted && state.ArenaStage == state.CampaignStage
            ? 0 : AutoArenaController.RequiredCampfireCount(state);
        int heldCampfires = AutoInventory.Count(ctx.Profile, ItemID.Campfire);
        int arena = (System.Math.Max(0, arenaPlatforms - heldPlatforms) + 1) / 2 +
            System.Math.Max(0, campfires - heldCampfires) * 12;

        (int exploration, int defense, int maintenance) = OperationalNeeds(state?.CampaignStage ??
            AutoCampaignStage.Foundation);
        int rawTarget = ApplySafety(housing + arena + exploration + defense + maintenance);
        return new WoodBudget(eligible, rooms, housing, arena, exploration, defense, maintenance,
            System.Math.Max(50, rawTarget));
    }

    public static int RequiredHousingRooms() =>
        System.Math.Max(MinimumRooms, ForecastEligibleTownNpcCount() + SpareRooms);

    /// <summary>Reserve which discretionary wood consumers may never cross.</summary>
    public static int OperationalReserve(AutoCompanionState state)
    {
        (int exploration, int defense, int maintenance) = OperationalNeeds(state?.CampaignStage ??
            AutoCampaignStage.Foundation);
        return ApplySafety(exploration + defense + maintenance);
    }

    /// <summary>Conservative chest keep-stock without assuming crafted platforms are still in the
    /// backpack. This deliberately keeps enough raw wood to rebuild after arena or housing damage.</summary>
    public static int StorageWoodKeep(AutoCompanionState state)
    {
        int rooms = RequiredHousingRooms();
        int completed = System.Math.Clamp(state?.HousingRoomsCompleted ?? 0, 0, rooms);
        int housing = System.Math.Max(0,
            EstimateHousingWood(rooms) - EstimateHousingWood(completed));
        int arena = state is null || state.ArenaCompleted && state.ArenaStage == state.CampaignStage
            ? 0 : (AutoArenaController.RequiredPlatformCount(state) + 1) / 2 +
                AutoArenaController.RequiredCampfireCount(state) * 12;
        return System.Math.Max(50, ApplySafety(housing + arena) + OperationalReserve(state));
    }

    private static int ApplySafety(int amount) =>
        (int)System.Math.Ceiling(System.Math.Max(0, amount) * SafetyMultiplier);

    /// <summary>Wood-equivalent of the exact aerial-room shape: shared shell, walls converted at
    /// four per wood, platform columns converted at two per wood, furniture and a repair allowance.</summary>
    private static int EstimateHousingWood(int rooms)
    {
        if (rooms <= 0)
            return 0;
        int width = 11 * rooms + 1;
        int shell = width * 2 + 5 + rooms * 2;
        int backgroundWalls = (rooms * 50 + 3) / 4;
        int supportColumns = (rooms + 1) * 9;
        int furniture = rooms * 20;
        const int sharedStorageToolsAndLights = 16;
        return shell + backgroundWalls + supportColumns + furniture +
            sharedStorageToolsAndLights;
    }

    private static (int Exploration, int Defense, int Maintenance) OperationalNeeds(
        AutoCampaignStage stage)
    {
        if (stage <= AutoCampaignStage.WallOfFlesh)
            return (72, 64, 36);
        if (stage <= AutoCampaignStage.Golem)
            return (128, 96, 52);
        return (180, 128, 72);
    }

    public static int ForecastEligibleTownNpcCount()
    {
        long now = (long)Main.GameUpdateCount;
        if (now >= lastForecastTick && now - lastForecastTick < ForecastCacheTicks)
            return cachedEligibleTownNpcCount;

        HashSet<int> eligible = [];
        foreach (NPC npc in Main.ActiveNPCs)
            if (npc.active && npc.townNPC && IsPermanentHousingNpc(npc.type))
                eligible.Add(npc.type);

        Add(eligible, NPCID.Guide, true);
        Add(eligible, NPCID.Merchant, NPC.SpawnAllowed_Merchant());
        bool merchantAvailable = eligible.Contains(NPCID.Merchant);
        Add(eligible, NPCID.Nurse, merchantAvailable && NPC.SpawnAllowed_Nurse());
        Add(eligible, NPCID.ArmsDealer, NPC.SpawnAllowed_ArmsDealer());
        Add(eligible, NPCID.Dryad, NPC.downedBoss1 || NPC.downedBoss2 || NPC.downedBoss3);
        Add(eligible, NPCID.Demolitionist, merchantAvailable && NPC.SpawnAllowed_Demolitionist());
        Add(eligible, NPCID.Clothier, NPC.downedBoss3);
        Add(eligible, NPCID.GoblinTinkerer, NPC.savedGoblin);
        Add(eligible, NPCID.Wizard, NPC.savedWizard);
        Add(eligible, NPCID.Mechanic, NPC.savedMech);
        Add(eligible, NPCID.SantaClaus, NPC.downedFrost && Main.xMas);
        Add(eligible, NPCID.Truffle, Main.hardMode);
        Add(eligible, NPCID.Steampunker, NPC.downedMechBossAny);
        Add(eligible, NPCID.WitchDoctor, NPC.downedQueenBee);
        Add(eligible, NPCID.Pirate, NPC.downedPirates);
        Add(eligible, NPCID.Cyborg, Main.hardMode && NPC.downedPlantBoss);
        Add(eligible, NPCID.Stylist, NPC.savedStylist);
        Add(eligible, NPCID.Angler, NPC.savedAngler);
        Add(eligible, NPCID.TaxCollector, NPC.savedTaxCollector);
        Add(eligible, NPCID.DD2Bartender, NPC.savedBartender);
        Add(eligible, NPCID.Golfer, NPC.savedGolfer);
        Add(eligible, NPCID.BestiaryGirl,
            Main.GetBestiaryProgressReport().CompletionPercent >= 0.1f);

        // Reuse the live vanilla eligibility array as an additional exact signal. It is refreshed
        // by Terraria's own town-spawn pass and covers event/unlock details without counting mods.
        foreach (int type in PermanentHousingNpcTypes)
            if (type >= 0 && type < Main.townNPCCanSpawn.Length && Main.townNPCCanSpawn[type])
                eligible.Add(type);

        Add(eligible, NPCID.DyeTrader, eligible.Count >= 4 && NPC.SpawnAllowed_DyeTrader());
        Add(eligible, NPCID.Painter, eligible.Count >= 8);
        Add(eligible, NPCID.PartyGirl, NPC.unlockedPartyGirlSpawn || eligible.Count >= 14);
        Add(eligible, NPCID.Princess, NPC.unlockedPrincessSpawn);
        lastForecastTick = now;
        cachedEligibleTownNpcCount = eligible.Count;
        return cachedEligibleTownNpcCount;
    }

    private static void Add(HashSet<int> set, int type, bool condition)
    {
        if (condition)
            set.Add(type);
    }

    private static bool IsPermanentHousingNpc(int type)
    {
        foreach (int candidate in PermanentHousingNpcTypes)
            if (type == candidate)
                return true;
        return false;
    }
}
