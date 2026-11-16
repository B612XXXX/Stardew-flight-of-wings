using HarmonyLib;
using Microsoft.Xna.Framework;
using StardewValley;
using System.Reflection;
using System.Reflection.Emit;

namespace AngelWings.VisualPreview.Flight;

public static class FlightCollision
{
    [ThreadStatic] private static GameLocation? Current;
    [ThreadStatic] private static int Suppression;
    public static IDisposable OriginalRules()
    {
        Suppression++;
        return new DisposeAction(()=>Suppression--);
    }

    public static void Install(Harmony harmony)
    {
        MethodInfo target = typeof(GameLocation).GetMethods().Single(m=>m.Name=="isCollidingPosition" && m.GetParameters().Length==10);
        harmony.Patch(target,prefix:new HarmonyMethod(typeof(FlightCollision),nameof(Prefix)),
            finalizer:new HarmonyMethod(typeof(FlightCollision),nameof(Finalizer)));
        var predicates = typeof(GameLocation).GetNestedTypes(BindingFlags.NonPublic|BindingFlags.Public)
            .SelectMany(t=>t.GetMethods(BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public|BindingFlags.DeclaredOnly))
            .Where(m=>m.Name.Contains("isCollidingPosition") && m.ReturnType==typeof(bool)
                && m.GetParameters().Select(p=>p.ParameterType).SequenceEqual(new[]{typeof(Vector2)}))
            .Select(m=>(Method:m,Strings:PatchProcessor.GetOriginalInstructions(m).Where(c=>c.opcode==OpCodes.Ldstr).Select(c=>(string)c.operand).ToArray()))
            .ToArray();
        MethodInfo back = predicates.Single(p=>p.Strings.SequenceEqual(new[]{"Passable","Passable"})).Method;
        MethodInfo buildings = predicates.Single(p=>p.Strings.Contains("Shadow") && p.Strings.Contains("ProjectilePassable") && p.Strings.Contains("NPCPassable")).Method;
        // Postfixes only change the two identified tile results; NPCs, walls, trees,
        // furniture, event barriers and derived location collision methods still run.
        harmony.Patch(back,postfix:new HarmonyMethod(typeof(FlightCollision),nameof(BackTilePostfix)));
        harmony.Patch(buildings,postfix:new HarmonyMethod(typeof(FlightCollision),nameof(BuildingTilePostfix)));
        harmony.Patch(AccessTools.Method(typeof(Farmer),nameof(Farmer.collideWith),new[]{typeof(StardewValley.Object)}),
            postfix:new HarmonyMethod(typeof(FlightCollision),nameof(FencePostfix)));
    }

    private static bool Prefix(GameLocation __instance, Rectangle position, Character character,
        bool pathfinding, bool projectile, ref bool __result, out GameLocation? __state)
    {
        __state = Current;
        Current = null;
        ModEntry? mod = ModEntry.Instance;
        if(Suppression>0 || mod==null || !mod.CanUseFlightCollision(character) || pathfinding || projectile) return true;
        if(mod.Surface==null || !mod.Surface.Matches(__instance))
        { __result=true; return false; }
        // The native movement path checks warps before collision and deliberately
        // treats map-edge rectangles as open. Let it own that handoff unchanged.
        if(mod.Surface.CrossesMapBoundary(position))return true;
        if(!mod.Surface.CanTraverse(position)){ __result=true; return false; }
        Current=__instance;
        return true;
    }

    private static void Finalizer(GameLocation? __state) => Current=__state;
    private static void BackTilePostfix(Vector2 __0, ref bool __result)
    {
        if(__result && Current!=null && Suppression==0 && ModEntry.Instance?.Surface is FlightMap map
            && map.Matches(Current) && map.CanCrossWater((int)__0.X,(int)__0.Y))__result=false;
    }
    private static void BuildingTilePostfix(Vector2 __0, ref bool __result)
    {
        if(__result && Current!=null && Suppression==0 && ModEntry.Instance?.Surface is FlightMap map && map.Matches(Current)
            && (map.CanCrossWater((int)__0.X,(int)__0.Y) || map.CanCrossMapLowObstacle((int)__0.X,(int)__0.Y)))__result=false;
    }
    private static void FencePostfix(Farmer __instance, StardewValley.Object o, ref bool __result)
    {
        if(Current!=null && Suppression==0 && o is Fence && ModEntry.Instance?.CanUseFlightCollision(__instance)==true)__result=false;
    }

    private sealed class DisposeAction(Action action) : IDisposable
    {
        private Action? Action=action;
        public void Dispose() { Interlocked.Exchange(ref Action,null)?.Invoke(); }
    }
}
