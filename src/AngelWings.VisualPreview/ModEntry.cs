using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Menus;
using StardewValley.Objects;
using StardewValley.Tools;
using AngelWings.VisualPreview.Flight;
using AngelWings.VisualPreview.Acquisition;
using AngelWings.VisualPreview.Quests;
using AngelWings.VisualPreview.Wings;
using AngelWings.VisualPreview.Compatibility;
using System.Text.Json;

namespace AngelWings.VisualPreview;

public sealed class ModEntry : Mod
{
    internal static ModEntry? Instance;
    private const string RecoveryKey = "AngelWingsProject.VisualPreview/Recovery";
    internal SwimCompatibility? Swim;
    internal readonly FlightController Flight = new();
    private readonly FlightSafety Safety = new();
    internal FlightMap? Surface;
    private readonly Dictionary<string, Texture2D> Textures = new();
    private ModConfig Config = null!;
    internal ModConfig Settings => Config;
    private readonly ControllerGesture ControllerInput = new();
    private readonly FlightWake WaterWake = new();
    internal readonly FlightAltitude Altitude = new();
    private readonly FlightLandingHint LandingHint = new();
    internal readonly FlightDoorPassage DoorPassage = new();
    internal WingItemService WingItems { get; private set; } = null!;
    internal HaleyFlowerQuestService HaleyFlowerQuest { get; private set; } = null!;
    private bool ControllerCaptured;
    private bool WorldSorted;
    private bool Ready;
    private bool Recovering;
    private double NoticeCooldown;
    private double RecoveryRetryMs;
    private SafePosition? PendingRecovery;
    private bool WasUsingTool;
    private bool EventRecoveryPending;

    public override void Entry(IModHelper helper)
    {
        Instance = this;
        ReadConfig();
        new WingShop(this).Register(helper);
        WingItems = new WingItemService(this);
        WingItems.Register(helper);
        HaleyFlowerQuest = new HaleyFlowerQuestService(this);
        HaleyFlowerQuest.Register(helper);
        if (!File.Exists(Path.Combine(helper.DirectoryPath, "config.json"))) helper.WriteConfig(Config);
        helper.Events.GameLoop.GameLaunched += OnLaunched;
        helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
        helper.Events.GameLoop.DayStarted += OnDayStarted;
        helper.Events.GameLoop.Saving += (_, _) => { if (Flight.Active) Recover("saving"); };
        helper.Events.GameLoop.UpdateTicking += OnUpdate;
        helper.Events.GameLoop.ReturnedToTitle += OnReturnedToTitle;
        helper.Events.Player.Warped += OnWarped;
        helper.Events.Input.ButtonPressed += OnButton;
        helper.Events.Display.RenderingStep += (_, e) =>
        {
            if (e.Step != StardewValley.Mods.RenderSteps.World_Sorted) return;
            WorldSorted = true;
            if (Ready && LocationAllowed && Surface?.Matches(Game1.currentLocation)==true)
            {
                if(Flight.Active)WaterWake.Draw(e.SpriteBatch,Surface);
                if(Config.ShowLandingHint)LandingHint.Draw(e.SpriteBatch);
            }
        };
        helper.Events.Display.RenderedStep += (_, e) => { if (e.Step == StardewValley.Mods.RenderSteps.World_Sorted) WorldSorted = false; };
        helper.ConsoleCommands.Add("angelwings_reload", "Reload wing settings while grounded.", (_, _) =>
        {
            if (Flight.Active) { Notice("land-before-settings"); return; }
            ReadConfig();
            Monitor.Log("Angel Wings settings reloaded.", LogLevel.Info);
        });
        helper.ConsoleCommands.Add("angelwings_recover", "Return to a rechecked safe ground position.", (_, _) =>
        {
            if (FlightSessionAllowed) Recover("console");
        });
        helper.ConsoleCommands.Add("angelwings_status", "Print flight and map diagnostics.", (_, _) =>
        {
            Monitor.Log($"Ready={Ready}; unlocked={Context.IsWorldReady && WingProgress.IsUnlocked(Game1.player)}; stage={Flight.Stage}; fishing={IsFishing}; location={Game1.player?.currentLocation?.NameOrUniqueName}; safe={Safety.LastSafe}", LogLevel.Info);
        });
    }

    private void ReadConfig()
    {
        Config = Helper.ReadConfig<ModConfig>();
        if (Config.ControllerToggleKey != SButton.None && !Config.ControllerToggleKey.TryGetController(out _))
        {
            Monitor.Log("ControllerToggleKey must be a controller button; using LeftStick.", LogLevel.Warn);
            Config.ControllerToggleKey = SButton.LeftStick;
        }
        ControllerInput.Cancel();
        ControllerCaptured = false;
        Config.WingScale = ClampFinite(Config.WingScale, .5f, 1.5f, 1f);
        Config.OffsetX = ClampFinite(Config.OffsetX, -64f, 64f, 0f);
        Config.OffsetY = ClampFinite(Config.OffsetY, -64f, 64f, 0f);
        Config.HoverHeight = ClampFinite(Config.HoverHeight, 8f, 40f, 36f);
        Config.FlightSpeedMultiplier = ClampFinite(Config.FlightSpeedMultiplier, 1f, 2f, 1.65f);
        Config.ObstacleLift = ClampFinite(Config.ObstacleLift,24f,128f,88f);
    }

