using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media.Imaging;

namespace FamilyMEP.Plugin.SprinklerModeler;

internal sealed record RenderedPdfPage(BitmapSource Bitmap, uint PageCount);

internal static class PdfPageRenderer
{
    internal static async Task<RenderedPdfPage> RenderFirstPageAsync(
        string pdfPath,
        uint targetWidth = 2200)
    {
        string renderer = ResolveRendererPath();
        string cachePath = ResolveCachePath(pdfPath, targetWidth);
        string? cacheFolder = Path.GetDirectoryName(cachePath);
        if (cacheFolder is not null)
            Directory.CreateDirectory(cacheFolder);

        var startInfo = new ProcessStartInfo
        {
            FileName = renderer,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add(pdfPath);
        startInfo.ArgumentList.Add(cachePath);
        startInfo.ArgumentList.Add(targetWidth.ToString());

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The FamilyMEP PDF renderer could not be started.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        string standardOutput = await process.StandardOutput.ReadToEndAsync(timeout.Token);
        string standardError = await process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        if (process.ExitCode != 0 || !File.Exists(cachePath))
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(standardError)
                    ? $"PDF renderer exited with code {process.ExitCode}."
                    : standardError.Trim());

        uint pageCount = ParsePageCount(standardOutput);
        BitmapSource bitmap = LoadBitmap(cachePath);
        return new RenderedPdfPage(bitmap, pageCount);
    }

    private static string ResolveRendererPath()
    {
        string root = Path.GetDirectoryName(typeof(PdfPageRenderer).Assembly.Location)
            ?? throw new InvalidOperationException("The FamilyMEP plugin folder is unavailable.");
        string path = Path.Combine(root, "Tools", "PdfRenderer", "FamilyMEP.PdfRenderer.exe");
        if (!File.Exists(path))
            throw new FileNotFoundException("FamilyMEP PDF renderer is missing.", path);
        return path;
    }

    private static string ResolveCachePath(string pdfPath, uint width)
    {
        FileInfo source = new(pdfPath);
        string fingerprint =
            $"{source.FullName.ToUpperInvariant()}|{source.Length}|{source.LastWriteTimeUtc.Ticks}|{width}";
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint)))[..20];
        string folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FamilyMEP",
            "Spinkler",
            "PdfCache");
        return Path.Combine(folder, $"{key}.page1.png");
    }

    private static BitmapSource LoadBitmap(string path)
    {
        using FileStream stream = File.OpenRead(path);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private static uint ParsePageCount(string output)
    {
        foreach (string line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("pages=", StringComparison.OrdinalIgnoreCase) &&
                uint.TryParse(line.AsSpan(6), out uint pages))
                return pages;
        }
        return 1;
    }
}
