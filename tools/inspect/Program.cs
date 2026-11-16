using System.Reflection;
using System.Runtime.Loader;

string game = args[0];
AssemblyLoadContext.Default.Resolving += (context, name) =>
{
    foreach (string dir in new[] { game, Path.Combine(game, "smapi-internal") })
    {
        string path = Path.Combine(dir, name.Name + ".dll");
        if (File.Exists(path)) return context.LoadFromAssemblyPath(path);
    }
    return null;
};
Assembly assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(game, "Stardew Valley.dll"));
if (args.Length > 1)
{
    Assembly mod = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(args[1]));
    if (args.Length > 2 && args[2] == "--quest-profile")
    {
        string profile = args[3];
        (int Hearts, int Flowers) expected = profile switch
        {
            "QuickTest" => (3, 0),
            "Formal" => (8, 369),
            _ => throw new ArgumentException("Quest profile must be QuickTest or Formal.")
        };
        Type quest = mod.GetType("AngelWings.VisualPreview.Quests.HaleyFlowerQuestService", true)!;
        int hearts = (int)quest.GetField("RequiredHaleyHearts")!.GetRawConstantValue()!;
        int flowers = (int)quest.GetField("RequiredFlowers")!.GetRawConstantValue()!;
        string version = mod.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+', 2)[0];
        if ((hearts, flowers) != expected || version.EndsWith("-quick-test") != (profile == "QuickTest"))
            throw new InvalidOperationException($"Compiled quest profile mismatch: {version}, {hearts} hearts, {flowers} flowers.");
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
        {
            profile, version, haley_hearts = hearts, flowers,
            source = Path.GetFullPath(args[1]), game_session_tested = false
        }));
        return;
    }
    Assembly harmony = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(game, "smapi-internal/0Harmony.dll"));
    Type harmonyType = harmony.GetType("HarmonyLib.Harmony", true)!;
    Type harmonyMethodType = harmony.GetType("HarmonyLib.HarmonyMethod", true)!;
    const string id = "AngelWingsProject.HeadlessBindingCheck";
    object patcher = Activator.CreateInstance(harmonyType, id)!;
    MethodInfo install = mod.GetType("AngelWings.VisualPreview.ModEntry", true)!.GetMethod("InstallPatches", BindingFlags.Public | BindingFlags.Static)!;
    try
    {
        install.Invoke(null, new object[] { patcher });
        var patched = ((System.Collections.IEnumerable)harmonyType.GetMethod("GetPatchedMethods")!.Invoke(patcher, null)!).Cast<MethodBase>().ToArray();
        Console.WriteLine($"PASS: all {patched.Length} flight patches installed against actual game methods.");
        foreach(var method in patched)Console.WriteLine($"  {method.DeclaringType?.FullName}.{method.Name}");
        Console.WriteLine("No game drawing method was invoked; graphics and in-game behavior remain unverified.");
    }
    finally
    {
        harmonyType.GetMethod("UnpatchAll")!.Invoke(patcher, new object?[] { id });
    }
    return;
}
foreach (string typeName in new[] { "StardewValley.FarmerRenderer", "StardewValley.Farmer", "StardewValley.Mods.RenderSteps", "StardewValley.Character" })
{
    Type type = assembly.GetType(typeName, true)!;
    Console.WriteLine(type.FullName);
    if (type.IsEnum) { Console.WriteLine(string.Join(", ", Enum.GetNames(type))); continue; }
    foreach (MethodInfo method in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
        if (method.Name.Contains("draw", StringComparison.OrdinalIgnoreCase) || method.Name is "isMoving" or "getLocalPosition" or "getStandingY")
            Console.WriteLine($"  {method.ReturnType.Name} {method.Name}({string.Join(", ", method.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))})");
    foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        if (field.Name.Contains("Offset", StringComparison.OrdinalIgnoreCase) || field.Name.Contains("jump", StringComparison.OrdinalIgnoreCase) || field.Name.Contains("renderer", StringComparison.OrdinalIgnoreCase))
            Console.WriteLine($"  field {field.FieldType.Name} {field.Name}");
}
