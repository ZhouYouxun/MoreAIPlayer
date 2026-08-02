using Microsoft.Xna.Framework;
using Terraria;
using Terraria.Audio;
using Terraria.DataStructures;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.ModLoader.IO;

namespace MoreAIPlayer;

/// <summary>
/// The authoritative size state for the real local player. It deliberately does not use the
/// companion execution-context swap: the owner is already Main.LocalPlayer, so scaling must
/// never alter Main.myPlayer or the synthetic-player registry.
/// </summary>
public sealed class PlayerScalePlayer : ModPlayer
{
    public static readonly float[] AllowedScales = [0.5f, 1f, 2f, 3f, 4f, 5f];

    private const int VanillaWidth = 20;
    private const int VanillaHeight = 42;

    /// <summary>The value currently armed in the matrix. It is retained between sessions.</summary>
    public float SelectedScale { get; private set; } = 1f;

    /// <summary>The value presently applied to the player. A second left click returns this to 1.</summary>
    public float ActiveScale { get; private set; } = 1f;

    public bool IsScaled => ActiveScale != 1f;

    public override void Initialize()
    {
        SelectedScale = 1f;
        ActiveScale = 1f;
    }

    public override void SaveData(TagCompound tag)
    {
        tag[nameof(SelectedScale)] = SelectedScale;
        tag[nameof(ActiveScale)] = ActiveScale;
    }

    public override void LoadData(TagCompound tag)
    {
        SelectedScale = Normalize(tag.ContainsKey(nameof(SelectedScale)) ? tag.GetFloat(nameof(SelectedScale)) : 1f);
        ActiveScale = Normalize(tag.ContainsKey(nameof(ActiveScale)) ? tag.GetFloat(nameof(ActiveScale)) : 1f);
    }

    public void SelectScale(float scale) => SelectedScale = Normalize(scale);

    /// <summary>Applies the armed scale, or cancels the active transformation on the next use.</summary>
    public void ToggleScale()
    {
        float previous = ActiveScale;
        ActiveScale = IsScaled ? 1f : SelectedScale;
        if (!TryApplyPhysicalSize(checkForSpace: true))
        {
            ActiveScale = previous;
            Main.NewText(Terraria.Localization.Language.GetTextValue(
                "Mods.MoreAIPlayer.CompanionCommand.ScaleNoSpace"), new Color(255, 180, 110));
            return;
        }
        SpawnTransitionDust();
        SoundEngine.PlaySound(SoundID.Item4 with { Pitch = IsScaled ? -0.15f : 0.1f }, Player.Center);
    }

    public override void PreUpdate()
    {
        // The feature is intentionally single-player only. A remote player needs a packet and
        // server-authoritative collision reconciliation; silently changing only one client
        // would desync hitboxes and projectile damage.
        if (Main.netMode == NetmodeID.SinglePlayer && !TryApplyPhysicalSize(checkForSpace: true))
            ActiveScale = 1f;
    }

    public override void PostUpdateMiscEffects()
    {
        if (!IsScaled)
            return;

        float movementFactor = ActiveScale;
        // moveSpeed affects walking before the final run-speed values are calculated, while
        // wingTimeMax is rebuilt by equipment every tick. Applying both here makes size matter
        // for ordinary movement and the total time a winged player can remain airborne.
        Player.moveSpeed *= movementFactor;
        Player.jumpSpeedBoost *= movementFactor;
        Player.wingTimeMax = (int)System.MathF.Round(Player.wingTimeMax * movementFactor);
        if (Player.wingTime > Player.wingTimeMax)
            Player.wingTime = Player.wingTimeMax;
    }

    public override void PostUpdateRunSpeeds()
    {
        if (!IsScaled)
            return;

        // Movement is part of the same transformation: a 3× body gets 3× running cap and
        // acceleration, while a 0.5× body gets half. This is intentionally not a realism
        // penalty; the book is an explicit proportional "everything grows" effect.
        float movementFactor = ActiveScale;
        Player.maxRunSpeed *= movementFactor;
        Player.accRunSpeed *= movementFactor;
        Player.runAcceleration *= movementFactor;
    }

