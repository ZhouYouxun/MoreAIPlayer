using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.ModLoader.IO;
using Terraria.ObjectData;

namespace MoreAIPlayer.Companion.Automation;

/// <summary>
/// Persistent, fail-closed proof of which containers the automation companion may touch.
/// Terraria saves chest contents and coordinates, but not whether a player or world generation
/// placed the furniture. Consequently, only containers captured at the end of generation are
/// eligible for opportunity looting. A world first opened with an older mod version has no such
/// proof, so all of its pre-existing containers remain protected.
/// </summary>
public sealed class AutoChestProvenanceSystem : ModSystem
{
    private const int CurrentLedgerVersion = 1;
    private const int PlacementResolutionTicks = 6;
    private const int MaximumPendingPlacements = 64;
    private const string LedgerVersionKey = "AutoChestLedgerVersion";
    private const string LedgerInitializedKey = "AutoChestLedgerInitialized";
    private const string NaturalChestsKey = "AutoNaturalChests";
    private const string AutomationChestsKey = "AutoOwnedChests";

    private static readonly Dictionary<Point, int> NaturalChests = [];
    private static readonly Dictionary<Point, int> AutomationChests = [];
    private static readonly List<PendingPlacement> PendingPlacements = [];
    private static bool ledgerInitialized;
    private static bool generationCapturePending;

    private sealed class PendingPlacement
    {
        public Point Probe;
        public bool AutomationOwned;
        public int TicksRemaining;
    }

    public override void ClearWorld() => Reset();

    public override void OnWorldUnload() => Reset();

    public override void PostWorldGen()
    {
        // Make the ledger usable immediately, then capture again on the first world tick (and on
        // an early save) so chests added by systems whose PostWorldGen hook runs after ours are
        // included too.
        CaptureGeneratedChests();
        generationCapturePending = true;
    }

    public override void PostUpdateWorld()
    {
        if (generationCapturePending)
            CaptureGeneratedChests();
        ResolvePendingPlacements();
    }

    public override void SaveWorldData(TagCompound tag)
    {
        if (generationCapturePending)
            CaptureGeneratedChests();

        tag[LedgerVersionKey] = CurrentLedgerVersion;
        tag[LedgerInitializedKey] = ledgerInitialized;
        tag[NaturalChestsKey] = SaveEntries(NaturalChests);
        tag[AutomationChestsKey] = SaveEntries(AutomationChests);
    }

    public override void LoadWorldData(TagCompound tag)
    {
        NaturalChests.Clear();
        AutomationChests.Clear();
        PendingPlacements.Clear();
        generationCapturePending = false;

        // No marker means this is an old/foreign save. Guessing from tile style, chest name or
        // contents can misclassify player property, so unknown provenance stays protected.
        ledgerInitialized = tag.ContainsKey(LedgerVersionKey) &&
            tag.GetInt(LedgerVersionKey) >= CurrentLedgerVersion &&
            tag.ContainsKey(LedgerInitializedKey) && tag.GetBool(LedgerInitializedKey);
        if (ledgerInitialized && tag.ContainsKey(NaturalChestsKey))
            LoadEntries(tag.GetList<TagCompound>(NaturalChestsKey), NaturalChests);
        if (tag.ContainsKey(AutomationChestsKey))
            LoadEntries(tag.GetList<TagCompound>(AutomationChestsKey), AutomationChests);
    }

    public override void PostWorldLoad()
    {
        RemoveStaleEntries(NaturalChests);
        RemoveStaleEntries(AutomationChests);
    }

    /// <summary>True only for a still-live container whose world-generation identity was saved.</summary>
    public static bool IsWorldGeneratedChest(Chest chest) =>
        ledgerInitialized && HasMatchingLiveEntry(chest, NaturalChests);

    /// <summary>True only for storage furniture physically placed by the automation companion.</summary>
    public static bool IsAutomationOwnedChest(Chest chest) =>
        HasMatchingLiveEntry(chest, AutomationChests);