    private void OnLaunched(object? sender, GameLaunchedEventArgs e)
    {
        var harmony = new Harmony(ModManifest.UniqueID);
        try
        {
            foreach (string wing in new[] { WingProgress.AngelWingId, WingProgress.ButterflyWingId })
            foreach (string state in new[] { "H", "M", "Q", "T", "L" })
            foreach (string view in new[] { "back", "right", "front", "left" })
            foreach (string layer in new[] { "behind", "in_front" })
            {
                string key = $"{state}/{view}/{layer}";
                string path = wing == WingProgress.AngelWingId ? $"assets/{key}.png" : $"assets/butterfly/{key}.png";
                Texture2D texture = Helper.ModContent.Load<Texture2D>(path);
                int count = state is "T" or "L" ? 10 : 16;
                if (texture.Width != count * 80 || texture.Height != 80) throw new InvalidDataException($"Wrong atlas dimensions: {wing}/{key}");
                Textures.Add($"{wing}/{key}", texture);
            }
            WaterWake.ConfigureTextures(
                Helper.ModContent.Load<Texture2D>("assets/effects/ocean-wake.png"),
                Helper.ModContent.Load<Texture2D>("assets/effects/river-wake.png"),
                Helper.ModContent.Load<Texture2D>("assets/effects/lake-ripple.png"));
            InstallPatches(harmony);
            Ready = true;
            Monitor.Log($"Angel Wings flight alpha ready. Toggle: {Config.ToggleKey}; recovery: {Config.EmergencyLandKey}; controller: tap {Config.ControllerToggleKey} to toggle, hold 1.2s to recover. See Options > Controls.", LogLevel.Info);
        }
        catch (Exception ex)
        {
            harmony.UnpatchAll(ModManifest.UniqueID);
            Ready = false;
            Monitor.Log($"Flight could not initialize; no flight changes enabled.\n{ex}", LogLevel.Error);
        }
        if (Ready)
        {
            Swim = new SwimCompatibility(this);
            Swim.Initialize(Helper.ModRegistry);
        }
    }

    public static void InstallPatches(Harmony harmony)
    {
        Type[] rendererParameters =
        [
            typeof(SpriteBatch), typeof(FarmerSprite.AnimationFrame), typeof(int), typeof(Rectangle),
            typeof(Vector2), typeof(Vector2), typeof(float), typeof(int), typeof(Color),
            typeof(float), typeof(float), typeof(Farmer)
        ];
        harmony.Patch(AccessTools.Method(typeof(FarmerRenderer), "draw", rendererParameters),
            prefix: new HarmonyMethod(typeof(ModEntry), nameof(DrawPrefix)),
            transpiler: new HarmonyMethod(typeof(FlightPoseDrawing), nameof(FlightPoseDrawing.RedirectSpriteDraws)),
            finalizer: new HarmonyMethod(typeof(ModEntry), nameof(DrawFinalizer)));
        FlightPoseDrawing.Install(harmony);
        FlightCollision.Install(harmony);
        FlightDrawing.Install(harmony);
        FlightControlsOptions.Install(harmony);
        WingItemService.InstallPatches(harmony);
        HaleyFlowerQuestService.InstallPatches(harmony);
        harmony.Patch(AccessTools.Method(typeof(Farmer), nameof(Farmer.getMovementSpeed)),
            postfix: new HarmonyMethod(typeof(ModEntry), nameof(SpeedPostfix)));
        harmony.Patch(AccessTools.Method(typeof(Farmer), nameof(Farmer.MovePosition)),
            prefix: new HarmonyMethod(typeof(ModEntry), nameof(MovePrefix)));
        harmony.Patch(AccessTools.Method(typeof(GameLocation), nameof(GameLocation.startEvent), new[] { typeof(StardewValley.Event) }),
            prefix: new HarmonyMethod(typeof(ModEntry), nameof(EventPrefix)));
        harmony.Patch(AccessTools.Method(typeof(Game1), nameof(Game1.ShowLocalCoopJoinMenu)),
            prefix: new HarmonyMethod(typeof(ModEntry), nameof(LocalCoopJoinPrefix)));
        harmony.Patch(AccessTools.Method(typeof(Farmer), "performPassOut"),
            prefix: new HarmonyMethod(typeof(ModEntry), nameof(PassOutPrefix)));
        harmony.Patch(AccessTools.Method(typeof(Game1), "performWarpFarmer"),
            postfix: new HarmonyMethod(typeof(ModEntry), nameof(WarpCommittedPostfix)));
    }

