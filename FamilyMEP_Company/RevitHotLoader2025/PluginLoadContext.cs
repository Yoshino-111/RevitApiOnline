using System.Reflection;
using System.Runtime.Loader;

namespace RevitHotLoader2025;

public sealed class PluginLoadContext : AssemblyLoadContext
{
    private static readonly HashSet<string> SharedAssemblyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "RevitHotReload.Abstractions",
        "RevitAPI",
        "RevitAPIUI",
    };

    private readonly AssemblyDependencyResolver _resolver;

    public PluginLoadContext(string pluginAssemblyPath)
        : base($"RevitHotReload-{Guid.NewGuid():N}", isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(pluginAssemblyPath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name is not null && SharedAssemblyNames.Contains(assemblyName.Name))
        {
            return Default.Assemblies.FirstOrDefault(
                assembly => string.Equals(
                    assembly.GetName().Name,
                    assemblyName.Name,
                    StringComparison.OrdinalIgnoreCase));
        }

        string? assemblyPath = _resolver.ResolveAssemblyToPath(assemblyName);
        return assemblyPath is null ? null : LoadFromAssemblyPath(assemblyPath);
    }

    protected override nint LoadUnmanagedDll(string unmanagedDllName)
    {
        string? libraryPath = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return libraryPath is null ? nint.Zero : LoadUnmanagedDllFromPath(libraryPath);
    }
}