    internal static void RegisterPlacedContainer(int i, int j, bool automationOwned)
    {
        if (Main.gameMenu)
            return;
        Point probe = new(i, j);
        if (TryFindContainingChest(probe, out Chest chest, out Point anchor, out int tileType))
        {
            RegisterResolvedPlacement(anchor, tileType, automationOwned);
            return;
        }

        // Vanilla may allocate Main.chest after PlaceInWorld. Resolve the actual top-left anchor on
        // the following ticks; failure only withholds access and can never authorize a player chest.
        InvalidateEntriesNear(probe, 4);
        if (PendingPlacements.Count >= MaximumPendingPlacements)
            PendingPlacements.RemoveAt(0);
        PendingPlacements.Add(new PendingPlacement
        {
            Probe = probe,
            AutomationOwned = automationOwned,
            TicksRemaining = PlacementResolutionTicks
        });
    }

    internal static void InvalidateContainerAt(int i, int j)
    {
        Point probe = new(i, j);
        if (TryFindContainingChest(probe, out _, out Point anchor, out _))
        {
            NaturalChests.Remove(anchor);
            AutomationChests.Remove(anchor);
        }
        else
        {
            InvalidateEntriesNear(probe, 4);
        }
    }

    internal static void ClaimContainerAt(int i, int j)
    {
        // Once a human has opened even a naturally generated chest, it may contain their deposited
        // property. Permanently remove it from both automation access classes.
        InvalidateContainerAt(i, j);
    }

    private static void CaptureGeneratedChests()
    {
        NaturalChests.Clear();
        AutomationChests.Clear();
        PendingPlacements.Clear();
        if (Main.chest is not null)
        {
            foreach (Chest chest in Main.chest)
                if (TryReadLiveContainer(chest, out Point anchor, out int tileType))
                    NaturalChests[anchor] = tileType;
        }
        ledgerInitialized = true;
        generationCapturePending = false;
    }

    private static void ResolvePendingPlacements()
    {
        for (int index = PendingPlacements.Count - 1; index >= 0; index--)
        {
            PendingPlacement pending = PendingPlacements[index];
            if (TryFindContainingChest(pending.Probe, out _, out Point anchor, out int tileType))
            {
                RegisterResolvedPlacement(anchor, tileType, pending.AutomationOwned);
                PendingPlacements.RemoveAt(index);
                continue;
            }

            pending.TicksRemaining--;
            if (pending.TicksRemaining > 0)
                continue;
            InvalidateEntriesNear(pending.Probe, 4);
            PendingPlacements.RemoveAt(index);
        }
    }

    private static void RegisterResolvedPlacement(Point anchor, int tileType, bool automationOwned)
    {
        NaturalChests.Remove(anchor);
        if (automationOwned)
            AutomationChests[anchor] = tileType;
        else
            AutomationChests.Remove(anchor);
    }

    private static bool HasMatchingLiveEntry(Chest chest, Dictionary<Point, int> entries)
    {
        if (!TryReadLiveContainer(chest, out Point anchor, out int tileType) ||
            !entries.TryGetValue(anchor, out int recordedType))
            return false;
        return tileType == recordedType;
    }

    private static bool TryReadLiveContainer(Chest chest, out Point anchor, out int tileType)
    {
        anchor = default;
        tileType = -1;
        if (chest is null || !WorldGen.InWorld(chest.x, chest.y, 2))
            return false;
        Tile tile = Main.tile[chest.x, chest.y];
        if (!tile.HasTile || !IsContainerTile(tile.TileType))
            return false;
        anchor = new Point(chest.x, chest.y);
        tileType = tile.TileType;
        return true;
    }

    private static bool TryFindContainingChest(Point probe, out Chest found, out Point anchor,
        out int tileType)
    {
        found = null;
        anchor = default;
        tileType = -1;
        if (Main.chest is null)
            return false;

        foreach (Chest chest in Main.chest)
        {
            if (!TryReadLiveContainer(chest, out Point candidateAnchor, out int candidateType))
                continue;
            Tile tile = Main.tile[candidateAnchor.X, candidateAnchor.Y];
            TileObjectData data = TileObjectData.GetTileData(tile);
            int width = data?.Width ?? (candidateType == TileID.Dressers ? 3 : 2);
            int height = data?.Height ?? 2;
            if (probe.X < candidateAnchor.X || probe.X >= candidateAnchor.X + width ||
                probe.Y < candidateAnchor.Y || probe.Y >= candidateAnchor.Y + height)
                continue;
            found = chest;
            anchor = candidateAnchor;
            tileType = candidateType;
            return true;
        }
        return false;
    }

