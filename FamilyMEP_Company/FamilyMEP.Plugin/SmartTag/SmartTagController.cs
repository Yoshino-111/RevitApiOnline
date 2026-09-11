using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Xml;
using System.Runtime.InteropServices;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using FamilyMEP.Plugin.Infrastructure;
using ExternalEvent = Autodesk.Revit.UI.ExternalEvent;
using WpfImage = System.Windows.Controls.Image;
using WpfComboBox = System.Windows.Controls.ComboBox;
using WpfTextBox = System.Windows.Controls.TextBox;
using WpfLine = System.Windows.Shapes.Line;
using WpfEllipse = System.Windows.Shapes.Ellipse;
using WpfColor = System.Windows.Media.Color;
using WpfPoint = System.Windows.Point;
using WpfRectangle = System.Windows.Shapes.Rectangle;
using WpfThumb = System.Windows.Controls.Primitives.Thumb;
using IoPath = System.IO.Path;

namespace FamilyMEP.Plugin.SmartTag;

internal sealed class SmartTagController : IDisposable
{
    private static readonly string DuctCategoryKey =
        ((long)BuiltInCategory.OST_DuctCurves).ToString();
    private static readonly HashSet<string> DuctCompanionCategoryKeys =
    [
        ((long)BuiltInCategory.OST_DuctTerminal).ToString(),
        ((long)BuiltInCategory.OST_DuctAccessory).ToString()
    ];

    private readonly UIApplication _uiApplication;
    private readonly Document _document;
    private readonly RevitRequestHandler _handler;
    private readonly ExternalEvent _externalEvent;
    private readonly Window _window;
    private readonly Window _quickAlignWindow;
    private readonly DispatcherTimer _previewTimer;
    private readonly DispatcherTimer _captureTimer;
    private SmartTagViewSnapshot? _snapshot;
    private SmartTagLayoutResult? _layout;
    private bool _busy;
    private bool _disposed;
    private bool _quickAlignReady;
    private bool _smartTagCompactMode;
    private bool _handlingSmartTagStateChange;
    private bool _suppressPreview;
    private bool _captureScheduled;
    private bool _analysisReady;
    private string? _nearHostWriteWarning;
    private long _documentRevision;
    private long _analyzedRevision = -1;
    private SmartTagLayoutSettings? _analyzedSettings;
    private Dictionary<string, long>? _analyzedTypes;
    private LayoutRect? _guidedZone;
    private bool _guidedZoneIsSuggested;
    private LayoutRect? _ductDensitySampleZone;
    private long _ductDensitySampleViewId = -1;
    private readonly Dictionary<string, CheckBox> _categoryChecks =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, WpfComboBox> _categoryTypeCombos =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _categorySelectionOrder = [];

    private Canvas Overlay => Find<Canvas>("overlay_canvas");
    private FrameworkElement PreviewSurface => Find<FrameworkElement>("preview_surface");
    private WpfImage PreviewImage => Find<WpfImage>("preview_image");
    private ScaleTransform PreviewScale =>
        _window.FindName("preview_scale") as ScaleTransform
        ?? throw new InvalidOperationException("UI transform 'preview_scale' was not found.");
    private TranslateTransform PreviewTranslate =>
        _window.FindName("preview_translate") as TranslateTransform
        ?? throw new InvalidOperationException("UI transform 'preview_translate' was not found.");
    private TextBlock Status => Find<TextBlock>("status_text");
    private TextBlock PreviewMode => Find<TextBlock>("preview_mode_text");
    private TextBlock AnalysisSummary => Find<TextBlock>("analysis_summary_text");
    private StackPanel CategoryList => Find<StackPanel>("category_list");
    private Button PreviewButton => Find<Button>("apply_btn");
    private Button WriteButton => Find<Button>("write_btn");

