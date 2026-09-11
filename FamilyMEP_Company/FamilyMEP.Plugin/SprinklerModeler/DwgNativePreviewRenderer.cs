using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FamilyMEP.Plugin.SprinklerModeler;

internal static class DwgNativePreviewRenderer
{
    internal static bool TryRender(string? dwgPath, out BitmapSource? preview, out string message)
    {
        preview = null;
        message = string.Empty;
        if (string.IsNullOrWhiteSpace(dwgPath) || !File.Exists(dwgPath) ||
            !string.Equals(Path.GetExtension(dwgPath), ".dwg", StringComparison.OrdinalIgnoreCase))
        {
            message = "Native color preview only supports an existing DWG file.";
            return false;
        }

        string? consolePath = FindAutoCadCoreConsole();
        if (consolePath is null)
        {
            message = "AutoCAD Core Console was not found; using the Revit CAD preview.";
            return false;
        }

        string workFolder = Path.Combine(Path.GetTempPath(), "FamilyMEP", "DwgPreview");
        Directory.CreateDirectory(workFolder);
        string token = Guid.NewGuid().ToString("N");
        string scriptBasePath = Path.Combine(workFolder, token);
        string scriptPath = scriptBasePath + ".scr";
        string outputPath = Path.Combine(workFolder, token + ".png");
        try
        {
            string scriptOutputPath = outputPath.Replace('\\', '/');
            File.WriteAllText(
                scriptPath,
                string.Join(
                    Environment.NewLine,
                    "_.FILEDIA",
                    "0",
                    "_.CMDECHO",
                    "0",
                    "_.ZOOM",
                    "_E",
                    "_.PNGOUT",
                    $"\"{scriptOutputPath}\"",
                    "_ALL",
                    string.Empty,
                    "_.QUIT",
                    "_Y",
                    string.Empty));

            var startInfo = new ProcessStartInfo
            {
                FileName = consolePath,
                Arguments = $"/i \"{dwgPath}\" /s \"{scriptBasePath}\" /l en-US",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            using Process process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("AutoCAD Core Console did not start.");
            if (!process.WaitForExit(45_000))
            {
                try { process.Kill(); }
                catch { }
                message = "AutoCAD color preview timed out; using the Revit CAD preview.";
                return false;
            }
            if (!File.Exists(outputPath) || new FileInfo(outputPath).Length < 256)
            {
                message = "AutoCAD did not return a preview image; using the Revit CAD preview.";
                return false;
            }

            using FileStream input = File.OpenRead(outputPath);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = input;
            bitmap.EndInit();
            bitmap.Freeze();
            preview = CropToCadExtents(bitmap, out bool cropped);
            message = cropped
                ? "AutoCAD native colors loaded and aligned to CAD extents."
                : "AutoCAD native colors loaded.";
            return true;
        }
        catch (Exception exception)
        {
            message = $"AutoCAD color preview failed: {exception.Message}";
            return false;
        }
        finally
        {
            TryDelete(scriptPath);
            TryDelete(outputPath);
        }
    }

    private static BitmapSource CropToCadExtents(BitmapSource source, out bool cropped)
    {
        cropped = false;
        BitmapSource bitmap = source;
        if (bitmap.Format != PixelFormats.Bgra32)
        {
            var converted = new FormatConvertedBitmap();
            converted.BeginInit();
            converted.Source = bitmap;
            converted.DestinationFormat = PixelFormats.Bgra32;
            converted.EndInit();
            converted.Freeze();
            bitmap = converted;
        }

        int width = bitmap.PixelWidth;
        int height = bitmap.PixelHeight;
        if (width < 40 || height < 40) return source;
        int stride = width * 4;
        byte[] pixels = new byte[stride * height];
        bitmap.CopyPixels(pixels, stride, 0);
        (double B, double G, double R) background = CornerBackground(pixels, width, height, stride);
        int minX = width;
        int minY = height;
        int maxX = -1;
        int maxY = -1;
        const double thresholdSquared = 20 * 20;
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int offset = y * stride + x * 4;
            double db = pixels[offset] - background.B;
            double dg = pixels[offset + 1] - background.G;
            double dr = pixels[offset + 2] - background.R;
            if (db * db + dg * dg + dr * dr < thresholdSquared) continue;
            minX = Math.Min(minX, x);
            minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x);
            maxY = Math.Max(maxY, y);
        }
        if (maxX < minX || maxY < minY) return source;
        minX = Math.Max(0, minX - 1);
        minY = Math.Max(0, minY - 1);
        maxX = Math.Min(width - 1, maxX + 1);
        maxY = Math.Min(height - 1, maxY + 1);
        int cropWidth = maxX - minX + 1;
        int cropHeight = maxY - minY + 1;
        if (cropWidth < width * 0.10 || cropHeight < height * 0.10 ||
            (cropWidth >= width * 0.985 && cropHeight >= height * 0.985))
            return source;

        var result = new CroppedBitmap(bitmap, new Int32Rect(minX, minY, cropWidth, cropHeight));
        result.Freeze();
        cropped = true;
        return result;
    }

    private static (double B, double G, double R) CornerBackground(
        byte[] pixels,
        int width,
        int height,
        int stride)
    {
        int insetX = Math.Max(1, width / 100);
        int insetY = Math.Max(1, height / 100);
        (int X, int Y)[] points =
        [
            (insetX, insetY),
            (width - 1 - insetX, insetY),
            (insetX, height - 1 - insetY),
            (width - 1 - insetX, height - 1 - insetY)
        ];
        double blue = 0;
        double green = 0;
        double red = 0;
        foreach ((int x, int y) in points)
        {
            int offset = y * stride + x * 4;
            blue += pixels[offset];
            green += pixels[offset + 1];
            red += pixels[offset + 2];
        }
        return (blue / points.Length, green / points.Length, red / points.Length);
    }

    private static string? FindAutoCadCoreConsole()
    {
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string autodesk = Path.Combine(programFiles, "Autodesk");
        if (!Directory.Exists(autodesk)) return null;
        return Directory.EnumerateDirectories(autodesk, "AutoCAD *", SearchOption.TopDirectoryOnly)
            .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(path => Path.Combine(path, "accoreconsole.exe"))
            .FirstOrDefault(File.Exists);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch { }
    }
}