    private static bool IsContainerTile(int type) => type >= 0 &&
        type < TileID.Sets.IsAContainer.Length && TileID.Sets.IsAContainer[type];

    private static List<TagCompound> SaveEntries(Dictionary<Point, int> entries)
    {
        List<TagCompound> saved = [];
        foreach ((Point anchor, int tileType) in entries)
            saved.Add(new TagCompound
            {
                ["X"] = anchor.X,
                ["Y"] = anchor.Y,
                ["TileType"] = tileType
            });
        return saved;
    }

    private static void LoadEntries(IList<TagCompound> saved, Dictionary<Point, int> destination)
    {
        foreach (TagCompound entry in saved)
        {
            if (!entry.ContainsKey("X") || !entry.ContainsKey("Y") ||
                !entry.ContainsKey("TileType"))
                continue;
            Point anchor = new(entry.GetInt("X"), entry.GetInt("Y"));
            int tileType = entry.GetInt("TileType");
            if (WorldGen.InWorld(anchor.X, anchor.Y, 2) && IsContainerTile(tileType))
                destination[anchor] = tileType;
        }
    }

    private static void RemoveStaleEntries(Dictionary<Point, int> entries)
    {
        List<Point> stale = [];
        foreach ((Point anchor, int tileType) in entries)
        {
            int chestIndex = Chest.FindChest(anchor.X, anchor.Y);
            Chest chest = chestIndex >= 0 ? Main.chest[chestIndex] : null;
            if (!TryReadLiveContainer(chest, out _, out int liveType) || liveType != tileType)
                stale.Add(anchor);
        }
        foreach (Point anchor in stale)
            entries.Remove(anchor);
    }

    private static void InvalidateEntriesNear(Point probe, int radius)
    {
        InvalidateEntriesNear(NaturalChests, probe, radius);
        InvalidateEntriesNear(AutomationChests, probe, radius);
    }

    private static void InvalidateEntriesNear(Dictionary<Point, int> entries, Point probe, int radius)
    {
        List<Point> affected = [];
        foreach (Point anchor in entries.Keys)
            if (System.Math.Abs(anchor.X - probe.X) <= radius &&
                System.Math.Abs(anchor.Y - probe.Y) <= radius)
                affected.Add(anchor);
        foreach (Point anchor in affected)
            entries.Remove(anchor);
    }

    private static void Reset()
    {
        NaturalChests.Clear();
        AutomationChests.Clear();
        PendingPlacements.Clear();
        ledgerInitialized = false;
        generationCapturePending = false;
    }
}

/// <summary>Feeds real tile interaction facts into the fail-closed chest ledger.</summary>
public sealed class AutoChestProvenanceGlobalTile : GlobalTile
{
    public override void PlaceInWorld(int i, int j, int type, Item item)
    {
        if (!IsContainerTile(type))
            return;
        bool automationOwned = CompanionEntitySystem.IsAutoCompanion(Main.LocalPlayer);
        AutoChestProvenanceSystem.RegisterPlacedContainer(i, j, automationOwned);
    }

    public override void KillTile(int i, int j, int type, ref bool fail, ref bool effectOnly,
        ref bool noItem)
    {
        if (!fail && !effectOnly && IsContainerTile(type))
            AutoChestProvenanceSystem.InvalidateContainerAt(i, j);
    }

    public override void RightClick(int i, int j, int type)
    {
        if (IsContainerTile(type) && !CompanionEntitySystem.IsAutoCompanion(Main.LocalPlayer))
            AutoChestProvenanceSystem.ClaimContainerAt(i, j);
    }

    private static bool IsContainerTile(int type) => type >= 0 &&
        type < TileID.Sets.IsAContainer.Length && TileID.Sets.IsAContainer[type];
}
