using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
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
/// One staff remembers one companion profile. The live profile stays in the existing player/entity
/// systems while its complete persistent snapshot travels inside the item, allowing ownership to
/// change without resetting the companion.
/// </summary>
public sealed class PhantomStaff : ModItem
{
    private const float MaximumSummonDistance = 12f * 16f;
    // Terraria packets use an unsigned-short length. Leave room for the item header, strings,
    // prefix and any GlobalItem payloads instead of trying to fill the entire 65,535-byte packet.
    private const int MaximumNetworkSnapshotBytes = 48 * 1024;
    private const int MaximumDecompressedSnapshotBytes = 2 * 1024 * 1024;
    private int profileId = -1;
    private string staffIdentity = string.Empty;
    private string boundOwnerIdentity = string.Empty;
    private string boundOwnerName = string.Empty;
    private int staffNumber;
    private TagCompound profileSnapshot;
    private byte[] cachedNetworkSnapshot;
    private bool snapshotWarningLogged;
    private int snapshotRefreshTime;
    private int ownershipWarningTime;

    protected override bool CloneNewInstances => true;
    public int ProfileId => profileId;
    public int StaffNumber => staffNumber;
    public bool IsBound => !string.IsNullOrWhiteSpace(boundOwnerName) && profileId >= 0;

    public override void SetStaticDefaults() => ItemID.Sets.GamepadWholeScreenUseRange[Type] = true;

