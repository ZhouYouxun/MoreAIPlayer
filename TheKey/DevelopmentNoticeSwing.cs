using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.DataStructures;
using Terraria.GameContent;
using Terraria.GameContent.Drawing;
using Terraria.ID;
using Terraria.ModLoader;

namespace MoreAIPlayer;

/// <summary>A close-range Keybrand swing. Its sparkle drawing follows the 1.4.4-style
/// sword-light pattern, while successful hits request the exact vanilla Keybrand particle set.</summary>
public sealed class DevelopmentNoticeSwing : ModProjectile
{
    // ExampleSwingingEnergySword uses its own local four-frame sword-light sheet. Keep the
    // same arrangement here so the Frame(1, 4) drawing below addresses the identical asset.
    public override string Texture => "MoreAIPlayer/TheKey/DevelopmentNoticeSwing";

    public override void SetStaticDefaults() =>
        ProjectileID.Sets.AllowsContactDamageFromJellyfish[Type] = true;

    public override void SetDefaults()
    {
        Projectile.width = 16;
        Projectile.height = 16;
        Projectile.friendly = true;
        Projectile.DamageType = DamageClass.Melee;
        Projectile.penetrate = -1;
        Projectile.usesLocalNPCImmunity = true;
        Projectile.localNPCHitCooldown = -1;
        Projectile.tileCollide = false;
        Projectile.ignoreWater = true;
        Projectile.ownerHitCheck = true;
        Projectile.ownerHitCheckDistance = 120f;
        Projectile.usesOwnerMeleeHitCD = true;
        Projectile.stopsDealingDamageAfterPenetrateHits = true;
        Projectile.aiStyle = -1;
        Projectile.noEnchantmentVisuals = true;
    }

    public override void AI()
    {
        Projectile.localAI[0]++;
        Player player = Main.player[Projectile.owner];
        if (!player.active || player.dead)
        {
            Projectile.Kill();
            return;
        }

        float swingDuration = Projectile.ai[1];
        if (swingDuration <= 0f)
        {
            Projectile.Kill();
            return;
        }

        float progress = Projectile.localAI[0] / swingDuration;
        float swingDirection = Projectile.ai[0];
        float aimRotation = Projectile.velocity.ToRotation();
        Projectile.rotation = MathHelper.Pi * swingDirection * progress + aimRotation + swingDirection * MathHelper.Pi + player.fullRotation;
        Projectile.Center = player.RotatedRelativePoint(player.MountedCenter) - Projectile.velocity;
        Projectile.scale = (1f + progress * 0.6f) * Projectile.ai[2];

        if (Projectile.localAI[0] >= swingDuration)
            Projectile.Kill();
    }

    public override bool? Colliding(Rectangle projHitbox, Rectangle targetHitbox)
    {
        float swingLength = 94f * Projectile.scale;
        float collisionRotation = Projectile.rotation + MathHelper.Pi * 2f / 25f * Projectile.ai[0];
        const float maximumAngle = MathHelper.PiOver4;

        if (targetHitbox.IntersectsConeSlowMoreAccurate(Projectile.Center, swingLength, collisionRotation, maximumAngle))
            return true;

        float backOfSwing = Utils.Remap(Projectile.localAI[0], Projectile.ai[1] * 0.3f, Projectile.ai[1] * 0.5f, 1f, 0f);
        if (backOfSwing <= 0f)
            return false;

        float trailingRotation = collisionRotation - MathHelper.PiOver4 * Projectile.ai[0] * backOfSwing;
        return targetHitbox.IntersectsConeSlowMoreAccurate(Projectile.Center, swingLength, trailingRotation, maximumAngle);
    }

    public override void OnHitNPC(NPC target, NPC.HitInfo hit, int damageDone)
    {
        Vector2 hitPosition = target.Hitbox.ClosestPointInRect(Projectile.Center);
        ParticleOrchestrator.RequestParticleSpawn(
            clientOnly: false,
            ParticleOrchestraType.Keybrand,
            new ParticleOrchestraSettings { PositionInWorld = hitPosition },
            Projectile.owner);

        hit.HitDirection = Main.player[Projectile.owner].Center.X < target.Center.X ? 1 : -1;
    }

