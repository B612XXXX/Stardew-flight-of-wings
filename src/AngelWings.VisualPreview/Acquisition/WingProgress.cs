using StardewValley;

namespace AngelWings.VisualPreview.Acquisition;

/// <summary>Per-character progress, saved atomically with the game's money and other save data.</summary>
public static class WingProgress
{
    public const string StateKey = "AngelWingsProject.VisualPreview/Acquisition";
    public const string TutorialKey = "AngelWingsProject.VisualPreview/TutorialPending";
    public const string OwnedWingsKey = "AngelWingsProject.VisualPreview/OwnedWings";
    public const string EquippedWingKey = "AngelWingsProject.VisualPreview/EquippedWing";
    public const int Price = 1000;
    public const string ShopId = "HatMouse";
    public const string AngelWingId = "angel";
    public const string ButterflyWingId = "butterfly";

    private static readonly string[] WingOrder = [AngelWingId, ButterflyWingId];

    public static bool IsUnlocked(Farmer player) => player.modData.TryGetValue(StateKey, out string state)
        && state == "purchased";

    public static void Initialize(Farmer player)
    {
        // Only a completed purchase grants flight. This also migrates 0.3.0's
        // free legacy grant, while preserving every real purchase and its tutorial.
        if (!IsUnlocked(player))
        {
            player.modData[StateKey] = "locked";
            player.modData.Remove(TutorialKey);
            player.modData.Remove(OwnedWingsKey);
            player.modData.Remove(EquippedWingKey);
            return;
        }

        // 0.3.x purchases had no appearance fields. They migrate to owning and
        // wearing the original angel wings without creating an inventory item.
        GrantWing(player, AngelWingId);
        string equipped = GetEquippedWingId(player);
        if (!OwnsWing(player, equipped)) player.modData[EquippedWingKey] = AngelWingId;
    }

    public static bool UnlockPurchase(Farmer player)
    {
        if (IsUnlocked(player)) return false;
        player.modData[StateKey] = "purchased";
        player.modData[TutorialKey] = "true";
        GrantWing(player, AngelWingId);
        player.modData[EquippedWingKey] = AngelWingId;
        return true;
    }

    public static bool IsKnownWing(string? wingId) => wingId is AngelWingId or ButterflyWingId;

    public static bool OwnsWing(Farmer player, string wingId) => ParseOwned(player).Contains(wingId);

    public static bool GrantWing(Farmer player, string wingId)
    {
        if (!IsKnownWing(wingId)) return false;
        HashSet<string> owned = ParseOwned(player);
        if (!owned.Add(wingId)) return false;
        player.modData[OwnedWingsKey] = string.Join(',', WingOrder.Where(owned.Contains));
        return true;
    }

    public static string GetEquippedWingId(Farmer player)
    {
        if (player.modData.TryGetValue(EquippedWingKey, out string equipped)
            && IsKnownWing(equipped))
            return equipped;
        return AngelWingId;
    }

    /// <summary>Equip an owned physical wing form and return the appearance it replaced.</summary>
    public static bool TryEquipWing(Farmer player, string wingId, out string previousWingId)
    {
        previousWingId = GetEquippedWingId(player);
        if (!IsUnlocked(player) || !IsKnownWing(wingId)) return false;
        GrantWing(player, wingId);
        if (previousWingId == wingId) return false;
        player.modData[EquippedWingKey] = wingId;
        return true;
    }

    private static HashSet<string> ParseOwned(Farmer player)
    {
        var owned = new HashSet<string>(StringComparer.Ordinal);
        if (!player.modData.TryGetValue(OwnedWingsKey, out string raw)) return owned;
        foreach (string value in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (IsKnownWing(value)) owned.Add(value);
        return owned;
    }

    public static bool NeedsTutorial(Farmer player) => IsUnlocked(player)
        && player.modData.TryGetValue(TutorialKey, out string pending) && pending == "true";

    public static void TutorialShown(Farmer player) => player.modData.Remove(TutorialKey);
}
