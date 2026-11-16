using System.Reflection;
using AngelWings.VisualPreview.Flight;
using HarmonyLib;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Utilities;
using StardewValley;
using StardewValley.Locations;

namespace AngelWings.VisualPreview.Compatibility;

/// <summary>Optional, version-bound integration. Never changes Swim's files or settings.</summary>
internal sealed class SwimCompatibility(ModEntry mod)
{
    private const string ModId = "FlyingTNT.Swim";
    private const string PatchId = "AngelWingsProject.VisualPreview.SwimCompatibility";
    private Harmony? patches;
    private Func<bool>? enabled;
    private Func<bool>? canSwimHere;
    private Func<bool>? isInWater;
    private Func<string, bool>? isMapUnderwater;
    private Func<bool>? alwaysSwimsuit;
    private Func<Vector2, GameLocation, bool>? validTarget;
    private Action<Vector2, int>? enterWater;
    private PerScreen<bool>? jumping, willSwim, underwater;
    private PerScreen<Vector2>? start, end;
    private PerScreen<ulong>? lastJump;
    private Farmer? pendingPlayer;
    private GameLocation? pendingLocation;

    internal bool Committing { get; private set; }
    internal bool Waiting => pendingPlayer != null;
    internal bool Enabled => enabled?.Invoke() == true;
    internal bool IsUnderwater => Enabled && (underwater!.Value
        || (Game1.player.currentLocation is { } location && isMapUnderwater!(location.Name)));
    internal bool InWaterOrTransition => Enabled && (Game1.player.swimming.Value || jumping!.Value || IsUnderwater);

    internal void Initialize(IModRegistry registry)
    {
        var info = registry.Get(ModId);
        if (info == null) return;
        var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Swim");
        if (assembly == null || !Bind(assembly, info.Manifest.Version.ToString()))
            mod.Monitor.Log("Swim compatibility unavailable for this version. Water handoff is disabled; ordinary flight rules remain enabled.", LogLevel.Warn);
    }

    // The assembly argument also allows offline checks without shipping a Swim dependency.
    internal bool Bind(Assembly assembly, string version)
    {
        if (version != "1.9.0") return false;
        try
        {
            var entry = assembly.GetType("Swim.ModEntry", throwOnError: true)!;
            var utils = assembly.GetType("Swim.SwimUtils", throwOnError: true)!;
            var events = assembly.GetType("Swim.SwimHelperEvents", throwOnError: true)!;
            var config = AccessTools.Property(entry, "Config") ?? throw new MissingMemberException("Swim.Config");
            var enable = AccessTools.Property(config.PropertyType, "EnableMod") ?? throw new MissingMemberException("Swim.EnableMod");
            var suit = AccessTools.Property(config.PropertyType, "SwimSuitAlways") ?? throw new MissingMemberException("Swim.SwimSuitAlways");
            Func<bool> readEnabled = () => config.GetValue(null) is { } value && enable.GetValue(value) is true;
            Func<bool> readSuit = () => config.GetValue(null) is { } value && suit.GetValue(value) is true;
            var canEnter = Require(utils, "CanSwimHere", typeof(bool)).CreateDelegate<Func<bool>>();
            var water = Require(utils, "IsInWater", typeof(bool)).CreateDelegate<Func<bool>>();
            var underwaterMap = Require(utils, "IsMapUnderwater", typeof(bool), typeof(string)).CreateDelegate<Func<string, bool>>();
            var target = Require(utils, "IsValidJumpLocation", typeof(bool), typeof(Vector2), typeof(GameLocation))
                .CreateDelegate<Func<Vector2, GameLocation, bool>>();
            var jump = Require(events, "DoJump", typeof(void), typeof(Vector2), typeof(int)).CreateDelegate<Action<Vector2, int>>();
            var isJumping = ReadScreen<bool>(events, "isJumping");
            var nextSwim = ReadScreen<bool>(events, "willSwim");
            var jumpStart = ReadScreen<Vector2>(events, "startJumpLoc");
            var jumpEnd = ReadScreen<Vector2>(events, "endJumpLoc");
            var jumpTime = ReadScreen<ulong>(events, "lastJump");
            var isUnderwater = AccessTools.Property(entry, "isUnderwater")?.GetValue(null) as PerScreen<bool>
                ?? throw new MissingMemberException("Swim.isUnderwater");
            var gate = Require(utils, "IsSafeToTryJump", typeof(bool));
            var dive = utils.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .Single(m => m.Name == "DiveTo" && m.ReturnType == typeof(void) && m.GetParameters().Length == 1
                    && m.GetParameters()[0].ParameterType.FullName == "Swim.Models.DiveLocation");

            patches = new Harmony(PatchId);
            patches.Patch(gate, postfix: new HarmonyMethod(typeof(SwimCompatibility), nameof(JumpGatePostfix)));
            patches.Patch(dive, prefix: new HarmonyMethod(typeof(SwimCompatibility), nameof(DivePrefix)));
            canSwimHere = canEnter; isInWater = water; validTarget = target; enterWater = jump;
            jumping = isJumping; willSwim = nextSwim; start = jumpStart; end = jumpEnd; lastJump = jumpTime;
            underwater = isUnderwater;
            isMapUnderwater = underwaterMap; alwaysSwimsuit = readSuit;
            enabled = readEnabled;
            mod.Monitor.Log("Optional Swim 1.9.0 compatibility enabled. Surface swimming and flight can switch both ways; underwater takeoff is blocked.", LogLevel.Info);
            return true;
        }
        catch (Exception ex)
        {
            patches?.UnpatchAll(PatchId);
            enabled = null;
            mod.Monitor.Log($"Could not bind optional Swim compatibility: {ex.Message}", LogLevel.Warn);
            return false;
        }
    }

