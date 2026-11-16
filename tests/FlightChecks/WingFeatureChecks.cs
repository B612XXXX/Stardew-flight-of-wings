using System.Buffers.Binary;
using System.Text.Json;
using HarmonyLib;
using AngelWings.VisualPreview.Acquisition;
using AngelWings.VisualPreview.Quests;
using AngelWings.VisualPreview.Wings;
using StardewValley;

static class WingFeatureChecks
{
    private static int Count;
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); Count++; }

    public static int Run(Farmer player)
    {
        Count = 0;
        CheckAppearanceMigration(player);
        CheckFlowerReadiness();
        CheckLocalizedContract();
        Count += WingFurnitureChecks.Run();
        CheckRuntimeArt();
        Console.WriteLine("PASS: multi-wing migration, flower threshold contract, exact Haley text and butterfly runtime assets.");
        return Count;
    }

    private static void CheckAppearanceMigration(Farmer player)
    {
        player.modData.Clear();
        player.modData[WingProgress.StateKey] = "purchased";
        WingProgress.Initialize(player);
        Check(WingProgress.OwnsWing(player, WingProgress.AngelWingId)
            && WingProgress.GetEquippedWingId(player) == WingProgress.AngelWingId,
            "0.3.x purchasers must migrate to owning and wearing angel wings");
        Check(WingProgress.GrantWing(player, WingProgress.ButterflyWingId)
            && !WingProgress.GrantWing(player, WingProgress.ButterflyWingId),
            "Butterfly ownership must be idempotent");
        Check(WingProgress.TryEquipWing(player, WingProgress.ButterflyWingId, out string previous)
            && previous == WingProgress.AngelWingId
            && WingProgress.GetEquippedWingId(player) == WingProgress.ButterflyWingId,
            "Equipping butterfly wings must atomically identify the angel appearance it replaces");
        WingProgress.Initialize(player);
        Check(WingProgress.GetEquippedWingId(player) == WingProgress.ButterflyWingId,
            "Selected appearance must survive save initialization");
        Check(!WingProgress.TryEquipWing(player, "unknown", out _), "Unknown appearances must never be equipped");
        player.modData[WingProgress.StateKey] = "locked";
        WingProgress.Initialize(player);
        Check(!player.modData.ContainsKey(WingProgress.OwnedWingsKey)
            && !player.modData.ContainsKey(WingProgress.EquippedWingKey),
            "Locked characters must not retain appearance ownership fields");
        Check(WingIds.FurnitureIdFor(WingProgress.ButterflyWingId) == WingIds.ButterflyFurnitureId,
            "The butterfly inventory/wall item must map to the butterfly runtime appearance");
        player.modData.Clear();
    }

    private static void CheckFlowerReadiness()
    {
        var crop = new Crop();
        crop.phaseDays.Add(1);
        crop.phaseDays.Add(99999);
        crop.currentPhase.Value = 1;
        Check(HaleyFlowerQuestService.IsHarvestReady(crop), "A living final-phase crop must be harvest-ready");
        crop.dead.Value = true;
        Check(!HaleyFlowerQuestService.IsHarvestReady(crop), "Dead flowers must not count");
        crop.dead.Value = false;
        crop.fullyGrown.Value = true;
        crop.dayOfCurrentPhase.Value = 2;
        Check(!HaleyFlowerQuestService.IsHarvestReady(crop), "A regrowing flower on cooldown must not count");
        crop.dayOfCurrentPhase.Value = 0;
        Check(HaleyFlowerQuestService.IsHarvestReady(crop), "A regrowing flower must count again when harvestable");
        crop.currentPhase.Value = 0;
        Check(!HaleyFlowerQuestService.IsHarvestReady(crop), "An immature living flower must not count");
#if ANGELWINGS_FORMAL
        Check(HaleyFlowerQuestService.RequiredHaleyHearts == 8,
            "The formal build must offer Haley's quest at eight hearts");
        Check(HaleyFlowerQuestService.RequiredFlowers == 369,
            "The formal build must require 369 simultaneous harvest-ready flowers");
#else
        Check(HaleyFlowerQuestService.RequiredHaleyHearts == 3,
            "The quick playtest build must offer Haley's quest at three hearts");
        Check(HaleyFlowerQuestService.RequiredFlowers == 0,
            "The zero-flower playtest build must make the quest immediately ready after acceptance");
#endif
    }

    private static void CheckLocalizedContract()
    {
        var zh = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "i18n", "zh.json")))!;
        string required = HaleyFlowerQuestService.RequiredFlowers.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string description = zh["haley-flower-quest-description"].Replace("{{farmName}}", "星露")
            .Replace("{{required}}", required);
        Check(description == $"海莉想拍下一片五颜六色的花海。让星露农场的 {required} 株花同时盛开，任何花卉都可以。",
            "The Chinese quest card must display the compiled profile's flower requirement");
        Check(zh["haley-flower-letter"].Replace("{{required}}", required).Contains($" {required} 株花同时盛开"),
            "The Chinese letter must use the same requirement as the quest");
        string rawQuest = HaleyFlowerQuestService.BuildQuestData(
            zh["haley-flower-quest-title"], description,
            zh["haley-flower-objective"].Replace("{{count}}", "0").Replace("{{required}}", required));
        string[] questFields = rawQuest.Split('/');
        Check(questFields.Length == 9 && int.TryParse(questFields[6], out int reward) && reward == -1,
            "Localized objective text must not corrupt the slash-delimited Data/Quests fields");
        string[] zhPages =
        [
            "我路过你的农场时看到那片花海了......真的太美了!",
            "我还拍下了很多漂亮的照片。",
            "谢谢为我做了这些。这是我答应你的神秘礼物。我觉得，它一定很适合你。"
        ];
        Check(zh["haley-flower-completion-dialogue"].Split("#$b#").SequenceEqual(zhPages),
            "Haley's Chinese completion dialogue must have exactly the three requested pages");
        var en = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "i18n", "default.json")))!;
        Check(en["haley-flower-letter"].Replace("{{required}}", required).Contains($" {required} flowers bloom")
            && en["haley-flower-quest-description"].Replace("{{required}}", required).Contains($" {required} flowers blooming"),
            "The English letter and quest card must use the compiled profile's requirement");
        Check(en.Keys.OrderBy(p => p).SequenceEqual(zh.Keys.OrderBy(p => p)),
            "English and Chinese translation keys must stay aligned");
        string[] enPages =
        [
            "I saw that field of flowers when I passed your farm... It was so beautiful!",
            "I took lots of lovely photos, too.",
            "Thank you for doing this for me. Here's the mysterious gift I promised. I think it'll look perfect on you."
        ];
        Check(en["haley-flower-completion-dialogue"].Split("#$b#").SequenceEqual(enPages),
            "Haley's English completion dialogue must use the same three-page structure");
        CheckNativeDialoguePages(zh["haley-flower-completion-dialogue"], zhPages);
        CheckNativeDialoguePages(en["haley-flower-completion-dialogue"], enPages);
    }

    private static void CheckNativeDialoguePages(string text, string[] expectedPages)
    {
        // Only skip unrelated random-word localization, which needs a graphics
        // content manager. Parsing, page advancement and onFinish are vanilla.
        var translatedField = AccessTools.Field(typeof(Dialogue), "nameArraysTranslated");
        object? previousTranslated = translatedField.GetValue(null);
        translatedField.SetValue(null, true);
        try
        {
            int rewards = 0;
            var dialogue = new Dialogue(new NPC { Name = "Haley" }, null, text);
            dialogue.onFinish = () => rewards++;
            Check(dialogue.dialogues.Count == 3 && rewards == 0,
                "The native parser must create three pages without granting the reward");
            for (int i = 0; i < expectedPages.Length; i++)
            {
                Check(dialogue.getCurrentDialogue() == expectedPages[i],
                    $"Native Haley dialogue page {i + 1} must match its exact text");
                Check(dialogue.isOnFinalDialogue() == (i == 2),
                    "Only the third page may be the final reward dialogue");
                dialogue.exitCurrentDialogue();
                Check(rewards == (i == 2 ? 1 : 0),
                    "The finish callback must wait until the third page is closed");
            }
            Check(dialogue.isDialogueFinished(), "Closing the third page must finish the conversation");
        }
        finally { translatedField.SetValue(null, previousTranslated); }
    }

    private static void CheckRuntimeArt()
    {
        foreach (string state in new[] { "H", "M", "Q", "T", "L" })
        foreach (string view in new[] { "back", "right", "front", "left" })
        foreach (string layer in new[] { "behind", "in_front" })
        {
            string path = Path.Combine(AppContext.BaseDirectory, "assets", "butterfly", state, view, layer + ".png");
            (int width, int height) = ReadPngSize(path);
            Check(width == (state is "T" or "L" ? 800 : 1280) && height == 80,
                $"Butterfly atlas dimensions changed: {state}/{view}/{layer}");
        }
        Check(ReadPngSize(Path.Combine(AppContext.BaseDirectory, "assets", "items", "wing-items.png")) == (160, 48),
            "Wing furniture sheet must contain two full-size 5x3-tile sprites");
    }

    private static (int Width, int Height) ReadPngSize(string path)
    {
        byte[] header = File.ReadAllBytes(path);
        Check(header.Length >= 24 && header.AsSpan(1, 3).SequenceEqual("PNG"u8), $"Missing or invalid PNG: {path}");
        return (BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(16, 4)),
            BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(20, 4)));
    }
}
