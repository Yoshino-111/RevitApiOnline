using System.Reflection;
using System.Runtime.CompilerServices;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitHotReload.Abstractions;

namespace RevitHotLoader2025;

internal static class HotReloadManager
{
    private static readonly object SyncRoot = new();
    private static readonly Dictionary<string, PluginHandle> CurrentPlugins =
        new(StringComparer.Ordinal);

    public static Result ReloadAndExecute(
        ExternalCommandData commandData,
        ref string message,
        ElementSet elements,
        string? pluginTypeName = null)
    {
        lock (SyncRoot)
        {
            HotReloadSettings settings = HotReloadSettings.Load();
            string resolvedTypeName = string.IsNullOrWhiteSpace(pluginTypeName)
                ? settings.PluginTypeName
                : pluginTypeName;
            HotReloadLog.Write(settings.LogFile, $"Reload requested for '{resolvedTypeName}'.");

            UnloadResult unload = UnloadCurrent(settings, resolvedTypeName);
            PluginHandle handle = LoadLatest(settings, resolvedTypeName);
            CurrentPlugins[resolvedTypeName] = handle;

            string unloadNote = unload.HadPlugin
                ? unload.Completed
                    ? "Previous DLL unloaded."
                    : "Previous DLL is still referenced; latest DLL was loaded from a new shadow folder."
                : "First load.";

            HotReloadLog.Write(
                settings.LogFile,
                $"Loaded '{handle.Plugin.Name}' from '{handle.ShadowAssemblyPath}'. {unloadNote}");

            try
            {
                Result result = handle.Plugin.Execute(commandData, ref message, elements);
                HotReloadLog.Write(settings.LogFile, $"Plugin returned {result}.");
                return result;
            }
            catch (Exception exception)
            {
                message = exception.ToString();
                HotReloadLog.Write(settings.LogFile, exception.ToString());
                throw;
            }
        }
    }

    public static UnloadResult Unload()
    {
        lock (SyncRoot)
        {
            HotReloadSettings settings = HotReloadSettings.Load();
            List<string> pluginTypes = CurrentPlugins.Keys.ToList();
            if (pluginTypes.Count == 0) return new UnloadResult(false, true, null);

            bool completed = true;
            foreach (string pluginType in pluginTypes)
            {
                completed &= UnloadCurrent(settings, pluginType).Completed;
            }
            return new UnloadResult(true, completed, null);
        }
    }

    private static PluginHandle LoadLatest(HotReloadSettings settings, string pluginTypeName)
    {
        if (!File.Exists(settings.PluginAssemblyPath))
        {
            throw new FileNotFoundException(
                "Build the plugin DLL before running the hot loader.",
                settings.PluginAssemblyPath);
        }

        string sourceFolder = Path.GetDirectoryName(settings.PluginAssemblyPath)
            ?? throw new InvalidOperationException("Cannot determine the plugin output directory.");
        Directory.CreateDirectory(settings.ShadowRoot);

        string shadowFolder = Path.Combine(
            settings.ShadowRoot,
            $"{DateTime.Now:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid():N}");
        CopyDirectoryWithRetry(sourceFolder, shadowFolder);

        string shadowAssemblyPath = Path.Combine(
            shadowFolder,
            Path.GetFileName(settings.PluginAssemblyPath));
        var loadContext = new PluginLoadContext(shadowAssemblyPath);

        try
        {
            Assembly assembly = loadContext.LoadFromAssemblyPath(shadowAssemblyPath);
            Type pluginType = assembly.GetType(pluginTypeName, throwOnError: true)
                ?? throw new TypeLoadException(pluginTypeName);

            if (!typeof(IHotReloadPlugin).IsAssignableFrom(pluginType))
            {
                throw new InvalidOperationException(
                    $"{pluginTypeName} must implement {typeof(IHotReloadPlugin).FullName}.");
            }

            var plugin = (IHotReloadPlugin?)Activator.CreateInstance(pluginType)
                ?? throw new InvalidOperationException("Could not create the plugin instance.");
            return new PluginHandle(
                loadContext,
                plugin,
                shadowFolder,
                shadowAssemblyPath,
                pluginTypeName);
        }
        catch
        {
            loadContext.Unload();
            throw;
        }
    }

    private static UnloadResult UnloadCurrent(HotReloadSettings settings, string pluginTypeName)
    {
        if (!CurrentPlugins.Remove(pluginTypeName, out PluginHandle? handle))
        {
            return new UnloadResult(false, true, null);
        }

        string shadowFolder = handle.ShadowFolder;
        WeakReference weakContext = InitiateUnload(handle, settings.LogFile);
        handle = null;

        for (int attempt = 0; weakContext.IsAlive && attempt < 8; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        bool completed = !weakContext.IsAlive;
        if (completed)
        {
            TryDeleteShadowFolder(settings.ShadowRoot, shadowFolder, settings.LogFile);
        }
        else
        {
            HotReloadLog.Write(
                settings.LogFile,
                "Unload remains pending. Check modeless windows, Revit event handlers, timers and background threads.");
        }

        return new UnloadResult(true, completed, shadowFolder);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference InitiateUnload(PluginHandle handle, string logFile)
    {
        try
        {
            handle.Plugin.Shutdown();
        }
        catch (Exception exception)
        {
            HotReloadLog.Write(logFile, $"Plugin shutdown error: {exception}");
        }

        var weakContext = new WeakReference(handle.LoadContext, trackResurrection: true);
        handle.LoadContext.Unload();
        return weakContext;
    }

    private static void CopyDirectoryWithRetry(string source, string destination)
    {
        Exception? lastError = null;
        for (int attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                CopyDirectory(source, destination);
                return;
            }
            catch (IOException exception)
            {
                lastError = exception;
                Thread.Sleep(attempt * 100);
            }
        }

        throw new IOException(
            "Could not shadow-copy the plugin. Wait for the build to finish and try again.",
            lastError);
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (string file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        }

        foreach (string directory in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
    }

    private static void TryDeleteShadowFolder(string shadowRoot, string shadowFolder, string logFile)
    {
        try
        {
            string resolvedRoot = Path.GetFullPath(shadowRoot)
                .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string resolvedFolder = Path.GetFullPath(shadowFolder)
                .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

            if (!resolvedFolder.StartsWith(resolvedRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Refusing to delete outside the configured shadow root.");
            }

            Directory.Delete(shadowFolder, recursive: true);
        }
        catch (Exception exception)
        {
            HotReloadLog.Write(logFile, $"Could not remove old shadow folder: {exception.Message}");
        }
    }

    private sealed record PluginHandle(
        PluginLoadContext LoadContext,
        IHotReloadPlugin Plugin,
        string ShadowFolder,
        string ShadowAssemblyPath,
        string PluginTypeName);
}

internal sealed record UnloadResult(bool HadPlugin, bool Completed, string? ShadowFolder);