    public override bool PreDraw(ref Color lightColor)
    {
        Texture2D texture = TextureAssets.Projectile[Type].Value;
        Vector2 drawPosition = Projectile.Center - Main.screenPosition;
        Rectangle swordFrame = texture.Frame(1, 4);
        Vector2 origin = swordFrame.Size() * 0.5f;
        float progress = Projectile.localAI[0] / Projectile.ai[1];
        float fade = Utils.Remap(progress, 0f, 0.6f, 0f, 1f) * Utils.Remap(progress, 0.6f, 1f, 1f, 0f);
        float lighting = Lighting.GetColor(Projectile.Center.ToTileCoordinates()).ToVector3().Length() / System.MathF.Sqrt(3f);
        lighting = Utils.Remap(lighting, 0.2f, 1f, 0f, 1f);
        float scale = Projectile.scale * 1.1f;
        SpriteEffects effects = Projectile.ai[0] >= 0f ? SpriteEffects.None : SpriteEffects.FlipVertically;

        // These overlapping passes are the standard 1.4.4 Excalibur sword-light construction:
        // one blade silhouette, a soft internal glow, and three thin highlights rather than
        // several independent sword sprites.
        Color backColor = new(180, 160, 60);
        Color middleColor = new(255, 255, 80);
        Color frontColor = new(255, 240, 150);
        Color faintLighting = Color.White * fade * 0.5f;
        faintLighting.A = (byte)(faintLighting.A * (1f - lighting));
        faintLighting *= lighting * 0.5f;
        faintLighting.G = (byte)(faintLighting.G * lighting);
        faintLighting.B = (byte)(faintLighting.R * (0.25f + lighting * 0.75f));

        Main.EntitySpriteDraw(texture, drawPosition, swordFrame, backColor * lighting * fade,
            Projectile.rotation - Projectile.ai[0] * MathHelper.PiOver4 * (1f - progress), origin, scale, effects, 0f);
        Main.EntitySpriteDraw(texture, drawPosition, swordFrame, faintLighting * 0.15f,
            Projectile.rotation + Projectile.ai[0] * 0.01f, origin, scale, effects, 0f);
        Main.EntitySpriteDraw(texture, drawPosition, swordFrame, middleColor * lighting * fade * 0.3f,
            Projectile.rotation, origin, scale, effects, 0f);
        Main.EntitySpriteDraw(texture, drawPosition, swordFrame, frontColor * lighting * fade * 0.5f,
            Projectile.rotation, origin, scale * 0.975f, effects, 0f);

        Rectangle highlightFrame = texture.Frame(1, 4, 0, 3);
        Main.EntitySpriteDraw(texture, drawPosition, highlightFrame, Color.White * 0.6f * fade,
            Projectile.rotation + Projectile.ai[0] * 0.01f, origin, scale, effects, 0f);
        Main.EntitySpriteDraw(texture, drawPosition, highlightFrame, Color.White * 0.5f * fade,
            Projectile.rotation - Projectile.ai[0] * 0.05f, origin, scale * 0.8f, effects, 0f);
        Main.EntitySpriteDraw(texture, drawPosition, highlightFrame, Color.White * 0.4f * fade,
            Projectile.rotation - Projectile.ai[0] * 0.1f, origin, scale * 0.6f, effects, 0f);

        for (float i = 0f; i < 8f; i++)
        {
            float edgeRotation = Projectile.rotation + Projectile.ai[0] * i * (MathHelper.Pi * -2f) * 0.025f +
                Utils.Remap(progress, 0f, 1f, 0f, MathHelper.PiOver4) * Projectile.ai[0];
            Vector2 sparklePosition = drawPosition + edgeRotation.ToRotationVector2() * (texture.Width * 0.5f - 6f) * scale;
            DrawPrettyStarSparkle(fade, sparklePosition, new Color(255, 255, 255, 0) * fade * (i / 9f), middleColor,
                progress, edgeRotation, new Vector2(0f, Utils.Remap(progress, 0f, 1f, 3f, 0f)) * scale, Vector2.One * scale);
        }

        Vector2 tipSparklePosition = drawPosition + (Projectile.rotation + Utils.Remap(progress, 0f, 1f, 0f, MathHelper.PiOver4) * Projectile.ai[0]).ToRotationVector2() * (texture.Width * 0.5f - 4f) * scale;
        DrawPrettyStarSparkle(fade, tipSparklePosition, new Color(255, 255, 255, 0) * fade * 0.5f, middleColor,
            progress, 0f, new Vector2(2f, Utils.Remap(progress, 0f, 1f, 4f, 1f)) * scale, Vector2.One * scale);

        return false;
    }

    private static void DrawPrettyStarSparkle(float opacity, Vector2 drawPosition, Color drawColor, Color shineColor,
        float progress, float rotation, Vector2 stretch, Vector2 scale)
    {
        Texture2D sparkleTexture = TextureAssets.Extra[ExtrasID.SharpTears].Value;
        Vector2 origin = sparkleTexture.Size() * 0.5f;
        float visibility = Utils.GetLerpValue(0f, 0.5f, progress, true) * Utils.GetLerpValue(1f, 0.5f, progress, true);
        Color bright = shineColor * opacity * 0.5f * visibility;
        bright.A = 0;
        Color dim = drawColor * 0.5f * visibility;
        Vector2 wideScale = new(scale.X * 0.5f, stretch.X);
        Vector2 tallScale = new(scale.Y * 0.5f, stretch.Y);

        Main.EntitySpriteDraw(sparkleTexture, drawPosition, null, bright, MathHelper.PiOver2 + rotation, origin, wideScale, SpriteEffects.None);
        Main.EntitySpriteDraw(sparkleTexture, drawPosition, null, bright, rotation, origin, tallScale, SpriteEffects.None);
        Main.EntitySpriteDraw(sparkleTexture, drawPosition, null, dim, MathHelper.PiOver2 + rotation, origin, wideScale * 0.6f, SpriteEffects.None);
        Main.EntitySpriteDraw(sparkleTexture, drawPosition, null, dim, rotation, origin, tallScale * 0.6f, SpriteEffects.None);
    }
}
