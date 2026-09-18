global using System.IO;

using System.Reflection;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Autodesk.Revit.UI;

namespace FamilyMEP.Ribbon;

internal sealed class DrainConnectionRibbonAnimator : IDisposable
{
    private const int FrameCount = 14;
    private const int VariantCount = 4;
    private const string ResourcePrefix = "FamilyMEP.RibbonIcons.DrainConnection";

    private readonly PushButton _button;
    private readonly IReadOnlyList<IReadOnlyList<ImageSource>> _smallVariants;
    private readonly IReadOnlyList<IReadOnlyList<ImageSource>> _largeVariants;
    private readonly DispatcherTimer _timer;
    private readonly Random _random;
    private int _variantIndex;
    private int _frameIndex;
    private bool _disposed;

    private DrainConnectionRibbonAnimator(
        PushButton button,
        IReadOnlyList<IReadOnlyList<ImageSource>> smallVariants,
        IReadOnlyList<IReadOnlyList<ImageSource>> largeVariants)
    {
        _button = button;
        _smallVariants = smallVariants;
        _largeVariants = largeVariants;
        _random = new Random(unchecked(Environment.TickCount * 397 ^ GetHashCode()));
        _variantIndex = _random.Next(VariantCount);
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            // Revit does not animate GIF files on ribbon buttons. A short PNG
            // sequence provides a smooth clockwise reveal with predictable memory use.
            Interval = TimeSpan.FromMilliseconds(110)
        };
        _timer.Tick += OnTick;

        ApplyFrame(0);
        _timer.Start();
    }

    public static DrainConnectionRibbonAnimator? TryStart(PushButton button, Assembly resourceAssembly)
    {
        try
        {
            var smallVariants = LoadVariants(resourceAssembly, 16);
            var largeVariants = LoadVariants(resourceAssembly, 32);
            return new DrainConnectionRibbonAnimator(button, smallVariants, largeVariants);
        }
        catch
        {
            // An icon must never prevent the Revit add-in from loading. If an
            // installation is missing an embedded frame, retain the text button.
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTick;
    }

    public void ShuffleVariant()
    {
        if (_disposed || VariantCount < 2)
        {
            return;
        }

        // Draw from one fewer option and skip over the current index. This
        // guarantees that every command run visibly changes the icon.
        var next = _random.Next(VariantCount - 1);
        if (next >= _variantIndex)
        {
            next++;
        }

        _variantIndex = next;
        _frameIndex = 0;
        ApplyFrame(_frameIndex);
    }

    private static IReadOnlyList<IReadOnlyList<ImageSource>> LoadVariants(
        Assembly assembly,
        int size)
    {
        var variants = new List<IReadOnlyList<ImageSource>>(VariantCount);
        for (var variant = 0; variant < VariantCount; variant++)
        {
            var frames = new List<ImageSource>(FrameCount);
            for (var index = 0; index < FrameCount; index++)
            {
                var resourceName =
                    $"{ResourcePrefix}.drain-connection-v{variant:00}-{index:00}-{size}.png";
                using Stream stream = assembly.GetManifestResourceStream(resourceName)
                    ?? throw new InvalidOperationException($"Ribbon icon resource was not found: {resourceName}");

                var decoder = BitmapDecoder.Create(
                    stream,
                    BitmapCreateOptions.PreservePixelFormat,
                    BitmapCacheOption.OnLoad);
                BitmapFrame frame = decoder.Frames[0];
                frame.Freeze();
                frames.Add(frame);
            }

            variants.Add(frames);
        }

        return variants;
    }

    private void OnTick(object? sender, EventArgs eventArgs)
    {
        if (_disposed)
        {
            return;
        }

        _frameIndex = (_frameIndex + 1) % FrameCount;
        ApplyFrame(_frameIndex);
    }

    private void ApplyFrame(int index)
    {
        _button.Image = _smallVariants[_variantIndex][index];
        _button.LargeImage = _largeVariants[_variantIndex][index];
    }
}