    public override void SetDefaults()
    {
        Item.width = 58;
        Item.height = 26;
        // Gun/bow-style use: hold the staff toward the cursor instead of swinging it
        // or using the fixed left/right "eat/place" pose.
        Item.useStyle = ItemUseStyleID.Shoot;
        Item.useTurn = true;
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

    public override bool CanUseItem(Player player)
    {
        if (!IsForeignTo(player))
            return true;
        if (player.whoAmI == Main.myPlayer && ownershipWarningTime <= 0)
        {
            Message("StaffForeignUseBlocked", new Color(255, 130, 110), boundOwnerName);
            ownershipWarningTime = 90;
        }
        return false;
    }

    public override void UpdateInventory(Player player)
    {
        if (Main.netMode == NetmodeID.Server || player.whoAmI != Main.myPlayer)
            return;

        if (ownershipWarningTime > 0)
            ownershipWarningTime--;

        bool changed = false;
        if (HasEarlierDuplicate(player))
        {
            ResetAsNewStaff();
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(boundOwnerName))
        {
            BindFreshStaff(player);
            changed = true;
        }
        else if (IsOwnedBy(player))
        {
            CompanionPlayer data = player.GetModPlayer<CompanionPlayer>();
            if (string.IsNullOrWhiteSpace(boundOwnerIdentity))
            {
                boundOwnerIdentity = data.PhantomOwnerIdentity;
                changed = true;
            }
            if (!string.Equals(boundOwnerName, OwnerName(player), StringComparison.Ordinal))
            {
                boundOwnerName = OwnerName(player);
                changed = true;
            }
            data.ObservePhantomStaffNumber(staffNumber);
            CompanionProfile profile = ResolveOrBindProfile(player, createIfMissing: true);
            if (profile is not null && (++snapshotRefreshTime >= 300 || profileSnapshot is null))
            {
                RefreshSnapshot(profile);
                snapshotRefreshTime = 0;
            }
        }

        if (changed)
            SyncInventoryItem(player);
    }

    public override bool CanRightClick() => IsForeignTo(Main.LocalPlayer);

    public override bool ConsumeItem(Player player) => false;

    public override void RightClick(Player player)
    {
        if (Main.netMode == NetmodeID.Server || player.whoAmI != Main.myPlayer || !IsForeignTo(player))
            return;
        if (profileSnapshot is null)
        {
            Message("StaffTransferDataMissing", new Color(255, 120, 100));
            return;
        }

        // Old/corrupted item data must never make multiple transferred staffs share an empty profile key.
        if (string.IsNullOrWhiteSpace(staffIdentity))
            staffIdentity = Guid.NewGuid().ToString("N");

        CompanionPlayer data = player.GetModPlayer<CompanionPlayer>();
        CompanionProfile profile = data.ImportPhantomStaffProfile(profileSnapshot, staffIdentity);
        boundOwnerIdentity = data.PhantomOwnerIdentity;
        boundOwnerName = OwnerName(player);
        staffNumber = data.AllocatePhantomStaffNumber(HighestOwnedNumber(player) + 1);
        profileId = profile.Id;
        RefreshSnapshot(profile);
        SyncInventoryItem(player);
        Message("StaffRebound", new Color(115, 230, 220), staffNumber, profile.Name);
    }

    public override bool? UseItem(Player player)
    {
        if (player.whoAmI != Main.myPlayer)
            return true;
        if (IsForeignTo(player))
        {
            Message("StaffForeignUseBlocked", new Color(255, 130, 110), boundOwnerName);
            return true;
        }
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
        TooltipLine itemName = tooltips.Find(line => line.Name == "ItemName");
        if (itemName is not null && staffNumber > 0)
            itemName.Text = T("StaffNumberedName", DisplayName.Value, staffNumber);

        CompanionProfile profile = IsOwnedBy(owner)
            ? owner.GetModPlayer<CompanionPlayer>().GetProfileByPhantomStaff(staffIdentity)
            : null;
        string companionName = profile?.Name ?? SnapshotCompanionName();
        if (IsForeignTo(owner))
        {
            tooltips.Add(new TooltipLine(Mod, "ForeignOwner", T("StaffForeignTooltip", boundOwnerName))
            {
                OverrideColor = new Color(255, 120, 105)
            });
            tooltips.Add(new TooltipLine(Mod, "RebindHint", T("StaffRebindHint"))
            {
                OverrideColor = new Color(255, 195, 105)
            });
        }
        else
        {
            string binding = string.IsNullOrWhiteSpace(boundOwnerName)
                ? T("StaffUnboundTooltip")
                : T("StaffOwnerTooltip", boundOwnerName);
            tooltips.Add(new TooltipLine(Mod, "Binding", binding)
            {
                OverrideColor = string.IsNullOrWhiteSpace(boundOwnerName)
                    ? new Color(180, 180, 190) : new Color(115, 230, 220)
            });
        }

        if (!string.IsNullOrWhiteSpace(companionName))
        {
            string state = profile is not null &&
                CompanionEntitySystem.FindByProfile(owner.whoAmI, profile.Id) is not null
                    ? T("StaffActive") : T("StaffStored");
            tooltips.Add(new TooltipLine(Mod, "Companion", T("StaffCompanionTooltip", companionName, state))
            {
                OverrideColor = new Color(150, 215, 255)
            });
        }
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
        // SaveData is also called while the character-select preview is being built. At that
        // point Main.LocalPlayer can be in the middle of unloading its ModPlayer instances, so
        // this callback must only serialize the snapshot already maintained during gameplay.
        if (profileId >= 0)
            tag[nameof(profileId)] = profileId;
        if (!string.IsNullOrWhiteSpace(staffIdentity))
            tag[nameof(staffIdentity)] = staffIdentity;
        if (!string.IsNullOrWhiteSpace(boundOwnerIdentity))
            tag[nameof(boundOwnerIdentity)] = boundOwnerIdentity;
        if (!string.IsNullOrWhiteSpace(boundOwnerName))
            tag[nameof(boundOwnerName)] = boundOwnerName;
        if (staffNumber > 0)
            tag[nameof(staffNumber)] = staffNumber;
        if (profileSnapshot is not null)
            tag[nameof(profileSnapshot)] = profileSnapshot;
    }

    public override void LoadData(TagCompound tag)
    {
        profileId = tag.ContainsKey(nameof(profileId)) ? tag.GetInt(nameof(profileId)) : -1;
        staffIdentity = tag.ContainsKey(nameof(staffIdentity)) ? tag.GetString(nameof(staffIdentity)) : string.Empty;
        boundOwnerIdentity = tag.ContainsKey(nameof(boundOwnerIdentity)) ? tag.GetString(nameof(boundOwnerIdentity)) : string.Empty;
        boundOwnerName = tag.ContainsKey(nameof(boundOwnerName)) ? tag.GetString(nameof(boundOwnerName)) : string.Empty;
        staffNumber = Math.Max(0, tag.ContainsKey(nameof(staffNumber)) ? tag.GetInt(nameof(staffNumber)) : 0);
        profileSnapshot = tag.ContainsKey(nameof(profileSnapshot)) ? tag.GetCompound(nameof(profileSnapshot)) : null;
        InvalidateNetworkSnapshotCache();
    }

    public override void NetSend(BinaryWriter writer)
    {
        // NetSend is invoked on both clients and servers. It must serialize only item-owned state:
        // Main.LocalPlayer is not a valid owner context on a server and can belong to someone else.
        writer.Write(profileId);
        writer.Write(staffIdentity ?? string.Empty);
        writer.Write(boundOwnerIdentity ?? string.Empty);
        writer.Write(boundOwnerName ?? string.Empty);
        writer.Write(staffNumber);
        WriteSnapshot(writer, profileSnapshot);
    }

    public override void NetReceive(BinaryReader reader)
    {
        profileId = reader.ReadInt32();
        staffIdentity = reader.ReadString();
        boundOwnerIdentity = reader.ReadString();
        boundOwnerName = reader.ReadString();
        staffNumber = Math.Max(0, reader.ReadInt32());
        profileSnapshot = ReadSnapshot(reader);
    }

    public CompanionProfile ResolveOrBindProfile(Player owner, bool createIfMissing)
    {
        if (owner is null || IsForeignTo(owner))
            return null;
        CompanionPlayer data = owner.GetModPlayer<CompanionPlayer>();
        CompanionProfile byIdentity = data.GetProfileByPhantomStaff(staffIdentity);
        if (byIdentity is not null && !byIdentity.IsAutoRunning)
        {
            profileId = byIdentity.Id;
            return byIdentity;
        }

        CompanionProfile existing = data.GetProfile(profileId);
        if (existing is not null && !existing.IsAutoRunning &&
            (string.IsNullOrWhiteSpace(existing.PhantomStaffIdentity) ||
             string.Equals(existing.PhantomStaffIdentity, staffIdentity, StringComparison.Ordinal)))
        {
            existing.BindToPhantomStaff(staffIdentity);
            return existing;
        }
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
            if (!candidate.IsAutoRunning && !claimed.Contains(candidate.Id) &&
                string.IsNullOrWhiteSpace(candidate.PhantomStaffIdentity))
            {
                profileId = candidate.Id;
                candidate.BindToPhantomStaff(staffIdentity);
                return candidate;
            }
        }

        CompanionProfile created = data.CreateProfile(data.Profiles.Count == 0);
        created.BindToPhantomStaff(staffIdentity);
        profileId = created.Id;
        return created;
    }

