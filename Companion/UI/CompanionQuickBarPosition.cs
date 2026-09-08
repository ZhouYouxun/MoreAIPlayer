using System;
using System.IO;
using Microsoft.Xna.Framework;
using Newtonsoft.Json;
using Terraria;

namespace MoreAIPlayer.Companion.UI;

/// <summary>One local, resolution-independent anchor for the entire companion tab group.</summary>
internal static class CompanionQuickBarPosition
{
    private static bool loaded;
    private static Vector2? savedAnchor;
    private sealed class PositionData
    {
        public float X { get; set; }
        public float Y { get; set; }
    }

    private static string SavePath => Path.Combine(Main.SavePath, "ModConfigs", "MoreAIPlayer.QuickAccessPosition.json");

    internal static Vector2 GetAnchor(Vector2 viewport)
    {
        if (!loaded)
        {
            loaded = true;
            try
            {
                if (File.Exists(SavePath))
                {
                    PositionData data = JsonConvert.DeserializeObject<PositionData>(File.ReadAllText(SavePath));
                    if (data is not null && float.IsFinite(data.X) && float.IsFinite(data.Y))
                        savedAnchor = Vector2.Clamp(new Vector2(data.X, data.Y), Vector2.Zero, Vector2.One);
                }
            }
            catch (Exception exception)
            {
                MoreAIPlayer.Instance?.Logger?.Warn("Could not read companion tab position.", exception);
            }
        }
        return savedAnchor is Vector2 normalized ? normalized * viewport : new Vector2(viewport.X * 0.5f, 18f);
    }

    internal static void Save(Vector2 anchor, Vector2 viewport)
    {
        if (Main.dedServ || viewport.X <= 0 || viewport.Y <= 0 ||
            !float.IsFinite(anchor.X) || !float.IsFinite(anchor.Y))
            return;
        savedAnchor = Vector2.Clamp(anchor / viewport, Vector2.Zero, Vector2.One);
        loaded = true;
        try
        {
            string path = SavePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temporaryPath = path + ".tmp";
            File.WriteAllText(temporaryPath, JsonConvert.SerializeObject(new PositionData
            {
                X = savedAnchor.Value.X,
                Y = savedAnchor.Value.Y
            }, Formatting.Indented));
            File.Move(temporaryPath, path, overwrite: true);
        }
        catch (Exception exception)
        {
            MoreAIPlayer.Instance?.Logger?.Warn("Could not save companion tab position.", exception);
        }
    }

    internal static Vector2 ClampAnchor(Vector2 anchor, Vector2 size, Vector2 viewport)
    {
        float halfWidth = size.X * 0.5f;
        float x = size.X + 16f <= viewport.X ? MathHelper.Clamp(anchor.X, halfWidth + 8f, viewport.X - halfWidth - 8f) : viewport.X * 0.5f;
        float y = MathHelper.Clamp(anchor.Y, 8f, Math.Max(8f, viewport.Y - size.Y - 8f));
        return new Vector2(x, y);
    }
}
