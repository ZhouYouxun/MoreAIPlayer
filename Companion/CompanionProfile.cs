using System.Collections.Generic;
using Microsoft.Xna.Framework;
using MoreAIPlayer.Companion.Automation;
using MoreAIPlayer.Companion.Commands;
using Terraria;
using Terraria.ID;
using Terraria.Localization;
using Terraria.ModLoader;
using Terraria.ModLoader.IO;

namespace MoreAIPlayer.Companion;

public sealed class CompanionProfile
{
    private const int CurrentHotbarLayoutVersion = 2;

    public enum AppearanceColor
    {
        Hair,
        Skin,
        Eyes,
        Shirt,
        UnderShirt,
        Pants,
        Shoes
    }

    public int Id { get; private set; }
    /// <summary>Stable identity of the Phantom Staff carrying this profile. The staff number is
    /// deliberately not stored here because numbering belongs to the current owner and changes
    /// when the staff is transferred.</summary>
    public string PhantomStaffIdentity { get; private set; } = string.Empty;
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

    /// <summary>Three ordinary attack weapons, checked from left to right.</summary>
    public const int FirstCombatSlot = 0;

    /// <summary>Dedicated minion and sentry staff slots.</summary>
    public const int SummonSlot = 3;

    public const int SentrySlot = 4;

    /// <summary>Dedicated tool and quick-potion slots.</summary>
    public const int PickSlot = 5;
    public const int AxeSlot = 6;
    public const int HammerSlot = 7;
    public const int HealingPotionSlot = 8;
    public const int ManaPotionSlot = 9;

    /// <summary>One past the last hotbar index the normal weapon rotation may draw from.</summary>
    public const int EndCombatSlot = 3;

