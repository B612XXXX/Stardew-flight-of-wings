using AngelWings.VisualPreview.Acquisition;
using StardewValley;

namespace AngelWings.VisualPreview.Wings;

public static class WingIds
{
    public const string AngelFurnitureId = "AngelWingsProject.VisualPreview_AngelWings";
    public const string ButterflyFurnitureId = "AngelWingsProject.VisualPreview_ButterflyWings";
    // Data/Furniture is slash-delimited. SMAPI normalizes backslashes in asset
    // names, so this keeps the entire path inside the texture field.
    public const string ItemTextureAsset = @"Mods\AngelWingsProject.VisualPreview\WingItems";

    public static string? FromItem(Item? item) => item?.ItemId switch
    {
        AngelFurnitureId when item.TypeDefinitionId == "(F)" => WingProgress.AngelWingId,
        ButterflyFurnitureId when item.TypeDefinitionId == "(F)" => WingProgress.ButterflyWingId,
        _ => null
    };

    public static string FurnitureIdFor(string wingId) => wingId switch
    {
        WingProgress.AngelWingId => AngelFurnitureId,
        WingProgress.ButterflyWingId => ButterflyFurnitureId,
        _ => throw new ArgumentOutOfRangeException(nameof(wingId), wingId, "Unknown wing appearance.")
    };
}
