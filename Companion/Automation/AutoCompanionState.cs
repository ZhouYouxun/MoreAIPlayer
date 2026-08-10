using Microsoft.Xna.Framework;
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
    public const int CurrentVersion = 2;

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

    /// <summary>True only after the shelter controller revalidated the complete shell, background
    /// walls, entrance, light, comfort item, work surface and storage in the live world.</summary>
    public bool ShelterCompleted;

    public TagCompound Save()
    {
        TagCompound tag = new()
        {
            [nameof(Version)] = Version,
            [nameof(Running)] = Running,
            [nameof(LifeCrystals)] = LifeCrystals,
            [nameof(LifeFruits)] = LifeFruits,
            [nameof(ManaCrystals)] = ManaCrystals,
            [nameof(ShelterCompleted)] = ShelterCompleted
        };
        SavePoint(tag, nameof(HomeAnchor), HomeAnchor);
        SavePoint(tag, nameof(WorkBench), WorkBench);
        SavePoint(tag, nameof(Furnace), Furnace);
        SavePoint(tag, nameof(Anvil), Anvil);
        return tag;
    }

    public static AutoCompanionState Load(TagCompound tag) => new()
    {
        // Versions 0/1 had no shelter flag. Missing fields already default safely; write the
        // current version on the next save instead of carrying a stale layout marker forever.
        Version = CurrentVersion,
        Running = !tag.ContainsKey(nameof(Running)) || tag.GetBool(nameof(Running)),
        LifeCrystals = tag.ContainsKey(nameof(LifeCrystals)) ? tag.GetInt(nameof(LifeCrystals)) : 0,
        LifeFruits = tag.ContainsKey(nameof(LifeFruits)) ? tag.GetInt(nameof(LifeFruits)) : 0,
        ManaCrystals = tag.ContainsKey(nameof(ManaCrystals)) ? tag.GetInt(nameof(ManaCrystals)) : 0,
        ShelterCompleted = tag.ContainsKey(nameof(ShelterCompleted)) && tag.GetBool(nameof(ShelterCompleted)),
        HomeAnchor = LoadPoint(tag, nameof(HomeAnchor)),
        WorkBench = LoadPoint(tag, nameof(WorkBench)),
        Furnace = LoadPoint(tag, nameof(Furnace)),
        Anvil = LoadPoint(tag, nameof(Anvil))
    };

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
}
