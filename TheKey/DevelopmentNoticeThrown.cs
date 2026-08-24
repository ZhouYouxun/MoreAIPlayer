using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.Audio;
using Terraria.GameContent;
using Terraria.ID;
using Terraria.ModLoader;

namespace MoreAIPlayer;

/// <summary>
/// Right-click key throw with a bounded sprite outline. It deliberately has no blade trail or
/// history-based drawing, so no visual segment can stretch across the screen.
/// </summary>
public sealed class DevelopmentNoticeThrown : ModProjectile
{
    private const float SpinSpeed = 0.56f;
    private const float Gravity = 0.18f;
    private const float MaximumFallSpeed = 10f;
    private const int MaximumBounces = 3;
    private const int BounceTimePenalty = 20;

    public override string Texture => $"Terraria/Images/Item_{ItemID.Keybrand}";

    private float SpinDirection => Projectile.ai[0] < 0f ? -1f : 1f;
    private int BounceCount => (int)Projectile.ai[1];

    public override void SetStaticDefaults() =>
        ProjectileID.Sets.AllowsContactDamageFromJellyfish[Type] = true;

    public override void SetDefaults()
    {
        Projectile.width = 30;
        Projectile.height = 30;
        Projectile.friendly = true;
        Projectile.DamageType = DamageClass.Melee;
        Projectile.penetrate = 5;
        Projectile.usesLocalNPCImmunity = true;
        Projectile.localNPCHitCooldown = 10;
        Projectile.tileCollide = true;
        Projectile.ignoreWater = true;
        Projectile.timeLeft = 240;
        Projectile.aiStyle = -1;
    }

    public override void AI()
    {
        Projectile.localAI[0]++;
        Projectile.rotation += SpinSpeed * SpinDirection;
        Projectile.velocity.Y = MathHelper.Clamp(Projectile.velocity.Y + Gravity,
            -MaximumFallSpeed, MaximumFallSpeed);
        Lighting.AddLight(Projectile.Center, 0.78f, 0.58f, 0.18f);

        if (!Main.dedServ && (int)Projectile.localAI[0] % 2 == 0)
        {
            Vector2 flightTangent = Projectile.velocity.SafeNormalize(Vector2.UnitX)
                .RotatedBy(MathHelper.PiOver2 * SpinDirection);
            Vector2 particlePosition = Projectile.Center + Main.rand.NextVector2Circular(9f, 9f);
            Dust dust = Dust.NewDustPerfect(particlePosition, DustID.GoldFlame,
                -Projectile.velocity * 0.035f + flightTangent * Main.rand.NextFloat(-0.35f, 0.35f),
                90, default, Main.rand.NextFloat(0.85f, 1.25f));
            dust.noGravity = true;
            VanillaLightParticleSystem.SpawnFlightSpark(particlePosition, flightTangent,
                (int)Projectile.localAI[0]);
        }
    }

    public override bool OnTileCollide(Vector2 oldVelocity)
    {
        Projectile.ai[1] = BounceCount + 1;
        Projectile.timeLeft = System.Math.Max(1, Projectile.timeLeft - BounceTimePenalty);
        Projectile.penetrate--;

        // Axis-aligned Terraria tiles make exact component reflection equivalent to
        // incidence angle = reflection angle. Corners invert both components.
        if (Projectile.velocity.X != oldVelocity.X)
            Projectile.velocity.X = -oldVelocity.X;
        if (Projectile.velocity.Y != oldVelocity.Y)
            Projectile.velocity.Y = -oldVelocity.Y;

        // Every tile impact keeps the bounce but also creates the same real 75x75 blast used by
        // enemy impacts. The third bounce still disappears immediately after releasing it.
        SpawnImpact(-1, -1);

        if (BounceCount >= MaximumBounces || Projectile.penetrate <= 0 || Projectile.timeLeft <= 1)
        {
            Projectile.Kill();
            return false;
        }

        Projectile.netUpdate = true;
        return false;
    }

    public override void OnHitNPC(NPC target, NPC.HitInfo hit, int damageDone) =>
        SpawnImpact(target.whoAmI, -1);

    public override void OnHitPlayer(Player target, Player.HurtInfo info) =>
        SpawnImpact(-1, target.whoAmI);

    private void SpawnImpact(int ignoredNpc, int ignoredPlayer)
    {
        Vector2 center = Projectile.Center;
        if (Main.netMode == NetmodeID.SinglePlayer || Projectile.owner == Main.myPlayer)
        {
            Projectile.NewProjectile(Projectile.GetSource_FromThis(), center, Vector2.Zero,
                ModContent.ProjectileType<DevelopmentNoticeExplosion>(), Projectile.damage,
                Projectile.knockBack, Projectile.owner, ignoredNpc + 1, ignoredPlayer + 1);
        }

        if (!Main.dedServ)
        {
            SoundEngine.PlaySound(SoundID.Item110, center);
            VanillaLightParticleSystem.SpawnImpactBurst(center);
            for (int i = 0; i < 32; i++)
            {
                Vector2 direction = Main.rand.NextVector2Unit();
                Dust dust = Dust.NewDustPerfect(center + direction * Main.rand.NextFloat(10f, 37f),
                    i % 4 == 0 ? DustID.YellowStarDust : DustID.GoldFlame,
                    direction * Main.rand.NextFloat(0.7f, 2.4f), 70, default,
                    Main.rand.NextFloat(1f, 1.65f));
                dust.noGravity = true;
            }
        }
    }

    public override bool PreDraw(ref Color lightColor)
    {
        Texture2D key = TextureAssets.Item[ItemID.Keybrand].Value;
        Vector2 drawPosition = Projectile.Center - Main.screenPosition;
        Vector2 origin = key.Size() * 0.5f;
        for (int i = 0; i < 8; i++)
        {
            Vector2 direction = (MathHelper.TwoPi * i / 8f).ToRotationVector2();
            Main.EntitySpriteDraw(key, drawPosition + direction * 3.2f, null,
                new Color(82, 48, 12) * 0.72f,
                Projectile.rotation, origin, Projectile.scale, SpriteEffects.None);
            Main.EntitySpriteDraw(key, drawPosition + direction * 1.65f, null,
                new Color(255, 196, 58, 0) * 0.54f,
                Projectile.rotation, origin, Projectile.scale, SpriteEffects.None);
        }
        Main.EntitySpriteDraw(key, drawPosition, null, lightColor, Projectile.rotation, origin,
            Projectile.scale, SpriteEffects.None);
        return false;
    }
}
