param(
    [string]$OutputDirectory = $PSScriptRoot
)

Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName WindowsBase

$frameCount = 14
$sizes = @(16, 32)
$transparent = [System.Windows.Media.Brushes]::Transparent
$backgroundBrush = [System.Windows.Media.SolidColorBrush]::new(
    [System.Windows.Media.Color]::FromArgb(232, 20, 31, 48))
$mainBrush = [System.Windows.Media.SolidColorBrush]::new(
    [System.Windows.Media.Color]::FromRgb(255, 112, 101))
$branchBrush = [System.Windows.Media.SolidColorBrush]::new(
    [System.Windows.Media.Color]::FromRgb(52, 211, 190))
$headBrush = [System.Windows.Media.SolidColorBrush]::new(
    [System.Windows.Media.Color]::FromRgb(178, 132, 255))
$pulseBrush = [System.Windows.Media.SolidColorBrush]::new(
    [System.Windows.Media.Color]::FromRgb(255, 213, 79))
$pulseGlowBrush = [System.Windows.Media.SolidColorBrush]::new(
    [System.Windows.Media.Color]::FromArgb(82, 255, 213, 79))

foreach ($brush in @($backgroundBrush, $mainBrush, $branchBrush, $headBrush, $pulseBrush, $pulseGlowBrush)) {
    $brush.Freeze()
}

function Get-RoutePoint {
    param(
        [double]$Progress,
        [double]$Size
    )

    $points = @(
        [System.Windows.Point]::new(0.30 * $Size, 0.22 * $Size),
        [System.Windows.Point]::new(0.30 * $Size, 0.49 * $Size),
        [System.Windows.Point]::new(0.70 * $Size, 0.49 * $Size),
        [System.Windows.Point]::new(0.70 * $Size, 0.69 * $Size)
    )

    $lengths = @()
    $totalLength = 0.0
    for ($index = 0; $index -lt $points.Count - 1; $index++) {
        $delta = $points[$index + 1] - $points[$index]
        $length = $delta.Length
        $lengths += $length
        $totalLength += $length
    }

    $distance = $Progress * $totalLength
    for ($index = 0; $index -lt $lengths.Count; $index++) {
        if ($distance -le $lengths[$index]) {
            $ratio = if ($lengths[$index] -eq 0) { 0 } else { $distance / $lengths[$index] }
            return [System.Windows.Point]::new(
                $points[$index].X + (($points[$index + 1].X - $points[$index].X) * $ratio),
                $points[$index].Y + (($points[$index + 1].Y - $points[$index].Y) * $ratio))
        }

        $distance -= $lengths[$index]
    }

    return $points[-1]
}

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

foreach ($size in $sizes) {
    $scale = $size / 32.0
    for ($frame = 0; $frame -lt $frameCount; $frame++) {
        $visual = [System.Windows.Media.DrawingVisual]::new()
        $drawing = $visual.RenderOpen()

        $drawing.DrawRoundedRectangle(
            $backgroundBrush,
            $null,
            [System.Windows.Rect]::new(1.0 * $scale, 1.0 * $scale, 30.0 * $scale, 30.0 * $scale),
            7.0 * $scale,
            7.0 * $scale)

        $mainPen = [System.Windows.Media.Pen]::new($mainBrush, 3.1 * $scale)
        $branchPen = [System.Windows.Media.Pen]::new($branchBrush, 3.1 * $scale)
        $headPen = [System.Windows.Media.Pen]::new($headBrush, 2.0 * $scale)
        foreach ($pen in @($mainPen, $branchPen, $headPen)) {
            $pen.StartLineCap = [System.Windows.Media.PenLineCap]::Round
            $pen.EndLineCap = [System.Windows.Media.PenLineCap]::Round
            $pen.LineJoin = [System.Windows.Media.PenLineJoin]::Round
            $pen.Freeze()
        }

        $drawing.DrawLine($mainPen,
            [System.Windows.Point]::new(4.0 * $scale, 7.0 * $scale),
            [System.Windows.Point]::new(28.0 * $scale, 7.0 * $scale))

        $route = [System.Windows.Media.StreamGeometry]::new()
        $routeContext = $route.Open()
        $routeContext.BeginFigure([System.Windows.Point]::new(9.6 * $scale, 7.0 * $scale), $false, $false)
        $routeContext.LineTo([System.Windows.Point]::new(9.6 * $scale, 15.7 * $scale), $true, $false)
        $routeContext.LineTo([System.Windows.Point]::new(22.4 * $scale, 15.7 * $scale), $true, $false)
        $routeContext.LineTo([System.Windows.Point]::new(22.4 * $scale, 22.0 * $scale), $true, $false)
        $routeContext.Close()
        $route.Freeze()
        $drawing.DrawGeometry($null, $branchPen, $route)

        $drawing.DrawEllipse($mainBrush, $null,
            [System.Windows.Point]::new(9.6 * $scale, 7.0 * $scale),
            2.35 * $scale, 2.35 * $scale)
        $drawing.DrawEllipse($headBrush, $null,
            [System.Windows.Point]::new(22.4 * $scale, 23.0 * $scale),
            2.5 * $scale, 2.5 * $scale)
        $drawing.DrawLine($headPen,
            [System.Windows.Point]::new(18.3 * $scale, 27.0 * $scale),
            [System.Windows.Point]::new(26.5 * $scale, 27.0 * $scale))
        $drawing.DrawLine($headPen,
            [System.Windows.Point]::new(19.7 * $scale, 29.0 * $scale),
            [System.Windows.Point]::new(25.1 * $scale, 29.0 * $scale))

        $progress = $frame / [double]($frameCount - 1)
        $pulsePoint = Get-RoutePoint -Progress $progress -Size $size
        $drawing.DrawEllipse($pulseGlowBrush, $null, $pulsePoint, 3.7 * $scale, 3.7 * $scale)
        $drawing.DrawEllipse($pulseBrush, $null, $pulsePoint, 1.65 * $scale, 1.65 * $scale)
        $drawing.Close()

        $bitmap = [System.Windows.Media.Imaging.RenderTargetBitmap]::new(
            $size, $size, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
        $bitmap.Render($visual)
        $bitmap.Freeze()

        $encoder = [System.Windows.Media.Imaging.PngBitmapEncoder]::new()
        $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
        $fileName = "sprinkler-modeler-{0:D2}-{1}.png" -f $frame, $size
        $filePath = Join-Path $OutputDirectory $fileName
        $stream = [System.IO.File]::Open($filePath, [System.IO.FileMode]::Create)
        try {
            $encoder.Save($stream)
        }
        finally {
            $stream.Dispose()
        }
    }
}

Write-Output "Generated $($frameCount * $sizes.Count) Sprinkler Modeler ribbon frames in $OutputDirectory"
