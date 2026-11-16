using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using AngelWings.VisualPreview.Flight;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Content;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewModdingAPI.Utilities;
using StardewValley;
using xTile;
using xTile.Layers;
using xTile.Tiles;
using WingsEntry = AngelWings.VisualPreview.ModEntry;
using SwimEntry = Swim.ModEntry;

// Executes the production compatibility module against the original Swim DLL.
// Graphics, devices, sound and SMAPI context are substituted; saves are never opened.
static class Program
{
    static readonly List<object> Results = new();
    static readonly List<string> Notices = new();
    static bool Manual, Prevent, Moving = true, Free = true, CanMove = true;
    static bool FailBeforeEntry, FailAfterEntry, FailInspection, EffectsBypassedDuringEntry;
    static int ControllerTicks;
    static Swim.ModConfig Config = null!;
    static WingsEntry Mod = null!;
    static bool Skip() => false;
    static bool Yes(ref bool __result) { __result = true; return false; }
    static bool No(ref bool __result) { __result = false; return false; }
    static bool Zero(ref int __result) { __result = 0; return false; }
    static bool ReadFree(ref bool __result) { __result = Free; return false; }
    static bool ReadCanMove(ref bool __result) { __result = CanMove; return false; }
    static bool ReadMoving(ref bool __result) { __result = Moving; return false; }
    static bool NativeSpeed(ref float __result) { __result = 6; return false; }
    static bool CaptureNotice(string key) { Notices.Add(key); return false; }
    static bool CountController() { ControllerTicks++; return false; }
    static bool KeyDown(KeybindList __instance, ref bool __result)
    {
        __result = ReferenceEquals(__instance, Config.ManualJumpButton) ? Manual
            : ReferenceEquals(__instance, Config.PreventJumpButton) && Prevent;
        return false;
    }
    static void EntryPrefix()
    {
        EffectsBypassedDuringEntry = !(bool)AccessTools.Method(typeof(WingsEntry), "CanUseFlightCollision").Invoke(Mod, new object[] { Game1.player })!
            && Game1.player.getMovementSpeed() == 6;
        if (FailBeforeEntry) throw new InvalidOperationException("injected pre-entry failure");
    }
    static void EntryPostfix()
    {
        if (FailAfterEntry) throw new InvalidOperationException("injected partial-entry failure");
    }
    static void InspectionPrefix()
    {
        if (FailInspection) throw new InvalidOperationException("injected inspection failure");
    }
    static void Set(object target, string name, object? value) => AccessTools.Field(target.GetType(), name).SetValue(target, value);
    static void Check(bool ok, string name)
    {
        Results.Add(new { check = name, passed = ok });
        if (!ok) throw new Exception(name);
        Console.WriteLine("PASS: " + name);
    }