    private void OnSaveLoaded(object? sender, SaveLoadedEventArgs e)
    {
        if (Context.ScreenId != 0) return;
        ResetSession();
        if (!Ready || !FlightSessionAllowed) return;
        if (EndFlightAtBed("save-loaded")) return;
        Farmer who = Game1.player;
        if (who.modData.TryGetValue(RecoveryKey, out string raw))
        {
            try { Safety.LastSafe = JsonSerializer.Deserialize<SafePosition>(raw); }
            catch (JsonException ex) { Monitor.Log($"Invalid wing recovery record: {ex.Message}", LogLevel.Warn); }
        }
        if (who.modData.ContainsKey(RecoveryKey)) Recover("save-loaded");
    }

    private void OnDayStarted(object? sender, DayStartedEventArgs e)
    {
        if (!Ready || !FlightSessionAllowed) return;
        if (EndFlightAtBed("day-started")) return;
        if (Flight.Active || Game1.player.modData.ContainsKey(RecoveryKey)) Recover("day-started");
    }

    private static bool AtBed => BedFurniture.IsBedHere(Game1.player.currentLocation,
        Game1.player.TilePoint.X, Game1.player.TilePoint.Y);

    private bool EndFlightAtBed(string reason)
    {
        if (!AtBed) return false;
        // Beds are valid native wake-up positions, but furniture occupancy makes
        // them unsuitable for ordinary flight landings. Never resurrect flight
        // or teleport to an old room coordinate while the game owns this spawn.
        bool hadFlight = Flight.Active || Game1.player.modData.ContainsKey(RecoveryKey);
        ResetSession();
        Game1.player.modData.Remove(RecoveryKey);
        if (hadFlight) Monitor.Log($"Flight cleared at native bed position ({reason}).", LogLevel.Debug);
        return true;
    }

    private void RebuildSurface(bool useLastSafe = true)
    {
        Farmer who = Game1.player;
        Point seed = who.GetBoundingBox().Center;
        if (useLastSafe && Safety.LastSafe is { } safe && safe.Location == who.currentLocation.NameOrUniqueName && float.IsFinite(safe.X) && float.IsFinite(safe.Y))
            seed = FlightSafety.BoxAt(who, new Vector2(safe.X, safe.Y)).Center;
        Surface = new FlightMap(who.currentLocation, new Point(seed.X / 64, seed.Y / 64));
        // A saved point may be inside a wall after a house/map change. Only a
        // physically valid current position may replace that invalid seed; never
        // search for an arbitrary component on the other side of a locked gate.
        Point current = who.GetBoundingBox().Center;
        if (!Surface.IsReachableGround(seed.X / 64, seed.Y / 64) && current != seed
            && Safety.CanStand(who, who.currentLocation, who.Position, Surface, requireAccess: false))
            Surface = new FlightMap(who.currentLocation, new Point(current.X / 64, current.Y / 64));
    }

    private void OnWarped(object? sender, WarpedEventArgs e)
    {
        if (!e.IsLocalPlayer || !Ready || !FlightSessionAllowed) return;
        Swim?.Cancel();
        if (Swim?.YieldToNativeSwimming() == true) return;
        bool wasFlying = Flight.Active;
        DoorPassage.Arrive();
        if(wasFlying)Monitor.Log($"Flight warp: {e.OldLocation.NameOrUniqueName} -> {e.NewLocation.NameOrUniqueName}; position={e.Player.Position}.",LogLevel.Debug);
        WaterWake.Reset();
        Altitude.OnWarp();
        LandingHint.Reset();
        ControllerInput.Cancel();
        ControllerCaptured = false;
        WasUsingTool = false;
        // A normal game warp legitimately selects the destination's access component.
        // An older safe point in the same named room must not override this arrival.
        try { RebuildSurface(useLastSafe: false); }
        catch (Exception ex)
        {
            Surface = null;
            Monitor.Log($"Cannot inspect the new map: {ex.Message}", LogLevel.Warn);
        }
        bool recoveryWarp = PendingRecovery is { };
        if (PendingRecovery is { } pending && pending.Location == e.NewLocation.NameOrUniqueName)
        {
            Vector2 target = new(pending.X, pending.Y);
            if (Surface != null && Safety.CanStand(e.Player, e.NewLocation, target, Surface)) e.Player.Position = target;
            PendingRecovery = null;
        }
        if (recoveryWarp || (!wasFlying && e.Player.modData.ContainsKey(RecoveryKey))) Recover("recovery-warp");
        else if (wasFlying && (Surface == null || !LocationAllowed)) Recover("unsafe-warp");
        // Ordinary warps keep stage, phase, height, velocity and the native arrival
        // position untouched. The recovery marker is NOT an instruction to land.
        if (CanLand) Safety.Remember(e.Player);
        if (Flight.Active) SaveRecoveryMarker();
    }

    private void OnButton(object? sender, ButtonPressedEventArgs e)
    {
        if (!Context.IsWorldReady || !Ready || Context.ScreenId != 0 || !Context.IsMainPlayer) return;
        if (Config.ControllerToggleKey != SButton.None && e.Button == Config.ControllerToggleKey)
        {
            if (InputAllowed && !Helper.Input.IsSuppressed(e.Button))
            {
                ControllerCaptured = true;
                Helper.Input.Suppress(e.Button);
            }
            return;
        }
        if (e.Button == Config.EmergencyLandKey)
        {
            if (InputAllowed && Flight.Active && Game1.activeClickableMenu == null)
            { ControllerInput.Cancel(); Helper.Input.Suppress(e.Button); Recover("key"); }
            return;
        }
        if (e.Button == Config.ToggleKey)
        {
            if (!InputAllowed) return;
            ControllerInput.Cancel();
            Helper.Input.Suppress(e.Button);
            ToggleFlight();
        }
    }

