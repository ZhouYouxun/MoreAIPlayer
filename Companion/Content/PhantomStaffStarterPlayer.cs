using System.Collections.Generic;
using Terraria;
using Terraria.DataStructures;
using Terraria.ModLoader;
using Terraria.ModLoader.IO;

namespace MoreAIPlayer.Companion.Content;

/// <summary>New characters start with one staff; existing characters receive exactly one on the
/// first load after the feature is installed. The old books are neither removed nor replaced.</summary>
public sealed class PhantomStaffStarterPlayer : ModPlayer
{
    private bool starterStaffGranted;

    public override IEnumerable<Item> AddStartingItems(bool mediumCoreDeath)
    {
        if (!mediumCoreDeath)
            yield return CreateStaff();
    }

    public override void OnEnterWorld()
    {
        if (Player.whoAmI != Main.myPlayer || starterStaffGranted)
            return;
        if (!Player.HasItem(ModContent.ItemType<PhantomStaff>()))
            Player.QuickSpawnItem(new EntitySource_Misc("MoreAIPlayer:StarterPhantomStaff"), CreateStaff());
        starterStaffGranted = true;
    }

    public override void SaveData(TagCompound tag) => tag[nameof(starterStaffGranted)] = starterStaffGranted;
    public override void LoadData(TagCompound tag) => starterStaffGranted = tag.GetBool(nameof(starterStaffGranted));

    private static Item CreateStaff()
    {
        Item item = new();
        item.SetDefaults(ModContent.ItemType<PhantomStaff>());
        return item;
    }
}