    private static MethodInfo Require(Type type, string name, Type result, params Type[] parameters)
    {
        var method = AccessTools.Method(type, name, parameters);
        if (method == null || !method.IsStatic || method.ReturnType != result) throw new MissingMethodException(type.FullName, name);
        return method;
    }

    private static PerScreen<T> ReadScreen<T>(Type type, string name) =>
        AccessTools.Field(type, name)?.GetValue(null) as PerScreen<T> ?? throw new MissingMemberException(type.FullName, name);

    private static void JumpGatePostfix(ref bool __result)
    {
        var owner = ModEntry.Instance;
        if (owner?.Swim?.Enabled == true && owner.ManagesLocalSwim && owner.Flight.Active) __result = false;
    }

    private static bool DivePrefix()
    {
        var owner = ModEntry.Instance;
        return owner?.Swim?.Enabled != true || !owner.ManagesLocalSwim || !owner.Flight.Active;
    }

    internal bool TryTakeoff()
    {
        if (!mod.ManagesLocalSwim || !InWaterOrTransition) return false;
        if (IsUnderwater) { mod.Notice("swim-surface-first"); return true; }
        if (jumping!.Value) { mod.Notice("swim-wait-transition"); return true; }
        var who = Game1.player;
        if (!mod.SurfaceTakeoffAllowed || mod.ActionBusy || !Context.IsPlayerFree || !Context.CanPlayerMove
            || who.freezePause > 0 || Game1.paused || Game1.activeClickableMenu != null || Game1.dialogueUp
            || Game1.freezeControls || Game1.fadeToBlack || Game1.eventOver || Game1.locationRequest != null || Game1.isWarping)
        { mod.Notice("cannot-start"); return true; }

        var location = who.currentLocation;
        var snapshot = new EntrySnapshot(who, jumping!, willSwim!, start!, end!, lastJump!);
        Committing = true;
        try
        {
            if (!canSwimHere!() || !isInWater!() || !TryTakeoffSurface(who, out var surface))
            { mod.Notice("swim-takeoff-unavailable"); return true; }

            // Use Swim's own exit setup and cooldown. Finish its exit immediately at
            // the same position; the existing wing takeoff supplies the animation.
            enterWater!(who.Tile, -1);
            if (who.swimming.Value || !jumping.Value || willSwim!.Value)
                throw new InvalidOperationException("Swim did not start its exit action.");
            jumping.Value = false;
            who.freezePause = 0;
            if (!alwaysSwimsuit!()) who.changeOutOfSwimSuit();
            mod.StartFlightFromSwimming(surface!);
            mod.Monitor.Log("Surface swimming handed off to the existing flight takeoff.", LogLevel.Debug);
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(who, Game1.player) && ReferenceEquals(location, who.currentLocation))
            {
                mod.ReleaseFlightForSwimming();
                snapshot.Restore(who, jumping!, willSwim!, start!, end!, lastJump!);
            }
            mod.Notice("swim-takeoff-unavailable");
            mod.Monitor.Log($"Surface takeoff failed; swimming retained: {ex.Message}", LogLevel.Warn);
        }
        finally { Committing = false; }
        return true;
    }

    private bool TryTakeoffSurface(Farmer who, out FlightMap surface)
    {
        surface = new FlightMap(who.currentLocation, who.TilePoint);
        // Validate only the current surface. A shore is not required for takeoff.
        var box = who.GetBoundingBox();
        foreach (var tile in FlightMap.CoveredTiles(box))
        {
            var obstacles = CollisionMask.All & ~CollisionMask.Farmers;
            if (who.currentLocation.IsTileOccupiedBy(tile.ToVector2(), obstacles, obstacles)) return false;
            if (surface.IsDryStandTile(tile.X, tile.Y) && !surface.IsReachableGround(tile.X, tile.Y))
                surface = new FlightMap(who.currentLocation, tile);
        }
        return surface.CanTraverse(box);
    }

    // A flight started in water has no land component yet. Select one only when
    // the farmer actually approaches that shore; never search for a distant shore.
    internal void PrepareMovement(Farmer who)
    {
        if (!Enabled || !mod.HasWaterFlightOrigin || mod.Surface is not { } surface || !surface.Matches(who.currentLocation)) return;
        var box = who.GetBoundingBox();
        foreach (int direction in who.movementDirections)
            if (direction is >= 0 and <= 3) box = Rectangle.Union(box, who.nextPosition(direction));
        if (surface.CanTraverse(box)) return;
        foreach (var tile in FlightMap.CoveredTiles(box))
        {
            if (!surface.IsDryStandTile(tile.X, tile.Y) || surface.IsReachableGround(tile.X, tile.Y)) continue;
            // A water origin is recorded in the existing recovery format. Once a
            // real dry anchor is observed, ordinary flight access rules take over.
            var candidate = new FlightMap(who.currentLocation, tile);
            if (candidate.CanTraverse(box)) { mod.Surface = candidate; return; }
        }
    }

    internal bool TryRecoverToSwimming()
    {
        if (!CanEnter()) return false;
        var who = Game1.player;
        var snapshot = new EntrySnapshot(who, jumping!, willSwim!, start!, end!, lastJump!);
        Committing = true;
        try
        {
            enterWater!(who.Tile, -1);
            if (!jumping!.Value || !willSwim!.Value)
                throw new InvalidOperationException("Swim did not start its recovery entry.");
            // A forced save/event recovery must finish synchronously. Keep the
            // current position and hand a stable swimming state to native code.
            jumping.Value = false;
            who.freezePause = 0;
            who.swimming.Value = true;
            mod.ReleaseFlightForSwimming();
            mod.Monitor.Log("Flight recovery completed in native surface swimming.", LogLevel.Debug);
            return true;
        }
        catch (Exception ex)
        {
            snapshot.Restore(who, jumping!, willSwim!, start!, end!, lastJump!);
            mod.Monitor.Log($"Cannot recover flight into Swim: {ex.Message}", LogLevel.Warn);
            return false;
        }
        finally { Committing = false; }
    }

    internal bool TryToggle(bool canLand)
    {
        if (Waiting)
        {
            Cancel();
            return true;
        }
        if (!Enabled || !mod.Flight.Active || !mod.Flight.DesiredFlight || mod.Flight.LandingQueued || canLand)
            return false;
        if (!CanEnter()) return false;
        pendingPlayer = Game1.player;
        pendingLocation = Game1.player.currentLocation;
        if (!TryCommit()) mod.Notice("swim-after-action");
        return true;
    }

    internal void Update()
    {
        if (YieldToNativeSwimming()) return;
        if (!Waiting) return;
        if (!ReferenceEquals(pendingPlayer, Game1.player) || !ReferenceEquals(pendingLocation, Game1.player.currentLocation)
            || !mod.Flight.Active || !Enabled || !mod.LocationAllowed || Game1.eventOver
            || Game1.locationRequest != null || Game1.isWarping)
        { Cancel(); return; }
        if (mod.ActionBusy || mod.Flight.Stage != FlightStage.Flying || !Context.IsPlayerFree || !Context.CanPlayerMove
            || Game1.paused || Game1.activeClickableMenu != null || Game1.dialogueUp || Game1.freezeControls || Game1.fadeToBlack)
            return;
        if (!CanEnter()) { Cancel(); mod.Notice("swim-unavailable"); return; }
        TryCommit();
    }

    internal bool YieldToNativeSwimming()
    {
        if (!mod.ManagesLocalSwim || !InWaterOrTransition) return false;
        if (mod.HasFlightStateForSwimming || Waiting)
            mod.ReleaseFlightForSwimming();
        return true;
    }

    private bool CanEnter()
    {
        try { return InspectEntry(); }
        catch (Exception ex)
        {
            Cancel();
            mod.Monitor.Log($"Cannot validate optional Swim entry; flight retained: {ex.Message}", LogLevel.Warn);
            return false;
        }
    }

    private bool InspectEntry()
    {
        if (!mod.ManagesLocalSwim || !Enabled || InWaterOrTransition || !mod.LocationAllowed) return false;
        var who = Game1.player;
        var location = who.currentLocation;
        if (Game1.eventOver || Game1.locationRequest != null || Game1.isWarping || location is BeachNightMarket
            || location.Map.Properties.ContainsKey("ProhibitSwimming") || !canSwimHere!() || !isInWater!()
            || !validTarget!(who.Tile, location)) return false;
        var box = FlightSafety.BoxAt(who, who.Tile * 64);
        foreach (var tile in FlightMap.CoveredTiles(box))
        {
            var position = new Vector2(tile.X, tile.Y);
            var obstacles = CollisionMask.All & ~CollisionMask.Farmers;
            if (!location.isTileOnMap(position) || location.IsTileOccupiedBy(position, obstacles, obstacles)) return false;
        }
        return true;
    }

    // Returns false only while the original native tool/takeoff is still running.
    private bool TryCommit()
    {
        if (mod.ActionBusy || mod.Flight.Stage != FlightStage.Flying || !Context.IsPlayerFree || !Context.CanPlayerMove
            || Game1.paused || Game1.activeClickableMenu != null || Game1.dialogueUp || Game1.freezeControls || Game1.fadeToBlack)
            return false;
        if (!CanEnter()) { Cancel(); mod.Notice("swim-unavailable"); return true; }
        var who = Game1.player;
        var location = who.currentLocation;
        var snapshot = new EntrySnapshot(who, jumping!, willSwim!, start!, end!, lastJump!);
        Committing = true;
        try
        {
            // Committing bypasses our effects without destroying the flight controller.
            // Both calls run on the game thread; no update observes an unmanaged gap.
            enterWater!(who.Tile, -1);
            if (!jumping!.Value && !who.swimming.Value) throw new InvalidOperationException("Swim did not start its entry action.");
            mod.ReleaseFlightForSwimming();
            mod.Monitor.Log("Flight handed off to Swim's native surface entry.", LogLevel.Debug);
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(who, Game1.player) && ReferenceEquals(location, who.currentLocation))
                snapshot.Restore(who, jumping!, willSwim!, start!, end!, lastJump!);
            // Flight was never reset on failure, so its exact phase/height and marker survive.
            Cancel();
            mod.Notice("swim-unavailable");
            mod.Monitor.Log($"Swim entry failed; flight retained: {ex.Message}", LogLevel.Warn);
        }
        finally { Committing = false; }
        return true;
    }

    internal void Cancel() { pendingPlayer = null; pendingLocation = null; }

    private sealed class EntrySnapshot
    {
        private readonly bool wasJumping, nextSwim, swimming, clothes, walkOnly, running;
        private readonly int freeze, speed;
        private readonly Vector2 position, from, to;
        private readonly ulong time;
        private readonly int[] directions;

        internal EntrySnapshot(Farmer who, PerScreen<bool> jump, PerScreen<bool> next,
            PerScreen<Vector2> start, PerScreen<Vector2> end, PerScreen<ulong> last)
        {
            wasJumping = jump.Value; nextSwim = next.Value; from = start.Value; to = end.Value; time = last.Value;
            swimming = who.swimming.Value; clothes = who.bathingClothes.Value; walkOnly = who.canOnlyWalk;
            running = who.running; speed = who.speed; freeze = who.freezePause; position = who.Position;
            directions = who.movementDirections.ToArray();
        }

        internal void Restore(Farmer who, PerScreen<bool> jump, PerScreen<bool> next,
            PerScreen<Vector2> start, PerScreen<Vector2> end, PerScreen<ulong> last)
        {
            jump.Value = wasJumping; next.Value = nextSwim; start.Value = from; end.Value = to; last.Value = time;
            who.swimming.Value = swimming; who.bathingClothes.Value = clothes; who.canOnlyWalk = walkOnly;
            who.running = running; who.speed = speed; who.freezePause = freeze; who.Position = position;
            who.movementDirections.Clear(); who.movementDirections.AddRange(directions);
        }
    }
}