    private bool InputAllowed => Context.IsWorldReady && Game1.game1.IsActive && !Game1.IsChatting && !Game1.paused && !Game1.eventUp
        && !Game1.dialogueUp && !Game1.fadeToBlack && !Game1.freezeControls
        && Game1.player.controller == null && (Game1.activeClickableMenu == null || Game1.activeClickableMenu is BobberBar);

    private void ToggleFlight()
    {
        if (!FlightSessionAllowed) { Notice("single-player"); return; }
        if (!Flight.Active)
        {
            if (!WingProgress.IsUnlocked(Game1.player)) { Notice("wings-locked"); return; }
            if (Swim?.TryTakeoff() == true) return;
            if (!LocationAllowed || Game1.player.UsingTool || !Context.IsPlayerFree) { Notice("cannot-start"); return; }
            RebuildSurface();
            if (!CanLand) { Notice("cannot-start"); return; }
            Safety.Remember(Game1.player);
            SaveRecoveryMarker();
        }
        bool safe = CanLand;
        if (Swim?.TryToggle(safe) == true) return;
        bool requestedLanding=Flight.Active && !Flight.LandingQueued && Flight.DesiredFlight;
        var result = Flight.Toggle(safe, ActionBusy);
        if(requestedLanding && Config.ShowLandingHint)LandingHint.Show(Game1.player.GetBoundingBox().Center.ToVector2(),safe);
        else LandingHint.Reset();
        Monitor.Log($"Flight toggle: {result}; stage={Flight.Stage}; queued={Flight.LandingQueued}.",LogLevel.Debug);
        if (result == ToggleResult.CannotLand) Notice("cannot-land");
        else if (ActionBusy && result == ToggleResult.Queued) Notice("landing-after-action");
    }

    private void UpdateController(double dt)
    {
        // SMAPI suppression hides the held button on the next tick. Its suppression
        // flag persists until physical release, so retain that state only for our captured press.
        bool down = ControllerCaptured && (Helper.Input.IsDown(Config.ControllerToggleKey) || Helper.Input.IsSuppressed(Config.ControllerToggleKey));
        bool eligible = InputAllowed && Game1.input.GetGamePadState().IsConnected;
        WingInputAction action = ControllerInput.Advance(down, eligible, dt);
        if (!down) ControllerCaptured = false;
        if (action == WingInputAction.Toggle) ToggleFlight();
        else if (action == WingInputAction.Recover && Flight.Active && Game1.activeClickableMenu == null) Recover("controller-hold");
    }

    private static bool FlightSessionAllowed => Context.IsWorldReady && Context.ScreenId == 0
        && Context.IsMainPlayer && !Game1.IsMultiplayer;

    internal bool ManagesLocalSwim => Ready && FlightSessionAllowed;
    internal bool HasFlightStateForSwimming => Flight.Active || Surface != null || Safety.LastSafe != null
        || PendingRecovery != null || EventRecoveryPending || Game1.player.modData.ContainsKey(RecoveryKey);

    internal bool HasWaterFlightOrigin => Surface != null && Safety.LastSafe is { } origin
        && origin.Location == Game1.player.currentLocation.NameOrUniqueName
        && Surface.IsOpenWaterAt(FlightSafety.BoxAt(Game1.player, new Vector2(origin.X, origin.Y)).Center.ToVector2());

    internal bool CanPurchaseWings => Ready && FlightSessionAllowed;
    internal Texture2D GetShopWingTexture() => Textures[$"{WingProgress.AngelWingId}/H/front/behind"];

    internal bool SurfaceTakeoffAllowed => FlightSessionAllowed && Game1.player.currentLocation != null
        && !Game1.eventUp && Game1.player.currentLocation.currentEvent == null
        && !Game1.player.isRidingHorse() && !Game1.player.IsSitting()
        && !Game1.player.passedOut && Game1.player.health > 0 && !AtBed;

    internal bool LocationAllowed => SurfaceTakeoffAllowed && Swim?.Committing != true && !Game1.player.swimming.Value;

    internal bool IsFishing => Context.IsWorldReady && Game1.player.CurrentTool is FishingRod rod
        && (Game1.player.UsingTool || rod.isTimingCast || rod.isCasting || rod.isFishing || rod.isNibbling
            || rod.isReeling || rod.pullingOutOfWater || rod.castedButBobberStillInAir || rod.fishCaught || rod.showingTreasure);

    // Preserve all native action frames and busy/CanMove rules, not only fishing.
    internal bool ActionBusy => Context.IsWorldReady && (Game1.player.UsingTool || Game1.player.isEating || IsFishing);

