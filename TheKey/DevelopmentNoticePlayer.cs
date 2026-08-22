using System.Collections.Generic;
using Terraria;
using Terraria.DataStructures;
using Terraria.ModLoader;
using Terraria.ModLoader.IO;

namespace MoreAIPlayer;

/// <summary>
/// Gives the notice to newly-created characters and grants it once to characters created
/// before this item existed. The saved flag deliberately prevents reload-time replacements.
/// </summary>
public sealed class DevelopmentNoticePlayer : ModPlayer
{
    private bool noticeGranted;

    public override IEnumerable<Item> AddStartingItems(bool mediumCoreDeath)
    {
        if (!mediumCoreDeath)
            yield return CreateNotice();
    }

    public override void OnEnterWorld()
    {
        if (Player.whoAmI != Main.myPlayer || noticeGranted)
            return;

        // Existing characters do not pass through AddStartingItems. Give them the same one-time
        // notice on their first load after this update, then persist the claim either way.
        if (!Player.HasItem(ModContent.ItemType<DevelopmentNotice>()))
            Player.QuickSpawnItem(new EntitySource_Misc("MoreAIPlayer:DevelopmentNotice"), CreateNotice());

        noticeGranted = true;
    }

    public override void SaveData(TagCompound tag) => tag[nameof(noticeGranted)] = noticeGranted;

    public override void LoadData(TagCompound tag) =>
        noticeGranted = tag.GetBool(nameof(noticeGranted));

    private static Item CreateNotice()
    {
        Item item = new();
        item.SetDefaults(ModContent.ItemType<DevelopmentNotice>());
        return item;
    }
}