    public override void PreUpdateMovement()
    {
        // This is the update boundary immediately before vanilla applies velocity and terrain
        // collision. ResetEffects and several vanilla movement branches can restore 20×42
        // earlier in the tick, so PreUpdate alone only changed a temporary rectangle. Reassert
        // the body here: the ground/platform collision now sees the same feet-to-head size that
        // the renderer sees.
        EnsurePhysicalSizeAfterVanillaReset();
    }

    public override void PostUpdate()
    {
        // Keep the final frame's hitbox in sync too. This prevents a late vanilla mount/rope
        // branch from leaving a large rendered player standing on a tiny 20×42 collider.
        EnsurePhysicalSizeAfterVanillaReset();
    }

    public override void ModifyWeaponDamage(Item item, ref StatModifier damage)
    {
        if (IsScaled && item.damage > 0)
            damage *= ActiveScale;
    }

    public override void ModifyItemScale(Item item, ref float scale)
    {
        if (IsScaled && !item.IsAir)
            scale *= ActiveScale;
    }

    /// <summary>
    /// Runs after vanilla and every modded player layer. Scaling the complete DrawData cache is
    /// what keeps armour, dyes, wings, mounts, held items and modded player layers visually
    /// attached to the same enlarged or shrunken body instead of scaling only the skin sprite.
    /// </summary>
    public override void TransformDrawData(ref PlayerDrawSet drawInfo)
    {
        if (!IsScaled)
            return;

        Vector2 pivot = Player.Center - Main.screenPosition;
        for (int i = 0; i < drawInfo.DrawDataCache.Count; i++)
        {
            DrawData data = drawInfo.DrawDataCache[i];
            data.position = pivot + (data.position - pivot) * ActiveScale;
            data.scale *= ActiveScale;
            drawInfo.DrawDataCache[i] = data;
        }
    }

    private void EnsurePhysicalSizeAfterVanillaReset()
    {
        if (Main.netMode != NetmodeID.SinglePlayer || !IsScaled)
            return;

        if (!TryApplyPhysicalSize(checkForSpace: false))
            ActiveScale = 1f;
    }

    private bool TryApplyPhysicalSize(bool checkForSpace)
    {
        int targetWidth = (int)System.MathF.Round(VanillaWidth * ActiveScale);
        int targetHeight = (int)System.MathF.Round(VanillaHeight * ActiveScale);
        if (Player.width == targetWidth && Player.height == targetHeight)
            return true;

        // Keep the feet anchored. Without this, every switch appears as a small teleport and
        // large forms can fall through the floor for one update.
        Vector2 feet = Player.Bottom;
        Vector2 targetPosition = feet - new Vector2(targetWidth * 0.5f, targetHeight);
        // Expanding inside a tunnel would otherwise leave a five-times-sized hitbox embedded
        // in solid tiles. Refuse that particular switch instead of creating a soft-lock. The
        // player can simply step into a larger area and use the book again.
        if (checkForSpace && ActiveScale > 1f && Collision.SolidCollision(targetPosition, targetWidth, targetHeight))
            return false;
        Player.width = targetWidth;
        Player.height = targetHeight;
        Player.Bottom = feet;
        return true;
    }

    private void SpawnTransitionDust()
    {
        float radius = 18f * ActiveScale;
        for (int i = 0; i < 24; i++)
        {
            Vector2 direction = Main.rand.NextVector2Unit();
            Dust dust = Dust.NewDustPerfect(Player.Center + direction * Main.rand.NextFloat(radius * 0.35f, radius),
                DustID.CrystalPulse, direction * Main.rand.NextFloat(1.5f, 4f) * ActiveScale,
                100, new Color(155, 235, 255), 0.85f * ActiveScale);
            dust.noGravity = true;
        }
    }

    private static float Normalize(float scale)
    {
        float closest = 1f;
        float smallestDifference = float.MaxValue;
        foreach (float candidate in AllowedScales)
        {
            float difference = System.MathF.Abs(candidate - scale);
            if (difference < smallestDifference)
            {
                smallestDifference = difference;
                closest = candidate;
            }
        }
        return closest;
    }
}
