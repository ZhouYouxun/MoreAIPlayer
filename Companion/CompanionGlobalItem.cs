using Terraria;
using Terraria.ModLoader;

namespace MoreAIPlayer.Companion;

/// <summary>
/// Item-level rules for companion players. Currently just their ammo supply: a companion
/// never runs dry on a weapon it is holding.
/// </summary>
public sealed class CompanionGlobalItem : GlobalItem
{
    /// <summary>
    /// Lets a companion fire an ammo-consuming weapon with an empty inventory. Vanilla's
    /// PickAmmo already implements exactly the wanted fallback: when nothing suitable is
    /// found and the weapon does not need ammo, it shoots with ContentSamples item
    /// <c>weapon.useAmmo</c> and skips consumption entirely (its shootWithNoAmmo path).
    /// Because every AmmoID is the item id of that ammo class's most basic member, this
    /// yields musket balls for guns, wooden arrows for bows, Rocket I for launchers and gel
    /// for flamethrowers — infinitely, for free.
    ///
    /// This is only the fallback. ChooseAmmo scans the ammo slots and the rest of the
    /// inventory first, so any special ammo the player stocks is picked (and consumed)
    /// ahead of it, exactly like a human player.
    ///
    /// The automation companion is deliberately excluded: the design forbids free ammo for it
    /// (§1.3), so it needs and consumes real ammunition exactly like a human, and its planner is
    /// responsible for keeping ammo stocked.
    /// </summary>
    public override bool NeedsAmmo(Item item, Player player) =>
        !CompanionEntitySystem.IsCompanion(player) || CompanionEntitySystem.IsAutoCompanion(player);
}
