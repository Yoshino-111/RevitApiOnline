namespace RevitHotLoader2025;

internal static class HotReloadLog
{
    public static void Write(string logFile, string message)
    {
        try
        {
            string? folder = Path.GetDirectoryName(logFile);
            if (!string.IsNullOrWhiteSpace(folder))
            {
                Directory.CreateDirectory(folder);
            }

            File.AppendAllText(
                logFile,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never stop a Revit command.
        }
    }
}