    private bool CanLand => Context.IsWorldReady && Surface != null && Surface.Matches(Game1.player.currentLocation)
        && Safety.CanStand(Game1.player, Game1.player.currentLocation, Game1.player.Position, Surface);

    private void OnUpdate(object? sender, UpdateTickingEventArgs e)
    {
        if (!Ready || !FlightSessionAllowed) return;
        try
        {
            double dt = Math.Clamp(Game1.currentGameTime.ElapsedGameTime.TotalMilliseconds, 0, 100);
            NoticeCooldown = Math.Max(0, NoticeCooldown - dt);
            RecoveryRetryMs = Math.Max(0, RecoveryRetryMs - dt);
            Swim?.Update();
            if (Swim?.InWaterOrTransition == true)
            {
                // Swimming also needs the existing controller tap/hold recognizer.
                UpdateController(dt);
                return;
            }
            if (EventRecoveryPending)
            {
                WaterWake.Reset();
                Altitude.Reset();
                DoorPassage.Reset();
                if (Game1.eventUp || Game1.eventOver || Game1.currentLocation?.currentEvent != null
                    || Game1.locationRequest != null || Game1.isWarping) return;
                EventRecoveryPending = false;
                if (Surface == null || !Surface.Matches(Game1.player.currentLocation)) RebuildSurface(useLastSafe: false);
                if (CanLand)
                {
                    Safety.Remember(Game1.player);
                    Game1.player.modData.Remove(RecoveryKey);
                    Monitor.Log("Flight event handoff completed on safe ground without moving the player.", LogLevel.Debug);
                }
                else Recover("event-finished");
                return;
            }
            UpdateController(dt);
            if(!Game1.paused && Game1.activeClickableMenu==null)LandingHint.Advance(dt);
            if (!Flight.Active) { WaterWake.Reset();Altitude.Reset();DoorPassage.Reset();WasUsingTool=false;return; }
            if (!LocationAllowed)
            {
                WaterWake.Reset();
                if (RecoveryRetryMs <= 0) Recover($"interrupted: allowed={LocationAllowed}, tool={Game1.player.UsingTool}, fishing={IsFishing}, path={Game1.player.controller!=null}");
                return;
            }
            if (Surface == null || !Surface.Matches(Game1.player.currentLocation))
            { if (RecoveryRetryMs <= 0) Recover("map-changed"); return; }
            if (Surface.DoorsChanged) RebuildSurface();
            bool visualPaused = Game1.paused || Game1.activeClickableMenu != null;
            bool crossingDoor = Flight.CanTravel && !ActionBusy && !Game1.dialogueUp && !Game1.freezeControls
                && !Game1.fadeToBlack && !visualPaused && FlightDoorPassage.CrossingOpenDoor(Game1.player);
            DoorPassage.Advance(dt,crossingDoor,Game1.locationRequest != null || Game1.isWarping,visualPaused);
            bool busy = ActionBusy;
            // An axe/pickaxe may have opened a former obstacle in the ground graph.
            if (WasUsingTool && !busy) RebuildSurface();
            WasUsingTool = busy;
            if (Game1.paused || (Game1.activeClickableMenu != null && Game1.activeClickableMenu is not BobberBar)) return;
            bool safe = CanLand;
            if (safe)
            {
                Safety.Remember(Game1.player);
                if (e.IsOneSecond) SaveRecoveryMarker();
            }
            // isMoving() uses animation flags set by updateFrame, which runs later
            // than UpdateTicking. Predict from the same directions MovePosition uses.
            bool moving = !busy && !Game1.dialogueUp && Game1.player.CanMove
                && (Game1.player.movementDirections.Count>0 || Game1.player.isMoving()) && !Flight.LandingQueued;
            Vector2 velocity=Vector2.Zero;
            if(moving)
            {
                foreach(int direction in Game1.player.movementDirections)
                    velocity+=direction switch {0=>new Vector2(0,-1),1=>new Vector2(1,0),2=>new Vector2(0,1),3=>new Vector2(-1,0),_=>Vector2.Zero};
                if(velocity.LengthSquared()>0)velocity=Vector2.Normalize(velocity)*Game1.player.getMovementSpeed()*60;
            }
            bool clearance=Config.AutoClimb && Flight.Stage!=FlightStage.Landing
                && FlightAltitude.NeedsClearance(Game1.player.GetBoundingBox(),velocity,Surface!.IsLowObstacle);
            Altitude.Advance(dt,clearance,Config.ObstacleLift,Flight.Active);
            if (Flight.Advance(dt, safe, busy, moving)) Notice("cannot-land");
            WaterWake.Advance(dt,Game1.player.currentLocation,Game1.player.GetBoundingBox().Center.ToVector2(),
                Flight.CanTravel && !busy,Surface!.IsOpenWaterAt,Surface.WaterEffectAt,GetFlightLift(Game1.player));
            if (!Flight.Active)
            {
                WaterWake.Reset();
                Altitude.Reset();
                DoorPassage.Reset();
                Game1.player.modData.Remove(RecoveryKey);
            }
        }
        catch (Exception ex)
        {
            Monitor.Log($"Flight update failed; attempting safe recovery.\n{ex}", LogLevel.Error);
            Recover("error");
        }
    }

