using Terraria;
using Terraria.ID;

namespace MoreAIPlayer.Companion.Hunter;

/// <summary>
/// Curated Hunter Game kits. These never inspect the target's defence: world milestones choose
/// the available roster, while hunter count applies the explicit group-strength adjustment.
/// </summary>
public static class HunterGearPool
{
    public enum HunterRole
    {
        Ranger,
        Gunner,
        Raider
    }

    public struct HunterLoadout
    {
        public HunterRole Role;
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
        public int HealingPotionCount;
        public float DamageMultiplier;
        public float LifeMultiplier;
        public float PreferredRange;
    }

    public static HunterLoadout GenerateLoadout(HunterGameStage stage, int hunterIndex, int hunterCount)
    {
        bool latePreHardmode = NPC.downedBoss3 || NPC.downedQueenBee;
        bool postMechanicalBoss = NPC.downedMechBoss1 || NPC.downedMechBoss2 || NPC.downedMechBoss3;
        int kit = (hunterIndex + Main.rand.Next(3)) % 3;

        HunterLoadout loadout = stage == HunterGameStage.EarlyHardmode
            ? CreateEarlyHardmodeLoadout(kit, postMechanicalBoss)
            : CreatePreHardmodeLoadout(kit, latePreHardmode);

        ApplyGroupTuning(ref loadout, stage, hunterCount);
        return loadout;
    }

    private static HunterLoadout CreatePreHardmodeLoadout(int kit, bool latePreHardmode)
    {
        HunterLoadout loadout = new()
        {
            BootAccessory = ItemID.HermesBoots,
            WingOrJumpAccessory = ItemID.CloudinaBottle,
            ShieldAccessory = ItemID.ObsidianShield,
            HealingPotionType = ItemID.HealingPotion,
            AmmoType = ItemID.FrostburnArrow,
            PreferredRange = 270f
        };

        if (!latePreHardmode)
        {
            switch (kit)
            {
                case 0:
                    loadout.Role = HunterRole.Ranger;
                    loadout.PrimaryWeapon = ItemID.GoldBow;
                    loadout.SecondaryWeapon = ItemID.GoldBroadsword;
                    loadout.HeadArmor = ItemID.GoldHelmet;
                    loadout.BodyArmor = ItemID.GoldChainmail;
                    loadout.LegArmor = ItemID.GoldGreaves;
                    break;
                case 1:
                    loadout.Role = HunterRole.Gunner;
                    loadout.PrimaryWeapon = ItemID.Musket;
                    loadout.SecondaryWeapon = ItemID.SilverBroadsword;
                    loadout.HeadArmor = ItemID.SilverHelmet;
                    loadout.BodyArmor = ItemID.SilverChainmail;
                    loadout.LegArmor = ItemID.SilverGreaves;
                    loadout.AmmoType = ItemID.MusketBall;
                    loadout.PreferredRange = 310f;
                    break;
                default:
                    loadout.Role = HunterRole.Raider;
                    loadout.PrimaryWeapon = ItemID.EnchantedBoomerang;
                    loadout.SecondaryWeapon = ItemID.GoldBroadsword;
                    loadout.HeadArmor = ItemID.GoldHelmet;
                    loadout.BodyArmor = ItemID.GoldChainmail;
                    loadout.LegArmor = ItemID.GoldGreaves;
                    loadout.PreferredRange = 190f;
                    break;
            }

            return loadout;
        }

        switch (kit)
        {
            case 0:
                loadout.Role = HunterRole.Ranger;
                loadout.PrimaryWeapon = ItemID.Boomstick;
                loadout.SecondaryWeapon = ItemID.FieryGreatsword;
                loadout.HeadArmor = ItemID.NecroHelmet;
                loadout.BodyArmor = ItemID.NecroBreastplate;
                loadout.LegArmor = ItemID.NecroGreaves;
                loadout.AmmoType = ItemID.MeteorShot;
                loadout.PreferredRange = 220f;
                break;
            case 1:
                loadout.Role = HunterRole.Gunner;
                loadout.PrimaryWeapon = ItemID.Minishark;
                loadout.SecondaryWeapon = ItemID.Muramasa;
                loadout.HeadArmor = ItemID.MoltenHelmet;
                loadout.BodyArmor = ItemID.MoltenBreastplate;
                loadout.LegArmor = ItemID.MoltenGreaves;
                loadout.AmmoType = ItemID.MusketBall;
                loadout.PreferredRange = 330f;
                break;
            default:
                loadout.Role = HunterRole.Raider;
                loadout.PrimaryWeapon = ItemID.DemonBow;
                loadout.SecondaryWeapon = ItemID.BladeofGrass;
                loadout.HeadArmor = ItemID.ShadowHelmet;
                loadout.BodyArmor = ItemID.ShadowScalemail;
                loadout.LegArmor = ItemID.ShadowGreaves;
                loadout.PreferredRange = 250f;
                break;
        }

        return loadout;
    }

