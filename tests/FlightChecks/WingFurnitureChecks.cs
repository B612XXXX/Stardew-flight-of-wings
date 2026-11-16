using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AngelWings.VisualPreview.Acquisition;
using AngelWings.VisualPreview.Wings;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.ItemTypeDefinitions;
using StardewValley.Objects;

// Exercise the real furniture parser, factory, source rectangles and wall draw.
// Only mod-content delivery and GPU submission are substituted; no save is opened.
static class WingFurnitureChecks
{
    private static int Count;
    private static readonly Dictionary<string, string> Data = new();
    private static readonly List<object> Draws = new();
    private static Texture2D Sheet = null!;
    private static string DrawingWing = "";
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); Count++; }
    private static bool FurnitureData(ref Dictionary<string, string> __result) { __result = Data; return false; }
    private static bool LoadTexture(ParsedItemData __instance, ref Texture2D __result)
    {
        Check(__instance.TextureName.Replace('\\', '/') == "Mods/AngelWingsProject.VisualPreview/WingItems",
            "The native furniture texture loader must receive the full mod asset path");
        __result = Sheet;
        return false;
    }
    private static bool Draw(Texture2D texture, Vector2 position, Rectangle? sourceRectangle,
        float scale, SpriteEffects effects, float layerDepth)
    {
        Rectangle rect = sourceRectangle!.Value;
        Check(ReferenceEquals(texture, Sheet), "Wall furniture must submit the wing texture, never an error sprite");
        Check(scale == 4f && effects == SpriteEffects.None, "Wall wings must use the same native pixel scale as worn wings");
        Draws.Add(new { wing = DrawingWing, x = position.X, y = position.Y,
            source_x = rect.X, source_y = rect.Y, width = rect.Width, height = rect.Height,
            scale, layer_depth = layerDepth, location_furniture = Furniture.isDrawingLocationFurniture });
        return false;
    }

    public static int Run()
    {
        Count = 0; Data.Clear(); Draws.Clear();
        foreach (string wing in new[] { WingProgress.AngelWingId, WingProgress.ButterflyWingId })
            Data[WingIds.FurnitureIdFor(wing)] = (string)AccessTools.Method(typeof(WingItemService), "BuildFurnitureData")
                .Invoke(null, new object[] { wing, "Fixture wings" })!;
        var harmony = new Harmony("AngelWingsProject.WingFurnitureChecks");
        bool registered = ItemRegistry.GetTypeDefinition("(F)") == null;
        bool wasDrawing = Furniture.isDrawingLocationFurniture;
        try
        {
            harmony.Patch(AccessTools.Method(typeof(DataLoader), nameof(DataLoader.Furniture)),
                prefix: new HarmonyMethod(typeof(WingFurnitureChecks), nameof(FurnitureData)));
            harmony.Patch(AccessTools.Method(typeof(ParsedItemData), "TryLoadTexture"),
                prefix: new HarmonyMethod(typeof(WingFurnitureChecks), nameof(LoadTexture)));
            harmony.Patch(AccessTools.Method(typeof(SpriteBatch), nameof(SpriteBatch.Draw),
                [typeof(Texture2D), typeof(Vector2), typeof(Rectangle?), typeof(Color), typeof(float),
                 typeof(Vector2), typeof(float), typeof(SpriteEffects), typeof(float)]),
                prefix: new HarmonyMethod(typeof(WingFurnitureChecks), nameof(Draw)));
            if (registered) ItemRegistry.AddTypeDefinition(new FurnitureDataDefinition());
            else ItemRegistry.ResetCache();
            Sheet = (Texture2D)RuntimeHelpers.GetUninitializedObject(typeof(Texture2D));
            Sheet.Name = "wall-wings";
            foreach ((string name, int value) in new[] { ("width", 160), ("height", 48) })
            {
                FieldInfo field = typeof(Texture2D).GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                    .First(f => f.FieldType == typeof(int) && f.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
                field.SetValue(Sheet, value);
            }
            var batch = (SpriteBatch)RuntimeHelpers.GetUninitializedObject(typeof(SpriteBatch));
            foreach (string wing in new[] { WingProgress.AngelWingId, WingProgress.ButterflyWingId })
            {
                string id = WingIds.FurnitureIdFor(wing);
                ParsedItemData parsed = new FurnitureDataDefinition().GetData(id);
                Check(parsed.TextureName.Replace('\\', '/') == "Mods/AngelWingsProject.VisualPreview/WingItems",
                    $"Furniture parser truncated the wing texture to '{parsed.TextureName}' for {wing}");
                Check(Data[id].Split('/').Length == 11, "Furniture data must retain its eleven fields");
                int index = wing == WingProgress.AngelWingId ? 0 : 5;
                Rectangle expected = new(index * 16, 0, 80, 48);
                Check(parsed.SpriteIndex == index, "Each appearance must select its own furniture cell");
                Furniture item = WingItemService.CreateWingFurniture(wing);
                item.SetPlacement(new Vector2(6, 2));
                Check(item.sourceRect.Value == expected && item.defaultSourceRect.Value == expected,
                    "The native furniture factory must use the complete, unscaled wing cell");
                Check(item.furniture_type.Value == Furniture.painting && item.questItem.Value && !item.canBeTrashed(),
                    "Wing furniture must remain wall-mountable and protected from trashing");

                // XML saves retain old source/bounding rectangles and the old butterfly index.
                item.ParentSheetIndex = wing == WingProgress.AngelWingId ? 0 : 4;
                item.sourceRect.Value = item.defaultSourceRect.Value = new Rectangle(item.ParentSheetIndex * 16, 0, 64, 48);
                item.boundingBox.Value = item.defaultBoundingBox.Value = new Rectangle(6 * 64, 2 * 64, 4 * 64, 3 * 64);
                foreach (int repeat in new[] { 0, 1 })
                {
                    AccessTools.Method(typeof(WingItemService), "RefreshWingFurniture").Invoke(null, new object[] { item });
                    Check(item.TileLocation == new Vector2(6, 2) && item.ItemId == id && item.questItem.Value,
                        "Refreshing stored wings must preserve their identity, tile and protection");
                    Check(item.ParentSheetIndex == index && item.sourceRect.Value == expected && item.defaultSourceRect.Value == expected,
                        "Previously saved wall wings must migrate to the current texture rectangle, idempotently");
                    Check(item.boundingBox.Value == new Rectangle(6 * 64, 2 * 64, 5 * 64, 3 * 64),
                        "The loaded furniture bounds must match its full-size sprite");
                }
                DrawingWing = wing;
                foreach (bool locationFurniture in new[] { false, true })
                {
                    Furniture.isDrawingLocationFurniture = locationFurniture;
                    item.draw(batch, 6, 2);
                    var record = JsonSerializer.SerializeToElement(Draws[^1]);
                    Check(record.GetProperty("source_x").GetInt32() == expected.X
                        && record.GetProperty("width").GetInt32() == 80 && record.GetProperty("height").GetInt32() == 48,
                        "Both native wall draw paths must select the matching whole wing cell");
                }
            }
            var ordinary = new Furniture { ItemId = "fixture-normal-furniture" };
            ordinary.sourceRect.Value = new Rectangle(1, 2, 16, 16);
            AccessTools.Method(typeof(WingItemService), "RefreshWingFurniture").Invoke(null, new object[] { ordinary });
            Check(ordinary.sourceRect.Value == new Rectangle(1, 2, 16, 16), "Other furniture must keep its existing source rectangle");
            string output = Path.GetFullPath("build/qa-wall-wing-0.4.11");
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, "native-furniture-draws.json"), JsonSerializer.Serialize(Draws, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("PASS: native furniture texture parsing, both wall draw paths, full-size cells and existing-item migration.");
            return Count;
        }
        finally
        {
            Furniture.isDrawingLocationFurniture = wasDrawing;
            if (registered)
            {
                ItemRegistry.ItemTypes.RemoveAll(type => type.Identifier == "(F)");
                ((Dictionary<string, IItemDataDefinition>)AccessTools.Field(typeof(ItemRegistry), "IdentifierLookup").GetValue(null)!).Remove("(F)");
            }
            ItemRegistry.ResetCache();
            harmony.UnpatchAll(harmony.Id);
        }
    }
}
