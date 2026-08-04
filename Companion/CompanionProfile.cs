using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.Localization;
using Terraria.ModLoader;
using Terraria.ModLoader.IO;

namespace MoreAIPlayer.Companion;

public sealed class CompanionProfile
{
    public int Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public bool MirrorsOwnerAppearance { get; private set; }
    public bool Male { get; private set; }
    public int SkinVariant { get; private set; }
    public int Hair { get; private set; }
    public Color HairColor { get; private set; }
    public Color SkinColor { get; private set; }
    public Color EyeColor { get; private set; }
    public Color ShirtColor { get; private set; }
    public Color UnderShirtColor { get; private set; }
    public Color PantsColor { get; private set; }
    public Color ShoeColor { get; private set; }

    /// <summary>
    /// Hotbar index reserved for the guard weapon. It is excluded from the normal weapon
    /// rotation and from summon-staff upkeep, and is used only when the companion is swarmed
    /// (see CompanionBrain's guard response). The UI marks it with a blue border.
    /// </summary>
    public const int GuardSlot = 0;

    /// <summary>
    /// Hotbar index reserved for the summon staff. Excluded from the normal rotation the same
    /// way <see cref="GuardSlot"/> is, and it is the only slot summon upkeep looks at. The UI
    /// marks it with a yellow border.
    /// </summary>
    public const int SummonSlot = CompanionPlayer.HotbarSize - 1;

    /// <summary>
    /// Hotbar index reserved for the pickaxe that Mining mode uses. Like the guard and summon
    /// slots it is outside the normal rotation, so the companion never swings its pick at an
    /// enemy. The UI marks it with a grey border.
    /// </summary>
    public const int PickSlot = GuardSlot + 1;

    /// <summary>First hotbar index the normal weapon rotation may draw from.</summary>
    public const int FirstCombatSlot = PickSlot + 1;

    /// <summary>One past the last hotbar index the normal weapon rotation may draw from.</summary>
    public const int EndCombatSlot = SummonSlot;

    public Item[] Hotbar { get; private set; }
    public Item[] Ammo { get; private set; }
    public Item[] Armor { get; private set; }
    public Item[] Accessories { get; private set; }
    public Item[] Vanity { get; private set; }
    public Item[] Dyes { get; private set; }
    public Item[] Backpack { get; private set; }

    /// <summary>The dedicated bug-net slot, as a one-element array so it can drive an ItemSlot.</summary>
    public Item[] Net { get; private set; }

    /// <summary>The dedicated fishing-pole slot, same shape as <see cref="Net"/>.</summary>
    public Item[] Rod { get; private set; }

    /// <summary>The dedicated quick-grapple slot. It is bound to Player.miscEquips[4].</summary>
    public Item[] Hook { get; private set; }

    /// <summary>The dedicated mount slot. It is bound to Player.miscEquips[3].</summary>
    public Item[] Mount { get; private set; }

    /// <summary>
    /// Air items handed to the entity in place of accessories the world has not unlocked yet.
    /// A shared singleton would be dangerous — vanilla writes through equipped Item references
    /// (prefixes, favorited flags) — so each locked slot gets its own instance.
    /// </summary>
    private Item[] lockedAccessoryFiller;

    public int ActiveWeaponSlot = -1;

    /// <summary>Behaviour mode cycled by the command book. Persists with the profile.</summary>
    public CompanionMode Mode = CompanionMode.Attack;

    /// <summary>潜伏 mode: with a rogue weapon, wait for full Calamity stealth before every attack.</summary>
    public bool RogueStealthWait = true;

    private readonly HashSet<string> preferAltUseWeapons = [];

    public bool PrefersAltUse(Item weapon) =>
        !weapon.IsAir && preferAltUseWeapons.Contains(CompanionWeaponOverrides.ItemKey(weapon));

    public void TogglePrefersAltUse(Item weapon)
    {
        if (weapon.IsAir)
            return;
        string key = CompanionWeaponOverrides.ItemKey(weapon);
        if (!preferAltUseWeapons.Remove(key))
            preferAltUseWeapons.Add(key);
    }

