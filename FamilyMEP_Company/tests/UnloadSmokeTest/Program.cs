using System.Reflection;
using System.Runtime.CompilerServices;
using RevitHotLoader2025;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: UnloadSmokeTest <source-dll> <shadow-dll>");
    return 2;
}

string sourceDll = Path.GetFullPath(args[0]);
string shadowDll = Path.GetFullPath(args[1]);
Directory.CreateDirectory(Path.GetDirectoryName(shadowDll)!);
File.Copy(sourceDll, shadowDll, overwrite: true);

WeakReference weakContext = LoadInvokeAndUnload(shadowDll);

using (File.Open(sourceDll, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
{
    Console.WriteLine("Source DLL is writable while the shadow DLL is used: OK");
}

for (int attempt = 0; weakContext.IsAlive && attempt < 10; attempt++)
{
    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();
}

if (weakContext.IsAlive)
{
    Console.Error.WriteLine("Collectible AssemblyLoadContext did not unload.");
    return 1;
}

Console.WriteLine("Collectible AssemblyLoadContext unloaded: OK");
return 0;

[MethodImpl(MethodImplOptions.NoInlining)]
static WeakReference LoadInvokeAndUnload(string shadowDll)
{
    var context = new PluginLoadContext(shadowDll);
    var weakContext = new WeakReference(context, trackResurrection: true);
    Assembly assembly = context.LoadFromAssemblyPath(shadowDll);
    Type type = assembly.GetType("GenericPlugin.Marker", throwOnError: true)!;
    MethodInfo method = type.GetMethod("GetValue", BindingFlags.Public | BindingFlags.Static)!;
    string value = (string)method.Invoke(null, null)!;
    if (value != "hot-reload-smoke-test")
    {
        throw new InvalidOperationException($"Unexpected plugin result: {value}");
    }

    Console.WriteLine("Shadow DLL loaded and invoked: OK");
    context.Unload();
    return weakContext;
}