    public bool IsActiveFor(Player owner) => owner is not null && owner.active && IsOwnedBy(owner) && profileId >= 0 &&
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
            if (item?.ModItem is not PhantomStaff candidate || candidate.profileId < 0 ||
                !candidate.IsOwnedBy(owner))
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

    private void BindFreshStaff(Player owner)
    {
        if (string.IsNullOrWhiteSpace(staffIdentity))
            staffIdentity = Guid.NewGuid().ToString("N");
        CompanionPlayer data = owner.GetModPlayer<CompanionPlayer>();
        boundOwnerIdentity = data.PhantomOwnerIdentity;
        boundOwnerName = OwnerName(owner);
        staffNumber = data.AllocatePhantomStaffNumber(HighestOwnedNumber(owner) + 1);
        CompanionProfile profile = ResolveOrBindProfile(owner, createIfMissing: true);
        if (profile is not null)
            RefreshSnapshot(profile);
    }

    private void ResetAsNewStaff()
    {
        profileId = -1;
        staffIdentity = Guid.NewGuid().ToString("N");
        boundOwnerIdentity = string.Empty;
        boundOwnerName = string.Empty;
        staffNumber = 0;
        profileSnapshot = null;
        InvalidateNetworkSnapshotCache();
        snapshotRefreshTime = 0;
    }