    /// <summary>
    /// Gives this companion a player-selected display name. The text comes from Terraria's
    /// platform text-input path, so it deliberately stays as Unicode instead of being reduced
    /// to an ASCII-only "player name". A blank value is not a useful companion name and is
    /// rejected by the caller.
    /// </summary>
    public void Rename(string name)
    {
        if (!string.IsNullOrWhiteSpace(name))
            Name = name.Trim();
    }

    private CompanionProfile(int id)
    {
        Id = id;
        Hotbar = CreateEmptyItems(CompanionPlayer.HotbarSize);
        Ammo = CreateEmptyItems(CompanionPlayer.AmmoSize);
        Armor = CreateEmptyItems(CompanionPlayer.ArmorSize);
        Accessories = CreateEmptyItems(CompanionPlayer.AccessorySize);
        Vanity = CreateEmptyItems(CompanionPlayer.VanitySize);
        Dyes = CreateEmptyItems(CompanionPlayer.DyeSize);
        Backpack = CreateEmptyItems(CompanionPlayer.BackpackSize);
        Net = CreateEmptyItems(CompanionPlayer.NetSize);
        Rod = CreateEmptyItems(CompanionPlayer.RodSize);
        Hook = CreateEmptyItems(CompanionPlayer.HookSize);
        Mount = CreateEmptyItems(CompanionPlayer.MountSize);
        lockedAccessoryFiller = CreateEmptyItems(CompanionPlayer.AccessorySize);
    }

    public static CompanionProfile Create(int id, Player owner, bool mirrorsOwner)
    {
        CompanionProfile profile = new(id)
        {
            MirrorsOwnerAppearance = mirrorsOwner,
            Name = GenerateName()
        };

        if (mirrorsOwner)
            profile.CopyOwnerAppearance(owner);
        else
            profile.RandomizeAppearance();
        return profile;
    }

    /// <summary>
    /// Draws from the vanilla town NPC name pools so companion names look like the names of
    /// real players joining the game. getNewNPCName is an instance method that reads the
    /// NPC's own type, so a throwaway NPC picks which pool to draw from.
    /// </summary>
    public static string GenerateName()
    {
        int[] namePools =
        [
            NPCID.Guide, NPCID.Merchant, NPCID.Nurse, NPCID.ArmsDealer, NPCID.Dryad,
            NPCID.Mechanic, NPCID.GoblinTinkerer, NPCID.Wizard, NPCID.Stylist,
            NPCID.Angler, NPCID.Pirate, NPCID.Steampunker, NPCID.PartyGirl, NPCID.Painter
        ];
        NPC probe = new();
        probe.SetDefaults(namePools[Main.rand.Next(namePools.Length)]);
        string name = probe.getNewNPCName();
        return string.IsNullOrWhiteSpace(name)
            ? Language.GetTextValue("Mods.MoreAIPlayer.CompanionUI.DefaultName")
            : name;
    }

    public Item ActiveWeapon =>
        ActiveWeaponSlot >= 0 && ActiveWeaponSlot < Hotbar.Length ? Hotbar[ActiveWeaponSlot] : new Item();

    /// <summary>Whatever the player parked in <see cref="GuardSlot"/>; air when they left it empty.</summary>
    public Item GuardWeapon => Hotbar[GuardSlot];

    /// <summary>Whatever the player parked in <see cref="SummonSlot"/>; air when they left it empty.</summary>
    public Item SummonWeapon => Hotbar[SummonSlot];

    /// <summary>The equipped bug net, or air when the net slot is empty.</summary>
    public Item NetTool => Net[0];

    /// <summary>The equipped fishing pole, or air when the rod slot is empty.</summary>
    public Item RodTool => Rod[0];

    /// <summary>The equipped quick-grapple item, or air when the hook slot is empty.</summary>
    public Item HookTool => Hook[0];

    /// <summary>The equipped mount item, or air when the mount slot is empty.</summary>
    public Item MountTool => Mount[0];

    /// <summary>The equipped pickaxe, or air when the pick slot is empty.</summary>
    public Item PickTool => Hotbar[PickSlot];

