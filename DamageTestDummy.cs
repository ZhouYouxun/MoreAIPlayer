using Microsoft.Xna.Framework;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;
using Terraria.ModLoader;

namespace MoreAIPlayer;

/// <summary>
/// A local, stripped-down adaptation of Calamity's open-source Super Dummy behavior.
/// It exists solely for consistent damage tests: no attacks, hit sounds, hit visuals,
/// death effects, or health bar can obscure the damage numbers the player is measuring.
/// </summary>
public sealed class DamageTestDummy : ModItem
{
    // The item deliberately looks like a vanilla bunny, matching the dummy it summons.
    public override string Texture => $"Terraria/Images/Item_{ItemID.Bunny}";

    public override void SetDefaults()
    {
        Item.width = 20;
        Item.height = 30;
        Item.useAnimation = Item.useTime = 15;
        Item.useStyle = ItemUseStyleID.Swing;
        Item.autoReuse = true;
        Item.useTurn = true;
        Item.value = 0;
        Item.rare = ItemRarityID.Blue;
    }

    public override bool AltFunctionUse(Player player) => true;

    public static void DeleteDummies()
    {
        foreach (NPC npc in Main.ActiveNPCs)
        {
            if (npc.type != ModContent.NPCType<DamageTestDummyNPC>())
                continue;

            npc.life = 0;
            npc.active = false;
        }
    }

    public override bool? UseItem(Player player)
    {
        // Like the rest of the companion system, this initial test tool is deliberately
        // single-player-only. A client-side NPC spawn needs a server packet before it can be
        // made safe for multiplayer, and a damage dummy must never create ghost NPCs.
        if (Main.netMode != NetmodeID.SinglePlayer || player.whoAmI != Main.myPlayer)
            return true;

        if (player.altFunctionUse == 2)
        {
            DeleteDummies();
            return true;
        }

        if (NPC.CountNPCS(ModContent.NPCType<DamageTestDummyNPC>()) < 50)
        {
            int x = (int)Main.MouseWorld.X - 9;
            int y = (int)Main.MouseWorld.Y - 20;
            NPC.NewNPC(new EntitySource_ItemUse(player, Item), x, y, ModContent.NPCType<DamageTestDummyNPC>());
        }
        return true;
    }

    public override void AddRecipes()
    {
        // A cheap but deliberate early-game craft: the hay makes it visually read as a rabbit
        // dummy while the wood/work bench prevents it from being an accidental pocket recipe.
        CreateRecipe()
            .AddIngredient(ItemID.Hay, 10)
            .AddIngredient(ItemID.Wood, 10)
            .AddTile(TileID.WorkBenches)
            .Register();

        // Both conversion recipes are intentionally station-free: turn a captured bunny or an
        // already-owned vanilla target dummy into this testing variant anywhere.
        CreateRecipe()
            .AddIngredient(ItemID.Bunny)
            .Register();

        CreateRecipe()
            .AddIngredient(ItemID.TargetDummy)
            .Register();
    }
}

/// <summary>
/// A rabbit-skinned dummy that falls naturally until terrain or a platform catches it, then has
/// no AI movement. Health recovery follows the Super Dummy model so normal weapon tests can
/// continue indefinitely while Terraria still shows its ordinary damage text.
/// </summary>
public sealed class DamageTestDummyNPC : ModNPC
{
    /// <summary>Total real damage dealt to this specific dummy during its current lifetime.</summary>
    private long totalDamageTaken;

    public override string Texture => $"Terraria/Images/NPC_{NPCID.Bunny}";

    public override void SetStaticDefaults()
    {
        Main.npcFrameCount[Type] = 7;
        NPCID.Sets.CantTakeLunchMoney[Type] = true;
    }

    public override void SetDefaults()
    {
        NPC.width = 18;
        NPC.height = 40;
        NPC.damage = 0;
        NPC.defense = 0;
        NPC.lifeMax = 9_999_999;
        NPC.HitSound = null;
        NPC.DeathSound = null;
        NPC.knockBackResist = 0f;
        NPC.noGravity = false;
        NPC.noTileCollide = false;
        NPC.aiStyle = -1;
        NPC.netAlways = true;
    }

    public override void FindFrame(int frameHeight)
    {
        // Bunny sprites have seven frames. With no vanilla bunny AI running, advance these
        // ourselves so the rabbit skin is displayed as the complete animated sprite sheet.
        NPC.frameCounter += 0.12d;
        int frame = (int)NPC.frameCounter % Main.npcFrameCount[Type];
        NPC.frame.Y = frame * frameHeight;
    }

    public override void UpdateLifeRegen(ref int damage)
    {
        if (NPC.lifeRegen >= 0)
            NPC.lifeRegen += 2_000_000;
    }

    public override bool CanHitPlayer(Player target, ref int cooldownSlot) => false;

    public override bool? DrawHealthBar(byte hbPosition, ref float scale, ref Vector2 position) => false;

    public override void OnHitByItem(Player player, Item item, NPC.HitInfo hit, int damageDone) =>
        AddRecordedDamage(damageDone);

    public override void OnHitByProjectile(Projectile projectile, NPC.HitInfo hit, int damageDone) =>
        AddRecordedDamage(damageDone);

    public override void PostDraw(Microsoft.Xna.Framework.Graphics.SpriteBatch spriteBatch, Vector2 screenPos, Color drawColor)
    {
        // Deliberately show only the number: this is the dummy's running total, not a DPS meter
        // or a combat popup. Every newly placed rabbit begins at zero.
        string text = totalDamageTaken.ToString();
        var font = Terraria.GameContent.FontAssets.MouseText.Value;
        Vector2 size = font.MeasureString(text) * 0.9f;
        Vector2 position = NPC.Top - screenPos - new Vector2(size.X * 0.5f, 24f);
        Terraria.Utils.DrawBorderString(spriteBatch, text, position, Color.White, 0.9f);
    }

    // Intentionally no HitEffect: no dust, recoil animation, sound, or other feedback is
    // created here. Terraria's normal combat text remains visible for damage testing.

    public override bool CheckDead()
    {
        if (NPC.lifeRegen < 0)
        {
            NPC.life = NPC.lifeMax;
            return false;
        }
        return true;
    }

    private void AddRecordedDamage(int damageDone)
    {
        if (damageDone <= 0)
            return;

        totalDamageTaken = System.Math.Min(long.MaxValue - damageDone, totalDamageTaken) + damageDone;
    }
}

/// <summary>Sells the test dummy from the ordinary Merchant for 20 silver.</summary>
public sealed class DamageTestDummyShop : GlobalNPC
{
    public override void ModifyShop(NPCShop shop)
    {
        if (shop.NpcType != NPCID.Merchant)
            return;

        shop.Add(new Item(ModContent.ItemType<DamageTestDummy>())
        {
            shopCustomPrice = Item.buyPrice(silver: 20)
        });
    }
}