    static void Main(string[] args)
    {
        string game = args[0];
        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            foreach (string folder in new[] { game, Path.Combine(game, "smapi-internal") })
            {
                string path = Path.Combine(folder, name.Name + ".dll");
                if (File.Exists(path)) return context.LoadFromAssemblyPath(path);
            }
            return null;
        };
        try { Run(); }
        finally
        {
            string source = typeof(WingsEntry).Assembly.Location;
            File.WriteAllText(args[1], JsonSerializer.Serialize(new
            {
                flight_version = typeof(WingsEntry).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
                flight_sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source))).ToLowerInvariant(),
                swim_sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(SwimEntry).Assembly.Location))).ToLowerInvariant(),
                checks = Results, logs = ProbeMonitor.Logs, game_session_tested = false, saves_opened = false
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Run()
    {
        var harmony = new Harmony("AngelWings.ProductionSwimIntegrationChecks");
        try
        {
            void Prefix(Type type, string method, string callback) => harmony.Patch(AccessTools.Method(type, method), prefix: new HarmonyMethod(typeof(Program), callback));
            Prefix(typeof(Farmer), "farmerInit", nameof(Skip));
            Prefix(typeof(Farmer), "getMovementSpeed", nameof(NativeSpeed));
            Prefix(typeof(StardewValley.BellsAndWhistles.PlayerStatusList), "AddSpriteDefinition", nameof(Skip));
            Prefix(typeof(AnimatedSprite), "LoadTexture", nameof(Skip));
            Prefix(typeof(StardewValley.Objects.Furniture), "timeToTurnOnLights", nameof(No));
            foreach (string method in new[] { "get_IsWorldReady", "get_IsMainPlayer" }) Prefix(typeof(Context), method, nameof(Yes));
            Prefix(typeof(Context), "get_IsPlayerFree", nameof(ReadFree));
            Prefix(typeof(Context), "get_CanPlayerMove", nameof(ReadCanMove));
            Prefix(typeof(Context), "get_ScreenId", nameof(Zero));
            foreach (string method in new[] { "get_IsMultiplayer", "get_HostPaused", "get_IsChatting", "get_fadeToBlack" }) Prefix(typeof(Game1), method, nameof(No));
            Prefix(typeof(WingsEntry), "UpdateController", nameof(CountController));
            Prefix(typeof(WingsEntry), "Notice", nameof(CaptureNotice));
            Prefix(typeof(GameLocation), "isFarmerCollidingWithAnyCharacter", nameof(No));
            Prefix(typeof(GameLocation), "playSound", nameof(Skip));
            Prefix(typeof(Swim.SwimUtils), "UpdateOxygenValue", nameof(Skip));
            Prefix(typeof(Swim.SwimUtils), "CanSwimHere", nameof(InspectionPrefix));
            Prefix(typeof(KeybindList), "IsDown", nameof(KeyDown));
            Prefix(typeof(Character), "isMoving", nameof(ReadMoving));
            Prefix(typeof(UpdateTickingEventArgs), "get_IsOneSecond", nameof(No));

            Game1.game1 = (Game1)RuntimeHelpers.GetUninitializedObject(typeof(Game1));
            Game1.options = (Options)RuntimeHelpers.GetUninitializedObject(typeof(Options));
            Game1.currentGameTime = new GameTime(TimeSpan.Zero, TimeSpan.FromMilliseconds(100));
            Game1.displayFarmer = true;
            Game1.viewport = new xTile.Dimensions.Rectangle(0, 0, 1280, 1280);
            var who = new Farmer();
            AccessTools.Field(typeof(Game1), "_player").SetValue(null, who);
            who.Sprite = new FarmerSprite(null); who.CanMove = true; who.health = 100;
            who.movementDirections = new List<int>(); who.millisecondsPlayed = 10000;
            var location = CreateLocation();
            Game1.game1.instanceGameLocation = location; who.currentLocation = location;
            var monitor = DispatchProxy.Create<IMonitor, ProbeMonitor>();
            Config = new Swim.ModConfig();
            AccessTools.Property(typeof(SwimEntry), "Config").SetValue(null, Config);
            Swim.SwimUtils.Initialize(monitor, null!); Swim.SwimHelperEvents.Initialize(monitor, null!);
            var swimPatches = typeof(SwimEntry).Assembly.GetType("Swim.SwimPatches")!;
            AccessTools.Method(swimPatches, "Initialize").Invoke(null, new object?[] { monitor, null });
            var animation = typeof(SwimEntry).Assembly.GetType("Swim.AnimationManager")!;
            AccessTools.Method(animation, "Initialize").Invoke(null, new object?[] { monitor, null });
            Mod = new WingsEntry(); Set(Mod, "Ready", true); Set(Mod, "Config", new AngelWings.VisualPreview.ModConfig());
            AccessTools.Property(typeof(Mod), "Monitor").SetValue(Mod, monitor);
            AccessTools.Field(typeof(WingsEntry), "Instance").SetValue(null, Mod);
            var flight = (FlightController)AccessTools.Field(typeof(WingsEntry), "Flight").GetValue(Mod)!;
            var safety = (FlightSafety)AccessTools.Field(typeof(WingsEntry), "Safety").GetValue(Mod)!;
            var shore = new Vector2(5 * 64, 5 * 64);
            var water = new Vector2(8 * 64, 5 * 64);
            WingsEntry.InstallPatches(harmony);
            AccessTools.Method(animation, "Patch").Invoke(null, new object[] { harmony });
            var collision = typeof(GameLocation).GetMethods().Single(m => m.Name == "isCollidingPosition" && m.GetParameters().Length == 10);
            harmony.Patch(collision, postfix: new HarmonyMethod(swimPatches, "GameLocation_isCollidingPosition_Postfix"));
            var entry = AccessTools.Method(typeof(Swim.SwimHelperEvents), "DoJump");
            harmony.Patch(entry, prefix: new HarmonyMethod(typeof(Program), nameof(EntryPrefix)), postfix: new HarmonyMethod(typeof(Program), nameof(EntryPostfix)));
            var compatibilityType = typeof(WingsEntry).Assembly.GetType("AngelWings.VisualPreview.Compatibility.SwimCompatibility")!;
            object NewCompatibility() => Activator.CreateInstance(compatibilityType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new object[] { Mod }, null)!;
            var compatibility = NewCompatibility(); Set(Mod, "Swim", compatibility);
            bool Bind(object value, string version) => (bool)AccessTools.Method(compatibilityType, "Bind").Invoke(value, new object[] { typeof(SwimEntry).Assembly, version })!;
            bool Waiting() => (bool)AccessTools.Property(compatibilityType, "Waiting").GetValue(compatibility)!;
            void Toggle() => AccessTools.Method(typeof(WingsEntry), "ToggleFlight").Invoke(Mod, null);
            void TickWings() => AccessTools.Method(typeof(WingsEntry), "OnUpdate").Invoke(Mod, new object?[] { null, RuntimeHelpers.GetUninitializedObject(typeof(UpdateTickingEventArgs)) });
            void TickSwim() => Swim.SwimHelperEvents.GameLoop_UpdateTicked(null!, (UpdateTickedEventArgs)RuntimeHelpers.GetUninitializedObject(typeof(UpdateTickedEventArgs)));
            void Fly()
            {
                AccessTools.Method(typeof(WingsEntry), "ResetSession").Invoke(Mod, null);
                who.swimming.Value = who.bathingClothes.Value = false;
                who.freezePause = 0; who.UsingTool = who.isEating = false;
                who.CanMove = true; who.canOnlyWalk = false; who.running = true; who.speed = 5;
                who.movementDirections.Clear(); who.movementDirections.Add(1);
                who.millisecondsPlayed += 1000;
                Swim.SwimHelperEvents.isJumping.Value = Swim.SwimHelperEvents.willSwim.Value = false;
                Swim.SwimHelperEvents.lastJump.Value = 0;
                SwimEntry.isUnderwater.Value = SwimEntry.locationIsPool.Value = SwimEntry.locationProhibitsSwimming.Value = false;
                Config.EnableMod = true; Config.ReadyToSwim = false; Config.NoAutoSwimSuit = Config.SwimSuitAlways = false;
                Free = CanMove = true; Manual = Prevent = FailBeforeEntry = FailAfterEntry = FailInspection = false;
                Game1.eventUp = Game1.eventOver = Game1.paused = Game1.dialogueUp = Game1.freezeControls = false;
                Game1.activeClickableMenu = null; Game1.locationRequest = null; location.currentEvent = null;
                Game1.game1.instanceGameLocation = location; who.currentLocation = location;
                who.Position = shore;
                safety.Remember(who);
                Set(Mod, "Surface", new FlightMap(location, new Point(5, 5)));
                AngelWings.VisualPreview.Acquisition.WingProgress.UnlockPurchase(who);
                Toggle(); for (int i = 0; i < 7; i++) flight.Advance(100, true, false, false);
                who.Position = water;
                Notices.Clear(); EffectsBypassedDuringEntry = false;
            }
            void FinishEntry() { who.freezePause = 0; TickSwim(); }
            void SurfaceSwim() { Fly(); Toggle(); FinishEntry(); TickWings(); }
            void ExitTo(Vector2 destination)
            {
                entry.Invoke(null, new object[] { destination / 64, -1 });
                who.freezePause = 0; TickSwim();
            }

            Check(Bind(compatibility, "1.9.0"), "Production adapter binds to the original Swim 1.9.0 DLL");
            Check(typeof(WingsEntry).Assembly.GetReferencedAssemblies().All(a => a.Name != "Swim"), "Flight runtime has no hard reference to Swim.dll");
            var gateInfo = Harmony.GetPatchInfo(AccessTools.Method(typeof(Swim.SwimUtils), "IsSafeToTryJump"));
            Check(gateInfo.Postfixes.Any(p => p.owner == "AngelWingsProject.VisualPreview.SwimCompatibility")
                && !gateInfo.Prefixes.Any(p => p.owner == "AngelWingsProject.VisualPreview.SwimCompatibility"), "Swim's entry predicate keeps its original method and gets a postfix filter");
            Fly(); Config.ReadyToSwim = true;
            TickSwim(); TickWings();
            Check(flight.Active && !who.swimming.Value && !Swim.SwimHelperEvents.isJumping.Value, "Automatic swimming cannot interrupt flight when Swim updates first");
            TickWings(); TickSwim();
            Check(flight.Active && !who.swimming.Value && !Swim.SwimHelperEvents.isJumping.Value, "Automatic swimming cannot interrupt flight when flight updates first");
            Config.ReadyToSwim = false; Manual = true; TickSwim();
            Check(flight.Active && !Swim.SwimHelperEvents.isJumping.Value, "Right-click swimming cannot interrupt flight");
            Swim.SwimUtils.DiveTo(null!);
            Check(flight.Active && !who.swimming.Value, "Dive entry is stopped before its native body can change maps");
            Fly();
            string progress = "compatibility-must-not-change-quest";
            who.modData["AngelWingsProject.VisualPreview/HaleyFlowerSea"] = progress;
            var originalKeys = who.modData.Keys.Where(k => k != "AngelWingsProject.VisualPreview/Recovery").ToArray();
            string swimSettings = JsonSerializer.Serialize(Config);
            Toggle();
            Check(!flight.Active && Swim.SwimHelperEvents.isJumping.Value && Swim.SwimHelperEvents.willSwim.Value, "Actual flight toggle starts native entry with Swim automatic entry disabled");
            Check(EffectsBypassedDuringEntry, "Flight collision and speed effects are bypassed while Swim's native entry runs");
            Check(who.freezePause == Config.JumpTimeInMilliseconds && who.bathingClothes.Value, "Native Swim entry owns its configured animation time and swimsuit change");
            Check(JsonSerializer.Serialize(Config) == swimSettings, "Entry does not change any Swim configuration or key bindings");
            Check(!who.modData.ContainsKey("AngelWingsProject.VisualPreview/Recovery") && !Waiting(), "Successful entry removes the old flight recovery marker and pending request");
            Check(who.modData["AngelWingsProject.VisualPreview/HaleyFlowerSea"] == progress
                && originalKeys.All(who.modData.ContainsKey), "Entry preserves wing ownership, appearance and quest metadata");
            int errors = ProbeMonitor.Logs.Count(s => s.Contains("Error"));
            TickWings(); FinishEntry(); TickWings();
            Check(who.swimming.Value && !flight.Active && who.Tile == water / 64, "Swim finishes entry at the same surface tile without flight recovery");
            Check(ProbeMonitor.Logs.Count(s => s.Contains("Error")) == errors, "Swimming does not trigger failed shore recovery");
            Check(who.getMovementSpeed() == 6, "Swimming has no flight speed multiplication");
            Vector2 surfacePosition = who.Position;
            Toggle();
            Check(flight.Stage == FlightStage.TakingOff && flight.Animation == "T" && !who.swimming.Value,
                "Surface swimming starts the original wing takeoff directly");
            Check(who.Position == surfacePosition && !Swim.SwimHelperEvents.isJumping.Value && who.freezePause == 0,
                "Surface takeoff preserves exact position without a second jump or movement freeze");
            Check(!who.bathingClothes.Value && !who.canOnlyWalk && Swim.SwimHelperEvents.lastJump.Value == who.millisecondsPlayed,
                "Surface takeoff completes native exit clothing rules and cooldown");
            Check(JsonSerializer.Serialize(Config) == swimSettings && who.modData["AngelWingsProject.VisualPreview/HaleyFlowerSea"] == progress,
                "Surface takeoff preserves Swim settings and quest metadata");
            Config.ReadyToSwim = true; who.millisecondsPlayed += 1000;
            for (int i = 0; i < 7; i++) { TickSwim(); TickWings(); }
            Check(flight.CanTravel && !who.swimming.Value && !Swim.SwimHelperEvents.isJumping.Value,
                "Native automatic swimming cannot reclaim a surface-origin takeoff");
            Toggle(); FinishEntry(); TickWings();
            Check(who.swimming.Value && !flight.Active && who.Position == surfacePosition,
                "Flight and surface swimming complete a round trip at the same tile");
            int controllerTicks = ControllerTicks; TickWings();
            Check(ControllerTicks == controllerTicks + 1, "Native swimming still routes controller gestures through the existing input recognizer");
            entry.Invoke(null, new object[] { shore / 64, -1 });
            Check(!who.swimming.Value && Swim.SwimHelperEvents.isJumping.Value, "Original Swim clears its swimming flag before the exit animation finishes");
            who.Position = shore; Toggle();
            Check(!flight.Active, "Even over dry ground, unfinished Swim exit animation blocks takeoff");
            FinishEntry(); Toggle();
            Check(flight.Stage == FlightStage.TakingOff, "Takeoff is enabled after native exit has finished on legal shore");

            SurfaceSwim(); SwimEntry.isUnderwater.Value = true; Toggle();
            Check(!flight.Active && who.swimming.Value && Notices.Contains("swim-surface-first"),
                "Cached underwater state blocks wings and requests a return to the surface");
            controllerTicks = ControllerTicks; TickWings();
            Check(ControllerTicks == controllerTicks + 1, "Underwater swimming also routes controller gestures for the refusal notice");
            SwimEntry.isUnderwater.Value = false;
            SwimEntry.diveMaps[location.Name] = new Swim.Models.DiveMap { Name = location.Name, Features = new() { "Underwater" } };
            Notices.Clear(); Toggle();
            Check(!flight.Active && who.swimming.Value && Notices.Contains("swim-surface-first"),
                "An underwater map blocks takeoff before Swim refreshes its cached flag");
            who.swimming.Value = false; Notices.Clear(); Toggle();
            Check(!flight.Active && Notices.Contains("swim-surface-first"), "Underwater maps block wings even before the native swimming flag is set");
            SwimEntry.diveMaps.Remove(location.Name); who.swimming.Value = true; Toggle();
            Check(flight.Stage == FlightStage.TakingOff && !who.swimming.Value, "Returning to the surface permits direct takeoff again");
            var chinese = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "i18n/zh.json")))!;
            Check(chinese["swim-surface-first"] == "请回到水面后再打开翅膀", "The Chinese underwater notice exactly matches the requested text");

            Fly(); Toggle(); Toggle();
            Check(!flight.Active && Swim.SwimHelperEvents.isJumping.Value && Notices.Contains("swim-wait-transition"),
                "An unfinished native water entry cannot be interrupted by wing takeoff");
            SurfaceSwim(); who.UsingTool = true; Toggle();
            Check(!flight.Active && who.swimming.Value && who.UsingTool, "Surface takeoff does not cancel an active native tool action");
            SurfaceSwim(); CanMove = false; Toggle();
            Check(!flight.Active && who.swimming.Value, "Surface takeoff respects native movement locks");
            SurfaceSwim(); Config.SwimSuitAlways = true; Toggle();
            Check(flight.Active && who.bathingClothes.Value && who.canOnlyWalk,
                "Surface takeoff preserves Swim's always-swimsuit preference");
            SurfaceSwim(); Config.NoAutoSwimSuit = true; who.bathingClothes.Value = who.canOnlyWalk = false; Toggle();
            Check(flight.Active && !who.bathingClothes.Value && !Config.SwimSuitAlways && Config.NoAutoSwimSuit,
                "Surface takeoff does not override the no-auto-swimsuit preference");
            SurfaceSwim(); var waterBack = location.Map.GetLayer("Back"); waterBack.Tiles[8, 5].Properties["AngelWingsNoFly"] = "T"; Toggle();
            Check(!flight.Active && who.swimming.Value, "Surface takeoff respects explicit no-flight map tiles");
            waterBack.Tiles[8, 5].Properties.Remove("AngelWingsNoFly");
            SurfaceSwim(); location.objects[who.Tile] = new BlockingObject { TileLocation = who.Tile }; Toggle();
            Check(!flight.Active && who.swimming.Value, "Occupied surface water cannot start takeoff");
            location.objects.Clear();

            foreach (bool partial in new[] { false, true })
            {
                SurfaceSwim(); who.Position += new Vector2(3, 2); who.movementDirections.Clear(); who.movementDirections.Add(3);
                Vector2 position = who.Position; bool running = who.running; int speed = who.speed;
                ulong last = Swim.SwimHelperEvents.lastJump.Value;
                FailBeforeEntry = !partial; FailAfterEntry = partial; Toggle();
                Check(!flight.Active && who.swimming.Value && !Swim.SwimHelperEvents.isJumping.Value && who.freezePause == 0,
                    "Failed takeoff restores stable native swimming: partial=" + partial);
                Check(who.Position == position && who.bathingClothes.Value && who.canOnlyWalk && who.running == running && who.speed == speed
                    && who.movementDirections.SequenceEqual(new[] { 3 }) && Swim.SwimHelperEvents.lastJump.Value == last,
                    "Failed takeoff restores position, clothing, movement and cooldown: partial=" + partial);
                Check(!who.modData.ContainsKey("AngelWingsProject.VisualPreview/Recovery"), "Failed takeoff does not leave flight recovery metadata: partial=" + partial);
                FailBeforeEntry = FailAfterEntry = false; Toggle();
                Check(flight.Stage == FlightStage.TakingOff, "Surface takeoff can retry successfully after failure: partial=" + partial);
            }

            SurfaceSwim(); Toggle();
            Check(safety.LastSafe != null && safety.LastSafe.X == who.Position.X && safety.LastSafe.Y == who.Position.Y,
                "A surface-origin flight records its current water position without requiring a shore");
            AccessTools.Method(typeof(WingsEntry), "Recover").Invoke(Mod, new object[] { "saving" });
            Check(!flight.Active && who.swimming.Value && !Swim.SwimHelperEvents.isJumping.Value && who.freezePause == 0 && who.Position == water,
                "Forced recovery can return a water-origin flight directly to stable swimming");
            SurfaceSwim(); Toggle();
            AccessTools.Method(typeof(WingsEntry), "OnSaveLoaded").Invoke(Mod, new object?[] { null, null });
            Check(!flight.Active && who.swimming.Value && !who.modData.ContainsKey("AngelWingsProject.VisualPreview/Recovery"),
                "Loading a water-origin recovery record can restore native swimming without a dry anchor");
            SurfaceSwim(); Toggle(); AccessTools.Method(typeof(WingsEntry), "BeginEventHandoff").Invoke(Mod, null); TickWings();
            Check(!flight.Active && who.swimming.Value && who.Position == water, "Event completion can hand a water-origin flight back to swimming");

            SurfaceSwim(); Toggle(); for (int i = 0; i < 7; i++) TickWings();
            who.Position = new Vector2(7 * 64, 5 * 64); who.movementDirections.Clear(); who.movementDirections.Add(3);
            AccessTools.Method(typeof(WingsEntry), "MovePrefix").Invoke(null, new object[] { who });
            var waterSurface = (FlightMap)AccessTools.Field(typeof(WingsEntry), "Surface").GetValue(Mod)!;
            Check(waterSurface.IsReachableGround(6, 5) && flight.CanTravel, "A water-origin flight can acquire the shore it actually approaches");
            who.Position = shore; TickWings();
            Check(safety.LastSafe is { } dryAnchor && dryAnchor.X == shore.X && dryAnchor.Y == shore.Y,
                "A real dry position restores the ordinary land recovery anchor");
            who.Position = new Vector2(10 * 64, 5 * 64); who.movementDirections.Clear(); who.movementDirections.Add(1);
            AccessTools.Method(typeof(WingsEntry), "MovePrefix").Invoke(null, new object[] { who });
            waterSurface = (FlightMap)AccessTools.Field(typeof(WingsEntry), "Surface").GetValue(Mod)!;
            Check(!waterSurface.IsReachableGround(11, 5), "After acquiring a dry anchor, ordinary flight does not open a disconnected land component");

            foreach (bool partial in new[] { false, true })
            {
                SurfaceSwim(); Toggle(); for (int i = 0; i < 7; i++) TickWings();
                double oldPhase = flight.Phase; float oldHeight = flight.Height;
                string recovery = who.modData["AngelWingsProject.VisualPreview/Recovery"];
                FailBeforeEntry = !partial; FailAfterEntry = partial;
                bool recovered = (bool)AccessTools.Method(compatibilityType, "TryRecoverToSwimming").Invoke(compatibility, null)!;
                Check(!recovered && flight.CanTravel && flight.Phase == oldPhase && flight.Height == oldHeight,
                    "A failed swimming recovery preserves usable flight: partial=" + partial);
                Check(!who.swimming.Value && !Swim.SwimHelperEvents.isJumping.Value && who.freezePause == 0
                    && who.modData["AngelWingsProject.VisualPreview/Recovery"] == recovery,
                    "A failed swimming recovery restores native flags and retains the recovery record: partial=" + partial);
                FailBeforeEntry = FailAfterEntry = false;
            }

            SurfaceSwim(); Config.EnableMod = false; Toggle();
            Check(!flight.Active && who.swimming.Value, "Disabling Swim does not accidentally enable takeoff from its inactive swimming state");

            SurfaceSwim(); var waterOnly = CreateLocation(); waterOnly.name.Value = "WaterOnlyFixture";
            var allWaterBack = waterOnly.Map.GetLayer("Back");
            for (int y = 0; y < 20; y++) for (int x = 0; x < 20; x++)
            { allWaterBack.Tiles[x, y].Properties["Water"] = "T"; allWaterBack.Tiles[x, y].Properties["Passable"] = "T"; waterOnly.waterTiles[x, y] = true; }
            Game1.game1.instanceGameLocation = waterOnly; who.currentLocation = waterOnly; Toggle();
            Check(flight.Stage == FlightStage.TakingOff && !who.swimming.Value, "Surface takeoff works on an all-water map with no shore");
            AccessTools.Method(typeof(WingsEntry), "Recover").Invoke(Mod, new object[] { "console" });
            Check(!flight.Active && who.swimming.Value && who.Position == water, "An all-water flight can recover directly to swimming without searching for land");

            Fly(); Toggle(); FinishEntry(); ExitTo(new Vector2(12 * 64, 5 * 64)); Toggle();
            Check(flight.Stage == FlightStage.TakingOff, "A shore on a different island can seed a fresh flight land component");
            Fly(); Config.NoAutoSwimSuit = true; Toggle();
            Check(!flight.Active && Swim.SwimHelperEvents.isJumping.Value && !who.bathingClothes.Value, "Swim's no-auto-swimsuit preference is preserved");

            Fly(); flight.Reset(); flight.Toggle(true, false); flight.Advance(100, true, false, false); Toggle();
            Check(Waiting() && flight.Stage == FlightStage.TakingOff && !Swim.SwimHelperEvents.isJumping.Value, "Water entry during takeoff waits for the existing takeoff to finish");
            for (int i = 0; i < 8; i++) TickWings();
            Check(!flight.Active && Swim.SwimHelperEvents.isJumping.Value, "Queued takeoff-to-water entry finishes without changing the flight state machine");

            Fly(); who.UsingTool = true; Free = CanMove = false; Toggle();
            Check(Waiting() && flight.Active && !Swim.SwimHelperEvents.isJumping.Value && who.UsingTool, "Water entry waits without cancelling the current native tool action");
            Check(!(bool)AccessTools.Method(typeof(WingsEntry), "MovePrefix").Invoke(null, new object[] { who })!, "A pending entry holds horizontal flight movement");
            who.UsingTool = false; Free = CanMove = true; TickWings();
            Check(!flight.Active && Swim.SwimHelperEvents.isJumping.Value, "Deferred entry commits after the tool releases control");
            Fly(); who.UsingTool = true; Toggle(); double phase = flight.Phase; float height = flight.Height;
            Toggle();
            Check(!Waiting() && flight.Active && flight.Phase == phase && flight.Height == height, "Second flight toggle cancels a deferred entry without changing flight animation state");
            who.UsingTool = false; TickWings();
            Check(flight.Active && !Swim.SwimHelperEvents.isJumping.Value, "Cancelled deferred entry stays cancelled after tool completion");

            Fly(); who.UsingTool = true; Toggle(); who.Position = shore; who.UsingTool = false; TickWings();
            Check(!Waiting() && flight.Active && !Swim.SwimHelperEvents.isJumping.Value, "Deferred entry revalidates a position changed by another system");
            Fly(); who.UsingTool = true; Toggle();
            AccessTools.Method(typeof(WingsEntry), "BeginEventHandoff").Invoke(Mod, null);
            Check(!Waiting() && !flight.Active, "Native event handoff cancels an uncommitted water request");
            Fly(); who.UsingTool = true; Toggle();
            AccessTools.Method(typeof(WingsEntry), "OnReturnedToTitle").Invoke(Mod, new object?[] { null, null });
            Check(!Waiting() && !flight.Active, "Returning to title clears an uncommitted water request");
            Fly(); who.UsingTool = true; Toggle();
            AccessTools.Method(typeof(WingsEntry), "OnSaveLoaded").Invoke(Mod, new object?[] { null, null });
            Check(!Waiting() && !flight.Active, "Loading a save cannot replay an old water request");
            Fly(); who.UsingTool = true; Toggle();
            AccessTools.Method(typeof(WingsEntry), "Recover").Invoke(Mod, new object[] { "saving" });
            Check(!Waiting() && !flight.Active && who.Position == shore, "Saving cancels a pending entry and retains existing safe-ground recovery");
            Fly(); who.UsingTool = true; Toggle();
            var destination = CreateLocation(); destination.name.Value = "SwimCompatWarpDestination";
            Game1.game1.instanceGameLocation = destination; who.currentLocation = destination; who.Position = shore;
            var warp = Activator.CreateInstance(typeof(WarpedEventArgs), BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { who, location, destination }, null);
            AccessTools.Method(typeof(WingsEntry), "OnWarped").Invoke(Mod, new[] { null, warp });
            Check(!Waiting() && flight.Active && who.Position == shore, "Normal warp cancels the water request while retaining the original flight arrival behavior");

            Fly(); location.Map.Properties["ProhibitSwimming"] = "T"; Toggle();
            Check(flight.Active && !Waiting() && !Swim.SwimHelperEvents.isJumping.Value, "Prohibited swimming maps retain safe flight instead of dropping into water");
            location.Map.Properties.Remove("ProhibitSwimming");
            Fly(); SwimEntry.locationIsPool.Value = true; Toggle();
            Check(flight.Active && !Swim.SwimHelperEvents.isJumping.Value, "Swim's pool restriction is respected");
            Fly(); var nightMarket = new StardewValley.Locations.BeachNightMarket();
            nightMarket.name.Value = "BeachNightMarket"; nightMarket.Map = location.Map; nightMarket.waterTiles = location.waterTiles;
            Game1.game1.instanceGameLocation = nightMarket; who.currentLocation = nightMarket; Toggle();
            Check(flight.Active && !Swim.SwimHelperEvents.isJumping.Value, "Explicit water entry preserves Swim's night-market prohibition");
            Fly(); var building = location.Map.GetLayer("Buildings"); var bridge = new StaticTile(building, location.Map.TileSheets[0], BlendMode.Alpha, 1);
            bridge.Properties["Passable"] = "T"; building.Tiles[8, 5] = bridge; Toggle();
            Check(flight.Active && !Swim.SwimHelperEvents.isJumping.Value, "A passable bridge over water is not mistaken for an entry point");
            building.Tiles[8, 5] = null;
            Fly(); location.objects[who.Tile] = new BlockingObject { TileLocation = who.Tile }; Toggle();
            Check(flight.Active && !Swim.SwimHelperEvents.isJumping.Value, "Occupied water cannot receive an entry");
            location.objects.Clear();
            Fly(); Config.EnableMod = false; Toggle();
            Check(flight.Active && !Waiting() && !Swim.SwimHelperEvents.isJumping.Value, "Disabled Swim leaves original water-landing restrictions intact");
            Fly(); FailInspection = true; Toggle(); FailInspection = false;
            Check(flight.Active && !Waiting() && !Swim.SwimHelperEvents.isJumping.Value, "A failing Swim position query cannot escape the optional module or disable flight");

            foreach (bool partial in new[] { false, true })
            {
                Fly(); who.Position += new Vector2(3, 2); who.movementDirections.Clear(); who.movementDirections.Add(3);
                phase = flight.Phase; height = flight.Height; Vector2 position = who.Position; int speed = who.speed;
                string marker = who.modData["AngelWingsProject.VisualPreview/Recovery"];
                FailBeforeEntry = !partial; FailAfterEntry = partial; Toggle();
                Check(flight.Active && flight.Phase == phase && flight.Height == height && flight.CanTravel, "Failed entry preserves the exact flight state: partial=" + partial);
                Check(!who.swimming.Value && !Swim.SwimHelperEvents.isJumping.Value && who.freezePause == 0, "Failed entry rolls back native swim flags and movement freeze: partial=" + partial);
                Check(who.Position == position && !who.bathingClothes.Value && !who.canOnlyWalk && who.speed == speed
                    && who.running && who.movementDirections.SequenceEqual(new[] { 3 }), "Failed entry restores position, clothing and movement settings: partial=" + partial);
                Check(who.modData["AngelWingsProject.VisualPreview/Recovery"] == marker && !Waiting(), "Failed entry retains recovery data without replaying the request: partial=" + partial);
                FailBeforeEntry = FailAfterEntry = false; TickWings(); TickSwim();
                Check(flight.Active && !Swim.SwimHelperEvents.isJumping.Value, "Flight remains usable on the next tick after failed entry: partial=" + partial);
            }

            Fly(); who.swimming.Value = true; TickWings();
            Check(!flight.Active && who.swimming.Value && !who.modData.ContainsKey("AngelWingsProject.VisualPreview/Recovery"), "Unexpected native swimming releases flight without fighting Swim's shore collision");
            Fly(); who.Position = shore; Toggle();
            Check(flight.Active && flight.LandingQueued && !Waiting(), "Ordinary dry-ground landing still uses the original flight controller");
            for (int i = 0; i < 30; i++) TickWings();
            Check(!flight.Active && !Swim.SwimHelperEvents.isJumping.Value, "Ordinary dry-ground landing finishes without starting Swim");
            Config.ReadyToSwim = false;
            Check(!Swim.SwimUtils.IsSafeToTryJump(), "Outside flight, Swim's disabled automatic-entry rule is unchanged");
            Config.ReadyToSwim = true;
            Check(Swim.SwimUtils.IsSafeToTryJump(), "Outside flight, Swim's enabled automatic-entry rule is unchanged");
            Prevent = true;
            Check(!Swim.SwimUtils.IsSafeToTryJump(), "Outside flight, Swim's prevent-jump key remains effective");
            Prevent = false;
            Config.ReadyToSwim = false;
            entry.Invoke(null, new object[] { water / 64, -1 }); TickWings(); FinishEntry();
            ExitTo(new Vector2(12 * 64, 5 * 64)); Toggle();
            Check(flight.Stage == FlightStage.TakingOff, "Normal shore-to-swim entry also clears a previous flight's land component before a new-island takeoff");
            using (var content = new ContentManager(new GameServiceContainer(), Path.Combine(Environment.GetCommandLineArgs()[1], "Content")))
            foreach (string mapName in new[] { "Beach", "Forest", "Farm_Fishing" })
            {
                Fly();
                var actualMap = new GameLocation(); actualMap.name.Value = "SwimCompat_" + mapName; actualMap.IsOutdoors = true;
                actualMap.Map = content.Load<Map>("Maps/" + mapName);
                var back = actualMap.Map.GetLayer("Back");
                actualMap.waterTiles = new WaterTiles(back.LayerWidth, back.LayerHeight);
                for (int y = 0; y < back.LayerHeight; y++) for (int x = 0; x < back.LayerWidth; x++)
                    actualMap.waterTiles[x, y] = actualMap.doesTileHaveProperty(x, y, "Water", "Back") != null;
                Game1.game1.instanceGameLocation = actualMap; who.currentLocation = actualMap;
                var surface = new FlightMap(actualMap, new Point(1, 1));
                var candidates = new List<Point>();
                for (int y = 1; y < back.LayerHeight - 1; y++) for (int x = 1; x < back.LayerWidth - 1; x++)
                    if (actualMap.waterTiles[x, y] && actualMap.Map.GetLayer("Buildings").Tiles[x, y] == null)
                        candidates.Add(new Point(x, y));
                Point tile = candidates.First(p =>
                {
                    who.Position = p.ToVector2() * 64;
                    return Swim.SwimUtils.IsInWater() && Swim.SwimUtils.CanSwimHere()
                        && Swim.SwimUtils.IsValidJumpLocation(who.Tile);
                });
                who.Position = tile.ToVector2() * 64;
                Set(Mod, "Surface", surface);
                Toggle();
                Check(!flight.Active && Swim.SwimHelperEvents.isJumping.Value, "Native map resource allows production water entry: " + mapName);
                FinishEntry(); TickWings();
                Check(who.swimming.Value && !flight.Active && who.Tile == tile.ToVector2(), "Native map resource finishes entry without flight recovery: " + mapName);
                Toggle();
                Check(flight.Stage == FlightStage.TakingOff && !who.swimming.Value && who.Tile == tile.ToVector2(),
                    "Native map resource allows direct surface takeoff: " + mapName);
                AccessTools.Method(typeof(WingsEntry), "Recover").Invoke(Mod, new object[] { "console" });
                Check(!flight.Active && who.swimming.Value && !Swim.SwimHelperEvents.isJumping.Value,
                    "Native map resource allows stable swimming recovery: " + mapName);
            }
            var unsupported = NewCompatibility(); Set(Mod, "Swim", unsupported);
            Check(!Bind(unsupported, "1.10.0") && !(bool)AccessTools.Property(compatibilityType, "Enabled").GetValue(unsupported)!, "Unknown Swim versions are not bound speculatively");
            Check(!(bool)AccessTools.Method(compatibilityType, "Bind").Invoke(unsupported, new object[] { typeof(WingsEntry).Assembly, "1.9.0" })!
                && !(bool)AccessTools.Property(compatibilityType, "Enabled").GetValue(unsupported)!, "Missing Swim method signatures leave the optional module inert");
            Set(Mod, "Swim", null); Fly(); Toggle();
            Check(flight.Active && !Swim.SwimHelperEvents.isJumping.Value, "With no compatibility module, original water landing is still rejected");
            Check(ProbeMonitor.Logs.All(s => !s.Contains("Error")), "Production compatibility produced no unexpected error logs");
            Console.WriteLine($"PASS: {Results.Count} actual production/Swim integration checks; no game session or save opened.");
        }
        finally
        {
            harmony.UnpatchAll(harmony.Id);
            harmony.UnpatchAll("AngelWingsProject.VisualPreview.SwimCompatibility");
            AccessTools.Field(typeof(WingsEntry), "Instance").SetValue(null, null);
        }
    }

    static GameLocation CreateLocation()
    {
        var location = new GameLocation(); location.name.Value = "SwimCompatFixture"; location.IsOutdoors = true;
        var map = new Map();
        var sheet = new TileSheet("outdoors", map, "Maps/spring_outdoorsTileSheet", new xTile.Dimensions.Size(25, 79), new xTile.Dimensions.Size(16, 16)); map.AddTileSheet(sheet);
        var back = new Layer("Back", map, new xTile.Dimensions.Size(20, 20), new xTile.Dimensions.Size(16, 16));
        map.AddLayer(back); map.AddLayer(new Layer("Buildings", map, back.LayerSize, back.TileSize));
        location.waterTiles = new WaterTiles(20, 20);
        for (int y = 0; y < 20; y++) for (int x = 0; x < 20; x++)
        {
            back.Tiles[x, y] = new StaticTile(back, sheet, BlendMode.Alpha, 0);
            // Split the dry land into genuinely disconnected shores.
            if (x is >= 7 and <= 10)
            { back.Tiles[x, y].Properties["Water"] = "T"; back.Tiles[x, y].Properties["Passable"] = "T"; location.waterTiles[x, y] = true; }
        }
        location.Map = map;
        return location;
    }
}

// A real Object occupant with a fixed passability policy; item content is not loaded headlessly.
sealed class BlockingObject : StardewValley.Object
{
    public override bool isPassable() => false;
}

public class ProbeMonitor : DispatchProxy
{
    public static readonly List<string> Logs = new();
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method?.Name.StartsWith("Log") == true) Logs.Add(string.Join(" ", args ?? Array.Empty<object>()));
        return method?.ReturnType == typeof(bool) ? false : null;
    }
}
