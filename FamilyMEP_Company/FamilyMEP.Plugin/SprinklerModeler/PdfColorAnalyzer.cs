using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FamilyMEP.Plugin.SprinklerModeler;

internal sealed record PdfColorAnalysis(
    BitmapSource Overlay,
    IReadOnlyDictionary<int, Point> MarkerPositions);

internal static class PdfColorAnalyzer
{
    private const double OverlayWidth = 1000.0;

    internal static PdfColorAnalysis Analyze(BitmapSource source)
    {
        BitmapSource bitmap = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int width = bitmap.PixelWidth;
        int height = bitmap.PixelHeight;
        int stride = width * 4;
        byte[] pixels = new byte[stride * height];
        bitmap.CopyPixels(pixels, stride, 0);

        var blue = new bool[width * height];
        var magenta = new bool[width * height];
        var green = new bool[width * height];
        long blueX = 0, blueY = 0, blueCount = 0;
        long magentaX = 0, magentaY = 0, magentaCount = 0;
        long greenX = 0, greenY = 0, greenCount = 0;

        int mainY = 0;
        int mainStart = 0;
        int mainEnd = 0;
        int bestRun = 0;

        for (int y = 0; y < height; y++)
        {
            int runStart = -1;
            int gap = 0;
            for (int x = 0; x < width; x++)
            {
                int offset = y * stride + x * 4;
                byte b = pixels[offset];
                byte g = pixels[offset + 1];
                byte r = pixels[offset + 2];
                bool inDrawingRegion =
                    x >= width * 0.05 &&
                    x <= width * 0.74 &&
                    y >= height * 0.20 &&
                    y <= height * 0.82;
                bool isBlue = inDrawingRegion &&
                              b >= 145 && b > r + 45 && b > g + 25 && r < 130;
                bool isMagenta = inDrawingRegion &&
                                 r >= 145 && b >= 125 && r > g + 45 && b > g + 35;
                bool isGreen = inDrawingRegion &&
                               g >= 105 && g > r + 25 && g > b + 10 && r < 150;
                int index = y * width + x;
                blue[index] = isBlue;
                magenta[index] = isMagenta;
                green[index] = isGreen;

                if (isBlue)
                {
                    blueX += x;
                    blueY += y;
                    blueCount++;
                    if (runStart < 0) runStart = x;
                    gap = 0;
                }
                else if (runStart >= 0 && ++gap > 8)
                {
                    int end = x - gap;
                    int length = end - runStart + 1;
                    if (length > bestRun)
                    {
                        bestRun = length;
                        mainY = y;
                        mainStart = runStart;
                        mainEnd = end;
                    }
                    runStart = -1;
                    gap = 0;
                }

                if (isMagenta)
                {
                    magentaX += x;
                    magentaY += y;
                    magentaCount++;
                }
                if (isGreen)
                {
                    greenX += x;
                    greenY += y;
                    greenCount++;
                }
            }
        }

        byte[] overlayPixels = new byte[pixels.Length];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int index = y * width + x;
                if (magenta[index])
                    Paint(overlayPixels, stride, width, height, x, y, 118, 87, 232, 155);
                else if (blue[index])
                {
                    bool main = bestRun > width * 0.08 &&
                                Math.Abs(y - mainY) <= 3 &&
                                x >= mainStart - 5 &&
                                x <= mainEnd + 5;
                    if (main)
                        Paint(overlayPixels, stride, width, height, x, y, 243, 107, 91, 190);
                    else
                        Paint(overlayPixels, stride, width, height, x, y, 10, 166, 166, 140);
                }
                else if (green[index])
                    Paint(overlayPixels, stride, width, height, x, y, 54, 167, 109, 150);
            }
        }

        var overlay = BitmapSource.Create(
            width,
            height,
            bitmap.DpiX,
            bitmap.DpiY,
            PixelFormats.Pbgra32,
            null,
            overlayPixels,
            stride);
        overlay.Freeze();

        Point mainPoint = bestRun > 0
            ? new Point((mainStart + mainEnd) / 2.0, mainY)
            : Centroid(blueX, blueY, blueCount, width * 0.48, height * 0.64);
        Point sprinklerPoint = NearestMaskPoint(
            magenta,
            width,
            height,
            Centroid(
            magentaX,
            magentaY,
            magentaCount,
            width * 0.64,
            height * 0.30));
        Point branchPoint = NearestMaskPoint(
            blue,
            width,
            height,
            Centroid(
            blueX,
            blueY,
            blueCount,
            width * 0.58,
            height * 0.43));
        Point fittingPoint = FindIntersection(blue, width, height) ??
                             new Point(width * 0.38, height * 0.60);
        Point valvePoint = NearestMaskPoint(
            green,
            width,
            height,
            Centroid(
            greenX,
            greenY,
            greenCount,
            width * 0.43,
            height * 0.61));

        double scale = OverlayWidth / width;
        double overlayHeight = height * scale;
        var markers = new Dictionary<int, Point>
        {
            [1] = Normalize(mainPoint, scale, OverlayWidth, overlayHeight),
            [2] = Normalize(sprinklerPoint, scale, OverlayWidth, overlayHeight),
            [3] = Normalize(branchPoint, scale, OverlayWidth, overlayHeight),
            [4] = Normalize(fittingPoint, scale, OverlayWidth, overlayHeight),
            [5] = Normalize(valvePoint, scale, OverlayWidth, overlayHeight),
            [6] = new Point(OverlayWidth - 70, 40)
        };
        return new PdfColorAnalysis(overlay, markers);
    }

    private static void Paint(
        byte[] destination,
        int stride,
        int width,
        int height,
        int x,
        int y,
        byte red,
        byte green,
        byte blue,
        byte alpha)
    {
        for (int dy = -1; dy <= 1; dy++)
        {
            int py = y + dy;
            if (py < 0 || py >= height) continue;
            for (int dx = -1; dx <= 1; dx++)
            {
                int px = x + dx;
                if (px < 0 || px >= width) continue;
                int offset = py * stride + px * 4;
                if (destination[offset + 3] >= alpha) continue;
                destination[offset] = (byte)(blue * alpha / 255);
                destination[offset + 1] = (byte)(green * alpha / 255);
                destination[offset + 2] = (byte)(red * alpha / 255);
                destination[offset + 3] = alpha;
            }
        }
    }

    private static Point Centroid(
        long sumX,
        long sumY,
        long count,
        double fallbackX,
        double fallbackY) =>
        count > 0
            ? new Point((double)sumX / count, (double)sumY / count)
            : new Point(fallbackX, fallbackY);

    private static Point? FindIntersection(bool[] mask, int width, int height)
    {
        const int radius = 10;
        for (int y = radius; y < height - radius; y += 3)
        {
            for (int x = radius; x < width - radius; x += 3)
            {
                if (!HasMask(mask, width, x, y, 2)) continue;
                bool horizontal = HasMask(mask, width, x - radius, y, 3) &&
                                  HasMask(mask, width, x + radius, y, 3);
                bool vertical = HasMask(mask, width, x, y - radius, 3) &&
                                HasMask(mask, width, x, y + radius, 3);
                if (horizontal && vertical)
                    return new Point(x, y);
            }
        }
        return null;
    }

    private static Point NearestMaskPoint(
        bool[] mask,
        int width,
        int height,
        Point target)
    {
        Point best = target;
        double bestDistance = double.MaxValue;
        for (int y = 0; y < height; y += 2)
        {
            for (int x = 0; x < width; x += 2)
            {
                if (!HasMask(mask, width, x, y, 1)) continue;
                double dx = x - target.X;
                double dy = y - target.Y;
                double distance = dx * dx + dy * dy;
                if (distance >= bestDistance) continue;
                bestDistance = distance;
                best = new Point(x, y);
            }
        }
        return best;
    }

    private static bool HasMask(bool[] mask, int width, int x, int y, int radius)
    {
        int height = mask.Length / width;
        for (int py = Math.Max(0, y - radius); py <= Math.Min(height - 1, y + radius); py++)
        {
            for (int px = Math.Max(0, x - radius); px <= Math.Min(width - 1, x + radius); px++)
            {
                if (mask[py * width + px]) return true;
            }
        }
        return false;
    }

    private static Point Normalize(Point point, double scale, double width, double height) =>
        new(
            Math.Max(18, Math.Min(width - 18, point.X * scale)),
            Math.Max(18, Math.Min(height - 18, point.Y * scale)));
}