    private static HunterLoadout CreateEarlyHardmodeLoadout(int kit, bool postMechanicalBoss)
    {
        HunterLoadout loadout = new()
        {
            BootAccessory = ItemID.SpectreBoots,
            WingOrJumpAccessory = ItemID.AngelWings,
            ShieldAccessory = ItemID.CobaltShield,
            HealingPotionType = ItemID.HealingPotion,
            AmmoType = ItemID.SilverBullet,
            PreferredRange = 310f
        };

        if (postMechanicalBoss)
        {
            loadout.HeadArmor = ItemID.HallowedHelmet;
            loadout.BodyArmor = ItemID.HallowedPlateMail;
            loadout.LegArmor = ItemID.HallowedGreaves;
            loadout.HealingPotionType = ItemID.GreaterHealingPotion;
            loadout.AmmoType = ItemID.CrystalBullet;
        }
        else
        {
            loadout.HeadArmor = ItemID.TitaniumHelmet;
            loadout.BodyArmor = ItemID.TitaniumBreastplate;
            loadout.LegArmor = ItemID.TitaniumLeggings;
        }

        switch (kit)
        {
            case 0:
                loadout.Role = HunterRole.Ranger;
                loadout.PrimaryWeapon = ItemID.AdamantiteRepeater;
                loadout.SecondaryWeapon = ItemID.Excalibur;
                loadout.AmmoType = ItemID.FrostburnArrow;
                loadout.PreferredRange = 330f;
                break;
            case 1:
                loadout.Role = HunterRole.Gunner;
                loadout.PrimaryWeapon = postMechanicalBoss ? ItemID.Megashark : ItemID.ClockworkAssaultRifle;
                loadout.SecondaryWeapon = ItemID.DaoofPow;
                loadout.PreferredRange = 360f;
                break;
            default:
                loadout.Role = HunterRole.Raider;
                loadout.PrimaryWeapon = ItemID.Shotgun;
                loadout.SecondaryWeapon = ItemID.Excalibur;
                loadout.PreferredRange = 230f;
                break;
        }

        return loadout;
    }

    private static void ApplyGroupTuning(ref HunterLoadout loadout, HunterGameStage stage, int hunterCount)
    {
        hunterCount = System.Math.Clamp(hunterCount, 1, 3);
        float stageDamage = stage == HunterGameStage.EarlyHardmode ? 0.55f : 0.72f;
        float stageLife = stage == HunterGameStage.EarlyHardmode ? 0.95f : 0.82f;
        float groupMultiplier = hunterCount switch
        {
            1 => 1f,
            2 => 0.76f,
            _ => 0.58f
        };

        loadout.DamageMultiplier = stageDamage * groupMultiplier;
        loadout.LifeMultiplier = stageLife * groupMultiplier;
        loadout.HealingPotionCount = hunterCount switch
        {
            1 => 5,
            2 => 3,
            _ => 1
        };
    }

    public static void ApplyLoadout(Player hunter, HunterLoadout loadout)
    {
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

        hunter.inventory[0] = CreateScaledWeapon(loadout.PrimaryWeapon, loadout.DamageMultiplier);
        hunter.inventory[1] = CreateScaledWeapon(loadout.SecondaryWeapon, loadout.DamageMultiplier);
        hunter.armor[0] = new Item(loadout.HeadArmor);
        hunter.armor[1] = new Item(loadout.BodyArmor);
        hunter.armor[2] = new Item(loadout.LegArmor);
        hunter.armor[3] = new Item(loadout.BootAccessory);
        hunter.armor[4] = new Item(loadout.WingOrJumpAccessory);
        hunter.armor[5] = new Item(loadout.ShieldAccessory);
        hunter.inventory[54] = new Item(loadout.AmmoType, 999);
        hunter.inventory[10] = new Item(loadout.HealingPotionType, loadout.HealingPotionCount);
    }

    private static Item CreateScaledWeapon(int itemType, float multiplier)
    {
        Item weapon = new(itemType);
        weapon.damage = System.Math.Max(1, (int)System.MathF.Round(weapon.damage * multiplier));
        return weapon;
    }
}
