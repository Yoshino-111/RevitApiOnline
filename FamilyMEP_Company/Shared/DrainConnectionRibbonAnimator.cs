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
    private const string ResourcePrefix = "FamilyMEP.RibbonIcons.DrainConnection";

    private readonly PushButton _button;
    private readonly IReadOnlyList<ImageSource> _smallFrames;
    private readonly IReadOnlyList<ImageSource> _largeFrames;
    private readonly DispatcherTimer _timer;
    private int _frameIndex;
    private bool _disposed;

    private DrainConnectionRibbonAnimator(
        PushButton button,
        IReadOnlyList<ImageSource> smallFrames,
        IReadOnlyList<ImageSource> largeFrames)
    {
        _button = button;
        _smallFrames = smallFrames;
        _largeFrames = largeFrames;
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
            var smallFrames = LoadFrames(resourceAssembly, 16);
            var largeFrames = LoadFrames(resourceAssembly, 32);
            return new DrainConnectionRibbonAnimator(button, smallFrames, largeFrames);
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

    private static IReadOnlyList<ImageSource> LoadFrames(Assembly assembly, int size)
    {
        var frames = new List<ImageSource>(FrameCount);
        for (var index = 0; index < FrameCount; index++)
        {
            var resourceName =
                $"{ResourcePrefix}.drain-connection-{index:00}-{size}.png";
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

        return frames;
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
        _button.Image = _smallFrames[index];
        _button.LargeImage = _largeFrames[index];
    }
}