    internal bool CanUseFlightCollision(Character? character) => Ready && !Recovering && Flight.Stage == FlightStage.Flying
        && character == Game1.player && LocationAllowed;

    internal float GetVisualLift(Character? character)
    {
        return WorldSorted ? GetFlightLift(character) : 0;
    }

    // Shadows are drawn before World_Sorted; height must still be available there.
    internal float GetFlightLift(Character? character)
    {
        if (!Ready || !FlightSessionAllowed
            || character != Game1.player || !Flight.Active || !LocationAllowed || FarmerRenderer.isDrawingForUI)
            return 0;
        float lift = (Config.HoverHeight + Altitude.ExtraHeight) * Flight.Height;
        // Height also fades the wave at takeoff/landing; crossing a stage boundary
        // must not suddenly introduce/remove a three-pixel displacement.
        lift += FlightVisuals.HoverBob(Flight.VisualSeconds, Flight.MovementLean) * Flight.Height;
        return lift;
    }

    private static void SpeedPostfix(Farmer __instance, ref float __result)
    {
        var mod = Instance;
        if (mod != null && mod.CanUseFlightCollision(__instance) && mod.Flight.CanTravel && !mod.ActionBusy
            && Game1.activeClickableMenu == null && float.IsFinite(__result) && __result > 0)
            __result = mod.Flight.MovementSpeed(__result, mod.Config.FlightSpeedMultiplier, mod.IsFishing);
    }

    private static bool MovePrefix(Farmer __instance)
    {
        var mod = Instance;
        if(mod == null || !mod.Ready || !FlightSessionAllowed || __instance != Game1.player
            || !mod.Flight.Active || !mod.LocationAllowed || Game1.player.controller != null)return true;
        if(mod.Swim?.Waiting == true)return false;
        if(mod.IsFishing || !mod.Flight.CanTravel)return false;
        mod.Swim?.PrepareMovement(__instance);
        // At a close start there is no runway. Hold only entry into a low obstacle
        // until clear; do not block movement away or switch the player out of flight.
        if(mod.Config.AutoClimb && mod.Altitude.ExtraHeight<mod.Config.ObstacleLift*.94f
            && mod.Surface is { } surface && surface.Matches(__instance.currentLocation))
        {
            Rectangle body=__instance.GetBoundingBox();
            if(!surface.ContainsLowObstacle(body))
            {
                Rectangle swept=body;
                foreach(int direction in __instance.movementDirections)
                    if(direction is >=0 and <=3)swept=Rectangle.Union(swept,__instance.nextPosition(direction));
                if(surface.ContainsLowObstacle(swept))return false;
            }
        }
        return true;
    }

    private static void EventPrefix(GameLocation __instance)
    {
        var mod = Instance;
        if (mod?.Ready == true && mod.Flight.Active && __instance == Game1.currentLocation
            && !Game1.eventUp && !Game1.eventOver)
            mod.BeginEventHandoff();
    }

    private void BeginEventHandoff()
    {
        Swim?.Cancel();
        // startEvent captures the player's current position and then takes ownership
        // of CanMove. Moving to a recovery tile in its Harmony prefix can invalidate
        // scripted paths and leave the event's movement lock in place indefinitely.
        // Remove only flight-owned state here; validate the landing after the event.
        EventRecoveryPending = true;
        Flight.Reset();
        Altitude.Reset();
        LandingHint.Reset();
        WaterWake.Reset();
        DoorPassage.Reset();
        ControllerInput.Cancel();
        ControllerCaptured = false;
        WasUsingTool = false;
        Game1.player.modData.Remove(RecoveryKey);
        Monitor.Log($"Flight yielded to native event without moving the player; location={Game1.player.currentLocation.NameOrUniqueName}; position={Game1.player.Position}.", LogLevel.Debug);
    }

    private static bool LocalCoopJoinPrefix(ref bool __result)
    {
        // OptionsPage can expose the vanilla local-coop action even when the save
        // has no free farmhand cabin. If that action is reached while flying, the
        // game emits CoopMenu_NoSlots despite this mod being single-player-only.
        // Keep the vanilla action unchanged once grounded.
        if (Instance?.Ready == true && Instance.Flight.Active)
        {
            __result = false;
            return false;
        }
        return true;
    }
    private static void PassOutPrefix(Farmer __instance)
    {
        if (Instance?.Flight.Active == true && __instance == Game1.player) Instance.Recover("pass-out");
    }

    private static void WarpCommittedPostfix()
    {
        var mod = Instance;
        if (mod == null || !mod.Ready || !mod.Flight.Active || !mod.LocationAllowed
            || Game1.locationRequest is not { } request) return;
        // performWarpFarmer is reached only after the game's lock/time/event checks.
        // Do not fold for ordinary outdoor map-edge travel or emergency recovery.
        var destination = request.Location ?? Game1.getLocationFromName(request.Name);
        if (!mod.Recovering && destination != null
            && (!Game1.player.currentLocation.IsOutdoors || !destination.IsOutdoors))
            mod.DoorPassage.ConfirmWarp();
    }

