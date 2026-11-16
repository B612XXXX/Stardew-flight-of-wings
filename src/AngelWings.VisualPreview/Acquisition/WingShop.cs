using AngelWings.VisualPreview.Flight;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.GameData.Shops;
using StardewValley.Menus;

namespace AngelWings.VisualPreview.Acquisition;

public sealed class WingShop
{
    private readonly ModEntry Mod;
    private ShopMenu? PurchasedFrom;

    public WingShop(ModEntry mod) => Mod = mod;

    public void Register(IModHelper helper)
    {
        helper.Events.GameLoop.SaveCreating += OnSaveCreating;
        helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
        helper.Events.GameLoop.ReturnedToTitle += (_, _) => { if (Context.ScreenId == 0) PurchasedFrom = null; };
        helper.Events.Display.MenuChanged += OnMenuChanged;
        helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
    }

    private void OnSaveCreating(object? sender, SaveCreatingEventArgs e)
    {
        if (Context.ScreenId == 0 && Game1.player != null) WingProgress.Initialize(Game1.player);
    }

    private void OnSaveLoaded(object? sender, SaveLoadedEventArgs e)
    {
        if (Context.ScreenId != 0 || !Context.IsMainPlayer) return;
        PurchasedFrom = null;
        WingProgress.Initialize(Game1.player);
    }

    private void OnMenuChanged(object? sender, MenuChangedEventArgs e)
    {
        if (Mod.CanPurchaseWings && e.NewMenu is ShopMenu shop && AddOffer(shop)) shop.updateSaleButtonNeighbors();
    }

    public bool AddOffer(ShopMenu shop)
    {
        if (shop.ShopId != WingProgress.ShopId || shop.currency != 0 || shop.readOnly
            || WingProgress.IsUnlocked(Game1.player)
            || shop.forSale.Any(item => item is WingSkillOffer)) return false;
        shop.AddForSale(new WingSkillOffer(Mod.Translate, Mod.GetShopWingTexture,
                who => Mod.CanPurchaseWings && who == Game1.player && !WingProgress.IsUnlocked(who), OnPurchased),
            new ItemStockInformation(WingProgress.Price, 1, stockMode: LimitedStockMode.None,
                syncedKey: "AngelWingsProject.VisualPreview_FlightSkill"));
        return true;
    }

    private void OnPurchased()
    {
        if (WingProgress.UnlockPurchase(Game1.player)) PurchasedFrom = Game1.activeClickableMenu as ShopMenu;
    }

    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        if (!Mod.CanPurchaseWings || !WingProgress.NeedsTutorial(Game1.player)
            || Game1.eventUp || Game1.dialogueUp || Game1.fadeToBlack || Game1.currentMinigame != null) return;
        // Run after the shop completes charging and stock removal. Don't replace
        // another mod's popup or discard an item already held by the shop cursor.
        if (Game1.activeClickableMenu != null)
        {
            if (Game1.activeClickableMenu != PurchasedFrom || PurchasedFrom.heldItem != null
                || !PurchasedFrom.readyToClose()) return;
            PurchasedFrom.exitThisMenu();
            if (Game1.activeClickableMenu != null) return;
        }
        Game1.multipleDialogues(DescribeTutorial(Mod.Settings, Mod.Translate));
        WingProgress.TutorialShown(Game1.player);
        PurchasedFrom = null;
    }

    public static string[] DescribeTutorial(ModConfig config, Func<string, object?, string> translate)
    {
        ControlHint[] controls = FlightControlsOptions.Describe(config, translate);
        var keys = new
        {
            toggle = FlightControlsOptions.ButtonLabel(config.ToggleKey, translate),
            recover = FlightControlsOptions.ButtonLabel(config.EmergencyLandKey, translate),
            pad = FlightControlsOptions.ButtonLabel(config.ControllerToggleKey, translate)
        };
        bool keyboardOnly = config.ControllerToggleKey == SButton.None;
        return new[]
        {
            translate("wings-acquired", null) + " " + translate(keyboardOnly ? "wings-tutorial-toggle-keyboard" : "wings-tutorial-toggle", keys),
            controls[2].Detail + " " + controls[3].Detail,
            translate(keyboardOnly ? "wings-tutorial-recover-keyboard" : "wings-tutorial-recover", keys) + " " + translate("wings-help-again", null)
        };
    }
}
