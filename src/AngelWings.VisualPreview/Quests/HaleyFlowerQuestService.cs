using AngelWings.VisualPreview.Acquisition;
using AngelWings.VisualPreview.Wings;
using HarmonyLib;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Quests;
using StardewValley.TerrainFeatures;

namespace AngelWings.VisualPreview.Quests;

/// <summary>Haley's no-deadline, simultaneous-bloom butterfly-wing quest.</summary>
public sealed class HaleyFlowerQuestService
{
    public const string QuestId = "AngelWingsProject.VisualPreview_HaleyFlowerSea";
    public const string MailId = "AngelWingsProject.VisualPreview_HaleyFlowerSeaLetter";
    public const string StateKey = "AngelWingsProject.VisualPreview/HaleyFlowerSea";
#if ANGELWINGS_FORMAL
    public const int RequiredHaleyHearts = 8;
    public const int RequiredFlowers = 369;
#else
    // The default build keeps the same-day playtest conditions.
    public const int RequiredHaleyHearts = 3;
    public const int RequiredFlowers = 0;
#endif

    private readonly ModEntry Mod;
    private bool PresentingReward;
    internal int LastCount { get; private set; }

    public HaleyFlowerQuestService(ModEntry mod) => Mod = mod;

    public void Register(IModHelper helper)
    {
        helper.Events.Content.AssetRequested += OnAssetRequested;
        helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
        helper.Events.GameLoop.DayStarted += OnDayStarted;
        helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
        helper.Events.GameLoop.ReturnedToTitle += (_, _) =>
        {
            LastCount = 0;
            PresentingReward = false;
        };
    }

    private void OnAssetRequested(object? sender, AssetRequestedEventArgs e)
    {
        if (e.NameWithoutLocale.IsEquivalentTo("Data/Mail"))
        {
            e.Edit(asset =>
            {
                IDictionary<string, string> mail = asset.AsDictionary<string, string>().Data;
                mail[MailId] = Mod.Translate("haley-flower-letter", new { required = RequiredFlowers })
                    + $"^^%item quest {QuestId} %%";
            });
        }
        else if (e.NameWithoutLocale.IsEquivalentTo("Data/Quests"))
        {
            e.Edit(asset =>
            {
                IDictionary<string, string> quests = asset.AsDictionary<string, string>().Data;
                string farmName = Context.IsWorldReady ? Game1.player.farmName.Value : "";
                if (string.IsNullOrWhiteSpace(farmName)) farmName = Mod.Translate("farm-name-fallback", null);
                farmName = farmName.Replace('/', '／');
                // The legacy quest format is type/title/description/objective/conditions/
                // next quests/reward/reward description/cancellable.
                quests[QuestId] = BuildQuestData(
                    Mod.Translate("haley-flower-quest-title", null),
                    Mod.Translate("haley-flower-quest-description", new { farmName, required = RequiredFlowers }),
                    Mod.Translate("haley-flower-objective", new { count = 0, required = RequiredFlowers }));
            });
        }
    }

    public static string BuildQuestData(string title, string description, string objective)
    {
        // Data/Quests is a legacy slash-delimited asset with no escaping syntax.
        // Localized display text may contain '/', so replace it only in the raw
        // quest fields. The live objective patch can still use the normal text.
        static string SafeField(string value) => value.Replace('/', '／');
        return string.Join('/', "Basic", SafeField(title), SafeField(description), SafeField(objective),
            "", "", "-1", "", "false");
    }

    private void OnSaveLoaded(object? sender, SaveLoadedEventArgs e)
    {
        if (!Mod.CanPurchaseWings) return;
        PresentingReward = false;
        LastCount = 0;
        // Data/Quests may have been cached before a save supplied its farm name.
        Mod.Helper.GameContent.InvalidateCache("Data/Quests");
        if (!WingProgress.IsUnlocked(Game1.player)) return;
        NormalizeProgress(Game1.player);
        FlowerQuestState state = GetState(Game1.player);
        if (state is FlowerQuestState.Active or FlowerQuestState.Ready) EnsureQuestPresent(Game1.player);
        if (state == FlowerQuestState.Active) EvaluateFlowers();
    }

