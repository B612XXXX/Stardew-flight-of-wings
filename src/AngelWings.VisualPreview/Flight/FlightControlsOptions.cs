using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;
using System.Reflection;
using System.Reflection.Emit;

namespace AngelWings.VisualPreview.Flight;

public sealed record ControlHint(string Title, string Detail);

public static class FlightControlsOptions
{
    public const string ControlsHeadingKey = "Strings\\StringsFromCSFiles:OptionsPage.cs.11274";

    public static void Install(Harmony harmony) => harmony.Patch(
        AccessTools.Constructor(typeof(OptionsPage), new[] { typeof(int), typeof(int), typeof(int), typeof(int) }),
        transpiler: new HarmonyMethod(typeof(FlightControlsOptions), nameof(InsertAtControlsHeading)));

    public static IEnumerable<CodeInstruction> InsertAtControlsHeading(IEnumerable<CodeInstruction> instructions)
    {
        bool foundHeading = false;
        int replaced = 0;
        MethodInfo add = AccessTools.Method(typeof(List<OptionsElement>), nameof(List<OptionsElement>.Add));
        foreach (CodeInstruction code in instructions)
        {
            if (code.opcode == OpCodes.Ldstr && Equals(code.operand, ControlsHeadingKey)) foundHeading = true;
            if (foundHeading && code.Calls(add))
            {
                code.opcode = OpCodes.Call;
                code.operand = AccessTools.Method(typeof(FlightControlsOptions), nameof(AddHeadingAndHints));
                foundHeading = false;
                replaced++;
            }
            yield return code;
        }
        if (replaced != 1) throw new MissingMethodException("Expected one OptionsPage controls heading.");
    }

    // Insert during construction: a postfix insertion would shift the screenshot
    // button index captured by the rest of the original constructor.
    public static void AddHeadingAndHints(List<OptionsElement> options, OptionsElement heading)
    {
        options.Add(heading);
        if (ModEntry.Instance is not { } mod) return;
        for (int index = 0; index < 4; index++)
        {
            int row = index;
            options.Add(new WingControlHint(() => Describe(mod.Settings, mod.Translate)[row]));
        }
    }

    public static string ButtonLabel(SButton button, Func<string, object?, string> translate) => button switch
    {
        SButton.LeftStick => translate("button-left-stick", null),
        SButton.RightStick => translate("button-right-stick", null),
        SButton.None => translate("button-unbound", null),
        _ => button.ToString()
    };

    public static ControlHint[] Describe(ModConfig config, Func<string, object?, string> translate)
    {
        var keys = new
        {
            toggle = ButtonLabel(config.ToggleKey, translate),
            recover = ButtonLabel(config.EmergencyLandKey, translate),
            pad = ButtonLabel(config.ControllerToggleKey, translate)
        };
        return new[]
        {
            new ControlHint(translate("controls-toggle-title", null), translate(config.ControllerToggleKey == SButton.None ? "controls-toggle-keyboard-detail" : "controls-toggle-detail", keys)),
            new ControlHint(translate("controls-recover-title", null), translate(config.ControllerToggleKey == SButton.None ? "controls-recover-keyboard-detail" : "controls-recover-detail", keys)),
            new ControlHint(translate("controls-ground-title", null), translate("controls-ground-detail", null)),
            new ControlHint(translate("controls-fishing-title", null), translate("controls-fishing-detail", null))
        };
    }
}

/// <summary>Read-only instructions, visible and screen-readable with either input device.</summary>
public sealed class WingControlHint : OptionsElement
{
    private readonly Func<ControlHint> GetHint;
    public WingControlHint(Func<ControlHint> getHint) : base("")
    {
        GetHint = getHint;
        Refresh();
    }

    private ControlHint Refresh()
    {
        ControlHint hint = GetHint();
        label = ScreenReaderText = hint.Title;
        ScreenReaderDescription = hint.Detail;
        return hint;
    }

    public override void draw(SpriteBatch b, int slotX, int slotY, IClickableMenu? context = null)
    {
        ControlHint hint = Refresh();
        int slotWidth = context is OptionsPage page ? page.optionSlots[0].bounds.Width : 640;
        int slotHeight = context is OptionsPage owner ? owner.optionSlots[0].bounds.Height : 72;
        float availableWidth = Math.Max(1, slotWidth - 64);
        float lineHeight = Math.Max(1, (slotHeight - 12) / 2f);
        DrawLine(hint.Title, slotY + 4, Game1.textColor);
        DrawLine(hint.Detail, slotY + 4 + lineHeight, Game1.textColor * .85f);
        void DrawLine(string text, float y, Color color)
        {
            Vector2 size = Game1.smallFont.MeasureString(text);
            float scale = Math.Min(1, Math.Min(availableWidth / Math.Max(1, size.X), lineHeight / Math.Max(1, size.Y)));
            Utility.drawTextWithShadow(b, text, Game1.smallFont, new Vector2(slotX + 32, y), color, scale, .15f);
        }
    }
}
