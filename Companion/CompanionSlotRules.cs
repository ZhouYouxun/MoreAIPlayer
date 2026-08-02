using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace MoreAIPlayer.Companion;

public static class CompanionSlotRules
{
    public static bool IsAllowedWeapon(Item item)
    {
        if (item.IsAir)
            return true;

        bool tool = item.pick > 0 || item.axe > 0 || item.hammer > 0;
        return item.damage > 0 || tool;
    }

    /// <summary>
    /// The guard slot accepts any weapon at all — a shotgun, a greatsword and a suicide-tome
    /// spellbook are all equally valid answers to being swarmed, and picking between them is
    /// the player's call, not the AI's. Only pure tools are refused: a pick or axe here would
    /// have the companion mining while something chews on it.
    /// </summary>
    public static bool IsGuardWeapon(Item item)
    {
        if (item.IsAir)
            return true;

        bool tool = item.pick > 0 || item.axe > 0 || item.hammer > 0;
        return item.damage > 0 && !tool;
    }

    /// <summary>
    /// The summon slot takes minion and sentry staffs and nothing else. Whips are rejected on
    /// purpose: they carry the summon damage class but are swung at enemies like any other
    /// weapon, so they belong in the ordinary rotation, not in slot upkeep.
    /// </summary>
    public static bool IsSummonWeapon(Item item) =>
        item.IsAir || CompanionWeaponBehaviors.IsSummonStaff(item);

    /// <summary>Bug nets only — the vanilla set covers the Bug Net, Lavaproof Bug Net and Golden
    /// Bug Net, plus any modded catching tool that registers itself the same way.</summary>
    public static bool IsCatchingTool(Item item) =>
        item.IsAir || (ItemID.Sets.CatchingTool.Length > item.type && ItemID.Sets.CatchingTool[item.type]);

    public static bool IsFishingPole(Item item) => item.IsAir || item.fishingPole > 0;

    public static bool IsGrapplingHook(Item item) =>
        item.IsAir || (item.shoot > ProjectileID.None &&
            item.shoot < Main.projHook.Length && Main.projHook[item.shoot]);

    public static bool IsMount(Item item) => item.IsAir || item.mountType != MountID.None;

    /// <summary>
    /// Pickaxes only. Axes and hammers are deliberately excluded even though vanilla lets one
    /// item be several tools at once: the mining rules are tightly scoped to breaking blocks the
    /// companion was told to break, and a hammer or axe in this slot would widen that to walls
    /// and trees, which is exactly the "it digs wherever it likes" failure being avoided.
    /// </summary>
    public static bool IsPickaxe(Item item) =>
        item.IsAir || (item.pick > 0 && item.axe <= 0 && item.hammer <= 0);

    /// <summary>
    /// How many of the profile's accessory slots the current world actually grants. Companions
    /// get these for free — no Demon Heart, no Master Ninja Gear equivalent to consume:
    ///
    /// <list type="bullet">
    /// <item>Normal: 5, and Wall of Flesh does not change that.</item>
    /// <item>Expert: 5, rising to 6 once hardmode starts.</item>
    /// <item>Master: 6, rising to 7 once hardmode starts.</item>
    /// </list>
    ///
    /// Master worlds also report expertMode, so master is tested first.
    /// </summary>
    public static int UnlockedAccessorySlots()
    {
        if (Main.masterMode)
            return Main.hardMode ? 7 : 6;
        if (Main.expertMode)
            return Main.hardMode ? 6 : 5;
        return 5;
    }

    public static bool IsHeadArmor(Item item) => item.IsAir || item.headSlot >= 0;

    public static bool IsBodyArmor(Item item) => item.IsAir || item.bodySlot >= 0;

    public static bool IsLegArmor(Item item) => item.IsAir || item.legSlot >= 0;

    public static bool IsAccessory(Item item) => item.IsAir || item.accessory;

    public static bool IsDye(Item item) => item.IsAir || item.dye > 0;

    public static bool IsAmmo(Item item) => item.IsAir || item.ammo > AmmoID.None;

    public static bool AcceptAnything(Item item) => true;
}
