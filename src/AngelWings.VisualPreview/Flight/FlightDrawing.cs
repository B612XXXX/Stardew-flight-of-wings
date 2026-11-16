using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.Tools;
using System.Reflection;
using System.Reflection.Emit;

namespace AngelWings.VisualPreview.Flight;

public static class FlightDrawing
{
    public static void Install(Harmony harmony)
    {
        harmony.Patch(AccessTools.Method(typeof(Farmer),nameof(Farmer.DrawShadow),new[]{typeof(SpriteBatch)}),
            prefix:new HarmonyMethod(typeof(FlightDrawing),nameof(ShadowPrefix)));
        foreach(MethodInfo method in new[] {
            AccessTools.Method(typeof(Farmer),nameof(Farmer.draw),new[]{typeof(SpriteBatch)}),
            AccessTools.Method(typeof(Game1),nameof(Game1.drawTool),new[]{typeof(Farmer),typeof(int)}),
            AccessTools.Method(typeof(Game1),nameof(Game1.drawPlayerHeldObject),new[]{typeof(Farmer)}),
            AccessTools.Method(typeof(FishingRod),nameof(FishingRod.draw),new[]{typeof(SpriteBatch)}) })
            harmony.Patch(method,transpiler:new HarmonyMethod(typeof(FlightDrawing),nameof(VisualCoordinates)));
        harmony.Patch(AccessTools.Method(typeof(FishingRod),nameof(FishingRod.draw),new[]{typeof(SpriteBatch)}),
            prefix:new HarmonyMethod(typeof(FlightDrawing),nameof(FishingPrefix)),
            finalizer:new HarmonyMethod(typeof(FlightDrawing),nameof(FishingFinalizer)));
    }

    private static bool ShadowPrefix(Farmer __instance,SpriteBatch b)
    {
        float lift=ModEntry.Instance?.GetFlightLift(__instance)??0;
        if(lift<=0)return true;
        // Preserve the native ground anchor and layering; never float the shadow.
        var texture=Game1.shadowTexture;
        Vector2 ground=__instance.GetShadowOffset()+__instance.Position+new Vector2(32,24);
        b.Draw(texture,Game1.GlobalToLocal(ground),texture.Bounds,Color.White*FlightVisuals.ShadowOpacity(lift),
            0,new Vector2(texture.Bounds.Center.X,texture.Bounds.Center.Y),FlightVisuals.ShadowScale(lift),
            SpriteEffects.None,__instance.getDrawLayer()-1E-06f);
        return false;
    }

    public static IEnumerable<CodeInstruction> VisualCoordinates(IEnumerable<CodeInstruction> instructions, MethodBase original)
    {
        int replaced=0;
        bool tool=original.DeclaringType==typeof(Game1) && original.Name==nameof(Game1.drawTool);
        foreach(CodeInstruction code in instructions)
        {
            // Read-only substitutions inside drawing methods. Collision, bobber and
            // fishing targets retain real world coordinates and their original rules.
            if(code.operand is MethodInfo called && typeof(Character).IsAssignableFrom(called.DeclaringType)
                && (code.opcode==OpCodes.Call || code.opcode==OpCodes.Callvirt))
            {
                string? helper=null;
                if(called.Name==nameof(Character.getLocalPosition) && called.GetParameters().Length==1)helper=nameof(LocalPosition);
                // Game1.drawTool also reads Position for terrain occlusion tests; retain those.
                if(!tool && called.Name=="get_Position" && called.ReturnType==typeof(Vector2))helper=nameof(WorldPosition);
                if(helper!=null)
                {
                    code.opcode=OpCodes.Call;
                    code.operand=AccessTools.Method(typeof(FlightDrawing),helper);
                    replaced++;
                }
            }
            yield return code;
        }
        if(replaced==0)throw new MissingMethodException($"No expected drawing coordinate reads in {original}.");
    }

    public static Vector2 WorldPosition(Character character) => character.Position-new Vector2(0,ModEntry.Instance?.GetVisualLift(character)??0);
    public static Vector2 LocalPosition(Character character, xTile.Dimensions.Rectangle viewport)
        => character.getLocalPosition(viewport)-new Vector2(0,ModEntry.Instance?.GetVisualLift(character)??0);

    private static void FishingPrefix(FishingRod __instance, out List<(TemporaryAnimatedSprite Sprite, Vector2 Position)>? __state)
    {
        __state=null;
        Farmer who=__instance.getLastFarmerToUse();
        float lift=ModEntry.Instance?.GetVisualLift(who)??0;
        if(lift<=0 || who==null || __instance.animations.Count==0)return;
        __state=new();
        // The rod's cast/retrieve sprites use ground-space ballistic paths. Lift the
        // player end, leave the water end fixed, and restore every value after drawing.
        Vector2 water=__instance.bobber.Value;
        Vector2 span=water-who.Position;
        float lengthSquared=span.LengthSquared();
        if(lengthSquared<1)return;
        foreach(TemporaryAnimatedSprite sprite in __instance.animations)
        {
            Vector2 p=sprite.position;
            float t=Math.Clamp(Vector2.Dot(p-who.Position,span)/lengthSquared,0,1);
            __state.Add((sprite,p));
            sprite.position.Y-=lift*(1-t);
        }
    }

    private static void FishingFinalizer(List<(TemporaryAnimatedSprite Sprite, Vector2 Position)>? __state)
    {
        if(__state==null)return;
        foreach(var entry in __state)entry.Sprite.position=entry.Position;
    }
}