    /// <summary>
    /// The accessory to hand the entity for a given slot: the real item while the world has
    /// that slot unlocked, and a private air item once it is past the world's limit, so a
    /// locked accessory stops applying its effects without the player losing it.
    /// </summary>
    public Item AccessoryForSlot(int index, int unlockedCount) =>
        index < unlockedCount ? Accessories[index] : lockedAccessoryFiller[index];

    /// <summary>
    /// Moves items that cannot do a reserved slot's job out of it, once, at load time.
    ///
    /// Reserved slots were introduced into a weapon bar players had already filled, so a saved
    /// loadout can have an ordinary weapon sitting in what is now the pick or summon slot. The
    /// weapon still works — <see cref="CompanionWeaponBehaviors.SelectWeapon"/> falls back to
    /// the reserved slots rather than ignoring them — but it fights at the wrong priority and
    /// the slot cannot do the job it was reserved for. Relocating puts the loadout back into a
    /// state the player would have built themselves.
    ///
    /// The guard slot is left alone: it accepts any weapon by design, so nothing there is
    /// misplaced. Displaced items go to a free rotation slot, or the backpack, and are left
    /// where they are if there is no room anywhere — never destroyed.
    /// </summary>
    private void MigrateReservedSlots()
    {
        Relocate(PickSlot, CompanionSlotRules.IsPickaxe);
        Relocate(SummonSlot, CompanionSlotRules.IsSummonWeapon);

        void Relocate(int slot, System.Func<Item, bool> belongsHere)
        {
            Item occupant = Hotbar[slot];
            if (occupant.IsAir || belongsHere(occupant))
                return;

            for (int free = FirstCombatSlot; free < EndCombatSlot; free++)
            {
                if (!Hotbar[free].IsAir)
                    continue;

                Hotbar[free] = occupant;
                Hotbar[slot] = new Item();
                return;
            }

            Item moving = occupant.Clone();
            if (TryAddToBackpack(moving))
                Hotbar[slot] = new Item();
        }
    }

    public void ApplyAppearanceTo(Player visual, Player owner)
    {
        if (MirrorsOwnerAppearance)
            CopyOwnerAppearance(owner);

        visual.Male = Male;
        visual.skinVariant = SkinVariant;
        visual.hair = Hair;
        visual.hairColor = HairColor;
        visual.skinColor = SkinColor;
        visual.eyeColor = EyeColor;
        visual.shirtColor = ShirtColor;
        visual.underShirtColor = UnderShirtColor;
        visual.pantsColor = PantsColor;
        visual.shoeColor = ShoeColor;
    }

    public Item FindFirst(System.Func<Item, bool> predicate)
    {
        foreach (Item item in Backpack)
        {
            if (!item.IsAir && predicate(item))
                return item;
        }
        return null;
    }

    public Item FindTool(System.Func<Item, bool> predicate)
    {
        foreach (Item item in Hotbar)
        {
            if (!item.IsAir && predicate(item))
                return item;
        }
        return FindFirst(predicate);
    }

    public Item FindEquippedWing()
    {
        foreach (Item item in Accessories)
        {
            if (!item.IsAir && item.wingSlot > 0)
                return item;
        }
        return null;
    }

    public bool TryAddToBackpack(Item incoming)
    {
        if (incoming.IsAir)
            return true;

        foreach (Item stored in Backpack)
        {
            if (stored.IsAir || stored.type != incoming.type || stored.prefix != incoming.prefix || stored.stack >= stored.maxStack)
                continue;

            int transfer = System.Math.Min(incoming.stack, stored.maxStack - stored.stack);
            stored.stack += transfer;
            incoming.stack -= transfer;
            if (incoming.stack <= 0)
                return true;
        }

        for (int i = 0; i < Backpack.Length; i++)
        {
            if (!Backpack[i].IsAir)
                continue;

            Backpack[i] = incoming.Clone();
            incoming.TurnToAir();
            return true;
        }
        return false;
    }

