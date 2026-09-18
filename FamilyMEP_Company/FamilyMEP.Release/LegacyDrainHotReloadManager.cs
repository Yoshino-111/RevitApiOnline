#if REVIT2020
using System.Reflection;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitHotReload.Abstractions;

namespace FamilyMEP.Entry;

/// <summary>
/// Revit 2020 runs on .NET Framework, so it cannot use the collectible
/// AssemblyLoadContext used by Revit 2025. Loading the plugin from bytes keeps
/// the build output unlocked and gives every Drain button click a fresh plugin
/// assembly. Old assemblies remain in this Revit process, but their modeless
/// windows and event handlers are shut down before the next copy is executed.
/// </summary>
internal static class LegacyDrainHotReloadManager
{
    private const string PluginTypeName = "FamilyMEP.Plugin.DrainConnectionPlugin";
    private const string SourceFolderDataKey = "FamilyMEP.HotReloadSourceFolder";
    private static readonly object SyncRoot = new();
    private static IHotReloadPlugin? _currentPlugin;
    private static string? _currentSourceFolder;
    private static bool _resolverInstalled;

    public static Result ReloadAndExecute(
        ExternalCommandData commandData,
        ref string message,
        ElementSet elements)
    {
        lock (SyncRoot)
        {
            ShutdownCurrent();
            string assemblyPath = ResolvePluginAssemblyPath();
            _currentSourceFolder = Path.GetDirectoryName(assemblyPath)
                ?? throw new InvalidOperationException(
                    "Cannot determine the Revit 2020 hot-reload folder.");
            AppDomain.CurrentDomain.SetData(
                SourceFolderDataKey,
                _currentSourceFolder);
            InstallResolver();

            byte[] assemblyBytes = ReadAllBytesWithRetry(assemblyPath);
            string pdbPath = Path.ChangeExtension(assemblyPath, ".pdb");
            Assembly assembly = File.Exists(pdbPath)
                ? Assembly.Load(assemblyBytes, ReadAllBytesWithRetry(pdbPath))
                : Assembly.Load(assemblyBytes);
            Type pluginType = assembly.GetType(PluginTypeName, true)
                ?? throw new TypeLoadException(PluginTypeName);
            _currentPlugin = Activator.CreateInstance(pluginType) as IHotReloadPlugin
                ?? throw new InvalidOperationException(
                    $"{PluginTypeName} must implement {typeof(IHotReloadPlugin).FullName}.");
            WriteLog($"Loaded {assemblyPath}; module={assembly.ManifestModule.ModuleVersionId}.");
            return _currentPlugin.Execute(commandData, ref message, elements);
        }
    }

    public static void Shutdown()
    {
        lock (SyncRoot)
        {
            ShutdownCurrent();
        }
    }

    private static void ShutdownCurrent()
    {
        if (_currentPlugin is null)
            return;
        try
        {
            _currentPlugin.Shutdown();
        }
        catch (Exception exception)
        {
            WriteLog($"Shutdown error: {exception}");
        }
        finally
        {
            _currentPlugin = null;
        }
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }

    private static string ResolvePluginAssemblyPath()
    {
        string entryFolder = Path.GetDirectoryName(
            Assembly.GetExecutingAssembly().Location)
            ?? throw new InvalidOperationException(
                "Cannot determine the FamilyMEP.Entry folder.");
        string configPath = Path.Combine(entryFolder, "hotreload-source.txt");
        if (File.Exists(configPath))
        {
            string configured = File.ReadAllText(configPath).Trim().Trim('"');
            if (File.Exists(configured))
                return Path.GetFullPath(configured);
        }

        string developmentBuild = Path.GetFullPath(Path.Combine(
            entryFolder,
            "..", "..", "..", "..",
            "multi-release", "2020", "FamilyMEP.Plugin.dll"));
        if (File.Exists(developmentBuild))
            return developmentBuild;

        string packaged = Path.Combine(entryFolder, "FamilyMEP.Plugin.dll");
        if (File.Exists(packaged))
            return packaged;
        throw new FileNotFoundException(
            "Build the Revit 2020 plugin before running Drain Connection.",
            developmentBuild);
    }

    private static void InstallResolver()
    {
        if (_resolverInstalled)
            return;
        AppDomain.CurrentDomain.AssemblyResolve += ResolveDependency;
        _resolverInstalled = true;
    }

    private static Assembly? ResolveDependency(object? sender, ResolveEventArgs args)
    {
        string simpleName = new AssemblyName(args.Name).Name ?? string.Empty;
        Assembly? loaded = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(item => string.Equals(
                item.GetName().Name,
                simpleName,
                StringComparison.OrdinalIgnoreCase));
        if (loaded is not null)
            return loaded;
        if (string.IsNullOrWhiteSpace(_currentSourceFolder))
            return null;
        string candidate = Path.Combine(_currentSourceFolder, simpleName + ".dll");
        return File.Exists(candidate)
            ? Assembly.Load(ReadAllBytesWithRetry(candidate))
            : null;
    }

    private static byte[] ReadAllBytesWithRetry(string path)
    {
        Exception? lastError = null;
        for (int attempt = 1; attempt <= 8; attempt++)
        {
            try
            {
                return File.ReadAllBytes(path);
            }
            catch (IOException exception)
            {
                lastError = exception;
                Thread.Sleep(attempt * 75);
            }
        }
        throw new IOException(
            "The Revit 2020 plugin is still being written. Build again, then click Drain Connection.",
            lastError);
    }

    private static void WriteLog(string text)
    {
        try
        {
            string entryFolder = Path.GetDirectoryName(
                Assembly.GetExecutingAssembly().Location) ?? string.Empty;
            string log = Path.Combine(entryFolder, "hotreload-2020.log");
            File.AppendAllText(
                log,
                $"{DateTime.Now:O} {text}{Environment.NewLine}");
        }
        catch
        {
            // Diagnostics must never block a Revit command.
        }
    }
}
#endif