    private bool HasEarlierDuplicate(Player owner)
    {
        if (string.IsNullOrWhiteSpace(staffIdentity))
            return false;
        foreach (Item inventoryItem in owner.inventory)
        {
            if (inventoryItem?.ModItem is not PhantomStaff other)
                continue;
            if (ReferenceEquals(other, this))
                return false;
            if (string.Equals(other.staffIdentity, staffIdentity, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private int HighestOwnedNumber(Player owner)
    {
        int highest = 0;
        foreach (Item inventoryItem in owner.inventory)
        {
            if (inventoryItem?.ModItem is PhantomStaff other && !ReferenceEquals(other, this) &&
                other.IsOwnedBy(owner))
                highest = Math.Max(highest, other.staffNumber);
        }
        return highest;
    }

    private bool IsOwnedBy(Player owner)
    {
        if (owner is null || string.IsNullOrWhiteSpace(boundOwnerName))
            return false;
        if (string.IsNullOrWhiteSpace(boundOwnerIdentity))
            return string.Equals(boundOwnerName, OwnerName(owner), StringComparison.Ordinal);
        string ownerIdentity = owner.GetModPlayer<CompanionPlayer>().PhantomOwnerIdentity;
        return string.Equals(boundOwnerIdentity, ownerIdentity, StringComparison.Ordinal);
    }

    private bool IsForeignTo(Player owner) => owner is not null &&
        !string.IsNullOrWhiteSpace(boundOwnerName) && !IsOwnedBy(owner);

    private static string OwnerName(Player owner) => owner?.name?.Trim() ?? string.Empty;

    private string SnapshotCompanionName()
    {
        if (profileSnapshot is null || !profileSnapshot.ContainsKey(nameof(CompanionProfile.Name)))
            return string.Empty;
        return profileSnapshot.GetString(nameof(CompanionProfile.Name));
    }

    private void RefreshSnapshot(CompanionProfile profile)
    {
        profileId = profile.Id;
        profile.BindToPhantomStaff(staffIdentity);
        profileSnapshot = profile.Save();
        InvalidateNetworkSnapshotCache();
    }

    private void SyncInventoryItem(Player owner)
    {
        if (Main.netMode != NetmodeID.MultiplayerClient || owner.whoAmI != Main.myPlayer)
            return;
        for (int slot = 0; slot < owner.inventory.Length; slot++)
        {
            if (!ReferenceEquals(owner.inventory[slot]?.ModItem, this))
                continue;
            NetMessage.SendData(MessageID.SyncEquipment, -1, -1, null,
                owner.whoAmI, slot, Item.prefix);
            return;
        }
    }

    private void WriteSnapshot(BinaryWriter writer, TagCompound snapshot)
    {
        if (snapshot is null)
        {
            writer.Write(0);
            return;
        }

        byte[] bytes = GetOrCreateNetworkSnapshot(snapshot);
        if (bytes is null)
        {
            // A negative length means "snapshot deliberately omitted". The receiver fails closed:
            // the staff metadata still syncs, but profile import is refused instead of disconnecting
            // the player with an oversized Terraria packet.
            writer.Write(-1);
            return;
        }

        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private TagCompound ReadSnapshot(BinaryReader reader)
    {
        int length = reader.ReadInt32();
        if (length <= 0)
        {
            cachedNetworkSnapshot = null;
            return null;
        }
        if (length > MaximumNetworkSnapshotBytes)
        {
            SkipBytes(reader, length);
            WarnSnapshotOnce($"Rejected a Phantom Staff network snapshot of {length} bytes; the safe limit is {MaximumNetworkSnapshotBytes} bytes.");
            cachedNetworkSnapshot = null;
            return null;
        }

        byte[] bytes = reader.ReadBytes(length);
        if (bytes.Length != length)
            throw new EndOfStreamException("Phantom Staff profile snapshot ended early.");

        try
        {
            using MemoryStream compressed = new(bytes, writable: false);
            using GZipStream gzip = new(compressed, CompressionMode.Decompress, leaveOpen: false);
            using MemoryStream uncompressed = new();
            CopyWithLimit(gzip, uncompressed, MaximumDecompressedSnapshotBytes);
            uncompressed.Position = 0;
            TagCompound snapshot = TagIO.FromStream(uncompressed, compressed: false);
            cachedNetworkSnapshot = bytes;
            snapshotWarningLogged = false;
            return snapshot;
        }
        catch (Exception exception)
        {
            cachedNetworkSnapshot = null;
            WarnSnapshotOnce($"Rejected an invalid Phantom Staff network snapshot: {exception.GetType().Name}: {exception.Message}");
            return null;
        }
    }

    private byte[] GetOrCreateNetworkSnapshot(TagCompound snapshot)
    {
        if (cachedNetworkSnapshot is not null)
            return cachedNetworkSnapshot;

        try
        {
            using MemoryStream uncompressed = new();
            TagIO.ToStream(snapshot, uncompressed, compress: false);
            if (uncompressed.Length > MaximumDecompressedSnapshotBytes)
            {
                WarnSnapshotOnce($"Phantom Staff profile snapshot is {uncompressed.Length} bytes before compression; the safe limit is {MaximumDecompressedSnapshotBytes} bytes.");
                return null;
            }

            uncompressed.Position = 0;
            using MemoryStream compressed = new();
            using (GZipStream gzip = new(compressed, CompressionLevel.Optimal, leaveOpen: true))
                uncompressed.CopyTo(gzip);

            if (compressed.Length > MaximumNetworkSnapshotBytes)
            {
                WarnSnapshotOnce($"Phantom Staff profile snapshot is {compressed.Length} compressed bytes; the safe network limit is {MaximumNetworkSnapshotBytes} bytes.");
                return null;
            }

            cachedNetworkSnapshot = compressed.ToArray();
            snapshotWarningLogged = false;
            return cachedNetworkSnapshot;
        }
        catch (Exception exception)
        {
            WarnSnapshotOnce($"Could not serialize the Phantom Staff network snapshot: {exception.GetType().Name}: {exception.Message}");
            return null;
        }
    }

    private void InvalidateNetworkSnapshotCache()
    {
        cachedNetworkSnapshot = null;
        snapshotWarningLogged = false;
    }

    private void WarnSnapshotOnce(string message)
    {
        if (snapshotWarningLogged)
            return;
        snapshotWarningLogged = true;
        Mod.Logger.Warn(message);
    }

    private static void CopyWithLimit(Stream source, Stream destination, int maximumBytes)
    {
        byte[] buffer = new byte[8192];
        int total = 0;
        while (true)
        {
            int read = source.Read(buffer, 0, buffer.Length);
            if (read <= 0)
                return;
            total = checked(total + read);
            if (total > maximumBytes)
                throw new InvalidDataException($"Decompressed snapshot exceeds {maximumBytes} bytes.");
            destination.Write(buffer, 0, read);
        }
    }

    private static void SkipBytes(BinaryReader reader, int length)
    {
        if (length < 0)
            throw new InvalidDataException("Negative snapshot length.");

        Stream stream = reader.BaseStream;
        if (stream.CanSeek)
        {
            long remaining = stream.Length - stream.Position;
            if (length > remaining)
                throw new EndOfStreamException("Phantom Staff profile snapshot ended early.");
            stream.Position += length;
            return;
        }

        byte[] buffer = new byte[8192];
        int remainingBytes = length;
        while (remainingBytes > 0)
        {
            int read = reader.Read(buffer, 0, Math.Min(buffer.Length, remainingBytes));
            if (read <= 0)
                throw new EndOfStreamException("Phantom Staff profile snapshot ended early.");
            remainingBytes -= read;
        }
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

    private static void Message(string key, Color color, params object[] args) => Main.NewText(T(key, args), color);
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