    public TagCompound Save()
    {
        return new TagCompound
        {
            [nameof(Id)] = Id,
            [nameof(Name)] = Name,
            [nameof(MirrorsOwnerAppearance)] = MirrorsOwnerAppearance,
            [nameof(Male)] = Male,
            [nameof(SkinVariant)] = SkinVariant,
            [nameof(Hair)] = Hair,
            [nameof(HairColor)] = SaveColor(HairColor),
            [nameof(SkinColor)] = SaveColor(SkinColor),
            [nameof(EyeColor)] = SaveColor(EyeColor),
            [nameof(ShirtColor)] = SaveColor(ShirtColor),
            [nameof(UnderShirtColor)] = SaveColor(UnderShirtColor),
            [nameof(PantsColor)] = SaveColor(PantsColor),
            [nameof(ShoeColor)] = SaveColor(ShoeColor),
            [nameof(RogueStealthWait)] = RogueStealthWait,
            [nameof(Mode)] = (int)Mode,
            ["PreferAltUseWeapons"] = new List<string>(preferAltUseWeapons),
            [nameof(Hotbar)] = SaveItems(Hotbar),
            [nameof(Ammo)] = SaveItems(Ammo),
            [nameof(Armor)] = SaveItems(Armor),
            [nameof(Accessories)] = SaveItems(Accessories),
            [nameof(Vanity)] = SaveItems(Vanity),
            [nameof(Dyes)] = SaveItems(Dyes),
            [nameof(Backpack)] = SaveItems(Backpack),
            [nameof(Net)] = SaveItems(Net),
            [nameof(Rod)] = SaveItems(Rod),
            [nameof(Hook)] = SaveItems(Hook),
            [nameof(Mount)] = SaveItems(Mount)
        };
    }

    public static CompanionProfile Load(TagCompound tag)
    {
        CompanionProfile profile = new(tag.GetInt(nameof(Id)))
        {
            Name = tag.ContainsKey(nameof(Name)) && !string.IsNullOrWhiteSpace(tag.GetString(nameof(Name)))
                ? tag.GetString(nameof(Name))
                : GenerateName(),
            MirrorsOwnerAppearance = tag.GetBool(nameof(MirrorsOwnerAppearance)),
            Male = tag.GetBool(nameof(Male)),
            SkinVariant = tag.GetInt(nameof(SkinVariant)),
            Hair = tag.GetInt(nameof(Hair)),
            HairColor = LoadColor(tag, nameof(HairColor)),
            SkinColor = LoadColor(tag, nameof(SkinColor)),
            EyeColor = LoadColor(tag, nameof(EyeColor)),
            ShirtColor = LoadColor(tag, nameof(ShirtColor)),
            UnderShirtColor = LoadColor(tag, nameof(UnderShirtColor)),
            PantsColor = LoadColor(tag, nameof(PantsColor)),
            ShoeColor = LoadColor(tag, nameof(ShoeColor)),
            Hotbar = LoadItems(tag, nameof(Hotbar), CompanionPlayer.HotbarSize),
            Ammo = LoadItems(tag, nameof(Ammo), CompanionPlayer.AmmoSize),
            Armor = LoadItems(tag, nameof(Armor), CompanionPlayer.ArmorSize),
            Accessories = LoadItems(tag, nameof(Accessories), CompanionPlayer.AccessorySize),
            Vanity = LoadItems(tag, nameof(Vanity), CompanionPlayer.VanitySize),
            Dyes = LoadItems(tag, nameof(Dyes), CompanionPlayer.DyeSize),
            Backpack = LoadItems(tag, nameof(Backpack), CompanionPlayer.BackpackSize),
            Net = LoadItems(tag, nameof(Net), CompanionPlayer.NetSize),
            Rod = LoadItems(tag, nameof(Rod), CompanionPlayer.RodSize),
            Hook = LoadItems(tag, nameof(Hook), CompanionPlayer.HookSize),
            Mount = LoadItems(tag, nameof(Mount), CompanionPlayer.MountSize)
        };
        profile.RogueStealthWait = !tag.ContainsKey(nameof(RogueStealthWait)) || tag.GetBool(nameof(RogueStealthWait));
        if (tag.ContainsKey(nameof(Mode)))
            profile.Mode = (CompanionMode)tag.GetInt(nameof(Mode));
        if (tag.ContainsKey("PreferAltUseWeapons"))
        {
            foreach (string key in tag.GetList<string>("PreferAltUseWeapons"))
                profile.preferAltUseWeapons.Add(key);
        }
        profile.MigrateReservedSlots();
        return profile;
    }

