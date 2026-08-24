using Microsoft.Xna.Framework;
using Terraria;
using Terraria.GameContent;
using Terraria.Graphics.Renderers;

namespace MoreAIPlayer;

/// <summary>
/// Small, dependency-free helpers for Terraria's pooled PrettySparkleParticle system. These are
/// the same family of vanilla light points used by weapons such as the Rainbow Rod and Magic Missile.
/// </summary>
internal static class VanillaLightParticleSystem
{
    private static readonly ParticlePool<PrettySparkleParticle> SparklePool =
        new(240, () => new PrettySparkleParticle());

    public static void SpawnFlightSpark(Vector2 position, Vector2 tangent, int phase)
    {
        if (Main.dedServ)
            return;

        Color color = (phase % 3) switch
        {
            0 => new Color(255, 238, 145),
            1 => new Color(255, 184, 72),
            _ => new Color(175, 225, 255)
        };
        PrettySparkleParticle sparkle = SparklePool.RequestParticle();
        sparkle.ColorTint = color;
        sparkle.LocalPosition = position;
        sparkle.Velocity = tangent.SafeNormalize(Vector2.UnitY) * 0.45f;
        sparkle.Rotation = tangent.ToRotation();
        sparkle.Scale = new Vector2(1.15f, 0.34f);
        sparkle.FadeInNormalizedTime = 0.01f;
        sparkle.FadeOutNormalizedTime = 0.88f;
        sparkle.TimeToLive = 20;
        sparkle.FadeInEnd = 3;
        sparkle.FadeOutStart = 9;
        sparkle.FadeOutEnd = 20;
        sparkle.AdditiveAmount = 0.62f;
        Main.ParticleSystem_World_OverPlayers.Add(sparkle);
    }

    public static void SpawnImpactBurst(Vector2 center)
    {
        if (Main.dedServ)
            return;

        const int ringCount = 24;
        float angleOffset = Main.rand.NextFloat(MathHelper.TwoPi);
        for (int i = 0; i < ringCount; i++)
        {
            float angle = angleOffset + MathHelper.TwoPi * i / ringCount;
            Vector2 radial = angle.ToRotationVector2();
            PrettySparkleParticle sparkle = SparklePool.RequestParticle();
            sparkle.ColorTint = i % 3 == 0
                ? Color.White
                : Color.Lerp(new Color(255, 170, 45), new Color(255, 246, 155), Main.rand.NextFloat());
            sparkle.LocalPosition = center + radial * Main.rand.NextFloat(28f, 37f);
            sparkle.Velocity = radial * Main.rand.NextFloat(0.35f, 0.95f);
            sparkle.Rotation = angle;
            sparkle.Scale = new Vector2(Main.rand.NextFloat(1.5f, 2.7f), Main.rand.NextFloat(0.35f, 0.7f));
            sparkle.FadeInNormalizedTime = 0.01f;
            sparkle.FadeOutNormalizedTime = 0.9f;
            sparkle.TimeToLive = Main.rand.Next(18, 26);
            sparkle.FadeInEnd = 3;
            sparkle.FadeOutStart = 9;
            sparkle.FadeOutEnd = sparkle.TimeToLive;
            sparkle.AdditiveAmount = i % 3 == 0 ? 0.85f : 0.62f;
            Main.ParticleSystem_World_OverPlayers.Add(sparkle);
        }

        PrettySparkleParticle core = SparklePool.RequestParticle();
        core.ColorTint = Color.White;
        core.LocalPosition = center;
        core.Velocity = Vector2.Zero;
        core.Rotation = angleOffset;
        core.Scale = new Vector2(2.8f, 2.8f);
        core.FadeInNormalizedTime = 0.01f;
        core.FadeOutNormalizedTime = 0.76f;
        core.TimeToLive = 16;
        core.FadeInEnd = 2;
        core.FadeOutStart = 5;
        core.FadeOutEnd = 16;
        core.AdditiveAmount = 0.9f;
        Main.ParticleSystem_World_OverPlayers.Add(core);
    }
}
