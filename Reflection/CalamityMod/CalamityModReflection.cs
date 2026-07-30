using Terraria.ModLoader;

namespace MoreAIPlayer.Reflection.CalamityMod;

/// <summary>
/// Optional Calamity lookup surface. No Calamity type is referenced at compile time: when the
/// player has not enabled Calamity, every caller simply receives false and uses normal behavior.
/// </summary>
internal static class CalamityModReflection
{
    internal const string ModName = "CalamityMod";

    internal static bool TryGetItemType(string internalName, out int type)
    {
        type = 0;
        return ModLoader.TryGetMod(ModName, out Mod calamity) &&
            calamity.TryFind<ModItem>(internalName, out ModItem item) &&
            (type = item.Type) > 0;
    }

    internal static bool TryGetProjectileType(string internalName, out int type)
    {
        type = 0;
        return ModLoader.TryGetMod(ModName, out Mod calamity) &&
            calamity.TryFind<ModProjectile>(internalName, out ModProjectile projectile) &&
            (type = projectile.Type) > 0;
    }
}