    private void OnDayStarted(object? sender, DayStartedEventArgs e)
    {
        if (!FeatureAllowed) return;
        NormalizeProgress(Game1.player);
        TryOffer(Game1.player);
        FlowerQuestState state = GetState(Game1.player);
        if (state is FlowerQuestState.Active or FlowerQuestState.Ready) EnsureQuestPresent(Game1.player);
        if (state == FlowerQuestState.Active) EvaluateFlowers();
    }

    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        if (!FeatureAllowed || !e.IsMultipleOf(60)) return;
        Farmer player = Game1.player;
        FlowerQuestState state = GetState(player);
        if (state == FlowerQuestState.Offered && player.hasQuest(QuestId))
        {
            SetState(player, FlowerQuestState.Active);
            state = FlowerQuestState.Active;
        }
        if (state == FlowerQuestState.Active) EvaluateFlowers();
    }

    private bool FeatureAllowed => Mod.CanPurchaseWings && WingProgress.IsUnlocked(Game1.player);

    private static void NormalizeProgress(Farmer player)
    {
        FlowerQuestState state = GetState(player);
        if (state == FlowerQuestState.None && player.hasQuest(QuestId)) SetState(player, FlowerQuestState.Active);
        else if ((state is FlowerQuestState.None or FlowerQuestState.Offered)
            && player.mailReceived.Contains(MailId) && !player.hasQuest(QuestId))
        {
            // Closing an attached letter normally accepts its quest. Repair the
            // narrow interruption window between marking mail read and cleanup.
            player.addQuest(QuestId);
            SetState(player, FlowerQuestState.Active);
        }
        else if (state == FlowerQuestState.Offered && !player.mailReceived.Contains(MailId)
            && !player.mailbox.Contains(MailId))
            player.mailbox.Add(MailId);
    }

    private static bool IsEligibleForOffer(Farmer player) => WingProgress.IsUnlocked(player)
        && Game1.currentSeason == "spring"
        && player.getFriendshipHeartLevelForNPC("Haley") >= RequiredHaleyHearts
        && GetState(player) == FlowerQuestState.None;

    private static void TryOffer(Farmer player)
    {
        if (!IsEligibleForOffer(player)) return;
        if (!player.mailReceived.Contains(MailId) && !player.mailbox.Contains(MailId)) player.mailbox.Add(MailId);
        SetState(player, FlowerQuestState.Offered);
    }

    private static void EnsureQuestPresent(Farmer player)
    {
        if (!player.hasQuest(QuestId)) player.addQuest(QuestId);
    }

    private void EvaluateFlowers()
    {
        LastCount = CountReadyFlowers();
        if (LastCount < RequiredFlowers) return;
        LastCount = RequiredFlowers;
        // Ready is latched. Harvesting after this moment cannot undo delivery.
        SetState(Game1.player, FlowerQuestState.Ready);
        Game1.dayTimeMoneyBox.questsDirty = true;
        Game1.playSound("newArtifact");
    }

    internal static int CountReadyFlowers()
    {
        int count = CountReadyFlowers(Game1.getFarm());
        GameLocation? greenhouse = Game1.getLocationFromName("Greenhouse");
        if (greenhouse != null && greenhouse != Game1.getFarm()) count += CountReadyFlowers(greenhouse);
        return count;
    }

    internal static int CountReadyFlowers(GameLocation location)
    {
        int count = 0;
        foreach (TerrainFeature feature in location.terrainFeatures.Values)
        {
            if (feature is not HoeDirt { crop: { } crop } || !IsHarvestReady(crop)) continue;
            var itemData = ItemRegistry.GetDataOrErrorItem(crop.indexOfHarvest.Value);
            if (!itemData.IsErrorItem && itemData.Category == StardewValley.Object.flowersCategory) count++;
        }
        return count;
    }

    public static bool IsHarvestReady(Crop crop) => !crop.dead.Value
        && crop.phaseDays.Count > 0
        && crop.currentPhase.Value >= crop.phaseDays.Count - 1
        && (!crop.fullyGrown.Value || crop.dayOfCurrentPhase.Value <= 0);

    public static void InstallPatches(Harmony harmony)
    {
        harmony.Patch(AccessTools.Method(typeof(NPC), nameof(NPC.checkAction), [typeof(Farmer), typeof(GameLocation)]),
            prefix: new HarmonyMethod(typeof(HaleyFlowerQuestService), nameof(HaleyCheckActionPrefix)));
        harmony.Patch(AccessTools.Method(typeof(Quest), nameof(Quest.GetObjectiveDescriptions)),
            postfix: new HarmonyMethod(typeof(HaleyFlowerQuestService), nameof(ObjectivesPostfix)));
    }

    private static bool HaleyCheckActionPrefix(NPC __instance, Farmer who, ref bool __result)
    {
        ModEntry? mod = ModEntry.Instance;
        HaleyFlowerQuestService? quest = mod?.HaleyFlowerQuest;
        if (quest == null || __instance.Name != "Haley" || who != Game1.player
            || GetState(who) != FlowerQuestState.Ready || quest.PresentingReward
            || __instance.IsInvisible || __instance.isSleeping.Value || !who.CanMove)
            return true;

        quest.PresentingReward = true;
        // #$b# in the translation keeps the three pages in one native Dialogue.
        // Its onFinish callback runs only when the final page is closed.
        var dialogue = new Dialogue(__instance, null, mod!.Translate("haley-flower-completion-dialogue", null));
        dialogue.onFinish = quest.CompleteReward;
        __instance.CurrentDialogue.Clear();
        __instance.CurrentDialogue.Push(dialogue);
        Game1.drawDialogue(__instance);
        __instance.faceTowardFarmerForPeriod(6000, 3, faceAway: false, who);
        __result = true;
        return false;
    }

    private void CompleteReward()
    {
        PresentingReward = false;
        Farmer player = Game1.player;
        if (GetState(player) != FlowerQuestState.Ready) return;
        var reward = WingItemService.CreateWingFurniture(WingProgress.ButterflyWingId);
        SetState(player, FlowerQuestState.Completed);
        WingProgress.GrantWing(player, WingProgress.ButterflyWingId);
        Quest? quest = player.questLog.FirstOrDefault(p => p.id.Value == QuestId);
        quest?.questComplete();
        player.addItemByMenuIfNecessary(reward);
    }

    private static void ObjectivesPostfix(Quest __instance, ref List<string> __result)
    {
        ModEntry? mod = ModEntry.Instance;
        if (__instance.id.Value != QuestId || mod?.HaleyFlowerQuest == null || !Context.IsWorldReady) return;
        FlowerQuestState state = GetState(Game1.player);
        __result = state == FlowerQuestState.Ready
            ? [mod.Translate("haley-flower-objective-ready", null)]
            : [mod.Translate("haley-flower-objective", new
            {
                count = Math.Min(mod.HaleyFlowerQuest.LastCount, RequiredFlowers),
                required = RequiredFlowers
            })];
    }

    internal static FlowerQuestState GetState(Farmer player)
    {
        if (!player.modData.TryGetValue(StateKey, out string value)) return FlowerQuestState.None;
        return Enum.TryParse(value, ignoreCase: true, out FlowerQuestState state) ? state : FlowerQuestState.None;
    }

    internal static void SetState(Farmer player, FlowerQuestState state)
    {
        if (state == FlowerQuestState.None) player.modData.Remove(StateKey);
        else player.modData[StateKey] = state.ToString().ToLowerInvariant();
    }
}

public enum FlowerQuestState
{
    None,
    Offered,
    Active,
    Ready,
    Completed
}