    public SmartTagController(
        UIApplication uiApplication,
        RevitRequestHandler handler,
        ExternalEvent externalEvent)
    {
        _uiApplication = uiApplication;
        _document = uiApplication.ActiveUIDocument?.Document
            ?? throw new InvalidOperationException("Open a Revit project first.");
        _handler = handler;
        _externalEvent = externalEvent;
        _window = LoadWindow("SmartTagWindow.xaml");
        _quickAlignWindow = LoadWindow("SmartTagQuickAlignWindow.xaml");
        new WindowInteropHelper(_window).Owner = uiApplication.MainWindowHandle;
        _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(550) };
        _previewTimer.Tick += (_, _) =>
        {
            _previewTimer.Stop();
            RequestPreview();
        };
        _captureTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(350)
        };
        _captureTimer.Tick += (_, _) =>
        {
            _captureTimer.Stop();
            _captureScheduled = false;
            CaptureFromRevit();
        };
        WireEvents();
        _uiApplication.Application.DocumentChanged += OnDocumentChanged;
    }

    public void Show()
    {
        _smartTagCompactMode = false;
        _window.Show();
        _quickAlignReady = true;
        SyncQuickAlignVisibility();
    }

    private void OnDocumentChanged(object? sender, Autodesk.Revit.DB.Events.DocumentChangedEventArgs args)
    {
        if (args.GetDocument() != _document) return;
        _documentRevision++;
        bool hasAnalyzedExactPlan = _analysisReady &&
            _analyzedSettings?.LayoutStyle is
                SmartTagLayoutStyle.StandardNearHost or SmartTagLayoutStyle.GuidedZones;
        if (!_busy && !_disposed && !hasAnalyzedExactPlan)
        {
            _analysisReady = false;
            WriteButton.IsEnabled = false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _uiApplication.Application.DocumentChanged -= OnDocumentChanged;
        _previewTimer.Stop();
        _captureTimer.Stop();
        try { _quickAlignWindow.Close(); } catch { }
        try { _window.Close(); } catch { }
    }

    private void WireEvents()
    {
        _window.Closed += (_, _) =>
        {
            _uiApplication.Application.DocumentChanged -= OnDocumentChanged;
            _previewTimer.Stop();
            _captureTimer.Stop();
            _disposed = true;
            _quickAlignReady = false;
            try { _quickAlignWindow.Close(); } catch { }
        };
        _window.IsVisibleChanged += (_, _) => SyncQuickAlignVisibility();
        _window.LocationChanged += (_, _) => PositionQuickAlignWindow();
        _window.SizeChanged += (_, _) => PositionQuickAlignWindow();
        _window.StateChanged += (_, _) => HandleSmartTagWindowStateChanged();
        _window.Loaded += (_, _) =>
        {
            SyncQuickAlignVisibility();
            PositionQuickAlignWindow();
            RefreshFromRevit();
        };
        _quickAlignWindow.Loaded += (_, _) => PositionQuickAlignWindow();
        Find<Button>("smart_dim_btn").Click += (_, _) => CreateSmartDimensions();
        Find<Button>("pick_elements_btn").Click += (_, _) => PickElementsFromRevit();
        Find<Button>("pick_duct_density_zone_btn").Click += (_, _) => PickDuctDensitySampleZone();
        Find<Button>("suggest_tag_zone_btn").Click += (_, _) => SuggestGuidedTagZone(announce: true);
        Find<Button>("pick_tag_zone_btn").Click += (_, _) => PickGuidedTagZone();
        FindQuick<Button>("quick_align_tags_btn").Click += (_, _) => AlignPickedTags();
        FindQuick<Button>("quick_zone_tags_btn").Click += (_, _) => ArrangeTagsInPickedZone();
        FindQuick<Button>("quick_auto_column_btn").Click += (_, _) => ArrangeTagsAroundReference();
        FindQuick<Button>("quick_restore_smart_tag_btn").Click += (_, _) => RestoreSmartTagWindow();
        FindQuick<WpfThumb>("quick_drag_handle").DragDelta += (_, args) =>
            MoveQuickAlignWindow(args.HorizontalChange, args.VerticalChange);
        WpfComboBox quickAlignEdge = FindQuick<WpfComboBox>("quick_align_edge_combo");
        quickAlignEdge.SelectionChanged += (_, _) =>
            UpdateManualTagActionUi();
        UpdateManualTagActionUi();
        Find<Button>("refresh_btn").Click += (_, _) => RefreshFromRevit();
        Find<Button>("reset_btn").Click += (_, _) => ResetSettings();
        Find<Button>("cancel_btn").Click += (_, _) => _window.Close();
        PreviewButton.Click += (_, _) => PreviewTags(recomputeLayout: true);
        WriteButton.Click += (_, _) => ApplyToView();
        PreviewSurface.SizeChanged += (_, _) => RenderOverlay();
        PreviewSurface.MouseWheel += (_, args) => ZoomPreview(args);

        foreach (string name in new[]
        {
            "live_preview_cb", "avoid_elements_cb", "avoid_leaders_cb",
            "avoid_text_cb", "lock_upper_cb", "move_lower_cb"
        })
        {
            CheckBox checkBox = Find<CheckBox>(name);
            checkBox.Checked += (_, _) => RequestPreview();
            checkBox.Unchecked += (_, _) => RequestPreview();
        }
        Find<WpfComboBox>("layout_style_combo").SelectionChanged += (_, _) =>
        {
            UpdateGuidedZoneUi();
            if (ReadLayoutStyle() == SmartTagLayoutStyle.GuidedZones && _guidedZone is null)
            {
                SuggestGuidedTagZone(announce: false);
            }
            RequestPreview();
        };
        Find<WpfComboBox>("side_combo").SelectionChanged += (_, _) =>
        {
            if (ReadLayoutStyle() == SmartTagLayoutStyle.GuidedZones && _guidedZoneIsSuggested)
            {
                SuggestGuidedTagZone(announce: false);
            }
            RequestPreview();
        };
        foreach (string name in new[]
        {
            "column_width_tb", "offset_tb", "row_spacing_tb", "top_margin_tb", "clearance_tb"
        })
        {
            Find<WpfTextBox>(name).TextChanged += (_, _) => RequestPreview();
        }
    }

    private void RefreshFromRevit()
    {
        if (_busy || _captureScheduled || _disposed) return;
        _captureScheduled = true;
        WriteButton.IsEnabled = false;
        Status.Text = "Preparing a clean Active View capture...";
        _window.Hide();
        NativeWindow.TryActivate(_uiApplication.MainWindowHandle);
        _captureTimer.Stop();
        _captureTimer.Start();
    }

    private void CaptureFromRevit()
    {
        if (_busy || _disposed) return;
        SmartTagViewSnapshot? captured = null;
        bool fastGuidedCapture = ReadLayoutStyle() == SmartTagLayoutStyle.GuidedZones;
        var captureClock = System.Diagnostics.Stopwatch.StartNew();
        NativeWindow.TryActivate(_uiApplication.MainWindowHandle);
        Queue(
            app => captured = SmartTagRevitService.CaptureActiveView(
                app,
                fastGuidedCapture: fastGuidedCapture),
            fastGuidedCapture
                ? "Refreshing Active View with the Guided MEP-only collector..."
                : "Capturing Active View and reading MEP tags...",
            () =>
            {
                HashSet<string> selectedBeforeRefresh = SelectedGroups();
                Dictionary<string, long> selectedTypesBeforeRefresh = SelectedTagTypeIds();
                _snapshot = captured ?? throw new InvalidOperationException("Active View capture returned no data.");
                _guidedZone = null;
                _guidedZoneIsSuggested = false;
                UpdateGuidedZoneUi();
                UpdateDuctDensityUi();
                int totalElements = _snapshot.Tags.Select(item => item.ElementId).Distinct().Count();
                int existing = _snapshot.Tags.Count(item => !item.WillCreate);
                int missing = _snapshot.Tags.Count(item => item.WillCreate);
                int architectureObstacles = _snapshot.Obstacles.Count(item =>
                    item.Kind == LayoutObstacleKind.Architecture);
                Find<TextBlock>("view_name_text").Text =
                    $"{_snapshot.ViewName}  |  {totalElements} visible MEP elements  |  " +
                    $"{architectureObstacles} architecture obstacle(s)  |  " +
                    $"{existing} existing tag(s)  |  {missing} untagged candidate(s) total";
                PopulateCategories(
                    _snapshot.Categories,
                    selectedBeforeRefresh,
                    selectedTypesBeforeRefresh);
                if (ReadLayoutStyle() == SmartTagLayoutStyle.GuidedZones)
                {
                    SuggestGuidedTagZone(announce: false);
                }
                PreviewImage.Source = LoadBitmap(_snapshot.ImageBytes);
                PreviewMode.Text = "ACTIVE VIEW — select categories to render project tags";
                ResetPreviewZoom();
                if (!_window.IsVisible) _window.Show();
                _window.Activate();
                RequestPreview();
                Status.Text += $" Refresh completed in {captureClock.Elapsed.TotalSeconds:0.0}s.";
            });
    }

    private void CreateSmartDimensions()
    {
        if (_busy || _disposed) return;

        HashSet<string> selectedGroups = SelectedGroups();
        if (selectedGroups.Count == 0)
        {
            Status.Text = "Select one or more categories above before running Smart Dim.";
            Status.Foreground = Brush("#FFCC80");
            return;
        }

        SmartDimension.SmartDimensionResult? result = null;
        double maximumElementGapMillimeters;
        try
        {
            maximumElementGapMillimeters = ReadPositive(
                "dim_cluster_gap_tb",
                "Smart Dim maximum element gap");
        }
        catch (InvalidOperationException exception)
        {
            Status.Text = exception.Message;
            Status.Foreground = Brush("#FF8A80");
            return;
        }
        WriteButton.IsEnabled = false;
        Status.Text = "Smart Dim: drag one sample cluster zone. Its size will partition all checked model elements automatically.";
        Status.Foreground = Brush("#A8B2BC");
        _window.Hide();
        NativeWindow.TryActivate(_uiApplication.MainWindowHandle);
        Queue(
            app => result = SmartDimension.SmartDimensionService.Create(
                app,
                selectedGroups,
                maximumElementGapMillimeters),
            "Smart Dim selection mode: drag a sample rectangle around one typical cluster; press Esc to cancel...",
            () =>
            {
                if (!_window.IsVisible) _window.Show();
                _window.Activate();
                if (result is null || result.Cancelled)
                {
                    Status.Text = "Smart Dim cancelled; the model was not changed.";
                    Status.Foreground = Brush("#A8B2BC");
                    return;
                }

                Status.Text =
                    $"Smart Dim wrote {result.Created} real dimension(s). " +
                    (result.Grouped > 0
                        ? $"Grouped {result.Grouped} duct segment(s) into edge chains. "
                        : string.Empty) +
                    (result.Skipped > 0
                        ? $"Could not dimension {result.Skipped} selected element(s). "
                        : string.Empty) +
                    result.Summary;
                Status.ToolTip = string.Join(Environment.NewLine, result.Messages);
                Status.Foreground = result.Created > 0 ? Brush("#58D68D") : Brush("#FFCC80");
                _analysisReady = false;
                WriteButton.IsEnabled = false;
            });
    }

    private void PickDuctDensitySampleZone()
    {
        if (_busy || _disposed || _snapshot is null) return;

        LayoutRect? pickedZone = null;
        WriteButton.IsEnabled = false;
        Status.Text =
            $"Duct density: drag one SAMPLE area. Its size will repeat across the Active View with maximum {SmartTagDuctDensity.MaximumTagsPerZone} eligible Duct tags per area.";
        Status.Foreground = Brush("#A8B2BC");
        _window.Hide();
        NativeWindow.TryActivate(_uiApplication.MainWindowHandle);
        Queue(
            app => pickedZone = SmartTagRevitService.PickDuctDensitySampleZone(app),
            "Drag one sample area for Duct density; press Esc to keep the previous sample...",
            () =>
            {
                if (!_window.IsVisible) _window.Show();
                _window.Activate();
                if (pickedZone is null)
                {
                    Status.Text = "Duct sample selection cancelled; the previous density setting was kept.";
                    Status.Foreground = Brush("#A8B2BC");
                    return;
                }

                _ductDensitySampleZone = pickedZone;
                _ductDensitySampleViewId = _snapshot.ViewId;
                UpdateDuctDensityUi();
                RequestPreview();
                int selectedDucts = SelectedRecordsForLayout(SelectedGroups())
                    .Count(item => item.Layout.Group.Equals(
                        DuctCategoryKey,
                        StringComparison.OrdinalIgnoreCase));
                Status.Text =
                    $"Duct sample applied across the Active View: maximum " +
                    $"{SmartTagDuctDensity.MaximumTagsPerZone} per repeated area; " +
                    $"{selectedDucts} Duct tag(s) currently selected by the density rule.";
                Status.Foreground = Brush("#7FC5FF");
            });
    }

    private void PickElementsFromRevit()
    {
        if (_busy || _disposed) return;

        HashSet<string> selectedGroups = SelectedGroups();
        if (selectedGroups.Count == 0)
        {
            Status.Text = "Select at least one category before scanning elements.";
            Status.Foreground = Brush("#FFCC80");
            return;
        }

        Dictionary<string, long> selectedTypes = SelectedTagTypeIds();
        bool fastGuidedCapture = ReadLayoutStyle() == SmartTagLayoutStyle.GuidedZones;

        SmartTagViewSnapshot? captured = null;
        WriteButton.IsEnabled = false;
        Status.Text =
            $"Drag a Revit rectangle; only the {selectedGroups.Count} checked category/categories will be scanned.";
        _window.Hide();
        NativeWindow.TryActivate(_uiApplication.MainWindowHandle);
        Queue(
            app => captured = SmartTagRevitService.PickElementsAndCapture(
                app, selectedGroups, fastGuidedCapture),
            "Revit selection mode: drag a rectangle around elements in the checked categories...",
            () =>
            {
                if (captured is null)
                {
                    if (!_window.IsVisible) _window.Show();
                    _window.Activate();
                    Status.Text = "Element scan cancelled or no supported MEP element was selected.";
                    Status.Foreground = Brush("#A8B2BC");
                    return;
                }

                _snapshot = captured;
                _guidedZone = null;
                _guidedZoneIsSuggested = false;
                UpdateGuidedZoneUi();
                UpdateDuctDensityUi();
                int totalElements = _snapshot.Tags.Select(item => item.ElementId).Distinct().Count();
                int existing = _snapshot.Tags.Count(item => !item.WillCreate);
                int missing = _snapshot.Tags.Count(item => item.WillCreate);
                int architectureObstacles = _snapshot.Obstacles.Count(item =>
                    item.Kind == LayoutObstacleKind.Architecture);
                Find<TextBlock>("view_name_text").Text =
                    $"{_snapshot.ViewName}  |  {totalElements} scanned MEP elements  |  " +
                    $"{architectureObstacles} architecture obstacle(s)  |  " +
                    $"{existing} existing tag(s)  |  {missing} untagged candidate(s) total";
                PopulateCategories(_snapshot.Categories, selectedGroups, selectedTypes);
                // Do not run the Guided zone search here. Scan should return
                // the selected hosts immediately; Analyze performs the zone
                // suggestion and layout once, on the background worker.
                PreviewImage.Source = LoadBitmap(_snapshot.ImageBytes);
                PreviewMode.Text = "SCANNED ELEMENTS - selected categories are ready to analyze";
                ResetPreviewZoom();
                if (!_window.IsVisible) _window.Show();
                _window.Activate();
                RequestPreview();
            });
    }

    private void PickGuidedTagZone()
    {
        if (_busy || _disposed) return;
        if (ReadLayoutStyle() != SmartTagLayoutStyle.GuidedZones)
        {
            Status.Text = "Select Guided Zones in Layout Style before picking a tag area.";
            Status.Foreground = Brush("#FFCC80");
            return;
        }

        LayoutRect? pickedZone = null;
        WriteButton.IsEnabled = false;
        Status.Text = "Drag the Revit area that must contain every selected tag; it does not need to be empty.";
        Status.Foreground = Brush("#A8B2BC");
        _window.Hide();
        NativeWindow.TryActivate(_uiApplication.MainWindowHandle);
        Queue(
            app => pickedZone = SmartTagRevitService.PickGuidedTagZone(app),
            "Revit selection mode: drag the required tag-text zone...",
            () =>
            {
                if (!_window.IsVisible) _window.Show();
                _window.Activate();
                if (pickedZone is null)
                {
                    Status.Text = "Tag-zone selection cancelled; the previous zone was kept.";
                    Status.Foreground = Brush("#A8B2BC");
                    return;
                }

                _guidedZone = pickedZone;
                _guidedZoneIsSuggested = false;
                UpdateGuidedZoneUi();
                RequestPreview();
                Status.Text = "Tag zone selected. Analyze will force every tag inside it and report any remaining overlap.";
                Status.Foreground = Brush("#7FC5FF");
            });
    }

    private bool SuggestGuidedTagZone(bool announce)
    {
        if (_snapshot is null || ReadLayoutStyle() != SmartTagLayoutStyle.GuidedZones)
        {
            if (announce)
            {
                Status.Text = "Select Guided Zones and scan checked MEP categories first.";
                Status.Foreground = Brush("#FFCC80");
            }
            return false;
        }

        HashSet<string> groups = SelectedGroups();
        List<LayoutTagInput> tags = _snapshot.Tags
            .Where(item => groups.Contains(item.Layout.Group))
            .Select(item => item.Layout)
            .ToList();
        if (tags.Count == 0)
        {
            if (announce)
            {
                Status.Text = "Select at least one category containing scanned MEP elements.";
                Status.Foreground = Brush("#FFCC80");
            }
            return false;
        }

        try
        {
            // Keep Guided consistent with the accepted layout workflow: the
            // automatic search uses visible MEP geometry only. Architecture is
            // not allowed to reshuffle an already accepted tag arrangement.
            List<LayoutObstacle> obstacles = _snapshot.Obstacles
                .Where(item => item.Kind == LayoutObstacleKind.Mep)
                .ToList();
            SmartTagLayoutSettings settings = ReadSettings() with { GuidedZone = null };
            SmartTagGuidedZoneSuggestion suggestion = SmartTagGuidedZoneLayout.SuggestZone(
                tags,
                obstacles,
                _snapshot.Frame,
                settings);
            _guidedZone = suggestion.Zone;
            _guidedZoneIsSuggested = true;
            UpdateGuidedZoneUi();
            if (announce)
            {
                Status.Text = suggestion.Message +
                              " Click APPLY SELECTION + ANALYZE to solve the complete group order.";
                Status.Foreground = Brush("#7FC5FF");
            }
            return true;
        }
        catch (Exception exception)
        {
            if (announce)
            {
                Status.Text = exception.Message;
                Status.Foreground = Brush("#FF8A80");
            }
            return false;
        }
    }

    private void AlignPickedTags()
    {
        if (_busy || _disposed) return;

        ComboBoxItem? selected = FindQuick<WpfComboBox>(
            "quick_align_edge_combo").SelectedItem as ComboBoxItem;
        string action = selected?.Tag?.ToString() ?? "Left";
        bool arrangeStack = string.Equals(
            action,
            "Stack",
            StringComparison.OrdinalIgnoreCase);
        bool alignLeftEdge = !string.Equals(
            action,
            "Right",
            StringComparison.OrdinalIgnoreCase);
        double rowGapPaperMillimeters = ReadManualTagGapPaperMillimeters();
        SmartTagManualAlignResult? result = null;
        WriteButton.IsEnabled = false;
        Status.Text = arrangeStack
            ? "Pick the TOP REFERENCE tag, then select the messy target tags and click Finish to stack them below it."
            : alignLeftEdge
            ? "Pick one REFERENCE tag, then drag a rectangle across target text/leader lines to align their left edges."
            : "Pick one REFERENCE tag, then drag a rectangle across target text/leader lines to align their right edges.";
        Status.Foreground = Brush("#A8B2BC");
        bool keepCompactMode = _smartTagCompactMode;
        _window.Hide();
        if (keepCompactMode && _quickAlignWindow.IsVisible)
            _quickAlignWindow.Hide();
        NativeWindow.TryActivate(_uiApplication.MainWindowHandle);
        Queue(
            app => result = SmartTagRevitService.PickAndAlignExistingTags(
                app,
                alignLeftEdge,
                arrangeStack,
                rowGapPaperMillimeters),
            arrangeStack
                ? "First pick the top reference tag; then select target tags and click Finish..."
                : "First pick the reference tag; then drag across target tag text/leader lines...",
            () =>
            {
                if (keepCompactMode)
                {
                    SyncQuickAlignVisibility();
                }
                else
                {
                    if (!_window.IsVisible) _window.Show();
                    _window.Activate();
                }
                if (result is null)
                {
                    Status.Text = arrangeStack
                        ? "Tag stacking cancelled; Revit was not changed."
                        : "Tag edge alignment cancelled; Revit was not changed.";
                    Status.Foreground = Brush("#A8B2BC");
                    return;
                }

                _previewTimer.Stop();
                _layout = null;
                _analysisReady = false;
                WriteButton.IsEnabled = false;
                Overlay.Children.Clear();
                PreviewImage.Source = LoadBitmap(result.ImageBytes);
                PreviewMode.Text = arrangeStack
                    ? "ACTUAL REVIT TAGS - stacked by real text bounds with orthogonal leaders"
                    : alignLeftEdge
                    ? "ACTUAL REVIT TAGS - selected left text edges aligned"
                    : "ACTUAL REVIT TAGS - selected right text edges aligned";
                Status.Text = arrangeStack
                    ? $"Arranged {result.Aligned} target tag(s) below the fixed reference using real text bounds; " +
                      $"{result.Skipped} skipped. Leaders run horizontal from text, then vertical to their hosts."
                    : $"Aligned {result.Aligned} target tag(s) to the reference tag's " +
                      $"{(alignLeftEdge ? "left" : "right")} text edge; " +
                      $"{result.Skipped} skipped. Original vertical tag positions were preserved.";
                Status.ToolTip = result.Warnings.Count == 0
                    ? null
                    : string.Join(Environment.NewLine, result.Warnings);
                Status.Foreground = result.Aligned > 0 ? Brush("#7FC5FF") : Brush("#FF8A80");
            });
    }

    private void UpdateManualTagActionUi()
    {
        ComboBoxItem? selected = FindQuick<WpfComboBox>(
            "quick_align_edge_combo").SelectedItem as ComboBoxItem;
        bool arrangeStack = string.Equals(
            selected?.Tag?.ToString(),
            "Stack",
            StringComparison.OrdinalIgnoreCase);
        FindQuick<TextBlock>("quick_action_text").Text = arrangeStack
            ? "ARRANGE TAGS"
            : "ALIGN TAGS";
        FindQuick<Button>("quick_align_tags_btn").ToolTip = arrangeStack
            ? "Pick the top reference tag, select target tags, then click Finish."
            : "Pick one reference tag, then drag a rectangle across target text/leader lines. Runs immediately after the rectangle.";
    }

    private void ArrangeTagsInPickedZone()
    {
        if (_busy || _disposed) return;

        double rowGapPaperMillimeters = ReadManualTagGapPaperMillimeters();
        SmartTagManualAlignResult? result = null;
        WriteButton.IsEnabled = false;
        Status.Text =
            "Step 1: drag across tag text or leader lines. Step 2: drag the empty rectangle where those tags must be arranged.";
        Status.Foreground = Brush("#A8B2BC");
        bool keepCompactMode = _smartTagCompactMode;
        _window.Hide();
        if (keepCompactMode && _quickAlignWindow.IsVisible)
            _quickAlignWindow.Hide();
        NativeWindow.TryActivate(_uiApplication.MainWindowHandle);
        Queue(
            app => result = SmartTagRevitService.PickAndArrangeTagsInZone(
                app,
                rowGapPaperMillimeters),
            "Drag across tag text/leader lines, then mark the destination location. The second rectangle size is not a limit...",
            () =>
            {
                if (keepCompactMode)
                {
                    SyncQuickAlignVisibility();
                }
                else
                {
                    if (!_window.IsVisible) _window.Show();
                    _window.Activate();
                }
                if (result is null)
                {
                    Status.Text = "Zone tag arrangement cancelled; Revit was not changed.";
                    Status.Foreground = Brush("#A8B2BC");
                    return;
                }

                _previewTimer.Stop();
                _layout = null;
                _analysisReady = false;
                WriteButton.IsEnabled = false;
                Overlay.Children.Clear();
                PreviewImage.Source = LoadBitmap(result.ImageBytes);
                PreviewMode.Text =
                    "ACTUAL REVIT TAGS - collected by text/leader crossing and stacked at the picked location";
                Status.Text =
                    $"Arranged {result.Aligned} collected tag(s) at the picked location; " +
                    $"{result.Skipped} skipped. Real text bounds and orthogonal leaders were used.";
                Status.ToolTip = result.Warnings.Count == 0
                    ? null
                    : string.Join(Environment.NewLine, result.Warnings);
                Status.Foreground = result.Aligned > 0 ? Brush("#7FC5FF") : Brush("#FF8A80");
            });
    }

    private void ArrangeTagsAroundReference()
    {
        if (_busy || _disposed) return;

        ComboBoxItem? selectedSide = FindQuick<WpfComboBox>(
            "quick_auto_side_combo").SelectedItem as ComboBoxItem;
        bool placeAbove = string.Equals(
            selectedSide?.Tag?.ToString(),
            "Above",
            StringComparison.OrdinalIgnoreCase);
        double rowGapPaperMillimeters = ReadManualTagGapPaperMillimeters();
        SmartTagManualAlignResult? result = null;
        WriteButton.IsEnabled = false;
        Status.Text =
            $"Pick one fixed sample tag, then drag across target text/leader lines. Targets will be stacked {(placeAbove ? "above" : "below")} it.";
        Status.Foreground = Brush("#A8B2BC");
        bool keepCompactMode = _smartTagCompactMode;
        _window.Hide();
        if (keepCompactMode && _quickAlignWindow.IsVisible)
            _quickAlignWindow.Hide();
        NativeWindow.TryActivate(_uiApplication.MainWindowHandle);
        Queue(
            app => result = SmartTagRevitService.PickAndArrangeTagsAroundReference(
                app,
                rowGapPaperMillimeters,
                placeAbove),
            "Pick the sample tag, then drag across target tag text/leader lines...",
            () =>
            {
                if (keepCompactMode)
                {
                    SyncQuickAlignVisibility();
                }
                else
                {
                    if (!_window.IsVisible) _window.Show();
                    _window.Activate();
                }
                if (result is null)
                {
                    Status.Text = "Auto Column cancelled; Revit was not changed.";
                    Status.Foreground = Brush("#A8B2BC");
                    return;
                }

                _previewTimer.Stop();
                _layout = null;
                _analysisReady = false;
                WriteButton.IsEnabled = false;
                Overlay.Children.Clear();
                PreviewImage.Source = LoadBitmap(result.ImageBytes);
                PreviewMode.Text =
                    $"ACTUAL REVIT TAGS - auto column {(placeAbove ? "above" : "below")} fixed reference using real text widths";
                Status.Text =
                    $"Auto Column arranged {result.Aligned} target tag(s) {(placeAbove ? "above" : "below")} the fixed sample; " +
                    $"{result.Skipped} skipped. Crossing-aware orthogonal leaders were applied.";
                Status.ToolTip = result.Warnings.Count == 0
                    ? null
                    : string.Join(Environment.NewLine, result.Warnings);
                Status.Foreground = result.Aligned > 0 ? Brush("#7FC5FF") : Brush("#FF8A80");
            });
    }

    private void RequestPreview()
    {
        if (_suppressPreview || _disposed) return;
        _previewTimer.Stop();
        _layout = null;
        _analysisReady = false;
        WriteButton.IsEnabled = false;
        if (_snapshot is not null)
        {
            PreviewImage.Source = LoadBitmap(_snapshot.ImageBytes);
        }
        Overlay.Children.Clear();
        HashSet<string> groups = SelectedGroups();
        int selectedTags = SelectedRecordsForLayout(groups).Count;
        bool guidedNeedsSuggestion = ReadLayoutStyle() == SmartTagLayoutStyle.GuidedZones &&
                                     _guidedZone is null;
        Find<TextBlock>("tag_count_text").Text = selectedTags.ToString();
        Find<TextBlock>("elbow_count_text").Text = "0";
        Find<TextBlock>("clash_count_text").Text = "0";
        Find<TextBlock>("clash_count_text").Foreground = Brush("#A8B2BC");
        // Guided Zones no longer requires a manual rectangle before Analyze.
        // Analyze can first suggest a capacity-safe clear zone for the current
        // checked categories, so keep the action available here.
        PreviewButton.IsEnabled = !_busy && groups.Count > 0 && selectedTags > 0;
        PreviewMode.Text = groups.Count == 0
            ? "ACTIVE VIEW — select categories"
            : $"SELECTION READY — {groups.Count} categor{(groups.Count == 1 ? "y" : "ies")}; not arranged yet";
        AnalysisSummary.Text = guidedNeedsSuggestion
            ? "Guided Zones: Analyze will automatically find a clear zone large enough for all checked tags."
            : groups.Count == 0
            ? "Select one or more categories."
            : "Selection changed. Click APPLY SELECTION + ANALYZE to solve all selected categories together.";
        Status.Text = guidedNeedsSuggestion
            ? "No manual zone is required. Click APPLY SELECTION + ANALYZE to find a clear tag zone automatically."
            : groups.Count == 0
            ? "Select one or more categories; Revit is unchanged."
            : $"{groups.Count} categor{(groups.Count == 1 ? "y" : "ies")} selected, {selectedTags} tag(s). " +
              "Nothing has been arranged. Click APPLY SELECTION + ANALYZE.";
        Status.Foreground = Brush("#A8B2BC");
    }

    private async System.Threading.Tasks.Task RecomputePreviewAsync()
    {
        if (_snapshot is null || _disposed) return;
        try
        {
            SmartTagLayoutSettings settings = ReadSettings();
            HashSet<string> groups = SelectedGroups();
            List<SmartTagRecord> selectedRecords = SelectedRecordsForLayout(groups, settings);
            List<TagLayoutPlacement> fixedCompanionTags =
                settings.LayoutStyle is
                    SmartTagLayoutStyle.Standard or SmartTagLayoutStyle.StandardNearHost
                    ? BuildExistingCompanionReservations(
                        groups.Contains(DuctCategoryKey)
                            ? _snapshot.Tags
                            : selectedRecords)
                    : [];
            HashSet<long> fixedCompanionKeys = fixedCompanionTags
                .Select(item => item.TagKey)
                .ToHashSet();
            List<SmartTagRecord> recordsToArrange = selectedRecords
                .Where(item => !fixedCompanionKeys.Contains(item.TagKey))
                .ToList();
            List<LayoutTagInput> tags = recordsToArrange
                .Select(item => item.Layout)
                .ToList();
            int selectedExisting = recordsToArrange.Count(item => !item.WillCreate);
            int selectedNew = recordsToArrange.Count(item => item.WillCreate);
            string sideMode = ReadSideMode();
            // Architecture is a post-process constraint. The accepted MEP
            // layout must be identical to a view with architecture hidden; only
            // a complete clashing cluster may be translated afterwards.
            List<LayoutObstacle> baselineObstacles = _snapshot.Obstacles
                .Where(obstacle => obstacle.Kind == LayoutObstacleKind.Mep)
                .ToList();
            if (settings.LayoutStyle == SmartTagLayoutStyle.GuidedZones &&
                (_guidedZone is null || _guidedZoneIsSuggested) &&
                tags.Count > 0)
            {
                LayoutRect suggestionFrame = _snapshot.Frame;
                SmartTagGuidedZoneSuggestion suggestion =
                    await System.Threading.Tasks.Task.Run(() =>
                        SmartTagGuidedZoneLayout.SuggestZone(
                            tags,
                            baselineObstacles,
                            suggestionFrame,
                            settings with { GuidedZone = null }));
                if (_disposed) return;
                _guidedZone = suggestion.Zone;
                _guidedZoneIsSuggested = true;
                UpdateGuidedZoneUi();
                settings = settings with { GuidedZone = _guidedZone };
            }
            // Auto uses the accepted two-sided implementation unchanged.
            // Left and Right are isolated in their own files and solve all
            // selected categories on one shared rail/reservation pass.
            SmartTagGuidedZoneEvaluation? guidedEvaluation = null;
            SmartTagLayoutResult? nearHostLayout = null;
            if (settings.LayoutStyle == SmartTagLayoutStyle.StandardNearHost)
            {
                // Only immutable numeric DTOs enter the worker. No Revit API,
                // WPF controls or live snapshot access is permitted there.
                LayoutRect frame = _snapshot.Frame;
                nearHostLayout = await System.Threading.Tasks.Task.Run(() =>
                    SmartTagStandardV2Layout.ComputeClustered(
                        tags,
                        baselineObstacles,
                        frame,
                        settings,
                        settings.AutoSide,
                        initialReservations: fixedCompanionTags));
                if (_disposed) return;
            }
            SmartTagLayoutResult globalLayout;
            if (settings.LayoutStyle == SmartTagLayoutStyle.GuidedZones)
            {
                LayoutRect guidedFrame = _snapshot.Frame;
                guidedEvaluation = await System.Threading.Tasks.Task.Run(() =>
                    SmartTagGuidedZoneLayout.Compute(
                        tags,
                        baselineObstacles,
                        guidedFrame,
                        settings));
                if (_disposed) return;
                globalLayout = guidedEvaluation.Layout;
            }
            else
            {
                // Standard/Compact/Left/Right can also contain hundreds of
                // tags. Keep every numeric solver off the WPF dispatcher so
                // Windows never replaces the Smart Tag surface with a black
                // "Not Responding" ghost while the plan is being computed.
                LayoutRect workerFrame = _snapshot.Frame;
                globalLayout = await System.Threading.Tasks.Task.Run(() =>
                    settings.LayoutStyle switch
                    {
                        SmartTagLayoutStyle.StandardNearHost => nearHostLayout!,
                        SmartTagLayoutStyle.CompactGroups => SmartTagCompactGroupLayout.Compute(
                            tags,
                            baselineObstacles,
                            workerFrame,
                            settings),
                        _ => sideMode switch
                        {
                            "Left" => SmartTagLeftLayout.Compute(
                                tags,
                                baselineObstacles,
                                workerFrame,
                                settings,
                                fixedCompanionTags),
                            "Right" => SmartTagRightLayout.Compute(
                                tags,
                                baselineObstacles,
                                workerFrame,
                                settings,
                                fixedCompanionTags),
                            _ => SmartTagLayoutEngine.ComputeClustered(
                                tags,
                                baselineObstacles,
                                workerFrame,
                                settings,
                                autoSide: true,
                                initialReservations: fixedCompanionTags)
                        }
                    });
                if (_disposed) return;
            }
            List<TagLayoutPlacement> combined = globalLayout.Placements.ToList();
            LayoutRect combinedBounds = combined.Count == 0
                ? _snapshot.Frame
                : new LayoutRect(
                    combined.Min(item => item.TagBounds.MinU),
                    combined.Min(item => item.TagBounds.MinV),
                    combined.Max(item => item.TagBounds.MaxU),
                    combined.Max(item => item.TagBounds.MaxV));
            _layout = new SmartTagLayoutResult(
                combined,
                combined.Count(item => item.UsesElbow),
                combined.Count(item => item.HasClash),
                combinedBounds) { Diagnostic = globalLayout.Diagnostic };
            Find<TextBlock>("tag_count_text").Text = _layout.Placements.Count.ToString();
            Find<TextBlock>("elbow_count_text").Text = _layout.ElbowCount.ToString();
            Find<TextBlock>("clash_count_text").Text = _layout.ClashCount.ToString();
            Find<TextBlock>("clash_count_text").Foreground = Brush(
                _layout.ClashCount == 0 ? "#58D68D" : "#FF5C5C");
            PreviewButton.IsEnabled = !_busy && _layout.Placements.Count > 0;
            // Selection analysis must finish with a real project-family preview
            // before writing is enabled.
            WriteButton.IsEnabled = false;
            if (guidedEvaluation is not null)
            {
                AnalysisSummary.Text = guidedEvaluation.Message;
            }
            Status.Text = groups.Count == 0
                ? "Select at least one category from the Active View. Revit is unchanged."
                : tags.Count == 0
                ? "No visible MEP elements match the selected categories."
                : _layout.ClashCount == 0
                    ? $"Ready: create {selectedNew}, arrange {selectedExisting}; " +
                      $"{_layout.ElbowCount} one-elbow fallback(s), no clashes."
                    : $"{selectedNew} new + {selectedExisting} existing tag(s); " +
                      $"{_layout.ClashCount} warning clash(es). You can still write this layout to Revit for testing.";
            Status.Foreground = Brush(_layout.ClashCount == 0 ? "#A8B2BC" : "#FF8A80");
            if (_layout.Diagnostic is not null) Status.Text = _layout.Diagnostic;
            if (settings.LayoutStyle == SmartTagLayoutStyle.StandardNearHost)
            {
                // Near Host must never present DTO rectangles as if they were
                // project tags. Keep the clean model image until the temporary
                // Revit transaction returns the real family preview.
                Overlay.Children.Clear();
                PreviewImage.Source = LoadBitmap(_snapshot.ImageBytes);
                PreviewMode.Text = "STANDARD V2 READY - click Analyze to render real project tags";
            }
            else
            {
                RenderOverlay();
            }
        }
        catch (Exception exception)
        {
            if (_disposed) return;
            _layout = null;
            Overlay.Children.Clear();
            PreviewButton.IsEnabled = false;
            WriteButton.IsEnabled = false;
            Status.Text = exception.Message;
            Status.Foreground = Brush("#FF8A80");
        }
    }

    private void RenderOverlay()
    {
        Overlay.Children.Clear();
        if (ReadLayoutStyle() == SmartTagLayoutStyle.StandardNearHost)
        {
            return;
        }
        if (_snapshot is null || _layout is null ||
            PreviewSurface.ActualWidth <= 1 || PreviewSurface.ActualHeight <= 1)
        {
            return;
        }

        ViewportTransform transform = ViewportTransform.Create(
            _snapshot,
            PreviewSurface.ActualWidth,
            PreviewSurface.ActualHeight);
        if (ReadLayoutStyle() == SmartTagLayoutStyle.GuidedZones && _guidedZone is LayoutRect zone)
        {
            DrawColumn(zone, transform);
        }
        foreach (TagLayoutPlacement placement in _layout.Placements)
        {
            string color = placement.HasClash
                ? "#E53935"
                : placement.UsesFreeEnd || placement.UsesElbow ? "#F2A11B" : "#159447";
            DrawLine(placement.Head, placement.Elbow, transform, color, 1.0);
            if (placement.UsesElbow)
            {
                DrawLine(placement.End, placement.Elbow, transform, color, 1.2);
            }
            DrawAnchor(placement.End, transform, color);
            bool willCreate = _snapshot.Tags
                .FirstOrDefault(item => item.TagKey == placement.TagKey)?.WillCreate == true;
            DrawTag(placement, transform, color, willCreate);
        }
    }

    private void ZoomPreview(System.Windows.Input.MouseWheelEventArgs args)
    {
        double oldZoom = PreviewScale.ScaleX;
        double factor = args.Delta > 0 ? 1.18 : 1.0 / 1.18;
        double newZoom = Math.Clamp(oldZoom * factor, 1.0, 8.0);
        WpfPoint cursor = args.GetPosition(PreviewSurface);
        if (Math.Abs(newZoom - 1.0) <= 1e-8)
        {
            ResetPreviewZoom();
        }
        else if (Math.Abs(newZoom - oldZoom) > 1e-8)
        {
            double contentX = (cursor.X - PreviewTranslate.X) / oldZoom;
            double contentY = (cursor.Y - PreviewTranslate.Y) / oldZoom;
            PreviewScale.ScaleX = newZoom;
            PreviewScale.ScaleY = newZoom;
            PreviewTranslate.X = cursor.X - contentX * newZoom;
            PreviewTranslate.Y = cursor.Y - contentY * newZoom;
            Find<TextBlock>("zoom_text").Text = $"{newZoom * 100:0}%";
        }
        args.Handled = true;
    }

    private void ResetPreviewZoom()
    {
        PreviewScale.ScaleX = 1.0;
        PreviewScale.ScaleY = 1.0;
        PreviewTranslate.X = 0.0;
        PreviewTranslate.Y = 0.0;
        Find<TextBlock>("zoom_text").Text = "100%";
    }

    private void DrawColumn(LayoutRect bounds, ViewportTransform transform)
    {
        WpfPoint topLeft = transform.Map(new LayoutPoint(bounds.MinU, bounds.MaxV));
        WpfPoint bottomRight = transform.Map(new LayoutPoint(bounds.MaxU, bounds.MinV));
        var rectangle = new WpfRectangle
        {
            Width = Math.Max(1, bottomRight.X - topLeft.X),
            Height = Math.Max(1, bottomRight.Y - topLeft.Y),
            Stroke = Brush("#668096A8"),
            StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection([6, 5]),
            Fill = Brush("#18FFFFFF")
        };
        Canvas.SetLeft(rectangle, topLeft.X);
        Canvas.SetTop(rectangle, topLeft.Y);
        Overlay.Children.Add(rectangle);
    }

    private void DrawLine(
        LayoutPoint start,
        LayoutPoint end,
        ViewportTransform transform,
        string color,
        double thickness)
    {
        WpfPoint a = transform.Map(start);
        WpfPoint b = transform.Map(end);
        Overlay.Children.Add(new WpfLine
        {
            X1 = a.X,
            Y1 = a.Y,
            X2 = b.X,
            Y2 = b.Y,
            Stroke = Brush(color),
            StrokeThickness = thickness,
            SnapsToDevicePixels = true
        });
    }

    private void DrawAnchor(LayoutPoint point, ViewportTransform transform, string color)
    {
        WpfPoint mapped = transform.Map(point);
        var ellipse = new WpfEllipse
        {
            Width = 5,
            Height = 5,
            Fill = Brush(color),
            Stroke = Brushes.White,
            StrokeThickness = 1
        };
        Canvas.SetLeft(ellipse, mapped.X - 2.5);
        Canvas.SetTop(ellipse, mapped.Y - 2.5);
        Overlay.Children.Add(ellipse);
    }

    private void DrawTag(
        TagLayoutPlacement placement,
        ViewportTransform transform,
        string color,
        bool willCreate)
    {
        WpfPoint topLeft = transform.Map(new LayoutPoint(
            placement.TagBounds.MinU,
            placement.TagBounds.MaxV));
        WpfPoint bottomRight = transform.Map(new LayoutPoint(
            placement.TagBounds.MaxU,
            placement.TagBounds.MinV));
        double width = Math.Clamp(bottomRight.X - topLeft.X, 28, 220);
        double height = Math.Clamp(bottomRight.Y - topLeft.Y, 12, 64);
        var text = new TextBlock
        {
            Text = placement.Label,
            Foreground = Brushes.Black,
            FontSize = Math.Clamp(height * 0.36, 7, 11),
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        var border = new Border
        {
            Width = width,
            Height = height,
            Background = willCreate ? Brush("#EAF4FF") : Brushes.White,
            BorderBrush = Brush(color),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(5, 2, 4, 2),
            Child = text
        };
        Canvas.SetLeft(border, topLeft.X);
        Canvas.SetTop(border, topLeft.Y);
        Overlay.Children.Add(border);
    }

    private async void ApplyToView()
    {
        if (_snapshot is null || _layout is null || _busy || !_analysisReady) return;
        SmartTagApplyResult? result = null;
        Dictionary<string, long> tagTypes = SelectedTagTypeIds();
        SmartTagLayoutSettings settings = ReadSettings();
        bool requiresAnalyzedReplay = settings.LayoutStyle is
            SmartTagLayoutStyle.StandardNearHost or SmartTagLayoutStyle.GuidedZones;
        if (requiresAnalyzedReplay &&
            (_analyzedSettings != settings ||
             _analyzedTypes is null || !_analyzedTypes.OrderBy(p => p.Key).SequenceEqual(tagTypes.OrderBy(p => p.Key))))
        {
            _analysisReady = false;
            WriteButton.IsEnabled = false;
            Status.Text = "Model or settings changed. Analyze again before writing.";
            return;
        }
        if (settings.LayoutStyle == SmartTagLayoutStyle.StandardNearHost && _nearHostWriteWarning is not null)
        {
            Status.Text = _nearHostWriteWarning;
            return;
        }
        _window.Hide();
        // Hide is asynchronous at the HWND compositor boundary. Yield once so
        // the surface is actually removed before Revit enters the synchronous
        // ExternalEvent transaction; otherwise Windows can leave a large black
        // ghost window above Revit for the duration of a dense preview.
        await _window.Dispatcher.InvokeAsync(
            () => { },
            DispatcherPriority.ApplicationIdle);
        await System.Threading.Tasks.Task.Delay(40);
        if (_disposed) return;
        NativeWindow.TryActivate(_uiApplication.MainWindowHandle);
        Queue(
            app =>
            {
                result = SmartTagRevitService.Apply(
                app,
                _snapshot,
                _layout.Placements,
                tagTypes,
                settings,
                includeClashes: true,
                replayAnalyzed: requiresAnalyzedReplay);
            },
            "Applying Smart Tag layout to Revit...",
            () =>
            {
                SmartTagApplyResult applied = result
                    ?? new SmartTagApplyResult(0, 0, 0, ["Revit returned no result."], 0, 0, 0, 0);
                string warning = applied.Warnings.Count > 0
                    ? "  " + string.Join(" | ", applied.Warnings.Take(3))
                    : string.Empty;
                Status.Text = $"Created {applied.Created}; arranged {applied.Arranged}; " +
                              $"skipped {applied.Skipped}; text overlaps {applied.TextOverlaps}; " +
                              $"leader crossings {applied.LeaderCrossings}; " +
                              $"model overlaps {applied.ModelOverlaps}.{warning}  " +
                              "Use REFRESH ACTIVE VIEW before applying again.";
                Status.Foreground = Brush(applied.Applied > 0 ? "#58D68D" : "#FF8A80");
                _analysisReady = false;
                _layout = null;
                WriteButton.IsEnabled = false;
                if (!_window.IsVisible) _window.Show();
                _window.Activate();
            });
    }

    private SmartTagLayoutSettings ReadSettings()
    {
        double width = ReadPositive("column_width_tb", "Column Width");
        double offset = ReadNonNegative("offset_tb", "Tag Offset from MEP");
        double rowSpacing = ReadNonNegative("row_spacing_tb", "Row Spacing");
        double topMargin = ReadNonNegative("top_margin_tb", "Top Margin");
        double clearance = ReadNonNegative("clearance_tb", "Clearance");
        string sideMode = ReadSideMode();
        bool placeLeft = sideMode != "Right";
        bool autoSide = sideMode == "Auto";
        int viewScale = _snapshot?.ViewScale ?? 1;
        SmartTagLayoutStyle layoutStyle = ReadLayoutStyle();
        bool standardV2 = layoutStyle == SmartTagLayoutStyle.StandardNearHost;
        return new SmartTagLayoutSettings(
            placeLeft,
            ToViewFeet(width, viewScale),
            ToViewFeet(offset, viewScale),
            ToViewFeet(rowSpacing, viewScale),
            ToViewFeet(topMargin, viewScale),
            ToViewFeet(clearance, viewScale),
            standardV2 || Find<CheckBox>("avoid_elements_cb").IsChecked == true,
            standardV2 || Find<CheckBox>("avoid_leaders_cb").IsChecked == true,
            standardV2 || Find<CheckBox>("avoid_text_cb").IsChecked == true,
            Find<CheckBox>("lock_upper_cb").IsChecked == true,
            Find<CheckBox>("move_lower_cb").IsChecked == true,
            autoSide) with
        {
            LayoutStyle = layoutStyle,
            GuidedZone = _guidedZone
        };
    }

    private SmartTagLayoutStyle ReadLayoutStyle()
    {
        ComboBoxItem? selected = Find<WpfComboBox>("layout_style_combo").SelectedItem as ComboBoxItem;
        string value = selected?.Tag?.ToString() ??
                       selected?.Content?.ToString() ??
                       "Standard";
        if (value.StartsWith("Guided", StringComparison.OrdinalIgnoreCase))
        {
            return SmartTagLayoutStyle.GuidedZones;
        }
        if (value.Equals("StandardNearHost", StringComparison.OrdinalIgnoreCase))
            return SmartTagLayoutStyle.StandardNearHost;
        return value.StartsWith("Compact", StringComparison.OrdinalIgnoreCase)
            ? SmartTagLayoutStyle.CompactGroups
            : SmartTagLayoutStyle.Standard;
    }

    private void UpdateGuidedZoneUi()
    {
        bool guided = ReadLayoutStyle() == SmartTagLayoutStyle.GuidedZones;
        Find<Border>("guided_zone_panel").Visibility = guided
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;
        Find<Button>("pick_tag_zone_btn").IsEnabled = guided && !_busy;
        Find<Button>("suggest_tag_zone_btn").IsEnabled = guided && !_busy;
        Find<TextBlock>("guided_zone_text").Text = _guidedZone is LayoutRect zone
            ? _guidedZoneIsSuggested
                ? $"Auto suggested: {zone.Width:0.##} x {zone.Height:0.##} view ft. " +
                  "All checked tags fit; pick manually to override."
                : $"Manual zone: {zone.Width:0.##} x {zone.Height:0.##} view ft. " +
                  "All scanned tags will be forced inside; no re-scan is required."
            : "No tag zone selected.";
    }

    private string ReadSideMode()
    {
        ComboBoxItem? selected = Find<WpfComboBox>("side_combo").SelectedItem as ComboBoxItem;
        string value = selected?.Tag?.ToString() ??
                       selected?.Content?.ToString() ??
                       "Auto";
        if (value.StartsWith("Right", StringComparison.OrdinalIgnoreCase)) return "Right";
        if (value.StartsWith("Left", StringComparison.OrdinalIgnoreCase)) return "Left";
        return "Auto";
    }

    private HashSet<string> SelectedGroups()
    {
        return _categoryChecks
            .Where(item => item.Value.IsChecked == true)
            .Select(item => item.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private List<SmartTagRecord> SelectedRecordsForLayout(
        IReadOnlySet<string> groups,
        SmartTagLayoutSettings? settings = null)
    {
        if (_snapshot is null) return [];
        List<SmartTagRecord> selected = _snapshot.Tags
            .Where(item => groups.Contains(item.Layout.Group))
            .ToList();
        if (!groups.Contains(DuctCategoryKey)) return selected;

        // Existing visible DA/AT tags are valid anchors even when their category
        // checkbox is off. New companion tags are usable only when that category
        // is selected and will actually be created by this run.
        List<SmartTagRecord> companions = _snapshot.Tags
            .Where(item => DuctCompanionCategoryKeys.Contains(item.Layout.Group) &&
                           (!item.WillCreate || groups.Contains(item.Layout.Group)))
            .ToList();
        double followRadius = SmartTagLayoutEngine.NearbyFollowRadius(
            settings ?? ReadDuctDensitySettings());
        var densityCandidates = new List<DuctDensityCandidate>();
        foreach (SmartTagRecord duct in selected.Where(item => item.Layout.Group.Equals(
                     DuctCategoryKey,
                     StringComparison.OrdinalIgnoreCase)))
        {
            SmartTagRecord? nearest = companions
                .OrderBy(item => SquaredDistance(
                    duct.Layout.ElementBounds,
                    item.Layout.ElementBounds))
                .ThenBy(item => item.ElementId)
                .FirstOrDefault();
            double nearestDistance = nearest is null
                ? double.PositiveInfinity
                : Math.Sqrt(SquaredDistance(
                    duct.Layout.ElementBounds,
                    nearest.Layout.ElementBounds));
            // Density applies to every eligible Duct change. A nearby DA/AT
            // owns its rail; a Duct without a genuinely local companion still
            // consumes the area's quota and is retained for Standard placement
            // instead of silently disappearing from the selected set.
            if (nearestDistance > followRadius) nearest = null;
            densityCandidates.Add(new DuctDensityCandidate(
                duct.TagKey,
                duct.Layout.Anchor,
                Existing: !duct.WillCreate,
                nearest?.TagKey ?? 0,
                nearestDistance)
            {
                HostSpan = Math.Max(
                    duct.Layout.ElementBounds.Width,
                    duct.Layout.ElementBounds.Height)
            });
        }

        LayoutRect densityZone = _ductDensitySampleZone is LayoutRect sample &&
                                 _ductDensitySampleViewId == _snapshot.ViewId
            ? sample
            : _snapshot.Frame;
        IReadOnlySet<long> retainedDuctKeys = SmartTagDuctDensity.Select(
            densityCandidates,
            densityZone);
        Dictionary<long, long> preferredCompanionByDuct = densityCandidates
            .Where(item => retainedDuctKeys.Contains(item.Key))
            .ToDictionary(item => item.Key, item => item.NearestCompanionKey);
        return selected
            .Where(item => !item.Layout.Group.Equals(
                               DuctCategoryKey,
                               StringComparison.OrdinalIgnoreCase) ||
                           retainedDuctKeys.Contains(item.TagKey))
            .Select(item => preferredCompanionByDuct.TryGetValue(
                    item.TagKey,
                    out long companionKey)
                ? item with
                {
                    Layout = item.Layout with
                    {
                        PreferredFollowerAnchorTagKey = companionKey
                    }
                }
                : item)
            .ToList();
    }

    private static List<TagLayoutPlacement> BuildExistingCompanionReservations(
        IReadOnlyList<SmartTagRecord> selectedRecords)
    {
        var result = new List<TagLayoutPlacement>();
        foreach (SmartTagRecord record in selectedRecords.Where(item =>
                     !item.WillCreate &&
                     DuctCompanionCategoryKeys.Contains(item.Layout.Group)))
        {
            LayoutTagInput layout = record.Layout;
            double width = Math.Max(layout.TagWidth, 0.01);
            double height = Math.Max(layout.TagHeight, 0.01);
            double centreU = layout.CurrentHead.U + layout.TextOffsetU;
            double centreV = layout.CurrentHead.V + layout.TextOffsetV;
            var bounds = new LayoutRect(
                centreU - width * 0.5,
                centreV - height * 0.5,
                centreU + width * 0.5,
                centreV + height * 0.5);
            bool usesElbow = Math.Abs(layout.CurrentHead.V - layout.Anchor.V) > 1e-7;
            LayoutPoint elbow = usesElbow
                ? new LayoutPoint(layout.Anchor.U, layout.CurrentHead.V)
                : layout.Anchor;
            result.Add(new TagLayoutPlacement(
                layout.TagKey,
                layout.CurrentHead,
                layout.Anchor,
                elbow,
                bounds,
                usesElbow,
                usesElbow,
                HasClash: false,
                layout.Label,
                layout.Group)
            {
                CanAnchorDuctFollowers = true
            });
        }
        return result;
    }

    private SmartTagLayoutSettings ReadDuctDensitySettings()
    {
        int viewScale = _snapshot?.ViewScale ?? 1;
        double width = double.TryParse(
            Find<WpfTextBox>("column_width_tb").Text,
            out double parsedWidth) && parsedWidth > 0.0
                ? parsedWidth
                : 60.0;
        double offset = double.TryParse(
            Find<WpfTextBox>("offset_tb").Text,
            out double parsedOffset) && parsedOffset >= 0.0
                ? parsedOffset
                : 5.0;
        return new SmartTagLayoutSettings(
            PlaceLeft: true,
            ColumnWidth: ToViewFeet(width, viewScale),
            OffsetFromElements: ToViewFeet(offset, viewScale),
            RowSpacing: 0.0,
            TopMargin: 0.0,
            Clearance: 0.0,
            AvoidElements: false,
            AvoidLeaders: false,
            AvoidTagText: false,
            LockUpperLeaders: false,
            MoveLowerTagFirst: false,
            AutoSide: true);
    }

    private static double SquaredDistance(LayoutRect first, LayoutRect second)
    {
        double du = first.MaxU < second.MinU
            ? second.MinU - first.MaxU
            : second.MaxU < first.MinU
                ? first.MinU - second.MaxU
                : 0.0;
        double dv = first.MaxV < second.MinV
            ? second.MinV - first.MaxV
            : second.MaxV < first.MinV
                ? first.MinV - second.MaxV
                : 0.0;
        return du * du + dv * dv;
    }

    private void UpdateDuctDensityUi()
    {
        Find<Button>("pick_duct_density_zone_btn").IsEnabled =
            !_busy && _snapshot is not null;
        TextBlock text = Find<TextBlock>("duct_density_zone_text");
        text.Text = _ductDensitySampleZone is LayoutRect sample &&
                    _snapshot is not null &&
                    _ductDensitySampleViewId == _snapshot.ViewId
            ? $"Sample {sample.Width * 304.8:0} x {sample.Height * 304.8:0} model mm repeats across this view; " +
              $"maximum {SmartTagDuctDensity.MaximumTagsPerZone} eligible Duct tags per area."
            : $"No sample yet: maximum {SmartTagDuctDensity.MaximumTagsPerZone} Duct tags in the current view. " +
              "Pick a sample area to repeat that density across the view.";
    }

    private Dictionary<string, long> SelectedTagTypeIds() => _categoryTypeCombos
        .Where(item => item.Value.SelectedItem is ComboBoxItem)
        .ToDictionary(
            item => item.Key,
            item => (item.Value.SelectedItem as ComboBoxItem)?.Tag is long id ? id : 0,
            StringComparer.OrdinalIgnoreCase);

    private void PopulateCategories(
        IReadOnlyList<SmartTagCategoryInfo> categories,
        HashSet<string> selectedBeforeRefresh,
        IReadOnlyDictionary<string, long> selectedTypesBeforeRefresh)
    {
        _suppressPreview = true;
        try
        {
            CategoryList.Children.Clear();
            _categoryChecks.Clear();
            _categoryTypeCombos.Clear();
            foreach (SmartTagCategoryInfo category in categories)
            {
                int missing = Math.Max(0, category.ElementCount - category.ExistingTagCount);
                string categoryCount = category.Key.Equals(
                    DuctCategoryKey,
                    StringComparison.OrdinalIgnoreCase)
                    ? $"{missing} eligible / {category.ExistingTagCount} existing · max " +
                      $"{SmartTagDuctDensity.MaximumTagsPerZone} per sample area"
                    : $"{missing} new / {category.ExistingTagCount} existing";
                var checkBox = new CheckBox
                {
                    Content = $"{category.Name}\n{categoryCount}",
                    Tag = category.Key,
                    IsChecked = selectedBeforeRefresh.Contains(category.Key),
                    IsEnabled = category.TagTypes.Count > 0,
                    Margin = new Thickness(0, 3, 8, 3),
                    ToolTip = category.TagTypes.Count == 0
                        ? "No compatible Tag Family is loaded in this project."
                        : null
                };
                var typeCombo = new WpfComboBox
                {
                    Height = 30,
                    MinWidth = 170,
                    IsEnabled = category.TagTypes.Count > 0,
                    ToolTip = "Tag Family and Type from the current Revit project"
                };
                foreach (SmartTagTypeInfo type in category.TagTypes)
                {
                    typeCombo.Items.Add(new ComboBoxItem { Content = type.Name, Tag = type.Id });
                }
                long preferredType = selectedTypesBeforeRefresh.TryGetValue(category.Key, out long previous)
                    ? previous
                    : category.DefaultTagTypeId;
                typeCombo.SelectedItem = typeCombo.Items
                    .OfType<ComboBoxItem>()
                    .FirstOrDefault(item => item.Tag is long id && id == preferredType)
                    ?? typeCombo.Items.OfType<ComboBoxItem>().FirstOrDefault();
                checkBox.Checked += (_, _) =>
                {
                    if (!_categorySelectionOrder.Contains(category.Key))
                    {
                        _categorySelectionOrder.Add(category.Key);
                    }
                    RequestPreview();
                };
                checkBox.Unchecked += (_, _) =>
                {
                    _categorySelectionOrder.Remove(category.Key);
                    RequestPreview();
                };
                typeCombo.SelectionChanged += (_, _) => RequestPreview();
                var row = new System.Windows.Controls.Grid { Margin = new Thickness(0, 2, 0, 2) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(185) });
                row.Children.Add(checkBox);
                System.Windows.Controls.Grid.SetColumn(typeCombo, 1);
                row.Children.Add(typeCombo);
                CategoryList.Children.Add(row);
                _categoryChecks[category.Key] = checkBox;
                _categoryTypeCombos[category.Key] = typeCombo;
                if (checkBox.IsChecked == true && !_categorySelectionOrder.Contains(category.Key))
                {
                    _categorySelectionOrder.Add(category.Key);
                }
            }
        }
        finally
        {
            _suppressPreview = false;
        }
    }

    private async void PreviewTags(bool recomputeLayout)
    {
        if (_busy || _disposed) return;
        if (recomputeLayout)
        {
            _analysisReady = false;
            _layout = null;
            _busy = true;
            SetEnabled(false);
            var content = _window.Content as UIElement;
            if (content is not null) content.IsEnabled = false;
            Status.Text = ReadLayoutStyle() == SmartTagLayoutStyle.GuidedZones
                ? "Guided Analyze: finding one zone, then repairing model/leader clashes once. Revit is unchanged."
                : ReadLayoutStyle() == SmartTagLayoutStyle.StandardNearHost
                    ? "Computing layout... Near Host search is limited to 4 seconds; Revit is unchanged."
                    : "Computing tag layout; Revit is unchanged.";
            try { await RecomputePreviewAsync(); }
            finally
            {
                _busy = false;
                if (!_disposed)
                {
                    if (content is not null) content.IsEnabled = true;
                    SetEnabled(true);
                }
            }
        }
        if (_disposed) return;
        if (_snapshot is null || _layout is not { Placements.Count: > 0 })
        {
            WriteButton.IsEnabled = false;
            return;
        }

        SmartTagActualPreviewResult? actualPreview = null;
        _analysisReady = false;
        Dictionary<string, long> tagTypes = SelectedTagTypeIds();
        SmartTagLayoutSettings settings = ReadSettings();
        if (settings.LayoutStyle is SmartTagLayoutStyle.StandardNearHost or SmartTagLayoutStyle.GuidedZones)
        {
            Overlay.Children.Clear();
            PreviewImage.Source = LoadBitmap(_snapshot.ImageBytes);
            PreviewMode.Text = settings.LayoutStyle == SmartTagLayoutStyle.GuidedZones
                ? "MEASURING REAL PROJECT TAG FAMILIES..."
                : "CREATING REAL PROJECT TAG PREVIEW...";
        }
        _window.Hide();
        // Let Windows remove the Smart Tag HWND before Revit starts the real
        // project-family preview transaction on its UI thread.
        await _window.Dispatcher.InvokeAsync(
            () => { },
            DispatcherPriority.ApplicationIdle);
        await System.Threading.Tasks.Task.Delay(40);
        if (_disposed) return;
        NativeWindow.TryActivate(_uiApplication.MainWindowHandle);
        Queue(
            app =>
            {
                actualPreview = SmartTagRevitService.PreviewActualTags(
                    app,
                    _snapshot,
                    _layout.Placements,
                    tagTypes,
                    settings);
            },
            "Rendering project Tag Families in a temporary Revit transaction...",
            () =>
            {
                SmartTagActualPreviewResult rendered = actualPreview
                    ?? throw new InvalidOperationException("Revit returned no actual tag preview.");
                int plannedTagCount = _layout.Placements.Count;
                bool requiresExactSet = settings.LayoutStyle is
                    SmartTagLayoutStyle.StandardNearHost or SmartTagLayoutStyle.GuidedZones;
                bool incompleteExactSet = requiresExactSet &&
                    (rendered.LayoutResult.Skipped != 0 ||
                     rendered.LayoutResult.FinalPlacements.Count != plannedTagCount);
                bool incompleteNearHostSet =
                    settings.LayoutStyle == SmartTagLayoutStyle.StandardNearHost && incompleteExactSet;
                if (settings.LayoutStyle is SmartTagLayoutStyle.StandardNearHost or SmartTagLayoutStyle.GuidedZones)
                {
                    // Keep the complete analyzed set when real Revit creation
                    // misses a tag. Replacing it with FinalPlacements used to
                    // make the missing Air Terminal disappear permanently from
                    // the subsequent Write replay.
                    if (!incompleteExactSet)
                        _layout = _layout with { Placements = rendered.LayoutResult.FinalPlacements.ToArray() };
                    _analyzedSettings = settings;
                    _analyzedTypes = new Dictionary<string, long>(tagTypes);
                    _analyzedRevision = _documentRevision;
                }
                PreviewImage.Source = LoadBitmap(rendered.ImageBytes);
                Overlay.Children.Clear();
                string? nearHostRepairDiagnostic = rendered.LayoutResult.Warnings.FirstOrDefault(w =>
                    w.StartsWith("Standard local repair:", StringComparison.Ordinal));
                PreviewMode.Text = settings.LayoutStyle == SmartTagLayoutStyle.StandardNearHost &&
                                   nearHostRepairDiagnostic is not null
                    ? "ACTUAL NEAR HOST — " + nearHostRepairDiagnostic
                    : settings.LayoutStyle == SmartTagLayoutStyle.GuidedZones
                        ? "ACTUAL GUIDED TAGS — real Family/Type bounds; temporary transaction rolled back"
                        : "ACTUAL PROJECT TAGS — temporary transaction rolled back";
                string? acceptedNearHostDiagnostic = rendered.LayoutResult.Warnings.FirstOrDefault(w =>
                    w.StartsWith("Near Host accepted against Standard:", StringComparison.Ordinal));
                bool acceptedNearHost = settings.LayoutStyle == SmartTagLayoutStyle.StandardNearHost &&
                    acceptedNearHostDiagnostic is not null && rendered.LayoutResult.ActualClashes == 0;
                Find<TextBlock>("clash_count_text").Text = acceptedNearHost
                    ? "0"
                    : rendered.LayoutResult.ActualClashes.ToString();
                Find<TextBlock>("clash_count_text").Foreground = Brush(
                    acceptedNearHost || rendered.LayoutResult.ActualClashes == 0 ? "#58D68D" : "#FF5C5C");
                AnalysisSummary.Text = settings.LayoutStyle == SmartTagLayoutStyle.StandardNearHost
                    ? $"Near Host validation: {rendered.LayoutResult.ActualClashes} text/model or annotation clash(es). " +
                      "Each conflicting tag pair is counted once. Write is blocked only if V2 is worse than " +
                      "the same real-family Standard baseline or the analyzed tag set is incomplete."
                    : rendered.LayoutResult.TextOverlaps == 0 &&
                      rendered.LayoutResult.LeaderCrossings == 0 &&
                      rendered.LayoutResult.ModelOverlaps == 0
                        ? "Analysis complete: 0 critical clash; no text, leader, or model overlap."
                        : $"Critical clashes: {rendered.LayoutResult.TextOverlaps} text overlap(s), " +
                          $"{rendered.LayoutResult.LeaderCrossings} leader/text crossing(s), " +
                          $"{rendered.LayoutResult.ModelOverlaps} tag/model overlap(s). " +
                          "The selected Auto/Left/Right rail mode was analyzed.";
                string? railDiagnostic = rendered.LayoutResult.Warnings.FirstOrDefault(
                    warning => warning.StartsWith("Column alignment:", StringComparison.Ordinal));
                if (!string.IsNullOrWhiteSpace(railDiagnostic))
                {
                    AnalysisSummary.Text += " " + railDiagnostic;
                }
                string? unsafeDiagnostic = rendered.LayoutResult.Warnings.FirstOrDefault(w =>
                    w.StartsWith("Near Host unsafe:", StringComparison.Ordinal));
                bool nearHostBlocked = settings.LayoutStyle == SmartTagLayoutStyle.StandardNearHost &&
                    (unsafeDiagnostic is not null || incompleteNearHostSet);
                bool guidedBlocked = settings.LayoutStyle == SmartTagLayoutStyle.GuidedZones &&
                    incompleteExactSet;
                _nearHostWriteWarning = nearHostBlocked
                    ? unsafeDiagnostic ?? $"Preview reports {rendered.LayoutResult.ActualClashes} unresolved clash(es)."
                    : null;
                _analysisReady = !nearHostBlocked && !guidedBlocked;
                WriteButton.IsEnabled = _analysisReady && _layout.Placements.Count > 0;
                if (!_window.IsVisible) _window.Show();
                _window.Activate();
                Status.Text = $"Actual project Tag Family preview: {plannedTagCount} selected = " +
                              $"{rendered.LayoutResult.Created} new + " +
                              $"{rendered.LayoutResult.Arranged} existing; " +
                              $"{rendered.LayoutResult.Skipped} skipped; transaction rolled back. " +
                              (acceptedNearHost
                                  ? "Near Host has zero validated clashes. Ready to write the analyzed result."
                                  : rendered.LayoutResult.ActualClashes == 0
                                  ? "Ready to write."
                                  : $"{rendered.LayoutResult.ActualClashes} critical clash(es) remain; " +
                                    "V2 did not worsen the real-family Standard baseline. Ready to write the analyzed result.");
                Status.Foreground = Brush(
                    acceptedNearHost || rendered.LayoutResult.ActualClashes == 0 ? "#7FC5FF" : "#FF8A80");
                string? fallback = rendered.LayoutResult.Warnings.FirstOrDefault(w =>
                    w.StartsWith("Near Host search limit reached", StringComparison.Ordinal) ||
                    w.StartsWith("Standard local repair:", StringComparison.Ordinal) ||
                    w.StartsWith("Standard V2 local batches:", StringComparison.Ordinal) ||
                    w.StartsWith("Standard V2 local rail align:", StringComparison.Ordinal) ||
                    w.StartsWith("Standard V2 row-preserving align:", StringComparison.Ordinal) ||
                    w.StartsWith("Standard V2 improved", StringComparison.Ordinal) ||
                    w.StartsWith("Near Host accepted against Standard:", StringComparison.Ordinal))
                    ?? nearHostRepairDiagnostic
                    ?? _layout.Diagnostic;
                if (fallback is not null) AnalysisSummary.Text += " " + fallback;
                if (nearHostBlocked)
                {
                    Status.Text = _nearHostWriteWarning ?? "Near Host: unresolved clashes remain; Write blocked.";
                    AnalysisSummary.Text = Status.Text + (fallback is null ? "" : " " + fallback);
                    Status.Foreground = Brush("#FF8A80");
                }
                else if (guidedBlocked)
                {
                    Status.Text =
                        $"Guided actual preview is incomplete: {rendered.LayoutResult.Skipped} tag(s) skipped. " +
                        "Check the selected Tag Family/Type, then Analyze again. Write blocked.";
                    AnalysisSummary.Text = Status.Text;
                    Status.Foreground = Brush("#FF8A80");
                }
            });
    }

    private void ResetSettings()
    {
        _suppressPreview = true;
        try
        {
            Find<WpfComboBox>("layout_style_combo").SelectedIndex = 1;
            _guidedZone = null;
            _guidedZoneIsSuggested = false;
            _ductDensitySampleZone = null;
            _ductDensitySampleViewId = -1;
            Find<WpfComboBox>("side_combo").SelectedIndex = 0;
            Find<WpfTextBox>("column_width_tb").Text = "60";
            Find<WpfTextBox>("offset_tb").Text = "5";
            Find<WpfTextBox>("row_spacing_tb").Text = "1";
            Find<WpfTextBox>("top_margin_tb").Text = "5";
            Find<WpfTextBox>("clearance_tb").Text = "2";
            Find<WpfTextBox>("dim_cluster_gap_tb").Text = "2500";
            foreach (string name in new[]
            {
                "live_preview_cb", "avoid_elements_cb", "avoid_leaders_cb",
                "avoid_text_cb", "lock_upper_cb", "move_lower_cb"
            })
            {
                Find<CheckBox>(name).IsChecked = true;
            }
            foreach (CheckBox category in _categoryChecks.Values)
            {
                category.IsChecked = false;
            }
        }
        finally
        {
            _suppressPreview = false;
        }
        UpdateGuidedZoneUi();
        UpdateDuctDensityUi();
        RequestPreview();
    }

    private void Queue(Action<UIApplication> request, string status, Action completed)
    {
        if (_busy) return;
        _busy = true;
        Status.Text = status;
        SetEnabled(false);
        if (!_handler.TrySetRequest(request, error =>
        {
            _window.Dispatcher.BeginInvoke(() =>
            {
                _busy = false;
                SetEnabled(true);
                if (error is not null) ShowError(error);
                else completed();
            });
        }))
        {
            _busy = false;
            SetEnabled(true);
            if (!_window.IsVisible) _window.Show();
            throw new InvalidOperationException("Another Revit request is running.");
        }

        ExternalEventRequest raised = _externalEvent.Raise();
        if (raised is not ExternalEventRequest.Accepted and not ExternalEventRequest.Pending)
        {
            _busy = false;
            SetEnabled(true);
            if (!_window.IsVisible) _window.Show();
            throw new InvalidOperationException($"Revit rejected the request: {raised}.");
        }
    }

    private void SetEnabled(bool enabled)
    {
        Find<Button>("smart_dim_btn").IsEnabled = enabled;
        Find<Button>("pick_elements_btn").IsEnabled = enabled;
        Find<Button>("pick_duct_density_zone_btn").IsEnabled = enabled && _snapshot is not null;
        Find<Button>("suggest_tag_zone_btn").IsEnabled =
            enabled && ReadLayoutStyle() == SmartTagLayoutStyle.GuidedZones;
        Find<Button>("pick_tag_zone_btn").IsEnabled =
            enabled && ReadLayoutStyle() == SmartTagLayoutStyle.GuidedZones;
        FindQuick<Button>("quick_align_tags_btn").IsEnabled = enabled;
        FindQuick<Button>("quick_auto_column_btn").IsEnabled = enabled;
        FindQuick<Button>("quick_zone_tags_btn").IsEnabled = enabled;
        FindQuick<WpfComboBox>("quick_align_edge_combo").IsEnabled = enabled;
        FindQuick<WpfComboBox>("quick_auto_side_combo").IsEnabled = enabled;
        Find<Button>("refresh_btn").IsEnabled = enabled;
        Find<Button>("reset_btn").IsEnabled = enabled;
        PreviewButton.IsEnabled = enabled &&
                                  (SelectedGroups().Count > 0 || _layout is { Placements.Count: > 0 });
        WriteButton.IsEnabled = enabled && _analysisReady &&
                                _layout is { Placements.Count: > 0 };
    }

    private void ShowError(Exception exception)
    {
        if (!_window.IsVisible) _window.Show();
        Status.Text = exception.Message;
        Status.ToolTip = exception.ToString();
        Status.Foreground = Brush("#FF8A80");
    }

    private double ReadPositive(string name, string label)
    {
        if (!double.TryParse(Find<WpfTextBox>(name).Text, out double value) || value <= 0)
            throw new InvalidOperationException($"{label} must be greater than zero.");
        return value;
    }

    private double ReadNonNegative(string name, string label)
    {
        if (!double.TryParse(Find<WpfTextBox>(name).Text, out double value) || value < 0)
            throw new InvalidOperationException($"{label} cannot be negative.");
        return value;
    }

    private double ReadManualTagGapPaperMillimeters()
    {
        return double.TryParse(
                   Find<WpfTextBox>("row_spacing_tb").Text,
                   out double value) &&
               double.IsFinite(value) &&
               value >= 0.0
            ? value
            : 1.0;
    }

    private static double ToViewFeet(double paperMillimeters, int viewScale) =>
        paperMillimeters * Math.Max(1, viewScale) / 304.8;

    private static BitmapImage LoadBitmap(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private void SyncQuickAlignVisibility()
    {
        if (_disposed || !_quickAlignReady) return;
        FindQuick<Button>("quick_restore_smart_tag_btn").Visibility =
            _smartTagCompactMode
                ? System.Windows.Visibility.Visible
                : System.Windows.Visibility.Collapsed;
        FindQuick<WpfThumb>("quick_drag_handle").Visibility =
            _smartTagCompactMode
                ? System.Windows.Visibility.Visible
                : System.Windows.Visibility.Collapsed;
        // Some Windows/WPF themes report IsVisible=false near the end of a
        // minimize transition. Minimized is an intentional compact mode: the
        // quick toolbar must remain usable even when the setup window is gone.
        if (_smartTagCompactMode ||
            _window.WindowState == WindowState.Minimized ||
            _window.IsVisible)
        {
            if (!_quickAlignWindow.IsVisible) _quickAlignWindow.Show();
            if (!_smartTagCompactMode && _window.WindowState != WindowState.Minimized)
                PositionQuickAlignWindow();
        }
        else if (_quickAlignWindow.IsVisible)
        {
            _quickAlignWindow.Hide();
        }
    }

    private void PositionQuickAlignWindow()
    {
        if (_disposed || !_quickAlignReady ||
            !_window.IsVisible || !_quickAlignWindow.IsVisible) return;
        if (_window.WindowState == WindowState.Minimized) return;

        Rect workArea = SystemParameters.WorkArea;
        double width = _quickAlignWindow.ActualWidth > 1
            ? _quickAlignWindow.ActualWidth
            : _quickAlignWindow.Width;
        double height = _quickAlignWindow.ActualHeight > 1
            ? _quickAlignWindow.ActualHeight
            : _quickAlignWindow.Height;
        const double gap = 8;

        double rightCandidate = _window.Left + _window.ActualWidth + gap;
        double leftCandidate = _window.Left - width - gap;
        double left = rightCandidate + width <= workArea.Right
            ? rightCandidate
            : leftCandidate >= workArea.Left
                ? leftCandidate
                : Math.Clamp(
                    _window.Left + _window.ActualWidth - width - 18,
                    workArea.Left + gap,
                    workArea.Right - width - gap);
        double top = Math.Clamp(
            _window.Top + 92,
            workArea.Top + gap,
            workArea.Bottom - height - gap);

        _quickAlignWindow.Left = left;
        _quickAlignWindow.Top = top;
    }

    private void MoveQuickAlignWindow(double horizontalChange, double verticalChange)
    {
        if (_disposed || !_smartTagCompactMode) return;

        Rect workArea = SystemParameters.WorkArea;
        double width = _quickAlignWindow.ActualWidth > 1
            ? _quickAlignWindow.ActualWidth
            : _quickAlignWindow.Width;
        double height = _quickAlignWindow.ActualHeight > 1
            ? _quickAlignWindow.ActualHeight
            : _quickAlignWindow.Height;
        _quickAlignWindow.Left = Math.Clamp(
            _quickAlignWindow.Left + horizontalChange,
            workArea.Left,
            workArea.Right - width);
        _quickAlignWindow.Top = Math.Clamp(
            _quickAlignWindow.Top + verticalChange,
            workArea.Top,
            workArea.Bottom - height);
    }

    private void HandleSmartTagWindowStateChanged()
    {
        if (_disposed || !_quickAlignReady || _handlingSmartTagStateChange) return;
        if (_window.WindowState != WindowState.Minimized)
        {
            SyncQuickAlignVisibility();
            PositionQuickAlignWindow();
            return;
        }

        // Convert minimize into a compact hide. Keeping the large WPF window
        // in WindowState.Minimized lets Windows hide other dispatcher windows
        // with it on some systems. Normalizing and hiding only this window
        // leaves the ownerless Quick Align toolbar genuinely independent.
        _smartTagCompactMode = true;
        _handlingSmartTagStateChange = true;
        try
        {
            _window.WindowState = WindowState.Normal;
            _window.Hide();
        }
        finally
        {
            _handlingSmartTagStateChange = false;
        }

        _window.Dispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle,
            new Action(() =>
            {
                if (_disposed || !_quickAlignReady || !_smartTagCompactMode) return;
                SyncQuickAlignVisibility();
                if (!_quickAlignWindow.IsVisible) _quickAlignWindow.Show();
                _quickAlignWindow.Topmost = true;
                NativeWindow.TryShowNoActivate(
                    new WindowInteropHelper(_quickAlignWindow).Handle);
            }));
    }

    private void RestoreSmartTagWindow()
    {
        if (_disposed) return;
        _smartTagCompactMode = false;
        _handlingSmartTagStateChange = true;
        try
        {
            _window.WindowState = WindowState.Normal;
            if (!_window.IsVisible) _window.Show();
        }
        finally
        {
            _handlingSmartTagStateChange = false;
        }
        _window.Activate();
        SyncQuickAlignVisibility();
        PositionQuickAlignWindow();
    }

    private static Window LoadWindow(string fileName)
    {
        string root = IoPath.GetDirectoryName(typeof(SmartTagController).Assembly.Location)
            ?? throw new InvalidOperationException("Plugin output folder is unavailable.");
        string path = IoPath.Combine(root, "Ui", fileName);
        using FileStream stream = File.OpenRead(path);
        using XmlReader reader = XmlReader.Create(stream);
        return (Window)XamlReader.Load(reader);
    }

    private T Find<T>(string name) where T : FrameworkElement =>
        _window.FindName(name) as T
        ?? throw new InvalidOperationException($"UI control '{name}' was not found.");

    private T FindQuick<T>(string name) where T : FrameworkElement =>
        _quickAlignWindow.FindName(name) as T
        ?? throw new InvalidOperationException($"Quick Align control '{name}' was not found.");

    private static SolidColorBrush Brush(string hex) =>
        new((WpfColor)ColorConverter.ConvertFromString(hex));

    private readonly record struct ViewportTransform(
        LayoutRect Frame,
        double ScaleX,
        double ScaleY,
        double OffsetX,
        double OffsetY)
    {
        public static ViewportTransform Create(
            SmartTagViewSnapshot snapshot,
            double availableWidth,
            double availableHeight)
        {
            double scale = Math.Min(
                availableWidth / snapshot.PixelWidth,
                availableHeight / snapshot.PixelHeight);
            double renderedWidth = snapshot.PixelWidth * scale;
            double renderedHeight = snapshot.PixelHeight * scale;
            return new ViewportTransform(
                snapshot.Frame,
                renderedWidth / Math.Max(snapshot.Frame.Width, 1e-9),
                renderedHeight / Math.Max(snapshot.Frame.Height, 1e-9),
                (availableWidth - renderedWidth) * 0.5,
                (availableHeight - renderedHeight) * 0.5);
        }

        public WpfPoint Map(LayoutPoint point) => new(
            OffsetX + (point.U - Frame.MinU) * ScaleX,
            OffsetY + (Frame.MaxV - point.V) * ScaleY);
    }

    private static class NativeWindow
    {
        private const uint NoSize = 0x0001;
        private const uint NoMove = 0x0002;
        private const uint NoActivate = 0x0010;
        private const uint ShowWindowFlag = 0x0040;
        private const uint Invalidate = 0x0001;
        private const uint UpdateNow = 0x0100;
        private const uint AllChildren = 0x0080;
        private static readonly IntPtr TopWindow = IntPtr.Zero;
        private static readonly IntPtr TopMostWindow = new(-1);

        internal static void TryShowNoActivate(IntPtr handle)
        {
            if (handle == IntPtr.Zero) return;
            try
            {
                ShowWindow(handle, 4); // SW_SHOWNOACTIVATE
                SetWindowPos(
                    handle,
                    TopMostWindow,
                    0,
                    0,
                    0,
                    0,
                    NoMove | NoSize | NoActivate | ShowWindowFlag);
            }
            catch
            {
                // WPF's normal Show path remains available if Win32 refuses.
            }
        }

        internal static void TryActivate(IntPtr handle)
        {
            if (handle == IntPtr.Zero) return;
            try
            {
                // Focus the existing Revit window without SW_RESTORE. The tool
                // must never maximize, restore, move, or resize Revit merely to
                // obtain a preview image.
                if (IsIconic(handle)) return;
                SetWindowPos(
                    handle,
                    TopWindow,
                    0,
                    0,
                    0,
                    0,
                    NoMove | NoSize);
                SetForegroundWindow(handle);
                RedrawWindow(handle, IntPtr.Zero, IntPtr.Zero, Invalidate | UpdateNow | AllChildren);
                DwmFlush();
            }
            catch
            {
                // Capture still has a Revit API fallback path if Windows refuses foreground activation.
            }
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsIconic(IntPtr window);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShowWindow(IntPtr window, int command);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPos(
            IntPtr window,
            IntPtr insertAfter,
            int x,
            int y,
            int width,
            int height,
            uint flags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr window);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RedrawWindow(
            IntPtr window,
            IntPtr updateRectangle,
            IntPtr updateRegion,
            uint flags);

        [DllImport("dwmapi.dll")]
        private static extern int DwmFlush();
    }
}
