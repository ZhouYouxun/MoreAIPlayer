using Microsoft.Xna.Framework;
using Terraria;
using Terraria.DataStructures;
using Terraria.ModLoader;

namespace MoreAIPlayer;

/// <summary>
/// Captures the owner's body scale when a friendly projectile is born. Keeping a snapshot is
/// important: an arrow, beam, summon or child projectile must not suddenly resize halfway
/// through its life because its owner pressed the book again.
/// </summary>
public sealed class PlayerScaleGlobalProjectile : GlobalProjectile
{
    public override bool InstancePerEntity => true;

    public float SizeSnapshot { get; private set; } = 1f;

    public override void OnSpawn(Projectile projectile, IEntitySource source)
    {
        if (Main.netMode != Terraria.ID.NetmodeID.SinglePlayer || !projectile.friendly || projectile.hostile)
            return;

        if (source is EntitySource_Parent { Entity: Projectile parent })
            SizeSnapshot = parent.GetGlobalProjectile<PlayerScaleGlobalProjectile>().SizeSnapshot;
        else if (projectile.owner >= 0 && projectile.owner < Main.maxPlayers && Main.player[projectile.owner].active)
            SizeSnapshot = Main.player[projectile.owner].GetModPlayer<PlayerScalePlayer>().ActiveScale;

        if (SizeSnapshot != 1f)
            projectile.scale *= SizeSnapshot;
    }

    public override void ModifyDamageHitbox(Projectile projectile, ref Rectangle hitbox)
    {
        if (SizeSnapshot == 1f)
            return;

        Vector2 center = hitbox.Center.ToVector2();
        int width = System.Math.Max(1, (int)System.MathF.Round(hitbox.Width * SizeSnapshot));
        int height = System.Math.Max(1, (int)System.MathF.Round(hitbox.Height * SizeSnapshot));
        hitbox = new Rectangle((int)System.MathF.Round(center.X - width * 0.5f),
            (int)System.MathF.Round(center.Y - height * 0.5f), width, height);
    }
}