    public static CompanionProfile LoadLegacy(int id, Player owner, TagCompound tag)
    {
        CompanionProfile profile = Create(id, owner, true);
        profile.Hotbar = LoadItems(tag, nameof(Hotbar), CompanionPlayer.HotbarSize);
        profile.Ammo = LoadItems(tag, nameof(Ammo), CompanionPlayer.AmmoSize);
        profile.Armor = LoadItems(tag, nameof(Armor), CompanionPlayer.ArmorSize);
        profile.Accessories = LoadItems(tag, nameof(Accessories), CompanionPlayer.AccessorySize);
        profile.Vanity = LoadItems(tag, nameof(Vanity), CompanionPlayer.VanitySize);
        profile.Dyes = LoadItems(tag, nameof(Dyes), CompanionPlayer.DyeSize);
        profile.Backpack = LoadItems(tag, nameof(Backpack), CompanionPlayer.BackpackSize);
        profile.Net = LoadItems(tag, nameof(Net), CompanionPlayer.NetSize);
        profile.Rod = LoadItems(tag, nameof(Rod), CompanionPlayer.RodSize);
        profile.Hook = LoadItems(tag, nameof(Hook), CompanionPlayer.HookSize);
        profile.Mount = LoadItems(tag, nameof(Mount), CompanionPlayer.MountSize);
        profile.MigrateReservedSlots();
        return profile;
    }

    private void CopyOwnerAppearance(Player owner)
    {
        Male = owner.Male;
        SkinVariant = owner.skinVariant;
        Hair = owner.hair;
        HairColor = owner.hairColor;
        SkinColor = owner.skinColor;
        EyeColor = owner.eyeColor;
        ShirtColor = owner.shirtColor;
        UnderShirtColor = owner.underShirtColor;
        PantsColor = owner.pantsColor;
        ShoeColor = owner.shoeColor;
    }

    private void RandomizeAppearance()
    {
        int variantCount = PlayerVariantID.Sets.Male.Length;
        SkinVariant = Main.rand.Next(variantCount);
        Male = PlayerVariantID.Sets.Male[SkinVariant];
        Hair = Main.rand.Next(System.Math.Max(1, HairLoader.Count));
        HairColor = RandomColor(35, 235);
        SkinColor = new Color(Main.rand.Next(90, 256), Main.rand.Next(55, 205), Main.rand.Next(35, 170));
        EyeColor = RandomColor(40, 240);
        ShirtColor = RandomColor(30, 230);
        UnderShirtColor = RandomColor(30, 230);
        PantsColor = RandomColor(30, 230);
        ShoeColor = RandomColor(25, 180);
    }

    private static Color RandomColor(int minimum, int maximum) =>
        new(Main.rand.Next(minimum, maximum + 1), Main.rand.Next(minimum, maximum + 1), Main.rand.Next(minimum, maximum + 1));

    private static Item[] CreateEmptyItems(int count)
    {
        Item[] items = new Item[count];
        for (int i = 0; i < count; i++)
            items[i] = new Item();
        return items;
    }

    private static List<TagCompound> SaveItems(Item[] items)
    {
        List<TagCompound> saved = new(items.Length);
        foreach (Item item in items)
            saved.Add(ItemIO.Save(item));
        return saved;
    }

    private static Item[] LoadItems(TagCompound tag, string key, int expectedSize)
    {
        Item[] result = CreateEmptyItems(expectedSize);
        if (!tag.ContainsKey(key))
            return result;

        IList<TagCompound> saved = tag.GetList<TagCompound>(key);
        for (int i = 0; i < System.Math.Min(expectedSize, saved.Count); i++)
            result[i] = ItemIO.Load(saved[i]);
        return result;
    }

    private static TagCompound SaveColor(Color color) => new()
    {
        ["R"] = (int)color.R,
        ["G"] = (int)color.G,
        ["B"] = (int)color.B
    };

    private static Color LoadColor(TagCompound tag, string key)
    {
        if (!tag.ContainsKey(key))
            return Color.White;
        TagCompound color = tag.GetCompound(key);
        return new Color(color.GetInt("R"), color.GetInt("G"), color.GetInt("B"));
    }
}