    public Item[] Hotbar { get; private set; }
    public Item[] Ammo { get; private set; }
    public Item[] Armor { get; private set; }
    public Item[] Accessories { get; private set; }
    public Item[] ExtraAccessories { get; private set; }
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
    /// Keeping a Guide to Critter Companionship in this profile's backpack permanently prevents
    /// this companion from harming catchable critters, regardless of its selected work mode.
    /// </summary>
    public bool HasCritterCompanionshipGuide
    {
        get
        {
            foreach (Item item in Backpack)
            {
                if (item is not null && !item.IsAir && item.type == ItemID.DontHurtCrittersBook)
                    return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Air items handed to the entity in place of accessories the world has not unlocked yet.
    /// A shared singleton would be dangerous — vanilla writes through equipped Item references
    /// (prefixes, favorited flags) — so each locked slot gets its own instance.
    /// </summary>
    private Item[] lockedAccessoryFiller;

    public int ActiveWeaponSlot = -1;

    /// <summary>Behaviour mode cycled by the command book. Persists with the profile.</summary>
    public CompanionMode Mode = CompanionMode.Attack;

    // New wheel shell. The original Mode above deliberately remains persisted and usable by the
    // books; these fields remember the richer command vocabulary without replacing it.
    public CompanionWorkMode WorkMode = CompanionWorkMode.Combat;
    public IdleCommand IdleCommand = IdleCommand.Follow;
    public CombatCommand CombatCommand = CombatCommand.SelfDefense;
    public ExplorationCommand ExplorationCommand = ExplorationCommand.Follow;
    public BuildingCommand BuildingCommand = BuildingCommand.Platform;
    public FarmingCommand FarmingCommand = FarmingCommand.TreeFarm;
    public FishingCommand FishingCommand = FishingCommand.Fish;
    public CapturingCommand CapturingCommand = CapturingCommand.FixedArea;
    public bool ExplorationLighting = true;
    public bool ExplorationHazardRemoval;
    public bool ExplorationGathering;
    public bool CapturingBreakDebris;
    public bool WheelCommandsActive;

    /// <summary>Persistent source identity. This is intentionally not derived from whether the
    /// automation brain happens to be running right now.</summary>
    public CompanionOrigin Origin { get; private set; } = CompanionOrigin.OrdinaryDebug;

    /// <summary>Non-null only while this profile is (or once was) the world's automation companion.
    /// See <see cref="AutoCompanionState"/>.</summary>
    public AutoCompanionState AutoState { get; private set; }

    /// <summary>True while the automation flow owns this profile. Every normal-companion system
    /// (command book, build tactics, squad spread, rescue, manager panel) filters on this so an
    /// automation companion never joins them until it converts after Moon Lord.</summary>
    public bool IsAutoRunning => AutoState is not null && AutoState.Running;

    /// <summary>Turns a freshly created profile into the world's automation companion: marks the
    /// automation state active and hands it only the two starting tools the design allows.</summary>
    public void BeginAutomation()
    {
        Origin = CompanionOrigin.Automation;
        AutoState = new AutoCompanionState();
        GiveInitialAutomationTools();
    }

    /// <summary>Clears the automation identity when the run finishes (Moon Lord). Keeps every item,
    /// the name and the appearance so a perfectly ordinary companion profile remains.</summary>
    public void EndAutomation()
    {
        if (AutoState is not null)
            AutoState.Running = false;
    }

    /// <summary>
    /// The only items an automation companion starts with: a Copper Pickaxe in the reserved pick
    /// slot and a Copper Axe in the first normal weapon slot. Everything else must be gathered,
    /// dropped, looted, fished, bought or legally crafted.
    /// </summary>
    private void GiveInitialAutomationTools()
    {
        Item pick = new();
        pick.SetDefaults(ItemID.CopperPickaxe);
        Hotbar[PickSlot] = pick;

        Item axe = new();
        axe.SetDefaults(ItemID.CopperAxe);
        Hotbar[AxeSlot] = axe;
    }

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

    public void BindToPhantomStaff(string identity)
    {
        if (!string.IsNullOrWhiteSpace(identity))
            PhantomStaffIdentity = identity;
    }

    private CompanionProfile(int id)
    {
        Id = id;
        Hotbar = CreateEmptyItems(CompanionPlayer.HotbarSize);
        Ammo = CreateEmptyItems(CompanionPlayer.AmmoSize);
        Armor = CreateEmptyItems(CompanionPlayer.ArmorSize);
        Accessories = CreateEmptyItems(CompanionPlayer.AccessorySize);
        ExtraAccessories = CreateEmptyItems(CompanionPlayer.ExtraAccessorySize);
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

    /// <summary>Whatever the player parked in <see cref="SummonSlot"/>; air when they left it empty.</summary>
    public Item SummonWeapon => Hotbar[SummonSlot];

    /// <summary>Whatever the player parked in <see cref="SentrySlot"/>; air when they left it empty.</summary>
    public Item SentryWeapon => Hotbar[SentrySlot];

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

    public Item AxeTool => Hotbar[AxeSlot];
    public Item HammerTool => Hotbar[HammerSlot];
    public Item PreferredHealingPotion => Hotbar[HealingPotionSlot];
    public Item PreferredManaPotion => Hotbar[ManaPotionSlot];

    /// <summary>
    /// The accessory to hand the entity for a given slot: the real item while the world has
    /// that slot unlocked, and a private air item once it is past the world's limit, so a
    /// locked accessory stops applying its effects without the player losing it.
    /// </summary>
    public Item AccessoryForSlot(int index, int unlockedCount) =>
        index < unlockedCount ? Accessories[index] : lockedAccessoryFiller[index];

    /// <summary>Returns a companion-only extra accessory. Activation is controlled by the client slider.</summary>
    public Item ExtraAccessoryForSlot(int index) => ExtraAccessories[index];

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
    private void MigrateLegacyHotbarLayout()
    {
        Item[] legacy = Hotbar;
        Hotbar = CreateEmptyItems(CompanionPlayer.HotbarSize);
        foreach (Item item in legacy)
        {
            if (item is null || item.IsAir)
                continue;

            int preferred = CompanionWeaponBehaviors.IsMinionStaff(item) ? SummonSlot
                : CompanionWeaponBehaviors.IsSentryStaff(item) ? SentrySlot
                : CompanionSlotRules.IsHealingPotion(item) ? HealingPotionSlot
                : CompanionSlotRules.IsManaPotion(item) ? ManaPotionSlot
                : item.pick > 0 ? PickSlot
                : item.axe > 0 ? AxeSlot
                : item.hammer > 0 ? HammerSlot
                : CompanionSlotRules.IsRegularWeapon(item) ? FirstFreeCombatSlot()
                : -1;

            if (preferred >= 0 && Hotbar[preferred].IsAir)
            {
                Hotbar[preferred] = item;
                continue;
            }

            Item moving = item.Clone();
            if (TryAddToBackpack(moving))
                continue;

            // A full backpack must never turn a layout migration into item loss.
            for (int fallback = 0; fallback < Hotbar.Length; fallback++)
            {
                if (!Hotbar[fallback].IsAir)
                    continue;
                Hotbar[fallback] = item;
                break;
            }
        }

        int FirstFreeCombatSlot()
        {
            for (int slot = FirstCombatSlot; slot < EndCombatSlot; slot++)
                if (Hotbar[slot].IsAir)
                    return slot;
            return -1;
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

    /// <summary>
    /// Commits an edit made in the companion dresser. A custom look deliberately stops mirroring
    /// the owner: otherwise the next player update would immediately overwrite the user's edit.
    /// </summary>
    public void SetSkinVariant(int skinVariant)
    {
        int variantCount = System.Math.Max(1, PlayerVariantID.Sets.Male.Length);
        SkinVariant = ((skinVariant % variantCount) + variantCount) % variantCount;
        Male = PlayerVariantID.Sets.Male[SkinVariant];
        MirrorsOwnerAppearance = false;
    }

    public void SetHair(int hair)
    {
        int hairCount = System.Math.Max(1, HairLoader.Count);
        Hair = ((hair % hairCount) + hairCount) % hairCount;
        MirrorsOwnerAppearance = false;
    }

    public void SetAppearanceColor(AppearanceColor channel, Color color)
    {
        switch (channel)
        {
            case AppearanceColor.Hair: HairColor = color; break;
            case AppearanceColor.Skin: SkinColor = color; break;
            case AppearanceColor.Eyes: EyeColor = color; break;
            case AppearanceColor.Shirt: ShirtColor = color; break;
            case AppearanceColor.UnderShirt: UnderShirtColor = color; break;
            case AppearanceColor.Pants: PantsColor = color; break;
            case AppearanceColor.Shoes: ShoeColor = color; break;
        }
        MirrorsOwnerAppearance = false;
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
        TagCompound tag = new()
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
            [nameof(WorkMode)] = (int)WorkMode,
            [nameof(IdleCommand)] = (int)IdleCommand,
            [nameof(CombatCommand)] = (int)CombatCommand,
            [nameof(PhantomStaffIdentity)] = PhantomStaffIdentity,
            [nameof(ExplorationCommand)] = (int)ExplorationCommand,
            [nameof(BuildingCommand)] = (int)BuildingCommand,
            [nameof(FarmingCommand)] = (int)FarmingCommand,
            [nameof(FishingCommand)] = (int)FishingCommand,
            [nameof(CapturingCommand)] = (int)CapturingCommand,
            [nameof(ExplorationLighting)] = ExplorationLighting,
            [nameof(ExplorationHazardRemoval)] = ExplorationHazardRemoval,
            [nameof(ExplorationGathering)] = ExplorationGathering,
            [nameof(CapturingBreakDebris)] = CapturingBreakDebris,
            [nameof(WheelCommandsActive)] = WheelCommandsActive,
            [nameof(Origin)] = (int)Origin,
            ["HotbarLayoutVersion"] = CurrentHotbarLayoutVersion,
            ["PreferAltUseWeapons"] = new List<string>(preferAltUseWeapons),
            [nameof(Hotbar)] = SaveItems(Hotbar),
            [nameof(Ammo)] = SaveItems(Ammo),
            [nameof(Armor)] = SaveItems(Armor),
            [nameof(Accessories)] = SaveItems(Accessories),
            [nameof(ExtraAccessories)] = SaveItems(ExtraAccessories),
            [nameof(Vanity)] = SaveItems(Vanity),
            [nameof(Dyes)] = SaveItems(Dyes),
            [nameof(Backpack)] = SaveItems(Backpack),
            [nameof(Net)] = SaveItems(Net),
            [nameof(Rod)] = SaveItems(Rod),
            [nameof(Hook)] = SaveItems(Hook),
            [nameof(Mount)] = SaveItems(Mount)
        };
        if (AutoState is not null)
            tag[nameof(AutoState)] = AutoState.Save();
        return tag;
    }

    public static CompanionProfile Load(TagCompound tag)
    {
        int id = tag.GetInt(nameof(Id));
        CompanionProfile profile = new(id)
        {
            PhantomStaffIdentity = tag.ContainsKey(nameof(PhantomStaffIdentity))
                ? tag.GetString(nameof(PhantomStaffIdentity))
                : string.Empty,
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
            Hotbar = LoadItems(tag, nameof(Hotbar), CompanionPlayer.HotbarSize, id),
            Ammo = LoadItems(tag, nameof(Ammo), CompanionPlayer.AmmoSize, id),
            Armor = LoadItems(tag, nameof(Armor), CompanionPlayer.ArmorSize, id),
            Accessories = LoadItems(tag, nameof(Accessories), CompanionPlayer.AccessorySize, id),
            ExtraAccessories = LoadItems(tag, nameof(ExtraAccessories), CompanionPlayer.ExtraAccessorySize, id),
            Vanity = LoadItems(tag, nameof(Vanity), CompanionPlayer.VanitySize, id),
            Dyes = LoadItems(tag, nameof(Dyes), CompanionPlayer.DyeSize, id),
            Backpack = LoadItems(tag, nameof(Backpack), CompanionPlayer.BackpackSize, id),
            Net = LoadItems(tag, nameof(Net), CompanionPlayer.NetSize, id),
            Rod = LoadItems(tag, nameof(Rod), CompanionPlayer.RodSize, id),
            Hook = LoadItems(tag, nameof(Hook), CompanionPlayer.HookSize, id),
            Mount = LoadItems(tag, nameof(Mount), CompanionPlayer.MountSize, id)
        };
        profile.RogueStealthWait = !tag.ContainsKey(nameof(RogueStealthWait)) || tag.GetBool(nameof(RogueStealthWait));
        if (tag.ContainsKey(nameof(Mode)))
            profile.Mode = (CompanionMode)tag.GetInt(nameof(Mode));
        if (tag.ContainsKey(nameof(WorkMode))) profile.WorkMode = (CompanionWorkMode)tag.GetInt(nameof(WorkMode));
        if (tag.ContainsKey(nameof(IdleCommand))) profile.IdleCommand = (IdleCommand)tag.GetInt(nameof(IdleCommand));
        if (tag.ContainsKey(nameof(CombatCommand))) profile.CombatCommand = (CombatCommand)tag.GetInt(nameof(CombatCommand));
        if (tag.ContainsKey(nameof(ExplorationCommand))) profile.ExplorationCommand = (ExplorationCommand)tag.GetInt(nameof(ExplorationCommand));
        if (tag.ContainsKey(nameof(BuildingCommand))) profile.BuildingCommand = (BuildingCommand)tag.GetInt(nameof(BuildingCommand));
        if (tag.ContainsKey(nameof(FarmingCommand))) profile.FarmingCommand = (FarmingCommand)tag.GetInt(nameof(FarmingCommand));
        if (tag.ContainsKey(nameof(FishingCommand))) profile.FishingCommand = (FishingCommand)tag.GetInt(nameof(FishingCommand));
        if (tag.ContainsKey(nameof(CapturingCommand))) profile.CapturingCommand = (CapturingCommand)tag.GetInt(nameof(CapturingCommand));
        profile.ExplorationLighting = !tag.ContainsKey(nameof(ExplorationLighting)) || tag.GetBool(nameof(ExplorationLighting));
        profile.ExplorationHazardRemoval = tag.ContainsKey(nameof(ExplorationHazardRemoval)) && tag.GetBool(nameof(ExplorationHazardRemoval));
        profile.ExplorationGathering = tag.ContainsKey(nameof(ExplorationGathering)) && tag.GetBool(nameof(ExplorationGathering));
        profile.CapturingBreakDebris = tag.ContainsKey(nameof(CapturingBreakDebris)) && tag.GetBool(nameof(CapturingBreakDebris));
        profile.WheelCommandsActive = tag.ContainsKey(nameof(WheelCommandsActive)) && tag.GetBool(nameof(WheelCommandsActive));
        if (tag.ContainsKey("PreferAltUseWeapons"))
        {
            foreach (string key in tag.GetList<string>("PreferAltUseWeapons"))
                profile.preferAltUseWeapons.Add(key);
        }
        if (tag.ContainsKey(nameof(AutoState)))
            profile.AutoState = AutoCompanionState.Load(tag.GetCompound(nameof(AutoState)));
        int savedOrigin = tag.ContainsKey(nameof(Origin)) ? tag.GetInt(nameof(Origin)) : -1;
        if (savedOrigin >= (int)CompanionOrigin.OrdinaryDebug &&
            savedOrigin <= (int)CompanionOrigin.Automation)
        {
            profile.Origin = (CompanionOrigin)savedOrigin;
        }
        else
        {
            // Migration contract: all old profiles carrying automation history stay Automation;
            // every other legacy/debug-book profile becomes OrdinaryDebug.
            profile.Origin = profile.AutoState is null
                ? CompanionOrigin.OrdinaryDebug
                : CompanionOrigin.Automation;
        }
        if (!tag.ContainsKey("HotbarLayoutVersion") || tag.GetInt("HotbarLayoutVersion") < CurrentHotbarLayoutVersion)
            profile.MigrateLegacyHotbarLayout();
        return profile;
    }

    public static CompanionProfile LoadLegacy(int id, Player owner, TagCompound tag)
    {
        CompanionProfile profile = Create(id, owner, true);
        profile.Hotbar = LoadItems(tag, nameof(Hotbar), CompanionPlayer.HotbarSize, id);
        profile.Ammo = LoadItems(tag, nameof(Ammo), CompanionPlayer.AmmoSize, id);
        profile.Armor = LoadItems(tag, nameof(Armor), CompanionPlayer.ArmorSize, id);
        profile.Accessories = LoadItems(tag, nameof(Accessories), CompanionPlayer.AccessorySize, id);
        profile.ExtraAccessories = LoadItems(tag, nameof(ExtraAccessories), CompanionPlayer.ExtraAccessorySize, id);
        profile.Vanity = LoadItems(tag, nameof(Vanity), CompanionPlayer.VanitySize, id);
        profile.Dyes = LoadItems(tag, nameof(Dyes), CompanionPlayer.DyeSize, id);
        profile.Backpack = LoadItems(tag, nameof(Backpack), CompanionPlayer.BackpackSize, id);
        profile.Net = LoadItems(tag, nameof(Net), CompanionPlayer.NetSize, id);
        profile.Rod = LoadItems(tag, nameof(Rod), CompanionPlayer.RodSize, id);
        profile.Hook = LoadItems(tag, nameof(Hook), CompanionPlayer.HookSize, id);
        profile.Mount = LoadItems(tag, nameof(Mount), CompanionPlayer.MountSize, id);
        profile.MigrateLegacyHotbarLayout();
        return profile;
    }

    /// <summary>
    /// Last-resort loader for a profile whose metadata is malformed. Appearance and behaviour
    /// return to safe defaults, while every independently readable inventory section is retained.
    /// </summary>
    public static CompanionProfile RecoverInventory(int id, Player owner, TagCompound tag)
    {
        CompanionProfile profile = Create(id, owner, true);
        profile.Hotbar = LoadItems(tag, nameof(Hotbar), CompanionPlayer.HotbarSize, id);
        profile.Ammo = LoadItems(tag, nameof(Ammo), CompanionPlayer.AmmoSize, id);
        profile.Armor = LoadItems(tag, nameof(Armor), CompanionPlayer.ArmorSize, id);
        profile.Accessories = LoadItems(tag, nameof(Accessories), CompanionPlayer.AccessorySize, id);
        profile.ExtraAccessories = LoadItems(tag, nameof(ExtraAccessories), CompanionPlayer.ExtraAccessorySize, id);
        profile.Vanity = LoadItems(tag, nameof(Vanity), CompanionPlayer.VanitySize, id);
        profile.Dyes = LoadItems(tag, nameof(Dyes), CompanionPlayer.DyeSize, id);
        profile.Backpack = LoadItems(tag, nameof(Backpack), CompanionPlayer.BackpackSize, id);
        profile.Net = LoadItems(tag, nameof(Net), CompanionPlayer.NetSize, id);
        profile.Rod = LoadItems(tag, nameof(Rod), CompanionPlayer.RodSize, id);
        profile.Hook = LoadItems(tag, nameof(Hook), CompanionPlayer.HookSize, id);
        profile.Mount = LoadItems(tag, nameof(Mount), CompanionPlayer.MountSize, id);

        try
        {
            if (!tag.ContainsKey("HotbarLayoutVersion") || tag.GetInt("HotbarLayoutVersion") < CurrentHotbarLayoutVersion)
                profile.MigrateLegacyHotbarLayout();
        }
        catch (System.Exception exception)
        {
            CompanionFaultGuard.ReportRecovered($"profile {id}, hotbar migration", exception, true);
        }
        profile.RepairRuntimeState();
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

    private static Item[] LoadItems(TagCompound tag, string key, int expectedSize, int profileId)
    {
        Item[] result = CreateEmptyItems(expectedSize);
        if (!tag.ContainsKey(key))
            return result;

        IList<TagCompound> saved;
        try
        {
            saved = tag.GetList<TagCompound>(key);
        }
        catch (System.Exception exception)
        {
            CompanionFaultGuard.ReportRecovered($"profile {profileId}, {key} list", exception, true);
            return result;
        }

        for (int i = 0; i < System.Math.Min(expectedSize, saved.Count); i++)
        {
            try
            {
                Item loaded = ItemIO.Load(saved[i]);
                if (!IsRuntimeItemValid(loaded))
                    throw new System.IO.InvalidDataException($"Invalid item state (type={loaded?.type}, stack={loaded?.stack}).");
                result[i] = loaded;
            }
            catch (System.Exception exception)
            {
                // One unreadable modded item must not discard the rest of this companion or stop
                // the player file from loading. Its exact profile/list/index remains in the log.
                result[i] = new Item();
                CompanionFaultGuard.ReportRecovered($"profile {profileId}, {key}[{i}]", exception, true);
            }
        }
        return result;
    }

    /// <summary>
    /// Repairs only structural runtime damage (wrong array sizes, null/invalid item references).
    /// Valid unloaded-mod placeholders are ordinary tModLoader items and are deliberately kept.
    /// Returns the number of references replaced so callers can decide whether a rebind is needed.
    /// </summary>
    public int RepairRuntimeState()
    {
        int repairs = 0;
        Hotbar = RepairItems(Hotbar, CompanionPlayer.HotbarSize, ref repairs);
        Ammo = RepairItems(Ammo, CompanionPlayer.AmmoSize, ref repairs);
        Armor = RepairItems(Armor, CompanionPlayer.ArmorSize, ref repairs);
        Accessories = RepairItems(Accessories, CompanionPlayer.AccessorySize, ref repairs);
        ExtraAccessories = RepairItems(ExtraAccessories, CompanionPlayer.ExtraAccessorySize, ref repairs);
        Vanity = RepairItems(Vanity, CompanionPlayer.VanitySize, ref repairs);
        Dyes = RepairItems(Dyes, CompanionPlayer.DyeSize, ref repairs);
        Backpack = RepairItems(Backpack, CompanionPlayer.BackpackSize, ref repairs);
        Net = RepairItems(Net, CompanionPlayer.NetSize, ref repairs);
        Rod = RepairItems(Rod, CompanionPlayer.RodSize, ref repairs);
        Hook = RepairItems(Hook, CompanionPlayer.HookSize, ref repairs);
        Mount = RepairItems(Mount, CompanionPlayer.MountSize, ref repairs);
        lockedAccessoryFiller = RepairItems(lockedAccessoryFiller, CompanionPlayer.AccessorySize, ref repairs);
        if (ActiveWeaponSlot < -1 || ActiveWeaponSlot >= CompanionPlayer.HotbarSize)
        {
            ActiveWeaponSlot = -1;
            repairs++;
        }
        return repairs;
    }

    private static Item[] RepairItems(Item[] source, int expectedSize, ref int repairs)
    {
        if (source is null || source.Length != expectedSize)
        {
            Item[] resized = CreateEmptyItems(expectedSize);
            if (source is not null)
            {
                int copyCount = System.Math.Min(source.Length, expectedSize);
                for (int i = 0; i < copyCount; i++)
                    resized[i] = source[i];
            }
            source = resized;
            repairs++;
        }

        for (int i = 0; i < source.Length; i++)
        {
            if (IsRuntimeItemValid(source[i]))
                continue;
            source[i] = new Item();
            repairs++;
        }
        return source;
    }

    private static bool IsRuntimeItemValid(Item item) =>
        item is not null && item.type >= ItemID.None && item.type < ItemLoader.ItemCount && item.stack >= 0;

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
