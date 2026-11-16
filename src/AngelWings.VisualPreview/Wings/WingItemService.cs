using AngelWings.VisualPreview.Acquisition;
using HarmonyLib;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Menus;
using StardewValley.Objects;

namespace AngelWings.VisualPreview.Wings;

/// <summary>Data-driven wall furniture plus atomic inventory/wall appearance swaps.</summary>
public sealed class WingItemService
{
    private readonly ModEntry Mod;

    public WingItemService(ModEntry mod) => Mod = mod;

    public void Register(IModHelper helper)
    {
        helper.Events.Content.AssetRequested += OnAssetRequested;
        helper.Events.GameLoop.SaveLoaded += (_, _) => Utility.ForEachItem(RefreshWingFurniture);
    }

    private void OnAssetRequested(object? sender, AssetRequestedEventArgs e)
    {
        if (e.NameWithoutLocale.IsEquivalentTo("Data/Furniture"))
        {
            e.Edit(asset =>
            {
                IDictionary<string, string> data = asset.AsDictionary<string, string>().Data;
                data[WingIds.AngelFurnitureId] = BuildFurnitureData(WingProgress.AngelWingId,
                    Mod.Translate("angel-wing-item-name", null));
                data[WingIds.ButterflyFurnitureId] = BuildFurnitureData(WingProgress.ButterflyWingId,
                    Mod.Translate("butterfly-wing-item-name", null));
            });
        }
        else if (e.NameWithoutLocale.IsEquivalentTo(WingIds.ItemTextureAsset))
            e.LoadFromModFile<Microsoft.Xna.Framework.Graphics.Texture2D>("assets/items/wing-items.png", AssetLoadPriority.Exclusive);
    }

    internal static string BuildFurnitureData(string wingId, string displayName) => FurnitureData(
        WingIds.FurnitureIdFor(wingId), displayName, wingId == WingProgress.AngelWingId ? 0 : 5);

    private static string FurnitureData(string internalName, string displayName, int spriteIndex) => string.Join('/',
        internalName, "painting", "5 3", "5 3", "1", "0", "0", displayName.Replace('/', '／'),
        spriteIndex.ToString(System.Globalization.CultureInfo.InvariantCulture), WingIds.ItemTextureAsset, "true");

    internal static bool RefreshWingFurniture(Item item)
    {
        if (item is Furniture furniture && WingIds.FromItem(furniture) != null)
        {
            // Source rectangles and the old butterfly sheet index are serialized
            // in saves. Refresh existing wall/inventory/storage items in place.
            furniture.ResetParentSheetIndex();
            furniture.InitializeAtTile(furniture.TileLocation);
        }
        return true;
    }

    public static Furniture CreateWingFurniture(string wingId)
    {
        Furniture item = ItemRegistry.Create<Furniture>("(F)" + WingIds.FurnitureIdFor(wingId));
        // Furniture has no data-level trash flag. Marking the instance as a quest
        // item preserves ordinary wall placement while preventing deletion/loss.
        item.questItem.Value = true;
        return item;
    }

    public static void InstallPatches(Harmony harmony)
    {
        harmony.Patch(AccessTools.Method(typeof(InventoryPage), nameof(InventoryPage.receiveRightClick),
                [typeof(int), typeof(int), typeof(bool)]),
            prefix: new HarmonyMethod(typeof(WingItemService), nameof(InventoryRightClickPrefix)));
        harmony.Patch(AccessTools.Method(typeof(Furniture), nameof(Furniture.checkForAction),
                [typeof(Farmer), typeof(bool)]),
            prefix: new HarmonyMethod(typeof(WingItemService), nameof(WallActionPrefix)));
        harmony.Patch(AccessTools.Method(typeof(Furniture), nameof(Furniture.getDescription)),
            postfix: new HarmonyMethod(typeof(WingItemService), nameof(DescriptionPostfix)));
        harmony.Patch(AccessTools.Method(typeof(StardewValley.Object), nameof(StardewValley.Object.canBeTrashed)),
            postfix: new HarmonyMethod(typeof(WingItemService), nameof(CanBeTrashedPostfix)));
    }

    private static bool InventoryRightClickPrefix(InventoryPage __instance, int x, int y)
    {
        ModEntry? mod = ModEntry.Instance;
        if (mod == null || !Context.IsWorldReady || Game1.player.CursorSlotItem != null) return true;
        int slot = __instance.inventory.getInventoryPositionOfClick(x, y);
        if (slot < 0 || slot >= Game1.player.Items.Count) return true;
        string? requested = WingIds.FromItem(Game1.player.Items[slot]);
        if (requested == null) return true;

        if (mod.Flight.Active)
        {
            mod.Notice("wing-change-grounded");
            return false;
        }
        if (!WingProgress.IsUnlocked(Game1.player))
        {
            mod.Notice("wings-locked");
            return false;
        }
        string previous = WingProgress.GetEquippedWingId(Game1.player);
        if (requested == previous)
        {
            mod.Notice("wing-already-equipped");
            return false;
        }
        Furniture replacement = CreateWingFurniture(previous);
        if (!WingProgress.TryEquipWing(Game1.player, requested, out _))
        {
            mod.Notice("wing-already-equipped");
            return false;
        }

        // Replacement happens in the clicked slot, so this remains valid even
        // when every inventory slot is occupied.
        Game1.player.Items[slot] = replacement;
        Game1.playSound("smallSelect");
        mod.Notice("wing-equipped");
        return false;
    }

    private static bool WallActionPrefix(Furniture __instance, Farmer who, bool justCheckingForActivity, ref bool __result)
    {
        string? requested = WingIds.FromItem(__instance);
        ModEntry? mod = ModEntry.Instance;
        if (requested == null || mod == null || who != Game1.player) return true;
        if (justCheckingForActivity)
        {
            __result = true;
            return false;
        }
        if (mod.Flight.Active)
        {
            mod.Notice("wing-change-grounded");
            __result = true;
            return false;
        }

        if (!WingProgress.IsUnlocked(who))
        {
            mod.Notice("wings-locked");
            __result = true;
            return false;
        }

        string previous = WingProgress.GetEquippedWingId(who);
        if (requested == previous)
        {
            mod.Notice("wing-already-equipped");
            __result = true;
            return false;
        }
        GameLocation? location = __instance.Location;
        if (location == null || !location.furniture.Contains(__instance)) return true;

        Furniture replacement = CreateWingFurniture(previous);
        replacement.SetPlacement(__instance.TileLocation, __instance.currentRotation.Value);
        if (!location.furniture.Remove(__instance)) return true;
        __instance.OnRemoved(location, __instance.TileLocation);
        location.furniture.Add(replacement);
        replacement.OnAdded(location, replacement.TileLocation);
        if (!WingProgress.TryEquipWing(who, requested, out _))
        {
            location.furniture.Remove(replacement);
            location.furniture.Add(__instance);
            __instance.OnAdded(location, __instance.TileLocation);
            return true;
        }
        Game1.playSound("smallSelect");
        mod.Notice("wing-equipped");
        __result = true;
        return false;
    }

    private static void DescriptionPostfix(Furniture __instance, ref string __result)
    {
        if (WingIds.FromItem(__instance) != null && ModEntry.Instance is { } mod)
            __result = Game1.parseText(mod.Translate("wing-item-description", null), Game1.smallFont, 320);
    }

    private static void CanBeTrashedPostfix(StardewValley.Object __instance, ref bool __result)
    {
        if (WingIds.FromItem(__instance) != null) __result = false;
    }
}
