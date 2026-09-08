using Microsoft.Xna.Framework;
using Terraria;
using Terraria.GameContent.UI.Chat;
using Terraria.ModLoader;

namespace MoreAIPlayer;

public sealed class ChatMessageFilterSystem : ModSystem
{
    private delegate void OrigAdd(RemadeChatMonitor monitor, string text, Color color, int width);
    private delegate void AddDetour(OrigAdd orig, RemadeChatMonitor monitor, string text, Color color, int width);
    private delegate void OrigDraw(RemadeChatMonitor monitor, bool drawingPlayerChat);
    private delegate void DrawDetour(OrigDraw orig, RemadeChatMonitor monitor, bool drawingPlayerChat);

    public override void Load()
    {
        if (Main.dedServ)
            return;
        MonoModHooks.Add(typeof(RemadeChatMonitor).GetMethod(nameof(RemadeChatMonitor.AddNewMessage)),
            (AddDetour)AddMessage);
        MonoModHooks.Add(typeof(RemadeChatMonitor).GetMethod(nameof(RemadeChatMonitor.DrawChat)),
            (DrawDetour)DrawChat);
    }

    private static bool Muted => MoreAIPlayerClientConfig.Current?.SilenceAllChatMessages == true;

    // Main.NewText, multiline messages and received chat all end at this shared sink.
    private static void AddMessage(OrigAdd orig, RemadeChatMonitor monitor, string text, Color color, int width)
    {
        if (!Muted)
            orig(monitor, text, color, width);
    }

    private static void DrawChat(OrigDraw orig, RemadeChatMonitor monitor, bool drawingPlayerChat)
    {
        if (!Muted)
            orig(monitor, drawingPlayerChat);
    }
}
