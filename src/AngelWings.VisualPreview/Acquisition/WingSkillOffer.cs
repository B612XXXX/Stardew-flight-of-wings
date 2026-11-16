using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;

namespace AngelWings.VisualPreview.Acquisition;

/// <summary>A shop-only skill purchase. It never enters inventory or save serialization.</summary>
public sealed class WingSkillOffer : ISalable
{
    private readonly Func<string, object?, string> Translate;
    private readonly Func<Texture2D> GetTexture;
    private readonly Func<Farmer, bool> CanPurchase;
    private readonly Action Purchased;

    public WingSkillOffer(Func<string, object?, string> translate, Func<Texture2D> texture,
        Func<Farmer, bool> canPurchase, Action purchased)
    {
        Translate = translate;
        GetTexture = texture;
        CanPurchase = canPurchase;
        Purchased = purchased;
    }

    public string TypeDefinitionId => "(O)";
    public string QualifiedItemId => "(O)AngelWingsProject.VisualPreview_FlightSkill";
    public string Name => "AngelWingsProject.VisualPreview_FlightSkill";
    public string DisplayName => Translate("wings-shop-name", null);
    public bool IsRecipe { get => false; set { } }
    public int Stack { get => 1; set { } }
    public int Quality { get => 0; set { } }
    public string GetItemTypeId() => TypeDefinitionId;
    public bool ShouldDrawIcon() => true;
    public string getDescription() => Translate("wings-shop-description", null);
    public int maximumStackSize() => 1;
    public int addToStack(Item stack) => stack.Stack;
    public int sellToStorePrice(long specificPlayerID = -1) => 0;
    public int salePrice(bool ignoreProfitMargins = false) => WingProgress.Price;
    public bool appliesProfitMargins() => false;
    public bool canStackWith(ISalable other) => false;
    public bool CanBuyItem(Farmer farmer) => CanPurchase(farmer);
    public bool IsInfiniteStock() => false;
    public ISalable GetSalableInstance() => new WingSkillOffer(Translate, GetTexture, CanPurchase, Purchased);
    public void FixStackSize() { }
    public void FixQuality() { }

    public bool actionWhenPurchased(string shopId)
    {
        // The vanilla shop has checked eligibility and deducted the money already.
        if (shopId == WingProgress.ShopId && CanBuyItem(Game1.player)) Purchased();
        return true;
    }

    public void drawInMenu(SpriteBatch spriteBatch, Vector2 location, float scaleSize, float transparency,
        float layerDepth, StackDrawType drawStackNumber, Color color, bool drawShadow)
    {
        spriteBatch.Draw(GetTexture(), location + new Vector2(32), new Rectangle(0, 0, 80, 80),
            color * transparency, 0, new Vector2(40), .8f * scaleSize, SpriteEffects.None, layerDepth);
    }
}
