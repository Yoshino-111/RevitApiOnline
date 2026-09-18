using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Globalization;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.DB.Structure;
using FamilyMEP.Plugin.Infrastructure;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Microsoft.Win32;
using UIApplication = Autodesk.Revit.UI.UIApplication;
using RevitExternalEvent = Autodesk.Revit.UI.ExternalEvent;
using ExternalEventRequest = Autodesk.Revit.UI.ExternalEventRequest;
using UIDocument = Autodesk.Revit.UI.UIDocument;
using WpfColor = System.Windows.Media.Color;
using WpfComboBox = System.Windows.Controls.ComboBox;
using WpfGrid = System.Windows.Controls.Grid;
using WpfPanel = System.Windows.Controls.Panel;
using WpfVisibility = System.Windows.Visibility;

namespace FamilyMEP.Plugin.SprinklerModeler;

internal sealed class SprinklerModelerController : IDisposable
{
    private const string ElevationModeLevel = "Level / floor below";
    private const string ElevationModeFloor = "Floor top below";
    private const string ElevationModeCeiling = "Ceiling underside";
    private const string ElevationModeReferencePlane = "Reference plane";
    private readonly UIApplication _uiApplication;
    private readonly Document _document;
    private readonly Window _window;
    private readonly RevitRequestHandler _revitRequestHandler;
    private readonly RevitExternalEvent _revitExternalEvent;
    private readonly DispatcherTimer _scanTimer;
    private readonly List<LayerMappingItem> _layerItems;
    private readonly List<ReviewIssueItem> _reviewItems;
    private WebView2CompositionControl? _sourcePdfWebView;
    private WebView2CompositionControl? _layerPdfWebView;
    private CoreWebView2Environment? _pdfEnvironment;
    private BitmapSource? _renderedPdfPage;
    private PdfVectorScene? _vectorScene;
    private IReadOnlyList<int> _vectorHitCandidates = [];
    private readonly Dictionary<PdfVectorClass, PdfVectorPickSnapshot> _vectorPicks = [];
    private readonly Dictionary<PdfVectorClass, List<PdfVectorPickSnapshot>> _additionalVectorPicks = [];
    private readonly Dictionary<PdfVectorClass, HashSet<int>> _vectorExcludedPathIds = [];
    private readonly Dictionary<PdfVectorClass, HashSet<int>> _vectorExcludedSegmentIds = [];
    private IReadOnlyDictionary<int, string> _cadLayerByPathId = new Dictionary<int, string>();
    private IReadOnlyDictionary<string, int> _cadLayerCounts = new Dictionary<string, int>();
    private IReadOnlyDictionary<int, int> _cadNativeColorBySegmentId = new Dictionary<int, int>();
    private byte[]? _cadPreviewPixels;
    private int _cadPreviewPixelWidth;
    private int _cadPreviewPixelHeight;
    private int _cadPreviewPixelStride;
    private readonly Dictionary<int, CadPixelSignature> _cadPixelSignatureCache = [];
    private readonly Dictionary<(int Segment, int Hue, int Saturation, int Value), double> _cadColorCoverageCache = [];
    private readonly List<CadColorChoice> _cadColorChoices = [];
    private readonly List<CadPixelSignature> _hiddenCadColors = [];
    private bool _cadAreaScanMode;
    private bool _cadAreaDragging;
    private System.Windows.Point _cadAreaStart;
    private System.Windows.Shapes.Rectangle? _cadAreaRectangle;
    private System.Windows.Shapes.Line? _cadSweepLine;
    private bool _removeAreaScanMode;
    private bool _removeAreaDragging;
    private System.Windows.Point _removeAreaStart;
    private int _selectedVectorSegmentId = -1;
    private int _hoveredVectorSegmentId = -1;
    private PdfVectorSelectionScope _vectorSelectionScope = PdfVectorSelectionScope.Line;
    private PdfVectorClass _pendingVectorClass = PdfVectorClass.None;
    private PdfVectorClass _pendingExclusionClass = PdfVectorClass.None;
    private PdfVectorClass _lastAssignedVectorClass = PdfVectorClass.None;
    private PdfVectorPickSnapshot? _activeVectorPick;
    private bool _appendVectorPickMode;
    private bool _isolateVectorResult;
    private System.Windows.Shapes.Path? _mainVectorHighlight;
    private System.Windows.Shapes.Path? _branchVectorHighlight;
    private System.Windows.Shapes.Path? _sprinklerVectorHighlight;
    private readonly List<System.Windows.Shapes.Path> _cadBaseVectorPaths = [];
    private System.Windows.Shapes.Path? _fittingVectorHighlight;
    private System.Windows.Shapes.Path? _currentVectorHighlight;
    private System.Windows.Shapes.Path? _excludedVectorHighlight;
    private System.Windows.Shapes.Line? _hoverVectorHighlight;
    private System.Windows.Shapes.Path? _hoverVectorObjectHighlight;
    private IReadOnlyList<PdfFittingCandidate> _fittingCandidates = [];
    private readonly List<RevitViewOption> _placementViews = [];
    private readonly List<SprinklerFamilyOption> _sprinklerFamilies = [];
    private readonly List<PipeTypeOption> _pipeTypes = [];
    private readonly List<ElevationReferenceOption> _elevationReferences = [];
    private ElementId? _scannedPlacementViewId;
    private ElementId? _alignedPdfInstanceId;
    private XYZ? _alignedPdfBottomLeft;
    private XYZ? _alignedPdfBottomRight;
    private XYZ? _alignedPdfTopLeft;
    private string? _currentPdfPath;
    private double _pdfZoom = 1.0;
    private int _pdfRotation;
    private string _activeAdjustMode = string.Empty;
    private Button? _draggedMarker;
    private System.Windows.Point _dragStart;
    private double _dragOriginLeft;
    private double _dragOriginTop;
    private ScrollViewer? _panningViewer;
    private System.Windows.Point _panStart;
    private double _panStartHorizontalOffset;
    private double _panStartVerticalOffset;
    private string _selectedConnectionKey = "armover";
    private int _scanProgress;
    private bool _suppressOrientationChange;
    private string _lastElevationMode = string.Empty;
    private bool _disposed;

    private bool IsCadSource =>
        string.Equals(Path.GetExtension(_currentPdfPath), ".dwg", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Path.GetExtension(_currentPdfPath), ".dxf", StringComparison.OrdinalIgnoreCase);

    private TabControl MainTabs => Find<TabControl>("main_tabs");
    private TextBlock Status => Find<TextBlock>("status_text");
    private WpfComboBox HeadOrientation => Find<WpfComboBox>("head_orientation_combo");
    private WebView2CompositionControl? ActivePdfViewer =>
        MainTabs.SelectedIndex == 1 ? _layerPdfWebView : _sourcePdfWebView;

    internal SprinklerModelerController(
        UIApplication uiApplication,
        RevitRequestHandler revitRequestHandler,
        RevitExternalEvent revitExternalEvent)
    {
        _uiApplication = uiApplication;
        _revitRequestHandler = revitRequestHandler;
        _revitExternalEvent = revitExternalEvent;
        _document = uiApplication.ActiveUIDocument?.Document
            ?? throw new InvalidOperationException("Open a Revit project first.");
        _window = LoadWindow();
        new WindowInteropHelper(_window).Owner = uiApplication.MainWindowHandle;

        _layerItems = SprinklerModelerData.CreateLayerMappings();
        _reviewItems = SprinklerModelerData.CreateReviewIssues();
        _scanTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(70)
        };
        _scanTimer.Tick += OnScanTick;

        AttachLayerOverlayToPdfContent();
        WireEvents();
        LoadInitialData();
    }

    private void AttachLayerOverlayToPdfContent()
    {
        ScrollViewer viewer = Find<ScrollViewer>("v2_layer_pdf_scroll");
        Image image = Find<Image>("v2_layer_pdf_image");
        Viewbox overlay = Find<Viewbox>("v2_pdf_overlay_view");
        WpfGrid host = Find<WpfGrid>("v2_layer_pdf_host");

        // Keep the PDF and every vector markup in one scrolled coordinate space.
        // A sibling overlay drifts or gets clipped once the page is highly zoomed.
        viewer.Content = null;
        host.Children.Remove(overlay);
        WpfGrid pageSurface = new()
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Background = Brush("#1F2730")
        };
        pageSurface.Children.Add(image);
        overlay.Margin = new Thickness(0);
        overlay.RenderTransform = System.Windows.Media.Transform.Identity;
        WpfPanel.SetZIndex(overlay, 10);
        pageSurface.Children.Add(overlay);
        viewer.Content = pageSurface;
    }

    internal void Show() => _window.Show();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _scanTimer.Stop();
        _scanTimer.Tick -= OnScanTick;
        DisposePdfViewer();
        try { _window.Close(); } catch { }
    }

    private void WireEvents()
    {
        _window.Closed += (_, _) =>
        {
            _scanTimer.Stop();
            DisposePdfViewer();
            _disposed = true;
        };
        Find<Button>("v2_browse_source_btn").Click += (_, _) => BrowseSource();
        Find<Button>("v2_replace_source_btn").Click += (_, _) => BrowseSource();
        Find<Button>("v2_scan_source_btn").Click += (_, _) => StartSourceScan();
        Find<Button>("v2_place_pdf_in_revit_btn").Click += (_, _) => PlaceOrSelectPdfInRevit();
        Find<Button>("v2_use_aligned_pdf_btn").Click += (_, _) => CaptureAlignedPdfPosition();
        Find<WpfComboBox>("v2_cad_units_combo").SelectionChanged += (_, _) =>
            OnCadDrawingUnitsChanged();
        Find<WpfComboBox>("v2_cad_recognition_mode_combo").SelectionChanged += (_, _) =>
            UpdateCadRecognitionModeHint();
        Find<WpfComboBox>("v2_cad_color_combo").SelectionChanged += (_, _) =>
        {
            UpdateCadColorSwatch();
            ApplyCadArchitectureVisibility();
        };
        Find<Button>("v2_hide_selected_cad_color_btn").Click += (_, _) =>
            HideSelectedCadColor();
        Find<Button>("v2_restore_hidden_cad_colors_btn").Click += (_, _) =>
            RestoreHiddenCadColors();
        Find<CheckBox>("v2_cad_hide_architecture_cb").Checked += (_, _) =>
            ApplyCadArchitectureVisibility(showStatus: true);
        Find<CheckBox>("v2_cad_hide_architecture_cb").Unchecked += (_, _) =>
            ApplyCadArchitectureVisibility(showStatus: true);
        Find<Button>("v2_cad_scan_area_btn").Click += (_, _) => BeginCadAreaSweep();
        Find<Button>("v2_cad_scan_whole_btn").Click += (_, _) => ScanCadWholeDrawing();
        Find<TextBox>("v2_cad_scale_factor_tb").TextChanged += (_, _) =>
            OnCadScaleCorrectionChanged();
        Find<WpfComboBox>("v2_target_view_combo").SelectionChanged += (_, _) =>
        {
            ClearScannedPlacementArea();
            RefreshElevationReferenceTargets();
        };
        Find<WpfComboBox>("v2_sprinkler_reference_mode_combo").SelectionChanged += (_, _) =>
            RefreshElevationReferenceTargets();
        Find<TextBox>("v2_layer_filter_tb").TextChanged += (_, _) => FilterLayers();
        Find<Button>("v2_auto_map_btn").Click += (_, _) => AutoMapLayers();
        Find<Button>("v2_clear_all_classifications_btn").Click += (_, _) => ClearAllVectorClassifications();
        Find<DataGrid>("v2_layer_grid").SelectionChanged += (_, _) => UpdateLayerSelection();
        for (int marker = 1; marker <= 6; marker++)
        {
            int selectedMarker = marker;
            Button markerButton = Find<Button>($"v2_marker_{marker}_btn");
            markerButton.Click += (_, _) => SelectLayerByMarker(selectedMarker);
            markerButton.PreviewMouseLeftButtonDown += (_, eventArgs) => BeginMarkerDrag(markerButton, eventArgs);
            markerButton.PreviewMouseMove += (_, eventArgs) => DragMarker(markerButton, eventArgs);
            markerButton.PreviewMouseLeftButtonUp += (_, eventArgs) => EndMarkerDrag(markerButton, eventArgs);
        }
        Find<CheckBox>("v2_show_overlay_cb").Checked += (_, _) => SetOverlayVisibility(true);
        Find<CheckBox>("v2_show_overlay_cb").Unchecked += (_, _) => SetOverlayVisibility(false);
        Find<CheckBox>("v2_thin_markup_cb").Checked += (_, _) => ApplyMarkupLineWeights();
        Find<CheckBox>("v2_thin_markup_cb").Unchecked += (_, _) => ApplyMarkupLineWeights();
        Find<Button>("v2_apply_mapping_btn").Click += (_, _) => ApplyLayerOverride();
        Find<Button>("v2_confirm_marker_btn").Click += (_, _) => ConfirmLayerMapping();
        Find<Button>("v2_reclassify_marker_btn").Click += (_, _) => BeginLayerAdjustment();
        Find<Button>("v2_add_marker_btn").Click += (_, _) => AddSelectedClassificationSeed();
        Find<Button>("v2_delete_marker_btn").Click += (_, _) => DeleteLayerMapping();
        Find<Button>("v2_redraw_marker_btn").Click += (_, _) => SetAdjustMode("Redraw", "Redraw mode selected. Trace the intended geometry on the PDF.");
        Find<Button>("v2_split_marker_btn").Click += (_, _) => SetAdjustMode("Split / Merge", "Choose classifications to split or merge.");
        Find<Button>("v2_move_marker_btn").Click += (_, _) => SetAdjustMode("Move marker", "Drag any numbered marker to its correct PDF position.");
        Find<Button>("v2_ignore_marker_btn").Click += (_, _) => IgnoreLayerMapping();
        Find<Button>("v2_reset_mapping_btn").Click += (_, _) => ResetLayerMapping();
        Find<Button>("v2_pick_main_btn").Click += (_, _) => BeginVectorPick(PdfVectorClass.MainPipe);
        Find<Button>("v2_pick_branch_btn").Click += (_, _) => BeginVectorPick(PdfVectorClass.BranchPipe);
        Find<Button>("v2_pick_sprinkler_btn").Click += (_, _) => BeginVectorPick(PdfVectorClass.Sprinkler);
        Find<Button>("v2_layer_as_main_btn").Click += (_, _) =>
            AssignSelectedCadLayer(PdfVectorClass.MainPipe);
        Find<Button>("v2_layer_as_sprinkler_btn").Click += (_, _) =>
            AssignSelectedCadLayer(PdfVectorClass.Sprinkler);
        Find<Button>("v2_layer_as_branch_btn").Click += (_, _) =>
            AssignSelectedCadLayer(PdfVectorClass.BranchPipe);
        Find<Button>("v2_pick_similar_btn").Click += (_, _) => FindSimilarVectorGeometry();
        Find<Button>("v2_exclude_wrong_btn").Click += (_, _) => BeginExcludeWrongVectorObject();
        Find<Button>("v2_clear_exclusions_btn").Click += (_, _) => ClearVectorExclusions();
        Find<Button>("v2_remove_last_seed_btn").Click += (_, _) => RemoveLastVectorSeed();
        Find<Button>("v2_isolate_result_btn").Click += (_, _) => SetVectorIsolation(true);
        Find<Button>("v2_show_all_result_btn").Click += (_, _) => SetVectorIsolation(false);
        Find<Button>("v2_pdf_zoom_out_btn").Click += (_, _) => SetPdfZoom(_pdfZoom - 0.15);
        Find<Button>("v2_pdf_zoom_in_btn").Click += (_, _) => SetPdfZoom(_pdfZoom + 0.15);
        Find<Button>("v2_pdf_fit_btn").Click += (_, _) => SetPdfZoom(1.0);
        Find<Button>("v2_source_zoom_out_btn").Click += (_, _) => SetPdfZoom(_pdfZoom - 0.15);
        Find<Button>("v2_source_zoom_in_btn").Click += (_, _) => SetPdfZoom(_pdfZoom + 0.15);
        Find<Button>("v2_source_fit_btn").Click += (_, _) => SetPdfZoom(1.0);
        Find<Button>("v2_source_pan_btn").Click += (_, _) => FocusPdfViewer("Pan is active - drag or scroll inside the PDF.");
        Find<Button>("v2_pdf_pan_btn").Click += (_, _) => FocusPdfViewer("Pan is active - drag or scroll inside the PDF.");
        Find<Button>("v2_source_rotate_btn").Click += (_, _) => RotatePdf();
        Find<ScrollViewer>("v2_source_pdf_scroll").SizeChanged += (_, _) => UpdateRenderedPdfZoom();
        Find<ScrollViewer>("v2_layer_pdf_scroll").SizeChanged += (_, _) => UpdateRenderedPdfZoom();
        Find<ScrollViewer>("v2_layer_pdf_scroll").ScrollChanged += (_, _) => UpdateOverlayPlacement();
        foreach (string viewerName in new[] { "v2_source_pdf_scroll", "v2_layer_pdf_scroll" })
        {
            ScrollViewer viewer = Find<ScrollViewer>(viewerName);
            viewer.PreviewMouseWheel += (_, eventArgs) => ZoomPdfAtPointer(viewer, eventArgs);
            viewer.PreviewMouseDown += (_, eventArgs) => BeginPdfPan(viewer, eventArgs);
            viewer.PreviewMouseMove += (_, eventArgs) => PanPdf(viewer, eventArgs);
            viewer.PreviewMouseUp += (_, eventArgs) => EndPdfPan(viewer, eventArgs);
        }
        Canvas vectorCanvas = Find<Canvas>("v2_vector_selection_canvas");
        ScrollViewer layerViewer = Find<ScrollViewer>("v2_layer_pdf_scroll");
        WpfGrid layerHost = Find<WpfGrid>("v2_layer_pdf_host");
        vectorCanvas.PreviewMouseWheel += (_, eventArgs) => ZoomPdfAtPointer(layerViewer, eventArgs);
        vectorCanvas.PreviewMouseDown += (_, eventArgs) =>
        {
            if (eventArgs.ChangedButton == MouseButton.Left && _cadAreaScanMode)
            {
                BeginCadAreaDrag(eventArgs);
                return;
            }
            if (eventArgs.ChangedButton == MouseButton.Left && _removeAreaScanMode)
            {
                BeginRemoveAreaDrag(eventArgs);
                return;
            }
            if (eventArgs.ChangedButton == MouseButton.Middle)
                BeginPdfPan(layerViewer, eventArgs);
        };
        vectorCanvas.PreviewMouseMove += (_, eventArgs) =>
        {
            if (_cadAreaDragging)
                UpdateCadAreaDrag(eventArgs);
            else if (_removeAreaDragging)
                UpdateRemoveAreaDrag(eventArgs);
            else if (_panningViewer is not null)
                PanPdf(layerViewer, eventArgs);
            else
                HoverVectorAtPointer(eventArgs);
        };
        vectorCanvas.PreviewMouseUp += (_, eventArgs) =>
        {
            if (eventArgs.ChangedButton == MouseButton.Left && _cadAreaDragging)
            {
                CompleteCadAreaDrag(eventArgs);
                return;
            }
            if (eventArgs.ChangedButton == MouseButton.Left && _removeAreaDragging)
            {
                CompleteRemoveAreaDrag(eventArgs);
                return;
            }
            EndPdfPan(layerViewer, eventArgs);
        };
        vectorCanvas.MouseLeave += (_, _) => ClearVectorHover();
        layerHost.PreviewMouseLeftButtonDown += (_, eventArgs) => SelectVectorAtPointer(eventArgs);
        layerHost.PreviewMouseMove += (_, eventArgs) =>
        {
            if (_panningViewer is null)
                HoverVectorAtPointer(eventArgs);
        };
        _window.PreviewKeyDown += OnWindowPreviewKeyDown;
        Find<Button>("direct_drop_btn").Click += (_, _) => SelectConnection("direct_drop");
        Find<Button>("armover_btn").Click += (_, _) => SelectConnection("armover");
        Find<Button>("flexible_drop_btn").Click += (_, _) => SelectConnection("flexible_drop");
        Find<Button>("direct_rise_btn").Click += (_, _) => SelectConnection("direct_rise");
        Find<Button>("armover_rise_btn").Click += (_, _) => SelectConnection("armover_rise");
        HeadOrientation.SelectionChanged += (_, _) => OnOrientationChanged();
        Find<Button>("save_rule_btn").Click += (_, _) => SaveConnectionRule();
        Find<WpfComboBox>("v2_pipe_type_combo").SelectionChanged += (_, _) => UpdatePipeRoutingStatus();
        Find<Button>("auto_size_btn").Click += (_, _) => AutoSizePreview();
        MainTabs.SelectionChanged += (_, _) =>
        {
            UpdateNavigation();
            UpdateRenderedPdfVisibility();
        };
        Find<Button>("back_btn").Click += (_, _) => MoveTab(-1);
        Find<Button>("next_btn").Click += (_, _) => OnNextClicked();
        Find<Button>("create_verified_btn").Click += (_, _) => CreateVerified();
        Find<Button>("close_btn").Click += (_, _) => _window.Close();
    }

    private void LoadInitialData()
    {
        Find<TextBlock>("doc_name_text").Text = _document.Title;
        SetItems(Find<WpfComboBox>("v2_source_format_combo"), ["PDF", "DWG"], "PDF");
        SetItems(Find<WpfComboBox>("v2_system_type_combo"), ["Wet", "Dry", "Preaction"], "Wet");
        SetItems(Find<WpfComboBox>("v2_units_combo"), ["Millimeters", "Inches"], "Millimeters");
        SetItems(
            Find<WpfComboBox>("v2_cad_units_combo"),
            ["Millimeters", "Centimeters", "Meters", "Inches", "Feet"],
            "Millimeters");
        SetItems(
            Find<WpfComboBox>("v2_cad_recognition_mode_combo"),
            ["Native CAD layers", "PDF seed workflow"],
            "Native CAD layers");
        Find<TextBox>("v2_source_file_tb").Text = "Select a PDF or DWG source";

        List<string> levels = new FilteredElementCollector(_document)
            .OfClass(typeof(Level))
            .Cast<Level>()
            .OrderBy(level => level.Elevation)
            .Select(level => level.Name)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        if (levels.Count == 0) levels.Add("No levels found");
        SetItems(Find<WpfComboBox>("v2_link_level_combo"), levels, levels.First());

        _placementViews.Clear();
        _placementViews.AddRange(new FilteredElementCollector(_document)
            .OfClass(typeof(ViewPlan))
            .Cast<ViewPlan>()
            .Where(view => !view.IsTemplate && view.ViewType is
                ViewType.FloorPlan or ViewType.CeilingPlan or ViewType.EngineeringPlan)
            .OrderBy(view => view.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(view => new RevitViewOption(view.Id, view.Name, view.GenLevel?.Name ?? "No level")));
        WpfComboBox targetViewCombo = Find<WpfComboBox>("v2_target_view_combo");
        targetViewCombo.ItemsSource = _placementViews;
        targetViewCombo.SelectedItem = _placementViews.FirstOrDefault(option => option.Id == _document.ActiveView.Id)
                                       ?? _placementViews.FirstOrDefault();

        _sprinklerFamilies.Clear();
        _sprinklerFamilies.AddRange(new FilteredElementCollector(_document)
            .OfClass(typeof(FamilySymbol))
            .OfCategory(BuiltInCategory.OST_Sprinklers)
            .Cast<FamilySymbol>()
            .OrderBy(symbol => symbol.Family.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(symbol => symbol.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(symbol => new SprinklerFamilyOption(symbol.Id, symbol.Family.Name, symbol.Name)));
        WpfComboBox familyCombo = Find<WpfComboBox>("v2_sprinkler_family_combo");
        familyCombo.ItemsSource = _sprinklerFamilies;
        familyCombo.SelectedItem = _sprinklerFamilies.FirstOrDefault();

        SetItems(
            Find<WpfComboBox>("v2_sprinkler_reference_mode_combo"),
            [ElevationModeLevel, ElevationModeFloor, ElevationModeCeiling, ElevationModeReferencePlane],
            ElevationModeLevel);
        RefreshElevationReferenceTargets();

        _pipeTypes.Clear();
        _pipeTypes.AddRange(new FilteredElementCollector(_document)
            .OfClass(typeof(PipeType))
            .Cast<PipeType>()
            .OrderBy(type => type.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(type => new PipeTypeOption(
                type.Id,
                type.Name,
                type.RoutingPreferenceManager.GetNumberOfRules(RoutingPreferenceRuleGroupType.Junctions),
                type.RoutingPreferenceManager.GetNumberOfRules(RoutingPreferenceRuleGroupType.Transitions),
                type.RoutingPreferenceManager.GetNumberOfRules(RoutingPreferenceRuleGroupType.Elbows))));
        WpfComboBox pipeTypeCombo = Find<WpfComboBox>("v2_pipe_type_combo");
        pipeTypeCombo.ItemsSource = _pipeTypes;
        pipeTypeCombo.SelectedItem = _pipeTypes.FirstOrDefault();
        UpdatePipeRoutingStatus();
        string[] nominalDiameters =
        [
            "DN 15", "DN 20", "DN 25", "DN 32", "DN 40", "DN 50",
            "DN 65", "DN 80", "DN 100", "DN 125", "DN 150", "DN 200"
        ];
        SetItems(Find<WpfComboBox>("v2_main_dn_combo"), nominalDiameters, "DN 100");
        SetItems(Find<WpfComboBox>("v2_branch_dn_combo"), nominalDiameters, "DN 32");

        SetItems(HeadOrientation, ["Auto", "Pendent", "Upright", "Sidewall"], "Auto");
        SetItems(Find<WpfComboBox>("nearest_branch_combo"), ["Auto", "Above", "Below", "Same elevation"], "Auto");
        SetItems(
            Find<WpfComboBox>("fitting_set_combo"),
            ["Routing Preference (from Pipe Type)"],
            "Routing Preference (from Pipe Type)");

        SetItems(
            Find<WpfComboBox>("design_standard_combo"),
            ["NFPA 13 - 2025", "TCVN 7336:2021", "Project Custom"],
            "NFPA 13 - 2025");
        SetItems(
            Find<WpfComboBox>("hazard_combo"),
            ["Light Hazard", "Ordinary Hazard 1", "Ordinary Hazard 2", "Extra Hazard"],
            "Light Hazard");
        SetItems(
            Find<WpfComboBox>("sizing_method_combo"),
            ["Hydraulic", "Preliminary Schedule", "Manual Sizes"],
            "Hydraulic");
        SetItems(
            Find<WpfComboBox>("water_supply_combo"),
            ["Not Available", "Enter Manually", "Import Flow Test"],
            "Not Available");
        SetItems(
            Find<WpfComboBox>("k_factor_combo"),
            ["K 5.6 / K80", "K 8.0 / K115", "From Revit Family"],
            "K 5.6 / K80");

        SetItems(
            Find<WpfComboBox>("v2_mapping_category_combo"),
            ["Pipe", "Sprinkler", "Pipe Fitting", "Pipe Accessory", "Generic Model", "Ignore"],
            "Pipe");
        SetItems(
            Find<WpfComboBox>("v2_mapping_family_combo"),
            ["Wet Pipe - Main", "Wet Pipe - Branch", "Pendent / Upright", "Routing Preference", "Gate Valve", "-"],
            "Wet Pipe - Main");

        SetItems(
            Find<WpfComboBox>("v2_detection_rule_combo"),
            ["Symbol + endpoint", "Color + line type", "Layer / group", "Network topology"],
            "Symbol + endpoint");

        DataGrid layerGrid = Find<DataGrid>("v2_layer_grid");
        layerGrid.ItemsSource = _layerItems;
        layerGrid.SelectedIndex = 0;
        Find<DataGrid>("review_issue_grid").ItemsSource = _reviewItems;
        UpdateLayerSelection();
        SelectConnection("armover");
        MainTabs.SelectedIndex = 0;
        UpdateNavigation();
        Status.Text = "Ready - C# workflow loaded";
    }

    private void RefreshElevationReferenceTargets()
    {
        WpfComboBox modeCombo = Find<WpfComboBox>("v2_sprinkler_reference_mode_combo");
        string mode = modeCombo.SelectedItem as string ?? ElevationModeLevel;
        StackPanel targetPanel = Find<StackPanel>("v2_sprinkler_reference_target_panel");
        TextBlock targetLabel = Find<TextBlock>("v2_sprinkler_reference_target_label");
        TextBlock elevationLabel = Find<TextBlock>("v2_sprinkler_elevation_label");
        TextBlock hint = Find<TextBlock>("v2_sprinkler_elevation_hint");
        TextBox offsetBox = Find<TextBox>("v2_sprinkler_elevation_tb");
        WpfComboBox targetCombo = Find<WpfComboBox>("v2_sprinkler_reference_target_combo");
        bool modeChanged = !string.Equals(mode, _lastElevationMode, StringComparison.Ordinal);
        _lastElevationMode = mode;
        _elevationReferences.Clear();

        if (mode == ElevationModeFloor)
        {
            targetPanel.Visibility = WpfVisibility.Visible;
            targetLabel.Text = "Floor / slab below";
            elevationLabel.Text = "Offset above floor top";
            hint.Text = "Final elevation follows the actual top face of the selected/automatic floor";
            if (modeChanged) offsetBox.Text = "2700";
            _elevationReferences.Add(new ElevationReferenceOption(
                ElementId.InvalidElementId,
                "Auto - floor below each sprinkler",
                true));
            _elevationReferences.AddRange(new FilteredElementCollector(_document)
                .OfClass(typeof(Floor))
                .Cast<Floor>()
                .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(item => new ElevationReferenceOption(
                    item.Id,
                    $"{item.Name}  ·  Id {item.Id.CompatValue()}",
                    false)));
        }
        else if (mode == ElevationModeCeiling)
        {
            targetPanel.Visibility = WpfVisibility.Visible;
            targetLabel.Text = "Ceiling";
            elevationLabel.Text = "Offset below ceiling";
            hint.Text = "0 = align to ceiling underside; positive value moves the head downward";
            if (modeChanged) offsetBox.Text = "0";
            _elevationReferences.Add(new ElevationReferenceOption(
                ElementId.InvalidElementId,
                "Auto - ceiling above each sprinkler",
                true));
            _elevationReferences.AddRange(new FilteredElementCollector(_document)
                .OfClass(typeof(Ceiling))
                .Cast<Ceiling>()
                .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(item => new ElevationReferenceOption(
                    item.Id,
                    $"{item.Name}  ·  Id {item.Id.CompatValue()}",
                    false)));
        }
        else if (mode == ElevationModeReferencePlane)
        {
            targetPanel.Visibility = WpfVisibility.Visible;
            targetLabel.Text = "Named reference plane";
            elevationLabel.Text = "Signed offset from reference plane";
            hint.Text = "Positive = above plane; negative = below plane";
            if (modeChanged) offsetBox.Text = "0";
            _elevationReferences.AddRange(new FilteredElementCollector(_document)
                .OfClass(typeof(ReferencePlane))
                .Cast<ReferencePlane>()
                .Where(item => !string.IsNullOrWhiteSpace(item.Name))
                .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(item => new ElevationReferenceOption(
                    item.Id,
                    $"{item.Name}  ·  Id {item.Id.CompatValue()}",
                    false)));
        }
        else
        {
            targetPanel.Visibility = WpfVisibility.Collapsed;
            elevationLabel.Text = "Offset above selected Level / floor";
            hint.Text = "Final elevation = selected Level + offset";
            if (modeChanged) offsetBox.Text = "2700";
        }

        targetCombo.ItemsSource = null;
        targetCombo.ItemsSource = _elevationReferences;
        targetCombo.SelectedItem = _elevationReferences.FirstOrDefault();
    }

    private void BrowseSource()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select PDF or CAD spinkler source",
            Filter = "PDF or CAD (*.pdf;*.dwg;*.dxf)|*.pdf;*.dwg;*.dxf|All files (*.*)|*.*"
        };
        if (dialog.ShowDialog(_window) != true) return;

        _currentPdfPath = dialog.FileName;
        ClearScannedPlacementArea();
        _cadLayerByPathId = new Dictionary<int, string>();
        _cadLayerCounts = new Dictionary<string, int>();
        ClearCadRecognitionCache();
        PopulateCadLayerChooser();
        string fileName = Path.GetFileName(dialog.FileName);
        Find<TextBox>("v2_source_file_tb").Text = dialog.FileName;
        Find<TextBlock>("v2_source_document_name").Text = fileName;
        Find<TextBlock>("v2_pdf_file_name_text").Text = fileName;
        Find<WpfComboBox>("v2_source_format_combo").SelectedItem =
            string.Equals(Path.GetExtension(dialog.FileName), ".pdf", StringComparison.OrdinalIgnoreCase)
                ? "PDF"
                : "DWG";
        if (string.Equals(Path.GetExtension(dialog.FileName), ".pdf", StringComparison.OrdinalIgnoreCase))
        {
            ConfigureSourceUiForFormat(false);
            _ = NavigatePdfAsync(dialog.FileName);
            Find<TextBlock>("v2_source_quality_text").Text = "Ready to analyze";
            Find<TextBlock>("v2_source_quality_badge").Text = "NEW SOURCE";
        }
        else
        {
            _renderedPdfPage = null;
            _vectorScene = null;
            ClearVectorSourceState();
            ConfigureSourceUiForFormat(true);
            if (DwgNativePreviewRenderer.TryRender(
                    dialog.FileName,
                    out BitmapSource? nativePreview,
                    out string previewMessage) &&
                nativePreview is not null)
            {
                _renderedPdfPage = nativePreview;
                CacheCadPreviewPixels(nativePreview);
                _pdfRotation = 0;
                _pdfZoom = 1.0;
                Find<Image>("v2_source_pdf_image").Source = nativePreview;
                Find<Image>("v2_layer_pdf_image").Source = nativePreview;
                HidePdfPlaceholders();
                UpdatePdfZoomText();
                UpdateRenderedPdfVisibility();
                Find<TextBlock>("v2_source_quality_badge").Text = "CAD TRUE COLOR";
                Find<TextBlock>("v2_source_quality_text").Text =
                    "Preview ready. Select the DWG drawing units, then click Import and Analyze CAD.";
                Status.Text = "DWG preview ready - confirm its drawing units before importing.";
            }
            else
            {
                ShowPdfPlaceholder(previewMessage);
                Find<TextBlock>("v2_source_quality_badge").Text = "DWG SELECTED";
                Status.Text = "Select the DWG drawing units, then click Import and Analyze CAD.";
            }
            TryRestoreRememberedCadPlacement(dialog.FileName);
        }
    }

    private void OnCadDrawingUnitsChanged()
    {
        if (!IsCadSource) return;
        _vectorScene = null;
        _cadLayerByPathId = new Dictionary<int, string>();
        _cadLayerCounts = new Dictionary<string, int>();
        ClearCadRecognitionCache();
        PopulateCadLayerChooser();
        ClearVectorSourceState();
        string units = Find<WpfComboBox>("v2_cad_units_combo").SelectedItem as string
            ?? "Millimeters";
        Find<TextBlock>("v2_source_quality_text").Text =
            _alignedPdfInstanceId is null
                ? $"DWG scale set to {units}. It will apply only if a new CAD import is required."
                : $"Existing CAD will be reused. {units} applies only if a new import is required.";
        Status.Text = _alignedPdfInstanceId is null
            ? $"DWG units changed to {units}."
            : "Existing Revit CAD retained; no second import is required.";
    }

    private void OnCadScaleCorrectionChanged()
    {
        if (!IsCadSource) return;
        _vectorScene = null;
        _cadLayerByPathId = new Dictionary<int, string>();
        _cadLayerCounts = new Dictionary<string, int>();
        ClearCadRecognitionCache();
        PopulateCadLayerChooser();
        ClearVectorSourceState();
        string scale = Find<TextBox>("v2_cad_scale_factor_tb").Text;
        Find<TextBlock>("v2_source_quality_text").Text =
            _alignedPdfInstanceId is null
                ? $"DWG scale correction changed to {scale}. It applies to a new import only."
                : $"Existing CAD will be reused. Scale {scale} applies only to a new import.";
        Status.Text = _alignedPdfInstanceId is null
            ? "DWG scale changed."
            : "Existing Revit CAD retained; its placed scale and position are unchanged.";
    }

    private void ConfigureSourceUiForFormat(bool cad)
    {
        Find<FrameworkElement>("v2_cad_units_panel").Visibility = cad
            ? WpfVisibility.Visible
            : WpfVisibility.Collapsed;
        Find<FrameworkElement>("v2_cad_layer_direct_panel").Visibility = cad
            ? WpfVisibility.Visible
            : WpfVisibility.Collapsed;
        Find<Button>("v2_place_pdf_in_revit_btn").Content = cad
            ? "1  Use existing / import CAD in Revit"
            : "1  Place / select PDF in Revit";
        Find<Button>("v2_use_aligned_pdf_btn").Content = cad
            ? "2  Use selected CAD + read layers"
            : "2  Use aligned PDF position";
        Find<TextBlock>("v2_source_placeholder_title").Text = cad ? "DWG / DXF" : "PDF";
        Find<TextBlock>("v2_source_placeholder_hint").Text = cad
            ? "Import the CAD into the selected Revit view to preview real layers"
            : "Select a real PDF to preview";
        Find<TextBlock>("v2_source_document_meta").Text = cad ? "Model space / visible geometry" : "1 page";
        Find<Button>("v2_scan_source_btn").Content = cad ? "Import and Analyze CAD" : "Analyze Source";
        TextBlock placementStatus = Find<TextBlock>("v2_placement_area_status_text");
        placementStatus.Text = cad ? "CAD not imported / analyzed" : "PDF position not captured";
        placementStatus.Foreground = Brush("#C98200");
    }

    private void ClearVectorSourceState()
    {
        _vectorPicks.Clear();
        _additionalVectorPicks.Clear();
        _vectorExcludedPathIds.Clear();
        _vectorExcludedSegmentIds.Clear();
        _selectedVectorSegmentId = -1;
        _hoveredVectorSegmentId = -1;
        _pendingVectorClass = PdfVectorClass.None;
        _pendingExclusionClass = PdfVectorClass.None;
        _removeAreaScanMode = false;
        _removeAreaDragging = false;
        _lastAssignedVectorClass = PdfVectorClass.None;
        _isolateVectorResult = false;
    }

    private void ClearCadRecognitionCache()
    {
        _cadNativeColorBySegmentId = new Dictionary<int, int>();
        _cadPreviewPixels = null;
        _cadPreviewPixelWidth = 0;
        _cadPreviewPixelHeight = 0;
        _cadPreviewPixelStride = 0;
        _cadPixelSignatureCache.Clear();
        _cadColorCoverageCache.Clear();
        _cadColorChoices.Clear();
        _hiddenCadColors.Clear();
        _cadAreaScanMode = false;
        _cadAreaDragging = false;
        WpfComboBox? colorCombo = _window.FindName("v2_cad_color_combo") as WpfComboBox;
        if (colorCombo is not null)
            colorCombo.ItemsSource = null;
        if (_window.FindName("v2_restore_hidden_cad_colors_btn") is Button restoreButton)
        {
            restoreButton.Content = "Restore hidden (0)";
            restoreButton.IsEnabled = false;
        }
    }

    private void CacheCadPreviewPixels(BitmapSource bitmap)
    {
        BitmapSource source = bitmap;
        if (source.Format != PixelFormats.Bgra32)
        {
            var converted = new FormatConvertedBitmap();
            converted.BeginInit();
            converted.Source = source;
            converted.DestinationFormat = PixelFormats.Bgra32;
            converted.EndInit();
            converted.Freeze();
            source = converted;
        }
        _cadPreviewPixelWidth = source.PixelWidth;
        _cadPreviewPixelHeight = source.PixelHeight;
        _cadPreviewPixelStride = _cadPreviewPixelWidth * 4;
        _cadPreviewPixels = new byte[_cadPreviewPixelStride * _cadPreviewPixelHeight];
        source.CopyPixels(_cadPreviewPixels, _cadPreviewPixelStride, 0);
        _cadPixelSignatureCache.Clear();
        _cadColorCoverageCache.Clear();
    }

    private CadPixelSignature GetCadPixelSignature(int segmentId)
    {
        if (_cadPixelSignatureCache.TryGetValue(segmentId, out CadPixelSignature cached))
            return cached;
        if (_vectorScene is null || segmentId < 0 || segmentId >= _vectorScene.Segments.Length)
            return default;

        PdfVectorSegment segment = _vectorScene.Segments[segmentId];
        // Revit exposes the DWG subcategory color for every extracted vector.
        // It is deterministic and remains correct at crossings and at any zoom.
        // Sampling the bitmap first caused a gray architectural line to inherit
        // the blue/magenta color of a nearby MEP line.
        if (IsCadSource &&
            _cadNativeColorBySegmentId.TryGetValue(segmentId, out int vectorColor))
        {
            CadPixelSignature native = ToCadPixelSignature(
                (byte)((vectorColor >> 16) & 255),
                (byte)((vectorColor >> 8) & 255),
                (byte)(vectorColor & 255));
            // Many PDF-to-DWG files expose only a gray Revit subcategory even
            // though the original entities are blue/magenta. Trust native color
            // when it is chromatic; otherwise fall back to the true-color DWG
            // preview below instead of leaving the color chooser empty.
            if (native.IsChromatic)
            {
                _cadPixelSignatureCache[segmentId] = native;
                return native;
            }
        }

        CadPixelSignature best = default;
        if (_cadPreviewPixels is not null && _cadPreviewPixelWidth > 0 && _cadPreviewPixelHeight > 0)
        {
            var samples = new List<CadPixelSignature>();
            foreach (double position in new[] { 0.08, 0.18, 0.29, 0.40, 0.50, 0.60, 0.71, 0.82, 0.92 })
            {
                double normalizedX = segment.X1 + (segment.X2 - segment.X1) * position;
                double normalizedY = segment.Y1 + (segment.Y2 - segment.Y1) * position;
                int centerX = (int)Math.Round(normalizedX * (_cadPreviewPixelWidth - 1));
                int centerY = (int)Math.Round(normalizedY * (_cadPreviewPixelHeight - 1));
                CadPixelSignature pointBest = default;
                double pointBestScore = double.MinValue;
                for (int offsetY = -1; offsetY <= 1; offsetY++)
                for (int offsetX = -1; offsetX <= 1; offsetX++)
                {
                    int x = PortableMath.Clamp(centerX + offsetX, 0, _cadPreviewPixelWidth - 1);
                    int y = PortableMath.Clamp(centerY + offsetY, 0, _cadPreviewPixelHeight - 1);
                    int offset = y * _cadPreviewPixelStride + x * 4;
                    byte blue = _cadPreviewPixels[offset];
                    byte green = _cadPreviewPixels[offset + 1];
                    byte red = _cadPreviewPixels[offset + 2];
                    CadPixelSignature candidate = ToCadPixelSignature(red, green, blue);
                    double distance = Math.Sqrt(offsetX * offsetX + offsetY * offsetY);
                    // Prefer the pixel closest to the exact vector line. A highly
                    // saturated crossing two pixels away must not recolor a gray
                    // architectural line or a neighboring MEP line.
                    double score = candidate.Saturation * 0.85 + candidate.Value * 0.20 - distance * 0.82;
                    if (!candidate.IsChromatic || score < 0.08 || score <= pointBestScore) continue;
                    pointBest = candidate;
                    pointBestScore = score;
                }
                if (pointBest.IsChromatic) samples.Add(pointBest);
            }

            List<CadPixelSignature> dominant = samples
                .Select(anchor => samples.Where(sample => HueDistance(anchor.Hue, sample.Hue) <= 10.0).ToList())
                .OrderByDescending(cluster => cluster.Count)
                .FirstOrDefault() ?? [];
            // The chosen color must cover most of the vector length. This rejects
            // text strokes, fittings and gray lines that merely cross an MEP run.
            // Four consistent samples are enough for short CAD fragments and
            // anti-aliased lines. Random crossings normally contribute only one
            // or two samples and are still rejected.
            if (dominant.Count >= 4)
            {
                double sin = dominant.Average(sample => Math.Sin(sample.Hue * Math.PI / 180.0));
                double cos = dominant.Average(sample => Math.Cos(sample.Hue * Math.PI / 180.0));
                double hue = Math.Atan2(sin, cos) * 180.0 / Math.PI;
                if (hue < 0) hue += 360.0;
                best = new CadPixelSignature(
                    hue,
                    dominant.Average(sample => sample.Saturation),
                    dominant.Average(sample => sample.Value),
                    true);
            }
        }

        _cadPixelSignatureCache[segmentId] = best;
        return best;
    }

    private static CadPixelSignature ToCadPixelSignature(byte red, byte green, byte blue)
    {
        double r = red / 255.0;
        double g = green / 255.0;
        double b = blue / 255.0;
        double maximum = Math.Max(r, Math.Max(g, b));
        double minimum = Math.Min(r, Math.Min(g, b));
        double chroma = maximum - minimum;
        double hue = 0;
        if (chroma > 1e-6)
        {
            if (Math.Abs(maximum - r) < 1e-6)
                hue = 60.0 * (((g - b) / chroma) % 6.0);
            else if (Math.Abs(maximum - g) < 1e-6)
                hue = 60.0 * (((b - r) / chroma) + 2.0);
            else
                hue = 60.0 * (((r - g) / chroma) + 4.0);
        }
        if (hue < 0) hue += 360.0;
        double saturation = maximum <= 1e-6 ? 0 : chroma / maximum;
        bool chromatic = saturation >= 0.42 && maximum >= 0.26 && chroma >= 0.16;
        return new CadPixelSignature(hue, saturation, maximum, chromatic);
    }

    private static double HueDistance(double first, double second)
    {
        double distance = Math.Abs(first - second);
        return Math.Min(distance, 360.0 - distance);
    }

    private void PopulateCadLayerChooser()
    {
        WpfComboBox combo = Find<WpfComboBox>("v2_cad_layer_combo");
        CadLayerChoice[] choices = _cadLayerCounts
            .OrderByDescending(item => item.Value)
            .ThenBy(item => item.Key, StringComparer.CurrentCultureIgnoreCase)
            .Select(item => new CadLayerChoice(item.Key, item.Value))
            .ToArray();
        combo.ItemsSource = choices;
        combo.SelectedIndex = choices.Length > 0 ? 0 : -1;
        if (choices.Length > 0)
        {
            WpfComboBox mode = Find<WpfComboBox>("v2_cad_recognition_mode_combo");
            mode.SelectedItem = choices.Length <= 2
                ? "PDF seed workflow"
                : "Native CAD layers";
        }
        PopulateCadColorChoices();
        UpdateCadRecognitionModeHint();
    }

    private void PopulateCadColorChoices()
    {
        _cadColorChoices.Clear();
        WpfComboBox combo = Find<WpfComboBox>("v2_cad_color_combo");
        if (_vectorScene is null || _vectorScene.Segments.Length == 0)
        {
            combo.ItemsSource = null;
            UpdateCadColorSwatch();
            return;
        }

        int step = Math.Max(1, _vectorScene.Segments.Length / 8000);
        var buckets = new Dictionary<(int Hue, int Saturation, int Value), List<CadPixelSignature>>();
        for (int id = 0; id < _vectorScene.Segments.Length; id += step)
        {
            CadPixelSignature signature = GetCadPixelSignature(id);
            if (!signature.IsChromatic) continue;
            var bucket = (
                ((int)Math.Round(signature.Hue / 8.0)) % 45,
                (int)Math.Round(signature.Saturation / 0.12),
                (int)Math.Round(signature.Value / 0.12));
            if (!buckets.TryGetValue(bucket, out List<CadPixelSignature>? values))
                buckets[bucket] = values = [];
            values.Add(signature);
        }

        foreach (List<CadPixelSignature> values in buckets.Values
                     .Where(values => values.Count >= 2)
                     .OrderByDescending(values => values.Count)
                     .Take(16))
        {
            double sin = values.Average(value => Math.Sin(value.Hue * Math.PI / 180.0));
            double cos = values.Average(value => Math.Cos(value.Hue * Math.PI / 180.0));
            double hue = Math.Atan2(sin, cos) * 180.0 / Math.PI;
            if (hue < 0) hue += 360.0;
            var signature = new CadPixelSignature(
                hue,
                values.Average(value => value.Saturation),
                values.Average(value => value.Value),
                true);
            WpfColor displayColor = ColorFromHsv(signature);
            string hex = $"#{displayColor.R:X2}{displayColor.G:X2}{displayColor.B:X2}";
            _cadColorChoices.Add(new CadColorChoice(
                $"{CadColorName(hue)} {hex}",
                signature,
                values.Count * step));
        }

        RefreshCadColorChoices();
    }

    private void RefreshCadColorChoices(CadColorChoice? preferred = null)
    {
        WpfComboBox combo = Find<WpfComboBox>("v2_cad_color_combo");
        CadColorChoice? previous = preferred ?? combo.SelectedItem as CadColorChoice;
        CadColorChoice[] visible = _cadColorChoices
            .Where(choice => !IsCadColorHidden(choice.Signature))
            .ToArray();
        combo.ItemsSource = null;
        combo.ItemsSource = visible;
        CadColorChoice? selection = previous is null
            ? null
            : visible.FirstOrDefault(choice =>
                IsSameCadColorChoice(previous.Signature, choice.Signature));
        combo.SelectedItem = selection ?? visible.FirstOrDefault();
        UpdateCadColorSwatch();

        Button restore = Find<Button>("v2_restore_hidden_cad_colors_btn");
        restore.Content = $"Restore hidden ({_hiddenCadColors.Count:N0})";
        restore.IsEnabled = _hiddenCadColors.Count > 0;
        Find<Button>("v2_hide_selected_cad_color_btn").IsEnabled = combo.SelectedItem is CadColorChoice;
    }

    private void HideSelectedCadColor()
    {
        if (!IsCadSource ||
            Find<WpfComboBox>("v2_cad_color_combo").SelectedItem is not CadColorChoice choice)
        {
            Status.Text = "Select a CAD color before hiding it.";
            return;
        }

        if (!IsCadColorHidden(choice.Signature))
            _hiddenCadColors.Add(choice.Signature);
        RefreshCadColorChoices();
        ApplyHiddenCadColorPreview();
        ApplyCadArchitectureVisibility();
        Status.Text = $"Hidden {choice.Name} from the preview and all future CAD scans.";
        Find<TextBlock>("v2_vector_pick_text").Text =
            _cadColorChoices.Count == _hiddenCadColors.Count
                ? "All detected CAD colors are hidden. Use Restore hidden colors to scan again."
                : $"{choice.Name} is hidden. You can hide more colors, then sweep the remaining MEP geometry.";
    }

    private void RestoreHiddenCadColors()
    {
        if (_hiddenCadColors.Count == 0) return;
        int restored = _hiddenCadColors.Count;
        _hiddenCadColors.Clear();
        RefreshCadColorChoices();
        ApplyHiddenCadColorPreview();
        ApplyCadArchitectureVisibility();
        Status.Text = $"Restored {restored:N0} hidden CAD color(s).";
        Find<TextBlock>("v2_vector_pick_text").Text =
            "All detected CAD colors are visible and selectable again.";
    }

    private static bool IsSameCadColorChoice(CadPixelSignature first, CadPixelSignature second) =>
        HueDistance(first.Hue, second.Hue) <= 1.0 &&
        Math.Abs(first.Saturation - second.Saturation) <= 0.04 &&
        Math.Abs(first.Value - second.Value) <= 0.04;

    private bool IsCadColorHidden(CadPixelSignature signature) =>
        signature.IsChromatic &&
        _hiddenCadColors.Any(hidden => IsSimilarCadColor(hidden, signature));

    private bool IsCadSegmentHidden(int segmentId) =>
        IsCadColorHidden(GetCadPixelSignature(segmentId));

    private void ApplyHiddenCadColorPreview()
    {
        if (!IsCadSource || _renderedPdfPage is null) return;
        BitmapSource preview = _renderedPdfPage;
        if (_hiddenCadColors.Count > 0 &&
            _cadPreviewPixels is not null &&
            _cadPreviewPixelWidth > 0 &&
            _cadPreviewPixelHeight > 0)
        {
            byte[] pixels = (byte[])_cadPreviewPixels.Clone();
            for (int offset = 0; offset + 3 < pixels.Length; offset += 4)
            {
                CadPixelSignature pixel = ToCadPixelSignature(
                    pixels[offset + 2],
                    pixels[offset + 1],
                    pixels[offset]);
                if (!IsCadColorHidden(pixel)) continue;
                pixels[offset] = 0;
                pixels[offset + 1] = 0;
                pixels[offset + 2] = 0;
                pixels[offset + 3] = 0;
            }
            preview = BitmapSource.Create(
                _cadPreviewPixelWidth,
                _cadPreviewPixelHeight,
                _renderedPdfPage.DpiX,
                _renderedPdfPage.DpiY,
                PixelFormats.Bgra32,
                null,
                pixels,
                _cadPreviewPixelStride);
            preview.Freeze();
        }
        Find<Image>("v2_source_pdf_image").Source = preview;
        Find<Image>("v2_layer_pdf_image").Source = preview;
    }

    private void UpdateCadColorSwatch()
    {
        Border swatch = Find<Border>("v2_cad_color_swatch");
        if (Find<WpfComboBox>("v2_cad_color_combo").SelectedItem is not CadColorChoice choice)
        {
            swatch.Background = Brush("#D7E0E5");
            return;
        }
        swatch.Background = new SolidColorBrush(ColorFromHsv(choice.Signature));
    }

    private bool HideCadArchitectureDuringScan =>
        IsCadSource && IsSmartCadRecognition() &&
        Find<CheckBox>("v2_cad_hide_architecture_cb").IsChecked == true;

    private bool IsSelectedCadScanColor(int segmentId)
    {
        if (IsCadSegmentHidden(segmentId)) return false;
        if (!HideCadArchitectureDuringScan) return true;
        return Find<WpfComboBox>("v2_cad_color_combo").SelectedItem is CadColorChoice selected &&
               IsSimilarCadColor(selected.Signature, GetCadPixelSignature(segmentId));
    }

    private void ApplyCadArchitectureVisibility(bool showStatus = false)
    {
        if (_disposed) return;
        bool hide = HideCadArchitectureDuringScan;
        UpdateCadRasterContextOpacity();
        ApplyMarkupLineWeights();
        ClearVectorHover();
        if (!showStatus) return;
        Status.Text = hide
            ? "Architecture hidden: only the selected strong CAD color can be picked or swept."
            : "Architecture restored: every visible CAD color can be picked.";
        Find<TextBlock>("v2_vector_pick_text").Text = hide
            ? "Architecture filter ON. Choose a MEP color, then Sweep area. Sprinklers are matched by closed/repeated shape, not by straight pipe lines."
            : "Architecture filter OFF. The complete CAD preview is available for picking.";
    }

    private bool IsSmartCadRecognition() =>
        IsCadSource && string.Equals(
            Find<WpfComboBox>("v2_cad_recognition_mode_combo").SelectedItem as string,
            "PDF seed workflow",
            StringComparison.Ordinal);

    private void UpdateCadRecognitionModeHint()
    {
        TextBlock hint = Find<TextBlock>("v2_cad_layer_direct_hint");
        FrameworkElement nativeAssignment = Find<FrameworkElement>("v2_native_layer_assignment_panel");
        FrameworkElement colorScan = Find<FrameworkElement>("v2_cad_color_scan_panel");
        bool smart = IsSmartCadRecognition();
        nativeAssignment.Visibility = smart ? WpfVisibility.Collapsed : WpfVisibility.Visible;
        colorScan.Visibility = smart ? WpfVisibility.Visible : WpfVisibility.Collapsed;
        Find<Button>("v2_pick_similar_btn").Content = "✦  Find similar";
        if (_cadLayerCounts.Count == 0)
        {
            hint.Text = "Import and analyze CAD to load vector geometry.";
            return;
        }
        hint.Text = smart
            ? $"PDF workflow: {_cadLayerCounts.Count:N0} flattened layer(s). Pick one exact sample, press Tab for Line/Object/Similar style, then review Isolate. Color and line weight must match the seed."
            : $"{_cadLayerCounts.Count:N0} native CAD layer(s). Select one and assign it directly.";
        ApplyCadArchitectureVisibility();
    }

    private void BeginCadAreaSweep()
    {
        if (_removeAreaScanMode || _removeAreaDragging)
            CancelRemoveAreaMode("Switching from Remove/Sweep to CAD area scan.");
        // A new sweep must start from the complete drawing. Keeping Isolate on
        // made the command appear inactive because only the previous result was
        // visible while the user tried to draw the next review window.
        if (_isolateVectorResult)
            SetVectorIsolation(false);
        if (!IsSmartCadRecognition() || _vectorScene is null)
        {
            Status.Text = "Use PDF seed workflow after importing the CAD.";
            return;
        }
        if (Find<WpfComboBox>("v2_cad_color_combo").SelectedItem is not CadColorChoice)
        {
            Status.Text = "No strong CAD color was detected. Check that the DWG preview uses its native colors.";
            return;
        }
        PdfVectorClass vectorClass = CurrentVectorClass();
        if (vectorClass == PdfVectorClass.None)
        {
            Status.Text = "Select Main, Sprinkler, or Branch in the analyzed-layer list first.";
            return;
        }

        RestoreDefaultMappingForPickedClass(vectorClass, explicitPick: true);
        _cadAreaScanMode = true;
        _cadAreaDragging = false;
        _pendingVectorClass = PdfVectorClass.None;
        _pendingExclusionClass = PdfVectorClass.None;
        Canvas canvas = Find<Canvas>("v2_vector_selection_canvas");
        canvas.Cursor = Cursors.Cross;
        canvas.Focus();
        Button button = Find<Button>("v2_cad_scan_area_btn");
        button.Background = Brush("#DFF6F5");
        button.BorderBrush = Brush("#00A7A7");
        button.BorderThickness = new Thickness(2);
        string label = VectorClassLabel(vectorClass);
        Find<TextBlock>("v2_vector_pick_text").Text =
            vectorClass == PdfVectorClass.Sprinkler
                ? "Sweep one sprinkler symbol: include the complete round/closed head shape. Straight Main and Branch lines of the same color are ignored."
                : $"Sweep {label}: drag a rectangle around the required CAD line(s). Only exact-color line geometry inside the rectangle is classified.";
        Status.Text = $"CAD area scan active: {label}. Drag over the preview.";
    }

    private void ScanCadWholeDrawing()
    {
        PdfVectorClass vectorClass = CurrentVectorClass();
        if (vectorClass != PdfVectorClass.Sprinkler)
        {
            Status.Text = "Main and Branch use Sweep area. Drag a rectangle around the required exact-color CAD pipe lines.";
            Find<TextBlock>("v2_vector_pick_text").Text =
                "Pipe scan is limited to the swept rectangle and never expands to the whole drawing.";
            return;
        }
        ApplyCadColorAreaScan(new Rect(0, 0, 1, 1));
    }

    private void UpdateCadScanControls(PdfVectorClass vectorClass)
    {
        bool sprinkler = vectorClass == PdfVectorClass.Sprinkler;
        Find<TextBlock>("v2_cad_scan_mode_label").Text = sprinkler
            ? "Sprinkler geometry / block scan"
            : "Main / Branch exact-color area scan";
        Find<Button>("v2_cad_scan_area_btn").Content = sprinkler
            ? "▧  Sweep symbol"
            : "▧  Sweep area";
        Button whole = Find<Button>("v2_cad_scan_whole_btn");
        whole.Content = sprinkler ? "◉  Find same blocks" : "◉  Line only";
        whole.IsEnabled = sprinkler;
        whole.ToolTip = sprinkler
            ? "Find repeated sprinkler geometry matching the swept symbol"
            : "Disabled for pipes: use Sweep area to control the exact review region";
    }

    private void BeginCadAreaDrag(MouseButtonEventArgs eventArgs)
    {
        if (!_cadAreaScanMode) return;
        Canvas canvas = Find<Canvas>("v2_vector_selection_canvas");
        _cadAreaStart = ClampToCanvas(eventArgs.GetPosition(canvas), canvas);
        _cadAreaDragging = true;
        if (_cadSweepLine is not null)
            _cadSweepLine.Visibility = WpfVisibility.Collapsed;
        if (_cadAreaRectangle is not null)
        {
            Canvas.SetLeft(_cadAreaRectangle, _cadAreaStart.X);
            Canvas.SetTop(_cadAreaRectangle, _cadAreaStart.Y);
            _cadAreaRectangle.Width = 0;
            _cadAreaRectangle.Height = 0;
            _cadAreaRectangle.Visibility = WpfVisibility.Visible;
        }
        canvas.CaptureMouse();
        eventArgs.Handled = true;
    }

    private void UpdateCadAreaDrag(MouseEventArgs eventArgs)
    {
        if (!_cadAreaDragging) return;
        Canvas canvas = Find<Canvas>("v2_vector_selection_canvas");
        System.Windows.Point current = ClampToCanvas(eventArgs.GetPosition(canvas), canvas);
        if (_cadAreaRectangle is null) return;
        double left = Math.Min(_cadAreaStart.X, current.X);
        double top = Math.Min(_cadAreaStart.Y, current.Y);
        Canvas.SetLeft(_cadAreaRectangle, left);
        Canvas.SetTop(_cadAreaRectangle, top);
        _cadAreaRectangle.Width = Math.Abs(current.X - _cadAreaStart.X);
        _cadAreaRectangle.Height = Math.Abs(current.Y - _cadAreaStart.Y);
        eventArgs.Handled = true;
    }

    private void CompleteCadAreaDrag(MouseButtonEventArgs eventArgs)
    {
        Canvas canvas = Find<Canvas>("v2_vector_selection_canvas");
        System.Windows.Point current = ClampToCanvas(eventArgs.GetPosition(canvas), canvas);
        canvas.ReleaseMouseCapture();
        _cadAreaDragging = false;
        _cadAreaScanMode = false;
        canvas.Cursor = Cursors.Arrow;
        ResetCadAreaScanButton();
        if (_cadAreaRectangle is not null)
            _cadAreaRectangle.Visibility = WpfVisibility.Collapsed;
        if (_cadSweepLine is not null)
            _cadSweepLine.Visibility = WpfVisibility.Collapsed;

        double pixelWidth = Math.Abs(current.X - _cadAreaStart.X);
        double pixelHeight = Math.Abs(current.Y - _cadAreaStart.Y);
        bool sprinklerScan = CurrentVectorClass() == PdfVectorClass.Sprinkler;
        if (pixelWidth < 8 || pixelHeight < 8)
        {
            Status.Text = sprinklerScan
                ? "Sweep cancelled: drag a larger rectangle around one sprinkler symbol."
                : "Sweep cancelled: drag a larger rectangle around the required pipe lines.";
            eventArgs.Handled = true;
            return;
        }
        var bounds = new Rect(
            Math.Min(_cadAreaStart.X, current.X) / Math.Max(canvas.Width, 1),
            Math.Min(_cadAreaStart.Y, current.Y) / Math.Max(canvas.Height, 1),
            pixelWidth / Math.Max(canvas.Width, 1),
            pixelHeight / Math.Max(canvas.Height, 1));
        var normalizedStart = new System.Windows.Point(
            _cadAreaStart.X / Math.Max(canvas.Width, 1),
            _cadAreaStart.Y / Math.Max(canvas.Height, 1));
        var normalizedEnd = new System.Windows.Point(
            current.X / Math.Max(canvas.Width, 1),
            current.Y / Math.Max(canvas.Height, 1));
        ApplyCadColorAreaScan(bounds, normalizedStart, normalizedEnd);
        eventArgs.Handled = true;
    }

    private void ResetCadAreaScanButton()
    {
        Button button = Find<Button>("v2_cad_scan_area_btn");
        button.ClearValue(System.Windows.Controls.Control.BackgroundProperty);
        button.ClearValue(System.Windows.Controls.Control.BorderBrushProperty);
        button.ClearValue(System.Windows.Controls.Control.BorderThicknessProperty);
    }

    private static System.Windows.Point ClampToCanvas(System.Windows.Point point, Canvas canvas) =>
        new(
            PortableMath.Clamp(point.X, 0, Math.Max(canvas.Width, 0)),
            PortableMath.Clamp(point.Y, 0, Math.Max(canvas.Height, 0)));

    private void ApplyCadColorAreaScan(
        Rect bounds,
        System.Windows.Point? sweepStart = null,
        System.Windows.Point? sweepEnd = null)
    {
        _cadAreaScanMode = false;
        _cadAreaDragging = false;
        Canvas scanCanvas = Find<Canvas>("v2_vector_selection_canvas");
        scanCanvas.ReleaseMouseCapture();
        scanCanvas.Cursor = Cursors.Arrow;
        ResetCadAreaScanButton();
        if (_cadAreaRectangle is not null)
            _cadAreaRectangle.Visibility = WpfVisibility.Collapsed;
        if (_cadSweepLine is not null)
            _cadSweepLine.Visibility = WpfVisibility.Collapsed;
        if (_vectorScene is null || !IsSmartCadRecognition())
        {
            Status.Text = "Analyze the CAD in PDF seed workflow mode first.";
            return;
        }
        if (Find<WpfComboBox>("v2_cad_color_combo").SelectedItem is not CadColorChoice color)
        {
            Status.Text = "Select a detected MEP color first.";
            return;
        }
        PdfVectorClass vectorClass = CurrentVectorClass();
        if (vectorClass == PdfVectorClass.None)
        {
            Status.Text = "Select Main, Sprinkler, or Branch first.";
            return;
        }

        bool lineStrokeScan = false;
        double lineToleranceMillimeters = 0;
        int seedId = FindCadAreaSeed(vectorClass, color.Signature, bounds);
        if (seedId < 0)
        {
            Status.Text = $"No {VectorClassLabel(vectorClass)} geometry matching {color.Name} was found in the swept area.";
            return;
        }

        RestoreDefaultMappingForPickedClass(vectorClass, explicitPick: true);
        _selectedVectorSegmentId = seedId;
        _vectorSelectionScope = PdfVectorSelectionScope.SimilarStyle;
        _activeVectorPick = null;
        AssignCurrentVectorPick(vectorClass, keepPickMode: true);
        if (_activeVectorPick is null) return;
        _activeVectorPick.ColorAreaScan = true;
        _activeVectorPick.ScanLeft = bounds.Left;
        _activeVectorPick.ScanTop = bounds.Top;
        _activeVectorPick.ScanRight = bounds.Right;
        _activeVectorPick.ScanBottom = bounds.Bottom;
        _activeVectorPick.ScanHue = color.Signature.Hue;
        _activeVectorPick.ScanSaturation = color.Signature.Saturation;
        _activeVectorPick.ScanValue = color.Signature.Value;
        _activeVectorPick.LineStrokeScan = lineStrokeScan;
        _activeVectorPick.ScanStartX = sweepStart?.X ?? 0;
        _activeVectorPick.ScanStartY = sweepStart?.Y ?? 0;
        _activeVectorPick.ScanEndX = sweepEnd?.X ?? 0;
        _activeVectorPick.ScanEndY = sweepEnd?.Y ?? 0;
        _activeVectorPick.ScanToleranceMillimeters = lineToleranceMillimeters;
        _activeVectorPick.SmartFlattened = true;
        _activeVectorPick.ExactCadLayer = false;

        ApplyExclusiveVectorOwnership(vectorClass);
        UpdateVectorClassItem(vectorClass);
        UpdateVectorPickSummary();
        RefreshFittingTopology();
        RedrawVectorHighlights();
        SaveMappingSidecar();
        SetVectorIsolation(true);
        int detected = CountVectorDetected(vectorClass);
        Find<TextBlock>("v2_vector_pick_text").Text =
            vectorClass == PdfVectorClass.Sprinkler
                ? $"Geometry/block scan: {color.Name}, {detected:N0} repeated sprinkler symbol(s). Straight pipe lines were ignored."
                : $"Area scan: {color.Name}, {detected:N0} exact-color {VectorClassLabel(vectorClass)} line vector(s) inside the swept rectangle.";
        Status.Text = vectorClass == PdfVectorClass.Sprinkler
            ? $"CAD sprinkler geometry scan complete: {color.Name}."
            : $"CAD exact {VectorClassLabel(vectorClass)} line run captured.";
    }

    private int FindCadAreaSeed(PdfVectorClass vectorClass, CadPixelSignature color, Rect bounds)
    {
        if (_vectorScene is null) return -1;
        IEnumerable<PdfVectorSegment> candidates = _vectorScene.Segments.Where(segment =>
            IsSegmentInBounds(segment, bounds) &&
            IsCadScanColorFamily(segment.Id, color) &&
            GetCadScanFamilyCoverage(segment.Id, color) >= 0.66);
        if (vectorClass == PdfVectorClass.Sprinkler)
        {
            return candidates
                .GroupBy(segment => segment.PathId)
                .Select(group => group.First())
                .Select(segment => (Segment: segment, Metrics: _vectorScene.GetPathMetrics(segment.Id)))
                .Where(item => IsSprinklerShapeCandidate(item.Metrics))
                .OrderByDescending(item => item.Metrics.IsClosed)
                .ThenBy(item => Math.Abs(Math.Log(PortableMath.Clamp(item.Metrics.AspectRatio, 0.01, 100))))
                .ThenByDescending(item => item.Metrics.SegmentCount)
                .Select(item => item.Segment.Id)
                .DefaultIfEmpty(-1)
                .First();
        }
        return candidates
            .Where(segment => IsPipeScanCandidate(segment, vectorClass))
            .OrderByDescending(PhysicalCadSegmentLength)
            .Select(segment => segment.Id)
            .DefaultIfEmpty(-1)
            .First();
    }

    private int FindCadLineSweepSeed(
        PdfVectorClass vectorClass,
        CadPixelSignature color,
        System.Windows.Point start,
        System.Windows.Point end,
        double toleranceMillimeters)
    {
        if (_vectorScene is null) return -1;
        return _vectorScene.Segments
            .Where(segment =>
                IsSimilarCadColor(color, GetCadPixelSignature(segment.Id)) &&
                GetCadColorCoverage(segment.Id, color) >= 0.66 &&
                IsPipeScanCandidate(segment, vectorClass) &&
                DistanceBetweenCadSegmentsMillimeters(segment, start, end) <= toleranceMillimeters)
            .OrderBy(segment => DistanceBetweenCadSegmentsMillimeters(segment, start, end))
            .ThenByDescending(PhysicalCadSegmentLength)
            .Select(segment => segment.Id)
            .DefaultIfEmpty(-1)
            .First();
    }

    private bool IsPipeScanCandidate(
        PdfVectorSegment segment,
        PdfVectorClass vectorClass,
        double seedLength = 0)
    {
        double minimumLength = vectorClass == PdfVectorClass.MainPipe ? 250.0 : 80.0;
        if (seedLength > 0)
        {
            // A flattened PDF-to-DWG often draws sprinkler heads, ticks and
            // fitting glyphs with the very same ACI color as the pipe.  Those
            // pieces are normally only a small fraction of the swept pipe
            // sample.  Compare every candidate with that sample instead of
            // trusting the flattened CAD path, which may contain the complete
            // drawing.
            double relativeMinimum = vectorClass == PdfVectorClass.MainPipe
                ? seedLength * 0.085
                : seedLength * 0.028;
            minimumLength = Math.Max(minimumLength, relativeMinimum);
        }
        if (_vectorScene is null || PhysicalCadSegmentLength(segment) < minimumLength) return false;
        PdfVectorPathMetrics metrics = _vectorScene.GetPathMetrics(segment.Id);
        double physicalWidth = metrics.Width * _vectorScene.PageWidth;
        double physicalHeight = metrics.Height * _vectorScene.PageHeight;
        double diagonal = Math.Sqrt(
            Math.Pow(physicalWidth, 2) +
            Math.Pow(physicalHeight, 2));
        if (metrics.IsClosed) return false;
        bool compactSymbol = metrics.SegmentCount >= 3 &&
                             diagonal <= Math.Max(900.0, minimumLength * 2.2) &&
                             metrics.AspectRatio is >= 0.15 and <= 6.5;
        return !compactSymbol && IsCadOrthogonal(segment);
    }

    private bool IsSprinklerShapeCandidate(PdfVectorPathMetrics metrics)
    {
        if (_vectorScene is null || metrics.Width <= 0 || metrics.Height <= 0) return false;
        double physicalWidth = metrics.Width * _vectorScene.PageWidth;
        double physicalHeight = metrics.Height * _vectorScene.PageHeight;
        double diagonal = Math.Sqrt(
            Math.Pow(physicalWidth, 2) +
            Math.Pow(physicalHeight, 2));
        // A sprinkler seed must occupy area in both X and Y.  A pipe, even when
        // it has the identical ACI color, is a one-dimensional path and is never
        // accepted as a sprinkler.  Multi-segment non-closed shapes are retained
        // for cross/rosette head symbols exported without a formally closed arc.
        bool twoDimensionalSymbol = physicalWidth >= 2.0 && physicalHeight >= 2.0;
        bool curvedOrComposite = metrics.IsClosed || metrics.SegmentCount >= 4;
        return diagonal is >= 1.0 and <= 250.0 &&
               twoDimensionalSymbol &&
               metrics.AspectRatio is >= 0.32 and <= 3.2 &&
               curvedOrComposite;
    }

    private static bool IsSegmentInBounds(PdfVectorSegment segment, Rect bounds)
    {
        double minX = Math.Min(segment.X1, segment.X2);
        double maxX = Math.Max(segment.X1, segment.X2);
        double minY = Math.Min(segment.Y1, segment.Y2);
        double maxY = Math.Max(segment.Y1, segment.Y2);
        // Pipe candidates are near-horizontal/vertical, so this also catches a
        // long line crossing a narrow sweep even when both endpoints and its
        // midpoint fall outside the rectangle.
        return maxX >= bounds.Left && minX <= bounds.Right &&
               maxY >= bounds.Top && minY <= bounds.Bottom;
    }

    private void AssignSelectedCadLayer(PdfVectorClass vectorClass)
    {
        if (!IsCadSource || _vectorScene is null)
        {
            Status.Text = "Import and analyze the CAD before assigning a native layer.";
            return;
        }
        if (Find<WpfComboBox>("v2_cad_layer_combo").SelectedItem is not CadLayerChoice choice)
        {
            Status.Text = "Select a CAD layer first.";
            return;
        }
        int pathId = _cadLayerByPathId
            .Where(item => string.Equals(item.Value, choice.Name, StringComparison.OrdinalIgnoreCase))
            .Select(item => item.Key)
            .DefaultIfEmpty(-1)
            .First();
        if (pathId < 0)
        {
            Status.Text = $"No selectable geometry was found on CAD layer {choice.Name}.";
            return;
        }
        int segmentId = Array.FindIndex(
            _vectorScene.Segments,
            segment => segment.PathId == pathId);
        if (segmentId < 0)
        {
            Status.Text = $"CAD layer {choice.Name} contains no vector segments.";
            return;
        }

        RestoreDefaultMappingForPickedClass(vectorClass, explicitPick: true);
        _selectedVectorSegmentId = segmentId;
        _vectorSelectionScope = PdfVectorSelectionScope.SimilarStyle;
        _pendingVectorClass = PdfVectorClass.None;
        _activeVectorPick = null;
        AssignCurrentVectorPick(vectorClass, exactCadLayer: true);
        SetVectorIsolation(true);
        Status.Text =
            $"CAD layer {choice.Name} assigned directly to {VectorClassLabel(vectorClass)}.";
    }

    private void StartSourceScan()
    {
        if (_scanTimer.IsEnabled) return;
        if (IsCadSource && _vectorScene is null)
        {
            if (_alignedPdfInstanceId is null)
                PlaceOrSelectCadInRevit(analyzeAfterPlacement: true);
            else
                CaptureAlignedCadPosition();
            return;
        }
        if (string.Equals(
                Path.GetExtension(_currentPdfPath),
                ".pdf",
                StringComparison.OrdinalIgnoreCase) &&
            _renderedPdfPage is null)
        {
            string message = "PDF preview is not ready. Re-select the PDF and review the renderer message.";
            Find<TextBlock>("v2_source_quality_text").Text = message;
            Status.Text = message;
            return;
        }
        _scanProgress = 0;
        Find<ProgressBar>("v2_scan_progress").Value = 0;
        Find<Button>("v2_scan_source_btn").IsEnabled = false;
        Find<TextBlock>("v2_source_quality_text").Text =
            "Reading vector geometry, symbols, and topology...";
        Find<TextBlock>("v2_source_quality_badge").Text = "ANALYZING";
        Status.Text = "Analyzing source geometry";
        _scanTimer.Start();
    }

    private void OnScanTick(object? sender, EventArgs eventArgs)
    {
        _scanProgress += 5;
        Find<ProgressBar>("v2_scan_progress").Value = _scanProgress;
        if (_scanProgress < 100) return;

        _scanTimer.Stop();
        Find<Button>("v2_scan_source_btn").IsEnabled = true;
        if (_vectorScene is not null)
        {
            Find<TextBlock>("v2_source_quality_text").Text =
                IsCadSource
                    ? $"Native CAD geometry extracted from {_cadLayerCounts.Count:N0} layer(s) - exact layer picking is ready"
                    : "Native PDF vectors extracted - exact CAD-style picking is ready";
            Find<TextBlock>("v2_source_quality_badge").Text = IsCadSource ? "NATIVE CAD" : "VECTOR PDF";
            Find<TextBlock>("v2_vector_count_text").Text = _vectorScene.Segments.Length.ToString("N0");
            Find<TextBlock>("v2_symbol_count_text").Text = "Pick seed";
            Find<TextBlock>("v2_unclassified_count_text").Text = "3 types";
            Status.Text = IsCadSource
                ? "CAD layer extraction complete - pick Main, Branch, and Sprinkler layers"
                : "Vector extraction complete - pick Main, Branch, and Sprinkler seeds";
        }
        else
        {
            Find<TextBlock>("v2_source_quality_text").Text =
                "Raster PDF detected - exact vector picking is unavailable";
            Find<TextBlock>("v2_source_quality_badge").Text = "RASTER PDF";
            Find<TextBlock>("v2_vector_count_text").Text = "0";
            Find<TextBlock>("v2_symbol_count_text").Text = "—";
            Find<TextBlock>("v2_unclassified_count_text").Text = "Manual";
            Status.Text = "Raster analysis complete - manual verification is required";
        }
        MainTabs.SelectedIndex = 1;
    }

    private void FilterLayers()
    {
        string query = Find<TextBox>("v2_layer_filter_tb").Text.Trim();
        DataGrid grid = Find<DataGrid>("v2_layer_grid");
        grid.ItemsSource = string.IsNullOrWhiteSpace(query)
            ? _layerItems
            : _layerItems.Where(item =>
                    item.CadLayer.IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0 ||
                    item.RevitCategory.IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0 ||
                    item.Status.IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0)
                .ToList();
        if (grid.Items.Count > 0 && grid.SelectedItem is null)
            grid.SelectedIndex = 0;
    }

    private void AutoMapLayers()
    {
        if (_vectorScene is not null)
        {
            foreach (LayerMappingItem item in _layerItems)
            {
                PdfVectorClass vectorClass = item.Marker switch
                {
                    1 => PdfVectorClass.MainPipe,
                    2 => PdfVectorClass.Sprinkler,
                    3 => PdfVectorClass.BranchPipe,
                    _ => PdfVectorClass.None
                };
                if (vectorClass == PdfVectorClass.None)
                {
                    item.Detected = 0;
                    item.Confidence = "—";
                    item.Status = "Not seeded";
                    continue;
                }

                if (!_vectorPicks.TryGetValue(vectorClass, out PdfVectorPickSnapshot? pick))
                {
                    item.Detected = 0;
                    item.Confidence = "—";
                    item.Status = "Pick seed";
                    continue;
                }
                item.Detected = CountVectorDetected(vectorClass);
                item.Confidence = "User";
                item.Status = "Seeded";
            }
            RefreshFittingTopology();
            Find<DataGrid>("v2_layer_grid").Items.Refresh();
            UpdateVectorPickSummary();
            Status.Text = _vectorPicks.Count < 3
                ? "Auto Map is waiting for Main, Branch, and Sprinkler seed picks"
                : "Vector groups generated from the three user-confirmed PDF seeds";
            return;
        }

        foreach (LayerMappingItem item in _layerItems)
        {
            item.Status = item.RevitCategory == "Ignore"
                ? "Ignored"
                : item.Confidence is "96%" or "94%" or "91%"
                    ? "Mapped"
                    : "Review";
        }
        Find<DataGrid>("v2_layer_grid").Items.Refresh();
        LayerMappingItem? reviewItem = _layerItems.FirstOrDefault(item => item.Status == "Review");
        if (reviewItem is not null)
            SelectLayerByMarker(reviewItem.Marker);
        Status.Text = "Auto Map complete - compare numbered overlays before confirmation";
    }

    private bool TryAutoAnalyzeCadTopology()
    {
        if (_vectorScene is null || _vectorScene.Segments.Length == 0) return false;

        List<CadSprinklerPath> sprinklerPaths = DetectCadSprinklerPaths();
        HashSet<int> sprinklerPathIds = sprinklerPaths
            .Select(item => item.PathId)
            .ToHashSet();
        int[] sprinklerIds = _vectorScene.Segments
            .Where(segment => sprinklerPathIds.Contains(segment.PathId))
            .Select(segment => segment.Id)
            .Distinct()
            .Take(24000)
            .ToArray();

        int[] pipeCandidates = _vectorScene.Segments
            .Where(segment =>
                !sprinklerPathIds.Contains(segment.PathId) &&
                !IsCadSegmentHidden(segment.Id) &&
                GetCadPixelSignature(segment.Id).IsChromatic &&
                IsPipeScanCandidate(segment, PdfVectorClass.BranchPipe) &&
                !IsCadCompactSymbolPath(segment.Id))
            .Select(segment => segment.Id)
            .Distinct()
            .Take(30000)
            .ToArray();
        if (pipeCandidates.Length < 2) return false;

        HashSet<int> doubleLineIds = FindCadDoubleLineSegments(pipeCandidates);
        List<CadTopologyColorGroup> colorGroups = BuildCadTopologyColorGroups(
            pipeCandidates,
            sprinklerPaths,
            doubleLineIds);
        if (colorGroups.Count == 0) return false;

        CadTopologyColorGroup branchGroup = colorGroups
            .OrderByDescending(item => item.SprinklerContacts)
            .ThenByDescending(item => item.TotalLength)
            .First();
        CadTopologyColorGroup? mainGroup = colorGroups
            .Where(item => !ReferenceEquals(item, branchGroup))
            .OrderByDescending(item =>
                item.TotalLength *
                (1.0 + Math.Min(1.0, item.DoubleLineRatio)) *
                (1.0 + Math.Min(1.0, item.AverageLineWeight / 16.0)))
            .FirstOrDefault();

        int[] mainIds;
        int[] branchIds;
        bool sameColorNetwork = mainGroup is null;
        if (mainGroup is not null)
        {
            mainIds = mainGroup.SegmentIds.ToArray();
            branchIds = branchGroup.SegmentIds.ToArray();
        }
        else
        {
            (mainIds, branchIds) = SplitSingleColorCadNetwork(
                branchGroup.SegmentIds,
                sprinklerPaths,
                doubleLineIds);
        }

        if (mainIds.Length == 0 || branchIds.Length == 0)
            return false;

        // Commit only after all three classes have a usable result. This keeps
        // a failed automatic pass from destroying the user's existing seeds.
        _vectorPicks.Clear();
        _additionalVectorPicks.Clear();
        _vectorExcludedPathIds.Clear();
        _vectorExcludedSegmentIds.Clear();
        AddGeometryAnalysisPick(PdfVectorClass.MainPipe, mainIds);
        AddGeometryAnalysisPick(PdfVectorClass.BranchPipe, branchIds);
        if (sprinklerIds.Length > 0)
            AddGeometryAnalysisPick(PdfVectorClass.Sprinkler, sprinklerIds);

        foreach (PdfVectorClass vectorClass in new[]
                 {
                     PdfVectorClass.MainPipe,
                     PdfVectorClass.Sprinkler,
                     PdfVectorClass.BranchPipe
                 })
        {
            LayerMappingItem? item = _layerItems.FirstOrDefault(candidate =>
                candidate.Marker == MarkerForClass(vectorClass));
            if (item is null) continue;
            int detected = CountVectorDetected(vectorClass);
            item.Detected = detected;
            item.Confidence = vectorClass == PdfVectorClass.Sprinkler ? "Shape" : "Topology";
            item.Status = detected == 0
                ? "Review / add seed"
                : vectorClass == PdfVectorClass.Sprinkler
                    ? "Repeated symbol"
                    : sameColorNetwork
                        ? "Geometry review"
                        : "Color + geometry";
        }

        _lastAssignedVectorClass = sprinklerIds.Length > 0
            ? PdfVectorClass.Sprinkler
            : PdfVectorClass.MainPipe;
        _pendingVectorClass = PdfVectorClass.None;
        _activeVectorPick = null;
        _isolateVectorResult = false;
        RefreshFittingTopology();
        Find<DataGrid>("v2_layer_grid").Items.Refresh();
        UpdateVectorPickSummary();
        RestoreVectorPickMarkers();
        RedrawVectorHighlights();
        SaveMappingSidecar();
        if (sprinklerIds.Length > 0)
            SelectLayerByMarker(2);

        int teeCount = _fittingCandidates.Count(item => item.Kind == PdfFittingKind.Tee);
        int elbowCount = _fittingCandidates.Count(item => item.Kind == PdfFittingKind.Elbow);
        int crossCount = _fittingCandidates.Count(item => item.Kind == PdfFittingKind.Cross);
        int doubleCount = doubleLineIds.Count / 2;
        string review = sameColorNetwork
            ? " One-color network is preliminary; review Main/Branch before creation."
            : string.Empty;
        Find<TextBlock>("v2_vector_pick_text").Text =
            $"Geometry analysis: {mainIds.Length:N0} main line(s), {branchIds.Length:N0} branch line(s), " +
            $"{sprinklerPaths.Count:N0} repeated sprinkler symbol(s), {doubleCount:N0} double-line pair(s). " +
            $"Fittings: Tee {teeCount:N0}, Cross {crossCount:N0}, Elbow {elbowCount:N0}.{review}";
        Status.Text = "CAD topology analysis complete - review the isolated groups before creating Revit elements";
        return true;
    }

    private void AddGeometryAnalysisPick(PdfVectorClass vectorClass, IReadOnlyList<int> ids)
    {
        if (_vectorScene is null || ids.Count == 0) return;
        int[] valid = ids
            .Where(id => id >= 0 && id < _vectorScene.Segments.Length)
            .Distinct()
            .Take(30000)
            .ToArray();
        if (valid.Length == 0) return;
        _vectorPicks[vectorClass] = new PdfVectorPickSnapshot
        {
            ClassName = vectorClass.ToString(),
            SegmentId = valid[0],
            Scope = PdfVectorSelectionScope.Line.ToString(),
            SmartFlattened = true,
            GeometryAnalysis = true,
            ExplicitSegmentIds = valid.ToList()
        };
    }

    private List<CadSprinklerPath> DetectCadSprinklerPaths()
    {
        if (_vectorScene is null) return [];
        var candidates = new List<CadSprinklerPath>();
        foreach (IGrouping<int, PdfVectorSegment> path in _vectorScene.Segments.GroupBy(item => item.PathId))
        {
            PdfVectorSegment first = path.First();
            if (IsCadSegmentHidden(first.Id)) continue;
            CadPixelSignature color = GetCadPixelSignature(first.Id);
            if (!color.IsChromatic) continue;
            PdfVectorPathMetrics metrics = _vectorScene.GetPathMetrics(first.Id);
            if (!IsSprinklerShapeCandidate(metrics)) continue;
            double width = metrics.Width * _vectorScene.PageWidth;
            double height = metrics.Height * _vectorScene.PageHeight;
            double diagonal = Math.Sqrt(width * width + height * height);
            int geometryScore = (metrics.IsClosed ? 4 : 0) +
                                (metrics.SegmentCount >= 6 ? 4 : metrics.SegmentCount >= 4 ? 2 : 0) +
                                (metrics.AspectRatio is >= 0.65 and <= 1.55 ? 3 : 0) +
                                (metrics.RadialVariation <= 0.40 ? 2 : 0);
            candidates.Add(new CadSprinklerPath(
                path.Key,
                first.Id,
                metrics.CenterX,
                metrics.CenterY,
                diagonal,
                metrics.SegmentCount,
                metrics.AspectRatio,
                metrics.RadialVariation,
                color,
                geometryScore));
        }
        if (candidates.Count == 0) return [];

        List<IGrouping<CadSymbolKey, CadSprinklerPath>> clusters = candidates
            .GroupBy(item => new CadSymbolKey(
                (int)Math.Round(item.Color.Hue / 10.0),
                PortableMath.Clamp(item.SegmentCount / 2, 1, 20),
                (int)Math.Round(Math.Log(Math.Max(item.DiagonalMillimeters, 1.0), 1.35)),
                (int)Math.Round(PortableMath.Clamp(item.AspectRatio, 0.2, 5.0) * 4.0)))
            .Where(group => group.Count() >= 2)
            .OrderByDescending(group => group.Sum(item => item.GeometryScore) * Math.Sqrt(group.Count()))
            .ToList();
        if (clusters.Count == 0) return [];

        double bestScore = clusters.Max(group =>
            group.Sum(item => item.GeometryScore) * Math.Sqrt(group.Count()));
        CadPixelSignature bestColor = clusters[0].First().Color;
        return clusters
            .Where(group =>
                IsSimilarCadColor(bestColor, group.First().Color) &&
                group.Sum(item => item.GeometryScore) * Math.Sqrt(group.Count()) >= bestScore * 0.45)
            .Take(4)
            .SelectMany(group => group)
            .GroupBy(item => item.PathId)
            .Select(group => group.First())
            .Take(5000)
            .ToList();
    }

    private List<CadTopologyColorGroup> BuildCadTopologyColorGroups(
        IReadOnlyList<int> pipeCandidates,
        IReadOnlyList<CadSprinklerPath> sprinklers,
        ISet<int> doubleLineIds)
    {
        if (_vectorScene is null) return [];
        return pipeCandidates
            .GroupBy(id =>
            {
                CadPixelSignature color = GetCadPixelSignature(id);
                return new CadNetworkColorKey(
                    (int)Math.Round(color.Hue / 10.0),
                    (int)Math.Round(color.Saturation / 0.12),
                    (int)Math.Round(color.Value / 0.12));
            })
            .Select(group =>
            {
                List<int> ids = group.ToList();
                double totalLength = ids.Sum(id => PhysicalCadSegmentLength(_vectorScene.Segments[id]));
                double averageWeight = ids.Average(id => _vectorScene.Segments[id].StrokeWidth);
                int contacts = sprinklers.Count(sprinkler => ids.Any(id =>
                    DistanceToCadSegmentMillimeters(
                        _vectorScene.Segments[id],
                        sprinkler.CenterX,
                        sprinkler.CenterY) <= Math.Max(180.0, sprinkler.DiagonalMillimeters * 1.6)));
                double doubleRatio = ids.Count == 0
                    ? 0
                    : ids.Count(doubleLineIds.Contains) / (double)ids.Count;
                return new CadTopologyColorGroup(
                    ids,
                    totalLength,
                    averageWeight,
                    contacts,
                    doubleRatio);
            })
            .Where(item => item.SegmentIds.Count >= 2)
            .OrderByDescending(item => item.TotalLength)
            .Take(12)
            .ToList();
    }

    private (int[] Main, int[] Branch) SplitSingleColorCadNetwork(
        IReadOnlyList<int> source,
        IReadOnlyList<CadSprinklerPath> sprinklers,
        ISet<int> doubleLineIds)
    {
        if (_vectorScene is null || source.Count == 0) return ([], []);
        HashSet<int> available = source.ToHashSet();
        var branch = new HashSet<int>();
        var queue = new Queue<int>();
        foreach (CadSprinklerPath sprinkler in sprinklers)
        {
            foreach (int id in source.Where(id =>
                         DistanceToCadSegmentMillimeters(
                             _vectorScene.Segments[id],
                             sprinkler.CenterX,
                             sprinkler.CenterY) <= Math.Max(180.0, sprinkler.DiagonalMillimeters * 1.6)))
            {
                if (branch.Add(id)) queue.Enqueue(id);
            }
        }

        while (queue.Count > 0 && branch.Count < 24000)
        {
            int currentId = queue.Dequeue();
            PdfVectorSegment current = _vectorScene.Segments[currentId];
            foreach ((double X, double Y) endpoint in new[]
                     {
                         (current.X1, current.Y1),
                         (current.X2, current.Y2)
                     })
            {
                int[] neighbors = FindCadPipeNeighbors(endpoint.X, endpoint.Y, currentId, available);
                // A three/four-way node is the boundary between the terminal
                // branch run and the backbone. Do not flood through it.
                if (neighbors.Length >= 2) continue;
                foreach (int neighbor in neighbors)
                    if (branch.Add(neighbor)) queue.Enqueue(neighbor);
            }
        }

        var main = available.Except(branch).ToHashSet();
        if (main.Count < Math.Max(2, available.Count / 20))
        {
            int desiredMain = Math.Max(2, available.Count / 5);
            main = available
                .OrderByDescending(id => doubleLineIds.Contains(id))
                .ThenByDescending(id => _vectorScene.Segments[id].StrokeWidth)
                .ThenByDescending(id => PhysicalCadSegmentLength(_vectorScene.Segments[id]))
                .Take(desiredMain)
                .ToHashSet();
            branch = available.Except(main).ToHashSet();
        }
        return (main.ToArray(), branch.ToArray());
    }

    private int[] FindCadPipeNeighbors(
        double x,
        double y,
        int currentId,
        ISet<int> available)
    {
        if (_vectorScene is null) return [];
        const double toleranceMillimeters = 40.0;
        double toleranceX = toleranceMillimeters / Math.Max(_vectorScene.PageWidth, 1.0);
        double toleranceY = toleranceMillimeters / Math.Max(_vectorScene.PageHeight, 1.0);
        return _vectorScene.HitTest(x, y, toleranceX, toleranceY, 160)
            .Where(id => id != currentId && available.Contains(id))
            .Where(id =>
            {
                PdfVectorSegment segment = _vectorScene.Segments[id];
                double first = Math.Sqrt(
                    Math.Pow((segment.X1 - x) * _vectorScene.PageWidth, 2) +
                    Math.Pow((segment.Y1 - y) * _vectorScene.PageHeight, 2));
                double second = Math.Sqrt(
                    Math.Pow((segment.X2 - x) * _vectorScene.PageWidth, 2) +
                    Math.Pow((segment.Y2 - y) * _vectorScene.PageHeight, 2));
                return Math.Min(first, second) <= toleranceMillimeters;
            })
            .Distinct()
            .ToArray();
    }

    private HashSet<int> FindCadDoubleLineSegments(IReadOnlyList<int> source)
    {
        if (_vectorScene is null || source.Count < 2) return [];
        var result = new HashSet<int>();
        foreach (IGrouping<(int Hue, bool Horizontal), int> group in source.GroupBy(id =>
                 {
                     PdfVectorSegment segment = _vectorScene.Segments[id];
                     CadPixelSignature color = GetCadPixelSignature(id);
                     return ((int)Math.Round(color.Hue / 10.0),
                         Math.Abs(segment.X2 - segment.X1) >= Math.Abs(segment.Y2 - segment.Y1));
                 }))
        {
            (int Id, double Offset, double Start, double End)[] lines = group
                .Select(id =>
                {
                    PdfVectorSegment segment = _vectorScene.Segments[id];
                    bool horizontal = group.Key.Horizontal;
                    double offset = (horizontal
                        ? (segment.Y1 + segment.Y2) * 0.5 * _vectorScene.PageHeight
                        : (segment.X1 + segment.X2) * 0.5 * _vectorScene.PageWidth);
                    double first = horizontal
                        ? segment.X1 * _vectorScene.PageWidth
                        : segment.Y1 * _vectorScene.PageHeight;
                    double second = horizontal
                        ? segment.X2 * _vectorScene.PageWidth
                        : segment.Y2 * _vectorScene.PageHeight;
                    return (
                        Id: id,
                        Offset: offset,
                        Start: Math.Min(first, second),
                        End: Math.Max(first, second));
                })
                .OrderBy(item => item.Offset)
                .ToArray();
            for (int index = 0; index < lines.Length; index++)
            for (int candidateIndex = index + 1; candidateIndex < lines.Length; candidateIndex++)
            {
                double gap = lines[candidateIndex].Offset - lines[index].Offset;
                if (gap > 300.0) break;
                if (gap < 8.0) continue;
                double overlap = Math.Min(lines[index].End, lines[candidateIndex].End) -
                                 Math.Max(lines[index].Start, lines[candidateIndex].Start);
                double shorter = Math.Min(
                    lines[index].End - lines[index].Start,
                    lines[candidateIndex].End - lines[candidateIndex].Start);
                if (shorter <= 80.0 || overlap / shorter < 0.68) continue;
                result.Add(lines[index].Id);
                result.Add(lines[candidateIndex].Id);
                break;
            }
        }
        return result;
    }

    private void ClearAllVectorClassifications()
    {
        if (MessageBox.Show(
                _window,
                "Clear every Main, Branch, Sprinkler seed and all exclusions for this PDF?",
                "Clear all classifications",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        _vectorPicks.Clear();
        _additionalVectorPicks.Clear();
        _vectorExcludedPathIds.Clear();
        _vectorExcludedSegmentIds.Clear();
        _fittingCandidates = [];
        _activeVectorPick = null;
        _selectedVectorSegmentId = -1;
        _lastAssignedVectorClass = PdfVectorClass.None;
        _pendingVectorClass = PdfVectorClass.None;
        _pendingExclusionClass = PdfVectorClass.None;
        _appendVectorPickMode = false;
        _isolateVectorResult = false;
        Find<Image>("v2_layer_pdf_image").Opacity = 1.0;
        for (int marker = 1; marker <= 6; marker++)
            Find<Button>($"v2_marker_{marker}_btn").Visibility = WpfVisibility.Collapsed;
        List<LayerMappingItem> defaults = SprinklerModelerData.CreateLayerMappings();
        foreach (LayerMappingItem item in _layerItems)
        {
            LayerMappingItem? original = defaults.FirstOrDefault(candidate => candidate.Marker == item.Marker);
            if (original is not null)
            {
                item.RevitCategory = original.RevitCategory;
                item.FamilyType = original.FamilyType;
            }
            item.Detected = 0;
            item.Confidence = "—";
            item.Status = item.Marker <= 3 ? "Pick seed" : "Not seeded";
        }
        SetPickButtonState(PdfVectorClass.None);
        Find<Button>("v2_exclude_wrong_btn").ClearValue(System.Windows.Controls.Control.BackgroundProperty);
        Find<Button>("v2_exclude_wrong_btn").ClearValue(System.Windows.Controls.Control.BorderBrushProperty);
        Find<Button>("v2_exclude_wrong_btn").ClearValue(System.Windows.Controls.Control.BorderThicknessProperty);
        Find<DataGrid>("v2_layer_grid").Items.Refresh();
        UpdateLayerSelection();
        UpdateVectorPickSummary();
        SaveMappingSidecar();
        RedrawVectorHighlights();
        Find<TextBlock>("v2_vector_pick_text").Text =
            "All classifications cleared. Pick a sprinkler seed to restart, then add more seeds if any symbol style is missed.";
        Status.Text = "All PDF classifications cleared";
    }

    private void SelectLayerByMarker(int marker)
    {
        LayerMappingItem? item = _layerItems.FirstOrDefault(candidate => candidate.Marker == marker);
        if (item is null) return;

        TextBox filter = Find<TextBox>("v2_layer_filter_tb");
        if (!string.IsNullOrWhiteSpace(filter.Text))
        {
            filter.Text = string.Empty;
            Find<DataGrid>("v2_layer_grid").ItemsSource = _layerItems;
        }

        DataGrid grid = Find<DataGrid>("v2_layer_grid");
        grid.SelectedItem = item;
        grid.ScrollIntoView(item);
        UpdateLayerSelection();
        Status.Text = $"PDF marker {marker} selected - verify {item.CadLayer}";
    }

    private void UpdateLayerSelection()
    {
        if (Find<DataGrid>("v2_layer_grid").SelectedItem is not LayerMappingItem item) return;

        Find<Border>("v2_selected_layer_badge").Background = Brush(item.ColorHex);
        Find<TextBlock>("v2_selected_layer_number_text").Text = item.Marker.ToString();
        Find<TextBlock>("v2_selected_layer_name_text").Text = item.CadLayer;
        Find<TextBlock>("v2_selected_layer_detail_text").Text = $"{item.Detected} objects";
        Find<TextBlock>("v2_selected_confidence_text").Text = $"{item.Confidence}  •  {item.Status}";

        SelectComboValue(Find<WpfComboBox>("v2_mapping_category_combo"), item.RevitCategory);
        SelectComboValue(Find<WpfComboBox>("v2_mapping_family_combo"), item.FamilyType);

        string label = item.Marker switch
        {
            1 => "Main pipe",
            2 => "Sprinkler heads",
            3 => "Branch pipe",
            4 => "Pipe fittings",
            5 => "Valves",
            _ => "Text / ignored geometry"
        };
        Find<TextBlock>("v2_overlay_summary_text").Text =
            $"Marker {item.Marker} • {label} • {item.Detected} objects";

        PdfVectorClass selectedClass = ClassForMarker(item.Marker);
        UpdateCadScanControls(selectedClass);
        Find<Button>("v2_exclude_wrong_btn").Content = selectedClass == PdfVectorClass.None
            ? "−  Remove / Sweep"
            : $"−  Remove / Sweep {VectorClassLabel(selectedClass)}";
        Find<Button>("v2_clear_exclusions_btn").Content = selectedClass == PdfVectorClass.None
            ? "↶  Restore removed"
            : $"↶  Restore {VectorClassLabel(selectedClass)}";

        for (int marker = 1; marker <= 6; marker++)
        {
            Button markerButton = Find<Button>($"v2_marker_{marker}_btn");
            bool selected = marker == item.Marker;
            markerButton.Opacity = selected ? 1.0 : 0.68;
            markerButton.BorderBrush = selected ? Brush("#17243A") : Brush("#FFFFFF");
            markerButton.BorderThickness = new Thickness(selected ? 4 : 3);
        }
        if (_vectorScene is not null)
            RedrawVectorHighlights();
    }

    private void ApplyLayerOverride()
    {
        if (Find<DataGrid>("v2_layer_grid").SelectedItem is not LayerMappingItem item) return;

        item.RevitCategory = Find<WpfComboBox>("v2_mapping_category_combo").SelectedItem as string
            ?? item.RevitCategory;
        item.FamilyType = Find<WpfComboBox>("v2_mapping_family_combo").SelectedItem as string
            ?? item.FamilyType;
        item.Status = "Adjusted";
        Find<DataGrid>("v2_layer_grid").Items.Refresh();
        UpdateLayerSelection();
        SaveMappingSidecar();
        Status.Text = $"Marker {item.Marker} mapping adjusted by user";
    }

    private void ConfirmLayerMapping()
    {
        if (Find<DataGrid>("v2_layer_grid").SelectedItem is not LayerMappingItem item) return;
        item.Status = item.RevitCategory == "Ignore" ? "Ignored" : "Verified";
        Find<DataGrid>("v2_layer_grid").Items.Refresh();
        UpdateLayerSelection();
        SaveMappingSidecar();
        Status.Text = $"Marker {item.Marker} verified against imported PDF";
    }

    private void BeginLayerAdjustment()
    {
        WpfComboBox category = Find<WpfComboBox>("v2_mapping_category_combo");
        category.Focus();
        category.IsDropDownOpen = true;
        Status.Text = "Choose a new Revit category and family/type, then apply override";
    }

    private void AddSelectedClassificationSeed()
    {
        PdfVectorClass vectorClass = CurrentVectorClass();
        if (vectorClass == PdfVectorClass.None)
        {
            Status.Text = "Add is available for Main pipe, Branch pipe, or Sprinkler classifications.";
            return;
        }
        SetAdjustMode("Add", $"Click one missed {VectorClassLabel(vectorClass)} object on the PDF, then use Find similar if needed.");
        BeginVectorPick(vectorClass);
    }

    private void SetOverlayVisibility(bool visible)
    {
        Find<Viewbox>("v2_pdf_overlay_view").Visibility =
            visible ? WpfVisibility.Visible : WpfVisibility.Collapsed;
        Status.Text = visible ? "Numbered PDF overlay shown" : "Numbered PDF overlay hidden";
    }

    private void SetAdjustMode(string mode, string instruction)
    {
        _activeAdjustMode = mode;
        Find<TextBlock>("v2_adjust_mode_text").Text = instruction;
        Status.Text = $"{mode} adjustment mode";
        FocusPdfViewer(instruction);
    }

    private void DeleteLayerMapping()
    {
        if (Find<DataGrid>("v2_layer_grid").SelectedItem is not LayerMappingItem item) return;
        PdfVectorClass vectorClass = ClassForMarker(item.Marker);
        if (vectorClass != PdfVectorClass.None)
        {
            _vectorPicks.Remove(vectorClass);
            _additionalVectorPicks.Remove(vectorClass);
            _vectorExcludedPathIds.Remove(vectorClass);
            _vectorExcludedSegmentIds.Remove(vectorClass);
            _vectorExcludedSegmentIds.Remove(vectorClass);
            item.Detected = 0;
            item.Confidence = "—";
            Find<Button>($"v2_marker_{item.Marker}_btn").Visibility = WpfVisibility.Collapsed;
            _pendingVectorClass = PdfVectorClass.None;
            _pendingExclusionClass = PdfVectorClass.None;
            UpdateVectorPickSummary();
            RedrawVectorHighlights();
        }
        item.Status = "Removed";
        Find<DataGrid>("v2_layer_grid").Items.Refresh();
        UpdateLayerSelection();
        SaveMappingSidecar();
        SetAdjustMode("Delete", $"Classification {item.Marker} was removed. Use Reset to restore its default fields, then pick a new seed.");
    }

    private void IgnoreLayerMapping()
    {
        if (Find<DataGrid>("v2_layer_grid").SelectedItem is not LayerMappingItem item) return;
        item.RevitCategory = "Ignore";
        item.FamilyType = "-";
        item.Status = "Adjusted";
        Find<DataGrid>("v2_layer_grid").Items.Refresh();
        UpdateLayerSelection();
        SaveMappingSidecar();
        SetAdjustMode("Ignore", $"Marker {item.Marker} will be kept visible but ignored during Revit creation.");
    }

    private void ResetLayerMapping()
    {
        if (Find<DataGrid>("v2_layer_grid").SelectedItem is not LayerMappingItem item) return;
        PdfVectorClass vectorClass = ClassForMarker(item.Marker);
        if (vectorClass != PdfVectorClass.None)
        {
            _vectorPicks.Remove(vectorClass);
            _additionalVectorPicks.Remove(vectorClass);
            _vectorExcludedPathIds.Remove(vectorClass);
            Button vectorMarker = Find<Button>($"v2_marker_{item.Marker}_btn");
            vectorMarker.Visibility = WpfVisibility.Collapsed;
            _pendingVectorClass = PdfVectorClass.None;
            _pendingExclusionClass = PdfVectorClass.None;
            UpdateVectorPickSummary();
            RedrawVectorHighlights();
        }
        LayerMappingItem? original = SprinklerModelerData.CreateLayerMappings()
            .FirstOrDefault(candidate => candidate.Marker == item.Marker);
        if (original is null) return;

        item.RevitCategory = original.RevitCategory;
        item.FamilyType = original.FamilyType;
        item.Status = original.Status;
        if (_vectorScene is not null && vectorClass != PdfVectorClass.None)
        {
            item.Detected = 0;
            item.Confidence = "—";
            item.Status = "Pick seed";
        }
        Find<DataGrid>("v2_layer_grid").Items.Refresh();
        UpdateLayerSelection();
        SaveMappingSidecar();
        SetAdjustMode(string.Empty, "Original classification restored. Original PDF is unchanged.");
    }

    private void BeginMarkerDrag(Button marker, MouseButtonEventArgs eventArgs)
    {
        if (!string.Equals(_activeAdjustMode, "Move marker", StringComparison.OrdinalIgnoreCase)) return;
        _draggedMarker = marker;
        _dragStart = eventArgs.GetPosition(Find<Canvas>("v2_pdf_overlay_canvas"));
        _dragOriginLeft = Canvas.GetLeft(marker);
        _dragOriginTop = Canvas.GetTop(marker);
        marker.CaptureMouse();
        eventArgs.Handled = true;
    }

    private void DragMarker(Button marker, MouseEventArgs eventArgs)
    {
        if (_draggedMarker != marker || eventArgs.LeftButton != MouseButtonState.Pressed) return;
        Canvas canvas = Find<Canvas>("v2_pdf_overlay_canvas");
        System.Windows.Point current = eventArgs.GetPosition(canvas);
        double left = Math.Max(0, Math.Min(canvas.Width - marker.ActualWidth, _dragOriginLeft + current.X - _dragStart.X));
        double top = Math.Max(0, Math.Min(canvas.Height - marker.ActualHeight, _dragOriginTop + current.Y - _dragStart.Y));
        Canvas.SetLeft(marker, left);
        Canvas.SetTop(marker, top);
        Find<TextBlock>("v2_adjust_mode_text").Text =
            $"Marker moved to PDF coordinate {left:0}, {top:0}. Confirm mapping to save.";
    }

    private void EndMarkerDrag(Button marker, MouseButtonEventArgs eventArgs)
    {
        if (_draggedMarker != marker) return;
        marker.ReleaseMouseCapture();
        _draggedMarker = null;
        eventArgs.Handled = true;
        SaveMappingSidecar();
        Status.Text = "Marker position adjusted";
    }

    private void SaveMappingSidecar()
    {
        if (string.IsNullOrWhiteSpace(_currentPdfPath)) return;
        try
        {
            List<LayerMappingSnapshot> snapshots = _layerItems.Select(item =>
            {
                Button marker = Find<Button>($"v2_marker_{item.Marker}_btn");
                return new LayerMappingSnapshot
                {
                    Marker = item.Marker,
                    RevitCategory = item.RevitCategory,
                    FamilyType = item.FamilyType,
                    Status = item.Status,
                    MarkerLeft = Canvas.GetLeft(marker),
                    MarkerTop = Canvas.GetTop(marker)
                };
            }).ToList();
            var sidecar = new LayerMappingSidecar
            {
                SourceFile = _currentPdfPath,
                SavedUtc = DateTime.UtcNow,
                CadDocumentKey = IsCadSource ? CurrentDocumentKey() : string.Empty,
                CadImportElementId = IsCadSource ? _alignedPdfInstanceId?.CompatValue() : null,
                CadViewId = IsCadSource ? _scannedPlacementViewId?.CompatValue() : null,
                Items = snapshots,
                VectorPicks = _vectorPicks.Values
                    .Concat(_additionalVectorPicks.Values.SelectMany(items => items))
                    .ToList(),
                VectorExclusions = _vectorExcludedPathIds.Keys
                    .Concat(_vectorExcludedSegmentIds.Keys)
                    .Distinct()
                    .Select(vectorClass => new PdfVectorExclusionSnapshot
                    {
                        ClassName = vectorClass.ToString(),
                        PathIds = _vectorExcludedPathIds.GetValueOrDefault(vectorClass, [])
                            .OrderBy(pathId => pathId)
                            .ToList(),
                        SegmentIds = _vectorExcludedSegmentIds.GetValueOrDefault(vectorClass, [])
                            .OrderBy(segmentId => segmentId)
                            .ToList()
                    }).ToList()
            };
            string path = GetMappingSidecarPath(_currentPdfPath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(
                path,
                JsonSerializer.Serialize(sidecar, new JsonSerializerOptions { WriteIndented = true }),
                Encoding.UTF8);
        }
        catch (Exception exception)
        {
            Status.Text = $"Mapping is adjusted in memory; sidecar save failed: {exception.Message}";
        }
    }

    private void LoadMappingSidecar(string sourcePath)
    {
        try
        {
            string path = GetMappingSidecarPath(sourcePath);
            if (!File.Exists(path)) return;
            LayerMappingSidecar? sidecar = JsonSerializer.Deserialize<LayerMappingSidecar>(
                File.ReadAllText(path, Encoding.UTF8));
            if (sidecar?.Items is null) return;

            _vectorPicks.Clear();
            _additionalVectorPicks.Clear();
            _vectorExcludedPathIds.Clear();
            _vectorExcludedSegmentIds.Clear();
            bool discardedCadAutoAnalysis = false;
            foreach (PdfVectorPickSnapshot vectorPick in sidecar.VectorPicks ?? [])
            {
                // Migrate results produced by the former broad CAD Auto Map
                // back to the verified PDF-style seed workflow.
                if (IsCadSource && vectorPick.GeometryAnalysis)
                {
                    discardedCadAutoAnalysis = true;
                    continue;
                }
                // A saved pipe sweep remains an exact crossing scan even if an
                // older interaction persisted ColorAreaScan as false.
                if (vectorPick.LineStrokeScan)
                    vectorPick.ColorAreaScan = true;
                if (Enum.TryParse(vectorPick.ClassName, out PdfVectorClass vectorClass) &&
                    vectorClass != PdfVectorClass.None)
                {
                    if (!_vectorPicks.ContainsKey(vectorClass))
                        _vectorPicks[vectorClass] = vectorPick;
                    else
                    {
                        if (!_additionalVectorPicks.TryGetValue(vectorClass, out List<PdfVectorPickSnapshot>? picks))
                            _additionalVectorPicks[vectorClass] = picks = [];
                        picks.Add(vectorPick);
                    }
                }
            }
            foreach (PdfVectorExclusionSnapshot exclusion in sidecar.VectorExclusions ?? [])
            {
                if (Enum.TryParse(exclusion.ClassName, out PdfVectorClass vectorClass) &&
                    vectorClass != PdfVectorClass.None)
                {
                    _vectorExcludedPathIds[vectorClass] = exclusion.PathIds.ToHashSet();
                    _vectorExcludedSegmentIds[vectorClass] = exclusion.SegmentIds.ToHashSet();
                }
            }

            foreach (LayerMappingSnapshot snapshot in sidecar.Items)
            {
                LayerMappingItem? item = _layerItems.FirstOrDefault(candidate => candidate.Marker == snapshot.Marker);
                if (item is null) continue;
                item.RevitCategory = snapshot.RevitCategory;
                item.FamilyType = snapshot.FamilyType;
                item.Status = snapshot.Status;

                Button marker = Find<Button>($"v2_marker_{snapshot.Marker}_btn");
                Canvas.SetLeft(marker, snapshot.MarkerLeft);
                Canvas.SetTop(marker, snapshot.MarkerTop);
            }
            if (discardedCadAutoAnalysis)
            {
                foreach (LayerMappingItem item in _layerItems.Where(item => item.Marker is >= 1 and <= 3))
                {
                    item.Detected = 0;
                    item.Confidence = "—";
                    item.Status = "Pick seed";
                }
                LayerMappingItem? fitting = _layerItems.FirstOrDefault(item => item.Marker == 4);
                if (fitting is not null)
                {
                    fitting.Detected = 0;
                    fitting.Confidence = "—";
                    fitting.Status = "Pick pipes first";
                }
            }
            foreach (PdfVectorClass vectorClass in _vectorPicks.Keys.ToArray())
                RestoreDefaultMappingForPickedClass(vectorClass, explicitPick: false);
            Find<DataGrid>("v2_layer_grid").Items.Refresh();
            UpdateLayerSelection();
            RestoreVectorPickMarkers();
            RedrawVectorHighlights();
            Status.Text = "Saved PDF layer adjustments restored";
        }
        catch (Exception exception)
        {
            Status.Text = $"PDF loaded; saved mapping could not be restored: {exception.Message}";
        }
    }

    private static string GetMappingSidecarPath(string sourcePath)
    {
        string key = PortableApi.ToHexString(PortableApi.HashData(Encoding.UTF8.GetBytes(
            Path.GetFullPath(sourcePath).ToUpperInvariant())))[..16];
        string baseName = Path.GetFileNameWithoutExtension(sourcePath);
        string folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FamilyMEP",
            "Spinkler",
            "Mappings");
        return Path.Combine(folder, $"{baseName}.{key}.mapping.json");
    }

    private void FocusPdfViewer(string status)
    {
        ScrollViewer viewer = MainTabs.SelectedIndex == 1
            ? Find<ScrollViewer>("v2_layer_pdf_scroll")
            : Find<ScrollViewer>("v2_source_pdf_scroll");
        viewer.Focus();
        Status.Text = status;
    }

    private void RotatePdf()
    {
        if (_renderedPdfPage is null)
        {
            Status.Text = "Load a PDF before rotating the page";
            return;
        }
        _pdfRotation = (_pdfRotation + 90) % 360;
        var transform = new RotateTransform(_pdfRotation);
        Find<Image>("v2_source_pdf_image").LayoutTransform = transform;
        Find<Image>("v2_layer_pdf_image").LayoutTransform = new RotateTransform(_pdfRotation);
        UpdateRenderedPdfZoom();
        Status.Text = $"PDF rotated to {_pdfRotation}°";
    }

    private sealed class LayerMappingSidecar
    {
        public string SourceFile { get; set; } = string.Empty;
        public DateTime SavedUtc { get; set; }
        public string CadDocumentKey { get; set; } = string.Empty;
        public long? CadImportElementId { get; set; }
        public long? CadViewId { get; set; }
        public List<LayerMappingSnapshot> Items { get; set; } = [];
        public List<PdfVectorPickSnapshot> VectorPicks { get; set; } = [];
        public List<PdfVectorExclusionSnapshot> VectorExclusions { get; set; } = [];
    }

    private string CurrentDocumentKey()
    {
        string projectId = _document.ProjectInformation?.UniqueId ?? string.Empty;
        return $"{_document.Title}|{projectId}";
    }

    private void TryRestoreRememberedCadPlacement(string sourcePath)
    {
        try
        {
            string path = GetMappingSidecarPath(sourcePath);
            if (!File.Exists(path)) return;
            LayerMappingSidecar? sidecar = JsonSerializer.Deserialize<LayerMappingSidecar>(
                File.ReadAllText(path, Encoding.UTF8));
            if (sidecar is null ||
                !string.Equals(sidecar.CadDocumentKey, CurrentDocumentKey(), StringComparison.Ordinal) ||
                sidecar.CadImportElementId is null)
                return;
            var rememberedId = PortableApi.ElementId(sidecar.CadImportElementId.Value);
            if (_document.GetElement(rememberedId) is not ImportInstance importInstance) return;
            _alignedPdfInstanceId = rememberedId;
            _scannedPlacementViewId = sidecar.CadViewId is long viewId
                ? PortableApi.ElementId(viewId)
                : importInstance.OwnerViewId;
            RevitViewOption? rememberedView = _placementViews.FirstOrDefault(option =>
                option.Id == _scannedPlacementViewId);
            if (rememberedView is not null)
                Find<WpfComboBox>("v2_target_view_combo").SelectedItem = rememberedView;
            // SelectionChanged clears the transient placement fields, so restore
            // the remembered identifiers after assigning the combo value.
            _alignedPdfInstanceId = rememberedId;
            _scannedPlacementViewId = rememberedView?.Id ?? _scannedPlacementViewId;
            TextBlock placementStatus = Find<TextBlock>("v2_placement_area_status_text");
            placementStatus.Text = rememberedView is null
                ? "Existing CAD remembered. Analyze Source will reuse it."
                : $"Existing CAD remembered in {rememberedView.Name}. Analyze Source will reuse it.";
            placementStatus.Foreground = Brush("#23834B");
        }
        catch
        {
            // A stale sidecar must never prevent selecting or importing CAD.
        }
    }

    private sealed class LayerMappingSnapshot
    {
        public int Marker { get; set; }
        public string RevitCategory { get; set; } = string.Empty;
        public string FamilyType { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public double MarkerLeft { get; set; }
        public double MarkerTop { get; set; }
    }

    private async Task EnsurePdfViewerAsync()
    {
        if (_disposed) return;
        if (ActivePdfViewer?.CoreWebView2 is not null) return;

        try
        {
            string userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FamilyMEP",
                "WebView2",
                "SpinklerPdfViewer");
            _pdfEnvironment ??= await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: userDataFolder);
            if (MainTabs.SelectedIndex == 1)
                _layerPdfWebView ??= await CreatePdfViewerAsync("v2_layer_pdf_host", _pdfEnvironment);
            else
                _sourcePdfWebView ??= await CreatePdfViewerAsync("v2_source_pdf_host", _pdfEnvironment);
            AttachPdfViewerToActiveHost();
        }
        catch (Exception exception)
        {
            ShowPdfPlaceholder($"PDF viewer unavailable: {exception.Message}");
        }
    }

    private async Task<WebView2CompositionControl> CreatePdfViewerAsync(
        string hostName,
        CoreWebView2Environment environment)
    {
        var viewer = new WebView2CompositionControl
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Visibility = WpfVisibility.Collapsed,
            ZoomFactor = _pdfZoom
        };
        WpfGrid host = Find<WpfGrid>(hostName);
        WpfPanel.SetZIndex(viewer, 0);
        host.Children.Insert(0, viewer);
        await viewer.EnsureCoreWebView2Async(environment);
        viewer.CoreWebView2.Settings.AreDevToolsEnabled = false;
        viewer.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
        viewer.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = true;
        viewer.NavigationCompleted += OnPdfNavigationCompleted;
        viewer.ZoomFactorChanged += (_, _) =>
        {
            if (Math.Abs(_pdfZoom - viewer.ZoomFactor) < 0.001) return;
            _pdfZoom = viewer.ZoomFactor;
            SyncPdfZoom(viewer);
            UpdatePdfZoomText();
        };
        return viewer;
    }

    private async Task NavigatePdfAsync(string pdfPath)
    {
        _currentPdfPath = pdfPath;
        _vectorScene = null;
        _vectorPicks.Clear();
        _additionalVectorPicks.Clear();
        _vectorExcludedPathIds.Clear();
        _vectorExcludedSegmentIds.Clear();
        _selectedVectorSegmentId = -1;
        _hoveredVectorSegmentId = -1;
        _pendingVectorClass = PdfVectorClass.None;
        _pendingExclusionClass = PdfVectorClass.None;
        _lastAssignedVectorClass = PdfVectorClass.None;
        _isolateVectorResult = false;
        Find<TextBlock>("v2_pdf_file_name_text").Text = Path.GetFileName(pdfPath);
        Find<TextBlock>("v2_pdf_document_meta_text").Text = "  •  Loading real PDF...";
        Status.Text = "Loading imported PDF in the verification viewer";

        if (!File.Exists(pdfPath))
        {
            ShowPdfPlaceholder("The selected PDF file no longer exists.");
            return;
        }

        try
        {
            Task<RenderedPdfPage> renderTask = PdfPageRenderer.RenderFirstPageAsync(pdfPath);
            Task<PdfVectorScene> vectorTask = PdfVectorScene.ExtractAsync(pdfPath);
            RenderedPdfPage rendered = await renderTask;
            PdfColorAnalysis analysis = await Task.Run(() => PdfColorAnalyzer.Analyze(rendered.Bitmap));
            if (_disposed) return;
            _renderedPdfPage = rendered.Bitmap;
            _pdfRotation = 0;
            _pdfZoom = 1.0;
            Find<Image>("v2_source_pdf_image").Source = rendered.Bitmap;
            Find<Image>("v2_layer_pdf_image").Source = rendered.Bitmap;
            Find<Image>("v2_layer_pdf_image").Opacity = 1.0;
            ApplyAnalysisOverlay(rendered.Bitmap, analysis);
            try
            {
                _vectorScene = await vectorTask;
                ActivateVectorPicking();
            }
            catch (Exception vectorException)
            {
                _vectorScene = null;
                Find<TextBlock>("v2_vector_pick_text").Text =
                    "No selectable vector objects were found. Raster analysis fallback is active.";
                Find<TextBlock>("v2_overlay_summary_text").Text =
                    "Raster PDF fallback • exact CAD-style picking is unavailable";
                LogPdfRenderError(pdfPath, vectorException);
            }
            LoadMappingSidecar(pdfPath);
            Find<Image>("v2_source_pdf_image").LayoutTransform = System.Windows.Media.Transform.Identity;
            Find<Image>("v2_layer_pdf_image").LayoutTransform = System.Windows.Media.Transform.Identity;
            Find<TextBlock>("v2_pdf_document_meta_text").Text =
                $"  •  Page 1 / {rendered.PageCount}  •  Native PDF render";
            Find<TextBlock>("v2_source_quality_badge").Text = "PDF READY";
            Find<TextBlock>("v2_source_quality_text").Text =
                $"Native renderer loaded page 1 of {rendered.PageCount}.";
            HidePdfPlaceholders();
            UpdateRenderedPdfVisibility();
            UpdatePdfZoomText();
            await _window.Dispatcher.InvokeAsync(UpdateRenderedPdfZoom, DispatcherPriority.Loaded);
            Status.Text = _vectorScene is null
                ? "Imported PDF rendered - raster fallback is active"
                : $"Vector PDF ready - {_vectorScene.Segments.Length:N0} exact lines can be selected";
        }
        catch (Exception exception)
        {
            LogPdfRenderError(pdfPath, exception);
            ShowPdfPlaceholder($"Could not render PDF: {exception.Message}");
        }
    }

    private static void LogPdfRenderError(string pdfPath, Exception exception)
    {
        try
        {
            string folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FamilyMEP",
                "Spinkler",
                "Logs");
            Directory.CreateDirectory(folder);
            File.AppendAllText(
                Path.Combine(folder, "pdf-render.log"),
                $"[{DateTime.Now:O}] {pdfPath}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}",
                Encoding.UTF8);
        }
        catch
        {
            // Rendering error is already displayed in the tool.
        }
    }

    private async Task ActivatePdfViewerAsync()
    {
        AttachPdfViewerToActiveHost();
        if (_disposed || string.IsNullOrWhiteSpace(_currentPdfPath) || !File.Exists(_currentPdfPath))
            return;

        await EnsurePdfViewerAsync();
        WebView2CompositionControl? viewer = ActivePdfViewer;
        if (viewer?.CoreWebView2 is null) return;
        AttachPdfViewerToActiveHost();
        NavigateViewerToPdf(viewer, _currentPdfPath);
    }

    private static void NavigateViewerToPdf(WebView2CompositionControl viewer, string pdfPath)
    {
        string folder = Path.GetDirectoryName(pdfPath)
            ?? throw new InvalidOperationException("The PDF folder could not be resolved.");
        const string virtualHost = "spinkler-pdf.familymep";
        string encodedFile = Uri.EscapeDataString(Path.GetFileName(pdfPath));
        string target = $"https://{virtualHost}/{encodedFile}";
        if (string.Equals(viewer.Source?.AbsoluteUri, target, StringComparison.OrdinalIgnoreCase))
            return;
        try
        {
            viewer.CoreWebView2.ClearVirtualHostNameToFolderMapping(virtualHost);
        }
        catch
        {
            // The mapping does not exist on the first navigation.
        }
        viewer.CoreWebView2.SetVirtualHostNameToFolderMapping(
            virtualHost,
            folder,
            CoreWebView2HostResourceAccessKind.Allow);
        viewer.CoreWebView2.Navigate(target);
    }

    private void OnPdfNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs eventArgs)
    {
        if (eventArgs.IsSuccess)
        {
            Find<TextBlock>("v2_pdf_document_meta_text").Text =
                "  •  Page 1 / 1  •  Real PDF";
            Status.Text = "Imported PDF loaded - verify numbered classification overlays";
            return;
        }

        ShowPdfPlaceholder($"Could not load PDF ({eventArgs.WebErrorStatus}).");
    }

    private void SetPdfZoom(double zoom)
    {
        _pdfZoom = Math.Max(0.10, Math.Min(20.0, Math.Round(zoom, 2)));
        UpdateRenderedPdfZoom();
        UpdatePdfZoomText();
        Status.Text = $"PDF zoom: {_pdfZoom:P0}";
    }

    private void ZoomPdfAtPointer(ScrollViewer viewer, MouseWheelEventArgs eventArgs)
    {
        if (_renderedPdfPage is null) return;
        double previousZoom = _pdfZoom;
        double zoomFactor = eventArgs.Delta > 0 ? 1.12 : 1.0 / 1.12;
        double nextZoom = Math.Max(0.10, Math.Min(20.0, previousZoom * zoomFactor));
        if (Math.Abs(nextZoom - previousZoom) < 0.001) return;

        System.Windows.Point pointer = eventArgs.GetPosition(viewer);
        double contentX = viewer.HorizontalOffset + pointer.X;
        double contentY = viewer.VerticalOffset + pointer.Y;
        _pdfZoom = nextZoom;
        UpdateRenderedPdfZoom();
        UpdatePdfZoomText();
        eventArgs.Handled = true;

        double appliedFactor = nextZoom / previousZoom;
        _window.Dispatcher.BeginInvoke(
            new Action(() =>
            {
                viewer.UpdateLayout();
                viewer.ScrollToHorizontalOffset(contentX * appliedFactor - pointer.X);
                viewer.ScrollToVerticalOffset(contentY * appliedFactor - pointer.Y);
                UpdateOverlayPlacement();
            }),
            DispatcherPriority.Render);
        Status.Text = $"PDF zoom: {_pdfZoom:P0} • wheel centered at pointer";
    }

    private void BeginPdfPan(ScrollViewer viewer, MouseButtonEventArgs eventArgs)
    {
        if (eventArgs.ChangedButton != MouseButton.Middle || _renderedPdfPage is null) return;
        _panningViewer = viewer;
        _panStart = eventArgs.GetPosition(viewer);
        _panStartHorizontalOffset = viewer.HorizontalOffset;
        _panStartVerticalOffset = viewer.VerticalOffset;
        viewer.Cursor = Cursors.SizeAll;
        viewer.CaptureMouse();
        eventArgs.Handled = true;
    }

    private void PanPdf(ScrollViewer viewer, MouseEventArgs eventArgs)
    {
        if (_panningViewer != viewer || eventArgs.MiddleButton != MouseButtonState.Pressed) return;
        System.Windows.Point current = eventArgs.GetPosition(viewer);
        viewer.ScrollToHorizontalOffset(_panStartHorizontalOffset - (current.X - _panStart.X));
        viewer.ScrollToVerticalOffset(_panStartVerticalOffset - (current.Y - _panStart.Y));
        eventArgs.Handled = true;
    }

    private void EndPdfPan(ScrollViewer viewer, MouseButtonEventArgs eventArgs)
    {
        if (_panningViewer != viewer || eventArgs.ChangedButton != MouseButton.Middle) return;
        viewer.ReleaseMouseCapture();
        viewer.Cursor = Cursors.Arrow;
        _panningViewer = null;
        eventArgs.Handled = true;
        Status.Text = "PDF pan complete";
    }

    private void SyncPdfZoom(WebView2CompositionControl? source = null)
    {
        if (_sourcePdfWebView is not null && _sourcePdfWebView != source)
            _sourcePdfWebView.ZoomFactor = _pdfZoom;
        if (_layerPdfWebView is not null && _layerPdfWebView != source)
            _layerPdfWebView.ZoomFactor = _pdfZoom;
    }

    private void UpdatePdfZoomText()
    {
        if (_disposed) return;
        string value = $"{_pdfZoom:P0}";
        Find<TextBlock>("v2_pdf_zoom_text").Text = value;
        Find<TextBlock>("v2_source_zoom_text").Text = value;
    }

    private void ShowPdfPlaceholder(string message)
    {
        Find<ScrollViewer>("v2_source_pdf_scroll").Visibility = WpfVisibility.Collapsed;
        Find<ScrollViewer>("v2_layer_pdf_scroll").Visibility = WpfVisibility.Collapsed;
        if (_sourcePdfWebView is not null)
            _sourcePdfWebView.Visibility = WpfVisibility.Collapsed;
        if (_layerPdfWebView is not null)
            _layerPdfWebView.Visibility = WpfVisibility.Collapsed;
        Find<Border>("v2_source_pdf_placeholder").Visibility = WpfVisibility.Visible;
        Find<Viewbox>("v2_pdf_placeholder_view").Visibility = WpfVisibility.Visible;
        Find<TextBlock>("v2_pdf_document_meta_text").Text = $"  •  {message}";
        Find<TextBlock>("v2_source_quality_text").Text = message;
        Status.Text = message;
    }

    private void HidePdfPlaceholders()
    {
        Find<Border>("v2_source_pdf_placeholder").Visibility = WpfVisibility.Collapsed;
        Find<Viewbox>("v2_pdf_placeholder_view").Visibility = WpfVisibility.Collapsed;
    }

    private void UpdateRenderedPdfVisibility()
    {
        bool ready = _renderedPdfPage is not null;
        bool sourceActive = MainTabs.SelectedIndex == 0;
        bool layerActive = MainTabs.SelectedIndex == 1;
        Find<ScrollViewer>("v2_source_pdf_scroll").Visibility =
            ready && sourceActive ? WpfVisibility.Visible : WpfVisibility.Collapsed;
        Find<ScrollViewer>("v2_layer_pdf_scroll").Visibility =
            ready && layerActive ? WpfVisibility.Visible : WpfVisibility.Collapsed;
        if (ready)
            HidePdfPlaceholders();
        UpdateRenderedPdfZoom();
    }

    private void UpdateRenderedPdfZoom()
    {
        if (_renderedPdfPage is null || _disposed) return;
        // Always anti-alias the contextual bitmap. A nearest-neighbour DWG snapshot
        // turns every source pixel into a large square at 500%+ zoom. Crisp detail
        // is supplied by the native WPF vector overlay, not by hard pixel scaling.
        RenderOptions.SetBitmapScalingMode(
            Find<Image>("v2_source_pdf_image"),
            BitmapScalingMode.HighQuality);
        RenderOptions.SetBitmapScalingMode(
            Find<Image>("v2_layer_pdf_image"),
            BitmapScalingMode.HighQuality);
        UpdateRenderedImageSize(
            Find<Image>("v2_source_pdf_image"),
            Find<ScrollViewer>("v2_source_pdf_scroll"));
        UpdateRenderedImageSize(
            Find<Image>("v2_layer_pdf_image"),
            Find<ScrollViewer>("v2_layer_pdf_scroll"));
        UpdateCadRasterContextOpacity();
    }

    private void UpdateCadRasterContextOpacity()
    {
        if (_disposed || !IsCadSource) return;
        bool hide = HideCadArchitectureDuringScan;
        Image image = Find<Image>("v2_layer_pdf_image");
        if (_isolateVectorResult)
        {
            image.Opacity = hide ? 0.025 : 0.07;
            return;
        }
        if (hide)
        {
            image.Opacity = 0.055;
            return;
        }
        // At close zoom show the ImportInstance itself as WPF vector geometry, like
        // Revit does. Hiding the bitmap is the only way to eliminate raster blur;
        // text/fills remain available again when zooming back to Fit/100%.
        image.Opacity = _pdfZoom switch
        {
            >= 2.5 => 0.0,
            >= 1.7 => 0.22,
            _ => 1.0
        };
    }

    private void UpdateRenderedImageSize(Image image, ScrollViewer viewer)
    {
        if (_renderedPdfPage is null) return;
        double pageWidth = _renderedPdfPage.PixelWidth;
        double pageHeight = _renderedPdfPage.PixelHeight;
        if (_pdfRotation is 90 or 270)
            (pageWidth, pageHeight) = (pageHeight, pageWidth);

        double availableWidth = Math.Max(300, viewer.ActualWidth - 46);
        double availableHeight = Math.Max(240, viewer.ActualHeight - 46);
        double fit = Math.Min(availableWidth / pageWidth, availableHeight / pageHeight);
        double displayedWidth = _renderedPdfPage.PixelWidth * fit * _pdfZoom;
        double displayedHeight = _renderedPdfPage.PixelHeight * fit * _pdfZoom;
        image.Width = Math.Max(1, displayedWidth);
        image.Height = Math.Max(1, displayedHeight);
        if (ReferenceEquals(image, Find<Image>("v2_layer_pdf_image")))
            UpdateOverlayPlacement();
    }

    private void UpdateOverlayPlacement()
    {
        if (_renderedPdfPage is null || _disposed) return;
        Image image = Find<Image>("v2_layer_pdf_image");
        Viewbox overlay = Find<Viewbox>("v2_pdf_overlay_view");
        overlay.Width = image.Width;
        overlay.Height = image.Height;
        overlay.RenderTransform = System.Windows.Media.Transform.Identity;
        overlay.Visibility = Find<CheckBox>("v2_show_overlay_cb").IsChecked == true
            ? WpfVisibility.Visible
            : WpfVisibility.Collapsed;
        ApplyMarkupLineWeights();
    }

    private void ApplyAnalysisOverlay(BitmapSource page, PdfColorAnalysis analysis)
    {
        const double canvasWidth = 1000.0;
        double canvasHeight = canvasWidth * page.PixelHeight / page.PixelWidth;
        Canvas canvas = Find<Canvas>("v2_pdf_overlay_canvas");
        canvas.Width = canvasWidth;
        canvas.Height = canvasHeight;
        Canvas vectorCanvas = Find<Canvas>("v2_vector_selection_canvas");
        vectorCanvas.Width = canvasWidth;
        vectorCanvas.Height = canvasHeight;
        Image overlayImage = Find<Image>("v2_analysis_overlay_image");
        overlayImage.Source = analysis.Overlay;
        overlayImage.Width = canvasWidth;
        overlayImage.Height = canvasHeight;

        foreach ((int markerNumber, System.Windows.Point position) in analysis.MarkerPositions)
        {
            Button marker = Find<Button>($"v2_marker_{markerNumber}_btn");
            Canvas.SetLeft(marker, position.X - marker.Width / 2.0);
            Canvas.SetTop(marker, position.Y - marker.Height / 2.0);
        }
        Find<TextBlock>("v2_overlay_summary_text").Text =
            "Overlay generated from detected PDF colors and line coordinates";
    }

    private void ActivateVectorPicking()
    {
        if (_vectorScene is null) return;
        Find<Image>("v2_analysis_overlay_image").Visibility = WpfVisibility.Collapsed;
        for (int marker = 1; marker <= 6; marker++)
            Find<Button>($"v2_marker_{marker}_btn").Visibility = WpfVisibility.Collapsed;

        PrepareVectorCanvasForCurrentPage();
        Canvas canvas = Find<Canvas>("v2_vector_selection_canvas");
        canvas.Children.Clear();
        _cadBaseVectorPaths.Clear();
        canvas.Visibility = WpfVisibility.Visible;
        canvas.ReleaseMouseCapture();
        canvas.Cursor = Cursors.Arrow;
        _removeAreaScanMode = false;
        _removeAreaDragging = false;
        _mainVectorHighlight = CreateVectorPath("#F36B5B", 5, 0.82);
        _branchVectorHighlight = CreateVectorPath("#0AA6A6", 5, 0.82);
        _sprinklerVectorHighlight = CreateVectorPath("#7657E8", 5, 0.88);
        _fittingVectorHighlight = CreateVectorPath("#F5A623", 3, 1.0);
        _excludedVectorHighlight = CreateVectorPath("#D9364F", 3, 0.95);
        _excludedVectorHighlight.StrokeDashArray = new DoubleCollection([3, 2]);
        _currentVectorHighlight = CreateVectorPath("#FFB000", 4, 1.0);
        _hoverVectorObjectHighlight = CreateVectorPath("#00E5FF", 3.2, 1.0);
        _hoverVectorObjectHighlight.Visibility = WpfVisibility.Collapsed;
        _hoverVectorHighlight = new System.Windows.Shapes.Line
        {
            Stroke = Brush("#00BFD1"),
            StrokeThickness = 3,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            IsHitTestVisible = false,
            Visibility = WpfVisibility.Collapsed
        };
        _cadAreaRectangle = new System.Windows.Shapes.Rectangle
        {
            Stroke = Brush("#00A7A7"),
            StrokeThickness = 2,
            StrokeDashArray = new DoubleCollection([5, 3]),
            Fill = new SolidColorBrush(WpfColor.FromArgb(38, 0, 167, 167)),
            IsHitTestVisible = false,
            Visibility = WpfVisibility.Collapsed
        };
        _cadSweepLine = new System.Windows.Shapes.Line
        {
            Stroke = Brush("#00A7A7"),
            StrokeThickness = 3,
            StrokeDashArray = new DoubleCollection([6, 3]),
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            IsHitTestVisible = false,
            Visibility = WpfVisibility.Collapsed
        };
        AddCadBaseVectorPaths(canvas);
        canvas.Children.Add(_mainVectorHighlight);
        canvas.Children.Add(_branchVectorHighlight);
        canvas.Children.Add(_sprinklerVectorHighlight);
        canvas.Children.Add(_fittingVectorHighlight);
        canvas.Children.Add(_excludedVectorHighlight);
        canvas.Children.Add(_currentVectorHighlight);
        canvas.Children.Add(_hoverVectorObjectHighlight);
        canvas.Children.Add(_hoverVectorHighlight);
        canvas.Children.Add(_cadAreaRectangle);
        canvas.Children.Add(_cadSweepLine);

        Find<TextBlock>("v2_vector_pick_text").Text =
            $"{_vectorScene.Segments.Length:N0} selectable vector lines. Choose a type, click a line, then press Tab to expand selection.";
        Find<TextBlock>("v2_overlay_summary_text").Text =
            $"Native vector scene • {_vectorScene.Segments.Length:N0} selectable lines • PDF remains unchanged";
        Find<TextBlock>("v2_vector_count_text").Text = _vectorScene.Segments.Length.ToString("N0");
        foreach (LayerMappingItem item in _layerItems)
        {
            item.Detected = 0;
            item.Confidence = "—";
            item.Status = item.Marker <= 3 ? "Pick seed" : "Not seeded";
        }
        Find<DataGrid>("v2_layer_grid").Items.Refresh();
        UpdateVectorPickSummary();
        RedrawVectorHighlights();
        ApplyMarkupLineWeights();
        ApplyCadArchitectureVisibility();
    }

    private void PrepareVectorCanvasForCurrentPage()
    {
        if (_renderedPdfPage is null) return;
        const double canvasWidth = 1000.0;
        double canvasHeight = canvasWidth * _renderedPdfPage.PixelHeight /
                              Math.Max(1.0, _renderedPdfPage.PixelWidth);
        Canvas overlayCanvas = Find<Canvas>("v2_pdf_overlay_canvas");
        Canvas vectorCanvas = Find<Canvas>("v2_vector_selection_canvas");
        Image analysisOverlay = Find<Image>("v2_analysis_overlay_image");
        overlayCanvas.Width = canvasWidth;
        overlayCanvas.Height = canvasHeight;
        vectorCanvas.Width = canvasWidth;
        vectorCanvas.Height = canvasHeight;
        analysisOverlay.Width = canvasWidth;
        analysisOverlay.Height = canvasHeight;
    }

    private void AddCadBaseVectorPaths(Canvas canvas)
    {
        if (!IsCadSource || _vectorScene is null || _cadNativeColorBySegmentId.Count == 0)
            return;

        // Overlay native DWG vectors as WPF geometry. The bitmap supplies color and
        // fills, while these paths keep architecture, pipework and tiny sprinkler
        // symbols sharp at 1000%+ zoom.
        IEnumerable<IGrouping<int, int>> colorGroups = _vectorScene.Segments
            .Select(segment => new
            {
                segment.Id,
                Color = _cadNativeColorBySegmentId.GetValueOrDefault(segment.Id, segment.ColorArgb)
            })
            .GroupBy(item => item.Color, item => item.Id)
            // Always keep MEP colors before the much larger gray architecture
            // group, otherwise architecture can consume the vector budget and
            // sprinkler symbols disappear from the sharp overlay.
            .OrderByDescending(group =>
            {
                int color = group.Key;
                return ToCadPixelSignature(
                    (byte)((color >> 16) & 255),
                    (byte)((color >> 8) & 255),
                    (byte)(color & 255)).IsChromatic;
            })
            .ThenByDescending(group => group.Count())
            .Take(32);

        int rendered = 0;
        foreach (IGrouping<int, int> group in colorGroups)
        {
            const int totalVectorBudget = 300000;
            const int perColorBudget = 120000;
            if (rendered >= totalVectorBudget) break;
            int[] ids = group.Take(Math.Min(perColorBudget, totalVectorBudget - rendered)).ToArray();
            if (ids.Length == 0) continue;
            int color = group.Key;
            CadPixelSignature signature = ToCadPixelSignature(
                (byte)((color >> 16) & 255),
                (byte)((color >> 8) & 255),
                (byte)(color & 255));
            string stroke = signature.IsChromatic
                ? $"#{color & 0xFFFFFF:X6}"
                : "#D5DCE1";
            var path = CreateVectorPath(stroke, 0.8, signature.IsChromatic ? 0.96 : 0.58);
            path.Tag = color;
            path.Data = GeometryForSegments(ids);
            path.SnapsToDevicePixels = false;
            RenderOptions.SetEdgeMode(path, EdgeMode.Unspecified);
            _cadBaseVectorPaths.Add(path);
            canvas.Children.Add(path);
            rendered += ids.Length;
        }
    }

    private void ApplyMarkupLineWeights()
    {
        if (_disposed || _mainVectorHighlight is null || _branchVectorHighlight is null ||
            _sprinklerVectorHighlight is null || _fittingVectorHighlight is null ||
            _currentVectorHighlight is null || _excludedVectorHighlight is null)
            return;
        Canvas canvas = Find<Canvas>("v2_vector_selection_canvas");
        Viewbox overlay = Find<Viewbox>("v2_pdf_overlay_view");
        double displayScale = overlay.Width > 0 && canvas.Width > 0
            ? overlay.Width / canvas.Width
            : Math.Max(_pdfZoom, 0.1);
        displayScale = Math.Max(displayScale, 0.05);
        bool thin = Find<CheckBox>("v2_thin_markup_cb").IsChecked == true;
        bool hideArchitecture = HideCadArchitectureDuringScan;
        PdfVectorClass isolatedClass = _isolateVectorResult
            ? CurrentVectorClass()
            : PdfVectorClass.None;

        SetLine(_mainVectorHighlight,
            isolatedClass == PdfVectorClass.MainPipe ? 2.8 : thin ? 1.15 : 4.5,
            roundCaps: false,
            isolatedClass == PdfVectorClass.MainPipe);
        SetLine(_branchVectorHighlight,
            isolatedClass == PdfVectorClass.BranchPipe ? 2.8 : thin ? 1.15 : 4.5,
            roundCaps: false,
            isolatedClass == PdfVectorClass.BranchPipe);
        SetLine(_sprinklerVectorHighlight,
            isolatedClass == PdfVectorClass.Sprinkler ? 3.2 : thin ? 1.45 : 4.8,
            roundCaps: true,
            isolatedClass == PdfVectorClass.Sprinkler);
        SetLine(_fittingVectorHighlight, thin ? 1.25 : 3.2, roundCaps: false);
        SetLine(_currentVectorHighlight, thin ? 1.45 : 4.0, roundCaps: false);
        SetLine(_excludedVectorHighlight, thin ? 1.25 : 3.0, roundCaps: false);
        foreach (System.Windows.Shapes.Path basePath in _cadBaseVectorPaths)
        {
            bool hiddenCadColor = basePath.Tag is int hiddenNativeColor &&
                                  IsCadColorHidden(ToCadPixelSignature(
                                      (byte)((hiddenNativeColor >> 16) & 255),
                                      (byte)((hiddenNativeColor >> 8) & 255),
                                      (byte)(hiddenNativeColor & 255)));
            bool selectedCadColor = !hideArchitecture ||
                                    basePath.Tag is int nativeColor &&
                                    Find<WpfComboBox>("v2_cad_color_combo").SelectedItem is CadColorChoice selected &&
                                    IsSimilarCadColor(
                                        selected.Signature,
                                        ToCadPixelSignature(
                                            (byte)((nativeColor >> 16) & 255),
                                            (byte)((nativeColor >> 8) & 255),
                                            (byte)(nativeColor & 255)));
            basePath.Visibility = !hiddenCadColor && selectedCadColor
                ? WpfVisibility.Visible
                : WpfVisibility.Collapsed;
            basePath.StrokeThickness = (thin ? 0.72 : 1.05) / displayScale;
            basePath.Opacity = _isolateVectorResult
                ? 0.05
                : hideArchitecture ? 0.96 : 0.82;
            basePath.SnapsToDevicePixels = false;
            RenderOptions.SetEdgeMode(basePath, EdgeMode.Unspecified);
        }
        if (_hoverVectorHighlight is not null)
            _hoverVectorHighlight.StrokeThickness = (thin ? 1.35 : 3.0) / displayScale;
        if (_hoverVectorObjectHighlight is not null)
        {
            _hoverVectorObjectHighlight.StrokeThickness = (thin ? 1.7 : 3.5) / displayScale;
            _hoverVectorObjectHighlight.SnapsToDevicePixels = false;
            RenderOptions.SetEdgeMode(_hoverVectorObjectHighlight, EdgeMode.Unspecified);
        }

        void SetLine(
            System.Windows.Shapes.Path path,
            double screenPixels,
            bool roundCaps,
            bool isolatedFocus = false)
        {
            path.StrokeThickness = screenPixels / displayScale;
            path.StrokeStartLineCap = roundCaps ? PenLineCap.Round : PenLineCap.Flat;
            path.StrokeEndLineCap = roundCaps ? PenLineCap.Round : PenLineCap.Flat;
            path.StrokeLineJoin = roundCaps ? PenLineJoin.Round : PenLineJoin.Miter;
            path.Opacity = isolatedFocus ? 1.0 : thin ? 0.96 : 0.84;
            path.SnapsToDevicePixels = true;
        }
    }

    private static System.Windows.Shapes.Path CreateVectorPath(string color, double thickness, double opacity) =>
        new()
        {
            Stroke = Brush(color),
            StrokeThickness = thickness,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Opacity = opacity,
            IsHitTestVisible = false
        };

    private void BeginVectorPick(PdfVectorClass vectorClass)
    {
        if (_removeAreaScanMode || _removeAreaDragging)
            CancelRemoveAreaMode("Switching from Remove/Sweep to Pick mode.");
        if (_vectorScene is null)
        {
            Status.Text = "This PDF has no selectable vector scene. Use a vector PDF or import DWG.";
            return;
        }

        // Pressing Pick is an explicit request to use this class for creation.
        // Recover fields left as Ignore/Removed by an earlier adjustment so a
        // valid seed cannot remain visibly highlighted but silently disabled.
        RestoreDefaultMappingForPickedClass(vectorClass, explicitPick: true);
        _pendingVectorClass = vectorClass;
        _appendVectorPickMode = _vectorPicks.ContainsKey(vectorClass);
        _activeVectorPick = null;
        _pendingExclusionClass = PdfVectorClass.None;
        Find<Button>("v2_exclude_wrong_btn").ClearValue(System.Windows.Controls.Control.BackgroundProperty);
        Find<Button>("v2_exclude_wrong_btn").ClearValue(System.Windows.Controls.Control.BorderBrushProperty);
        Find<Button>("v2_exclude_wrong_btn").ClearValue(System.Windows.Controls.Control.BorderThicknessProperty);
        _vectorSelectionScope = PdfVectorSelectionScope.Line;
        string label = VectorClassLabel(vectorClass);
        Find<TextBlock>("v2_vector_pick_text").Text = _appendVectorPickMode
            ? $"Add another {label} seed: pick one missed object, then use Find similar to merge that shape with the existing result."
            : $"Pick {label}: click the exact PDF line. Press Tab before/after picking for PDF object or similar style.";
        SetPickButtonState(vectorClass);
        Find<Canvas>("v2_vector_selection_canvas").Focus();
        Status.Text = $"CAD Pick active: {label}";
    }

    private void SelectVectorAtPointer(
        MouseButtonEventArgs eventArgs,
        bool allowActiveRemoveMode = false)
    {
        if (_vectorScene is null || _cadAreaScanMode || _cadAreaDragging ||
            ((_removeAreaScanMode || _removeAreaDragging) && !allowActiveRemoveMode))
            return;
        Canvas canvas = Find<Canvas>("v2_vector_selection_canvas");
        double pickTolerance = _pendingVectorClass == PdfVectorClass.Sprinkler ||
                               _pendingExclusionClass != PdfVectorClass.None
            ? 22
            : 12;
        if (!TryGetNormalizedPdfPointer(eventArgs, pickTolerance, out double x, out double y,
                out double toleranceX, out double toleranceY))
            return;
        _vectorHitCandidates = _vectorScene.HitTest(
            x,
            y,
            toleranceX,
            toleranceY,
            _pendingVectorClass == PdfVectorClass.Sprinkler || _pendingExclusionClass != PdfVectorClass.None
                ? 80
                : 20);
        if (HideCadArchitectureDuringScan && _pendingExclusionClass == PdfVectorClass.None)
            _vectorHitCandidates = _vectorHitCandidates
                .Where(IsSelectedCadScanColor)
                .ToArray();
        if (_pendingExclusionClass != PdfVectorClass.None)
        {
            HashSet<int> classifiedMarkup = ResolveVectorClass(_pendingExclusionClass).ToHashSet();
            _vectorHitCandidates = _vectorHitCandidates
                .Where(classifiedMarkup.Contains)
                .ToArray();
        }
        if (_vectorHitCandidates.Count == 0)
        {
            _selectedVectorSegmentId = -1;
            Find<TextBlock>("v2_vector_pick_text").Text = _pendingExclusionClass != PdfVectorClass.None
                ? "No classified markup at this point. Click the colored isolated line, or drag a rectangle across it."
                : "No vector line at this point. Zoom in and click directly on a visible PDF line.";
            RedrawVectorHighlights();
            return;
        }

        _selectedVectorSegmentId = ChooseBestVectorHit(_vectorHitCandidates);
        _vectorSelectionScope = _pendingVectorClass == PdfVectorClass.Sprinkler ||
                                _pendingExclusionClass != PdfVectorClass.None
            ? PdfVectorSelectionScope.PdfObject
            : PdfVectorSelectionScope.Line;
        if (_pendingExclusionClass != PdfVectorClass.None)
            ExcludeCurrentVectorObject(_pendingExclusionClass);
        else if (_pendingVectorClass != PdfVectorClass.None)
            AssignCurrentVectorPick(_pendingVectorClass);
        else
            UpdateVectorSelectionText();
        RedrawVectorHighlights();
        canvas.Focus();
        eventArgs.Handled = true;
    }

    private int ChooseBestVectorHit(IReadOnlyList<int> candidates)
    {
        if (_vectorScene is null || candidates.Count == 0) return -1;
        if (_pendingVectorClass != PdfVectorClass.Sprinkler &&
            _pendingExclusionClass == PdfVectorClass.None)
            return candidates[0];

        return candidates
            .Select((id, index) =>
            {
                PdfVectorSegment segment = _vectorScene.Segments[id];
                PdfVectorPathMetrics metrics = _vectorScene.GetPathMetrics(id);
                int red = (segment.ColorArgb >> 16) & 255;
                int green = (segment.ColorArgb >> 8) & 255;
                int blue = segment.ColorArgb & 255;
                int colorRange = Math.Max(red, Math.Max(green, blue)) - Math.Min(red, Math.Min(green, blue));
                bool chromatic = colorRange >= 70;
                bool symbolSized = metrics.Diagonal is > 0.00015 and < 0.035;
                bool balanced = metrics.AspectRatio is > 0.28 and < 3.6;
                double score =
                    (chromatic ? 100 : 0) +
                    (symbolSized ? 35 : 0) +
                    (balanced ? 25 : 0) +
                    (metrics.SegmentCount >= 4 ? 18 : 0) -
                    index * 0.5;
                return (Id: id, Score: score);
            })
            .OrderByDescending(item => item.Score)
            .Select(item => item.Id)
            .First();
    }

    private void HoverVectorAtPointer(MouseEventArgs eventArgs)
    {
        if (_vectorScene is null || _hoverVectorHighlight is null || eventArgs.MiddleButton == MouseButtonState.Pressed)
            return;
        Canvas canvas = Find<Canvas>("v2_vector_selection_canvas");
        if (!TryGetNormalizedPdfPointer(eventArgs, 8, out double x, out double y,
                out double toleranceX, out double toleranceY))
        {
            ClearVectorHover();
            return;
        }
        IReadOnlyList<int> hits = _vectorScene.HitTest(
            x,
            y,
            toleranceX,
            toleranceY,
            HideCadArchitectureDuringScan ? 30 : 1);
        if (HideCadArchitectureDuringScan)
            hits = hits.Where(IsSelectedCadScanColor).Take(1).ToArray();
        int hit = hits.Count == 0
            ? -1
            : _pendingVectorClass == PdfVectorClass.Sprinkler
                ? ChooseBestVectorHit(hits)
                : hits[0];
        if (hit == _hoveredVectorSegmentId) return;
        _hoveredVectorSegmentId = hit;
        if (hit < 0)
        {
            _hoverVectorHighlight.Visibility = WpfVisibility.Collapsed;
            if (_hoverVectorObjectHighlight is not null)
                _hoverVectorObjectHighlight.Visibility = WpfVisibility.Collapsed;
            return;
        }

        PdfVectorSegment segment = _vectorScene.Segments[hit];
        if (_pendingVectorClass == PdfVectorClass.Sprinkler && _hoverVectorObjectHighlight is not null)
        {
            IReadOnlyList<int> symbolSegments = _vectorScene.ResolveSelection(
                hit,
                PdfVectorSelectionScope.PdfObject,
                600);
            _hoverVectorObjectHighlight.Data = GeometryForSegments(symbolSegments);
            _hoverVectorObjectHighlight.Visibility = WpfVisibility.Visible;
            _hoverVectorHighlight.Visibility = WpfVisibility.Collapsed;
            return;
        }

        if (_hoverVectorObjectHighlight is not null)
            _hoverVectorObjectHighlight.Visibility = WpfVisibility.Collapsed;
        _hoverVectorHighlight.X1 = segment.X1 * canvas.Width;
        _hoverVectorHighlight.Y1 = segment.Y1 * canvas.Height;
        _hoverVectorHighlight.X2 = segment.X2 * canvas.Width;
        _hoverVectorHighlight.Y2 = segment.Y2 * canvas.Height;
        _hoverVectorHighlight.Visibility = WpfVisibility.Visible;
    }

    private bool TryGetNormalizedPdfPointer(
        MouseEventArgs eventArgs,
        double screenTolerance,
        out double x,
        out double y,
        out double toleranceX,
        out double toleranceY)
    {
        x = y = toleranceX = toleranceY = 0;
        Image image = Find<Image>("v2_layer_pdf_image");
        WpfGrid host = Find<WpfGrid>("v2_layer_pdf_host");
        double pageWidth = image.ActualWidth > 1 ? image.ActualWidth : image.Width;
        double pageHeight = image.ActualHeight > 1 ? image.ActualHeight : image.Height;
        if (!PortableMath.IsFinite(pageWidth) || !PortableMath.IsFinite(pageHeight) || pageWidth <= 1 || pageHeight <= 1)
            return false;

        // Ask WPF for the image's real on-screen origin. This includes ScrollViewer
        // offsets, padding, layout transforms and high zoom, avoiding offset drift.
        System.Windows.Point pointer = eventArgs.GetPosition(host);
        System.Windows.Point pageOrigin;
        try
        {
            pageOrigin = image.TranslatePoint(new System.Windows.Point(0, 0), host);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        double pageX = pointer.X - pageOrigin.X;
        double pageY = pointer.Y - pageOrigin.Y;
        if (pageX < -screenTolerance || pageY < -screenTolerance ||
            pageX > pageWidth + screenTolerance || pageY > pageHeight + screenTolerance)
            return false;

        x = Math.Max(0, Math.Min(1, pageX / pageWidth));
        y = Math.Max(0, Math.Min(1, pageY / pageHeight));
        toleranceX = screenTolerance / pageWidth;
        toleranceY = screenTolerance / pageHeight;
        return true;
    }

    private void ClearVectorHover()
    {
        _hoveredVectorSegmentId = -1;
        if (_hoverVectorHighlight is not null)
            _hoverVectorHighlight.Visibility = WpfVisibility.Collapsed;
        if (_hoverVectorObjectHighlight is not null)
            _hoverVectorObjectHighlight.Visibility = WpfVisibility.Collapsed;
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key == Key.Escape && (_removeAreaScanMode || _removeAreaDragging))
        {
            CancelRemoveAreaMode("Remove/Sweep mode cancelled.");
            eventArgs.Handled = true;
            return;
        }
        if (eventArgs.Key == Key.Escape && (_cadAreaScanMode || _cadAreaDragging))
        {
            _cadAreaScanMode = false;
            _cadAreaDragging = false;
            Canvas canvas = Find<Canvas>("v2_vector_selection_canvas");
            canvas.ReleaseMouseCapture();
            canvas.Cursor = Cursors.Arrow;
            if (_cadAreaRectangle is not null)
                _cadAreaRectangle.Visibility = WpfVisibility.Collapsed;
            if (_cadSweepLine is not null)
                _cadSweepLine.Visibility = WpfVisibility.Collapsed;
            ResetCadAreaScanButton();
            Status.Text = "CAD area scan cancelled.";
            eventArgs.Handled = true;
            return;
        }
        if (eventArgs.Key != Key.Tab || MainTabs.SelectedIndex != 1 ||
            _vectorScene is null || _selectedVectorSegmentId < 0)
            return;
        int direction = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1;
        int count = Enum.GetValues(typeof(PdfVectorSelectionScope)).Length;
        int next = ((int)_vectorSelectionScope + direction + count) % count;
        _vectorSelectionScope = (PdfVectorSelectionScope)next;
        if (_lastAssignedVectorClass != PdfVectorClass.None &&
            _activeVectorPick is not null &&
            _activeVectorPick.SegmentId == _selectedVectorSegmentId)
        {
            _activeVectorPick.Scope = _vectorSelectionScope.ToString();
            LayerMappingItem? item = _layerItems.FirstOrDefault(candidate =>
                candidate.Marker == MarkerForClass(_lastAssignedVectorClass));
            if (item is not null)
                item.Detected = CountVectorDetected(_lastAssignedVectorClass);
            ApplyExclusiveVectorOwnership(_lastAssignedVectorClass);
            RefreshFittingTopology();
            Find<DataGrid>("v2_layer_grid").Items.Refresh();
            UpdateVectorPickSummary();
            SaveMappingSidecar();
        }
        UpdateVectorSelectionText();
        RedrawVectorHighlights();
        eventArgs.Handled = true;
    }

    private void FindSimilarVectorGeometry()
    {
        if (_vectorScene is null || _selectedVectorSegmentId < 0)
        {
            Status.Text = "Pick one exact PDF line first, then use Find similar.";
            return;
        }
        if (_lastAssignedVectorClass == PdfVectorClass.Sprinkler)
        {
            PdfVectorPathMetrics metrics = _vectorScene.GetPathMetrics(_selectedVectorSegmentId);
            bool cadRepeatedObject = IsSmartCadRecognition() && metrics.SegmentCount >= 2;
            if ((!metrics.IsClosed && !cadRepeatedObject) || metrics.Width <= 0 || metrics.Height <= 0 ||
                metrics.AspectRatio is <= 0.28 or >= 3.6)
            {
                Find<TextBlock>("v2_vector_pick_text").Text =
                    "Rejected: this seed is an open or elongated line, so it looks like pipe/fitting rather than a sprinkler symbol.";
                Status.Text = "Find Similar rejected an invalid sprinkler seed";
                return;
            }
        }
        _vectorSelectionScope = PdfVectorSelectionScope.SimilarStyle;
        if (_lastAssignedVectorClass != PdfVectorClass.None)
        {
            AssignCurrentVectorPick(_lastAssignedVectorClass, keepPickMode: true);
            SetVectorIsolation(true);
            int detected = CountVectorDetected(_lastAssignedVectorClass);
            Status.Text = IsCadSource
                ? $"CAD Find Similar: {detected:N0} exact color/line-weight object(s). Review Isolate and add another seed if needed."
                : $"Find Similar complete: {detected:N0} object(s).";
        }
        else
        {
            UpdateVectorSelectionText();
            RedrawVectorHighlights();
        }
    }

    private void BeginExcludeWrongVectorObject()
    {
        if (_vectorScene is null)
        {
            Status.Text = "Load a vector PDF before removing markup geometry.";
            return;
        }
        PdfVectorClass vectorClass = CurrentVectorClass();
        if (vectorClass == PdfVectorClass.None || !_vectorPicks.ContainsKey(vectorClass))
        {
            Status.Text = "Select a seeded Main, Branch, or Sprinkler group first.";
            return;
        }
        if (_removeAreaScanMode && _pendingExclusionClass == vectorClass)
        {
            CancelRemoveAreaMode("Remove mode cancelled.");
            return;
        }
        _pendingVectorClass = PdfVectorClass.None;
        _pendingExclusionClass = vectorClass;
        _removeAreaScanMode = true;
        _removeAreaDragging = false;
        _vectorSelectionScope = PdfVectorSelectionScope.PdfObject;
        SetPickButtonState(PdfVectorClass.None);
        Button excludeButton = Find<Button>("v2_exclude_wrong_btn");
        excludeButton.Background = Brush("#FFE7EA");
        excludeButton.BorderBrush = Brush("#D9364F");
        excludeButton.BorderThickness = new Thickness(2);
        string label = VectorClassLabel(vectorClass);
        Find<TextBlock>("v2_vector_pick_text").Text =
            $"Remove from {label}: click one highlighted object, or drag a rectangle across all wrong isolated markup. CAD geometry remains unchanged.";
        Status.Text = $"Remove/Sweep mode active for {label}";
        Canvas canvas = Find<Canvas>("v2_vector_selection_canvas");
        canvas.Cursor = Cursors.Cross;
        canvas.Focus();
    }

    private void BeginRemoveAreaDrag(MouseButtonEventArgs eventArgs)
    {
        if (!_removeAreaScanMode || _pendingExclusionClass == PdfVectorClass.None) return;
        Canvas canvas = Find<Canvas>("v2_vector_selection_canvas");
        _removeAreaStart = ClampToCanvas(eventArgs.GetPosition(canvas), canvas);
        _removeAreaDragging = true;
        if (_cadAreaRectangle is not null)
        {
            _cadAreaRectangle.Stroke = Brush("#D9364F");
            _cadAreaRectangle.Fill = new SolidColorBrush(WpfColor.FromArgb(42, 217, 54, 79));
            Canvas.SetLeft(_cadAreaRectangle, _removeAreaStart.X);
            Canvas.SetTop(_cadAreaRectangle, _removeAreaStart.Y);
            _cadAreaRectangle.Width = 0;
            _cadAreaRectangle.Height = 0;
            _cadAreaRectangle.Visibility = WpfVisibility.Visible;
        }
        canvas.CaptureMouse();
        eventArgs.Handled = true;
    }

    private void UpdateRemoveAreaDrag(MouseEventArgs eventArgs)
    {
        if (!_removeAreaDragging || _cadAreaRectangle is null) return;
        Canvas canvas = Find<Canvas>("v2_vector_selection_canvas");
        System.Windows.Point current = ClampToCanvas(eventArgs.GetPosition(canvas), canvas);
        Canvas.SetLeft(_cadAreaRectangle, Math.Min(_removeAreaStart.X, current.X));
        Canvas.SetTop(_cadAreaRectangle, Math.Min(_removeAreaStart.Y, current.Y));
        _cadAreaRectangle.Width = Math.Abs(current.X - _removeAreaStart.X);
        _cadAreaRectangle.Height = Math.Abs(current.Y - _removeAreaStart.Y);
        eventArgs.Handled = true;
    }

    private void CompleteRemoveAreaDrag(MouseButtonEventArgs eventArgs)
    {
        Canvas canvas = Find<Canvas>("v2_vector_selection_canvas");
        System.Windows.Point current = ClampToCanvas(eventArgs.GetPosition(canvas), canvas);
        canvas.ReleaseMouseCapture();
        _removeAreaDragging = false;
        if (_cadAreaRectangle is not null)
            _cadAreaRectangle.Visibility = WpfVisibility.Collapsed;
        double pixelWidth = Math.Abs(current.X - _removeAreaStart.X);
        double pixelHeight = Math.Abs(current.Y - _removeAreaStart.Y);
        if (pixelWidth < 7 && pixelHeight < 7)
        {
            SelectVectorAtPointer(eventArgs, allowActiveRemoveMode: true);
            eventArgs.Handled = true;
            return;
        }

        var bounds = new Rect(
            Math.Min(_removeAreaStart.X, current.X) / Math.Max(canvas.Width, 1),
            Math.Min(_removeAreaStart.Y, current.Y) / Math.Max(canvas.Height, 1),
            pixelWidth / Math.Max(canvas.Width, 1),
            pixelHeight / Math.Max(canvas.Height, 1));
        ExcludeVectorArea(_pendingExclusionClass, bounds);
        eventArgs.Handled = true;
    }

    private void ExcludeVectorArea(PdfVectorClass vectorClass, Rect bounds)
    {
        if (_vectorScene is null || vectorClass == PdfVectorClass.None) return;
        int[] touched = ResolveVectorClass(vectorClass)
            .Where(id => SegmentIntersectsBounds(_vectorScene.Segments[id], bounds))
            .Distinct()
            .ToArray();
        if (touched.Length == 0)
        {
            Find<TextBlock>("v2_vector_pick_text").Text =
                "No highlighted markup crosses this rectangle. Drag across the colored isolated lines, not the faded CAD background.";
            Status.Text = "Remove sweep found no classified markup";
            return;
        }

        if (vectorClass == PdfVectorClass.Sprinkler)
        {
            if (!_vectorExcludedPathIds.TryGetValue(vectorClass, out HashSet<int>? paths))
                _vectorExcludedPathIds[vectorClass] = paths = [];
            paths.UnionWith(touched.Select(id => _vectorScene.Segments[id].PathId));
        }
        else
        {
            if (!_vectorExcludedSegmentIds.TryGetValue(vectorClass, out HashSet<int>? segments))
                _vectorExcludedSegmentIds[vectorClass] = segments = [];
            segments.UnionWith(touched);
        }

        UpdateVectorClassItem(vectorClass);
        UpdateLayerSelection();
        UpdateVectorPickSummary();
        RefreshFittingTopology();
        SaveMappingSidecar();
        RedrawVectorHighlights();
        int removedCount = ExclusionCount(vectorClass);
        Find<TextBlock>("v2_vector_pick_text").Text =
            $"Removed {touched.Length:N0} highlighted {VectorClassLabel(vectorClass)} line(s) inside the rectangle. {removedCount:N0} saved removal(s); click or sweep again if needed.";
        Status.Text = $"Markup sweep removed {touched.Length:N0} line(s)";
    }

    private static bool SegmentIntersectsBounds(PdfVectorSegment segment, Rect bounds)
    {
        if (bounds.Contains(segment.X1, segment.Y1) || bounds.Contains(segment.X2, segment.Y2))
            return true;
        double minX = Math.Min(segment.X1, segment.X2);
        double maxX = Math.Max(segment.X1, segment.X2);
        double minY = Math.Min(segment.Y1, segment.Y2);
        double maxY = Math.Max(segment.Y1, segment.Y2);
        if (maxX < bounds.Left || minX > bounds.Right ||
            maxY < bounds.Top || minY > bounds.Bottom)
            return false;

        double dx = segment.X2 - segment.X1;
        double dy = segment.Y2 - segment.Y1;
        double t0 = 0;
        double t1 = 1;
        return Clip(-dx, segment.X1 - bounds.Left, ref t0, ref t1) &&
               Clip(dx, bounds.Right - segment.X1, ref t0, ref t1) &&
               Clip(-dy, segment.Y1 - bounds.Top, ref t0, ref t1) &&
               Clip(dy, bounds.Bottom - segment.Y1, ref t0, ref t1);

        static bool Clip(double p, double q, ref double first, ref double second)
        {
            if (Math.Abs(p) < 1e-12) return q >= 0;
            double ratio = q / p;
            if (p < 0)
            {
                if (ratio > second) return false;
                if (ratio > first) first = ratio;
            }
            else
            {
                if (ratio < first) return false;
                if (ratio < second) second = ratio;
            }
            return true;
        }
    }

    private void CancelRemoveAreaMode(string status)
    {
        _removeAreaScanMode = false;
        _removeAreaDragging = false;
        _pendingExclusionClass = PdfVectorClass.None;
        Canvas canvas = Find<Canvas>("v2_vector_selection_canvas");
        canvas.ReleaseMouseCapture();
        canvas.Cursor = Cursors.Arrow;
        if (_cadAreaRectangle is not null)
            _cadAreaRectangle.Visibility = WpfVisibility.Collapsed;
        Button button = Find<Button>("v2_exclude_wrong_btn");
        button.ClearValue(System.Windows.Controls.Control.BackgroundProperty);
        button.ClearValue(System.Windows.Controls.Control.BorderBrushProperty);
        button.ClearValue(System.Windows.Controls.Control.BorderThicknessProperty);
        Status.Text = status;
    }

    private void ExcludeCurrentVectorObject(PdfVectorClass vectorClass)
    {
        if (_vectorScene is null || _selectedVectorSegmentId < 0) return;
        int pathId = _vectorScene.Segments[_selectedVectorSegmentId].PathId;
        if (!_vectorExcludedPathIds.TryGetValue(vectorClass, out HashSet<int>? excluded))
            _vectorExcludedPathIds[vectorClass] = excluded = [];
        excluded.Add(pathId);
        _selectedVectorSegmentId = -1;
        UpdateVectorClassItem(vectorClass);
        UpdateLayerSelection();
        UpdateVectorPickSummary();
        RefreshFittingTopology();
        SaveMappingSidecar();
        RedrawVectorHighlights();
        Find<TextBlock>("v2_vector_pick_text").Text =
            $"Removed PDF object {pathId} from {VectorClassLabel(vectorClass)}. It is hidden from markup and excluded from Revit creation. {ExclusionCount(vectorClass):N0} removal(s) saved; click or sweep again if needed.";
        Status.Text = $"Object removed from {VectorClassLabel(vectorClass)} markup";
    }

    private void ClearVectorExclusions()
    {
        PdfVectorClass vectorClass = CurrentVectorClass();
        if (vectorClass == PdfVectorClass.None)
        {
            Status.Text = "Select Main, Branch, or Sprinkler before restoring removed objects.";
            return;
        }
        _vectorExcludedPathIds.Remove(vectorClass);
        _vectorExcludedSegmentIds.Remove(vectorClass);
        CancelRemoveAreaMode($"Removed {VectorClassLabel(vectorClass)} objects restored");
        Find<Button>("v2_exclude_wrong_btn").ClearValue(System.Windows.Controls.Control.BackgroundProperty);
        Find<Button>("v2_exclude_wrong_btn").ClearValue(System.Windows.Controls.Control.BorderBrushProperty);
        Find<Button>("v2_exclude_wrong_btn").ClearValue(System.Windows.Controls.Control.BorderThicknessProperty);
        UpdateVectorClassItem(vectorClass);
        UpdateLayerSelection();
        UpdateVectorPickSummary();
        RefreshFittingTopology();
        SaveMappingSidecar();
        RedrawVectorHighlights();
        Find<TextBlock>("v2_vector_pick_text").Text =
            $"All removed objects restored to {VectorClassLabel(vectorClass)} markup.";
        Status.Text = $"Removed {VectorClassLabel(vectorClass)} objects restored";
    }

    private void RemoveLastVectorSeed()
    {
        PdfVectorClass vectorClass = CurrentVectorClass();
        if (vectorClass == PdfVectorClass.None ||
            !_additionalVectorPicks.TryGetValue(vectorClass, out List<PdfVectorPickSnapshot>? additional) ||
            additional.Count == 0)
        {
            Find<TextBlock>("v2_vector_pick_text").Text =
                "There is no additional seed to remove. Use Reset only if you want to clear the entire group.";
            Status.Text = "No additional seed found";
            return;
        }

        PdfVectorPickSnapshot removed = additional[^1];
        additional.RemoveAt(additional.Count - 1);
        if (additional.Count == 0)
            _additionalVectorPicks.Remove(vectorClass);
        _activeVectorPick = _vectorPicks.GetValueOrDefault(vectorClass);
        _selectedVectorSegmentId = -1;
        ApplyExclusiveVectorOwnership(vectorClass);
        UpdateVectorClassItem(vectorClass);
        UpdateVectorPickSummary();
        RefreshFittingTopology();
        SaveMappingSidecar();
        RedrawVectorHighlights();
        int remaining = VectorPicksFor(vectorClass).Count();
        Find<TextBlock>("v2_vector_pick_text").Text =
            $"Removed the last {VectorClassLabel(vectorClass)} seed (PDF object {removed.SegmentId}). {remaining} seed(s) remain.";
        Status.Text = $"Last {VectorClassLabel(vectorClass)} seed removed";
    }

    private void SetVectorIsolation(bool isolate)
    {
        if (_vectorScene is null)
        {
            Status.Text = "Load and classify a vector PDF before isolating results.";
            return;
        }
        PdfVectorClass vectorClass = CurrentVectorClass();
        if (isolate && (vectorClass == PdfVectorClass.None || !_vectorPicks.ContainsKey(vectorClass)))
        {
            Status.Text = "Select a classified Main, Branch, or Sprinkler row first.";
            return;
        }
        _isolateVectorResult = isolate;
        if (isolate && Find<CheckBox>("v2_show_overlay_cb").IsChecked != true)
            Find<CheckBox>("v2_show_overlay_cb").IsChecked = true;
        // Isolate is a verification mode: keep only a faint reference drawing
        // and make the selected classification unambiguously visible.
        Find<Image>("v2_layer_pdf_image").Opacity = isolate
            ? IsCadSource ? 0.08 : 0.10
            : 1.0;
        Button isolateButton = Find<Button>("v2_isolate_result_btn");
        isolateButton.Background = isolate ? Brush("#DDF5F4") : Brush("#FFFFFF");
        isolateButton.BorderBrush = isolate ? Brush("#0AA6A6") : Brush("#D8E0E6");
        isolateButton.BorderThickness = new Thickness(isolate ? 2 : 1);
        RedrawVectorHighlights();
        ApplyMarkupLineWeights();
        ApplyCadArchitectureVisibility();
        if (isolate)
        {
            int count = ResolveVectorClass(vectorClass).Count;
            string countLabel = vectorClass == PdfVectorClass.Sprinkler
                ? $"{CountVectorDetected(vectorClass):N0} symbol(s) • {count:N0} vector line(s)"
                : $"{count:N0} vector line(s)";
            Find<TextBlock>("v2_vector_pick_text").Text =
                $"Isolated {VectorClassLabel(vectorClass)} • {countLabel}. Inspect the result, then remove wrong objects or Show all.";
            Status.Text = $"Isolated Find Similar result: {VectorClassLabel(vectorClass)}";
        }
        else
        {
            Find<TextBlock>("v2_vector_pick_text").Text =
                "All PDF geometry is visible. Select a group and use Isolate result to recheck it.";
            Status.Text = "Full PDF restored";
        }
    }

    private void ApplyVectorIsolationVisuals()
    {
        if (_mainVectorHighlight is null || _branchVectorHighlight is null ||
            _sprinklerVectorHighlight is null || _fittingVectorHighlight is null || _currentVectorHighlight is null ||
            _excludedVectorHighlight is null)
            return;
        PdfVectorClass focus = CurrentVectorClass();
        _mainVectorHighlight.Visibility = !_isolateVectorResult || focus == PdfVectorClass.MainPipe
            ? WpfVisibility.Visible : WpfVisibility.Collapsed;
        _branchVectorHighlight.Visibility = !_isolateVectorResult || focus == PdfVectorClass.BranchPipe
            ? WpfVisibility.Visible : WpfVisibility.Collapsed;
        _sprinklerVectorHighlight.Visibility = !_isolateVectorResult || focus == PdfVectorClass.Sprinkler
            ? WpfVisibility.Visible : WpfVisibility.Collapsed;
        _fittingVectorHighlight.Visibility = _isolateVectorResult
            ? WpfVisibility.Collapsed : WpfVisibility.Visible;
        _currentVectorHighlight.Visibility = _isolateVectorResult
            ? WpfVisibility.Collapsed : WpfVisibility.Visible;
        // Removed objects remain untouched in the source PDF, but disappear from
        // classification markup until the user chooses Restore removed.
        _excludedVectorHighlight.Visibility = WpfVisibility.Collapsed;
        if (_hoverVectorHighlight is not null && _isolateVectorResult)
            _hoverVectorHighlight.Visibility = WpfVisibility.Collapsed;

        for (int marker = 1; marker <= 3; marker++)
        {
            PdfVectorClass markerClass = ClassForMarker(marker);
            Button markerButton = Find<Button>($"v2_marker_{marker}_btn");
            markerButton.Visibility = _vectorPicks.ContainsKey(markerClass) &&
                                      (!_isolateVectorResult || markerClass == focus)
                ? WpfVisibility.Visible
                : WpfVisibility.Collapsed;
        }
    }

    private IReadOnlyList<int> ResolveVectorPick(PdfVectorClass vectorClass, PdfVectorPickSnapshot snapshot)
    {
        if (_vectorScene is null)
            return [];
        if (snapshot.GeometryAnalysis && snapshot.ExplicitSegmentIds.Count > 0)
        {
            IReadOnlyList<int> analyzed = snapshot.ExplicitSegmentIds
                .Where(id => id >= 0 && id < _vectorScene.Segments.Length &&
                             (!IsCadSource || !IsCadSegmentHidden(id)))
                .Distinct()
                .ToArray();
            _vectorExcludedPathIds.TryGetValue(vectorClass, out HashSet<int>? analyzedExcludedPaths);
            _vectorExcludedSegmentIds.TryGetValue(vectorClass, out HashSet<int>? analyzedExcludedSegments);
            return analyzed.Where(id =>
                    (analyzedExcludedPaths is null ||
                     !analyzedExcludedPaths.Contains(_vectorScene.Segments[id].PathId)) &&
                    (analyzedExcludedSegments is null || !analyzedExcludedSegments.Contains(id)))
                .ToArray();
        }
        if (snapshot.SegmentId < 0 || snapshot.SegmentId >= _vectorScene.Segments.Length)
            return [];
        PdfVectorSelectionScope scope = Enum.TryParse(snapshot.Scope, out PdfVectorSelectionScope parsed)
            ? parsed
            : PdfVectorSelectionScope.Line;
        IReadOnlyList<int> source;
        // LineStrokeScan is authoritative. Older/current UI interactions may
        // reset ColorAreaScan while preserving the saved sweep coordinates;
        // falling through here would incorrectly expand the seed through
        // SimilarStyle and turn one swept line into hundreds of CAD vectors.
        if ((snapshot.ColorAreaScan || snapshot.LineStrokeScan) && IsCadSource)
        {
            source = ResolveCadColorAreaSelection(vectorClass, snapshot);
        }
        else if (snapshot.ExactCadLayer && IsCadSource)
        {
            int seedPathId = _vectorScene.Segments[snapshot.SegmentId].PathId;
            if (!_cadLayerByPathId.TryGetValue(seedPathId, out string? seedLayer))
                return [];
            source = _vectorScene.Segments
                .Where(segment =>
                    _cadLayerByPathId.TryGetValue(segment.PathId, out string? layer) &&
                    string.Equals(layer, seedLayer, StringComparison.OrdinalIgnoreCase))
                .Select(segment => segment.Id)
                .Take(12000)
                .ToArray();
        }
        else if (IsCadSource && scope == PdfVectorSelectionScope.SimilarStyle)
        {
            CadPixelSignature seedColor = GetCadPixelSignature(snapshot.SegmentId);
            source = vectorClass == PdfVectorClass.Sprinkler
                ? ResolveCadRepeatedSymbol(snapshot.SegmentId, seedColor)
                : ResolveCadPipeColorSimilar(snapshot.SegmentId, seedColor);
        }
        else if (vectorClass == PdfVectorClass.Sprinkler &&
                                    scope == PdfVectorSelectionScope.SimilarStyle
        )
            source = _vectorScene.ResolveShapeSimilar(snapshot.SegmentId);
        else
            source = _vectorScene.ResolveSelection(snapshot.SegmentId, scope);
        _vectorExcludedPathIds.TryGetValue(vectorClass, out HashSet<int>? excludedPaths);
        _vectorExcludedSegmentIds.TryGetValue(vectorClass, out HashSet<int>? excludedSegments);
        if (IsCadSource)
            source = source.Where(id => !IsCadSegmentHidden(id)).ToArray();
        if ((excludedPaths is null || excludedPaths.Count == 0) &&
            (excludedSegments is null || excludedSegments.Count == 0))
            return source;
        return source.Where(id =>
                (excludedPaths is null || !excludedPaths.Contains(_vectorScene.Segments[id].PathId)) &&
                (excludedSegments is null || !excludedSegments.Contains(id)))
            .ToArray();
    }

    private IReadOnlyList<int> ResolveCadColorAreaSelection(
        PdfVectorClass vectorClass,
        PdfVectorPickSnapshot snapshot)
    {
        if (_vectorScene is null) return [];
        var bounds = new Rect(
            PortableMath.Clamp(snapshot.ScanLeft, 0, 1),
            PortableMath.Clamp(snapshot.ScanTop, 0, 1),
            PortableMath.Clamp(snapshot.ScanRight - snapshot.ScanLeft, 0, 1),
            PortableMath.Clamp(snapshot.ScanBottom - snapshot.ScanTop, 0, 1));
        var desiredColor = new CadPixelSignature(
            snapshot.ScanHue,
            snapshot.ScanSaturation,
            snapshot.ScanValue,
            snapshot.ScanSaturation >= 0.42);
        if (!desiredColor.IsChromatic) return [];

        IReadOnlyList<int> candidates;
        if (vectorClass == PdfVectorClass.Sprinkler)
        {
            // The swept rectangle defines one sample symbol. Once a circular or
            // closed seed is found, search the full drawing for repeated symbols
            // of the same native CAD color and geometry.
            candidates = ResolveCadRepeatedSymbol(snapshot.SegmentId, desiredColor)
                .Where(id =>
                {
                    PdfVectorPathMetrics metrics = _vectorScene.GetPathMetrics(id);
                    return IsSprinklerShapeCandidate(metrics);
                })
                .Distinct()
                .Take(24000)
                .ToArray();
        }
        else
        {
            // Main/Branch use a user-controlled window. Every straight vector
            // must both intersect that window and match the native CAD color.
            // No disconnected same-color geometry outside the window is added.
            candidates = ResolveCadPipeAreaSelection(vectorClass, bounds, desiredColor);
        }
        return candidates;
    }

    private IReadOnlyList<int> ResolveCadPipeAreaSelection(
        PdfVectorClass vectorClass,
        Rect bounds,
        CadPixelSignature desiredColor)
    {
        if (_vectorScene is null) return [];
        int[] colorLines = _vectorScene.Segments
            .Where(segment =>
                IsSegmentInBounds(segment, bounds) &&
                IsCadScanColorFamily(segment.Id, desiredColor) &&
                GetCadScanFamilyCoverage(segment.Id, desiredColor) >= 0.66 &&
                IsCadOrthogonal(segment) &&
                !IsCadCompactSymbolPath(segment.Id))
            .Select(segment => segment.Id)
            .Distinct()
            .Take(24000)
            .ToArray();
        if (colorLines.Length == 0) return [];

        var accepted = colorLines
            .Where(id => IsPipeScanCandidate(_vectorScene.Segments[id], vectorClass))
            .ToHashSet();
        if (accepted.Count == 0) return [];

        // PDF-to-DWG conversion often breaks a physical pipe at tees, elbows or
        // text masks. Add only short, collinear fragments connected to a strong
        // accepted pipe. This fills genuine gaps without reintroducing isolated
        // ticks, symbols or pale architectural strokes.
        double shortMinimum = vectorClass == PdfVectorClass.MainPipe ? 18.0 : 10.0;
        int[] remaining = colorLines
            .Where(id => !accepted.Contains(id) &&
                         PhysicalCadSegmentLength(_vectorScene.Segments[id]) >= shortMinimum)
            .ToArray();
        for (int pass = 0; pass < 5 && remaining.Length > 0; pass++)
        {
            int[] additions = remaining
                .Where(id => IsCollinearFragmentConnected(id, accepted, 35.0))
                .ToArray();
            if (additions.Length == 0) break;
            accepted.UnionWith(additions);
            HashSet<int> added = additions.ToHashSet();
            remaining = remaining.Where(id => !added.Contains(id)).ToArray();
        }
        return accepted.Take(24000).ToArray();
    }

    private bool IsCadCompactSymbolPath(int segmentId)
    {
        if (_vectorScene is null) return true;
        PdfVectorPathMetrics metrics = _vectorScene.GetPathMetrics(segmentId);
        if (metrics.IsClosed || IsSprinklerShapeCandidate(metrics)) return true;
        double width = metrics.Width * _vectorScene.PageWidth;
        double height = metrics.Height * _vectorScene.PageHeight;
        double diagonal = Math.Sqrt(width * width + height * height);
        return metrics.SegmentCount >= 4 &&
               diagonal <= 250.0 &&
               width >= 2.0 && height >= 2.0;
    }

    private bool IsCollinearFragmentConnected(
        int candidateId,
        ISet<int> accepted,
        double toleranceMillimeters)
    {
        if (_vectorScene is null) return false;
        PdfVectorSegment candidate = _vectorScene.Segments[candidateId];
        foreach (int acceptedId in accepted)
        {
            PdfVectorSegment pipe = _vectorScene.Segments[acceptedId];
            if (PhysicalParallelism(candidate, pipe) < 0.985) continue;
            if (DistanceBetweenCadSegments(candidate, pipe) <= toleranceMillimeters)
                return true;
        }
        return false;
    }

    private double DistanceBetweenCadSegments(PdfVectorSegment first, PdfVectorSegment second)
    {
        if (_vectorScene is null) return double.MaxValue;
        var firstStart = new System.Windows.Point(
            first.X1 * _vectorScene.PageWidth,
            first.Y1 * _vectorScene.PageHeight);
        var firstEnd = new System.Windows.Point(
            first.X2 * _vectorScene.PageWidth,
            first.Y2 * _vectorScene.PageHeight);
        var secondStart = new System.Windows.Point(
            second.X1 * _vectorScene.PageWidth,
            second.Y1 * _vectorScene.PageHeight);
        var secondEnd = new System.Windows.Point(
            second.X2 * _vectorScene.PageWidth,
            second.Y2 * _vectorScene.PageHeight);
        if (CadSegmentsIntersect(firstStart, firstEnd, secondStart, secondEnd)) return 0;
        return Math.Min(
            Math.Min(
                DistancePointToSegment(firstStart, secondStart, secondEnd),
                DistancePointToSegment(firstEnd, secondStart, secondEnd)),
            Math.Min(
                DistancePointToSegment(secondStart, firstStart, firstEnd),
                DistancePointToSegment(secondEnd, firstStart, firstEnd)));
    }

    private double PhysicalParallelism(PdfVectorSegment first, PdfVectorSegment second)
    {
        if (_vectorScene is null) return 0;
        double firstX = (first.X2 - first.X1) * _vectorScene.PageWidth;
        double firstY = (first.Y2 - first.Y1) * _vectorScene.PageHeight;
        double secondX = (second.X2 - second.X1) * _vectorScene.PageWidth;
        double secondY = (second.Y2 - second.Y1) * _vectorScene.PageHeight;
        double denominator = Math.Sqrt(firstX * firstX + firstY * firstY) *
                             Math.Sqrt(secondX * secondX + secondY * secondY);
        return denominator <= 1e-9
            ? 0
            : Math.Abs((firstX * secondX + firstY * secondY) / denominator);
    }

    private double DistanceBetweenCadSegmentsMillimeters(
        PdfVectorSegment candidate,
        System.Windows.Point normalizedStart,
        System.Windows.Point normalizedEnd)
    {
        if (_vectorScene is null) return double.MaxValue;
        var firstStart = new System.Windows.Point(
            candidate.X1 * _vectorScene.PageWidth,
            candidate.Y1 * _vectorScene.PageHeight);
        var firstEnd = new System.Windows.Point(
            candidate.X2 * _vectorScene.PageWidth,
            candidate.Y2 * _vectorScene.PageHeight);
        var secondStart = new System.Windows.Point(
            normalizedStart.X * _vectorScene.PageWidth,
            normalizedStart.Y * _vectorScene.PageHeight);
        var secondEnd = new System.Windows.Point(
            normalizedEnd.X * _vectorScene.PageWidth,
            normalizedEnd.Y * _vectorScene.PageHeight);

        if (CadSegmentsIntersect(firstStart, firstEnd, secondStart, secondEnd))
            return 0;
        return Math.Min(
            Math.Min(
                DistancePointToSegment(firstStart, secondStart, secondEnd),
                DistancePointToSegment(firstEnd, secondStart, secondEnd)),
            Math.Min(
                DistancePointToSegment(secondStart, firstStart, firstEnd),
                DistancePointToSegment(secondEnd, firstStart, firstEnd)));
    }

    private static double DistancePointToSegment(
        System.Windows.Point point,
        System.Windows.Point start,
        System.Windows.Point end)
    {
        Vector direction = end - start;
        double lengthSquared = direction.X * direction.X + direction.Y * direction.Y;
        if (lengthSquared <= 1e-9) return (point - start).Length;
        Vector fromStart = point - start;
        double fraction = PortableMath.Clamp(
            (fromStart.X * direction.X + fromStart.Y * direction.Y) / lengthSquared,
            0,
            1);
        var projection = new System.Windows.Point(
            start.X + fraction * direction.X,
            start.Y + fraction * direction.Y);
        return (point - projection).Length;
    }

    private static bool CadSegmentsIntersect(
        System.Windows.Point firstStart,
        System.Windows.Point firstEnd,
        System.Windows.Point secondStart,
        System.Windows.Point secondEnd)
    {
        const double epsilon = 1e-7;
        double firstA = Cross(firstStart, firstEnd, secondStart);
        double firstB = Cross(firstStart, firstEnd, secondEnd);
        double secondA = Cross(secondStart, secondEnd, firstStart);
        double secondB = Cross(secondStart, secondEnd, firstEnd);
        if (((firstA > epsilon && firstB < -epsilon) || (firstA < -epsilon && firstB > epsilon)) &&
            ((secondA > epsilon && secondB < -epsilon) || (secondA < -epsilon && secondB > epsilon)))
            return true;
        return Math.Abs(firstA) <= epsilon && IsPointOnSegment(firstStart, firstEnd, secondStart, epsilon) ||
               Math.Abs(firstB) <= epsilon && IsPointOnSegment(firstStart, firstEnd, secondEnd, epsilon) ||
               Math.Abs(secondA) <= epsilon && IsPointOnSegment(secondStart, secondEnd, firstStart, epsilon) ||
               Math.Abs(secondB) <= epsilon && IsPointOnSegment(secondStart, secondEnd, firstEnd, epsilon);
    }

    private static double Cross(
        System.Windows.Point start,
        System.Windows.Point end,
        System.Windows.Point point) =>
        (end.X - start.X) * (point.Y - start.Y) -
        (end.Y - start.Y) * (point.X - start.X);

    private static bool IsPointOnSegment(
        System.Windows.Point start,
        System.Windows.Point end,
        System.Windows.Point point,
        double epsilon) =>
        point.X >= Math.Min(start.X, end.X) - epsilon &&
        point.X <= Math.Max(start.X, end.X) + epsilon &&
        point.Y >= Math.Min(start.Y, end.Y) - epsilon &&
        point.Y <= Math.Max(start.Y, end.Y) + epsilon;

    private IReadOnlyList<int> FilterCadPipeScanTopology(
        IReadOnlyList<int> source,
        PdfVectorClass vectorClass,
        double seedLength)
    {
        if (_vectorScene is null || source.Count == 0) return [];
        seedLength = Math.Max(seedLength, source.Max(id =>
            PhysicalCadSegmentLength(_vectorScene.Segments[id])));

        // First collapse exact/near-exact duplicate strokes generated when the
        // PDF was imported into AutoCAD.  Classification must represent one
        // physical centerline, not every overprinted vector copy.
        const double duplicateGridMillimeters = 6.0;
        var representatives = new Dictionary<(long X1, long Y1, long X2, long Y2), int>();
        foreach (int id in source.OrderByDescending(id =>
                     PhysicalCadSegmentLength(_vectorScene.Segments[id])))
        {
            PdfVectorSegment segment = _vectorScene.Segments[id];
            (long X, long Y) first = Quantize(segment.X1, segment.Y1);
            (long X, long Y) second = Quantize(segment.X2, segment.Y2);
            var key = first.X < second.X || first.X == second.X && first.Y <= second.Y
                ? (first.X, first.Y, second.X, second.Y)
                : (second.X, second.Y, first.X, first.Y);
            representatives.TryAdd(key, id);
        }

        int[] unique = representatives.Values.ToArray();
        var available = unique.ToHashSet();
        double longStandaloneMinimum = seedLength *
                                       (vectorClass == PdfVectorClass.MainPipe ? 0.10 : 0.085);
        double connectedMinimum = seedLength *
                                  (vectorClass == PdfVectorClass.MainPipe ? 0.035 : 0.028);
        var accepted = new List<int>(unique.Length);
        foreach (int id in unique)
        {
            PdfVectorSegment segment = _vectorScene.Segments[id];
            double length = PhysicalCadSegmentLength(segment);
            bool firstConnected = HasPipeConnection(id, segment.X1, segment.Y1, available);
            bool secondConnected = HasPipeConnection(id, segment.X2, segment.Y2, available);
            int connectedEnds = (firstConnected ? 1 : 0) + (secondConnected ? 1 : 0);

            // Keep substantial standalone pipe runs. Short pieces must belong to
            // a real run at both ends; this rejects text strokes, head ticks and
            // isolated architectural fragments that merely share the MEP color.
            if (length >= longStandaloneMinimum ||
                connectedEnds == 2 && length >= connectedMinimum ||
                connectedEnds == 1 && length >= longStandaloneMinimum * 0.72)
                accepted.Add(id);
        }
        return accepted.Take(24000).ToArray();

        (long X, long Y) Quantize(double x, double y) =>
            ((long)Math.Round(x * _vectorScene.PageWidth / duplicateGridMillimeters),
             (long)Math.Round(y * _vectorScene.PageHeight / duplicateGridMillimeters));
    }

    private bool HasPipeConnection(
        int segmentId,
        double x,
        double y,
        ISet<int> available)
    {
        if (_vectorScene is null) return false;
        const double toleranceMillimeters = 35.0;
        double toleranceX = toleranceMillimeters / Math.Max(_vectorScene.PageWidth, 1.0);
        double toleranceY = toleranceMillimeters / Math.Max(_vectorScene.PageHeight, 1.0);
        foreach (int candidateId in _vectorScene.HitTest(x, y, toleranceX, toleranceY, 80))
        {
            if (candidateId == segmentId || !available.Contains(candidateId)) continue;
            PdfVectorSegment candidate = _vectorScene.Segments[candidateId];
            if (DistanceToCadSegmentMillimeters(candidate, x, y) <= toleranceMillimeters)
                return true;
        }
        return false;
    }

    private double DistanceToCadSegmentMillimeters(PdfVectorSegment segment, double x, double y)
    {
        if (_vectorScene is null) return double.MaxValue;
        double x1 = segment.X1 * _vectorScene.PageWidth;
        double y1 = segment.Y1 * _vectorScene.PageHeight;
        double x2 = segment.X2 * _vectorScene.PageWidth;
        double y2 = segment.Y2 * _vectorScene.PageHeight;
        double px = x * _vectorScene.PageWidth;
        double py = y * _vectorScene.PageHeight;
        double dx = x2 - x1;
        double dy = y2 - y1;
        double lengthSquared = dx * dx + dy * dy;
        if (lengthSquared < 1e-9)
            return Math.Sqrt(Math.Pow(px - x1, 2) + Math.Pow(py - y1, 2));
        double parameter = PortableMath.Clamp(((px - x1) * dx + (py - y1) * dy) / lengthSquared, 0, 1);
        double nearestX = x1 + parameter * dx;
        double nearestY = y1 + parameter * dy;
        return Math.Sqrt(Math.Pow(px - nearestX, 2) + Math.Pow(py - nearestY, 2));
    }

    private IReadOnlyList<int> ResolveSmartFlattenedSelection(
        PdfVectorClass vectorClass,
        int seedSegmentId)
    {
        if (_vectorScene is null || seedSegmentId < 0 || seedSegmentId >= _vectorScene.Segments.Length)
            return [];

        PdfVectorSegment seed = _vectorScene.Segments[seedSegmentId];
        CadPixelSignature seedColor = GetCadPixelSignature(seedSegmentId);
        if (vectorClass == PdfVectorClass.Sprinkler)
        {
            IReadOnlyList<int> shapeCandidates = _vectorScene.ResolveShapeSimilar(seedSegmentId)
                .Concat(ResolveCadRepeatedSymbol(seedSegmentId, seedColor))
                .Distinct()
                .Take(24000)
                .ToArray();
            if (!seedColor.IsChromatic) return shapeCandidates;
            HashSet<int> matchingPaths = shapeCandidates
                .Where(id => IsSimilarCadColor(seedColor, GetCadPixelSignature(id)))
                .Select(id => _vectorScene.Segments[id].PathId)
                .ToHashSet();
            return shapeCandidates
                .Where(id => matchingPaths.Contains(_vectorScene.Segments[id].PathId))
                .Take(24000)
                .ToArray();
        }
        if (!seedColor.IsChromatic)
            return _vectorScene.ResolveSelection(
                seedSegmentId,
                PdfVectorSelectionScope.PdfObject,
                400);
        IReadOnlyList<int> connected = TraceConnectedCadPipeNetwork(
            seedSegmentId,
            seedColor,
            6000);
        return connected;
    }

    private IReadOnlyList<int> ResolveCadRepeatedSymbol(
        int seedSegmentId,
        CadPixelSignature seedColor)
    {
        if (_vectorScene is null) return [];
        PdfVectorPathMetrics seed = _vectorScene.GetPathMetrics(seedSegmentId);
        if (seed.Width <= 0 || seed.Height <= 0 ||
            (!seed.IsClosed && seed.SegmentCount < 2) ||
            seed.AspectRatio is <= 0.22 or >= 4.5)
            return [];
        double seedDiagonal = Math.Max(seed.Diagonal, 1e-9);
        var matches = new List<int>();
        foreach (IGrouping<int, PdfVectorSegment> path in _vectorScene.Segments.GroupBy(item => item.PathId))
        {
            PdfVectorSegment first = path.First();
            PdfVectorPathMetrics candidate = _vectorScene.GetPathMetrics(first.Id);
            if (candidate.Width <= 0 || candidate.Height <= 0) continue;
            if (seed.IsClosed != candidate.IsClosed) continue;
            if (Math.Abs(candidate.SegmentCount - seed.SegmentCount) > Math.Max(2, seed.SegmentCount / 4))
                continue;
            if (candidate.Diagonal < seedDiagonal * 0.72 || candidate.Diagonal > seedDiagonal * 1.38)
                continue;
            if (candidate.AspectRatio < seed.AspectRatio * 0.72 ||
                candidate.AspectRatio > seed.AspectRatio * 1.38)
                continue;
            if (Math.Abs(candidate.RadialVariation - seed.RadialVariation) > 0.14)
                continue;
            if (seedColor.IsChromatic &&
                (!IsSimilarCadColor(seedColor, GetCadPixelSignature(first.Id)) ||
                 IsCadSegmentHidden(first.Id)))
                continue;
            foreach (PdfVectorSegment segment in path)
            {
                matches.Add(segment.Id);
                if (matches.Count >= 24000) return matches;
            }
        }
        return matches;
    }

    private IReadOnlyList<int> ResolveCadPipeColorSimilar(
        int seedSegmentId,
        CadPixelSignature seedColor)
    {
        if (_vectorScene is null || !seedColor.IsChromatic) return [];
        PdfVectorSegment seed = _vectorScene.Segments[seedSegmentId];
        double seedLength = PhysicalCadSegmentLength(seed);
        double minimumLength = Math.Max(2.0, Math.Min(15.0, seedLength * 0.015));
        bool seedOrthogonal = IsCadOrthogonal(seed);
        float weightTolerance = Math.Max(0.05f, Math.Abs(seed.StrokeWidth) * 0.08f);
        var matches = new List<int>();
        foreach (PdfVectorSegment candidate in _vectorScene.Segments)
        {
            double length = PhysicalCadSegmentLength(candidate);
            if (length < minimumLength) continue;
            if (seedOrthogonal && !IsCadOrthogonal(candidate)) continue;
            PdfVectorPathMetrics metrics = _vectorScene.GetPathMetrics(candidate.Id);
            double diagonal = Math.Sqrt(
                Math.Pow(metrics.Width * _vectorScene.PageWidth, 2) +
                Math.Pow(metrics.Height * _vectorScene.PageHeight, 2));
            if (metrics.IsClosed && diagonal < 160.0) continue;
            if (IsCadCompactSymbolPath(candidate.Id)) continue;
            if (Math.Abs(candidate.StrokeWidth - seed.StrokeWidth) > weightTolerance) continue;
            if (!IsExactCadStyleColor(seedColor, GetCadPixelSignature(candidate.Id))) continue;
            if (GetCadColorCoverage(candidate.Id, seedColor) < 0.66) continue;
            matches.Add(candidate.Id);
            if (matches.Count >= 24000) break;
        }
        return matches;
    }

    private static bool IsExactCadStyleColor(CadPixelSignature first, CadPixelSignature second) =>
        first.IsChromatic && second.IsChromatic &&
        HueDistance(first.Hue, second.Hue) <= 5.0 &&
        Math.Abs(first.Saturation - second.Saturation) <= 0.14 &&
        Math.Abs(first.Value - second.Value) <= 0.14;

    private double PhysicalCadSegmentLength(PdfVectorSegment segment)
    {
        if (_vectorScene is null) return 0;
        double dx = (segment.X2 - segment.X1) * _vectorScene.PageWidth;
        double dy = (segment.Y2 - segment.Y1) * _vectorScene.PageHeight;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static bool IsCadOrthogonal(PdfVectorSegment segment)
    {
        double dx = Math.Abs(segment.X2 - segment.X1);
        double dy = Math.Abs(segment.Y2 - segment.Y1);
        return Math.Min(dx, dy) / Math.Max(segment.Length, 1e-12) <= 0.10;
    }

    private IReadOnlyList<int> TraceConnectedCadPipeNetwork(
        int seedSegmentId,
        CadPixelSignature seedColor,
        int maximum = 16000)
    {
        if (_vectorScene is null) return [];
        // Imported PDF-to-DWG geometry is commonly split around fitting graphics.
        // Allow a small physical gap while still requiring the same exact color
        // and endpoint direction.
        const double connectionGapMillimeters = 45.0;
        const double minimumSegmentMillimeters = 1.5;
        double toleranceX = PortableMath.Clamp(
            connectionGapMillimeters / Math.Max(_vectorScene.PageWidth, 1.0),
            0.000015,
            0.0012);
        double toleranceY = PortableMath.Clamp(
            connectionGapMillimeters / Math.Max(_vectorScene.PageHeight, 1.0),
            0.000015,
            0.0012);
        var accepted = new HashSet<int>();
        var queued = new HashSet<int>();
        var queue = new Queue<int>();

        void QueueSegment(int segmentId)
        {
            if (accepted.Count >= maximum || !accepted.Add(segmentId)) return;
            if (queued.Add(segmentId)) queue.Enqueue(segmentId);
        }

        // A flattened PDF may store thousands of unrelated entities in one CAD
        // path/layer. Expanding the whole path was the reason one blue seed lit
        // up much of the drawing. Traverse actual endpoint connectivity only.
        QueueSegment(seedSegmentId);
        while (queue.Count > 0 && accepted.Count < maximum)
        {
            int currentId = queue.Dequeue();
            PdfVectorSegment current = _vectorScene.Segments[currentId];
            TraceEndpoint(currentId, current.X1, current.Y1);
            TraceEndpoint(currentId, current.X2, current.Y2);
        }
        return accepted.Take(maximum).ToArray();

        void TraceEndpoint(int currentId, double x, double y)
        {
            PdfVectorSegment current = _vectorScene.Segments[currentId];
            var candidates = new List<(int Id, double Parallelism)>();
            foreach (int candidateId in _vectorScene.HitTest(x, y, toleranceX, toleranceY, 120))
            {
                if (accepted.Contains(candidateId) || candidateId == currentId) continue;
                PdfVectorSegment candidate = _vectorScene.Segments[candidateId];
                if (!HasEndpointNear(candidate, x, y, connectionGapMillimeters)) continue;
                double physicalLength = Math.Sqrt(
                    Math.Pow((candidate.X2 - candidate.X1) * _vectorScene.PageWidth, 2) +
                    Math.Pow((candidate.Y2 - candidate.Y1) * _vectorScene.PageHeight, 2));
                if (physicalLength < minimumSegmentMillimeters) continue;
                PdfVectorPathMetrics metrics = _vectorScene.GetPathMetrics(candidateId);
                double physicalDiagonal = Math.Sqrt(
                    Math.Pow(metrics.Width * _vectorScene.PageWidth, 2) +
                    Math.Pow(metrics.Height * _vectorScene.PageHeight, 2));
                if (metrics.IsClosed && physicalDiagonal < 90.0) continue;
                CadPixelSignature candidateColor = GetCadPixelSignature(candidateId);
                if (seedColor.IsChromatic && !IsSimilarCadColor(seedColor, candidateColor))
                    continue;
                candidates.Add((candidateId, Parallelism(current, candidate)));
            }
            if (candidates.Count == 0) return;

            // At a tee/cross, continue straight and stop before entering a branch.
            // At a genuine elbow, there is only one possible outgoing segment.
            (int Id, double Parallelism)[] straight = candidates
                .Where(item => item.Parallelism >= 0.90)
                .ToArray();
            IEnumerable<int> next = straight.Length > 0
                ? straight.Select(item => item.Id)
                : candidates.Count == 1
                    ? [candidates[0].Id]
                    : [];
            foreach (int nextId in next)
            {
                QueueSegment(nextId);
                if (accepted.Count >= maximum) break;
            }
        }

        bool HasEndpointNear(
            PdfVectorSegment segment,
            double x,
            double y,
            double maximumDistanceMillimeters)
        {
            double first = EndpointDistanceMillimeters(segment.X1, segment.Y1, x, y);
            double second = EndpointDistanceMillimeters(segment.X2, segment.Y2, x, y);
            return Math.Min(first, second) <= maximumDistanceMillimeters;
        }

        double EndpointDistanceMillimeters(double ax, double ay, double bx, double by)
        {
            double dx = (ax - bx) * _vectorScene.PageWidth;
            double dy = (ay - by) * _vectorScene.PageHeight;
            return Math.Sqrt(dx * dx + dy * dy);
        }
    }

    private static double Parallelism(PdfVectorSegment first, PdfVectorSegment second)
    {
        double ax = first.X2 - first.X1;
        double ay = first.Y2 - first.Y1;
        double bx = second.X2 - second.X1;
        double by = second.Y2 - second.Y1;
        double denominator = Math.Max(first.Length * second.Length, 1e-12);
        return Math.Abs((ax * bx + ay * by) / denominator);
    }

    private double GetCadColorCoverage(int segmentId, CadPixelSignature desired)
    {
        if (_vectorScene is null || segmentId < 0 || segmentId >= _vectorScene.Segments.Length ||
            !desired.IsChromatic)
            return 0;
        var key = (
            segmentId,
            (int)Math.Round(desired.Hue / 2.0),
            (int)Math.Round(desired.Saturation * 20.0),
            (int)Math.Round(desired.Value * 20.0));
        if (_cadColorCoverageCache.TryGetValue(key, out double cached))
            return cached;

        if (_cadNativeColorBySegmentId.TryGetValue(segmentId, out int nativeColor))
        {
            CadPixelSignature native = ToCadPixelSignature(
                (byte)((nativeColor >> 16) & 255),
                (byte)((nativeColor >> 8) & 255),
                (byte)(nativeColor & 255));
            if (native.IsChromatic)
            {
                double nativeCoverage = IsSimilarCadColor(desired, native) ? 1.0 : 0.0;
                _cadColorCoverageCache[key] = nativeCoverage;
                return nativeCoverage;
            }
        }

        if (_cadPreviewPixels is null || _cadPreviewPixelWidth <= 0 || _cadPreviewPixelHeight <= 0)
            return 0;

        PdfVectorSegment segment = _vectorScene.Segments[segmentId];
        double score = 0;
        double[] positions = [0.04, 0.12, 0.20, 0.28, 0.36, 0.44, 0.52, 0.60, 0.68, 0.76, 0.84, 0.92, 0.98];
        foreach (double position in positions)
        {
            double normalizedX = segment.X1 + (segment.X2 - segment.X1) * position;
            double normalizedY = segment.Y1 + (segment.Y2 - segment.Y1) * position;
            int centerX = (int)Math.Round(normalizedX * (_cadPreviewPixelWidth - 1));
            int centerY = (int)Math.Round(normalizedY * (_cadPreviewPixelHeight - 1));
            double pointScore = 0;
            for (int offsetY = -1; offsetY <= 1; offsetY++)
            for (int offsetX = -1; offsetX <= 1; offsetX++)
            {
                int x = PortableMath.Clamp(centerX + offsetX, 0, _cadPreviewPixelWidth - 1);
                int y = PortableMath.Clamp(centerY + offsetY, 0, _cadPreviewPixelHeight - 1);
                int offset = y * _cadPreviewPixelStride + x * 4;
                CadPixelSignature pixel = ToCadPixelSignature(
                    _cadPreviewPixels[offset + 2],
                    _cadPreviewPixels[offset + 1],
                    _cadPreviewPixels[offset]);
                if (!IsSimilarCadColor(desired, pixel)) continue;
                int distanceSquared = offsetX * offsetX + offsetY * offsetY;
                double weight = distanceSquared switch
                {
                    0 => 1.0,
                    1 => 0.72,
                    _ => 0.55
                };
                pointScore = Math.Max(pointScore, weight);
            }
            score += pointScore;
        }
        double coverage = score / positions.Length;
        _cadColorCoverageCache[key] = coverage;
        return coverage;
    }

    private static bool IsSimilarCadColor(CadPixelSignature seed, CadPixelSignature candidate)
    {
        if (!seed.IsChromatic || !candidate.IsChromatic) return false;
        // DWG colors are discrete vector properties, so use a much tighter
        // tolerance than raster PDF colors. This prevents nearby cyan/gray or a
        // pale architectural variant from joining a pure blue MEP run.
        return HueDistance(seed.Hue, candidate.Hue) <= 4.0 &&
               candidate.Saturation >= Math.Max(0.58, seed.Saturation * 0.82) &&
               Math.Abs(seed.Saturation - candidate.Saturation) <= 0.16 &&
               candidate.Value >= seed.Value * 0.78 &&
               Math.Abs(seed.Value - candidate.Value) <= 0.18;
    }

    private bool IsExactCadScanColor(int segmentId, CadPixelSignature desired)
    {
        if (!desired.IsChromatic || IsCadSegmentHidden(segmentId)) return false;
        if (_cadNativeColorBySegmentId.TryGetValue(segmentId, out int nativeArgb))
        {
            CadPixelSignature native = ToCadPixelSignature(
                (byte)((nativeArgb >> 16) & 255),
                (byte)((nativeArgb >> 8) & 255),
                (byte)(nativeArgb & 255));
            if (native.IsChromatic)
            {
                return HueDistance(desired.Hue, native.Hue) <= 1.5 &&
                       Math.Abs(desired.Saturation - native.Saturation) <= 0.08 &&
                       Math.Abs(desired.Value - native.Value) <= 0.08;
            }
        }

        // Flattened PDF-to-DWG entities sometimes arrive through Revit with a
        // gray subcategory. Only in that case use the rendered true-color
        // samples, with a tighter threshold than ordinary SimilarStyle.
        CadPixelSignature sampled = GetCadPixelSignature(segmentId);
        return sampled.IsChromatic &&
               HueDistance(desired.Hue, sampled.Hue) <= 2.0 &&
               Math.Abs(desired.Saturation - sampled.Saturation) <= 0.10 &&
               Math.Abs(desired.Value - sampled.Value) <= 0.12;
    }

    private double GetCadScanColorCoverage(int segmentId, CadPixelSignature desired)
    {
        if (IsCadSegmentHidden(segmentId)) return 0.0;
        if (_cadNativeColorBySegmentId.TryGetValue(segmentId, out int nativeArgb))
        {
            CadPixelSignature native = ToCadPixelSignature(
                (byte)((nativeArgb >> 16) & 255),
                (byte)((nativeArgb >> 8) & 255),
                (byte)(nativeArgb & 255));
            if (native.IsChromatic)
                return IsExactCadScanColor(segmentId, desired) ? 1.0 : 0.0;
        }
        return GetCadColorCoverage(segmentId, desired);
    }

    private bool IsCadScanColorFamily(int segmentId, CadPixelSignature desired)
    {
        if (!desired.IsChromatic || IsCadSegmentHidden(segmentId)) return false;
        if (_cadNativeColorBySegmentId.TryGetValue(segmentId, out int nativeArgb))
        {
            CadPixelSignature native = ToCadPixelSignature(
                (byte)((nativeArgb >> 16) & 255),
                (byte)((nativeArgb >> 8) & 255),
                (byte)(nativeArgb & 255));
            if (native.IsChromatic)
                return IsSimilarCadColor(desired, native);
        }
        return IsSimilarCadColor(desired, GetCadPixelSignature(segmentId));
    }

    private double GetCadScanFamilyCoverage(int segmentId, CadPixelSignature desired)
    {
        if (IsCadSegmentHidden(segmentId)) return 0.0;
        if (_cadNativeColorBySegmentId.TryGetValue(segmentId, out int nativeArgb))
        {
            CadPixelSignature native = ToCadPixelSignature(
                (byte)((nativeArgb >> 16) & 255),
                (byte)((nativeArgb >> 8) & 255),
                (byte)(nativeArgb & 255));
            if (native.IsChromatic)
                return IsSimilarCadColor(desired, native) ? 1.0 : 0.0;
        }
        return GetCadColorCoverage(segmentId, desired);
    }

    private IEnumerable<PdfVectorPickSnapshot> VectorPicksFor(PdfVectorClass vectorClass)
    {
        if (_vectorPicks.TryGetValue(vectorClass, out PdfVectorPickSnapshot? primary))
            yield return primary;
        if (_additionalVectorPicks.TryGetValue(vectorClass, out List<PdfVectorPickSnapshot>? additional))
            foreach (PdfVectorPickSnapshot snapshot in additional)
                yield return snapshot;
    }

    private IReadOnlyList<int> ResolveVectorClass(PdfVectorClass vectorClass) =>
        VectorPicksFor(vectorClass)
            .SelectMany(snapshot => ResolveVectorPick(vectorClass, snapshot))
            .Distinct()
            .ToArray();

    private int CountVectorDetected(PdfVectorClass vectorClass)
    {
        IReadOnlyList<int> segments = ResolveVectorClass(vectorClass);
        return vectorClass == PdfVectorClass.Sprinkler && _vectorScene is not null
            ? ResolveSprinklerObjects().Count
            : segments.Count;
    }

    private IReadOnlyList<System.Windows.Point> ResolveSprinklerObjects()
    {
        if (_vectorScene is null) return [];
        PdfVectorPathMetrics[] paths = ResolveVectorClass(PdfVectorClass.Sprinkler)
            .GroupBy(id => _vectorScene.Segments[id].PathId)
            .Select(group => _vectorScene.GetPathMetrics(group.First()))
            .Where(metrics => (metrics.IsClosed || (IsSmartCadRecognition() && metrics.SegmentCount >= 2)) &&
                              metrics.Width > 0 && metrics.Height > 0 &&
                              metrics.AspectRatio is > 0.28 and < 3.6)
            .Where(metrics => !_fittingCandidates.Any(fitting =>
                Math.Abs((fitting.X - metrics.CenterX) * _vectorScene.PageWidth) <= 4.0 &&
                Math.Abs((fitting.Y - metrics.CenterY) * _vectorScene.PageHeight) <= 4.0))
            .ToArray();
        if (paths.Length == 0) return [];

        double[] diagonals = paths
            .Select(metrics => Math.Sqrt(
                Math.Pow(metrics.Width * _vectorScene.PageWidth, 2) +
                Math.Pow(metrics.Height * _vectorScene.PageHeight, 2)))
            .OrderBy(value => value)
            .ToArray();
        double medianDiagonal = diagonals[diagonals.Length / 2];
        // PDF writers often emit one filled sprinkler dot as several nearby
        // sub-path groups. Count the physical symbol cluster, not its
        // individual drawing paths or lobes.
        double clusterTolerancePoints = Math.Max(3.0, medianDiagonal * 5.0);
        var clusters = new List<SprinklerObjectCluster>();
        foreach (PdfVectorPathMetrics path in paths)
        {
            SprinklerObjectCluster? nearest = null;
            double nearestDistance = double.MaxValue;
            foreach (SprinklerObjectCluster cluster in clusters)
            {
                double dx = (cluster.CenterX - path.CenterX) * _vectorScene.PageWidth;
                double dy = (cluster.CenterY - path.CenterY) * _vectorScene.PageHeight;
                double distance = Math.Sqrt(dx * dx + dy * dy);
                if (distance <= clusterTolerancePoints && distance < nearestDistance)
                {
                    nearest = cluster;
                    nearestDistance = distance;
                }
            }
            if (nearest is null)
            {
                nearest = new SprinklerObjectCluster();
                clusters.Add(nearest);
            }
            nearest.Add(path.CenterX, path.CenterY);
        }
        return clusters
            .Select(cluster => new System.Windows.Point(cluster.CenterX, cluster.CenterY))
            .ToArray();
    }

    private sealed class SprinklerObjectCluster
    {
        private double _xSum;
        private double _ySum;
        private int _count;
        internal double CenterX => _xSum / Math.Max(1, _count);
        internal double CenterY => _ySum / Math.Max(1, _count);

        internal void Add(double x, double y)
        {
            _xSum += x;
            _ySum += y;
            _count++;
        }
    }

    private IReadOnlyList<PdfPipeRun> ResolvePipeRuns(PdfVectorClass vectorClass)
    {
        if (_vectorScene is null) return [];
        double pageWidth = _vectorScene.PageWidth;
        double pageHeight = _vectorScene.PageHeight;
        PdfPipeFragment[] fragments = ResolveVectorClass(vectorClass)
            .Select(id => _vectorScene.Segments[id])
            .Select(segment =>
            {
                double x1 = segment.X1 * pageWidth;
                double y1 = segment.Y1 * pageHeight;
                double x2 = segment.X2 * pageWidth;
                double y2 = segment.Y2 * pageHeight;
                double dx = x2 - x1;
                double dy = y2 - y1;
                double length = Math.Sqrt(dx * dx + dy * dy);
                PdfPipeAxis axis = Math.Abs(dy) <= Math.Max(0.45, Math.Abs(dx) * 0.035)
                    ? PdfPipeAxis.Horizontal
                    : Math.Abs(dx) <= Math.Max(0.45, Math.Abs(dy) * 0.035)
                        ? PdfPipeAxis.Vertical
                        : PdfPipeAxis.Diagonal;
                return new PdfPipeFragment(x1, y1, x2, y2, length, axis);
            })
            .Where(fragment => fragment.Length >= 2.0)
            .ToArray();

        var runs = new List<PdfPipeRun>();
        MergeAxisFragments(fragments.Where(item => item.Axis == PdfPipeAxis.Horizontal), true, vectorClass, runs);
        MergeAxisFragments(fragments.Where(item => item.Axis == PdfPipeAxis.Vertical), false, vectorClass, runs);
        runs.AddRange(fragments
            .Where(item => item.Axis == PdfPipeAxis.Diagonal && item.Length >= 8.0)
            .Select(item => new PdfPipeRun(item.X1, item.Y1, item.X2, item.Y2, vectorClass)));
        return runs;
    }

    private static void MergeAxisFragments(
        IEnumerable<PdfPipeFragment> source,
        bool horizontal,
        PdfVectorClass vectorClass,
        ICollection<PdfPipeRun> output)
    {
        const double lineTolerancePoints = 1.4;
        const double joinGapPoints = 2.8;
        foreach (IGrouping<long, PdfPipeFragment> group in source.GroupBy(fragment =>
                     (long)Math.Round((horizontal
                         ? (fragment.Y1 + fragment.Y2) * 0.5
                         : (fragment.X1 + fragment.X2) * 0.5) / lineTolerancePoints)))
        {
            PdfPipeFragment[] fragments = group.ToArray();
            double fixedCoordinate = fragments.Average(fragment => horizontal
                ? (fragment.Y1 + fragment.Y2) * 0.5
                : (fragment.X1 + fragment.X2) * 0.5);
            (double Start, double End)[] intervals = fragments
                .Select(fragment => horizontal
                    ? (Math.Min(fragment.X1, fragment.X2), Math.Max(fragment.X1, fragment.X2))
                    : (Math.Min(fragment.Y1, fragment.Y2), Math.Max(fragment.Y1, fragment.Y2)))
                .OrderBy(interval => interval.Item1)
                .ToArray();
            if (intervals.Length == 0) continue;
            double start = intervals[0].Start;
            double end = intervals[0].End;
            for (int index = 1; index <= intervals.Length; index++)
            {
                if (index < intervals.Length && intervals[index].Start <= end + joinGapPoints)
                {
                    end = Math.Max(end, intervals[index].End);
                    continue;
                }
                if (end - start >= 4.0)
                {
                    output.Add(horizontal
                        ? new PdfPipeRun(start, fixedCoordinate, end, fixedCoordinate, vectorClass)
                        : new PdfPipeRun(fixedCoordinate, start, fixedCoordinate, end, vectorClass));
                }
                if (index < intervals.Length)
                {
                    start = intervals[index].Start;
                    end = intervals[index].End;
                }
            }
        }
    }

    private static IReadOnlyList<PdfPipeRun> SplitPipeRuns(IReadOnlyList<PdfPipeRun> source)
    {
        const double intersectionTolerancePoints = 1.6;
        PdfPipeRun[] adjustedRuns = SnapPipeRunEndpoints(source);
        var result = new List<PdfPipeRun>();
        for (int index = 0; index < adjustedRuns.Length; index++)
        {
            PdfPipeRun run = adjustedRuns[index];
            double dx = run.X2 - run.X1;
            double dy = run.Y2 - run.Y1;
            double lengthSquared = dx * dx + dy * dy;
            double length = Math.Sqrt(lengthSquared);
            if (length < 0.01) continue;
            var parameters = new List<double> { 0, 1 };
            for (int otherIndex = 0; otherIndex < adjustedRuns.Length; otherIndex++)
            {
                if (otherIndex == index) continue;
                PdfPipeRun other = adjustedRuns[otherIndex];
                double sx = other.X2 - other.X1;
                double sy = other.Y2 - other.Y1;
                double cross = dx * sy - dy * sx;
                double qx = other.X1 - run.X1;
                double qy = other.Y1 - run.Y1;
                if (Math.Abs(cross) > 0.000001)
                {
                    double t = (qx * sy - qy * sx) / cross;
                    double u = (qx * dy - qy * dx) / cross;
                    if (t >= -0.0001 && t <= 1.0001 && u >= -0.0001 && u <= 1.0001)
                        parameters.Add(PortableMath.Clamp(t, 0, 1));
                    continue;
                }

                AddProjectedEndpoint(other.X1, other.Y1);
                AddProjectedEndpoint(other.X2, other.Y2);

                void AddProjectedEndpoint(double x, double y)
                {
                    double t = ((x - run.X1) * dx + (y - run.Y1) * dy) / lengthSquared;
                    if (t <= 0 || t >= 1) return;
                    double projectedX = run.X1 + t * dx;
                    double projectedY = run.Y1 + t * dy;
                    double distance = Math.Sqrt(Math.Pow(projectedX - x, 2) + Math.Pow(projectedY - y, 2));
                    if (distance <= intersectionTolerancePoints)
                        parameters.Add(t);
                }
            }

            double parameterTolerance = Math.Min(0.01, 0.75 / length);
            double[] splitParameters = parameters
                .OrderBy(value => value)
                .Aggregate(new List<double>(), (items, value) =>
                {
                    if (items.Count == 0 || value - items[^1] > parameterTolerance)
                        items.Add(value);
                    return items;
                })
                .ToArray();
            for (int part = 0; part < splitParameters.Length - 1; part++)
            {
                double t1 = splitParameters[part];
                double t2 = splitParameters[part + 1];
                if ((t2 - t1) * length < 1.0) continue;
                result.Add(new PdfPipeRun(
                    run.X1 + t1 * dx,
                    run.Y1 + t1 * dy,
                    run.X1 + t2 * dx,
                    run.Y1 + t2 * dy,
                    run.VectorClass));
            }
        }
        return result;
    }

    private static IReadOnlyList<PdfPipeRun> CollapseNearDuplicatePipeRuns(
        IEnumerable<PdfPipeRun> source,
        double centerlineTolerancePoints)
    {
        // Vector PDFs commonly draw both pipe edges plus repeated color strokes.
        // Treat strongly overlapping, near-parallel runs as one centerline so a
        // single physical junction cannot generate several Revit pipes/fittings.
        var collapsed = new List<PdfPipeRun>();
        foreach (PdfPipeRun candidate in source
                     .OrderByDescending(PipeRunLength))
        {
            int duplicateIndex = -1;
            for (int index = 0; index < collapsed.Count; index++)
            {
                if (AreNearDuplicatePipeRuns(
                        collapsed[index],
                        candidate,
                        centerlineTolerancePoints))
                {
                    duplicateIndex = index;
                    break;
                }
            }
            if (duplicateIndex < 0)
            {
                collapsed.Add(candidate);
                continue;
            }
            collapsed[duplicateIndex] = MergeDuplicatePipeRuns(collapsed[duplicateIndex], candidate);
        }
        return collapsed;
    }

    private static IReadOnlyList<PdfPipeRun> MergeConnectedCollinearPipeRuns(
        IEnumerable<PdfPipeRun> source,
        double endpointTolerancePoints)
    {
        // A fitting glyph or PDF stroke often divides one straight physical pipe
        // into two vectors whose directions differ by a fraction of a degree.
        // Merge those vectors before splitting at real junctions. This removes
        // the shallow V/Y centerlines which prevent Revit from creating a Tee.
        var runs = source
            .Where(run => PipeRunLength(run) >= 1.0)
            .OrderByDescending(PipeRunLength)
            .ToList();
        bool changed;
        do
        {
            changed = false;
            for (int first = 0; first < runs.Count && !changed; first++)
            for (int second = first + 1; second < runs.Count; second++)
            {
                PdfPipeRun a = runs[first];
                PdfPipeRun b = runs[second];
                if (a.VectorClass != b.VectorClass) continue;
                double aDx = a.X2 - a.X1;
                double aDy = a.Y2 - a.Y1;
                double bDx = b.X2 - b.X1;
                double bDy = b.Y2 - b.Y1;
                double aLength = Math.Sqrt(aDx * aDx + aDy * aDy);
                double bLength = Math.Sqrt(bDx * bDx + bDy * bDy);
                if (aLength < 0.01 || bLength < 0.01) continue;
                double parallel = Math.Abs((aDx * bDx + aDy * bDy) / (aLength * bLength));
                if (parallel < 0.999) continue;

                double endpointDistance = new[]
                {
                    Distance2D(a.X1, a.Y1, b.X1, b.Y1),
                    Distance2D(a.X1, a.Y1, b.X2, b.Y2),
                    Distance2D(a.X2, a.Y2, b.X1, b.Y1),
                    Distance2D(a.X2, a.Y2, b.X2, b.Y2)
                }.Min();
                if (endpointDistance > endpointTolerancePoints) continue;
                double closestNormalOffset = Math.Min(
                    Math.Abs((b.X1 - a.X1) * aDy - (b.Y1 - a.Y1) * aDx) / aLength,
                    Math.Abs((b.X2 - a.X1) * aDy - (b.Y2 - a.Y1) * aDx) / aLength);
                if (closestNormalOffset > 1.5) continue;

                runs[first] = MergeDuplicatePipeRuns(a, b);
                runs.RemoveAt(second);
                changed = true;
                break;
            }
        } while (changed);
        return runs;
    }

    private IReadOnlyList<PdfPipeRun> PruneDanglingBranchRuns(
        IReadOnlyList<PdfPipeRun> source,
        IReadOnlyList<System.Windows.Point> normalizedSprinklers,
        double anchorTolerancePoints)
    {
        if (_vectorScene is null) return source;
        PdfPipeRun[] mains = source
            .Where(run => run.VectorClass == PdfVectorClass.MainPipe)
            .ToArray();
        PdfPipeRun[] branches = source
            .Where(run => run.VectorClass == PdfVectorClass.BranchPipe)
            .ToArray();
        if (branches.Length == 0) return source;

        var headPoints = normalizedSprinklers
            .Select(point => new System.Windows.Point(
                point.X * _vectorScene.PageWidth,
                point.Y * _vectorScene.PageHeight))
            .ToArray();
        const double nodeTolerancePoints = 1.5;
        var startKeys = new (long X, long Y)[branches.Length];
        var endKeys = new (long X, long Y)[branches.Length];
        var nodeEdges = new Dictionary<(long X, long Y), List<int>>();
        for (int index = 0; index < branches.Length; index++)
        {
            PdfPipeRun branch = branches[index];
            startKeys[index] = NodeKey(branch.X1, branch.Y1);
            endKeys[index] = NodeKey(branch.X2, branch.Y2);
            AddNodeEdge(startKeys[index], index);
            AddNodeEdge(endKeys[index], index);
        }

        var anchors = new HashSet<(long X, long Y)>();
        foreach (((long X, long Y) key, List<int> edges) in nodeEdges)
        {
            PdfPipeRun sample = branches[edges[0]];
            bool isStart = startKeys[edges[0]].Equals(key);
            double x = isStart ? sample.X1 : sample.X2;
            double y = isStart ? sample.Y1 : sample.Y2;
            bool nearHead = headPoints.Any(point =>
                Distance2D(x, y, point.X, point.Y) <= anchorTolerancePoints);
            // SplitPipeRuns produces an exact branch/main intersection. Keeping
            // this tolerance small prevents a short line beyond the main from
            // being treated as a second valid branch anchor.
            bool nearMain = mains.Any(main =>
                DistancePointToPipeRun(x, y, main) <= nodeTolerancePoints);
            if (nearHead || nearMain) anchors.Add(key);
        }

        var active = new bool[branches.Length];
        PortableApi.Fill(active, true);
        bool removed;
        do
        {
            removed = false;
            for (int index = 0; index < branches.Length; index++)
            {
                if (!active[index]) continue;
                int startDegree = nodeEdges[startKeys[index]].Count(edge => active[edge]);
                int endDegree = nodeEdges[endKeys[index]].Count(edge => active[edge]);
                bool danglingStart = startDegree <= 1 && !anchors.Contains(startKeys[index]);
                bool danglingEnd = endDegree <= 1 && !anchors.Contains(endKeys[index]);
                if (!danglingStart && !danglingEnd) continue;
                active[index] = false;
                removed = true;
            }
        } while (removed);

        return mains
            .Concat(branches.Where((_, index) => active[index]))
            .ToArray();

        (long X, long Y) NodeKey(double x, double y) =>
            ((long)Math.Round(x / nodeTolerancePoints),
             (long)Math.Round(y / nodeTolerancePoints));

        void AddNodeEdge((long X, long Y) key, int edge)
        {
            if (!nodeEdges.TryGetValue(key, out List<int>? edges))
                nodeEdges[key] = edges = [];
            edges.Add(edge);
        }
    }

    private static double Distance2D(double x1, double y1, double x2, double y2)
    {
        double dx = x2 - x1;
        double dy = y2 - y1;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static double DistancePointToPipeRun(double x, double y, PdfPipeRun run)
    {
        double dx = run.X2 - run.X1;
        double dy = run.Y2 - run.Y1;
        double lengthSquared = dx * dx + dy * dy;
        if (lengthSquared < 0.000001)
            return Distance2D(x, y, run.X1, run.Y1);
        double parameter = PortableMath.Clamp(
            ((x - run.X1) * dx + (y - run.Y1) * dy) / lengthSquared,
            0,
            1);
        return Distance2D(
            x,
            y,
            run.X1 + parameter * dx,
            run.Y1 + parameter * dy);
    }

    private static bool AreNearDuplicatePipeRuns(PdfPipeRun first, PdfPipeRun second, double tolerance)
    {
        if (first.VectorClass != second.VectorClass) return false;
        double firstDx = first.X2 - first.X1;
        double firstDy = first.Y2 - first.Y1;
        double secondDx = second.X2 - second.X1;
        double secondDy = second.Y2 - second.Y1;
        double firstLength = Math.Sqrt(firstDx * firstDx + firstDy * firstDy);
        double secondLength = Math.Sqrt(secondDx * secondDx + secondDy * secondDy);
        if (firstLength < 0.01 || secondLength < 0.01) return false;
        double parallel = Math.Abs((firstDx * secondDx + firstDy * secondDy) /
                                   (firstLength * secondLength));
        if (parallel < 0.998) return false;

        double normalDistance1 = Math.Abs(
            (second.X1 - first.X1) * firstDy -
            (second.Y1 - first.Y1) * firstDx) / firstLength;
        double normalDistance2 = Math.Abs(
            (second.X2 - first.X1) * firstDy -
            (second.Y2 - first.Y1) * firstDx) / firstLength;
        if (Math.Max(normalDistance1, normalDistance2) > tolerance) return false;

        double secondStart = ((second.X1 - first.X1) * firstDx +
                              (second.Y1 - first.Y1) * firstDy) / firstLength;
        double secondEnd = ((second.X2 - first.X1) * firstDx +
                            (second.Y2 - first.Y1) * firstDy) / firstLength;
        double overlap = Math.Min(firstLength, Math.Max(secondStart, secondEnd)) -
                         Math.Max(0, Math.Min(secondStart, secondEnd));
        return overlap >= Math.Min(firstLength, secondLength) * 0.72;
    }

    private static PdfPipeRun MergeDuplicatePipeRuns(PdfPipeRun first, PdfPipeRun second)
    {
        double dx = first.X2 - first.X1;
        double dy = first.Y2 - first.Y1;
        double length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 0.01) return first;
        double ux = dx / length;
        double uy = dy / length;
        double nx = -uy;
        double ny = ux;
        var points = new[]
        {
            (X: first.X1, Y: first.Y1),
            (X: first.X2, Y: first.Y2),
            (X: second.X1, Y: second.Y1),
            (X: second.X2, Y: second.Y2)
        };
        double originX = first.X1;
        double originY = first.Y1;
        double minAlong = points.Min(point => (point.X - originX) * ux + (point.Y - originY) * uy);
        double maxAlong = points.Max(point => (point.X - originX) * ux + (point.Y - originY) * uy);
        double averageNormal = points.Average(point =>
            (point.X - originX) * nx + (point.Y - originY) * ny);
        double centerX = originX + averageNormal * nx;
        double centerY = originY + averageNormal * ny;
        return new PdfPipeRun(
            centerX + minAlong * ux,
            centerY + minAlong * uy,
            centerX + maxAlong * ux,
            centerY + maxAlong * uy,
            first.VectorClass);
    }

    private static double PipeRunLength(PdfPipeRun run) =>
        Math.Sqrt(Math.Pow(run.X2 - run.X1, 2) + Math.Pow(run.Y2 - run.Y1, 2));

    private static PdfPipeRun[] SnapPipeRunEndpoints(IReadOnlyList<PdfPipeRun> source)
    {
        // PDF fitting glyphs often leave a visible break between the three
        // centerlines. Extend only near, non-parallel fire-pipe runs to their
        // mathematical intersection before building the Revit node.
        const double snapDistancePoints = 12.0;
        PdfPipeRun[] runs = source.ToArray();
        for (int first = 0; first < runs.Length; first++)
        for (int second = first + 1; second < runs.Length; second++)
        {
            PdfPipeRun a = runs[first];
            PdfPipeRun b = runs[second];
            double adx = a.X2 - a.X1;
            double ady = a.Y2 - a.Y1;
            double bdx = b.X2 - b.X1;
            double bdy = b.Y2 - b.Y1;
            double aLength = Math.Sqrt(adx * adx + ady * ady);
            double bLength = Math.Sqrt(bdx * bdx + bdy * bdy);
            if (aLength < 0.01 || bLength < 0.01) continue;
            double cross = adx * bdy - ady * bdx;
            if (Math.Abs(cross) < 0.000001) continue;
            double qx = b.X1 - a.X1;
            double qy = b.Y1 - a.Y1;
            double ta = (qx * bdy - qy * bdx) / cross;
            double tb = (qx * ady - qy * adx) / cross;
            double aTolerance = snapDistancePoints / aLength;
            double bTolerance = snapDistancePoints / bLength;
            if (ta < -aTolerance || ta > 1 + aTolerance ||
                tb < -bTolerance || tb > 1 + bTolerance)
                continue;
            bool aNeedsSnap = ta < 0 || ta > 1;
            bool bNeedsSnap = tb < 0 || tb > 1;
            if (!aNeedsSnap && !bNeedsSnap) continue;
            double intersectionX = a.X1 + ta * adx;
            double intersectionY = a.Y1 + ta * ady;
            if (ta < 0) a = a with { X1 = intersectionX, Y1 = intersectionY };
            else if (ta > 1) a = a with { X2 = intersectionX, Y2 = intersectionY };
            if (tb < 0) b = b with { X1 = intersectionX, Y1 = intersectionY };
            else if (tb > 1) b = b with { X2 = intersectionX, Y2 = intersectionY };
            runs[first] = a;
            runs[second] = b;
        }
        return runs;
    }

    private void AlignBranchEndpointsToSprinklers(
        IList<System.Windows.Point> sprinklerPoints,
        IList<PdfPipeRun> branchRuns,
        double maximumDistancePoints)
    {
        if (_vectorScene is null || branchRuns.Count == 0) return;
        double pageWidth = _vectorScene.PageWidth;
        double pageHeight = _vectorScene.PageHeight;
        var usedEndpoints = new HashSet<(int Run, bool Start)>();
        for (int sprinklerIndex = 0; sprinklerIndex < sprinklerPoints.Count; sprinklerIndex++)
        {
            double headX = sprinklerPoints[sprinklerIndex].X * pageWidth;
            double headY = sprinklerPoints[sprinklerIndex].Y * pageHeight;
            (int Run, bool Start)? nearest = null;
            double nearestDistance = double.MaxValue;
            for (int runIndex = 0; runIndex < branchRuns.Count; runIndex++)
            {
                PdfPipeRun run = branchRuns[runIndex];
                ConsiderEndpoint(runIndex, true, run.X1, run.Y1);
                ConsiderEndpoint(runIndex, false, run.X2, run.Y2);
            }
            if (nearest is null) continue;

            PdfPipeRun selected = branchRuns[nearest.Value.Run];
            double dx = selected.X2 - selected.X1;
            double dy = selected.Y2 - selected.Y1;
            double lengthSquared = dx * dx + dy * dy;
            if (lengthSquared < 0.000001) continue;
            double projection = ((headX - selected.X1) * dx + (headY - selected.Y1) * dy) / lengthSquared;
            double targetX = selected.X1 + projection * dx;
            double targetY = selected.Y1 + projection * dy;
            selected = nearest.Value.Start
                ? selected with { X1 = targetX, Y1 = targetY }
                : selected with { X2 = targetX, Y2 = targetY };
            branchRuns[nearest.Value.Run] = selected;
            sprinklerPoints[sprinklerIndex] = new System.Windows.Point(targetX / pageWidth, targetY / pageHeight);
            usedEndpoints.Add(nearest.Value);

            void ConsiderEndpoint(int runIndex, bool start, double x, double y)
            {
                if (usedEndpoints.Contains((runIndex, start))) return;
                double distance = Math.Sqrt(Math.Pow(x - headX, 2) + Math.Pow(y - headY, 2));
                if (distance <= maximumDistancePoints && distance < nearestDistance)
                {
                    nearest = (runIndex, start);
                    nearestDistance = distance;
                }
            }
        }
    }

    private void AlignNearbySprinklerRowsAndColumns(
        IList<System.Windows.Point> sprinklerPoints,
        double tolerancePoints)
    {
        if (_vectorScene is null || sprinklerPoints.Count < 2) return;
        double pageWidth = _vectorScene.PageWidth;
        double pageHeight = _vectorScene.PageHeight;
        SnapAxis(horizontalCoordinate: true, pageWidth, tolerancePoints);
        SnapAxis(horizontalCoordinate: false, pageHeight, tolerancePoints);

        void SnapAxis(bool horizontalCoordinate, double pageSize, double tolerance)
        {
            int[] order = Enumerable.Range(0, sprinklerPoints.Count)
                .OrderBy(index => (horizontalCoordinate
                    ? sprinklerPoints[index].X
                    : sprinklerPoints[index].Y) * pageSize)
                .ToArray();
            var cluster = new List<int>();
            double clusterMean = 0;
            foreach (int index in order)
            {
                double coordinate = (horizontalCoordinate
                    ? sprinklerPoints[index].X
                    : sprinklerPoints[index].Y) * pageSize;
                if (cluster.Count > 0 && Math.Abs(coordinate - clusterMean) > tolerance)
                {
                    ApplyCluster();
                    cluster.Clear();
                    clusterMean = 0;
                }
                cluster.Add(index);
                clusterMean = cluster.Count == 1
                    ? coordinate
                    : (clusterMean * (cluster.Count - 1) + coordinate) / cluster.Count;
            }
            ApplyCluster();

            void ApplyCluster()
            {
                if (cluster.Count < 2) return;
                double snapped = cluster
                    .Select(index => (horizontalCoordinate
                        ? sprinklerPoints[index].X
                        : sprinklerPoints[index].Y) * pageSize)
                    .OrderBy(value => value)
                    .ElementAt(cluster.Count / 2) / pageSize;
                foreach (int index in cluster)
                {
                    System.Windows.Point point = sprinklerPoints[index];
                    sprinklerPoints[index] = horizontalCoordinate
                        ? new System.Windows.Point(snapped, point.Y)
                        : new System.Windows.Point(point.X, snapped);
                }
            }
        }
    }

    private IReadOnlyList<int> ResolveRawVectorPick(PdfVectorClass vectorClass, PdfVectorPickSnapshot snapshot)
    {
        if (_vectorScene is null)
            return [];
        if (snapshot.GeometryAnalysis && snapshot.ExplicitSegmentIds.Count > 0)
            return snapshot.ExplicitSegmentIds
                .Where(id => id >= 0 && id < _vectorScene.Segments.Length)
                .Distinct()
                .ToArray();
        if (snapshot.SegmentId < 0 || snapshot.SegmentId >= _vectorScene.Segments.Length)
            return [];
        PdfVectorSelectionScope scope = Enum.TryParse(snapshot.Scope, out PdfVectorSelectionScope parsed)
            ? parsed
            : PdfVectorSelectionScope.Line;
        if (IsCadSource && scope == PdfVectorSelectionScope.SimilarStyle)
        {
            CadPixelSignature seedColor = GetCadPixelSignature(snapshot.SegmentId);
            return vectorClass == PdfVectorClass.Sprinkler
                ? ResolveCadRepeatedSymbol(snapshot.SegmentId, seedColor)
                : ResolveCadPipeColorSimilar(snapshot.SegmentId, seedColor);
        }
        return vectorClass == PdfVectorClass.Sprinkler && scope == PdfVectorSelectionScope.SimilarStyle
            ? _vectorScene.ResolveShapeSimilar(snapshot.SegmentId)
            : _vectorScene.ResolveSelection(snapshot.SegmentId, scope);
    }

    private void ApplyExclusiveVectorOwnership(PdfVectorClass ownerClass)
    {
        if (_vectorScene is null || !_vectorPicks.ContainsKey(ownerClass))
            return;
        HashSet<int> ownedPaths = VectorPicksFor(ownerClass)
            .Where(snapshot => snapshot.ColorAreaScan || snapshot.LineStrokeScan ||
                               ownerClass == PdfVectorClass.Sprinkler ||
                               !string.Equals(snapshot.Scope, PdfVectorSelectionScope.SimilarStyle.ToString(), StringComparison.Ordinal))
            .SelectMany(snapshot => snapshot.ColorAreaScan || snapshot.LineStrokeScan
                ? ResolveVectorPick(ownerClass, snapshot)
                : ResolveRawVectorPick(ownerClass, snapshot))
            .Select(id => _vectorScene.Segments[id].PathId)
            .ToHashSet();
        if (ownerClass != PdfVectorClass.Sprinkler &&
            _vectorPicks.ContainsKey(PdfVectorClass.Sprinkler))
        {
            HashSet<int> protectedSprinklerPaths = VectorPicksFor(PdfVectorClass.Sprinkler)
                .SelectMany(snapshot => snapshot.ColorAreaScan || snapshot.LineStrokeScan
                    ? ResolveVectorPick(PdfVectorClass.Sprinkler, snapshot)
                    : ResolveRawVectorPick(PdfVectorClass.Sprinkler, snapshot))
                .Select(id => _vectorScene.Segments[id].PathId)
                .ToHashSet();
            ownedPaths.ExceptWith(protectedSprinklerPaths);
            if (!_vectorExcludedPathIds.TryGetValue(ownerClass, out HashSet<int>? pipeExclusions))
                _vectorExcludedPathIds[ownerClass] = pipeExclusions = [];
            pipeExclusions.UnionWith(protectedSprinklerPaths);
            if (_vectorExcludedPathIds.TryGetValue(PdfVectorClass.Sprinkler, out HashSet<int>? sprinklerExclusions))
                sprinklerExclusions.ExceptWith(protectedSprinklerPaths);
        }
        if (_vectorExcludedPathIds.TryGetValue(ownerClass, out HashSet<int>? ownerExclusions))
            ownerExclusions.ExceptWith(ownedPaths);
        foreach (PdfVectorClass otherClass in _vectorPicks.Keys.Where(item => item != ownerClass).ToArray())
        {
            if (!_vectorExcludedPathIds.TryGetValue(otherClass, out HashSet<int>? excluded))
                _vectorExcludedPathIds[otherClass] = excluded = [];
            excluded.UnionWith(ownedPaths);
            UpdateVectorClassItem(otherClass);
        }
    }

    private void UpdateVectorClassItem(PdfVectorClass vectorClass)
    {
        if (!_vectorPicks.TryGetValue(vectorClass, out PdfVectorPickSnapshot? snapshot)) return;
        LayerMappingItem? item = _layerItems.FirstOrDefault(candidate =>
            candidate.Marker == MarkerForClass(vectorClass));
        if (item is null) return;
        item.Detected = CountVectorDetected(vectorClass);
        item.Confidence = "User";
        int excludedCount = ExclusionCount(vectorClass);
        item.Status = excludedCount > 0
            ? $"Adjusted (-{excludedCount})"
            : "User seed";
        Find<DataGrid>("v2_layer_grid").Items.Refresh();
    }

    private int ExclusionCount(PdfVectorClass vectorClass) =>
        (_vectorExcludedPathIds.TryGetValue(vectorClass, out HashSet<int>? paths) ? paths.Count : 0) +
        (_vectorExcludedSegmentIds.TryGetValue(vectorClass, out HashSet<int>? segments) ? segments.Count : 0);

    private PdfVectorClass CurrentVectorClass()
    {
        if (Find<DataGrid>("v2_layer_grid").SelectedItem is LayerMappingItem item)
        {
            PdfVectorClass selected = ClassForMarker(item.Marker);
            if (selected != PdfVectorClass.None) return selected;
        }
        return _lastAssignedVectorClass;
    }

    private bool IsVectorClassEnabled(PdfVectorClass vectorClass)
    {
        LayerMappingItem? item = _layerItems.FirstOrDefault(candidate =>
            candidate.Marker == MarkerForClass(vectorClass));
        return item is not null &&
               !string.Equals(item.RevitCategory, "Ignore", StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(item.Status, "Removed", StringComparison.OrdinalIgnoreCase);
    }

    private void RestoreDefaultMappingForPickedClass(
        PdfVectorClass vectorClass,
        bool explicitPick)
    {
        int marker = MarkerForClass(vectorClass);
        if (marker <= 0) return;
        LayerMappingItem? item = _layerItems.FirstOrDefault(candidate => candidate.Marker == marker);
        LayerMappingItem? original = SprinklerModelerData.CreateLayerMappings()
            .FirstOrDefault(candidate => candidate.Marker == marker);
        if (item is null || original is null) return;

        bool disabled = string.Equals(item.RevitCategory, "Ignore", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(item.Status, "Removed", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(item.Status, "Ignored", StringComparison.OrdinalIgnoreCase);
        bool intentionalWholeClassIgnore =
            string.Equals(item.RevitCategory, "Ignore", StringComparison.OrdinalIgnoreCase) &&
            (string.Equals(item.Status, "Adjusted", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(item.Status, "Ignored", StringComparison.OrdinalIgnoreCase));
        if (!disabled || (!explicitPick && intentionalWholeClassIgnore)) return;

        item.RevitCategory = original.RevitCategory;
        item.FamilyType = original.FamilyType;
        item.Status = _vectorPicks.ContainsKey(vectorClass) ? "User seed" : "Pick seed";
        Find<DataGrid>("v2_layer_grid").Items.Refresh();
    }

    private void AssignCurrentVectorPick(
        PdfVectorClass vectorClass,
        bool keepPickMode = false,
        bool exactCadLayer = false)
    {
        if (_vectorScene is null || _selectedVectorSegmentId < 0) return;
        // CAD uses the same deterministic workflow as vector PDF. Similar
        // means equal source color and line weight, not a topology flood.
        bool smartFlattened = false;
        PdfVectorPickSnapshot snapshot;
        if (_activeVectorPick is not null &&
            string.Equals(_activeVectorPick.ClassName, vectorClass.ToString(), StringComparison.Ordinal) &&
            _activeVectorPick.SegmentId == _selectedVectorSegmentId)
        {
            snapshot = _activeVectorPick;
            snapshot.Scope = _vectorSelectionScope.ToString();
            snapshot.ExactCadLayer = exactCadLayer;
            snapshot.SmartFlattened = smartFlattened;
        }
        else
        {
            snapshot = VectorPicksFor(vectorClass)
                .FirstOrDefault(item => item.SegmentId == _selectedVectorSegmentId)
                ?? new PdfVectorPickSnapshot
                {
                    ClassName = vectorClass.ToString(),
                    SegmentId = _selectedVectorSegmentId,
                    Scope = _vectorSelectionScope.ToString(),
                    ExactCadLayer = exactCadLayer,
                    SmartFlattened = smartFlattened
                };
            snapshot.Scope = _vectorSelectionScope.ToString();
            snapshot.ExactCadLayer = exactCadLayer;
            snapshot.SmartFlattened = smartFlattened;
            if (!_vectorPicks.ContainsKey(vectorClass))
                _vectorPicks[vectorClass] = snapshot;
            else if (!ReferenceEquals(_vectorPicks[vectorClass], snapshot) &&
                     !_additionalVectorPicks.GetValueOrDefault(vectorClass, []).Contains(snapshot))
            {
                if (!_additionalVectorPicks.TryGetValue(vectorClass, out List<PdfVectorPickSnapshot>? additional))
                    _additionalVectorPicks[vectorClass] = additional = [];
                additional.Add(snapshot);
            }
        }
        _activeVectorPick = snapshot;
        if (!keepPickMode)
        {
            snapshot.ColorAreaScan = false;
            snapshot.LineStrokeScan = false;
        }
        _lastAssignedVectorClass = vectorClass;
        _pendingExclusionClass = PdfVectorClass.None;
        Find<Button>("v2_exclude_wrong_btn").ClearValue(System.Windows.Controls.Control.BackgroundProperty);
        Find<Button>("v2_exclude_wrong_btn").ClearValue(System.Windows.Controls.Control.BorderBrushProperty);
        Find<Button>("v2_exclude_wrong_btn").ClearValue(System.Windows.Controls.Control.BorderThicknessProperty);
        if (!keepPickMode)
            _pendingVectorClass = PdfVectorClass.None;
        ApplyExclusiveVectorOwnership(vectorClass);
        SetPickButtonState(_pendingVectorClass);
        int marker = MarkerForClass(vectorClass);
        SelectLayerByMarker(marker);
        PositionVectorMarker(marker, _selectedVectorSegmentId);
        LayerMappingItem? item = _layerItems.FirstOrDefault(candidate => candidate.Marker == marker);
        if (item is not null)
        {
            int pathId = _vectorScene.Segments[_selectedVectorSegmentId].PathId;
            if (IsCadSource && _cadLayerByPathId.TryGetValue(pathId, out string? cadLayer))
                item.CadLayer = cadLayer;
            item.Detected = CountVectorDetected(vectorClass);
            item.Confidence = "User";
            int seedCount = VectorPicksFor(vectorClass).Count();
            item.Status = seedCount > 1 ? $"{seedCount} user seeds" : "User seed";
        }
        Find<DataGrid>("v2_layer_grid").Items.Refresh();
        UpdateVectorPickSummary();
        SaveMappingSidecar();
        UpdateVectorSelectionText();
        RefreshFittingTopology();
        RedrawVectorHighlights();
    }

    private void UpdateVectorSelectionText()
    {
        if (_vectorScene is null || _selectedVectorSegmentId < 0) return;
        string description = _vectorScene.Describe(_selectedVectorSegmentId, _vectorSelectionScope);
        int pathId = _vectorScene.Segments[_selectedVectorSegmentId].PathId;
        string layer = IsCadSource && _cadLayerByPathId.TryGetValue(pathId, out string? cadLayer)
            ? $" â€¢ layer {cadLayer}"
            : string.Empty;
        string candidates = _vectorHitCandidates.Count > 1
            ? $" • {_vectorHitCandidates.Count} overlapping candidates"
            : string.Empty;
        Find<TextBlock>("v2_vector_pick_text").Text =
            $"{description}{layer}{candidates}. Tab: Line → Object → Similar style.";
        Status.Text = IsCadSource
            ? $"CAD vector selected: {description}{layer}"
            : $"PDF vector selected: {description}";
    }

    private void RedrawVectorHighlights()
    {
        if (_vectorScene is null || _mainVectorHighlight is null || _branchVectorHighlight is null ||
            _sprinklerVectorHighlight is null || _fittingVectorHighlight is null || _currentVectorHighlight is null ||
            _excludedVectorHighlight is null)
            return;
        _mainVectorHighlight.Data = GeometryForPick(PdfVectorClass.MainPipe);
        _branchVectorHighlight.Data = GeometryForPick(PdfVectorClass.BranchPipe);
        _sprinklerVectorHighlight.Data = GeometryForPick(PdfVectorClass.Sprinkler);
        _fittingVectorHighlight.Data = GeometryForFittings();
        _excludedVectorHighlight.Data = Geometry.Empty;
        _currentVectorHighlight.Data = _selectedVectorSegmentId >= 0
            ? GeometryForSegments(ResolveCurrentVectorSelection())
            : Geometry.Empty;
        ApplyVectorHighlightColors();
        ApplyVectorIsolationVisuals();
    }

    private void ApplyVectorHighlightColors()
    {
        if (_mainVectorHighlight is null || _branchVectorHighlight is null ||
            _sprinklerVectorHighlight is null)
            return;

        // Normal review colors remain stable across PDF and CAD.
        _mainVectorHighlight.Stroke = Brush("#F36B5B");
        _branchVectorHighlight.Stroke = Brush("#0AA6A6");
        _sprinklerVectorHighlight.Stroke = Brush("#7657E8");
        if (!_isolateVectorResult || !IsCadSource) return;

        PdfVectorClass focus = CurrentVectorClass();
        PdfVectorPickSnapshot? colorScan = VectorPicksFor(focus)
            .LastOrDefault(snapshot =>
                (snapshot.ColorAreaScan || snapshot.LineStrokeScan) &&
                snapshot.ScanSaturation >= 0.42);
        if (colorScan is null) return;

        var signature = new CadPixelSignature(
            colorScan.ScanHue,
            colorScan.ScanSaturation,
            colorScan.ScanValue,
            true);
        var sourceColor = new SolidColorBrush(ColorFromHsv(signature));
        sourceColor.Freeze();
        switch (focus)
        {
            case PdfVectorClass.MainPipe:
                _mainVectorHighlight.Stroke = sourceColor;
                break;
            case PdfVectorClass.BranchPipe:
                _branchVectorHighlight.Stroke = sourceColor;
                break;
            case PdfVectorClass.Sprinkler:
                _sprinklerVectorHighlight.Stroke = sourceColor;
                break;
        }
    }

    private IReadOnlyList<int> ResolveCurrentVectorSelection()
    {
        if (_vectorScene is null || _selectedVectorSegmentId < 0) return [];
        if (_lastAssignedVectorClass != PdfVectorClass.None &&
            _activeVectorPick is not null &&
            _activeVectorPick.SegmentId == _selectedVectorSegmentId &&
            string.Equals(_activeVectorPick.Scope, _vectorSelectionScope.ToString(), StringComparison.Ordinal))
            return ResolveVectorPick(_lastAssignedVectorClass, _activeVectorPick);
        return _vectorScene.ResolveSelection(_selectedVectorSegmentId, _vectorSelectionScope);
    }

    private Geometry GeometryForExclusions(PdfVectorClass vectorClass)
    {
        if (_vectorScene is null ||
            !_vectorExcludedPathIds.TryGetValue(vectorClass, out HashSet<int>? excluded) ||
            excluded.Count == 0)
            return Geometry.Empty;
        List<int> segments = [];
        foreach (int pathId in excluded)
        {
            segments.AddRange(_vectorScene.GetSegmentsForPath(pathId));
            if (segments.Count >= 12000) break;
        }
        return GeometryForSegments(segments.Take(12000).ToArray());
    }

    private Geometry GeometryForPick(PdfVectorClass vectorClass)
    {
        if (_vectorScene is null || !_vectorPicks.TryGetValue(vectorClass, out PdfVectorPickSnapshot? snapshot) ||
            snapshot.SegmentId < 0 || snapshot.SegmentId >= _vectorScene.Segments.Length)
            return Geometry.Empty;
        return GeometryForSegments(ResolveVectorClass(vectorClass));
    }

    private Geometry GeometryForSegments(IReadOnlyList<int> segmentIds)
    {
        if (_vectorScene is null || segmentIds.Count == 0)
            return Geometry.Empty;
        Canvas canvas = Find<Canvas>("v2_vector_selection_canvas");
        var geometry = new StreamGeometry();
        using (StreamGeometryContext context = geometry.Open())
        {
            foreach (int id in segmentIds)
            {
                if (id < 0 || id >= _vectorScene.Segments.Length) continue;
                PdfVectorSegment segment = _vectorScene.Segments[id];
                context.BeginFigure(
                    new System.Windows.Point(segment.X1 * canvas.Width, segment.Y1 * canvas.Height),
                    false,
                    false);
                context.LineTo(
                    new System.Windows.Point(segment.X2 * canvas.Width, segment.Y2 * canvas.Height),
                    true,
                    false);
            }
        }
        geometry.Freeze();
        return geometry;
    }

    private void RefreshFittingTopology()
    {
        LayerMappingItem? fittingItem = _layerItems.FirstOrDefault(item => item.Marker == 4);
        if (_vectorScene is null || fittingItem is null ||
            !_vectorPicks.ContainsKey(PdfVectorClass.MainPipe) ||
            !_vectorPicks.ContainsKey(PdfVectorClass.BranchPipe))
        {
            _fittingCandidates = [];
            if (fittingItem is not null)
            {
                fittingItem.Detected = 0;
                fittingItem.Confidence = "—";
                fittingItem.Status = "Pick pipes first";
            }
            if (_fittingVectorHighlight is not null)
                _fittingVectorHighlight.Data = Geometry.Empty;
            return;
        }

        _fittingCandidates = _vectorScene.DetectFittings(
            ResolveVectorClass(PdfVectorClass.MainPipe),
            ResolveVectorClass(PdfVectorClass.BranchPipe));
        int teeCount = _fittingCandidates.Count(item => item.Kind == PdfFittingKind.Tee);
        int elbowCount = _fittingCandidates.Count(item => item.Kind == PdfFittingKind.Elbow);
        int crossCount = _fittingCandidates.Count(item => item.Kind == PdfFittingKind.Cross);
        fittingItem.Detected = _fittingCandidates.Count;
        fittingItem.Confidence = "Topology";
        fittingItem.Status = $"T {teeCount} · Co {elbowCount} · Cross {crossCount}";
        if (_fittingVectorHighlight is not null)
            _fittingVectorHighlight.Data = GeometryForFittings();
    }

    private Geometry GeometryForFittings()
    {
        if (_fittingCandidates.Count == 0)
            return Geometry.Empty;
        Canvas canvas = Find<Canvas>("v2_vector_selection_canvas");
        var geometry = new StreamGeometry();
        using (StreamGeometryContext context = geometry.Open())
        {
            foreach (PdfFittingCandidate fitting in _fittingCandidates)
            {
                double x = fitting.X * canvas.Width;
                double y = fitting.Y * canvas.Height;
                const double size = 4.0;
                switch (fitting.Kind)
                {
                    case PdfFittingKind.Elbow:
                        context.BeginFigure(new System.Windows.Point(x - size, y), false, false);
                        context.LineTo(new System.Windows.Point(x, y), true, false);
                        context.LineTo(new System.Windows.Point(x, y - size), true, false);
                        break;
                    case PdfFittingKind.Tee:
                        context.BeginFigure(new System.Windows.Point(x - size, y), false, false);
                        context.LineTo(new System.Windows.Point(x + size, y), true, false);
                        context.BeginFigure(new System.Windows.Point(x, y), false, false);
                        context.LineTo(new System.Windows.Point(x, y + size), true, false);
                        break;
                    case PdfFittingKind.Cross:
                        context.BeginFigure(new System.Windows.Point(x - size, y), false, false);
                        context.LineTo(new System.Windows.Point(x + size, y), true, false);
                        context.BeginFigure(new System.Windows.Point(x, y - size), false, false);
                        context.LineTo(new System.Windows.Point(x, y + size), true, false);
                        break;
                }
            }
        }
        geometry.Freeze();
        return geometry;
    }

    private void PositionVectorMarker(int markerNumber, int segmentId)
    {
        if (_vectorScene is null || segmentId < 0 || segmentId >= _vectorScene.Segments.Length) return;
        Canvas canvas = Find<Canvas>("v2_pdf_overlay_canvas");
        Button marker = Find<Button>($"v2_marker_{markerNumber}_btn");
        PdfVectorSegment segment = _vectorScene.Segments[segmentId];
        Canvas.SetLeft(marker, (segment.X1 + segment.X2) * 0.5 * canvas.Width - marker.Width / 2.0);
        Canvas.SetTop(marker, (segment.Y1 + segment.Y2) * 0.5 * canvas.Height - marker.Height / 2.0);
        marker.Visibility = WpfVisibility.Visible;
    }

    private void RestoreVectorPickMarkers()
    {
        if (_vectorScene is null) return;
        foreach (PdfVectorClass vectorClass in _vectorPicks.Keys)
        {
            foreach (PdfVectorPickSnapshot snapshot in VectorPicksFor(vectorClass))
            {
                if (snapshot.SegmentId >= 0 && snapshot.SegmentId < _vectorScene.Segments.Length)
                    PositionVectorMarker(MarkerForClass(vectorClass), snapshot.SegmentId);
            }
            LayerMappingItem? item = _layerItems.FirstOrDefault(candidate =>
                candidate.Marker == MarkerForClass(vectorClass));
            if (item is not null)
            {
                int seedCount = VectorPicksFor(vectorClass).Count();
                item.Detected = CountVectorDetected(vectorClass);
                item.Confidence = "User";
                item.Status = seedCount > 1 ? $"{seedCount} user seeds" : "User seed";
            }
        }
        // Reconcile old sidecars with the current priority rule: a confirmed
        // sprinkler symbol always owns its PDF path ahead of pipe color groups.
        if (_vectorPicks.ContainsKey(PdfVectorClass.Sprinkler))
            ApplyExclusiveVectorOwnership(PdfVectorClass.Sprinkler);
        RefreshFittingTopology();
        Find<DataGrid>("v2_layer_grid").Items.Refresh();
        UpdateVectorPickSummary();
        SaveMappingSidecar();
    }

    private void UpdateVectorPickSummary()
    {
        if (_vectorScene is null) return;
        int classified = _vectorPicks.Keys.Sum(vectorClass => ResolveVectorClass(vectorClass).Count);
        int needed = Math.Max(0, 3 - _vectorPicks.Count);
        Find<TextBlock>("v2_layer_classified_text").Text = $"{classified:N0} user-classified";
        Find<TextBlock>("v2_layer_review_text").Text = needed == 0
            ? "3 seed types ready"
            : $"{needed} seed type(s) needed";
    }

    private void SetPickButtonState(PdfVectorClass activeClass)
    {
        Find<Button>("v2_pick_sprinkler_btn").Content = _vectorPicks.ContainsKey(PdfVectorClass.Sprinkler)
            ? "2  Add sprinkler seed"
            : "2  Pick sprinkler";
        foreach ((string name, PdfVectorClass vectorClass) in new[]
                 {
                     ("v2_pick_main_btn", PdfVectorClass.MainPipe),
                     ("v2_pick_branch_btn", PdfVectorClass.BranchPipe),
                     ("v2_pick_sprinkler_btn", PdfVectorClass.Sprinkler)
                 })
        {
            Button button = Find<Button>(name);
            bool active = activeClass == vectorClass;
            button.Background = active ? Brush("#DDF5F4") : Brush("#FFFFFF");
            button.BorderBrush = active ? Brush("#0AA6A6") : Brush("#D8E0E6");
            button.BorderThickness = new Thickness(active ? 2 : 1);
        }
    }

    private static int MarkerForClass(PdfVectorClass vectorClass) => vectorClass switch
    {
        PdfVectorClass.MainPipe => 1,
        PdfVectorClass.Sprinkler => 2,
        PdfVectorClass.BranchPipe => 3,
        _ => 0
    };

    private static PdfVectorClass ClassForMarker(int marker) => marker switch
    {
        1 => PdfVectorClass.MainPipe,
        2 => PdfVectorClass.Sprinkler,
        3 => PdfVectorClass.BranchPipe,
        _ => PdfVectorClass.None
    };

    private static string VectorClassLabel(PdfVectorClass vectorClass) => vectorClass switch
    {
        PdfVectorClass.MainPipe => "main pipe",
        PdfVectorClass.BranchPipe => "branch pipe",
        PdfVectorClass.Sprinkler => "sprinkler",
        _ => "geometry"
    };

    private void AttachPdfViewerToActiveHost()
    {
        if (_disposed) return;
        bool hasPdf = !string.IsNullOrWhiteSpace(_currentPdfPath);
        bool sourceActive = MainTabs.SelectedIndex == 0;
        bool layerActive = MainTabs.SelectedIndex == 1;
        if (_sourcePdfWebView is not null)
            _sourcePdfWebView.Visibility = hasPdf && sourceActive && _sourcePdfWebView.CoreWebView2 is not null
                ? WpfVisibility.Visible
                : WpfVisibility.Collapsed;
        if (_layerPdfWebView is not null)
            _layerPdfWebView.Visibility = hasPdf && layerActive && _layerPdfWebView.CoreWebView2 is not null
                ? WpfVisibility.Visible
                : WpfVisibility.Collapsed;

        if (hasPdf && ActivePdfViewer?.CoreWebView2 is not null)
            HidePdfPlaceholders();
        else
        {
            Find<Border>("v2_source_pdf_placeholder").Visibility =
                sourceActive ? WpfVisibility.Visible : WpfVisibility.Collapsed;
            Find<Viewbox>("v2_pdf_placeholder_view").Visibility =
                layerActive ? WpfVisibility.Visible : WpfVisibility.Collapsed;
        }
    }

    private void DisposePdfViewer()
    {
        foreach (WebView2CompositionControl viewer in new[] { _sourcePdfWebView, _layerPdfWebView }
                     .Where(viewer => viewer is not null)
                     .Cast<WebView2CompositionControl>())
        {
            try
            {
                viewer.NavigationCompleted -= OnPdfNavigationCompleted;
                viewer.Dispose();
            }
            catch
            {
                // WebView2 may already be closing with the host Revit window.
            }
        }
        _sourcePdfWebView = null;
        _layerPdfWebView = null;
        _pdfEnvironment = null;
    }

    private void SelectConnection(string key)
    {
        if (!SprinklerModelerData.ConnectionRules.TryGetValue(key, out ConnectionRule? rule)) return;
        _selectedConnectionKey = key;
        SolidColorBrush selectedBorder = Brush("#0AA6A6");
        SolidColorBrush selectedBackground = Brush("#E5F8F7");
        SolidColorBrush idleBorder = Brush("#D8E0E6");
        SolidColorBrush idleBackground = Brush("#FFFFFF");

        foreach (ConnectionRule item in SprinklerModelerData.ConnectionRules.Values)
        {
            bool selected = string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase);
            Border card = Find<Border>($"{item.Key}_card");
            card.BorderBrush = selected ? selectedBorder : idleBorder;
            card.Background = selected ? selectedBackground : idleBackground;
            card.BorderThickness = new Thickness(selected ? 2 : 1);
            Find<FrameworkElement>($"preview_{item.Key}").Visibility =
                selected ? WpfVisibility.Visible : WpfVisibility.Collapsed;
        }

        Find<TextBlock>("connection_name_text").Text = rule.Name;
        Find<TextBlock>("connection_description_text").Text = rule.Description;
        Find<TextBlock>("connection_confidence_text").Text = rule.Confidence;
        Find<TextBlock>("connection_orientation_text").Text = rule.Orientation;
        _suppressOrientationChange = true;
        try { HeadOrientation.SelectedItem = rule.Orientation; }
        finally { _suppressOrientationChange = false; }
        Status.Text = $"Connection rule selected: {rule.Name}";
    }

    private void OnOrientationChanged()
    {
        if (_suppressOrientationChange) return;
        string orientation = HeadOrientation.SelectedItem as string ?? string.Empty;
        if (SprinklerModelerData.ConnectionRules.TryGetValue(_selectedConnectionKey, out ConnectionRule? current) &&
            current.Orientation == orientation)
            return;

        string? defaultRule = SprinklerModelerData.DefaultRuleForOrientation(orientation);
        if (defaultRule is not null)
            SelectConnection(defaultRule);
        else if (orientation == "Sidewall")
            Status.Text = "Sidewall orientation needs a project-specific review rule";
    }

    private void SaveConnectionRule()
    {
        ConnectionRule rule = SprinklerModelerData.ConnectionRules[_selectedConnectionKey];
        Find<TextBlock>("rule_saved_text").Text = $"Rule saved: {rule.Name}";
        Status.Text = "Connection rule saved for similar heads";
    }

    private void UpdatePipeRoutingStatus()
    {
        TextBlock text = Find<TextBlock>("v2_pipe_routing_status_text");
        if (Find<WpfComboBox>("v2_pipe_type_combo").SelectedItem is not PipeTypeOption pipeType)
        {
            text.Text = "No Revit Pipe Type is loaded.";
            text.Foreground = Brush("#C98200");
            return;
        }
        text.Text = $"Routing rules • Tee {pipeType.JunctionRules} • Reducer {pipeType.TransitionRules} • Elbow {pipeType.ElbowRules}";
        text.Foreground = pipeType.JunctionRules > 0 && pipeType.TransitionRules > 0
            ? Brush("#23834B")
            : Brush("#C98200");
    }

    private void ClearScannedPlacementArea()
    {
        _scannedPlacementViewId = null;
        _alignedPdfInstanceId = null;
        _alignedPdfBottomLeft = null;
        _alignedPdfBottomRight = null;
        _alignedPdfTopLeft = null;
        TextBlock status = Find<TextBlock>("v2_placement_area_status_text");
        status.Text = IsCadSource ? "CAD position / layers not captured" : "PDF position not captured";
        status.Foreground = Brush("#C98200");
    }

    private void PlaceOrSelectPdfInRevit()
    {
        if (IsCadSource)
        {
            PlaceOrSelectCadInRevit();
            return;
        }
        if (Find<WpfComboBox>("v2_target_view_combo").SelectedItem is not RevitViewOption selectedView)
        {
            Status.Text = "Select a target Revit view first.";
            return;
        }
        if (string.IsNullOrWhiteSpace(_currentPdfPath) || !File.Exists(_currentPdfPath))
        {
            Status.Text = "Browse and load a PDF before placing it in Revit.";
            return;
        }

        var result = new PdfAlignmentResult();
        Button placeButton = Find<Button>("v2_place_pdf_in_revit_btn");
        placeButton.IsEnabled = false;
        Status.Text = $"Opening {selectedView.Name} and selecting the PDF...";
        string pdfPath = Path.GetFullPath(_currentPdfPath);
        bool queued = _revitRequestHandler.TrySetRequest(
            app =>
            {
                UIDocument uiDocument = app.ActiveUIDocument
                    ?? throw new InvalidOperationException("Open the target Revit project first.");
                if (!IsSameRevitDocument(uiDocument.Document))
                    throw new InvalidOperationException("The active document changed. Reopen Spinkler in the target project.");
                Document document = uiDocument.Document;
                ViewPlan view = document.GetElement(selectedView.Id) as ViewPlan
                    ?? throw new InvalidOperationException("The selected target view is no longer available.");
                uiDocument.ActiveView = view;

                ImageInstance? image = FindPdfImageInstance(document, view, pdfPath);
                if (image is null)
                {
                    using var transaction = new Transaction(document, "FamilyMEP - Place aligned sprinkler PDF");
                    transaction.Start();
                    ImageType? imageType = FindPdfImageType(document, pdfPath);
                    if (imageType is null)
                    {
                        using var options = PortableApi.ImageOptions(pdfPath);
                        options.PageNumber = 1;
                        options.Resolution = 300;
                        imageType = ImageType.Create(document, options);
                    }

                    BoundingBoxXYZ crop = view.CropBox;
                    XYZ cropCenter = (crop.Min + crop.Max) * 0.5;
                    XYZ placementCenter = crop.Transform.OfPoint(cropCenter);
                    using var placement = new ImagePlacementOptions(placementCenter, BoxPlacement.Center);
                    image = ImageInstance.Create(document, view, imageType.Id, placement);
                    if (image.CanHaveSnaps)
                        image.EnableSnaps = true;
                    transaction.Commit();
                }

                uiDocument.Selection.SetElementIds(new[] { image.Id });
                result.ImageInstanceId = image.Id;
                result.ViewId = view.Id;
                result.ViewName = view.Name;
                result.LevelName = view.GenLevel?.Name ?? string.Empty;
            },
            error => _window.Dispatcher.BeginInvoke(() =>
            {
                if (_disposed) return;
                placeButton.IsEnabled = true;
                if (error is not null)
                {
                    Status.Text = $"PDF placement failed: {error.Message}";
                    MessageBox.Show(_window, error.Message, "FamilyMEP - Spinkler", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                _alignedPdfInstanceId = result.ImageInstanceId;
                _scannedPlacementViewId = result.ViewId;
                _alignedPdfBottomLeft = null;
                _alignedPdfBottomRight = null;
                _alignedPdfTopLeft = null;
                if (!string.IsNullOrWhiteSpace(result.LevelName))
                    SelectComboValue(Find<WpfComboBox>("v2_link_level_combo"), result.LevelName);
                TextBlock placementStatus = Find<TextBlock>("v2_placement_area_status_text");
                placementStatus.Text = $"PDF selected in {result.ViewName}. Move / scale / rotate it, then click step 2.";
                placementStatus.Foreground = Brush("#C98200");
                Status.Text = "PDF is selected in Revit. Align it with the architecture, then capture its position.";
            }));
        if (!queued)
        {
            placeButton.IsEnabled = true;
            Status.Text = "Another Revit request is still running.";
            return;
        }
        ExternalEventRequest request = _revitExternalEvent.Raise();
        if (request is not ExternalEventRequest.Accepted and not ExternalEventRequest.Pending)
        {
            placeButton.IsEnabled = true;
            Status.Text = "Revit did not accept the PDF placement request.";
        }
    }

    private void CaptureAlignedPdfPosition()
    {
        if (IsCadSource)
        {
            CaptureAlignedCadPosition();
            return;
        }
        if (Find<WpfComboBox>("v2_target_view_combo").SelectedItem is not RevitViewOption selectedView)
        {
            Status.Text = "Select the Revit view containing the aligned PDF.";
            return;
        }

        var result = new PdfAlignmentResult();
        Button captureButton = Find<Button>("v2_use_aligned_pdf_btn");
        captureButton.IsEnabled = false;
        Status.Text = "Reading the aligned PDF transform from Revit...";
        string? pdfPath = string.IsNullOrWhiteSpace(_currentPdfPath) ? null : Path.GetFullPath(_currentPdfPath);
        ElementId? knownImageId = _alignedPdfInstanceId;
        bool queued = _revitRequestHandler.TrySetRequest(
            app =>
            {
                UIDocument uiDocument = app.ActiveUIDocument
                    ?? throw new InvalidOperationException("Open the target Revit project first.");
                if (!IsSameRevitDocument(uiDocument.Document))
                    throw new InvalidOperationException("The active document changed. Reopen Spinkler in the target project.");
                Document document = uiDocument.Document;
                ViewPlan view = document.GetElement(selectedView.Id) as ViewPlan
                    ?? throw new InvalidOperationException("The selected target view is no longer available.");
                uiDocument.ActiveView = view;

                ImageInstance? image = knownImageId is not null
                    ? document.GetElement(knownImageId) as ImageInstance
                    : null;
                if (image is null || image.OwnerViewId != view.Id)
                {
                    image = uiDocument.Selection.GetElementIds()
                        .Select(document.GetElement)
                        .OfType<ImageInstance>()
                        .FirstOrDefault(item => item.OwnerViewId == view.Id);
                }
                image ??= pdfPath is null ? null : FindPdfImageInstance(document, view, pdfPath);
                if (image is null)
                    throw new InvalidOperationException("No PDF is selected in this view. Use step 1, align the PDF in Revit, then try again.");

                result.ImageInstanceId = image.Id;
                result.ViewId = view.Id;
                result.ViewName = view.Name;
                result.LevelName = view.GenLevel?.Name ?? string.Empty;
                result.BottomLeft = image.GetLocation(BoxPlacement.BottomLeft);
                result.BottomRight = image.GetLocation(BoxPlacement.BottomRight);
                result.TopLeft = image.GetLocation(BoxPlacement.TopLeft);
                if (result.Width <= 0.001 || result.Height <= 0.001)
                    throw new InvalidOperationException("The selected PDF has an invalid size. Scale it in Revit and capture again.");
            },
            error => _window.Dispatcher.BeginInvoke(() =>
            {
                if (_disposed) return;
                captureButton.IsEnabled = true;
                if (error is not null)
                {
                    Status.Text = $"Could not capture the PDF position: {error.Message}";
                    MessageBox.Show(_window, error.Message, "FamilyMEP - Spinkler", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                _alignedPdfInstanceId = result.ImageInstanceId;
                _scannedPlacementViewId = result.ViewId;
                _alignedPdfBottomLeft = result.BottomLeft;
                _alignedPdfBottomRight = result.BottomRight;
                _alignedPdfTopLeft = result.TopLeft;
                if (!string.IsNullOrWhiteSpace(result.LevelName))
                    SelectComboValue(Find<WpfComboBox>("v2_link_level_combo"), result.LevelName);
                TextBlock placementStatus = Find<TextBlock>("v2_placement_area_status_text");
                placementStatus.Text = $"Aligned PDF captured in {result.ViewName} • {result.Width:0.##} × {result.Height:0.##} ft";
                placementStatus.Foreground = Brush("#23834B");
                Status.Text = "Aligned PDF transform captured. Sprinklers will follow this exact position.";
            }));
        if (!queued)
        {
            captureButton.IsEnabled = true;
            Status.Text = "Another Revit request is still running.";
            return;
        }
        ExternalEventRequest request = _revitExternalEvent.Raise();
        if (request is not ExternalEventRequest.Accepted and not ExternalEventRequest.Pending)
        {
            captureButton.IsEnabled = true;
            Status.Text = "Revit did not accept the PDF capture request.";
        }
    }

    private void PlaceOrSelectCadInRevit(bool analyzeAfterPlacement = false)
    {
        if (Find<WpfComboBox>("v2_target_view_combo").SelectedItem is not RevitViewOption selectedView)
        {
            Status.Text = "Select a target Revit view first.";
            return;
        }
        if (string.IsNullOrWhiteSpace(_currentPdfPath) || !File.Exists(_currentPdfPath))
        {
            Status.Text = "Browse and load a DWG or DXF before importing it.";
            return;
        }

        string cadPath = Path.GetFullPath(_currentPdfPath);
        string cadUnitName = Find<WpfComboBox>("v2_cad_units_combo").SelectedItem as string
            ?? "Millimeters";
        ImportUnit cadImportUnit = CadImportUnitFromName(cadUnitName);
        if (!TryReadPositiveScaleFactor(
                Find<TextBox>("v2_cad_scale_factor_tb").Text,
                out double cadScaleCorrection))
        {
            Status.Text = "Enter a positive DWG scale correction, for example 1.0 or 6.6667.";
            MessageBox.Show(
                _window,
                "DWG scale correction must be a positive number.\n\nUse: required distance / measured distance.",
                "FamilyMEP - Spinkler",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }
        ElementId? knownImportId = _alignedPdfInstanceId;
        double customScaleRelativeToProject =
            CadScaleRelativeToProjectUnits(_document, cadUnitName) * cadScaleCorrection;
        var result = new PdfAlignmentResult();
        Button placeButton = Find<Button>("v2_place_pdf_in_revit_btn");
        placeButton.IsEnabled = false;
        Status.Text = $"Looking for an existing {Path.GetFileName(cadPath)} in {selectedView.Name}...";
        bool queued = _revitRequestHandler.TrySetRequest(
            app =>
            {
                UIDocument uiDocument = app.ActiveUIDocument
                    ?? throw new InvalidOperationException("Open the target Revit project first.");
                if (!IsSameRevitDocument(uiDocument.Document))
                    throw new InvalidOperationException("The active document changed. Reopen Spinkler in the target project.");
                Document document = uiDocument.Document;
                ViewPlan view = document.GetElement(selectedView.Id) as ViewPlan
                    ?? throw new InvalidOperationException("The selected target view is no longer available.");
                uiDocument.ActiveView = view;

                ImportInstance? importInstance = FindExistingCadImport(
                    document,
                    view,
                    cadPath,
                    knownImportId,
                    uiDocument.Selection.GetElementIds());
                result.ReusedExisting = importInstance is not null;
                if (importInstance is null)
                {
                    using var transaction = new Transaction(document, "FamilyMEP - Import sprinkler CAD");
                    transaction.Start();
                    var options = new DWGImportOptions
                    {
                        ColorMode = ImportColorMode.Preserved,
                        Unit = cadImportUnit,
                        Placement = ImportPlacement.Origin,
                        OrientToView = true,
                        ThisViewOnly = true,
                        VisibleLayersOnly = false
                    };
                    if (Math.Abs(cadScaleCorrection - 1.0) > 0.0000001)
                    {
                        options.Unit = ImportUnit.Custom;
                        options.CustomScale = customScaleRelativeToProject;
                    }
                    if (!document.Import(cadPath, options, view, out ElementId importedId) ||
                        importedId == ElementId.InvalidElementId)
                        throw new InvalidOperationException("Revit could not import the selected CAD file. Check its units and file integrity.");
                    if (transaction.Commit() != TransactionStatus.Committed)
                        throw new InvalidOperationException("Revit rolled back the CAD import.");
                    importInstance = document.GetElement(importedId) as ImportInstance
                        ?? throw new InvalidOperationException("The imported CAD instance is unavailable.");
                }

                uiDocument.Selection.SetElementIds([importInstance.Id]);
                result.ImageInstanceId = importInstance.Id;
                result.ViewId = view.Id;
                result.ViewName = view.Name;
                result.LevelName = view.GenLevel?.Name ?? string.Empty;
            },
            error => _window.Dispatcher.BeginInvoke(() =>
            {
                if (_disposed) return;
                placeButton.IsEnabled = true;
                if (error is not null)
                {
                    Status.Text = $"CAD import failed: {error.Message}";
                    MessageBox.Show(_window, error.Message, "FamilyMEP - Spinkler", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                _alignedPdfInstanceId = result.ImageInstanceId;
                _scannedPlacementViewId = result.ViewId;
                _alignedPdfBottomLeft = null;
                _alignedPdfBottomRight = null;
                _alignedPdfTopLeft = null;
                if (!string.IsNullOrWhiteSpace(result.LevelName))
                    SelectComboValue(Find<WpfComboBox>("v2_link_level_combo"), result.LevelName);
                TextBlock placementStatus = Find<TextBlock>("v2_placement_area_status_text");
                placementStatus.Text =
                    $"CAD selected in {result.ViewName} at {cadUnitName} × {cadScaleCorrection:0.####}. " +
                    "Move / rotate it if needed, then click step 2.";
                placementStatus.Foreground = Brush("#C98200");
                if (result.ReusedExisting)
                    placementStatus.Text =
                        $"Existing CAD reused in {result.ViewName}. Current Revit scale and position are preserved.";
                Status.Text = result.ReusedExisting
                    ? "Existing CAD selected. Its current scale and aligned position are preserved."
                    : "New CAD imported. Align it with the architecture, then read its layers.";
                if (analyzeAfterPlacement)
                {
                    Status.Text = result.ReusedExisting
                        ? "Existing CAD found. Reading its layers and geometry..."
                        : "CAD imported. Reading layers and building the preview...";
                    _window.Dispatcher.BeginInvoke(
                        new Action(CaptureAlignedCadPosition),
                        DispatcherPriority.Background);
                }
            }));
        if (!queued)
        {
            placeButton.IsEnabled = true;
            Status.Text = "Another Revit request is still running.";
            return;
        }
        ExternalEventRequest request = _revitExternalEvent.Raise();
        if (request is not ExternalEventRequest.Accepted and not ExternalEventRequest.Pending)
        {
            placeButton.IsEnabled = true;
            Status.Text = "Revit did not accept the CAD import request.";
        }
    }

    private static ImportUnit CadImportUnitFromName(string unitName) => unitName switch
    {
        "Meters" => ImportUnit.Meter,
        "Centimeters" => ImportUnit.Centimeter,
        "Inches" => ImportUnit.Inch,
        "Feet" => ImportUnit.Foot,
        _ => ImportUnit.Millimeter
    };

#if REVIT2020
    private static DisplayUnitType CadUnitTypeIdFromName(string unitName) => unitName switch
    {
        "Meters" => DisplayUnitType.DUT_METERS,
        "Centimeters" => DisplayUnitType.DUT_CENTIMETERS,
        "Inches" => DisplayUnitType.DUT_DECIMAL_INCHES,
        "Feet" => DisplayUnitType.DUT_DECIMAL_FEET,
        _ => DisplayUnitType.DUT_MILLIMETERS
    };

    private static double CadScaleRelativeToProjectUnits(Document document, string unitName)
    {
        DisplayUnitType drawingUnit = CadUnitTypeIdFromName(unitName);
        DisplayUnitType projectUnit = document.GetUnits()
            .GetFormatOptions(UnitType.UT_Length)
            .DisplayUnits;
        double drawingUnitInFeet = UnitUtils.ConvertToInternalUnits(1.0, drawingUnit);
        double projectUnitInFeet = UnitUtils.ConvertToInternalUnits(1.0, projectUnit);
        return drawingUnitInFeet / Math.Max(projectUnitInFeet, 1e-12);
    }

#else
    private static ForgeTypeId CadUnitTypeIdFromName(string unitName) => unitName switch
    {
        "Meters" => UnitTypeId.Meters,
        "Centimeters" => UnitTypeId.Centimeters,
        "Inches" => UnitTypeId.Inches,
        "Feet" => UnitTypeId.Feet,
        _ => UnitTypeId.Millimeters
    };

    private static double CadScaleRelativeToProjectUnits(Document document, string unitName)
    {
        ForgeTypeId drawingUnit = CadUnitTypeIdFromName(unitName);
        ForgeTypeId projectUnit = document.GetUnits()
            .GetFormatOptions(SpecTypeId.Length)
            .GetUnitTypeId();
        double drawingUnitInFeet = UnitUtils.ConvertToInternalUnits(1.0, drawingUnit);
        double projectUnitInFeet = UnitUtils.ConvertToInternalUnits(1.0, projectUnit);
        return drawingUnitInFeet / Math.Max(projectUnitInFeet, 1e-12);
    }

#endif
    private static bool TryReadPositiveScaleFactor(string text, out double value)
    {
        bool parsed = double.TryParse(
                          text,
                          NumberStyles.Float,
                          CultureInfo.CurrentCulture,
                          out value) ||
                      double.TryParse(
                          text,
                          NumberStyles.Float,
                          CultureInfo.InvariantCulture,
                          out value);
        return parsed && PortableMath.IsFinite(value) && value > 0;
    }

    private void CaptureAlignedCadPosition()
    {
        if (Find<WpfComboBox>("v2_target_view_combo").SelectedItem is not RevitViewOption selectedView)
        {
            Status.Text = "Select the Revit view containing the imported CAD.";
            return;
        }
        string? cadPath = string.IsNullOrWhiteSpace(_currentPdfPath)
            ? null
            : Path.GetFullPath(_currentPdfPath);
        ElementId? knownImportId = _alignedPdfInstanceId;
        CadImportAnalysis? analysis = null;
        Button captureButton = Find<Button>("v2_use_aligned_pdf_btn");
        captureButton.IsEnabled = false;
        Status.Text = "Reading CAD layers and vector geometry from Revit...";
        bool queued = _revitRequestHandler.TrySetRequest(
            app =>
            {
                UIDocument uiDocument = app.ActiveUIDocument
                    ?? throw new InvalidOperationException("Open the target Revit project first.");
                if (!IsSameRevitDocument(uiDocument.Document))
                    throw new InvalidOperationException("The active document changed. Reopen Spinkler in the target project.");
                Document document = uiDocument.Document;
                ViewPlan view = document.GetElement(selectedView.Id) as ViewPlan
                    ?? throw new InvalidOperationException("The selected target view is no longer available.");
                uiDocument.ActiveView = view;
                ImportInstance? importInstance = FindExistingCadImport(
                    document,
                    view,
                    cadPath,
                    knownImportId,
                    uiDocument.Selection.GetElementIds());
                if (importInstance is null)
                    throw new InvalidOperationException(
                        "No matching imported CAD was found. Select the existing CAD in this view, or use step 1 to import it once.");
                analysis = CadImportExtractor.Extract(document, importInstance, view);
            },
            error => _window.Dispatcher.BeginInvoke(() =>
            {
                if (_disposed) return;
                captureButton.IsEnabled = true;
                if (error is not null || analysis is null)
                {
                    string message = error?.Message ?? "CAD analysis returned no result.";
                    Status.Text = $"Could not analyze CAD: {message}";
                    MessageBox.Show(_window, message, "FamilyMEP - Spinkler", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                try
                {
                    CadSceneBuildResult build = analysis.BuildScene();
                    // The Revit-extracted preview and vector scene share exactly the
                    // same ImportInstance extents. AutoCAD PNGOUT is only a screenshot
                    // and can crop/scale differently, causing blurred or drifting CAD.
                    // Use native Revit geometry as the authoritative Layer preview.
                    BitmapSource displayPreview = build.Preview;
                    ClearVectorSourceState();
                    _vectorScene = build.Scene;
                    _renderedPdfPage = displayPreview;
                    _cadLayerByPathId = build.LayerByPath;
                    _cadLayerCounts = build.LayerCounts;
                    _cadNativeColorBySegmentId = build.NativeColorBySegment;
                    CacheCadPreviewPixels(displayPreview);
                    PopulateCadLayerChooser();
                    _pdfRotation = 0;
                    _pdfZoom = 1.0;
                    Find<Image>("v2_source_pdf_image").Source = displayPreview;
                    Find<Image>("v2_layer_pdf_image").Source = displayPreview;
                    Find<Image>("v2_layer_pdf_image").Opacity = 1.0;
                    _alignedPdfInstanceId = analysis.ImportInstanceId;
                    _scannedPlacementViewId = analysis.ViewId;
                    _alignedPdfBottomLeft = analysis.BottomLeft;
                    _alignedPdfBottomRight = analysis.BottomRight;
                    _alignedPdfTopLeft = analysis.TopLeft;
                    if (!string.IsNullOrWhiteSpace(analysis.LevelName))
                        SelectComboValue(Find<WpfComboBox>("v2_link_level_combo"), analysis.LevelName);
                    Find<TextBlock>("v2_pdf_document_meta_text").Text =
                        $"  â€¢  {_cadLayerCounts.Count:N0} CAD layers  â€¢  Native Revit geometry";
                    Find<TextBlock>("v2_source_quality_badge").Text = "REVIT VECTOR CAD";
                    Find<TextBlock>("v2_source_quality_text").Text =
                        $"{build.Scene.Segments.Length:N0} native Revit CAD vectors read from {_cadLayerCounts.Count:N0} layer(s).";
                    Find<TextBlock>("v2_vector_count_text").Text = build.Scene.Segments.Length.ToString("N0");
                    Find<TextBlock>("v2_symbol_count_text").Text = "Pick block";
                    Find<TextBlock>("v2_unclassified_count_text").Text = _cadLayerCounts.Count.ToString("N0");
                    TextBlock placementStatus = Find<TextBlock>("v2_placement_area_status_text");
                    placementStatus.Text =
                        $"Aligned CAD captured in {analysis.ViewName} â€¢ {_cadLayerCounts.Count:N0} layers â€¢ {analysis.Width:0.##} Ã— {analysis.Height:0.##} ft";
                    placementStatus.Foreground = Brush("#23834B");
                    ActivateVectorPicking();
                    HidePdfPlaceholders();
                    UpdateRenderedPdfVisibility();
                    UpdatePdfZoomText();
                    UpdateRenderedPdfZoom();
                    LoadMappingSidecar(cadPath ?? string.Empty);
                    SaveMappingSidecar();
                    Status.Text = "CAD ready with native Revit vector geometry - zoom and vector picking are active.";
                }
                catch (Exception buildException)
                {
                    Status.Text = $"Could not build CAD preview: {buildException.Message}";
                    MessageBox.Show(_window, buildException.Message, "FamilyMEP - Spinkler", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }));
        if (!queued)
        {
            captureButton.IsEnabled = true;
            Status.Text = "Another Revit request is still running.";
            return;
        }
        ExternalEventRequest request = _revitExternalEvent.Raise();
        if (request is not ExternalEventRequest.Accepted and not ExternalEventRequest.Pending)
        {
            captureButton.IsEnabled = true;
            Status.Text = "Revit did not accept the CAD analysis request.";
        }
    }

    private static ImportInstance? FindExistingCadImport(
        Document document,
        View view,
        string? cadPath,
        ElementId? preferredId,
        ICollection<ElementId> selectedIds)
    {
        bool IsAvailableInView(ImportInstance instance) =>
            instance.OwnerViewId == view.Id || instance.OwnerViewId == ElementId.InvalidElementId;

        if (preferredId is not null &&
            document.GetElement(preferredId) is ImportInstance remembered &&
            IsAvailableInView(remembered))
            return remembered;

        ImportInstance? selected = selectedIds
            .Select(document.GetElement)
            .OfType<ImportInstance>()
            .FirstOrDefault(IsAvailableInView);
        if (selected is not null) return selected;

        if (string.IsNullOrWhiteSpace(cadPath)) return null;
        string fileName = Path.GetFileName(cadPath);
        string baseName = Path.GetFileNameWithoutExtension(cadPath);
        return new FilteredElementCollector(document)
            .OfClass(typeof(ImportInstance))
            .Cast<ImportInstance>()
            .Where(IsAvailableInView)
            .FirstOrDefault(instance =>
            {
                Element? type = document.GetElement(instance.GetTypeId());
                string[] names = [instance.Name ?? string.Empty, type?.Name ?? string.Empty];
                return names.Any(name =>
                    string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, baseName, StringComparison.OrdinalIgnoreCase) ||
                    name.Contains(fileName, StringComparison.OrdinalIgnoreCase) ||
                    name.Contains(baseName, StringComparison.OrdinalIgnoreCase));
            });
    }

    private static ImageInstance? FindPdfImageInstance(Document document, View view, string pdfPath)
    {
        string expected = Path.GetFullPath(pdfPath);
        return new FilteredElementCollector(document, view.Id)
            .OfClass(typeof(ImageInstance))
            .Cast<ImageInstance>()
            .FirstOrDefault(instance =>
            {
                ImageType? type = document.GetElement(instance.GetTypeId()) as ImageType;
                if (type is null || string.IsNullOrWhiteSpace(type.Path)) return false;
                try { return string.Equals(Path.GetFullPath(type.Path), expected, StringComparison.OrdinalIgnoreCase); }
                catch { return string.Equals(type.Path, pdfPath, StringComparison.OrdinalIgnoreCase); }
            });
    }

    private static ImageType? FindPdfImageType(Document document, string pdfPath)
    {
        string expected = Path.GetFullPath(pdfPath);
        return new FilteredElementCollector(document)
            .OfClass(typeof(ImageType))
            .Cast<ImageType>()
            .FirstOrDefault(type =>
            {
                if (string.IsNullOrWhiteSpace(type.Path)) return false;
                try { return string.Equals(Path.GetFullPath(type.Path), expected, StringComparison.OrdinalIgnoreCase); }
                catch { return string.Equals(type.Path, pdfPath, StringComparison.OrdinalIgnoreCase); }
            });
    }

    private void OnNextClicked()
    {
        if (MainTabs.SelectedIndex == 2)
        {
            CreateSprinklerLayerTest();
            return;
        }
        MoveTab(1);
    }

    private void CreateSprinklerLayerTest()
    {
        if (_vectorScene is null || !_vectorPicks.ContainsKey(PdfVectorClass.Sprinkler) ||
            !IsVectorClassEnabled(PdfVectorClass.Sprinkler))
        {
            MessageBox.Show(
                _window,
                "Pick and verify the FP-SPRINKLER layer before creating the test model.",
                "FamilyMEP - Spinkler",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        List<System.Windows.Point> points = ResolveSprinklerObjects().ToList();
        if (points.Count == 0)
        {
            Status.Text = "No closed sprinkler symbols remain after exclusions.";
            return;
        }
        RestoreDefaultMappingForPickedClass(PdfVectorClass.MainPipe, explicitPick: false);
        RestoreDefaultMappingForPickedClass(PdfVectorClass.BranchPipe, explicitPick: false);
        var unavailablePipeLayers = new List<string>();
        AddUnavailablePipeLayer(PdfVectorClass.MainPipe, "FP-PIPE-MAIN");
        AddUnavailablePipeLayer(PdfVectorClass.BranchPipe, "FP-PIPE-BRANCH");
        if (unavailablePipeLayers.Count > 0)
        {
            MessageBox.Show(
                _window,
                "The following pipe classification is not ready:\n\n" +
                string.Join("\n", unavailablePipeLayers) +
                "\n\nReturn to Layers and use its Pick button. Picking a new seed now automatically restores the correct Revit category.",
                "Pipe layers required",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        void AddUnavailablePipeLayer(PdfVectorClass vectorClass, string name)
        {
            if (!_vectorPicks.ContainsKey(vectorClass))
            {
                unavailablePipeLayers.Add($"• {name}: no PDF seed selected");
                return;
            }
            LayerMappingItem? layer = _layerItems.FirstOrDefault(item =>
                item.Marker == MarkerForClass(vectorClass));
            if (!IsVectorClassEnabled(vectorClass))
                unavailablePipeLayers.Add(
                    $"• {name}: disabled ({layer?.RevitCategory ?? "unknown"} / {layer?.Status ?? "unknown"})");
        }
        List<PdfPipeRun> mainPipeRuns = CollapseNearDuplicatePipeRuns(
            ResolvePipeRuns(PdfVectorClass.MainPipe),
            4.0).ToList();
        List<PdfPipeRun> branchPipeRuns = CollapseNearDuplicatePipeRuns(
            ResolvePipeRuns(PdfVectorClass.BranchPipe),
            4.0).ToList();
        mainPipeRuns = MergeConnectedCollinearPipeRuns(mainPipeRuns, 12.0).ToList();
        branchPipeRuns = MergeConnectedCollinearPipeRuns(branchPipeRuns, 12.0).ToList();
        AlignNearbySprinklerRowsAndColumns(points, 6.0);
        AlignBranchEndpointsToSprinklers(points, branchPipeRuns, 10.0);
        List<PdfPipeRun> pipeRuns = CollapseNearDuplicatePipeRuns(
            SplitPipeRuns(mainPipeRuns.Concat(branchPipeRuns).ToArray()),
            1.25).ToList();
        pipeRuns = PruneDanglingBranchRuns(pipeRuns, points, 12.0).ToList();
        if (pipeRuns.Count == 0)
        {
            Status.Text = "No usable main or branch pipe centerlines were found.";
            return;
        }
        if (pipeRuns.Count > 2500)
        {
            MessageBox.Show(
                _window,
                $"The current classifications produce {pipeRuns.Count:N0} pipe segments. Exclude incorrect PDF geometry before testing (maximum 2,500).",
                "Too many pipe segments",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        if (Find<WpfComboBox>("v2_target_view_combo").SelectedItem is not RevitViewOption targetView)
        {
            Status.Text = "Select a target Revit view.";
            return;
        }
        if (_scannedPlacementViewId is null || _alignedPdfInstanceId is null || _alignedPdfBottomLeft is null ||
            _alignedPdfBottomRight is null || _alignedPdfTopLeft is null ||
            _scannedPlacementViewId != targetView.Id)
        {
            MessageBox.Show(
                _window,
                IsCadSource
                    ? "Return to Source, import and align the CAD in the selected Revit view, then click 'Use aligned CAD + read layers'."
                    : "Return to Source, place and align the PDF in the selected Revit view, then click 'Use aligned PDF position'.",
                IsCadSource ? "Aligned CAD required" : "Aligned PDF position required",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }
        if (Find<WpfComboBox>("v2_sprinkler_family_combo").SelectedItem is not SprinklerFamilyOption selectedFamily)
        {
            Status.Text = "Load and select a Revit Sprinkler Family / Type first.";
            return;
        }
        if (Find<WpfComboBox>("v2_pipe_type_combo").SelectedItem is not PipeTypeOption selectedPipeType)
        {
            Status.Text = "Load and select a Revit Pipe Type first.";
            return;
        }
        if (selectedPipeType.JunctionRules == 0 || selectedPipeType.TransitionRules == 0 || selectedPipeType.ElbowRules == 0)
        {
            var missing = new List<string>();
            if (selectedPipeType.JunctionRules == 0) missing.Add("Tee / Junction");
            if (selectedPipeType.TransitionRules == 0) missing.Add("Reducer / Transition");
            if (selectedPipeType.ElbowRules == 0) missing.Add("Elbow");
            MessageBox.Show(
                _window,
                $"Pipe Type '{selectedPipeType.Name}' is missing Routing Preference rules for:\n\n" +
                $"• {string.Join("\n• ", missing)}\n\n" +
                "Choose another Pipe Type or add the missing fitting families to its Routing Preferences before creating the system.",
                "Pipe fitting rules are missing",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }
        if (!TryReadNominalDiameter(Find<WpfComboBox>("v2_main_dn_combo"), out double mainDiameterMm) ||
            !TryReadNominalDiameter(Find<WpfComboBox>("v2_branch_dn_combo"), out double branchDiameterMm))
        {
            Status.Text = "Select valid DN sizes for main and branch pipes.";
            return;
        }
        string elevationText = Find<TextBox>("v2_sprinkler_elevation_tb").Text.Trim();
        if (!double.TryParse(elevationText, NumberStyles.Float, CultureInfo.CurrentCulture, out double elevationValue) &&
            !double.TryParse(elevationText, NumberStyles.Float, CultureInfo.InvariantCulture, out elevationValue))
        {
            Status.Text = "Enter a valid sprinkler elevation reference offset.";
            return;
        }
        string units = Find<WpfComboBox>("v2_units_combo").SelectedItem as string ?? "Millimeters";
        string elevationMode = Find<WpfComboBox>("v2_sprinkler_reference_mode_combo").SelectedItem as string
                               ?? ElevationModeLevel;
        ElevationReferenceOption? elevationReference =
            Find<WpfComboBox>("v2_sprinkler_reference_target_combo").SelectedItem as ElevationReferenceOption;
        if (elevationMode == ElevationModeReferencePlane && elevationReference is null)
        {
            Status.Text = "Select a named Reference Plane for sprinkler placement.";
            return;
        }
        if (elevationMode == ElevationModeCeiling && elevationReference is null)
        {
            Status.Text = "Select Auto or a Ceiling for sprinkler placement.";
            return;
        }
        if (elevationMode == ElevationModeFloor && elevationReference is null)
        {
            Status.Text = "Select Auto or a Floor for sprinkler placement.";
            return;
        }
        ElementId elevationReferenceId = elevationReference?.Id ?? ElementId.InvalidElementId;
        string elevationDescription = elevationMode == ElevationModeLevel
            ? $"{elevationValue:0.###} {units} above {Find<WpfComboBox>("v2_link_level_combo").SelectedItem}"
            : elevationMode == ElevationModeFloor
                ? $"{elevationValue:0.###} {units} above {elevationReference}"
            : elevationMode == ElevationModeCeiling
                ? $"{elevationValue:0.###} {units} below {elevationReference}"
                : $"{elevationValue:0.###} {units} from {elevationReference}";
        string pipeElevationText = Find<TextBox>("v2_pipe_elevation_tb").Text.Trim();
        if (!double.TryParse(pipeElevationText, NumberStyles.Float, CultureInfo.CurrentCulture, out double pipeElevationValue) &&
            !double.TryParse(pipeElevationText, NumberStyles.Float, CultureInfo.InvariantCulture, out pipeElevationValue))
        {
            Status.Text = "Enter a valid pipe elevation above level.";
            return;
        }
        double elevationOffset = UnitUtils.ConvertToInternalUnits(
            elevationValue,
            units == "Inches" ? UnitTypeId.Inches : UnitTypeId.Millimeters);
        double pipeElevationOffset = UnitUtils.ConvertToInternalUnits(
            pipeElevationValue,
            units == "Inches" ? UnitTypeId.Inches : UnitTypeId.Millimeters);
        XYZ pdfBottomLeft = _alignedPdfBottomLeft;
        XYZ pdfBottomRight = _alignedPdfBottomRight;
        XYZ pdfTopLeft = _alignedPdfTopLeft;
        double alignedWidth = pdfBottomLeft.DistanceTo(pdfBottomRight);
        double alignedHeight = pdfBottomLeft.DistanceTo(pdfTopLeft);
        int mainRunCount = pipeRuns.Count(run => run.VectorClass == PdfVectorClass.MainPipe);
        int branchRunCount = pipeRuns.Count - mainRunCount;
        string selectedSystem = Find<WpfComboBox>("v2_system_type_combo").SelectedItem as string ?? "Wet";

        MessageBoxResult confirmation = MessageBox.Show(
            _window,
            $"Test mode will create:\n" +
            $"• {points.Count:N0} sprinkler heads\n" +
            $"• {mainRunCount:N0} main pipe segments — DN {mainDiameterMm:0}\n" +
            $"• {branchRunCount:N0} branch pipe segments — DN {branchDiameterMm:0}\n" +
            "• Elbow / tee / cross fittings at connected pipe nodes\n\n" +
            $"View: {targetView.Name}\nAligned {(IsCadSource ? "CAD" : "PDF")}: {alignedWidth:0.##} × {alignedHeight:0.##} ft\n" +
            $"Sprinkler Family / Type: {selectedFamily}\nPipe Type: {selectedPipeType}\n" +
            $"Sprinkler reference: {elevationDescription}\n" +
            $"Pipe elevation: {pipeElevationValue:0.###} {units}\n\n" +
            $"Each detected point will follow the {(IsCadSource ? "CAD" : "PDF")}'s captured Revit position and scale. " +
            "Use one Revit Undo to remove the batch if the alignment is not correct.\n\nContinue?",
            "Create FP-SPRINKLER test",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirmation != MessageBoxResult.Yes) return;

        string targetLevelName = Find<WpfComboBox>("v2_link_level_combo").SelectedItem as string ?? string.Empty;
        var result = new SprinklerCreationResult();
        Button nextButton = Find<Button>("next_btn");
        nextButton.IsEnabled = false;
        Status.Text = $"Creating {points.Count:N0} heads plus main/branch pipes and fittings in Revit...";
        bool queued = _revitRequestHandler.TrySetRequest(
            app => ExecuteSprinklerLayerTest(
                app,
                points,
                pipeRuns,
                targetLevelName,
                targetView.Id,
                selectedFamily.Id,
                selectedPipeType.Id,
                selectedSystem,
                UnitUtils.ConvertToInternalUnits(mainDiameterMm, UnitTypeId.Millimeters),
                UnitUtils.ConvertToInternalUnits(branchDiameterMm, UnitTypeId.Millimeters),
                _alignedPdfInstanceId,
                IsCadSource,
                pdfBottomLeft,
                pdfBottomRight,
                pdfTopLeft,
                elevationMode,
                elevationReferenceId,
                elevationOffset,
                pipeElevationOffset,
                result),
            error => _window.Dispatcher.BeginInvoke(() =>
            {
                if (_disposed) return;
                nextButton.IsEnabled = true;
                if (error is not null)
                {
                    Status.Text = $"Sprinkler test failed: {error.Message}";
                    MessageBox.Show(
                        _window,
                        error.Message,
                        "FamilyMEP - Spinkler",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                    return;
                }
                Status.Text = $"Created {result.Created:N0} heads, {result.MainPipes:N0} main pipes, {result.BranchPipes:N0} branch pipes, and {result.Fittings:N0} fittings.";
                MessageBox.Show(
                    _window,
                    $"Sprinklers: {result.Created:N0} created / {result.Skipped:N0} skipped\n" +
                    $"Main pipes: {result.MainPipes:N0}\nBranch pipes: {result.BranchPipes:N0}\n" +
                    $"Pipe segments skipped: {result.PipesSkipped:N0}\n" +
                    $"Fittings: {result.Fittings:N0} created / {result.FittingsSkipped:N0} skipped\n" +
                    $"Sprinkler drops / rises: {result.SprinklerDrops:N0}\n" +
                    $"Sprinkler connections: {result.SprinklerConnections:N0} connected / {result.SprinklerConnectionsSkipped:N0} skipped\n" +
                    $"Family / Type: {result.FamilyType}\n" +
                    $"{result.PlacementFailureSummary}" +
                    $"{result.FittingFailureSummary}\n\n" +
                    "Use one Revit Undo to remove the complete test batch.",
                    "FP-SPRINKLER system test complete",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }));
        if (!queued)
        {
            nextButton.IsEnabled = true;
            Status.Text = "Another Revit request is still running.";
            return;
        }
        ExternalEventRequest request = _revitExternalEvent.Raise();
        if (request is not ExternalEventRequest.Accepted and not ExternalEventRequest.Pending)
        {
            nextButton.IsEnabled = true;
            Status.Text = "Revit did not accept the sprinkler creation request.";
        }
    }

    private void ExecuteSprinklerLayerTest(
        UIApplication application,
        IReadOnlyList<System.Windows.Point> normalizedPoints,
        IReadOnlyList<PdfPipeRun> pipeRuns,
        string targetLevelName,
        ElementId targetViewId,
        ElementId familySymbolId,
        ElementId pipeTypeId,
        string requestedSystem,
        double mainDiameter,
        double branchDiameter,
        ElementId alignedPdfInstanceId,
        bool sourceIsCad,
        XYZ pdfBottomLeft,
        XYZ pdfBottomRight,
        XYZ pdfTopLeft,
        string elevationMode,
        ElementId elevationReferenceId,
        double elevationOffset,
        double pipeElevationOffset,
        SprinklerCreationResult result)
    {
        UIDocument uiDocument = application.ActiveUIDocument
            ?? throw new InvalidOperationException("Open a Revit project before creating sprinklers.");
        Document document = uiDocument.Document;
        if (!IsSameRevitDocument(document))
            throw new InvalidOperationException("The active Revit document changed. Reopen Spinkler in the target project.");
        ViewPlan view = document.GetElement(targetViewId) as ViewPlan
            ?? throw new InvalidOperationException("The selected target view is no longer available.");
        uiDocument.ActiveView = view;
        if (sourceIsCad)
        {
            ImportInstance alignedCad = document.GetElement(alignedPdfInstanceId) as ImportInstance
                ?? throw new InvalidOperationException("The aligned CAD import is no longer available in Revit. Return to Source and select it again.");
            if (alignedCad.OwnerViewId != ElementId.InvalidElementId && alignedCad.OwnerViewId != view.Id)
                throw new InvalidOperationException("The aligned CAD import does not belong to the selected target view.");
        }
        else
        {
            ImageInstance alignedPdf = document.GetElement(alignedPdfInstanceId) as ImageInstance
                ?? throw new InvalidOperationException("The aligned PDF is no longer available in Revit. Return to Source and select it again.");
            if (alignedPdf.OwnerViewId != view.Id)
                throw new InvalidOperationException("The aligned PDF does not belong to the selected target view.");
            // Read PDF corners again so a final image Move/Scale/Rotate is honored.
            pdfBottomLeft = alignedPdf.GetLocation(BoxPlacement.BottomLeft);
            pdfBottomRight = alignedPdf.GetLocation(BoxPlacement.BottomRight);
            pdfTopLeft = alignedPdf.GetLocation(BoxPlacement.TopLeft);
        }
        Level level = new FilteredElementCollector(document)
            .OfClass(typeof(Level))
            .Cast<Level>()
            .FirstOrDefault(item => string.Equals(item.Name, targetLevelName, StringComparison.CurrentCultureIgnoreCase))
            ?? throw new InvalidOperationException($"Target level '{targetLevelName}' was not found.");
        FamilySymbol symbol = document.GetElement(familySymbolId) as FamilySymbol
            ?? throw new InvalidOperationException("The selected Sprinkler Family / Type is no longer available.");
        if (symbol.Category?.Id.CompatValue() != (long)BuiltInCategory.OST_Sprinklers)
            throw new InvalidOperationException("The selected Family / Type is not in the Revit Sprinklers category.");
        result.FamilyType = $"{symbol.Family.Name} : {symbol.Name}";
        PipeType pipeType = document.GetElement(pipeTypeId) as PipeType
            ?? throw new InvalidOperationException("The selected Revit Pipe Type is no longer available.");
        PipingSystemType systemType = new FilteredElementCollector(document)
            .OfClass(typeof(PipingSystemType))
            .Cast<PipingSystemType>()
            .FirstOrDefault(type =>
                type.SystemClassification.ToString().Contains(requestedSystem, StringComparison.OrdinalIgnoreCase) ||
                type.Name.Contains(requestedSystem, StringComparison.OrdinalIgnoreCase))
            ?? new FilteredElementCollector(document)
                .OfClass(typeof(PipingSystemType))
                .Cast<PipingSystemType>()
                .FirstOrDefault()
            ?? throw new InvalidOperationException("No Revit Piping System Type is available in the project.");

        XYZ pdfXAxis = pdfBottomRight - pdfBottomLeft;
        XYZ pdfYAxis = pdfTopLeft - pdfBottomLeft;
        if (pdfXAxis.GetLength() <= 0.001 || pdfYAxis.GetLength() <= 0.001)
            throw new InvalidOperationException("The aligned source transform is invalid. Capture its position again.");
        double pageWidth = _vectorScene?.PageWidth ?? 0;
        double pageHeight = _vectorScene?.PageHeight ?? 0;
        if (pageWidth <= 0 || pageHeight <= 0)
            throw new InvalidOperationException("The source vector dimensions are unavailable.");

        List<Floor> placementFloors = [];
        List<Ceiling> placementCeilings = [];
        ReferencePlane? placementReferencePlane = null;
        if (elevationMode == ElevationModeFloor)
        {
            if (elevationReferenceId != ElementId.InvalidElementId)
            {
                if (document.GetElement(elevationReferenceId) is not Floor selectedFloor)
                    throw new InvalidOperationException("The selected Floor is no longer available.");
                placementFloors.Add(selectedFloor);
            }
            else
            {
                placementFloors.AddRange(new FilteredElementCollector(document)
                    .OfClass(typeof(Floor))
                    .Cast<Floor>());
            }
            if (placementFloors.Count == 0)
                throw new InvalidOperationException("No Floor is available for sprinkler elevation placement.");
        }
        else if (elevationMode == ElevationModeCeiling)
        {
            if (elevationReferenceId != ElementId.InvalidElementId)
            {
                if (document.GetElement(elevationReferenceId) is not Ceiling selectedCeiling)
                    throw new InvalidOperationException("The selected Ceiling is no longer available.");
                placementCeilings.Add(selectedCeiling);
            }
            else
            {
                placementCeilings.AddRange(new FilteredElementCollector(document)
                    .OfClass(typeof(Ceiling))
                    .Cast<Ceiling>());
            }
            if (placementCeilings.Count == 0)
                throw new InvalidOperationException("No Ceiling is available for sprinkler elevation placement.");
        }
        else if (elevationMode == ElevationModeReferencePlane)
        {
            placementReferencePlane = document.GetElement(elevationReferenceId) as ReferencePlane
                ?? throw new InvalidOperationException("The selected Reference Plane is no longer available.");
            if (Math.Abs(placementReferencePlane.GetPlane().Normal.Z) < 0.01)
                throw new InvalidOperationException("The selected Reference Plane is vertical and cannot define sprinkler elevation.");
        }

        using var transactionGroup = new TransactionGroup(document, "FamilyMEP - Create FP-SPRINKLER system test");
        transactionGroup.Start();
        using var transaction = new Transaction(document, "Create sprinkler heads and pipes");
        transaction.Start();
        ConfigureSilentFailureHandling(transaction);
        if (!symbol.IsActive)
        {
            symbol.Activate();
            document.Regenerate();
        }
        var createdSprinklers = new List<FamilyInstance>();
        foreach (System.Windows.Point normalized in normalizedPoints)
        {
            XYZ mapped = pdfBottomLeft + normalized.X * pdfXAxis + (1.0 - normalized.Y) * pdfYAxis;
            if (!TryResolveSprinklerTargetElevation(
                    elevationMode,
                    placementFloors,
                    placementCeilings,
                    placementReferencePlane,
                    mapped,
                    level,
                    elevationOffset,
                    out double targetElevation,
                    out string elevationFailure))
            {
                result.Skipped++;
                result.RecordPlacementFailure(elevationFailure);
                continue;
            }
            XYZ location = new(
                mapped.X,
                mapped.Y,
                targetElevation);
            try
            {
                FamilyInstance instance = document.Create.NewFamilyInstance(
                    location,
                    symbol,
                    level,
                    StructuralType.NonStructural);
                Parameter? offsetParameter = instance.get_Parameter(BuiltInParameter.INSTANCE_ELEVATION_PARAM);
                if (offsetParameter is null || offsetParameter.IsReadOnly)
                    offsetParameter = instance.get_Parameter(BuiltInParameter.INSTANCE_FREE_HOST_OFFSET_PARAM);
                if (offsetParameter is not null && !offsetParameter.IsReadOnly)
                    offsetParameter.Set(targetElevation - level.Elevation);
                createdSprinklers.Add(instance);
                result.Created++;
            }
            catch (Exception exception)
            {
                result.Skipped++;
                result.RecordPlacementFailure(exception.Message);
            }
        }
        var createdPipes = new List<CreatedPipeEdge>();
        foreach (PdfPipeRun run in pipeRuns)
        {
            XYZ startMapped = pdfBottomLeft + (run.X1 / pageWidth) * pdfXAxis +
                              (1.0 - run.Y1 / pageHeight) * pdfYAxis;
            XYZ endMapped = pdfBottomLeft + (run.X2 / pageWidth) * pdfXAxis +
                            (1.0 - run.Y2 / pageHeight) * pdfYAxis;
            XYZ start = new(startMapped.X, startMapped.Y, level.Elevation + pipeElevationOffset);
            XYZ end = new(endMapped.X, endMapped.Y, level.Elevation + pipeElevationOffset);
            double requestedDiameter = run.VectorClass == PdfVectorClass.MainPipe
                ? mainDiameter
                : branchDiameter;
            double safeMinimumLength = Math.Max(0.05, requestedDiameter * 3.0);
            if (start.DistanceTo(end) < safeMinimumLength)
            {
                result.PipesSkipped++;
                continue;
            }
            ElementId createdPipeId = ElementId.InvalidElementId;
            try
            {
                Pipe pipe = Pipe.Create(document, systemType.Id, pipeType.Id, level.Id, start, end);
                createdPipeId = pipe.Id;
                Parameter? diameter = pipe.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM);
                if (diameter is not null && !diameter.IsReadOnly)
                {
                    if (!diameter.Set(requestedDiameter))
                        throw new InvalidOperationException("The selected Pipe Type does not support the requested DN size.");
                }
                createdPipes.Add(new CreatedPipeEdge(pipe, start, end, run.VectorClass));
                if (run.VectorClass == PdfVectorClass.MainPipe) result.MainPipes++;
                else result.BranchPipes++;
            }
            catch
            {
                if (createdPipeId != ElementId.InvalidElementId)
                {
                    try { document.Delete(createdPipeId); } catch { }
                }
                result.PipesSkipped++;
            }
        }
        NormalizeMainBranchJunctions(document, createdPipes, result);
        NormalizeCollinearPipeEndpointGaps(document, createdPipes);
        NormalizeNearbyPipeEndpoints(document, createdPipes);
        document.Regenerate();
        if (result.Created == 0)
        {
            transaction.RollBack();
            transactionGroup.RollBack();
            throw new InvalidOperationException(
                "The loaded sprinkler family requires a host or face and could not be placed by level. Load a non-hosted/level-based sprinkler family for this test.");
        }
        if (transaction.Commit() != TransactionStatus.Committed)
        {
            transactionGroup.RollBack();
            throw new InvalidOperationException("Revit could not commit the sprinkler and pipe geometry. Review the selected PDF line classifications.");
        }
        CreateInteriorMainBranchFittings(document, createdPipes, result);
        CreatePipeFittings(document, createdPipes, result);
        ConnectSprinklersToBranchPipes(document, createdSprinklers, createdPipes, result);
        transactionGroup.Assimilate();
    }

    private static bool TryResolveSprinklerTargetElevation(
        string mode,
        IReadOnlyList<Floor> floors,
        IReadOnlyList<Ceiling> ceilings,
        ReferencePlane? referencePlane,
        XYZ planPoint,
        Level level,
        double offset,
        out double targetElevation,
        out string failureReason)
    {
        failureReason = string.Empty;
        if (mode == ElevationModeLevel)
        {
            targetElevation = level.Elevation + offset;
            return true;
        }
        if (mode == ElevationModeFloor)
        {
            double? floorTop = floors
                .Select(floor => TryGetFloorTopAtPoint(floor, planPoint, out double z)
                    ? (double?)z
                    : null)
                .Where(value => value.HasValue && value.Value <= level.Elevation + 1.0)
                .OrderByDescending(value => value!.Value)
                .FirstOrDefault();
            if (!floorTop.HasValue)
            {
                targetElevation = 0;
                failureReason = "No selected/automatic Floor covers this sprinkler XY position below the target Level.";
                return false;
            }
            targetElevation = floorTop.Value + offset;
            return true;
        }
        if (mode == ElevationModeReferencePlane)
        {
            if (referencePlane is null)
            {
                targetElevation = 0;
                failureReason = "Reference Plane is unavailable.";
                return false;
            }
            Autodesk.Revit.DB.Plane plane = referencePlane.GetPlane();
            if (Math.Abs(plane.Normal.Z) < 0.01)
            {
                targetElevation = 0;
                failureReason = $"Reference Plane '{referencePlane.Name}' is vertical.";
                return false;
            }
            targetElevation = plane.Origin.Z -
                              (plane.Normal.X * (planPoint.X - plane.Origin.X) +
                               plane.Normal.Y * (planPoint.Y - plane.Origin.Y)) /
                              plane.Normal.Z + offset;
            return true;
        }

        double? ceilingUnderside = ceilings
            .Select(ceiling => TryGetCeilingUndersideAtPoint(ceiling, planPoint, out double z)
                ? (double?)z
                : null)
            .Where(value => value.HasValue && value.Value >= level.Elevation - 0.1)
            .OrderBy(value => value!.Value)
            .FirstOrDefault();
        if (!ceilingUnderside.HasValue)
        {
            targetElevation = 0;
            failureReason = "No selected/automatic Ceiling covers this sprinkler XY position.";
            return false;
        }
        targetElevation = ceilingUnderside.Value - offset;
        return true;
    }

    private static bool TryGetFloorTopAtPoint(Floor floor, XYZ planPoint, out double elevation)
    {
        double xyTolerance = UnitUtils.ConvertToInternalUnits(25, UnitTypeId.Millimeters);
        BoundingBoxXYZ? box = floor.get_BoundingBox(null);
        if (box is null ||
            planPoint.X < box.Min.X - xyTolerance || planPoint.X > box.Max.X + xyTolerance ||
            planPoint.Y < box.Min.Y - xyTolerance || planPoint.Y > box.Max.Y + xyTolerance)
        {
            elevation = 0;
            return false;
        }
        var topElevations = new List<double>();
        var options = new Options
        {
            ComputeReferences = false,
            IncludeNonVisibleObjects = true,
            DetailLevel = ViewDetailLevel.Fine
        };
        GeometryElement? geometry = floor.get_Geometry(options);
        if (geometry is not null)
            CollectHorizontalFaces(geometry, planPoint, topElevations, upward: true);
        elevation = topElevations.Count > 0 ? topElevations.Max() : box.Max.Z;
        return true;
    }

    private static bool TryGetCeilingUndersideAtPoint(Ceiling ceiling, XYZ planPoint, out double elevation)
    {
        double xyTolerance = UnitUtils.ConvertToInternalUnits(25, UnitTypeId.Millimeters);
        BoundingBoxXYZ? box = ceiling.get_BoundingBox(null);
        if (box is null ||
            planPoint.X < box.Min.X - xyTolerance || planPoint.X > box.Max.X + xyTolerance ||
            planPoint.Y < box.Min.Y - xyTolerance || planPoint.Y > box.Max.Y + xyTolerance)
        {
            elevation = 0;
            return false;
        }

        var undersideElevations = new List<double>();
        var options = new Options
        {
            ComputeReferences = false,
            IncludeNonVisibleObjects = true,
            DetailLevel = ViewDetailLevel.Fine
        };
        GeometryElement? geometry = ceiling.get_Geometry(options);
        if (geometry is not null)
            CollectHorizontalFaces(geometry, planPoint, undersideElevations, upward: false);
        if (undersideElevations.Count > 0)
        {
            elevation = undersideElevations.Min();
            return true;
        }

        // Bounding-box fallback supports simple ceilings whose analytical face
        // is unavailable at the current Revit detail level.
        elevation = box.Min.Z;
        return true;
    }

    private static void CollectHorizontalFaces(
        GeometryElement geometry,
        XYZ planPoint,
        ICollection<double> elevations,
        bool upward)
    {
        foreach (Autodesk.Revit.DB.GeometryObject geometryObject in geometry)
        {
            if (geometryObject is GeometryInstance instance)
            {
                CollectHorizontalFaces(instance.GetInstanceGeometry(), planPoint, elevations, upward);
                continue;
            }
            if (geometryObject is not Solid solid || solid.Volume <= 1e-9) continue;
            foreach (Face face in solid.Faces)
            {
                if (face is not PlanarFace planarFace ||
                    (upward ? planarFace.FaceNormal.Z < 0.9 : planarFace.FaceNormal.Z > -0.9))
                    continue;
                IntersectionResult? projection = planarFace.Project(
                    new XYZ(planPoint.X, planPoint.Y, planarFace.Origin.Z));
                if (projection is not null && planarFace.IsInside(projection.UVPoint))
                    elevations.Add(projection.XYZPoint.Z);
            }
        }
    }

    private static void NormalizeMainBranchJunctions(
        Document document,
        List<CreatedPipeEdge> pipes,
        SprinklerCreationResult result)
    {
        // A PDF tee symbol normally leaves a small graphical gap between the
        // main and branch centerlines. Revit requires all three connectors to
        // meet at one exact XYZ before NewTeeFitting can succeed.
        double snapTolerance = UnitUtils.ConvertToInternalUnits(250, UnitTypeId.Millimeters);
        double endpointTolerance = UnitUtils.ConvertToInternalUnits(125, UnitTypeId.Millimeters);
        double minimumPartLength = UnitUtils.ConvertToInternalUnits(35, UnitTypeId.Millimeters);
        CreatedPipeEdge[] branches = pipes
            .Where(item => item.VectorClass == PdfVectorClass.BranchPipe)
            .ToArray();

        foreach (CreatedPipeEdge branchEdge in branches)
        {
            Pipe? branch = document.GetElement(branchEdge.Pipe.Id) as Pipe;
            if (branch?.Location is not LocationCurve branchLocation || branchLocation.Curve is not Line branchLine)
                continue;
            foreach (int branchEndIndex in new[] { 0, 1 })
            {
                XYZ branchEnd = branchLine.GetEndPoint(branchEndIndex);
                (CreatedPipeEdge Edge, Pipe Pipe, XYZ Point, double Distance)? nearest = null;
                foreach (CreatedPipeEdge mainEdge in pipes
                             .Where(item => item.VectorClass == PdfVectorClass.MainPipe)
                             .ToArray())
                {
                    Pipe? main = document.GetElement(mainEdge.Pipe.Id) as Pipe;
                    if (main?.Location is not LocationCurve mainLocation || mainLocation.Curve is not Line mainLine)
                        continue;
                    if (!TryFindExtendedIntersection(
                            branchLine,
                            branchEndIndex,
                            mainLine,
                            snapTolerance,
                            out XYZ intersection,
                            out double intersectionDistance) ||
                        (nearest is not null && intersectionDistance >= nearest.Value.Distance))
                        continue;
                    nearest = (mainEdge, main, intersection, intersectionDistance);
                }
                if (nearest is null) continue;

                XYZ node = nearest.Value.Point;
                node = new XYZ(node.X, node.Y, branchEnd.Z);
                List<Pipe> mainEndsAtNode = pipes
                    .Where(item => item.VectorClass == PdfVectorClass.MainPipe)
                    .Select(item => document.GetElement(item.Pipe.Id) as Pipe)
                    .Where(item => item?.Location is LocationCurve)
                    .Cast<Pipe>()
                    .Where(item => PipeHasEndpointNear(item, node, snapTolerance))
                    .DistinctBy(item => item.Id)
                    .Take(2)
                    .ToList();

                if (mainEndsAtNode.Count < 2)
                {
                    Line exactMainLine = (Line)((LocationCurve)nearest.Value.Pipe.Location).Curve;
                    IntersectionResult? exactProjection = exactMainLine.Project(node);
                    if (exactProjection is null) continue;
                    node = exactProjection.XYZPoint;
                }

                bool branchMoved = MovePipeEndpoint(branch, branchEnd, node, minimumPartLength);
                if (!branchMoved) continue;

                if (mainEndsAtNode.Count >= 2)
                {
                    foreach (Pipe main in mainEndsAtNode)
                    {
                        XYZ closestEnd = ClosestPipeEndpoint(main, node);
                        _ = MovePipeEndpoint(main, closestEnd, node, minimumPartLength);
                    }
                }
                else
                {
                    Pipe main = nearest.Value.Pipe;
                    LocationCurve mainLocation = (LocationCurve)main.Location;
                    Line mainLine = (Line)mainLocation.Curve;
                    double distanceToStart = node.DistanceTo(mainLine.GetEndPoint(0));
                    double distanceToEnd = node.DistanceTo(mainLine.GetEndPoint(1));
                    if (distanceToStart <= minimumPartLength || distanceToEnd <= minimumPartLength)
                    {
                        XYZ closestEnd = distanceToStart <= distanceToEnd
                            ? mainLine.GetEndPoint(0)
                            : mainLine.GetEndPoint(1);
                        _ = MovePipeEndpoint(main, closestEnd, node, minimumPartLength);
                    }
                    // Do not break an interior main here. Fitting creation runs
                    // after this transaction and must own the break so a failed
                    // tee can roll the split back with no open pipe ends left.
                }
                document.Regenerate();
                branchLine = (Line)((LocationCurve)branch.Location).Curve;
            }
        }
    }

    private static void NormalizeNearbyPipeEndpoints(
        Document document,
        IReadOnlyList<CreatedPipeEdge> pipes)
    {
        // PDF fitting symbols leave small gaps although their centerlines are
        // logically connected. Revit does not create a fitting until every end
        // connector at a node has the exact same XYZ position.
        double snapTolerance = UnitUtils.ConvertToInternalUnits(10, UnitTypeId.Millimeters);
        double minimumPipeLength = UnitUtils.ConvertToInternalUnits(35, UnitTypeId.Millimeters);
        var endpoints = new List<PipeEndpointCandidate>();
        foreach (CreatedPipeEdge edge in pipes
                     .GroupBy(item => item.Pipe.Id)
                     .Select(group => group.First()))
        {
            if (document.GetElement(edge.Pipe.Id) is not Pipe pipe ||
                pipe.Location is not LocationCurve location ||
                location.Curve is not Line line)
                continue;
            XYZ start = line.GetEndPoint(0);
            XYZ end = line.GetEndPoint(1);
            endpoints.Add(new PipeEndpointCandidate(pipe, start, end, edge.VectorClass));
            endpoints.Add(new PipeEndpointCandidate(pipe, end, start, edge.VectorClass));
        }

        var clusters = new List<List<PipeEndpointCandidate>>();
        foreach (PipeEndpointCandidate endpoint in endpoints)
        {
            List<List<PipeEndpointCandidate>> matches = clusters
                .Where(cluster => cluster.Any(item => item.Point.DistanceTo(endpoint.Point) <= snapTolerance))
                .ToList();
            if (matches.Count == 0)
            {
                clusters.Add([endpoint]);
                continue;
            }
            List<PipeEndpointCandidate> targetCluster = matches[0];
            targetCluster.Add(endpoint);
            foreach (List<PipeEndpointCandidate> extraCluster in matches.Skip(1).ToArray())
            {
                targetCluster.AddRange(extraCluster);
                clusters.Remove(extraCluster);
            }
        }

        foreach (List<PipeEndpointCandidate> rawCluster in clusters)
        {
            PipeEndpointCandidate[] cluster = rawCluster
                .GroupBy(item => item.Pipe.Id)
                .Select(group => group.First())
                .ToArray();
            if (cluster.Length is < 2 or > 4) continue;
            if (cluster.Any(item => item.Point.DistanceTo(cluster[0].Point) > snapTolerance * 2.0))
                continue;
            if (!IsSupportedEndpointCluster(cluster)) continue;

            XYZ target = ResolveEndpointNodeTarget(cluster);
            if (cluster.Any(item => item.OtherPoint.DistanceTo(target) <= minimumPipeLength))
                continue;
            foreach (PipeEndpointCandidate endpoint in cluster)
                _ = MovePipeEndpoint(endpoint.Pipe, endpoint.Point, target, minimumPipeLength);
            document.Regenerate();
        }
    }

    private static void NormalizeCollinearPipeEndpointGaps(
        Document document,
        IReadOnlyList<CreatedPipeEdge> pipes)
    {
        double maximumGap = UnitUtils.ConvertToInternalUnits(200, UnitTypeId.Millimeters);
        double minimumPipeLength = UnitUtils.ConvertToInternalUnits(35, UnitTypeId.Millimeters);
        var endpoints = new List<PipeEndpointCandidate>();
        foreach (CreatedPipeEdge edge in pipes
                     .GroupBy(item => item.Pipe.Id)
                     .Select(group => group.First()))
        {
            if (document.GetElement(edge.Pipe.Id) is not Pipe pipe ||
                pipe.Location is not LocationCurve location ||
                location.Curve is not Line line)
                continue;
            XYZ start = line.GetEndPoint(0);
            XYZ end = line.GetEndPoint(1);
            endpoints.Add(new PipeEndpointCandidate(pipe, start, end, edge.VectorClass));
            endpoints.Add(new PipeEndpointCandidate(pipe, end, start, edge.VectorClass));
        }

        var used = new HashSet<ElementId>();
        for (int first = 0; first < endpoints.Count; first++)
        {
            PipeEndpointCandidate a = endpoints[first];
            if (used.Contains(a.Pipe.Id)) continue;
            PipeEndpointCandidate? best = null;
            double bestDistance = double.MaxValue;
            XYZ aDirection = (a.OtherPoint - a.Point).Normalize();
            for (int second = first + 1; second < endpoints.Count; second++)
            {
                PipeEndpointCandidate b = endpoints[second];
                if (a.Pipe.Id == b.Pipe.Id || used.Contains(b.Pipe.Id) ||
                    a.VectorClass != b.VectorClass)
                    continue;
                double distance = a.Point.DistanceTo(b.Point);
                if (distance < 1e-7 || distance > maximumGap || distance >= bestDistance)
                    continue;
                XYZ bDirection = (b.OtherPoint - b.Point).Normalize();
                if (aDirection.DotProduct(bDirection) > -0.999) continue;
                XYZ gapDirection = (b.Point - a.Point).Normalize();
                if (Math.Abs(aDirection.DotProduct(gapDirection)) < 0.999) continue;
                best = b;
                bestDistance = distance;
            }
            if (best is null) continue;
            XYZ target = (a.Point + best.Point) * 0.5;
            if (a.OtherPoint.DistanceTo(target) <= minimumPipeLength ||
                best.OtherPoint.DistanceTo(target) <= minimumPipeLength)
                continue;
            if (!MovePipeEndpoint(a.Pipe, a.Point, target, minimumPipeLength) ||
                !MovePipeEndpoint(best.Pipe, best.Point, target, minimumPipeLength))
                continue;
            used.Add(a.Pipe.Id);
            used.Add(best.Pipe.Id);
            document.Regenerate();
        }
    }

    private static bool IsSupportedEndpointCluster(IReadOnlyList<PipeEndpointCandidate> endpoints)
    {
        XYZ[] directions = endpoints
            .Select(item => (item.OtherPoint - item.Point).Normalize())
            .ToArray();
        if (directions.Length == 2)
        {
            double dot = directions[0].DotProduct(directions[1]);
            return dot <= -0.96 || Math.Abs(dot) <= 0.15;
        }
        if (directions.Length == 3)
        {
            for (int first = 0; first < 3; first++)
            for (int second = first + 1; second < 3; second++)
            {
                if (directions[first].DotProduct(directions[second]) > -0.96) continue;
                int branch = Enumerable.Range(0, 3)
                    .First(index => index != first && index != second);
                if (Math.Abs(directions[first].DotProduct(directions[branch])) <= 0.15 &&
                    Math.Abs(directions[second].DotProduct(directions[branch])) <= 0.15)
                    return true;
            }
            return false;
        }
        return directions.Length == 4;
    }

    private static XYZ ResolveEndpointNodeTarget(IReadOnlyList<PipeEndpointCandidate> endpoints)
    {
        XYZ average = new(
            endpoints.Average(item => item.Point.X),
            endpoints.Average(item => item.Point.Y),
            endpoints.Average(item => item.Point.Z));
        if (endpoints.Count != 2) return average;

        XYZ firstDirection = endpoints[0].OtherPoint - endpoints[0].Point;
        XYZ secondDirection = endpoints[1].OtherPoint - endpoints[1].Point;
        double firstDx = firstDirection.X;
        double firstDy = firstDirection.Y;
        double secondDx = secondDirection.X;
        double secondDy = secondDirection.Y;
        double cross = firstDx * secondDy - firstDy * secondDx;
        if (Math.Abs(cross) < 0.000001) return average;
        double qx = endpoints[1].Point.X - endpoints[0].Point.X;
        double qy = endpoints[1].Point.Y - endpoints[0].Point.Y;
        double firstParameter = (qx * secondDy - qy * secondDx) / cross;
        XYZ intersection = endpoints[0].Point + firstParameter * firstDirection;
        double maximumMove = endpoints.Max(item => item.Point.DistanceTo(average)) * 2.5;
        if (endpoints.Any(item => item.Point.DistanceTo(intersection) > Math.Max(maximumMove, 0.001)))
            return average;
        return new XYZ(intersection.X, intersection.Y, average.Z);
    }

    private static void CreateInteriorMainBranchFittings(
        Document document,
        List<CreatedPipeEdge> pipes,
        SprinklerCreationResult result)
    {
        double onCurveTolerance = UnitUtils.ConvertToInternalUnits(12, UnitTypeId.Millimeters);
        double minimumEndClearance = UnitUtils.ConvertToInternalUnits(35, UnitTypeId.Millimeters);
        CreatedPipeEdge[] sourceEdges = pipes.ToArray();
        var processedEnds = new HashSet<(long PipeId, int EndIndex)>();

        foreach (CreatedPipeEdge sourceEdge in sourceEdges)
        {
            ElementId sourcePipeId = sourceEdge.Pipe.Id;
            Pipe? sourcePipe = document.GetElement(sourcePipeId) as Pipe;
            if (sourcePipe?.Location is not LocationCurve sourceLocation ||
                sourceLocation.Curve is not Line sourceLine)
                continue;

            foreach (int sourceEndIndex in new[] { 0, 1 })
            {
                if (!processedEnds.Add((sourcePipeId.CompatValue(), sourceEndIndex))) continue;
                sourcePipe = document.GetElement(sourcePipeId) as Pipe;
                if (sourcePipe?.Location is not LocationCurve currentSourceLocation ||
                    currentSourceLocation.Curve is not Line currentSourceLine)
                    continue;
                XYZ sourcePoint = currentSourceLine.GetEndPoint(sourceEndIndex);
                XYZ sourceFarPoint = currentSourceLine.GetEndPoint(1 - sourceEndIndex);
                XYZ sourceDirection = (sourceFarPoint - sourcePoint).Normalize();
                Connector sourceConnector = FindPipeEndConnector(sourcePipe, sourcePoint);
                if (sourceConnector.IsConnected) continue;

                (CreatedPipeEdge Edge, Pipe Pipe, XYZ Point, double Distance)? nearest = null;
                foreach (CreatedPipeEdge targetEdge in pipes.ToArray())
                {
                    if (targetEdge.Pipe.Id == sourcePipeId) continue;
                    Pipe? targetPipe = document.GetElement(targetEdge.Pipe.Id) as Pipe;
                    if (targetPipe?.Location is not LocationCurve targetLocation ||
                        targetLocation.Curve is not Line targetLine)
                        continue;
                    XYZ targetDirection = (targetLine.GetEndPoint(1) - targetLine.GetEndPoint(0)).Normalize();
                    if (Math.Abs(sourceDirection.DotProduct(targetDirection)) > 0.20) continue;
                    IntersectionResult? projection = targetLine.Project(sourcePoint);
                    if (projection is null) continue;
                    XYZ point = projection.XYZPoint;
                    double distance = point.DistanceTo(sourcePoint);
                    if (distance > onCurveTolerance ||
                        point.DistanceTo(targetLine.GetEndPoint(0)) <= minimumEndClearance ||
                        point.DistanceTo(targetLine.GetEndPoint(1)) <= minimumEndClearance ||
                        (nearest is not null && distance >= nearest.Value.Distance))
                        continue;
                    nearest = (targetEdge, targetPipe, point, distance);
                }
                if (nearest is null) continue;

                using var fittingGroup = new TransactionGroup(document, "Create interior pipe tee safely");
                try
                {
                    fittingGroup.Start();
                    ElementId splitId;
                    using (var breakTransaction = new Transaction(document, "Align endpoint and break pipe for tee"))
                    {
                        breakTransaction.Start();
                        ConfigureSilentFailureHandling(breakTransaction);
                        Pipe currentSource = document.GetElement(sourcePipeId) as Pipe
                            ?? throw new InvalidOperationException("The source pipe is unavailable.");
                        if (sourcePoint.DistanceTo(nearest.Value.Point) > 1e-7 &&
                            !MovePipeEndpoint(currentSource, sourcePoint, nearest.Value.Point, minimumEndClearance))
                            throw new InvalidOperationException("The source endpoint cannot be aligned safely to the tee node.");
                        document.Regenerate();
                        splitId = PlumbingUtils.BreakCurve(document, nearest.Value.Pipe.Id, nearest.Value.Point);
                        if (splitId == ElementId.InvalidElementId ||
                            breakTransaction.Commit() != TransactionStatus.Committed)
                            throw new InvalidOperationException("Revit could not split the through pipe at the tee node.");
                    }

                    Pipe runA = document.GetElement(nearest.Value.Pipe.Id) as Pipe
                        ?? throw new InvalidOperationException("The first split through pipe is unavailable.");
                    Pipe runB = document.GetElement(splitId) as Pipe
                        ?? throw new InvalidOperationException("The second split through pipe is unavailable.");
                    Pipe currentSourcePipe = document.GetElement(sourcePipeId) as Pipe
                        ?? throw new InvalidOperationException("The source pipe is unavailable after splitting the through pipe.");
                    XYZ node = nearest.Value.Point;
                    PipeNodeConnection[] connections =
                    [
                        CreatePipeNodeConnection(runA, node, nearest.Value.Edge.VectorClass),
                        CreatePipeNodeConnection(runB, node, nearest.Value.Edge.VectorClass),
                        CreatePipeNodeConnection(currentSourcePipe, node, sourceEdge.VectorClass)
                    ];

                    if (!TryCreatePreferredNodeFitting(document, connections, result, out string fittingFailure))
                        throw new InvalidOperationException(fittingFailure);
                    if (fittingGroup.Assimilate() != TransactionStatus.Committed)
                        throw new InvalidOperationException("Revit could not commit the main split and tee as one operation.");

                    if (document.GetElement(splitId) is Pipe committedSplit &&
                        committedSplit.Location is LocationCurve splitLocation &&
                        splitLocation.Curve is Line splitLine)
                    {
                        pipes.Add(new CreatedPipeEdge(
                            committedSplit,
                            splitLine.GetEndPoint(0),
                            splitLine.GetEndPoint(1),
                            nearest.Value.Edge.VectorClass));
                        if (nearest.Value.Edge.VectorClass == PdfVectorClass.MainPipe)
                            result.MainPipes++;
                        else
                            result.BranchPipes++;
                    }
                    result.Fittings++; // Tee; reducer(s) are counted by the fallback helper.
                }
                catch (Exception exception)
                {
                    try { fittingGroup.RollBack(); } catch { }
                    result.FittingsSkipped++;
                    result.RecordFittingFailure(
                        $"The through pipe was not left broken because its tee failed: {exception.Message}");
                }
            }
        }
    }

    private static PipeNodeConnection CreatePipeNodeConnection(
        Pipe pipe,
        XYZ node,
        PdfVectorClass vectorClass)
    {
        if (pipe.Location is not LocationCurve location || location.Curve is not Line line)
            throw new InvalidOperationException("A split pipe has no straight centerline.");
        XYZ first = line.GetEndPoint(0);
        XYZ second = line.GetEndPoint(1);
        XYZ farPoint = first.DistanceTo(node) >= second.DistanceTo(node) ? first : second;
        XYZ direction = farPoint - node;
        if (direction.GetLength() < 1e-9)
            throw new InvalidOperationException("A split pipe segment is too short for a fitting.");
        return new PipeNodeConnection(
            pipe.Id,
            FindPipeEndConnector(pipe, node),
            node,
            direction.Normalize(),
            vectorClass);
    }

    private static (XYZ Point, double Parameter, double Distance) ProjectPointToLineXY(XYZ point, Line line)
    {
        XYZ start = line.GetEndPoint(0);
        XYZ end = line.GetEndPoint(1);
        double dx = end.X - start.X;
        double dy = end.Y - start.Y;
        double lengthSquared = dx * dx + dy * dy;
        double parameter = lengthSquared < 1e-12
            ? 0
            : ((point.X - start.X) * dx + (point.Y - start.Y) * dy) / lengthSquared;
        parameter = PortableMath.Clamp(parameter, 0, 1);
        XYZ projected = new(
            start.X + parameter * dx,
            start.Y + parameter * dy,
            start.Z + parameter * (end.Z - start.Z));
        double distance = Math.Sqrt(
            Math.Pow(projected.X - point.X, 2) +
            Math.Pow(projected.Y - point.Y, 2));
        return (projected, parameter, distance);
    }

    private static (XYZ Point, double Parameter, double Distance) ProjectPointToInfiniteLineXY(XYZ point, Line line)
    {
        XYZ start = line.GetEndPoint(0);
        XYZ end = line.GetEndPoint(1);
        double dx = end.X - start.X;
        double dy = end.Y - start.Y;
        double lengthSquared = dx * dx + dy * dy;
        double parameter = lengthSquared < 1e-12
            ? 0
            : ((point.X - start.X) * dx + (point.Y - start.Y) * dy) / lengthSquared;
        XYZ projected = new(
            start.X + parameter * dx,
            start.Y + parameter * dy,
            start.Z + parameter * (end.Z - start.Z));
        double distance = Math.Sqrt(
            Math.Pow(projected.X - point.X, 2) +
            Math.Pow(projected.Y - point.Y, 2));
        return (projected, parameter, distance);
    }

    private static Pipe? FindCollinearBranchContinuation(
        Document document,
        IReadOnlyList<ElementId> branchPipeIds,
        Pipe selectedPipe,
        XYZ node,
        double tolerance)
    {
        if (selectedPipe.Location is not LocationCurve selectedLocation ||
            selectedLocation.Curve is not Line selectedLine)
            return null;
        XYZ selectedDirection = (selectedLine.GetEndPoint(1) - selectedLine.GetEndPoint(0)).Normalize();
        Pipe? best = null;
        double bestDistance = double.MaxValue;
        foreach (ElementId pipeId in branchPipeIds)
        {
            if (pipeId == selectedPipe.Id || document.GetElement(pipeId) is not Pipe candidate ||
                candidate.Location is not LocationCurve candidateLocation ||
                candidateLocation.Curve is not Line candidateLine)
                continue;
            XYZ direction = (candidateLine.GetEndPoint(1) - candidateLine.GetEndPoint(0)).Normalize();
            if (Math.Abs(selectedDirection.DotProduct(direction)) < 0.96) continue;
            XYZ endpoint = ClosestPipeEndpoint(candidate, node);
            double distance = endpoint.DistanceTo(node);
            if (distance > tolerance || distance >= bestDistance) continue;
            Connector connector = FindPipeEndConnector(candidate, endpoint);
            if (connector.IsConnected) continue;
            best = candidate;
            bestDistance = distance;
        }
        return best;
    }

    private static bool TryFindExtendedIntersection(
        Line branchLine,
        int branchEndIndex,
        Line mainLine,
        double tolerance,
        out XYZ intersection,
        out double distanceFromBranchEnd)
    {
        XYZ a = branchLine.GetEndPoint(0);
        XYZ b = branchLine.GetEndPoint(1);
        XYZ c = mainLine.GetEndPoint(0);
        XYZ d = mainLine.GetEndPoint(1);
        double adx = b.X - a.X;
        double ady = b.Y - a.Y;
        double mdx = d.X - c.X;
        double mdy = d.Y - c.Y;
        double cross = adx * mdy - ady * mdx;
        if (Math.Abs(cross) < 1e-10)
        {
            intersection = XYZ.Zero;
            distanceFromBranchEnd = double.MaxValue;
            return false;
        }
        double qx = c.X - a.X;
        double qy = c.Y - a.Y;
        double branchParameter = (qx * mdy - qy * mdx) / cross;
        double mainParameter = (qx * ady - qy * adx) / cross;
        double branchLength = Math.Sqrt(adx * adx + ady * ady);
        double mainLength = Math.Sqrt(mdx * mdx + mdy * mdy);
        if (branchLength < 1e-8 || mainLength < 1e-8)
        {
            intersection = XYZ.Zero;
            distanceFromBranchEnd = double.MaxValue;
            return false;
        }
        double branchEndParameter = branchEndIndex == 0 ? 0 : 1;
        if (Math.Abs(branchParameter - branchEndParameter) * branchLength > tolerance ||
            mainParameter < -tolerance / mainLength ||
            mainParameter > 1 + tolerance / mainLength)
        {
            intersection = XYZ.Zero;
            distanceFromBranchEnd = double.MaxValue;
            return false;
        }
        intersection = new XYZ(
            a.X + branchParameter * adx,
            a.Y + branchParameter * ady,
            a.Z + branchParameter * (b.Z - a.Z));
        XYZ branchEnd = branchLine.GetEndPoint(branchEndIndex);
        distanceFromBranchEnd = branchEnd.DistanceTo(intersection);
        return true;
    }

    private static bool PipeHasEndpointNear(Pipe pipe, XYZ point, double tolerance)
    {
        if (pipe.Location is not LocationCurve location || location.Curve is not Line line) return false;
        return line.GetEndPoint(0).DistanceTo(point) <= tolerance ||
               line.GetEndPoint(1).DistanceTo(point) <= tolerance;
    }

    private static XYZ ClosestPipeEndpoint(Pipe pipe, XYZ point)
    {
        Line line = (Line)((LocationCurve)pipe.Location).Curve;
        XYZ start = line.GetEndPoint(0);
        XYZ end = line.GetEndPoint(1);
        return start.DistanceTo(point) <= end.DistanceTo(point) ? start : end;
    }

    private static bool MovePipeEndpoint(Pipe pipe, XYZ sourceEnd, XYZ target, double minimumLength)
    {
        if (pipe.Location is not LocationCurve location || location.Curve is not Line line) return false;
        XYZ start = line.GetEndPoint(0);
        XYZ end = line.GetEndPoint(1);
        bool moveStart = start.DistanceTo(sourceEnd) <= end.DistanceTo(sourceEnd);
        XYZ fixedEnd = moveStart ? end : start;
        if (fixedEnd.DistanceTo(target) <= minimumLength) return false;
        location.Curve = moveStart
            ? Line.CreateBound(target, fixedEnd)
            : Line.CreateBound(fixedEnd, target);
        return true;
    }

    private static void ConnectSprinklersToBranchPipes(
        Document document,
        IReadOnlyList<FamilyInstance> sprinklers,
        IReadOnlyList<CreatedPipeEdge> pipes,
        SprinklerCreationResult result)
    {
        double connectionTolerance = UnitUtils.ConvertToInternalUnits(250, UnitTypeId.Millimeters);
        double endpointTolerance = UnitUtils.ConvertToInternalUnits(35, UnitTypeId.Millimeters);
        double minimumPipeLength = UnitUtils.ConvertToInternalUnits(35, UnitTypeId.Millimeters);
        var branchPipeIds = pipes
            .Where(item => item.VectorClass == PdfVectorClass.BranchPipe)
            .Select(item => item.Pipe.Id)
            .Distinct()
            .ToList();
        foreach (FamilyInstance sprinkler in sprinklers)
        {
            Connector? sprinklerConnector = sprinkler.MEPModel?.ConnectorManager?.Connectors
                .Cast<Connector>()
                .Where(connector => !connector.IsConnected)
                .OrderBy(connector => connector.Origin.Z)
                .FirstOrDefault();
            if (sprinklerConnector is null)
            {
                result.SprinklerConnectionsSkipped++;
                continue;
            }

            ElementId nearestPipeId = ElementId.InvalidElementId;
            XYZ? nearestPoint = null;
            double nearestDistance = double.MaxValue;
            foreach (ElementId pipeId in branchPipeIds.ToArray())
            {
                if (document.GetElement(pipeId) is not Pipe pipe ||
                    pipe.Location is not LocationCurve location || location.Curve is not Line line)
                    continue;
                (XYZ Point, double Parameter, double Distance) projection =
                    ProjectPointToLineXY(sprinklerConnector.Origin, line);
                if (projection.Distance > connectionTolerance || projection.Distance >= nearestDistance)
                    continue;
                nearestPipeId = pipeId;
                nearestPoint = projection.Point;
                nearestDistance = projection.Distance;
            }
            if (nearestPipeId == ElementId.InvalidElementId || nearestPoint is null)
            {
                if (TryConnectSprinklerUsingAlignedEndpointArmover(
                        document,
                        sprinkler.Id,
                        branchPipeIds,
                        connectionTolerance,
                        minimumPipeLength,
                        result,
                        out _))
                    continue;
                result.SprinklerConnectionsSkipped++;
                continue;
            }

            using var transaction = new Transaction(document, "Connect branch pipe to sprinkler");
            try
            {
                transaction.Start();
                ConfigureSilentFailureHandling(transaction);
                Pipe branchPipe = document.GetElement(nearestPipeId) as Pipe
                    ?? throw new InvalidOperationException("The selected branch pipe is no longer available.");
                Line branchLine = ((LocationCurve)branchPipe.Location).Curve as Line
                    ?? throw new InvalidOperationException("The selected branch pipe has no straight centerline.");
                (XYZ Point, double Parameter, double Distance) currentProjection =
                    ProjectPointToLineXY(sprinklerConnector.Origin, branchLine);
                XYZ branchPoint = new(currentProjection.Point.X, currentProjection.Point.Y, branchLine.GetEndPoint(0).Z);
                (XYZ Point, double Parameter, double Distance) extendedProjection =
                    ProjectPointToInfiniteLineXY(sprinklerConnector.Origin, branchLine);
                XYZ extendedPoint = new(
                    extendedProjection.Point.X,
                    extendedProjection.Point.Y,
                    branchLine.GetEndPoint(0).Z);
                double selectedEndDistance = Math.Min(
                    extendedPoint.DistanceTo(branchLine.GetEndPoint(0)),
                    extendedPoint.DistanceTo(branchLine.GetEndPoint(1)));
                Pipe? continuationPipe = selectedEndDistance <= connectionTolerance
                    ? FindCollinearBranchContinuation(
                        document,
                        branchPipeIds,
                        branchPipe,
                        extendedPoint,
                        connectionTolerance)
                    : null;
                if (continuationPipe is not null)
                    branchPoint = extendedPoint;
                else
                {
                    IntersectionResult? exactProjection = branchLine.Project(branchPoint);
                    if (exactProjection is null)
                        throw new InvalidOperationException("The sprinkler point cannot be projected onto the branch pipe.");
                    branchPoint = exactProjection.XYZPoint;
                }

                // Move a slightly offset PDF head onto the exact branch centerline.
                XYZ planarMove = new(
                    branchPoint.X - sprinklerConnector.Origin.X,
                    branchPoint.Y - sprinklerConnector.Origin.Y,
                    0);
                if (planarMove.GetLength() > 1e-7)
                {
                    ElementTransformUtils.MoveElement(document, sprinkler.Id, planarMove);
                    document.Regenerate();
                }
                sprinklerConnector = sprinkler.MEPModel?.ConnectorManager?.Connectors
                    .Cast<Connector>()
                    .Where(connector => !connector.IsConnected)
                    .OrderBy(connector => connector.Origin.DistanceTo(branchPoint))
                    .FirstOrDefault();
                if (sprinklerConnector is null)
                    throw new InvalidOperationException("A free sprinkler connector is unavailable.");

                double distanceToStart = branchPoint.DistanceTo(branchLine.GetEndPoint(0));
                double distanceToEnd = branchPoint.DistanceTo(branchLine.GetEndPoint(1));
                bool interiorConnection = continuationPipe is not null ||
                                          (distanceToStart > endpointTolerance && distanceToEnd > endpointTolerance);
                Connector pipeConnector;
                Connector? oppositeRunConnector = null;
                ElementId splitPipeId = ElementId.InvalidElementId;
                if (continuationPipe is not null)
                {
                    XYZ branchEnd = ClosestPipeEndpoint(branchPipe, branchPoint);
                    XYZ continuationEnd = ClosestPipeEndpoint(continuationPipe, branchPoint);
                    if (!MovePipeEndpoint(branchPipe, branchEnd, branchPoint, minimumPipeLength) ||
                        !MovePipeEndpoint(continuationPipe, continuationEnd, branchPoint, minimumPipeLength))
                        throw new InvalidOperationException("The two branch pieces are too short to form a sprinkler tee.");
                    document.Regenerate();
                    pipeConnector = FindPipeEndConnector(branchPipe, branchPoint);
                    oppositeRunConnector = FindPipeEndConnector(continuationPipe, branchPoint);
                }
                else if (interiorConnection)
                {
                    branchLine = ((LocationCurve)branchPipe.Location).Curve as Line
                        ?? throw new InvalidOperationException("The branch centerline changed before it could be split.");
                    IntersectionResult? breakProjection = branchLine.Project(branchPoint);
                    if (breakProjection is null)
                        throw new InvalidOperationException("The sprinkler tee point cannot be projected onto the branch curve.");
                    branchPoint = breakProjection.XYZPoint;
                    splitPipeId = PlumbingUtils.BreakCurve(document, branchPipe.Id, branchPoint);
                    if (splitPipeId == ElementId.InvalidElementId || document.GetElement(splitPipeId) is not Pipe splitPipe)
                        throw new InvalidOperationException("Revit could not split the branch at the sprinkler centerline.");
                    document.Regenerate();
                    pipeConnector = FindPipeEndConnector(branchPipe, branchPoint);
                    oppositeRunConnector = FindPipeEndConnector(splitPipe, branchPoint);
                }
                else
                {
                    pipeConnector = FindPipeEndConnector(branchPipe, branchPoint);
                    branchPoint = pipeConnector.Origin;
                }
                if (pipeConnector.IsConnected || oppositeRunConnector?.IsConnected == true)
                    throw new InvalidOperationException("The branch connection point is already occupied.");

                double verticalDifference = Math.Abs(branchPoint.Z - sprinklerConnector.Origin.Z);
                bool createdVertical = false;
                if (verticalDifference > minimumPipeLength)
                {
                    ElementId systemTypeId = branchPipe
                        .get_Parameter(BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM)?.AsElementId()
                        ?? ElementId.InvalidElementId;
                    if (systemTypeId == ElementId.InvalidElementId)
                        throw new InvalidOperationException("Branch pipe system type is unavailable for the sprinkler drop/rise.");
                    XYZ headPoint = new(branchPoint.X, branchPoint.Y, sprinklerConnector.Origin.Z);
                    Pipe verticalPipe = Pipe.Create(
                        document,
                        systemTypeId,
                        branchPipe.GetTypeId(),
                        branchPipe.ReferenceLevel.Id,
                        branchPoint,
                        headPoint);
                    Parameter? verticalDiameter = verticalPipe.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM);
                    double branchDiameter = pipeConnector.Radius * 2.0;
                    if (verticalDiameter is null || verticalDiameter.IsReadOnly || !verticalDiameter.Set(branchDiameter))
                        throw new InvalidOperationException("Could not size the sprinkler drop/rise pipe.");
                    document.Regenerate();
                    Connector verticalAtBranch = FindPipeEndConnector(verticalPipe, branchPoint);
                    Connector verticalAtHead = FindPipeEndConnector(verticalPipe, headPoint);
                    if (interiorConnection && oppositeRunConnector is not null)
                    {
                        _ = CreateVerifiedTeeFitting(
                            document,
                            pipeConnector,
                            oppositeRunConnector,
                            verticalAtBranch);
                    }
                    else
                    {
                        _ = document.Create.NewElbowFitting(pipeConnector, verticalAtBranch);
                    }
                    verticalAtHead.ConnectTo(sprinklerConnector);
                    createdVertical = true;
                }
                else if (interiorConnection && oppositeRunConnector is not null)
                {
                    _ = CreateVerifiedTeeFitting(
                        document,
                        pipeConnector,
                        oppositeRunConnector,
                        sprinklerConnector);
                }
                else
                {
                    pipeConnector.ConnectTo(sprinklerConnector);
                }
                if (transaction.Commit() != TransactionStatus.Committed)
                    throw new InvalidOperationException("Revit rejected the sprinkler connection.");
                if (createdVertical)
                {
                    result.BranchPipes++;
                    result.Fittings++;
                    result.SprinklerDrops++;
                }
                if (splitPipeId != ElementId.InvalidElementId)
                {
                    result.BranchPipes++;
                }
                if (interiorConnection && !createdVertical) result.Fittings++;
                if (splitPipeId != ElementId.InvalidElementId)
                    branchPipeIds.Add(splitPipeId);
                result.SprinklerConnections++;
            }
            catch (Exception exception)
            {
                try { transaction.RollBack(); } catch { }
                if (TryConnectSprinklerUsingAlignedEndpointArmover(
                        document,
                        sprinkler.Id,
                        branchPipeIds,
                        connectionTolerance,
                        minimumPipeLength,
                        result,
                        out string armoverFailure))
                    continue;
                if (TryConnectSprinklerUsingEndpointFallback(
                        document,
                        sprinkler.Id,
                        branchPipeIds,
                        connectionTolerance,
                        minimumPipeLength,
                        result,
                        out string fallbackFailure))
                    continue;
                result.SprinklerConnectionsSkipped++;
                result.RecordFittingFailure(
                    $"Sprinkler connection: {exception.Message}; aligned armover: {armoverFailure}; " +
                    $"endpoint fallback: {fallbackFailure}");
            }
        }
    }

    private static bool TryConnectSprinklerUsingAlignedEndpointArmover(
        Document document,
        ElementId sprinklerId,
        List<ElementId> branchPipeIds,
        double alignmentTolerance,
        double minimumPipeLength,
        SprinklerCreationResult result,
        out string failureReason)
    {
        failureReason = string.Empty;
        using var transaction = new Transaction(document, "Connect aligned sprinkler armover");
        try
        {
            transaction.Start();
            ConfigureSilentFailureHandling(transaction);
            FamilyInstance sprinkler = document.GetElement(sprinklerId) as FamilyInstance
                ?? throw new InvalidOperationException("The sprinkler is no longer available.");
            Connector sprinklerConnector = sprinkler.MEPModel?.ConnectorManager?.Connectors
                .Cast<Connector>()
                .Where(connector => !connector.IsConnected)
                .OrderBy(connector => connector.Origin.Z)
                .FirstOrDefault()
                ?? throw new InvalidOperationException("The sprinkler has no free connector.");

            Pipe? selectedPipe = null;
            XYZ? selectedJoinPoint = null;
            XYZ? armoverEnd = null;
            bool selectedInterior = false;
            double nearestDistance = double.MaxValue;
            double maximumSafetyLength = UnitUtils.ConvertToInternalUnits(12000, UnitTypeId.Millimeters);
            foreach (ElementId pipeId in branchPipeIds.Distinct().ToArray())
            {
                if (document.GetElement(pipeId) is not Pipe pipe ||
                    pipe.Location is not LocationCurve location || location.Curve is not Line line)
                    continue;
                double planarLineLength = Math.Sqrt(
                    Math.Pow(line.GetEndPoint(1).X - line.GetEndPoint(0).X, 2) +
                    Math.Pow(line.GetEndPoint(1).Y - line.GetEndPoint(0).Y, 2));
                if (planarLineLength <= minimumPipeLength) continue;
                (XYZ Point, double Parameter, double Distance) projection =
                    ProjectPointToLineXY(sprinklerConnector.Origin, line);
                XYZ joinPoint = new(projection.Point.X, projection.Point.Y, line.GetEndPoint(0).Z);
                double dx = Math.Abs(joinPoint.X - sprinklerConnector.Origin.X);
                double dy = Math.Abs(joinPoint.Y - sprinklerConnector.Origin.Y);
                bool sameColumn = dx <= alignmentTolerance;
                bool sameRow = dy <= alignmentTolerance;
                if (!sameColumn && !sameRow) continue;
                XYZ target = sameRow && (!sameColumn || dx >= dy)
                    ? new XYZ(sprinklerConnector.Origin.X, joinPoint.Y, joinPoint.Z)
                    : new XYZ(joinPoint.X, sprinklerConnector.Origin.Y, joinPoint.Z);
                double distance = joinPoint.DistanceTo(target);
                if (distance <= minimumPipeLength || distance > maximumSafetyLength || distance >= nearestDistance)
                    continue;
                double distanceToStart = joinPoint.DistanceTo(line.GetEndPoint(0));
                double distanceToEnd = joinPoint.DistanceTo(line.GetEndPoint(1));
                bool interior = distanceToStart > minimumPipeLength && distanceToEnd > minimumPipeLength;
                if (!interior)
                {
                    XYZ endpoint = distanceToStart <= distanceToEnd
                        ? line.GetEndPoint(0)
                        : line.GetEndPoint(1);
                    Connector endpointConnector = FindPipeEndConnector(pipe, endpoint);
                    if (endpointConnector.IsConnected) continue;
                    joinPoint = endpoint;
                    target = sameRow
                        ? new XYZ(sprinklerConnector.Origin.X, endpoint.Y, endpoint.Z)
                        : new XYZ(endpoint.X, sprinklerConnector.Origin.Y, endpoint.Z);
                    XYZ inward = ClosestOtherPipeEndpoint(line, endpoint) - endpoint;
                    XYZ outward = target - endpoint;
                    if (outward.GetLength() <= minimumPipeLength ||
                        inward.Normalize().DotProduct(outward.Normalize()) > 0.96)
                        continue;
                    distance = outward.GetLength();
                }
                selectedPipe = pipe;
                selectedJoinPoint = joinPoint;
                armoverEnd = target;
                selectedInterior = interior;
                nearestDistance = distance;
            }
            if (selectedPipe is null || selectedJoinPoint is null || armoverEnd is null)
                throw new InvalidOperationException("No branch run can form a perpendicular Tee/armover to this sprinkler.");

            ElementId systemTypeId = selectedPipe
                .get_Parameter(BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM)?.AsElementId()
                ?? ElementId.InvalidElementId;
            if (systemTypeId == ElementId.InvalidElementId)
                throw new InvalidOperationException("The branch system type is unavailable for an aligned armover.");
            Connector sourceConnector;
            Connector? oppositeRunConnector = null;
            ElementId splitPipeId = ElementId.InvalidElementId;
            if (selectedInterior)
            {
                splitPipeId = PlumbingUtils.BreakCurve(document, selectedPipe.Id, selectedJoinPoint);
                if (splitPipeId == ElementId.InvalidElementId || document.GetElement(splitPipeId) is not Pipe splitPipe)
                    throw new InvalidOperationException("Revit could not split the branch for the sprinkler Tee.");
                document.Regenerate();
                sourceConnector = FindPipeEndConnector(selectedPipe, selectedJoinPoint);
                oppositeRunConnector = FindPipeEndConnector(splitPipe, selectedJoinPoint);
            }
            else
            {
                sourceConnector = FindPipeEndConnector(selectedPipe, selectedJoinPoint);
            }
            Pipe armover = Pipe.Create(
                document,
                systemTypeId,
                selectedPipe.GetTypeId(),
                selectedPipe.ReferenceLevel.Id,
                selectedJoinPoint,
                armoverEnd);
            Parameter? armoverDiameter = armover.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM);
            if (armoverDiameter is null || armoverDiameter.IsReadOnly ||
                !armoverDiameter.Set(sourceConnector.Radius * 2.0))
                throw new InvalidOperationException("The aligned armover could not be sized.");
            document.Regenerate();

            Connector armoverAtSource = FindPipeEndConnector(armover, selectedJoinPoint);
            if (selectedInterior && oppositeRunConnector is not null)
                _ = CreateVerifiedTeeFitting(
                    document,
                    sourceConnector,
                    oppositeRunConnector,
                    armoverAtSource);
            else
            {
                XYZ sourceDirection = ClosestOtherPipeEndpoint(
                    (Line)((LocationCurve)selectedPipe.Location).Curve,
                    selectedJoinPoint) - selectedJoinPoint;
                XYZ armoverDirection = armoverEnd - selectedJoinPoint;
                double sourceDot = sourceDirection.Normalize().DotProduct(armoverDirection.Normalize());
                if (sourceDot < -0.96)
                    _ = document.Create.NewUnionFitting(sourceConnector, armoverAtSource);
                else
                    _ = document.Create.NewElbowFitting(sourceConnector, armoverAtSource);
            }

            XYZ planarMove = new(
                armoverEnd.X - sprinklerConnector.Origin.X,
                armoverEnd.Y - sprinklerConnector.Origin.Y,
                0);
            if (planarMove.GetLength() > 1e-7)
            {
                ElementTransformUtils.MoveElement(document, sprinkler.Id, planarMove);
                document.Regenerate();
            }
            sprinklerConnector = sprinkler.MEPModel?.ConnectorManager?.Connectors
                .Cast<Connector>()
                .Where(connector => !connector.IsConnected)
                .OrderBy(connector => connector.Origin.DistanceTo(armoverEnd))
                .FirstOrDefault()
                ?? throw new InvalidOperationException("The aligned sprinkler connector is unavailable.");

            Connector armoverAtHead = FindPipeEndConnector(armover, armoverEnd);
            bool createdVertical = false;
            double verticalDifference = Math.Abs(armoverEnd.Z - sprinklerConnector.Origin.Z);
            if (verticalDifference > minimumPipeLength)
            {
                XYZ headPoint = new(armoverEnd.X, armoverEnd.Y, sprinklerConnector.Origin.Z);
                Pipe verticalPipe = Pipe.Create(
                    document,
                    systemTypeId,
                    selectedPipe.GetTypeId(),
                    selectedPipe.ReferenceLevel.Id,
                    armoverEnd,
                    headPoint);
                Parameter? verticalDiameter = verticalPipe.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM);
                if (verticalDiameter is null || verticalDiameter.IsReadOnly ||
                    !verticalDiameter.Set(sourceConnector.Radius * 2.0))
                    throw new InvalidOperationException("The aligned sprinkler rise/drop could not be sized.");
                document.Regenerate();
                Connector verticalAtArmover = FindPipeEndConnector(verticalPipe, armoverEnd);
                Connector verticalAtHead = FindPipeEndConnector(verticalPipe, headPoint);
                _ = document.Create.NewElbowFitting(armoverAtHead, verticalAtArmover);
                verticalAtHead.ConnectTo(sprinklerConnector);
                createdVertical = true;
            }
            else
            {
                armoverAtHead.ConnectTo(sprinklerConnector);
            }

            if (transaction.Commit() != TransactionStatus.Committed)
                throw new InvalidOperationException("Revit rejected the aligned sprinkler armover.");
            branchPipeIds.Add(armover.Id);
            if (splitPipeId != ElementId.InvalidElementId)
                branchPipeIds.Add(splitPipeId);
            result.BranchPipes += (createdVertical ? 2 : 1) +
                                  (splitPipeId != ElementId.InvalidElementId ? 1 : 0);
            result.Fittings += createdVertical ? 2 : 1;
            if (createdVertical) result.SprinklerDrops++;
            result.SprinklerConnections++;
            return true;
        }
        catch (Exception exception)
        {
            try { transaction.RollBack(); } catch { }
            failureReason = exception.Message;
            return false;
        }
    }

    private static XYZ ClosestOtherPipeEndpoint(Line line, XYZ endpoint)
    {
        XYZ first = line.GetEndPoint(0);
        XYZ second = line.GetEndPoint(1);
        return first.DistanceTo(endpoint) <= second.DistanceTo(endpoint) ? second : first;
    }

    private static bool TryConnectSprinklerUsingEndpointFallback(
        Document document,
        ElementId sprinklerId,
        IReadOnlyList<ElementId> branchPipeIds,
        double connectionTolerance,
        double minimumPipeLength,
        SprinklerCreationResult result,
        out string failureReason)
    {
        failureReason = string.Empty;
        using var transaction = new Transaction(document, "Fallback endpoint sprinkler connection");
        try
        {
            transaction.Start();
            ConfigureSilentFailureHandling(transaction);
            FamilyInstance sprinkler = document.GetElement(sprinklerId) as FamilyInstance
                ?? throw new InvalidOperationException("The sprinkler is no longer available.");
            Connector sprinklerConnector = sprinkler.MEPModel?.ConnectorManager?.Connectors
                .Cast<Connector>()
                .Where(connector => !connector.IsConnected)
                .OrderBy(connector => connector.Origin.Z)
                .FirstOrDefault()
                ?? throw new InvalidOperationException("The sprinkler has no free connector.");

            Pipe? selectedPipe = null;
            XYZ? selectedEndpoint = null;
            XYZ? selectedTarget = null;
            double nearestDistance = double.MaxValue;
            foreach (ElementId pipeId in branchPipeIds.Distinct())
            {
                if (document.GetElement(pipeId) is not Pipe pipe ||
                    pipe.Location is not LocationCurve location || location.Curve is not Line line)
                    continue;
                (XYZ Point, double Parameter, double Distance) projection =
                    ProjectPointToInfiniteLineXY(sprinklerConnector.Origin, line);
                foreach (XYZ endpoint in new[] { line.GetEndPoint(0), line.GetEndPoint(1) })
                {
                    Connector endpointConnector = FindPipeEndConnector(pipe, endpoint);
                    if (endpointConnector.IsConnected) continue;
                    double extensionDistance = endpoint.DistanceTo(projection.Point);
                    double planarHeadDistance = Math.Sqrt(
                        Math.Pow(endpoint.X - sprinklerConnector.Origin.X, 2) +
                        Math.Pow(endpoint.Y - sprinklerConnector.Origin.Y, 2));
                    if (extensionDistance > connectionTolerance ||
                        projection.Distance > connectionTolerance ||
                        planarHeadDistance >= nearestDistance)
                        continue;
                    selectedPipe = pipe;
                    selectedEndpoint = endpoint;
                    selectedTarget = new XYZ(projection.Point.X, projection.Point.Y, endpoint.Z);
                    nearestDistance = planarHeadDistance;
                }
            }
            if (selectedPipe is null || selectedEndpoint is null || selectedTarget is null)
                throw new InvalidOperationException("No free branch endpoint is close enough for the legacy connection.");

            if (!MovePipeEndpoint(
                    selectedPipe,
                    selectedEndpoint,
                    selectedTarget,
                    minimumPipeLength))
                throw new InvalidOperationException("The nearest branch cannot be extended safely.");
            document.Regenerate();
            Connector pipeConnector = FindPipeEndConnector(selectedPipe, selectedTarget);

            XYZ move = new(
                selectedTarget.X - sprinklerConnector.Origin.X,
                selectedTarget.Y - sprinklerConnector.Origin.Y,
                0);
            if (move.GetLength() > 1e-7)
            {
                ElementTransformUtils.MoveElement(document, sprinkler.Id, move);
                document.Regenerate();
            }
            sprinklerConnector = sprinkler.MEPModel?.ConnectorManager?.Connectors
                .Cast<Connector>()
                .Where(connector => !connector.IsConnected)
                .OrderBy(connector => connector.Origin.DistanceTo(pipeConnector.Origin))
                .FirstOrDefault()
                ?? throw new InvalidOperationException("The aligned sprinkler connector is unavailable.");

            double verticalDifference = Math.Abs(pipeConnector.Origin.Z - sprinklerConnector.Origin.Z);
            bool createdVertical = false;
            if (verticalDifference > minimumPipeLength)
            {
                ElementId systemTypeId = selectedPipe
                    .get_Parameter(BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM)?.AsElementId()
                    ?? ElementId.InvalidElementId;
                if (systemTypeId == ElementId.InvalidElementId)
                    throw new InvalidOperationException("The branch system type is unavailable.");
                XYZ branchPoint = pipeConnector.Origin;
                XYZ headPoint = new(branchPoint.X, branchPoint.Y, sprinklerConnector.Origin.Z);
                Pipe verticalPipe = Pipe.Create(
                    document,
                    systemTypeId,
                    selectedPipe.GetTypeId(),
                    selectedPipe.ReferenceLevel.Id,
                    branchPoint,
                    headPoint);
                Parameter? diameter = verticalPipe.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM);
                if (diameter is null || diameter.IsReadOnly || !diameter.Set(pipeConnector.Radius * 2.0))
                    throw new InvalidOperationException("The fallback rise/drop could not be sized.");
                document.Regenerate();
                Connector verticalAtBranch = FindPipeEndConnector(verticalPipe, branchPoint);
                Connector verticalAtHead = FindPipeEndConnector(verticalPipe, headPoint);
                _ = document.Create.NewElbowFitting(pipeConnector, verticalAtBranch);
                verticalAtHead.ConnectTo(sprinklerConnector);
                createdVertical = true;
            }
            else
            {
                pipeConnector.ConnectTo(sprinklerConnector);
            }
            if (transaction.Commit() != TransactionStatus.Committed)
                throw new InvalidOperationException("Revit rolled back the legacy endpoint connection.");
            if (createdVertical)
            {
                result.BranchPipes++;
                result.Fittings++;
                result.SprinklerDrops++;
            }
            result.SprinklerConnections++;
            return true;
        }
        catch (Exception exception)
        {
            try { transaction.RollBack(); } catch { }
            failureReason = exception.Message;
            return false;
        }
    }

    private static void CreatePipeFittings(
        Document document,
        IReadOnlyList<CreatedPipeEdge> pipes,
        SprinklerCreationResult result)
    {
        double nodeToleranceFeet =
            UnitUtils.ConvertToInternalUnits(5, UnitTypeId.Millimeters);
        var nodes = new Dictionary<(long X, long Y, long Z), List<PipeNodeConnection>>();
        foreach (CreatedPipeEdge edge in pipes)
        {
            if (edge.Pipe.Location is not LocationCurve pipeLocation || pipeLocation.Curve is not Line centerLine)
                continue;
            Connector[] connectors = edge.Pipe.ConnectorManager.Connectors
                .Cast<Connector>()
                .Where(connector => connector.ConnectorType == ConnectorType.End)
                .ToArray();
            if (connectors.Length < 2) continue;
            XYZ actualStart = centerLine.GetEndPoint(0);
            XYZ actualEnd = centerLine.GetEndPoint(1);
            AddEndpoint(actualStart, actualEnd);
            AddEndpoint(actualEnd, actualStart);

            void AddEndpoint(XYZ point, XYZ otherPoint)
            {
                Connector? connector = connectors.OrderBy(item => item.Origin.DistanceTo(point)).FirstOrDefault();
                if (connector is null) return;
                XYZ direction = (otherPoint - point).Normalize();
                var key = (
                    (long)Math.Round(point.X / nodeToleranceFeet),
                    (long)Math.Round(point.Y / nodeToleranceFeet),
                    (long)Math.Round(point.Z / nodeToleranceFeet));
                if (!nodes.TryGetValue(key, out List<PipeNodeConnection>? connections))
                    nodes[key] = connections = [];
                connections.Add(new PipeNodeConnection(edge.Pipe.Id, connector, point, direction, edge.VectorClass));
            }
        }

        foreach (List<PipeNodeConnection> node in nodes.Values)
        {
            // Creating or rolling back a fitting rebuilds Revit's connector
            // topology. Never read Connector objects cached by an earlier node;
            // resolve fresh connectors from the stable pipe ElementIds instead.
            PipeNodeConnection[] nodeReferences = node
                .GroupBy(item => item.PipeId)
                .Select(group => group.First())
                .ToArray();
            PipeNodeConnection[] connections = RefreshNodeConnections(document, nodeReferences);
            if (connections.Length < 2) continue;
            bool created = TryCreatePreferredNodeFitting(
                document,
                connections,
                result,
                out string fittingFailure);
            if (created)
                result.Fittings++;
            else
            {
                result.FittingsSkipped++;
                string healFailure = string.Empty;
                bool healedMainRun = connections.Length == 3 &&
                                     TryCloseFailedTeeRunWithUnion(document, connections, out healFailure);
                result.RecordFittingFailure(healedMainRun
                    ? $"{fittingFailure} The two collinear main segments were closed with a union; the branch remains disconnected."
                    : string.IsNullOrWhiteSpace(healFailure)
                        ? fittingFailure
                        : $"{fittingFailure} Main-run safety union also failed: {healFailure}");
                if (healedMainRun) result.Fittings++;
            }
        }
    }

    private static bool TryCreatePreferredNodeFitting(
        Document document,
        PipeNodeConnection[] connections,
        SprinklerCreationResult result,
        out string failureReason)
    {
        failureReason = string.Empty;
        if (connections.Length == 3 && HasMixedDiameters(connections))
        {
            // A real straight nipple keeps the reducer outside the tee body.
            // If the run is too short, use one reducing tee instead of allowing
            // two separate fittings to overlap or clash.
            if (TryCreateSeparatedReducerArrangement(document, connections, result, out string reducerFailure))
                return true;
            if (TryCommitNodeFitting(document, connections, out string directFailure))
                return true;
            failureReason = $"Equal tee + nipple + reducer failed: {reducerFailure} " +
                            $"Direct reducing tee failed: {directFailure}";
            return false;
        }
        if (connections.Length == 2 && HasMixedDiameters(connections))
        {
            double dot = connections[0].Direction.DotProduct(connections[1].Direction);
            if (dot >= -0.96 && dot <= 0.96)
            {
                if (TryCreateSeparatedReducerArrangement(
                        document,
                        connections,
                        result,
                        out string reducerFailure))
                    return true;
                if (TryCommitNodeFitting(document, connections, out string directFailure))
                    return true;
                failureReason = $"Equal elbow + short nipple + reducer failed: {reducerFailure} " +
                                $"Direct reducing elbow failed: {directFailure}";
                return false;
            }
        }

        return TryCommitNodeFitting(document, connections, out failureReason);
    }

    private static bool HasMixedDiameters(IReadOnlyList<PipeNodeConnection> connections)
    {
        if (connections.Count < 2) return false;
        double smallest = connections.Min(item => item.Connector.Radius * 2.0);
        double largest = connections.Max(item => item.Connector.Radius * 2.0);
        return largest - smallest > 0.0005;
    }

    private static bool TryCloseFailedTeeRunWithUnion(
        Document document,
        PipeNodeConnection[] connections,
        out string failureReason)
    {
        failureReason = string.Empty;
        PipeNodeConnection[] refreshed = RefreshNodeConnections(document, connections);
        if (refreshed.Length != 3)
        {
            failureReason = "The three pipe ends are no longer available.";
            return false;
        }
        (int First, int Second) pair = FindMostOppositePair(refreshed);
        PipeNodeConnection first = refreshed[pair.First];
        PipeNodeConnection second = refreshed[pair.Second];
        if (first.Direction.DotProduct(second.Direction) > -0.96)
        {
            failureReason = "No opposite pipe pair was found at the failed tee.";
            return false;
        }
        if (Math.Abs(first.Connector.Radius - second.Connector.Radius) > 0.00025)
        {
            failureReason = "The opposite pipe pair has different diameters.";
            return false;
        }
        return TryCommitNodeFitting(document, [first, second], out failureReason);
    }

    private static bool TryCommitNodeFitting(
        Document document,
        PipeNodeConnection[] connections,
        out string failureReason)
    {
        failureReason = string.Empty;
        using var fittingTransaction = new Transaction(document, "Create pipe fitting");
        try
        {
            fittingTransaction.Start();
            ConfigureSilentFailureHandling(fittingTransaction);
            PipeNodeConnection[] refreshed = RefreshNodeConnections(document, connections);
            if (refreshed.Length != connections.Length || !TryCreateNodeFitting(document, refreshed))
            {
                fittingTransaction.RollBack();
                failureReason = "The pipe node does not form a supported elbow, tee, cross, or union.";
                return false;
            }
            if (fittingTransaction.Commit() == TransactionStatus.Committed)
                return true;
            failureReason = "Routing Preferences rejected the fitting size combination.";
            return false;
        }
        catch (Exception exception)
        {
            try { fittingTransaction.RollBack(); } catch { }
            failureReason = exception.Message;
            return false;
        }
    }

    private static PipeNodeConnection[] RefreshNodeConnections(
        Document document,
        IReadOnlyList<PipeNodeConnection> connections)
    {
        double connectorTolerance = UnitUtils.ConvertToInternalUnits(10, UnitTypeId.Millimeters);
        var refreshed = new List<PipeNodeConnection>();
        foreach (PipeNodeConnection item in connections)
        {
            Pipe? pipe = document.GetElement(item.PipeId) as Pipe;
            if (pipe is null) continue;
            Connector? connector = pipe.ConnectorManager.Connectors
                .Cast<Connector>()
                .Where(candidate => candidate.ConnectorType == ConnectorType.End &&
                                    !candidate.IsConnected &&
                                    candidate.Origin.DistanceTo(item.Point) <= connectorTolerance)
                .OrderBy(candidate => candidate.Origin.DistanceTo(item.Point))
                .FirstOrDefault();
            if (connector is not null)
                refreshed.Add(item with { Connector = connector });
        }
        return refreshed.ToArray();
    }

    private static bool TryCreateSeparatedReducerArrangement(
        Document document,
        PipeNodeConnection[] connections,
        SprinklerCreationResult result,
        out string failureReason)
    {
        var failures = new List<string>();
        foreach (double nippleLengthMillimeters in new[] { 180.0, 250.0, 350.0 })
        {
            if (TryCreateEqualNodeFittingWithReducer(
                    document,
                    connections,
                    result,
                    nippleLengthMillimeters,
                    out string attemptFailure))
            {
                failureReason = string.Empty;
                return true;
            }
            failures.Add($"{nippleLengthMillimeters:0} mm: {attemptFailure}");
        }
        failureReason = string.Join(" | ", failures);
        return false;
    }

    private static bool TryCreateEqualNodeFittingWithReducer(
        Document document,
        PipeNodeConnection[] connections,
        SprinklerCreationResult result,
        double minimumNippleMillimeters,
        out string failureReason)
    {
        failureReason = string.Empty;
        using var transaction = new Transaction(document, "Create equal fitting, nipple and reducer");
        try
        {
            transaction.Start();
            ConfigureSilentFailureHandling(transaction);
            PipeNodeConnection[] refreshed = RefreshNodeConnections(document, connections);
            if (refreshed.Length is not (2 or 3))
            {
                transaction.RollBack();
                failureReason = "Pipe connectors were not available for the separated reducer arrangement.";
                return false;
            }
            (int First, int Second) mainPair = refreshed.Length == 3
                ? FindMostOppositePair(refreshed)
                : (0, 1);
            int thirdIndex = refreshed.Length == 3
                ? Enumerable.Range(0, 3)
                    .First(index => index != mainPair.First && index != mainPair.Second)
                : -1;
            double teeDiameter = refreshed.Max(item => item.Connector.Radius * 2.0);
            if (refreshed.All(item => Math.Abs(item.Connector.Radius * 2.0 - teeDiameter) < 0.0005))
            {
                transaction.RollBack();
                failureReason = "The tee ports are already the same size; no reducer fallback is applicable.";
                return false;
            }
            var reducerPorts = new List<ReducerPort>();
            for (int index = 0; index < refreshed.Length; index++)
            {
                PipeNodeConnection port = refreshed[index];
                double portDiameter = port.Connector.Radius * 2.0;
                if (Math.Abs(portDiameter - teeDiameter) < 0.0005) continue;
                Pipe originalPipe = document.GetElement(port.PipeId) as Pipe
                    ?? throw new InvalidOperationException("A reduced pipe port is unavailable.");
                LocationCurve location = originalPipe.Location as LocationCurve
                    ?? throw new InvalidOperationException("A reduced pipe has no editable centerline.");
                XYZ curveStart = location.Curve.GetEndPoint(0);
                XYZ curveEnd = location.Curve.GetEndPoint(1);
                XYZ farPoint = curveStart.DistanceTo(port.Point) > curveEnd.DistanceTo(port.Point)
                    ? curveStart
                    : curveEnd;
                double availableLength = port.Point.DistanceTo(farPoint);
                double preferredTeeStub = Math.Max(
                    UnitUtils.ConvertToInternalUnits(minimumNippleMillimeters, UnitTypeId.Millimeters),
                    teeDiameter * 2.5 + portDiameter);
                double preferredSmallStraight = Math.Max(
                    UnitUtils.ConvertToInternalUnits(75, UnitTypeId.Millimeters),
                    portDiameter * 1.5);
                if (availableLength < preferredTeeStub + preferredSmallStraight)
                    throw new InvalidOperationException(
                        "Not enough straight length for a visible nipple and reducer; use the direct reducing-fitting fallback.");
                double reducerLength = preferredTeeStub;
                XYZ reducerPoint = port.Point + port.Direction.Normalize() * reducerLength;
                location.Curve = Line.CreateBound(reducerPoint, farPoint);
                ElementId systemTypeId = originalPipe
                    .get_Parameter(BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM)?.AsElementId()
                    ?? ElementId.InvalidElementId;
                if (systemTypeId == ElementId.InvalidElementId)
                    throw new InvalidOperationException("Pipe system type is unavailable for the reducer stub.");
                Pipe teeStub = Pipe.Create(
                    document,
                    systemTypeId,
                    originalPipe.GetTypeId(),
                    originalPipe.ReferenceLevel.Id,
                    port.Point,
                    reducerPoint);
                Parameter? stubDiameter = teeStub.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM);
                if (stubDiameter is null || stubDiameter.IsReadOnly || !stubDiameter.Set(teeDiameter))
                    throw new InvalidOperationException("Could not size an equal-tee stub.");
                reducerPorts.Add(new ReducerPort(index, teeStub, originalPipe, reducerPoint, port.VectorClass));
            }
            document.Regenerate();

            var teeConnectors = new Connector[3];
            for (int index = 0; index < refreshed.Length; index++)
            {
                ReducerPort? reducer = reducerPorts.FirstOrDefault(item => item.Index == index);
                teeConnectors[index] = reducer is null
                    ? FindPipeEndConnector((Pipe)document.GetElement(refreshed[index].PipeId), refreshed[index].Point)
                    : FindPipeEndConnector(reducer.Stub, refreshed[index].Point);
            }
            if (refreshed.Length == 3)
            {
                _ = CreateVerifiedTeeFitting(
                    document,
                    teeConnectors[mainPair.First],
                    teeConnectors[mainPair.Second],
                    teeConnectors[thirdIndex]);
            }
            else
            {
                _ = document.Create.NewElbowFitting(teeConnectors[0], teeConnectors[1]);
            }
            foreach (ReducerPort reducer in reducerPorts)
            {
                Connector stubAtReducer = FindPipeEndConnector(reducer.Stub, reducer.ReducerPoint);
                Connector pipeAtReducer = FindPipeEndConnector(reducer.OriginalPipe, reducer.ReducerPoint);
                _ = document.Create.NewTransitionFitting(stubAtReducer, pipeAtReducer);
            }
            if (transaction.Commit() != TransactionStatus.Committed)
            {
                failureReason = "Routing Preferences could not create the equal fitting, short nipple, and reducer combination.";
                return false;
            }
            result.MainPipes += reducerPorts.Count(item => item.VectorClass == PdfVectorClass.MainPipe);
            result.BranchPipes += reducerPorts.Count(item => item.VectorClass == PdfVectorClass.BranchPipe);
            result.Fittings += reducerPorts.Count; // Reducers; the caller counts the tee.
            return true;
        }
        catch (Exception exception)
        {
            try { transaction.RollBack(); } catch { }
            failureReason = exception.Message;
            return false;
        }
    }

    private static Connector FindPipeEndConnector(Pipe pipe, XYZ point) =>
        pipe.ConnectorManager.Connectors
            .Cast<Connector>()
            .Where(connector => connector.ConnectorType == ConnectorType.End)
            .OrderBy(connector => connector.Origin.DistanceTo(point))
            .First();

    private static FamilyInstance CreateVerifiedTeeFitting(
        Document document,
        Connector firstMain,
        Connector secondMain,
        Connector branch)
    {
        FamilyInstance fitting = document.Create.NewTeeFitting(firstMain, secondMain, branch);
        document.Regenerate();
        Connector[] ports = fitting.MEPModel?.ConnectorManager?.Connectors
            .Cast<Connector>()
            .Where(connector => connector.ConnectorType == ConnectorType.End)
            .ToArray() ?? [];
        if (!HasOrthogonalTeeConnectorGeometry(ports))
            throw new InvalidOperationException(
                $"Routing Preferences selected '{fitting.Symbol.Family.Name} : {fitting.Symbol.Name}', " +
                "but its connectors form a Y/Wye instead of a 90-degree Tee.");
        return fitting;
    }

    private static bool HasOrthogonalTeeConnectorGeometry(IReadOnlyList<Connector> connectors)
    {
        if (connectors.Count != 3) return false;
        XYZ[] axes = connectors
            .Select(connector => connector.CoordinateSystem.BasisZ.Normalize())
            .ToArray();
        for (int first = 0; first < axes.Length; first++)
        for (int second = first + 1; second < axes.Length; second++)
        {
            if (axes[first].DotProduct(axes[second]) > -0.92) continue;
            int branch = Enumerable.Range(0, 3)
                .First(index => index != first && index != second);
            if (Math.Abs(axes[first].DotProduct(axes[branch])) <= 0.20 &&
                Math.Abs(axes[second].DotProduct(axes[branch])) <= 0.20)
                return true;
        }
        return false;
    }

    private static bool TryCreateNodeFitting(Document document, PipeNodeConnection[] connections)
    {
        if (connections.Length == 2)
        {
            double dot = connections[0].Direction.DotProduct(connections[1].Direction);
            if (dot > 0.96) return false;
            if (dot < -0.96)
            {
                _ = HasMixedDiameters(connections)
                    ? document.Create.NewTransitionFitting(connections[0].Connector, connections[1].Connector)
                    : document.Create.NewUnionFitting(connections[0].Connector, connections[1].Connector);
            }
            else
            {
                _ = document.Create.NewElbowFitting(connections[0].Connector, connections[1].Connector);
            }
            return true;
        }

        if (connections.Length == 3)
        {
            (int First, int Second) pair = FindMostOppositePair(connections);
            int branch = Enumerable.Range(0, 3).First(index => index != pair.First && index != pair.Second);
            if (connections[pair.First].Direction.DotProduct(connections[pair.Second].Direction) > -0.96 ||
                Math.Abs(connections[pair.First].Direction.DotProduct(connections[branch].Direction)) > 0.12 ||
                Math.Abs(connections[pair.Second].Direction.DotProduct(connections[branch].Direction)) > 0.12)
                return false;
            _ = CreateVerifiedTeeFitting(
                document,
                connections[pair.First].Connector,
                connections[pair.Second].Connector,
                connections[branch].Connector);
            return true;
        }

        if (connections.Length == 4)
        {
            (int First, int Second) firstPair = FindMostOppositePair(connections);
            int[] remaining = Enumerable.Range(0, 4)
                .Where(index => index != firstPair.First && index != firstPair.Second)
                .ToArray();
            _ = document.Create.NewCrossFitting(
                connections[firstPair.First].Connector,
                connections[firstPair.Second].Connector,
                connections[remaining[0]].Connector,
                connections[remaining[1]].Connector);
            return true;
        }
        return false;
    }

    private static void ConfigureSilentFailureHandling(Transaction transaction)
    {
        FailureHandlingOptions options = transaction.GetFailureHandlingOptions();
        options.SetFailuresPreprocessor(new SilentRollbackFailuresPreprocessor());
        options.SetClearAfterRollback(true);
        transaction.SetFailureHandlingOptions(options);
    }

    private static (int First, int Second) FindMostOppositePair(IReadOnlyList<PipeNodeConnection> connections)
    {
        (int First, int Second) result = (0, 1);
        double smallestDot = double.MaxValue;
        for (int first = 0; first < connections.Count; first++)
        for (int second = first + 1; second < connections.Count; second++)
        {
            double dot = connections[first].Direction.DotProduct(connections[second].Direction);
            if (dot < smallestDot)
            {
                smallestDot = dot;
                result = (first, second);
            }
        }
        return result;
    }

    private static bool TryReadNominalDiameter(WpfComboBox comboBox, out double diameterMillimeters)
    {
        string value = comboBox.SelectedItem?.ToString() ?? string.Empty;
        value = value.Replace("DN", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out diameterMillimeters) &&
               diameterMillimeters > 0;
    }

    private bool IsSameRevitDocument(Document candidate)
    {
        if (!candidate.IsValidObject || !_document.IsValidObject)
            return false;
        if (candidate.Equals(_document) || candidate.GetHashCode() == _document.GetHashCode())
            return true;
        string candidatePath = candidate.PathName ?? string.Empty;
        string originalPath = _document.PathName ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(candidatePath) && !string.IsNullOrWhiteSpace(originalPath))
            return string.Equals(candidatePath, originalPath, StringComparison.OrdinalIgnoreCase);
        // Detached and unsaved Revit documents may not expose a path, so their
        // stable project title is the best available identity across API wrappers.
        return string.Equals(candidate.Title, _document.Title, StringComparison.Ordinal);
    }

    private sealed class SprinklerCreationResult
    {
        internal int Created;
        internal int Skipped;
        internal int MainPipes;
        internal int BranchPipes;
        internal int PipesSkipped;
        internal int Fittings;
        internal int FittingsSkipped;
        internal int SprinklerConnections;
        internal int SprinklerConnectionsSkipped;
        internal int SprinklerDrops;
        internal string FamilyType = string.Empty;
        private readonly Dictionary<string, int> _fittingFailures = [];
        private readonly Dictionary<string, int> _placementFailures = [];

        internal void RecordPlacementFailure(string reason)
        {
            string key = string.IsNullOrWhiteSpace(reason) ? "Unknown sprinkler placement error." : reason.Trim();
            _placementFailures[key] = _placementFailures.GetValueOrDefault(key) + 1;
        }

        internal void RecordFittingFailure(string reason)
        {
            string key = string.IsNullOrWhiteSpace(reason) ? "Unknown fitting creation error." : reason.Trim();
            _fittingFailures[key] = _fittingFailures.GetValueOrDefault(key) + 1;
        }

        internal string FittingFailureSummary => _fittingFailures.Count == 0
            ? string.Empty
            : "\nFitting skip reasons:\n" + string.Join(
                "\n",
                _fittingFailures
                    .OrderByDescending(item => item.Value)
                    .Take(3)
                    .Select(item => $"• {item.Value:N0} × {item.Key}"));

        internal string PlacementFailureSummary => _placementFailures.Count == 0
            ? string.Empty
            : "\nPlacement skip reasons:\n" + string.Join(
                "\n",
                _placementFailures
                    .OrderByDescending(item => item.Value)
                    .Take(3)
                    .Select(item => $"• {item.Value:N0} × {item.Key}"));
    }

    private sealed class PdfAlignmentResult
    {
        internal ElementId? ImageInstanceId;
        internal ElementId? ViewId;
        internal bool ReusedExisting;
        internal string ViewName = string.Empty;
        internal string LevelName = string.Empty;
        internal XYZ? BottomLeft;
        internal XYZ? BottomRight;
        internal XYZ? TopLeft;
        internal double Width => BottomLeft?.DistanceTo(BottomRight) ?? 0;
        internal double Height => BottomLeft?.DistanceTo(TopLeft) ?? 0;
    }

    private sealed class RevitViewOption(ElementId id, string name, string levelName)
    {
        internal ElementId Id { get; } = id;
        internal string Name { get; } = name;
        internal string LevelName { get; } = levelName;
        public override string ToString() => $"{Name}  ·  {LevelName}";
    }

    private sealed class SprinklerFamilyOption(ElementId id, string familyName, string typeName)
    {
        internal ElementId Id { get; } = id;
        internal string FamilyName { get; } = familyName;
        internal string TypeName { get; } = typeName;
        public override string ToString() => $"{FamilyName} : {TypeName}";
    }

    private sealed class PipeTypeOption(
        ElementId id,
        string name,
        int junctionRules,
        int transitionRules,
        int elbowRules)
    {
        internal ElementId Id { get; } = id;
        internal string Name { get; } = name;
        internal int JunctionRules { get; } = junctionRules;
        internal int TransitionRules { get; } = transitionRules;
        internal int ElbowRules { get; } = elbowRules;
        public override string ToString() => Name;
    }

    private sealed class ElevationReferenceOption(ElementId id, string label, bool isAutomatic)
    {
        internal ElementId Id { get; } = id;
        internal bool IsAutomatic { get; } = isAutomatic;
        internal string Label { get; } = label;
        public override string ToString() => Label;
    }

    private enum PdfPipeAxis
    {
        Horizontal,
        Vertical,
        Diagonal
    }

    private readonly record struct PdfPipeFragment(
        double X1,
        double Y1,
        double X2,
        double Y2,
        double Length,
        PdfPipeAxis Axis);

    private readonly record struct PdfPipeRun(
        double X1,
        double Y1,
        double X2,
        double Y2,
        PdfVectorClass VectorClass);

    private sealed record CreatedPipeEdge(
        Pipe Pipe,
        XYZ Start,
        XYZ End,
        PdfVectorClass VectorClass);

    private sealed record PipeEndpointCandidate(
        Pipe Pipe,
        XYZ Point,
        XYZ OtherPoint,
        PdfVectorClass VectorClass);

    private sealed record PipeNodeConnection(
        ElementId PipeId,
        Connector Connector,
        XYZ Point,
        XYZ Direction,
        PdfVectorClass VectorClass);

    private sealed record ReducerPort(
        int Index,
        Pipe Stub,
        Pipe OriginalPipe,
        XYZ ReducerPoint,
        PdfVectorClass VectorClass);

    private sealed class SilentRollbackFailuresPreprocessor : IFailuresPreprocessor
    {
        public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
        {
            bool hasError = false;
            foreach (FailureMessageAccessor message in failuresAccessor.GetFailureMessages())
            {
                if (message.GetSeverity() == FailureSeverity.Warning)
                    failuresAccessor.DeleteWarning(message);
                else
                    hasError = true;
            }
            return hasError
                ? FailureProcessingResult.ProceedWithRollBack
                : FailureProcessingResult.Continue;
        }
    }

    private void AutoSizePreview()
    {
        string waterSupply = Find<WpfComboBox>("water_supply_combo").SelectedItem as string
            ?? "Not Available";
        Find<TextBlock>("head_size_text").Text = "DN15";
        Find<TextBlock>("branch_size_text").Text = "DN25 proposed";
        Border sizingStatus = Find<Border>("sizing_status_border");
        TextBlock sizingText = Find<TextBlock>("sizing_status_text");
        if (waterSupply == "Not Available")
        {
            Find<TextBlock>("main_size_text").Text = "Pending";
            sizingText.Text =
                "Preliminary only - add water supply data for hydraulic verification.";
            sizingStatus.Background = Brush("#FFF2D6");
            Status.Text = "Preliminary branch sizing complete - main remains blocked";
            return;
        }

        Find<TextBlock>("main_size_text").Text = "DN80 proposed";
        sizingText.Text =
            "Hydraulic inputs available - run final calculation before creation.";
        sizingStatus.Background = Brush("#E8F6EC");
        Status.Text = "Sizing preview updated";
    }

    private void MoveTab(int delta)
    {
        int next = Math.Max(0, Math.Min(MainTabs.SelectedIndex + delta, MainTabs.Items.Count - 1));
        MainTabs.SelectedIndex = next;
    }

    private void UpdateNavigation()
    {
        int index = Math.Max(0, MainTabs.SelectedIndex);
        string[] labels = ["Source", "Layers", "Connections", "Sizing", "Review"];
        Find<TextBlock>("step_text").Text = $"Step {index + 1} of 5  /  {labels[index]}";
        Find<Button>("back_btn").IsEnabled = index > 0;
        Find<Button>("next_btn").Content = index == 2 ? "Create System" : "Continue";
        Find<Button>("next_btn").Visibility = index < 4 ? WpfVisibility.Visible : WpfVisibility.Collapsed;
        Find<Button>("create_verified_btn").Visibility = WpfVisibility.Collapsed;
    }

    private void CreateVerified()
    {
        string waterSupply = Find<WpfComboBox>("water_supply_combo").SelectedItem as string
            ?? "Not Available";
        string message = waterSupply == "Not Available"
            ? "The C# UI and connection rules are ready, but main pipe sizing is blocked because water supply data is missing. No Revit elements were changed."
            : "The verified creation request is ready for the Revit modeling engine. No Revit elements were changed in this UI-first phase.";
        MessageBox.Show(
            _window,
            message,
            "FamilyMEP - Spinkler",
            MessageBoxButton.OK,
            waterSupply == "Not Available" ? MessageBoxImage.Warning : MessageBoxImage.Information);
    }

    private static void SetItems(WpfComboBox comboBox, IEnumerable<string> items, string selected)
    {
        List<string> values = items.ToList();
        comboBox.ItemsSource = values;
        comboBox.SelectedItem = values.Contains(selected) ? selected : values.FirstOrDefault();
    }

    private static void SelectComboValue(WpfComboBox comboBox, string value)
    {
        List<string> values = comboBox.ItemsSource?.Cast<string>().ToList() ?? [];
        if (!values.Contains(value))
        {
            values.Add(value);
            comboBox.ItemsSource = values;
        }
        comboBox.SelectedItem = value;
    }

    private static Window LoadWindow()
    {
        string root = Path.GetDirectoryName(typeof(SprinklerModelerController).Assembly.Location)
            ?? throw new InvalidOperationException("Plugin output folder is unavailable.");
        string path = Path.Combine(root, "Ui", "SprinklerModelerWindow.xaml");
        using FileStream stream = File.OpenRead(path);
        using XmlReader reader = XmlReader.Create(stream);
        return (Window)XamlReader.Load(reader);
    }

    private T Find<T>(string name) where T : FrameworkElement =>
        _window.FindName(name) as T
        ?? throw new InvalidOperationException($"UI control '{name}' was not found.");

    private sealed record CadLayerChoice(string Name, int SegmentCount)
    {
        public override string ToString() => $"{Name}  ({SegmentCount:N0})";
    }

    private sealed record CadColorChoice(
        string Name,
        CadPixelSignature Signature,
        int SampleCount)
    {
        public override string ToString() => $"{Name}  ({SampleCount:N0})";
    }

    private sealed record CadSprinklerPath(
        int PathId,
        int FirstSegmentId,
        double CenterX,
        double CenterY,
        double DiagonalMillimeters,
        int SegmentCount,
        double AspectRatio,
        double RadialVariation,
        CadPixelSignature Color,
        int GeometryScore);

    private readonly record struct CadSymbolKey(
        int Hue,
        int Segments,
        int Size,
        int Aspect);

    private readonly record struct CadNetworkColorKey(
        int Hue,
        int Saturation,
        int Value);

    private sealed record CadTopologyColorGroup(
        List<int> SegmentIds,
        double TotalLength,
        double AverageLineWeight,
        int SprinklerContacts,
        double DoubleLineRatio);

    private readonly record struct CadPixelSignature(
        double Hue,
        double Saturation,
        double Value,
        bool IsChromatic);

    private static string CadColorName(double hue) => hue switch
    {
        < 15 or >= 345 => "Red",
        < 45 => "Orange",
        < 75 => "Yellow",
        < 165 => "Green",
        < 195 => "Cyan",
        < 255 => "Blue",
        < 285 => "Violet",
        < 330 => "Magenta",
        _ => "Rose"
    };

    private static WpfColor ColorFromHsv(CadPixelSignature color)
    {
        double hue = ((color.Hue % 360) + 360) % 360;
        double chroma = color.Value * color.Saturation;
        double x = chroma * (1 - Math.Abs((hue / 60.0) % 2 - 1));
        double m = color.Value - chroma;
        (double R, double G, double B) rgb = hue switch
        {
            < 60 => (chroma, x, 0),
            < 120 => (x, chroma, 0),
            < 180 => (0, chroma, x),
            < 240 => (0, x, chroma),
            < 300 => (x, 0, chroma),
            _ => (chroma, 0, x)
        };
        return WpfColor.FromRgb(
            (byte)PortableMath.Clamp(Math.Round((rgb.R + m) * 255), 0, 255),
            (byte)PortableMath.Clamp(Math.Round((rgb.G + m) * 255), 0, 255),
            (byte)PortableMath.Clamp(Math.Round((rgb.B + m) * 255), 0, 255));
    }

    private static SolidColorBrush Brush(string hex) =>
        new((WpfColor)ColorConverter.ConvertFromString(hex));
}
