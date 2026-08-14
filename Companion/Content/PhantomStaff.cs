using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.Audio;
using Terraria.DataStructures;
using Terraria.GameContent;
using Terraria.ID;
using Terraria.Localization;
using Terraria.ModLoader;
using Terraria.ModLoader.IO;

namespace MoreAIPlayer.Companion.Content;

/// <summary>
/// One staff remembers one companion profile. It is a second shell over the existing profile and
/// entity systems: the debugger and command books remain valid and no inventory or AI state is
/// duplicated into the item itself.
/// </summary>
public sealed class PhantomStaff : ModItem
{
    private const float MaximumSummonDistance = 12f * 16f;
    private int profileId = -1;

    protected override bool CloneNewInstances => true;
    public int ProfileId => profileId;
    public bool IsBound => profileId >= 0;

    public override void SetStaticDefaults() => ItemID.Sets.GamepadWholeScreenUseRange[Type] = true;

    public override void SetDefaults()
    {
        Item.width = 58;
        Item.height = 26;
        // The sprite is a staff in appearance only. Shoot style keeps its long axis pointed at
        // the cursor and uses the same horizontal grip as a gun instead of the usual 45-degree
        // staff swing.
        Item.useStyle = ItemUseStyleID.Shoot;
        Item.useAnimation = 24;
        Item.useTime = 24;
        Item.UseSound = SoundID.Item8;
        Item.rare = ItemRarityID.Blue;
        Item.value = Item.sellPrice(silver: 20);
        Item.noMelee = true;
        Item.autoReuse = false;
        Item.maxStack = 1;
    }

    public override bool AltFunctionUse(Player player) => true;

    public override bool? UseItem(Player player)
    {
        if (player.whoAmI != Main.myPlayer)
            return true;
        if (Main.netMode != NetmodeID.SinglePlayer)
        {
            Message("SingleplayerOnly", new Color(255, 160, 120));
            return true;
        }

        CompanionProfile profile = ResolveOrBindProfile(player, createIfMissing: player.altFunctionUse != 2);
        if (profile is null)
        {
            Message("StaffUnbound", new Color(255, 180, 120));
            return true;
        }

        CompanionInstance active = CompanionEntitySystem.FindByProfile(player.whoAmI, profile.Id);
        if (player.altFunctionUse == 2)
        {
            if (active is null)
                Message("StaffNotSummoned", new Color(255, 180, 120));
            else
                UI.CompanionUISystem.Open(active.Slot);
            return true;
        }

        if (active is not null)
        {
            CompanionEntitySystem.Dismiss(active.Slot);
            return true;
        }

        Vector2 spawn = FindNearbySpawn(player, Main.MouseWorld);
        if (!CompanionEntitySystem.Summon(player, profile, spawn))
            Message("NoFreeSlot", new Color(255, 160, 120));
        return true;
    }

    public override void ModifyTooltips(List<TooltipLine> tooltips)
    {
        Player owner = Main.LocalPlayer;
        CompanionProfile profile = owner?.GetModPlayer<CompanionPlayer>()?.GetProfile(profileId);
        string binding = profile is null
            ? T("StaffUnboundTooltip")
            : T("StaffBoundTooltip", profile.Name,
                CompanionEntitySystem.FindByProfile(owner.whoAmI, profile.Id) is null
                    ? T("StaffStored") : T("StaffActive"));
        tooltips.Add(new TooltipLine(Mod, "Binding", binding)
        {
            OverrideColor = profile is null ? new Color(180, 180, 190) : new Color(115, 230, 220)
        });
    }

    public override bool PreDrawInInventory(SpriteBatch spriteBatch, Vector2 position, Rectangle frame,
        Color drawColor, Color itemColor, Vector2 origin, float scale)
    {
        Texture2D texture = TextureAssets.Item[Type].Value;
        bool active = IsActiveFor(Main.LocalPlayer);
        if (active)
        {
            Color glow = new Color(75, 245, 235, 210);
            for (int i = 0; i < 8; i++)
            {
                float angle = MathHelper.TwoPi * i / 8f;
                spriteBatch.Draw(texture, position + angle.ToRotationVector2() * 2.2f * scale,
                    frame, glow, 0f, origin, scale, SpriteEffects.None, 0f);
            }
        }
        spriteBatch.Draw(texture, position, frame, drawColor, 0f, origin, scale, SpriteEffects.None, 0f);
        return false;
    }

