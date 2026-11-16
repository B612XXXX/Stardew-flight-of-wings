using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngelWings.VisualPreview;
using AngelWings.VisualPreview.Acquisition;
using AngelWings.VisualPreview.Flight;
using HarmonyLib;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;

// Uses the installed game's real ShopMenu transaction and stock logic. Only
// graphics/audio/menu transitions are replaced; never loads a user save.
static class AcquisitionChecks
{
    static int Count;
    static int Dialogues;
    static string[] LastDialogue = Array.Empty<string>();
    static void Check(bool value, string message) { if (!value) throw new Exception(message); Count++; }
    static bool Silence(ref bool __result) { __result = true; return false; }
    static bool NoFade(ref bool __result) { __result = false; return false; }
    static bool CloseMenu() { Game1.activeClickableMenu = null; return false; }
    static bool ShowDialogue(string[] messages) { Dialogues++; LastDialogue = messages; return false; }
    static bool Translate(string key, object? tokens, ref string __result)
    {
        __result = Localize("zh", key, tokens);
        return false;
    }
    static string Localize(string language, string key, object? tokens)
    {
        var strings = JsonSerializer.Deserialize<Dictionary<string,string>>(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory,"i18n",language+".json")))!;
        return Regex.Replace(strings[key], @"\{\{(\w+)\}\}", m =>
            tokens?.GetType().GetProperty(m.Groups[1].Value)?.GetValue(tokens)?.ToString()
            ?? throw new Exception("Missing translation token"));
    }
    static void Invoke(WingShop shop, string method) => AccessTools.Method(typeof(WingShop),method).Invoke(shop,new object?[]{null,null});
    static ShopMenu Shop(string id = WingProgress.ShopId)
    {
        var shop = (ShopMenu)RuntimeHelpers.GetUninitializedObject(typeof(ShopMenu));
        shop.ShopId = id;
        shop.forSale = new List<ISalable>();
        shop.itemPriceAndStock = new Dictionary<ISalable,ItemStockInformation>();
        shop.buyBackItems = new HashSet<ISalable>();
        var animations=AccessTools.Field(typeof(ShopMenu),"animations");
        animations.SetValue(shop,Activator.CreateInstance(animations.FieldType));
        return shop;
    }
    static bool Purchase(ShopMenu shop, ISalable item, int count = 1) => (bool)AccessTools.Method(typeof(ShopMenu),"tryToPurchaseItem")
        .Invoke(shop,new object?[]{item,shop.heldItem,count,0,0})!;

    public static int Run(Harmony h, ModEntry mod, Farmer who, FlightController flight)
    {
        var ownedPatches = new List<MethodInfo>();
        void Prefix(MethodInfo target,string callback)
        {
            h.Patch(target,prefix:new HarmonyMethod(typeof(AcquisitionChecks),callback));
            ownedPatches.Add(target);
        }
        Prefix(AccessTools.Method(typeof(Game1),nameof(Game1.playSound),new[]{typeof(string),typeof(int?)}),nameof(Silence));
        Prefix(AccessTools.Method(typeof(IClickableMenu),nameof(IClickableMenu.exitThisMenu)),nameof(CloseMenu));
        Prefix(AccessTools.Method(typeof(Game1),nameof(Game1.multipleDialogues)),nameof(ShowDialogue));
        Prefix(AccessTools.Method(typeof(ModEntry),"Translate"),nameof(Translate));
        Prefix(AccessTools.PropertyGetter(typeof(Game1),nameof(Game1.fadeToBlack)),nameof(NoFade));
        var oldMoneyBox = Game1.dayTimeMoneyBox;
        Game1.dayTimeMoneyBox = (DayTimeMoneyBox)RuntimeHelpers.GetUninitializedObject(typeof(DayTimeMoneyBox));
        var acquisition = new WingShop(mod);
        try
        {
            who.modData.Clear();
            Invoke(acquisition,"OnSaveCreating");
            Check(!WingProgress.IsUnlocked(who),"A newly created character must start locked");
            Check(who.modData[WingProgress.StateKey]=="locked","New-game lock must exist before the initial save");
            Invoke(acquisition,"OnSaveLoaded");
            Check(!WingProgress.IsUnlocked(who),"First SaveLoaded must not grant legacy access to a new farm");
            AccessTools.Method(typeof(ModEntry),"ToggleFlight").Invoke(mod,null);
            Check(!flight.Active,"Keyboard/controller toggle must refuse flight before buying");
            var saved = who.modData.Pairs.ToDictionary(p=>p.Key,p=>p.Value);
            who.modData.Clear();
            foreach(var pair in saved)who.modData[pair.Key]=pair.Value;
            Invoke(acquisition,"OnSaveLoaded");
            Check(!WingProgress.IsUnlocked(who),"Reloading an unpurchased save must stay locked");

            var shop = Shop();
            Check(!acquisition.AddOffer(Shop("SeedShop")),"Other shops must not sell the skill");
            Check(acquisition.AddOffer(shop),"Hat Mouse must sell wings to locked characters");
            Check(!acquisition.AddOffer(shop)&&shop.forSale.Count==1,"Menu callbacks must not duplicate the offer");
            ISalable offer = shop.forSale.Single();
            Check(shop.itemPriceAndStock[offer].Price==1000 && offer.maximumStackSize()==1,
                "Price must be exactly 1000g, with one purchase maximum");
            Check(offer is not Item && !offer.IsRecipe,"Skill must not become an inventory item or fake recipe");
            Game1.activeClickableMenu=shop;
            who._money=999;
            Check(!Purchase(shop,offer),"Insufficient funds must fail the real game transaction");
            Check(who.Money==999&&!WingProgress.IsUnlocked(who)&&!WingProgress.NeedsTutorial(who),
                "Failed purchase must not charge, unlock, or schedule a tutorial");
            for(int i=0;i<12;i++)who.Items.Add(new StardewValley.Object());
            int inventoryCount=who.Items.Count;
            who._money=1500;
            Check(Purchase(shop,offer,5),"Shift/multiple purchase must succeed once and exhaust stock");
            Check(who.Money==500&&WingProgress.IsUnlocked(who),"Real transaction must charge exactly 1000g and unlock");
            Check(WingProgress.OwnsWing(who,WingProgress.AngelWingId)
                && WingProgress.GetEquippedWingId(who)==WingProgress.AngelWingId,
                "Hat Mouse purchase must own and equip the original angel appearance");
            Check(who.Items.Count==inventoryCount&&shop.heldItem==null,"Full inventory must not prevent skill purchase or leave a cursor item");
            Check(shop.itemPriceAndStock[offer].Stock==0,"Vanilla stock must reach zero");
            Check(WingProgress.NeedsTutorial(who)&&Dialogues==0,"Tutorial must wait until the transaction has completed");
            Purchase(shop,offer);
            Check(who.Money==500,"A second click on sold-out stock must not charge again");
            Check(!acquisition.AddOffer(Shop()),"Reopening Hat Mouse must not sell an already unlocked skill");

            // A stale shop copied before the purchase must also reject repeat payment.
            var stale=Shop();
            stale.AddForSale(offer,new ItemStockInformation(1000,1));
            who._money=2000;
            Check(!Purchase(stale,offer)&&who.Money==2000,"Stale offers must recheck ownership before charging");
            var foreignMenu=(DialogueBox)RuntimeHelpers.GetUninitializedObject(typeof(DialogueBox));
            Game1.activeClickableMenu=foreignMenu;
            Invoke(acquisition,"OnUpdateTicked");
            Check(Dialogues==0&&ReferenceEquals(Game1.activeClickableMenu,foreignMenu),"Tutorial must not replace another popup");
            Game1.activeClickableMenu=shop;
            Invoke(acquisition,"OnUpdateTicked");
            Check(Dialogues==1&&LastDialogue.Length==3&&!WingProgress.NeedsTutorial(who),"Purchase must show one complete tutorial after closing its shop");
            Invoke(acquisition,"OnUpdateTicked");
            Check(Dialogues==1,"Tutorial must not repeat on subsequent ticks");
            Invoke(acquisition,"OnSaveLoaded");
            Check(WingProgress.IsUnlocked(who)&&!WingProgress.NeedsTutorial(who),"Purchase and tutorial state must survive reload");
            AccessTools.Method(typeof(ModEntry),"ToggleFlight").Invoke(mod,null);
            Check(flight.Active,"Purchased skill must enable the existing flight controller");
            flight.Reset();

            // A different/new character has its own progress and can spend exactly 1000g.
            who.modData.Clear();
            Invoke(acquisition,"OnSaveCreating");
            var exact=Shop();
            Check(acquisition.AddOffer(exact),"A new character must not inherit the preceding character's unlock");
            who._money=1000;
            Game1.activeClickableMenu=exact;
            Check(Purchase(exact,exact.forSale.Single())&&who.Money==0,"Exactly 1000g must be sufficient");
            Invoke(acquisition,"OnSaveLoaded");
            Check(WingProgress.NeedsTutorial(who),"Unshown purchase instructions must survive a reload");
            Game1.activeClickableMenu=null;
            Invoke(acquisition,"OnUpdateTicked");
            Check(Dialogues==2&&!WingProgress.NeedsTutorial(who),"A pending tutorial must resume once the player is free");

            foreach(string language in new[]{"zh","default"})
            {
                string T(string key,object? tokens)=>Localize(language,key,tokens);
                var config=new ModConfig{ToggleKey=SButton.F6,EmergencyLandKey=SButton.F9,ControllerToggleKey=SButton.RightStick};
                var pages=WingShop.DescribeTutorial(config,T);
                Check(pages[0].Contains("F6")&&pages[0].Contains("R3")&&!pages[0].Contains("F8"),"Tutorial must use rebound keyboard and controller keys");
                Check(pages[2].Contains("F9")&&pages[2].Contains("1.2")&&pages.All(p=>!p.Contains("{{")),"Tutorial must include real recovery keys without unresolved tokens");
                config.ControllerToggleKey=SButton.None;
                Check(!string.Join(" ",WingShop.DescribeTutorial(config,T)).Contains("R3"),"Disabled controller must not be advertised");
            }
            who.modData.Clear();
            Invoke(acquisition,"OnSaveLoaded");
            Check(!WingProgress.IsUnlocked(who)&&who.modData[WingProgress.StateKey]=="locked","Existing saves without progress must require purchase");
            Check(!WingProgress.NeedsTutorial(who)&&acquisition.AddOffer(Shop()),"Unpurchased old saves must see the offer without a purchase popup");
            AccessTools.Method(typeof(ModEntry),"ToggleFlight").Invoke(mod,null);
            Check(!flight.Active,"An old save without a purchase must not start flight");

            who.modData[WingProgress.StateKey]="legacy";
            who.modData[WingProgress.TutorialKey]="true";
            who.modData["Fixture/Unrelated"]="preserved";
            Check(!WingProgress.IsUnlocked(who),"A 0.3.0 free legacy grant is not a purchase, even before migration");
            Invoke(acquisition,"OnSaveLoaded");
            Check(who.modData[WingProgress.StateKey]=="locked"&&!WingProgress.NeedsTutorial(who),"A free legacy grant must migrate to locked without a purchase tutorial");
            Check(who.modData["Fixture/Unrelated"]=="preserved","Migration must preserve unrelated character data");
            var upgradedShop=Shop();
            Check(acquisition.AddOffer(upgradedShop),"Migrated 0.3.0 characters must be able to buy wings");
            who._money=3000;
            Game1.activeClickableMenu=upgradedShop;
            Check(Purchase(upgradedShop,upgradedShop.forSale.Single()),"A previously free legacy character must complete a real purchase");
            Check(who.Money==2000&&WingProgress.IsUnlocked(who)&&WingProgress.NeedsTutorial(who),"Old-character purchase must charge 1000g, unlock flight and queue instructions");
            Invoke(acquisition,"OnSaveLoaded");
            Check(who.modData[WingProgress.StateKey]=="purchased"&&!acquisition.AddOffer(Shop()),"A saved real purchase must survive migration and hide the offer");
            WingProgress.Initialize(who);
            Check(WingProgress.IsUnlocked(who),"Initialization must never overwrite existing ownership");
            Console.WriteLine("PASS: native shop payment/stock, new and legacy saves, ownership, full inventory and purchase tutorial.");
            return Count;
        }
        finally
        {
            Game1.activeClickableMenu=null;
            Game1.dayTimeMoneyBox=oldMoneyBox;
            who.Items.Clear();
            foreach(var target in ownedPatches)h.Unpatch(target,HarmonyPatchType.Prefix,h.Id);
        }
    }
}
