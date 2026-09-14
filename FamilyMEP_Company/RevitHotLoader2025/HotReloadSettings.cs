using System.Reflection;
using System.Text.Json;

namespace RevitHotLoader2025;

internal sealed class HotReloadSettings
{
    public string PluginAssemblyPath { get; init; } = string.Empty;

    public string PluginTypeName { get; init; } = string.Empty;

    public string ShadowRoot { get; init; } = string.Empty;

    public string LogFile { get; init; } = string.Empty;

    public static HotReloadSettings Load()
    {
        string loaderFolder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)
            ?? throw new InvalidOperationException("Cannot determine the hot-loader directory.");
        string configPath = Path.Combine(loaderFolder, "hotloader.json");

        if (!File.Exists(configPath))
        {
            throw new FileNotFoundException("Hot-loader configuration was not found.", configPath);
        }

        string json = File.ReadAllText(configPath);
        HotReloadSettings settings = JsonSerializer.Deserialize<HotReloadSettings>(
            json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("The hot-loader configuration is empty.");

        return settings.Validate(loaderFolder);
    }

    private HotReloadSettings Validate(string loaderFolder)
    {
        string pluginPath = ResolvePath(loaderFolder, PluginAssemblyPath);
        string shadowRoot = ResolvePath(loaderFolder, ShadowRoot);
        string logFile = ResolvePath(loaderFolder, LogFile);

        if (string.IsNullOrWhiteSpace(PluginTypeName))
        {
            throw new InvalidOperationException("PluginTypeName is required in hotloader.json.");
        }

        return new HotReloadSettings
        {
            PluginAssemblyPath = pluginPath,
            PluginTypeName = PluginTypeName,
            ShadowRoot = shadowRoot,
            LogFile = logFile,
        };
    }

    private static string ResolvePath(string baseFolder, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException("A required path is missing in hotloader.json.");
        }

        return Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(baseFolder, value));
    }
}

