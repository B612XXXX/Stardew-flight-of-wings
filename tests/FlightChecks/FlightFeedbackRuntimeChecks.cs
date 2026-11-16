using System.Reflection;
using System.Runtime.CompilerServices;
using AngelWings.VisualPreview;
using AngelWings.VisualPreview.Flight;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;

static class FlightFeedbackRuntimeChecks
{
    static readonly List<(Vector2 Position,float Scale,Color Color)> Calls=new();
    static bool Capture(Vector2 position,float scale,Color color)
    { Calls.Add((position,scale,color));return false; }
    static bool CaptureVector(Vector2 position,Vector2 scale,Color color) => Capture(position,scale.X,color);
    public static int Run(Harmony harmony,ModEntry mod,Farmer who,FlightController flight)
    {
        var signature=new[]{typeof(Texture2D),typeof(Vector2),typeof(Rectangle?),typeof(Color),typeof(float),typeof(Vector2),typeof(float),typeof(SpriteEffects),typeof(float)};
        harmony.Patch(AccessTools.Method(typeof(SpriteBatch),nameof(SpriteBatch.Draw),signature),prefix:new HarmonyMethod(typeof(FlightFeedbackRuntimeChecks),nameof(Capture)));
        signature[6]=typeof(Vector2);
        harmony.Patch(AccessTools.Method(typeof(SpriteBatch),nameof(SpriteBatch.Draw),signature),prefix:new HarmonyMethod(typeof(FlightFeedbackRuntimeChecks),nameof(CaptureVector)));
        return Exercise(mod,who,flight);
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    static int Exercise(ModEntry mod,Farmer who,FlightController flight)
    {
        int count=0;
        void Check(bool ok,string message){if(!ok)throw new Exception(message);count++;}
        var texture=(Texture2D)RuntimeHelpers.GetUninitializedObject(typeof(Texture2D));
        foreach(string name in new[]{"width","height"})
            typeof(Texture2D).GetFields(BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public).First(f=>f.FieldType==typeof(int)&&f.Name.Contains(name,StringComparison.OrdinalIgnoreCase)).SetValue(texture,16);
        var oldTexture=Game1.shadowTexture;var sorted=AccessTools.Field(typeof(ModEntry),"WorldSorted");object oldSorted=sorted.GetValue(mod)!;
        var altitude=(FlightAltitude)AccessTools.Field(typeof(ModEntry),"Altitude").GetValue(mod)!;
        try
        {
            Game1.shadowTexture=texture;sorted.SetValue(mod,false);
            flight.Reset();flight.Toggle(true,false);for(int i=0;i<7;i++)flight.Advance(100,true,false,false);
            altitude.Reset();Vector2 ground=who.Position;
            var batch=(SpriteBatch)RuntimeHelpers.GetUninitializedObject(typeof(SpriteBatch));
            Calls.Clear();who.DrawShadow(batch);
            Check(Calls.Count==1,"Native DrawShadow must produce one shadow even before World_Sorted");var low=Calls[0];
            for(int i=0;i<120;i++)altitude.Advance(16,true,88,true);
            Calls.Clear();who.DrawShadow(batch);var high=Calls.Single();
            Check(high.Position==low.Position && who.Position==ground,"Climbing must not lift the shadow or mutate player coordinates");
            Check(high.Scale<low.Scale && high.Color.A<low.Color.A,"Actual shadow drawing must reflect adaptive altitude");
            var other=new Farmer();other.Position=ground;
            Calls.Clear();other.DrawShadow(batch);
            Check(Calls.Single().Scale==4 && Calls[0].Color==Color.White,"Other actors must keep native shadows");
            flight.Reset();Calls.Clear();who.DrawShadow(batch);
            Check(Calls.Single().Scale==4 && Calls[0].Color==Color.White,"Grounded player must regain the exact native shadow");
            Console.WriteLine("PASS: actual Farmer.DrawShadow at cruise/high/grounded, fixed ground anchor, other actor unchanged; GPU submission intercepted.");
        }
        finally {Game1.shadowTexture=oldTexture;sorted.SetValue(mod,oldSorted);altitude.Reset();}
        return count;
    }
}