    public override void SaveData(TagCompound tag)
    {
        if (profileId >= 0)
            tag[nameof(profileId)] = profileId;
    }

    public override void LoadData(TagCompound tag) =>
        profileId = tag.ContainsKey(nameof(profileId)) ? tag.GetInt(nameof(profileId)) : -1;

    public override void NetSend(BinaryWriter writer) => writer.Write(profileId);
    public override void NetReceive(BinaryReader reader) => profileId = reader.ReadInt32();

    public CompanionProfile ResolveOrBindProfile(Player owner, bool createIfMissing)
    {
        CompanionPlayer data = owner.GetModPlayer<CompanionPlayer>();
        CompanionProfile existing = data.GetProfile(profileId);
        if (existing is not null && !existing.IsAutoRunning)
            return existing;
        if (!createIfMissing)
            return null;

        HashSet<int> claimed = [];
        foreach (Item inventoryItem in owner.inventory)
        {
            if (inventoryItem?.ModItem is PhantomStaff other && !ReferenceEquals(other, this) && other.profileId >= 0)
                claimed.Add(other.profileId);
        }

        foreach (CompanionProfile candidate in data.Profiles)
        {
            if (!candidate.IsAutoRunning && !claimed.Contains(candidate.Id))
            {
                profileId = candidate.Id;
                return candidate;
            }
        }

        CompanionProfile created = data.CreateProfile(data.Profiles.Count == 0);
        profileId = created.Id;
        return created;
    }

    public bool IsActiveFor(Player owner) => owner is not null && owner.active && profileId >= 0 &&
        CompanionEntitySystem.FindByProfile(owner.whoAmI, profileId) is not null;

    public static bool TryFindFirstActive(Player owner, out PhantomStaff staff, out CompanionInstance companion)
    {
        staff = null;
        companion = null;
        if (owner?.inventory is null)
            return false;

        // Player.inventory order is the visible inventory order: hotbar first, then each row
        // left-to-right. This makes "frontmost staff" deterministic and easy to control.
        foreach (Item item in owner.inventory)
        {
            if (item?.ModItem is not PhantomStaff candidate || candidate.profileId < 0)
                continue;
            CompanionInstance active = CompanionEntitySystem.FindByProfile(owner.whoAmI, candidate.profileId);
            if (active is null || active.IsAutoIdentity)
                continue;
            staff = candidate;
            companion = active;
            return true;
        }
        return false;
    }

    private static Vector2 FindNearbySpawn(Player owner, Vector2 requested)
    {
        Vector2 offset = requested - owner.Center;
        if (offset.LengthSquared() > MaximumSummonDistance * MaximumSummonDistance)
            offset = Vector2.Normalize(offset) * MaximumSummonDistance;
        Vector2 desiredCenter = owner.Center + offset;
        Vector2 topLeft = desiredCenter - new Vector2(Player.defaultWidth, Player.defaultHeight) * 0.5f;
        if (!Collision.SolidCollision(topLeft, Player.defaultWidth, Player.defaultHeight))
            return desiredCenter;

        for (int ring = 1; ring <= 10; ring++)
        {
            for (int step = 0; step < 12; step++)
            {
                Vector2 candidate = owner.Center + (MathHelper.TwoPi * step / 12f).ToRotationVector2() * ring * 16f;
                topLeft = candidate - new Vector2(Player.defaultWidth, Player.defaultHeight) * 0.5f;
                if (!Collision.SolidCollision(topLeft, Player.defaultWidth, Player.defaultHeight))
                    return candidate;
            }
        }
        return owner.Center;
    }

    private static void Message(string key, Color color) => Main.NewText(T(key), color);
    private static string T(string key, params object[] args) =>
        Language.GetTextValue($"Mods.MoreAIPlayer.CompanionCommand.{key}", args);

    public override void AddRecipes()
    {
        CreateRecipe()
            .AddIngredient(ItemID.Wood, 20)
            .AddIngredient(ItemID.Amethyst, 3)
            .AddIngredient(ItemID.Topaz, 3)
            .AddTile(TileID.WorkBenches)
            .Register();
    }
}
