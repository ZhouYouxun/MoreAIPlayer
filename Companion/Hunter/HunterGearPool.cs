using Terraria;
using Terraria.ID;

namespace MoreAIPlayer.Companion.Hunter;

public static class HunterGearPool
{
    public struct HunterLoadout
    {
        public int PrimaryWeapon;
        public int SecondaryWeapon;
        public int HeadArmor;
        public int BodyArmor;
        public int LegArmor;
        public int BootAccessory;
        public int WingOrJumpAccessory;
        public int ShieldAccessory;
        public int AmmoType;
        public int HealingPotionType;
    }

    public static HunterLoadout GenerateLoadout(Player player)
    {
        bool postMoonLord = NPC.downedMoonlord;
        bool postPlantera = NPC.downedPlantBoss;
        bool hardMode = Main.hardMode;
        bool postSkeletron = NPC.downedBoss3;

        HunterLoadout loadout = new()
        {
            HealingPotionType = ItemID.LesserHealingPotion,
            AmmoType = ItemID.MusketBall
        };

        if (postMoonLord || player.statDefense >= 70)
        {
            // Endgame tier
            loadout.PrimaryWeapon = ItemID.TerraBlade;
            loadout.SecondaryWeapon = ItemID.Megashark;
            loadout.HeadArmor = ItemID.SolarFlareHelmet;
            loadout.BodyArmor = ItemID.SolarFlareBreastplate;
            loadout.LegArmor = ItemID.SolarFlareLeggings;
            loadout.BootAccessory = ItemID.TerrasparkBoots;
            loadout.WingOrJumpAccessory = ItemID.SteampunkWings;
            loadout.ShieldAccessory = ItemID.AnkhShield;
            loadout.HealingPotionType = ItemID.GreaterHealingPotion;
            loadout.AmmoType = ItemID.HighVelocityBullet;
        }
        else if (postPlantera || player.statDefense >= 45)
        {
            // Post-Plantera / Late Hardmode
            loadout.PrimaryWeapon = ItemID.TrueNightsEdge;
            loadout.SecondaryWeapon = ItemID.Megashark;
            loadout.HeadArmor = ItemID.HallowedMask;
            loadout.BodyArmor = ItemID.HallowedPlateMail;
            loadout.LegArmor = ItemID.HallowedGreaves;
            loadout.BootAccessory = ItemID.LightningBoots;
            loadout.WingOrJumpAccessory = ItemID.DemonWings;
            loadout.ShieldAccessory = ItemID.ObsidianShield;
            loadout.HealingPotionType = ItemID.GreaterHealingPotion;
            loadout.AmmoType = ItemID.IchorBullet;
        }
        else if (hardMode || player.statDefense >= 25)
        {
            // Early Hardmode
            loadout.PrimaryWeapon = ItemID.Excalibur;
            loadout.SecondaryWeapon = ItemID.ClockworkAssaultRifle;
            loadout.HeadArmor = ItemID.TitaniumHelmet;
            loadout.BodyArmor = ItemID.TitaniumBreastplate;
            loadout.LegArmor = ItemID.TitaniumLeggings;
            loadout.BootAccessory = ItemID.SpectreBoots;
            loadout.WingOrJumpAccessory = ItemID.AngelWings;
            loadout.ShieldAccessory = ItemID.CobaltShield;
            loadout.HealingPotionType = ItemID.HealingPotion;
            loadout.AmmoType = ItemID.SilverBullet;
        }
        else if (postSkeletron || player.statDefense >= 15)
        {
            // Post-Skeletron / Pre-Hardmode Endgame
            loadout.PrimaryWeapon = ItemID.FieryGreatsword;
            loadout.SecondaryWeapon = ItemID.Minishark;
            loadout.HeadArmor = ItemID.MoltenHelmet;
            loadout.BodyArmor = ItemID.MoltenBreastplate;
            loadout.LegArmor = ItemID.MoltenGreaves;
            loadout.BootAccessory = ItemID.HermesBoots;
            loadout.WingOrJumpAccessory = ItemID.CloudinaBottle;
            loadout.ShieldAccessory = ItemID.ObsidianSkull;
            loadout.HealingPotionType = ItemID.HealingPotion;
            loadout.AmmoType = ItemID.MusketBall;
        }
        else
        {
            // Early Game
            loadout.PrimaryWeapon = ItemID.GoldBroadsword;
            loadout.SecondaryWeapon = ItemID.GoldBow;
            loadout.HeadArmor = ItemID.GoldHelmet;
            loadout.BodyArmor = ItemID.GoldChainmail;
            loadout.LegArmor = ItemID.GoldGreaves;
            loadout.BootAccessory = ItemID.HermesBoots;
            loadout.WingOrJumpAccessory = ItemID.CloudinaBottle;
            loadout.ShieldAccessory = ItemID.Shackle;
            loadout.HealingPotionType = ItemID.LesserHealingPotion;
            loadout.AmmoType = ItemID.WoodenArrow;
        }

        return loadout;
    }

    public static void ApplyLoadout(Player hunter, HunterLoadout loadout)
    {
        // Ensure non-null items in inventory/armor/dye arrays
        if (hunter.inventory is not null)
        {
            for (int i = 0; i < hunter.inventory.Length; i++)
                hunter.inventory[i] ??= new Item();
        }
        if (hunter.armor is not null)
        {
            for (int i = 0; i < hunter.armor.Length; i++)
                hunter.armor[i] ??= new Item();
        }
        if (hunter.dye is not null)
        {
            for (int i = 0; i < hunter.dye.Length; i++)
                hunter.dye[i] ??= new Item();
        }

        // Primary weapon in hotbar 0
        hunter.inventory[0] = new Item(loadout.PrimaryWeapon);
        hunter.inventory[1] = new Item(loadout.SecondaryWeapon);

        // Armor
        hunter.armor[0] = new Item(loadout.HeadArmor);
        hunter.armor[1] = new Item(loadout.BodyArmor);
        hunter.armor[2] = new Item(loadout.LegArmor);

        // Accessories
        hunter.armor[3] = new Item(loadout.BootAccessory);
        hunter.armor[4] = new Item(loadout.WingOrJumpAccessory);
        hunter.armor[5] = new Item(loadout.ShieldAccessory);

        // Ammo
        hunter.inventory[54] = new Item(loadout.AmmoType, 999);

        // Potions
        hunter.inventory[10] = new Item(loadout.HealingPotionType, 30);
    }
}