    private static void DrawPrefix(SpriteBatch b, ref FarmerSprite.AnimationFrame animationFrame, ref int currentFrame,
        ref Rectangle sourceRect, Vector2 position, Vector2 origin, float layerDepth, int facingDirection,
        Color overrideColor, ref float rotation, float scale, Farmer who, out FlightPoseDrawing.PoseContext? __state)
    {
        __state = FlightPoseDrawing.Current;
        FlightPoseDrawing.Current = null;
        ModEntry? mod = Instance;
        if (mod == null || !mod.Ready || !mod.WorldSorted || !mod.Flight.Active || mod.Flight.Opacity <= 0 ||
            !FlightSessionAllowed || FarmerRenderer.isDrawingForUI || who != Game1.player ||
            !mod.LocationAllowed || overrideColor != Color.White) return;
        // Farmer.draw now supplies lifted coordinates to both body and accessories.
        int direction = facingDirection;
        if (direction is 1 or 3) direction = animationFrame.flip ? 3 : 1;
        bool preserveToolPose = mod.ActionBusy;
        if (!preserveToolPose)
        {
            // Farmer.updateFrame selects the normal walk/run animation even while flight
            // owns horizontal movement. Replace only the visual frame; tool state and
            // the rest of the Farmer update loop remain untouched.
            currentFrame = FlightVisuals.IdleFrame(direction);
            animationFrame = new FarmerSprite.AnimationFrame(currentFrame, 100, secondaryArm: false, flip: direction == 3);
            sourceRect = new Rectangle(currentFrame % 6 * 16, currentFrame / 6 * 32, 16, 32);
            rotation = 0f;
        }
        else if (rotation != 0f)
        {
            // Keep the game's own fishing/tool rotation intact and avoid rotating the
            // wing accessory around a different pivot.
            return;
        }
        string view = direction switch { 0 => "back", 1 => "right", 2 => "front", _ => "left" };
        float bodyScale = 4f * scale;
        Vector2 animationOffset = new(animationFrame.xOffset * 4, animationFrame.positionOffset * 4);
        float shoulderX = direction switch { 1 => 5f, 3 => 11f, _ => 8f };
        Vector2 bodyPivot = new Vector2(MathF.Floor(position.X), MathF.Floor(position.Y)) + origin + animationOffset;
        Vector2 topLeft = bodyPivot - origin * bodyScale;
        Vector2 anchor = topLeft + new Vector2(shoulderX, 14f) * bodyScale;
        float wingRotation = 0;
        if (!preserveToolPose)
        {
            var pose = new FlightPoseDrawing.PoseContext(topLeft, bodyScale, direction, mod.Flight.PoseAmount);
            FlightPoseDrawing.Current = pose;
            anchor = pose.Map(anchor);
            wingRotation = pose.Rotation;
        }
        anchor += new Vector2(mod.Config.OffsetX, mod.Config.OffsetY);
        Rectangle rect = new(mod.Flight.Frame * 80, 0, 80, 80);
        string wing = WingProgress.GetEquippedWingId(Game1.player);
        bool butterfly = wing == "butterfly";
        Vector2 wingOrigin = FlightVisuals.WingSourceOrigin(butterfly, direction);
        float before = FlightVisuals.RelativeLayer(FarmerRenderer.GetLayerDepth(layerDepth, FarmerRenderer.FarmerSpriteLayers.Base),
            -.0000005f, Game1.isUsingBackToFrontSorting);
        // The complete near-side membrane is opaque over the farmer, including
        // arms/hair in tool poses. Angel/back/front roots keep shirt ordering.
        float after = FlightVisuals.RelativeLayer(FarmerRenderer.GetLayerDepth(layerDepth,
            FlightVisuals.WingForegroundLayer(butterfly, direction, preserveToolPose)),
            .0000005f, Game1.isUsingBackToFrontSorting);
        mod.DoorPassage.Draw(b,mod.Textures[$"{wing}/{mod.Flight.Animation}/{view}/behind"],anchor,rect,Color.White * mod.Flight.Opacity,
            wingRotation,wingOrigin,bodyScale * mod.Config.WingScale,before,direction,mod.Textures[$"{wing}/T/{view}/behind"]);
        mod.DoorPassage.Draw(b,mod.Textures[$"{wing}/{mod.Flight.Animation}/{view}/in_front"],anchor,rect,Color.White * mod.Flight.Opacity,
            wingRotation,wingOrigin,bodyScale * mod.Config.WingScale,after,direction,mod.Textures[$"{wing}/T/{view}/in_front"]);
    }

    private static void DrawFinalizer(FlightPoseDrawing.PoseContext? __state) => FlightPoseDrawing.Current = __state;

