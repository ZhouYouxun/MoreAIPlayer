using Terraria.ModLoader.IO;

namespace MoreAIPlayer.Companion.Automation;

/// <summary>
/// Records, per character, which profile is the automation companion for one specific world and
/// whether that world's run has already finished. The table is keyed by the world's unique id, so
/// the same character gets a fresh from-scratch automation companion in every world (no carrying a
/// Moon Lord loadout into a brand-new world to skip the whole flow), and re-entering an old world
/// reuses the profile it built there. A copied world file with a new unique id is naturally treated
/// as a new world because its key no longer matches.
/// </summary>
public sealed class AutoWorldBinding
{
    /// <summary>Profile id bound to this world's automation companion.</summary>
    public int ProfileId;

    /// <summary>True once Moon Lord has been defeated here. The profile remains automation-owned,
    /// and the automation book must never create a second automation companion for this world.</summary>
    public bool Completed;

    public TagCompound Save() => new()
    {
        [nameof(ProfileId)] = ProfileId,
        [nameof(Completed)] = Completed
    };

    public static AutoWorldBinding Load(TagCompound tag) => new()
    {
        ProfileId = tag.GetInt(nameof(ProfileId)),
        Completed = tag.ContainsKey(nameof(Completed)) && tag.GetBool(nameof(Completed))
    };
}
