using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.GameContent;
using Terraria.UI;

namespace MoreAIPlayer.Companion.UI;

public sealed class CompanionItemSlot : UIElement
{
    public const float SlotSize = 52f;

    private readonly Item[] items;
    private readonly int index;
    private readonly Func<Item, bool> canAccept;
    private readonly string rejectionText;
    private readonly Func<bool> isHighlighted;
    private readonly Color? accentColor;
    private readonly Func<bool> isLocked;
    private readonly string lockedText;

    /// <summary>Border colour of a slot that plays a special role, drawn permanently so the slot
    /// reads as different at a glance. The in-use highlight still wins while it is showing.</summary>
    public static readonly Color GuardAccent = new(70, 150, 255, 225);

    public static readonly Color SummonAccent = new(240, 200, 60, 225);

    public static readonly Color PickAccent = new(165, 170, 180, 225);

    public CompanionItemSlot(Item[] items, int index, Func<Item, bool> canAccept, string rejectionText,
        Func<bool> isHighlighted = null, Color? accentColor = null, Func<bool> isLocked = null, string lockedText = null)
    {
        this.items = items;
        this.index = index;
        this.canAccept = canAccept;
        this.rejectionText = rejectionText;
        this.isHighlighted = isHighlighted;
        this.accentColor = accentColor;
        this.isLocked = isLocked;
        this.lockedText = lockedText;
        Width.Set(SlotSize, 0f);
        Height.Set(SlotSize, 0f);
    }

    public override void Update(GameTime gameTime)
    {
        base.Update(gameTime);
        if (ContainsPoint(Main.MouseScreen))
            Main.LocalPlayer.mouseInterface = true;
    }

    protected override void DrawSelf(SpriteBatch spriteBatch)
    {
        base.DrawSelf(spriteBatch);
        CalculatedStyle dimensions = GetDimensions();
        Vector2 position = dimensions.Position();

        bool locked = isLocked?.Invoke() == true;

        // ItemSlot.Draw/Handle size themselves off Main.inventoryScale, which the game leaves at
        // 0.85 — so a slot drawn as 52px of layout actually rendered about 44px, and every accent
        // border sat a visible ring outside its own slot. Pinning the scale for the duration of
        // the draw makes the sprite exactly SlotSize and the borders land on it.
        float previousScale = Main.inventoryScale;
        Main.inventoryScale = SlotSize / 52f;
        try
        {
            DrawSlot(spriteBatch, position, locked);
        }
        finally
        {
            Main.inventoryScale = previousScale;
        }
    }

    private void DrawSlot(SpriteBatch spriteBatch, Vector2 position, bool locked)
    {

        // The in-use highlight takes precedence, so an active guard slot still reads as "this
        // is what the companion is swinging right now" rather than staying flatly blue.
        Color? border = isHighlighted?.Invoke() == true ? new Color(255, 205, 75, 220) : accentColor;
        if (border is Color borderColor)
        {
            Rectangle frame = new((int)position.X - 2, (int)position.Y - 2, (int)SlotSize + 4, (int)SlotSize + 4);
            spriteBatch.Draw(TextureAssets.MagicPixel.Value, frame, borderColor);
        }

        if (ContainsPoint(Main.MouseScreen))
        {
            Main.LocalPlayer.mouseInterface = true;
            bool mayRemoveExistingItem = Main.mouseItem.IsAir;
            if (locked)
                Main.instance.MouseText(lockedText);
            else if (mayRemoveExistingItem || canAccept(Main.mouseItem))
                ItemSlot.Handle(items, ItemSlot.Context.BankItem, index);
            else
                Main.instance.MouseText(rejectionText);
        }

        ItemSlot.Draw(spriteBatch, items, ItemSlot.Context.BankItem, index, position);

        // Drawn over the slot so a locked accessory still shows what is parked in it — the item
        // is not lost, it is just inert until the world unlocks the slot.
        if (locked)
        {
            Rectangle veil = new((int)position.X, (int)position.Y, (int)SlotSize, (int)SlotSize);
            spriteBatch.Draw(TextureAssets.MagicPixel.Value, veil, new Color(12, 12, 18, 190));
        }
    }
}