    private bool Recover(string reason)
    {
        Swim?.Cancel();
        if (Swim?.YieldToNativeSwimming() == true) return true;
        if (Recovering || !FlightSessionAllowed) return false;
        RecoveryRetryMs = 1000;
        Monitor.Log($"Flight recovery requested: {reason}; stage={Flight.Stage}; location={Game1.player.currentLocation.NameOrUniqueName}.",LogLevel.Debug);
        Recovering = true;
        try
        {
            Farmer who = Game1.player;
            if (IsFishing && who.CurrentTool is FishingRod rod)
            {
                // Forced interruptions use the rod's own cleanup; no extra bait/tackle consumption.
                AccessTools.Method(typeof(FishingRod), "doDoneFishing").Invoke(rod, new object[] { false });
                rod.resetState();
            }
            if (EndFlightAtBed(reason)) return true;
            if (Surface == null || !Surface.Matches(who.currentLocation)) RebuildSurface();
            if (Surface != null && Safety.TryFindGround(who, who.currentLocation, Surface, out Vector2 ground))
            {
                who.Position = ground;
                Flight.Reset();
                Altitude.Reset();
                LandingHint.Reset();
                WaterWake.Reset();
                DoorPassage.Reset();
                Safety.Remember(who);
                who.modData.Remove(RecoveryKey);
                Monitor.Log($"Flight ended safely ({reason}); location={who.currentLocation.NameOrUniqueName}; position={who.Position}.", LogLevel.Debug);
                return true;
            }
            if (Safety.LastSafe is { } safe && safe.Location != who.currentLocation.NameOrUniqueName)
            {
                GameLocation location = Game1.getLocationFromName(safe.Location);
                if (location != null && float.IsFinite(safe.X) && float.IsFinite(safe.Y))
                {
                    Vector2 target = new(safe.X, safe.Y);
                    Point center = FlightSafety.BoxAt(who, target).Center;
                    var otherMap = new FlightMap(location, new Point(center.X / 64, center.Y / 64));
                    if (Safety.CanStand(who, location, target, otherMap))
                    {
                        PendingRecovery = safe;
                        Flight.RetainForRecovery();
                        Game1.warpFarmer(safe.Location, center.X / 64, center.Y / 64, false);
                        return true;
                    }
                }
            }
            if (Swim?.TryRecoverToSwimming() == true) return true;
            SaveRecoveryMarker();
            Flight.RetainForRecovery();
            Notice("recovery-failed");
            Monitor.Log($"No verified landing spot ({reason}). Recovery marker retained; no unsafe switch to walking was made.", LogLevel.Error);
            return false;
        }
        catch (Exception ex)
        {
            Flight.RetainForRecovery();
            Monitor.Log($"Safe recovery needs attention ({reason}).\n{ex}", LogLevel.Error);
            return false;
        }
        finally { Recovering = false; }
    }

    private void SaveRecoveryMarker()
    {
        if (Safety.LastSafe != null) Game1.player.modData[RecoveryKey] = JsonSerializer.Serialize(Safety.LastSafe);
    }

    private void ResetSession()
    {
        Swim?.Cancel();
        Flight.Reset();
        DoorPassage.Reset();
        Altitude.Reset();
        LandingHint.Reset();
        WaterWake.Reset();
        ControllerInput.Cancel();
        ControllerCaptured = false;
        WasUsingTool = false;
        Surface = null;
        Safety.LastSafe = null;
        PendingRecovery = null;
        EventRecoveryPending = false;
        WorldSorted = Recovering = false;
        NoticeCooldown = RecoveryRetryMs = 0;
    }

    internal void StartFlightFromSwimming(FlightMap surface)
    {
        Surface = surface;
        // This is a water recovery origin, not a required dry landing point.
        // Recover rechecks it and can hand back to Swim when no dry anchor exists.
        Safety.Remember(Game1.player);
        SaveRecoveryMarker();
        Flight.Toggle(false, false);
        LandingHint.Reset();
    }

    internal void ReleaseFlightForSwimming()
    {
        Swim?.Cancel();
        Flight.Reset();
        Altitude.Reset(); LandingHint.Reset(); WaterWake.Reset(); DoorPassage.Reset();
        ControllerInput.Cancel(); ControllerCaptured = WasUsingTool = false;
        PendingRecovery = null; EventRecoveryPending = false; RecoveryRetryMs = 0;
        // Swimming may reach a different island in the same map. A new takeoff
        // must seed its land component from that shore, not the old flight's shore.
        Surface = null; Safety.LastSafe = null;
        Game1.player.modData.Remove(RecoveryKey);
    }

    private void OnReturnedToTitle(object? sender, ReturnedToTitleEventArgs e)
    {
        // A temporary local-coop farmhand picker has its own screen and can return
        // to title without the main player leaving the loaded save.
        if (Context.ScreenId == 0) ResetSession();
    }

    internal void Notice(string key)
    {
        if (NoticeCooldown > 0 || !Context.IsWorldReady) return;
        Game1.addHUDMessage(new HUDMessage(Helper.Translation.Get(key)));
        NoticeCooldown = 1500;
    }

    internal string Translate(string key, object? tokens = null) => Helper.Translation.Get(
        key == "controls-ground-detail" && Swim?.Enabled == true ? "controls-ground-detail-swim" : key, tokens);

    private static float ClampFinite(float value, float min, float max, float fallback) => float.IsFinite(value) ? Math.Clamp(value,min,max) : fallback;
}
