using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.GameContent;
using Terraria.ID;
using Terraria.ModLoader;

namespace MoreAIPlayer;

/// <summary>
/// A one-update 75x75 damage pulse with a short, explicitly diameter-bounded visual ring. The
/// directly struck target is excluded because it has already taken the key's contact damage.
/// </summary>
public sealed class DevelopmentNoticeExplosion : ModProjectile
{
    private const int ExplosionSize = 75;
    private const int VisualLifetime = 14;

    private int IgnoredNpc => (int)Projectile.ai[0] - 1;
    private int IgnoredPlayer => (int)Projectile.ai[1] - 1;

    public override string Texture => "Terraria/Images/Projectile_0";

    public override void SetDefaults()
    {
        Projectile.width = ExplosionSize;
        Projectile.height = ExplosionSize;
        Projectile.friendly = true;
        Projectile.DamageType = DamageClass.Melee;
        Projectile.penetrate = -1;
        Projectile.usesLocalNPCImmunity = true;
        Projectile.localNPCHitCooldown = -1;
        Projectile.tileCollide = false;
        Projectile.ignoreWater = true;
        Projectile.timeLeft = VisualLifetime;
        Projectile.aiStyle = -1;
    }

    public override bool ShouldUpdatePosition() => false;

    public override void AI()
    {
        Projectile.localAI[0]++;
        Lighting.AddLight(Projectile.Center, 0.95f, 0.68f, 0.18f);
    }

    public override bool? CanDamage() => Projectile.localAI[0] <= 1f ? null : false;

    public override bool? CanHitNPC(NPC target) => target.whoAmI == IgnoredNpc ? false : null;

    public override bool CanHitPlayer(Player target) => target.whoAmI != IgnoredPlayer;

    public override bool PreDraw(ref Color lightColor)
    {
        Texture2D ring = TextureAssets.Extra[ExtrasID.KeybrandRing].Value;
        float progress = MathHelper.Clamp(Projectile.localAI[0] / VisualLifetime, 0f, 1f);
        float eased = 1f - (1f - progress) * (1f - progress);
        float diameter = MathHelper.Lerp(14f, ExplosionSize, eased);
        float scale = diameter / System.Math.Max(1f, ring.Width);
        float opacity = System.MathF.Sin(progress * MathHelper.Pi);
        Vector2 drawPosition = Projectile.Center - Main.screenPosition;
        Vector2 origin = ring.Size() * 0.5f;

        Main.EntitySpriteDraw(ring, drawPosition, null, new Color(255, 155, 32, 0) * (0.88f * opacity),
            -progress * 0.8f, origin, scale * 1.08f, SpriteEffects.None);
        Main.EntitySpriteDraw(ring, drawPosition, null, new Color(255, 239, 145, 0) * opacity,
            progress * 0.55f, origin, scale, SpriteEffects.None);
        Main.EntitySpriteDraw(ring, drawPosition, null, Color.White * (0.48f * opacity),
            progress * 0.25f, origin, scale * 0.86f, SpriteEffects.None);
        return false;
    }
}
