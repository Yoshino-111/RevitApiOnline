using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using FamilyMEP.Plugin.Infrastructure;
using DbReference = Autodesk.Revit.DB.Reference;
using ExternalEvent = Autodesk.Revit.UI.ExternalEvent;
using RevitOperationCanceledException = Autodesk.Revit.Exceptions.OperationCanceledException;
using WpfComboBox = System.Windows.Controls.ComboBox;
using WpfColor = System.Windows.Media.Color;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace FamilyMEP.Plugin.DrainConnection;

internal sealed class DrainConnectionController : IDisposable
{
    private readonly UIApplication _uiApplication;
    private readonly Document _document;
    private readonly RevitRequestHandler _handler;
    private readonly ExternalEvent _externalEvent;
    private readonly Window _window;
    private readonly List<PipeTypeItem> _pipeTypes;
    private readonly List<PipeTypeItem> _junctionTypes;
    private readonly Dictionary<long, double> _compatibleYAngles;
    private ElementId? _sourceId;
    private ElementId? _targetId;
    private XYZ? _targetPickPoint;
    private int _activeCase = 1;
    private string _manualSlopeText = "2.0";
    private bool _updatingSlopeText;
    private bool _busy;
    private bool _disposed;

    private Button PickRoute => Find<Button>("pick_route_btn");
    private Button Preview => Find<Button>("preview_btn");
    private Button Create => Find<Button>("create_btn");
    private WpfTextBox Slope => Find<WpfTextBox>("slope_tb");
    private WpfTextBox YRoll => Find<WpfTextBox>("y_roll_tb");
    private WpfTextBox Case04MiddleLength =>
        Find<WpfTextBox>("case04_middle_length_tb");
    private WpfComboBox PipeTypes => Find<WpfComboBox>("pipe_type_combo");
    private WpfComboBox JunctionTypes => Find<WpfComboBox>("junction_type_combo");
    private TextBlock Status => Find<TextBlock>("status_text");
    private TextBlock YRollStatus => Find<TextBlock>("y_roll_status_text");
    private TextBlock Case04MiddleLengthStatus =>
        Find<TextBlock>("case04_middle_length_status_text");
    private Border Case01Card => Find<Border>("case01_card");
    private Border Case02Card => Find<Border>("case02_card");
    private Border Case03Card => Find<Border>("case03_card");
    private Border Case04Card => Find<Border>("case04_card");
    private Border Case05Card => Find<Border>("case05_card");
    private Border Case06Card => Find<Border>("case06_card");

    public DrainConnectionController(
        UIApplication uiApplication,
        RevitRequestHandler handler,
        ExternalEvent externalEvent)
    {
        _uiApplication = uiApplication;
        _document = uiApplication.ActiveUIDocument?.Document
            ?? throw new InvalidOperationException("Open a Revit project first.");
        _handler = handler;
        _externalEvent = externalEvent;
        _window = LoadWindow();
        string revitVersion = uiApplication.Application.VersionNumber;
        Find<TextBlock>("runtime_badge_text").Text = revitVersion == "2020"
            ? "Revit 2020 • Case02 Socket Fix"
            : $"Revit {revitVersion} • Case02 Socket Fix";
        _manualSlopeText = Slope.Text;
        new WindowInteropHelper(_window).Owner = uiApplication.MainWindowHandle;

        _pipeTypes = new FilteredElementCollector(_document)
            .OfClass(typeof(PipeType))
            .Cast<PipeType>()
            .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(item => new PipeTypeItem(item.Id, item.Name))
            .ToList();
        if (_pipeTypes.Count == 0)
            throw new InvalidOperationException("No Pipe Types were found.");

        PipeTypes.ItemsSource = _pipeTypes;
        PipeTypes.SelectedIndex = 0;
        IReadOnlyList<PipeTypeItem> compatibleYTypes =
            DrainRoutingService.FindCompatibleLoadedYTypes(_document);
        _compatibleYAngles = compatibleYTypes
            .Where(item =>
                item.FittingAngleDegrees.HasValue &&
                !item.AngleRequiresMeasurement)
            .ToDictionary(
                item => item.Id.CompatValue(),
                item => item.FittingAngleDegrees!.Value);
        _junctionTypes = new List<PipeTypeItem>
        {
            new(ElementId.InvalidElementId, "AUTO - main pipe routing preferences")
        };
        _junctionTypes.AddRange(compatibleYTypes);
        JunctionTypes.ItemsSource = _junctionTypes;
        JunctionTypes.SelectedIndex = compatibleYTypes.Count > 0 ? 1 : 0;
        Find<TextBlock>("junction_catalog_text").Text = compatibleYTypes.Count > 0
            ? $"{compatibleYTypes.Count} loaded Y candidate(s). The selected type is measured from its 3 real connectors before pipes are created."
            : "No loaded Y candidate was found. Load a 3-connector Wye/Y family or add it to a Pipe Type Junction rule, then reopen this tool.";
        Find<TextBlock>("junction_catalog_text").Foreground = Brush(
            compatibleYTypes.Count > 0 ? "#238A46" : "#B3261E");
        LoadImages();
        WireEvents();
        Refresh();
    }

    public void Show() => _window.Show();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _window.Close(); } catch { }
    }

    private void WireEvents()
    {
        _window.Closed += (_, _) => _disposed = true;
        PickRoute.Click += (_, _) => PickSourceAndTarget();
        Preview.Click += (_, _) => PreviewRoute();
        Create.Click += (_, _) => CreateRoute();
        Case01Card.MouseLeftButtonUp += (_, _) => SelectCase(1);
        Case02Card.MouseLeftButtonUp += (_, _) => SelectCase(2);
        Case03Card.MouseLeftButtonUp += (_, _) => SelectCase(3);
        Case04Card.MouseLeftButtonUp += (_, _) => SelectCase(4);
        Case05Card.MouseLeftButtonUp += (_, _) => SelectCase(5);
        Case06Card.MouseLeftButtonUp += (_, _) => SelectCase(6);
        Slope.TextChanged += (_, _) =>
        {
            if (!_updatingSlopeText && _activeCase is not (4 or 5))
                _manualSlopeText = Slope.Text;
            Refresh();
        };
        YRoll.TextChanged += (_, _) => Refresh();
        Case04MiddleLength.TextChanged += (_, _) => Refresh();
        PipeTypes.SelectionChanged += (_, _) => Refresh();
        JunctionTypes.SelectionChanged += (_, _) => Refresh();
    }

    private void SelectCase(int caseNumber)
    {
        if (_busy || caseNumber is < 1 or > 6) return;
        bool previousSlopeWasAutomatic = _activeCase is 4 or 5;
        if (!previousSlopeWasAutomatic)
            _manualSlopeText = Slope.Text;
        _activeCase = caseNumber;
        bool case02 = caseNumber == 2;
        bool case03 = caseNumber == 3;
        bool case04 = caseNumber == 4;
        bool case05 = caseNumber == 5;
        bool case06 = caseNumber == 6;
        bool autoFromMain = case04 || case05;
        bool slopeIsGeometryDriven = case03 || autoFromMain;
        if (previousSlopeWasAutomatic && !autoFromMain &&
            !string.Equals(Slope.Text, _manualSlopeText, StringComparison.Ordinal))
        {
            _updatingSlopeText = true;
            Slope.Text = _manualSlopeText;
            _updatingSlopeText = false;
        }
        Case01Card.BorderBrush = Brush(caseNumber == 1 ? "#1473E6" : "#D5DADF");
        Case01Card.BorderThickness = new Thickness(caseNumber == 1 ? 2 : 1);
        Case02Card.BorderBrush = Brush(case02 ? "#1473E6" : "#D5DADF");
        Case02Card.BorderThickness = new Thickness(case02 ? 2 : 1);
        Case03Card.BorderBrush = Brush(case03 ? "#1473E6" : "#D5DADF");
        Case03Card.BorderThickness = new Thickness(case03 ? 2 : 1);
        Case04Card.BorderBrush = Brush(case04 ? "#1473E6" : "#D5DADF");
        Case04Card.BorderThickness = new Thickness(case04 ? 2 : 1);
        Case05Card.BorderBrush = Brush(case05 ? "#1473E6" : "#D5DADF");
        Case05Card.BorderThickness = new Thickness(case05 ? 2 : 1);
        Case06Card.BorderBrush = Brush(case06 ? "#1473E6" : "#D5DADF");
        Case06Card.BorderThickness = new Thickness(case06 ? 2 : 1);
        Find<TextBlock>("preview_title_text").Text =
            $"CASE {caseNumber:00} PREVIEW";
        Find<TextBlock>("settings_title_text").Text =
            $"CASE {caseNumber:00} SETTINGS";
        Find<TextBlock>("slope_label").Text = case03
            ? "Branch Slope — SET BY Y GEOMETRY"
            : autoFromMain
                ? "Branch Slope — AUTO FROM MAIN"
                : "Branch Slope (%)";
        Find<TextBlock>("pipe_type_label").Text = autoFromMain
            ? "Pipe Type / Diameter — AUTO FROM MAIN"
            : "Branch Pipe Type";
        Slope.IsReadOnly = slopeIsGeometryDriven;
        Slope.IsEnabled = !slopeIsGeometryDriven;
        Slope.Visibility = slopeIsGeometryDriven
            ? System.Windows.Visibility.Collapsed
            : System.Windows.Visibility.Visible;
        Find<TextBlock>("slope_label").Visibility = Slope.Visibility;
        JunctionTypes.IsEnabled = !autoFromMain;
        JunctionTypes.Visibility = autoFromMain
            ? System.Windows.Visibility.Collapsed
            : System.Windows.Visibility.Visible;
        Find<TextBlock>("junction_catalog_text").Visibility =
            JunctionTypes.Visibility;
        PipeTypes.IsEnabled = !autoFromMain;
        PipeTypes.Visibility = autoFromMain
            ? System.Windows.Visibility.Collapsed
            : System.Windows.Visibility.Visible;
        Find<TextBlock>("pipe_type_label").Visibility = PipeTypes.Visibility;
        Find<TextBlock>("pick_help_text").Text = case04 || case05
            ? "Pick device, then click an open end or the point where the short surplus main tail should be removed."
            : "Pick device, then click the connection point on the main.";
        Find<Image>("plan_image").Visibility =
            caseNumber == 1 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        Find<Viewbox>("case02_plan_view").Visibility =
            case02 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        Find<Viewbox>("case03_plan_view").Visibility =
            case03 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        Find<Viewbox>("case04_plan_view").Visibility =
            case04 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        Find<Viewbox>("case05_plan_view").Visibility =
            case05 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        Find<Viewbox>("case06_plan_view").Visibility =
            case06 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        // Case 06 is a fixed plan layout: A runs parallel to the main and B
        // turns 45 degrees into the routed Y. Keep the old roll input hidden.
        YRoll.IsEnabled = false;
        YRoll.Visibility = System.Windows.Visibility.Collapsed;
        Find<TextBlock>("y_roll_label").Visibility = YRoll.Visibility;
        YRollStatus.Visibility = YRoll.Visibility;
        Case04MiddleLength.IsEnabled = case04;
        Case04MiddleLength.Visibility = case04
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;
        Find<TextBlock>("case04_middle_length_label").Visibility =
            Case04MiddleLength.Visibility;
        Case04MiddleLengthStatus.Visibility = Case04MiddleLength.Visibility;
        Find<TextBlock>("y_roll_label").Foreground =
            Brush(case06 ? "#20262D" : "#7A858F");
        Find<TextBlock>("case_help_text").Text = caseNumber switch
        {
            2 => "Case 02 runs straight toward the main, turns through one 45° elbow, then uses a short diagonal leg into the manually placed and rolled routing Y.",
            3 => "Case 03 requires the device above the main in plan. Its vertical-plane branch slope and source elbow follow the selected Y angle plus the main slope; the calculated slope is shown in Preview.",
            4 => "Case 04 connects at an open end or a picked interior trim point. The shorter surplus main tail is removed automatically before the two 45° turns are completed.",
            5 => "Case 05 connects the Case 01 gravity route at an open end or picked trim point with a 45 degree elbow. The shorter surplus main tail is removed automatically; no Y is created.",
            6 => "Case 06 drops vertically, uses two 45 degree elbows into pipe A parallel to the main, then one 45 degree elbow into pipe B and a Y at the picked main break point.",
            _ => "After Stage 1 is committed, the tool breaks the main exactly at the branch endpoint and places, rotates, and connects the Y using the selected Y type (AUTO uses main pipe routing preferences)."
        };
        Find<TextBlock>("stage2_label_text").Text = case04
            ? "Case 04 Output"
            : case05
                ? "Case 05 Endpoint Elbow Output"
                : case06
                    ? "Case 06 Elbows + Routed Y"
                    : "Stage 2 — Routed Y Fitting";
        Find<TextBlock>("stage2_mode_text").Text = case04
            ? "MAIN DN + MAIN SLOPE"
            : case05
                ? "MAIN DN + MAIN SLOPE + 45° ELBOW"
                : "AUTO — routing preference fit";
        Find<TextBlock>("elbow_label_text").Text =
            case06 ? "Case 06 Fittings" : "45° Elbows";
        Find<TextBlock>("y_roll_label").Text = "Y Up-Roll (°)";
        Create.Content = $"CREATE CASE {caseNumber:00}";
        Find<TextBlock>("plan_summary_text").Text =
            "Pick a drain and a horizontal main.";
        Status.Text = $"Case {caseNumber:00} selected.";
        Status.Foreground = Brush("#68727D");
        Refresh();
    }

    private void PickSourceAndTarget()
    {
        _window.Hide();
        Queue(
            app =>
            {
                try
                {
                    UIDocument uidoc = RequireUidoc(app);
                    DbReference sourceReference = uidoc.Selection.PickObject(
                        ObjectType.Element,
                        new DrainSourceFilter(),
                        "1/2 - Select the drain device first (open round piping connector).");
                    string targetPrompt = _activeCase is 4 or 5
                        ? "2/2 - Click an OPEN END, or click inside the main where its short surplus tail should be removed."
                        : "2/2 - Click the exact connection point on the horizontal or sloped main pipe.";
                    DbReference targetReference = uidoc.Selection.PickObject(
                        ObjectType.Element,
                        new HorizontalPipeFilter(),
                        targetPrompt);

                    Pipe target = (Pipe)_document.GetElement(targetReference.ElementId);
                    if (target.Location is not LocationCurve targetLocation)
                        throw new InvalidOperationException(
                            "The selected main pipe has no usable centerline.");
                    XYZ rawPickPoint = targetReference.GlobalPoint
                        ?? throw new InvalidOperationException(
                            "Revit did not return the picked point on the main pipe.");
                    IntersectionResult projection = targetLocation.Curve.Project(rawPickPoint)
                        ?? throw new InvalidOperationException(
                            "The picked point could not be projected to the main centerline.");

                    _sourceId = sourceReference.ElementId;
                    _targetId = targetReference.ElementId;
                    _targetPickPoint = projection.XYZPoint;
                    PipeTypeItem? matching = _pipeTypes.FirstOrDefault(item => item.Id == target.GetTypeId());
                    if (matching is not null)
                        _window.Dispatcher.BeginInvoke(() => PipeTypes.SelectedItem = matching);
                    PipeType? targetType = _document.GetElement(target.GetTypeId()) as PipeType;
                    PipeTypeItem? matchingY = FirstCompatibleYRule(targetType);
                    if (matchingY is not null)
                        _window.Dispatcher.BeginInvoke(() => JunctionTypes.SelectedItem = matchingY);
                }
                catch (RevitOperationCanceledException)
                {
                    // Treat the two picks as one atomic selection and keep the previous pair.
                }
            },
            "Pick the device first, then the main pipe in Revit...",
            () =>
            {
                _window.Show();
                _window.Activate();
                Refresh();
            });
    }

    private void PreviewRoute()
    {
        DrainSettings settings;
        try { settings = ReadSettings(); }
        catch (Exception exception) { ShowError(exception); return; }

        DrainRoute? route = null;
        Queue(
            app =>
            {
                route = (_activeCase switch
                {
                    3 => DrainRoutingService.PreviewCase03(
                        _document,
                        RequireId(_sourceId, "source drain"),
                        RequireId(_targetId, "target main"),
                        settings),
                    4 => DrainRoutingService.PreviewCase04(
                        _document,
                        RequireId(_sourceId, "source drain"),
                        RequireId(_targetId, "target main"),
                        settings),
                    5 => DrainRoutingService.PreviewCase05(
                        _document,
                        RequireId(_sourceId, "source drain"),
                        RequireId(_targetId, "target main"),
                        settings),
                    6 => DrainRoutingService.PreviewCase06(
                        _document,
                        RequireId(_sourceId, "source drain"),
                        RequireId(_targetId, "target main"),
                        settings),
                    2 => DrainRoutingService.PreviewCase02(
                        _document,
                        RequireId(_sourceId, "source drain"),
                        RequireId(_targetId, "target main"),
                        settings),
                    _ => DrainRoutingService.Preview(
                        _document,
                        RequireId(_sourceId, "source drain"),
                        RequireId(_targetId, "target main"),
                        settings)
                })[0];
                UIDocument uidoc = RequireUidoc(app);
                var ids = new List<ElementId> { _sourceId!, _targetId! };
                uidoc.Selection.SetElementIds(ids);
                uidoc.ShowElements(ids);
                uidoc.RefreshActiveView();
            },
            "Validating automatic route...",
            () =>
            {
                if (route is not null)
                {
                    ShowRoute(route, "Route validated. Model unchanged until Create.");
                }
            });
    }

    private void CreateRoute()
    {
        DrainSettings settings;
        try { settings = ReadSettings(); }
        catch (Exception exception) { ShowError(exception); return; }

        DrainOperationResult? result = null;
        Queue(
            app =>
            {
                result = _activeCase switch
                {
                    3 => DrainRoutingService.CreateCase03(
                        _document,
                        RequireId(_sourceId, "source drain"),
                        RequireId(_targetId, "target main"),
                        settings),
                    4 => DrainRoutingService.CreateCase04(
                        _document,
                        RequireId(_sourceId, "source drain"),
                        RequireId(_targetId, "target main"),
                        settings),
                    5 => DrainRoutingService.CreateCase05(
                        _document,
                        RequireId(_sourceId, "source drain"),
                        RequireId(_targetId, "target main"),
                        settings),
                    6 => DrainRoutingService.CreateCase06(
                        _document,
                        RequireId(_sourceId, "source drain"),
                        RequireId(_targetId, "target main"),
                        settings),
                    2 => DrainRoutingService.CreateCase02(
                        _document,
                        RequireId(_sourceId, "source drain"),
                        RequireId(_targetId, "target main"),
                        settings),
                    _ => DrainRoutingService.Create(
                        _document,
                        RequireId(_sourceId, "source drain"),
                        RequireId(_targetId, "target main"),
                        settings)
                };
                if (result.CreatedIds is { Count: > 0 })
                {
                    UIDocument uidoc = RequireUidoc(app);
                    List<ElementId> ids = result.CreatedIds.ToList();
                    uidoc.Selection.SetElementIds(ids);
                    uidoc.ShowElements(ids);
                    uidoc.RefreshActiveView();
                }
            },
            $"Creating Case {_activeCase:00} connection...",
            () =>
            {
                if (result is null) return;
                if (result.Route is not null)
                {
                    ShowRoute(result.Route, result.Message);
                }
                string operationStatus = result.Message +
                    (result.Partial && result.Warnings is { Count: > 0 }
                        ? "  " + string.Join(" | ", result.Warnings.TakeLast(4))
                        : string.Empty);
                try
                {
                    Directory.CreateDirectory(AppPaths.LogFolder);
                    File.AppendAllText(Path.Combine(AppPaths.LogFolder, "drain-connection.log"),
                        $"{DateTime.Now:O} Case {_activeCase:00}; source={_sourceId}; main={_targetId}; " +
                        $"selectedY={JunctionTypes.SelectedItem}; point={_targetPickPoint}\n" +
                        operationStatus + "\n" + string.Join("\n", result.Warnings ?? []) + "\n\n");
                }
                catch (Exception exception)
                {
                    operationStatus += $" Log could not be written: {exception.Message}";
                }
                if (result.Partial)
                {
                    string fittingFailure = result.Warnings is { Count: > 0 }
                        ? result.Warnings.Last()
                        : "No fitting diagnostic was returned.";
                    var details = new TaskDialog("Drain Connection - Y insertion failed")
                    {
                        MainInstruction = "Branch pipes were created, but the Y was not connected.",
                        MainContent = "The main break was rolled back.\n\nLast fitting error:\n" + fittingFailure,
                        ExpandedContent = operationStatus,
                        CommonButtons = TaskDialogCommonButtons.Close
                    };
                    details.Show();
                }
                Status.Text = operationStatus;
                Status.ToolTip = operationStatus;
                Status.Foreground = Brush(result.Partial ? "#B3261E" : "#237A41");
                Refresh();
            });
    }

    private void Queue(Action<UIApplication> request, string status, Action completed)
    {
        if (_busy) return;
        _busy = true;
        Status.Text = status;
        SetEnabled(false);
        Action<UIApplication> loggedRequest = app =>
        {
            try
            {
                request(app);
            }
            catch (Exception exception)
            {
                LogRequestFailure(exception);
                throw;
            }
        };
        if (!_handler.TrySetRequest(loggedRequest, error =>
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
            throw new InvalidOperationException("Another Revit request is running.");
        }

        ExternalEventRequest raised = _externalEvent.Raise();
        if (raised is not ExternalEventRequest.Accepted and not ExternalEventRequest.Pending)
        {
            _busy = false;
            SetEnabled(true);
            throw new InvalidOperationException($"Revit rejected the request: {raised}.");
        }
    }

    private void LogRequestFailure(Exception exception)
    {
        try
        {
            string sourceGeometry = "unavailable";
            if (_sourceId is not null && _document.GetElement(_sourceId) is Element source)
            {
                Connector connector = DrainSelection.ChooseSourceConnector(source);
                sourceGeometry = $"origin={connector.Origin}; DN={DrainGeometry.ToMm(connector.Radius * 2):0.###}";
            }

            string mainGeometry = "unavailable";
            if (_targetId is not null &&
                _document.GetElement(_targetId) is Pipe main &&
                main.Location is LocationCurve location)
            {
                mainGeometry =
                    $"start={location.Curve.GetEndPoint(0)}; " +
                    $"end={location.Curve.GetEndPoint(1)}; " +
                    $"DN={DrainGeometry.ToMm(main.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM)?.AsDouble() ?? 0):0.###}";
            }

            PipeTypeItem? selectedY = JunctionTypes.SelectedItem as PipeTypeItem;
            Directory.CreateDirectory(AppPaths.LogFolder);
            File.AppendAllText(
                Path.Combine(AppPaths.LogFolder, "drain-connection.log"),
                $"{DateTime.Now:O} FAILED Case {_activeCase:00}; " +
                $"source={_sourceId} ({sourceGeometry}); main={_targetId} ({mainGeometry}); " +
                $"pick={_targetPickPoint}; slope={Slope.Text}; selectedY={selectedY}; " +
                $"YAngle={selectedY?.FittingAngleDegrees?.ToString("0.###") ?? "AUTO"}\n" +
                exception + "\n\n");
        }
        catch
        {
            // Diagnostics must never replace the original Revit error.
        }
    }

    private void Refresh()
    {
        if (_disposed) return;
        Element? source = _sourceId is null ? null : _document.GetElement(_sourceId);
        Pipe? target = _targetId is null ? null : _document.GetElement(_targetId) as Pipe;
        bool sourceReady = false;
        string sourceNote = "Pick a floor drain.";
        if (source is not null)
        {
            try
            {
                Connector connector = DrainSelection.ChooseSourceConnector(source);
                sourceReady = true;
                sourceNote = $"Open round connector: DN{DrainGeometry.ToMm(connector.Radius * 2):0}";
            }
            catch (Exception exception) { sourceNote = exception.Message; }
        }
        bool targetReady = DrainSelection.IsHorizontalPipe(target);
        bool mainDirectionAutomatic = false;
        if (targetReady && target?.Location is LocationCurve targetLocation)
        {
            mainDirectionAutomatic = DrainGeometry.MainHasPlanUphillDirection(
                targetLocation.Curve.GetEndPoint(0),
                targetLocation.Curve.GetEndPoint(1));
        }
        double slope = 0.0;
        bool slopeReady;
        if (_activeCase is 4 or 5 && target is not null)
        {
            try
            {
                slope = MainSlopePercent(target);
                slopeReady = true;
            }
            catch
            {
                slopeReady = false;
            }
        }
        else if (_activeCase == 3)
        {
            slopeReady = true;
        }
        else
        {
            slopeReady = double.TryParse(Slope.Text, out slope) &&
                slope > 0 && slope <= 20;
        }
        if (_activeCase is 4 or 5 && slopeReady)
        {
            string automaticSlope = slope.ToString("0.###");
            if (!string.Equals(Slope.Text, automaticSlope, StringComparison.Ordinal))
            {
                _updatingSlopeText = true;
                Slope.Text = automaticSlope;
                _updatingSlopeText = false;
            }
        }
        double yRoll = 0.0;
        bool rollReady = _activeCase != 6 ||
            (double.TryParse(YRoll.Text, out yRoll) &&
             yRoll >= 0.0 && yRoll < 89.9);
        double case04MiddleLengthMm = 0.0;
        bool case04MiddleLengthReady = _activeCase != 4 ||
            (double.TryParse(Case04MiddleLength.Text, out case04MiddleLengthMm) &&
             case04MiddleLengthMm >= 0.0 && case04MiddleLengthMm <= 10000.0);
        PipeTypeItem? typeItem = PipeTypes.SelectedItem as PipeTypeItem;
        PipeType? targetType = target is null ? null : _document.GetElement(target.GetTypeId()) as PipeType;
        PipeType? branchType = _activeCase is 4 or 5
            ? targetType
            : typeItem is null
                ? null
                : _document.GetElement(typeItem.Id) as PipeType;
        var branchRules = DrainRoutingService.RoutingRuleCounts(branchType);
        var targetRules = DrainRoutingService.RoutingRuleCounts(targetType);
        bool autoYReady = TargetHasCompatibleYRule(targetType);
        string targetJunctions =
            DrainRoutingService.DescribeJunctionRules(_document, targetType);
        double mainDiameter = target?.get_Parameter(
            BuiltInParameter.RBS_PIPE_DIAMETER_PARAM)?.AsDouble() ?? 0;
        double deviceDiameter = 0;
        if (sourceReady && source is not null)
        {
            try
            {
                deviceDiameter = DrainSelection.ChooseSourceConnector(source).Radius * 2.0;
            }
            catch
            {
                deviceDiameter = 0;
            }
        }
        string reducingSizeNote = mainDiameter > 0 && deviceDiameter > 0
            ? $" • Y run DN{DrainGeometry.ToMm(mainDiameter):0.#}/DN{DrainGeometry.ToMm(mainDiameter):0.#}, branch DN{DrainGeometry.ToMm(deviceDiameter):0.#}"
            : string.Empty;

        bool specialGeometryReady = true;
        string specialGeometryNote = "Route geometry not checked";
        if (_activeCase is 4 or 5 or 6)
        {
            specialGeometryReady = false;
            if (sourceReady && targetReady && slopeReady && rollReady &&
                case04MiddleLengthReady &&
                _targetPickPoint is not null &&
                _sourceId is not null && _targetId is not null)
            {
                try
                {
                    DrainSettings previewSettings = ReadSettings();
                    IReadOnlyList<DrainRoute> routes = _activeCase switch
                    {
                        4 => DrainRoutingService.PreviewCase04(
                            _document,
                            _sourceId,
                            _targetId,
                            previewSettings),
                        5 => DrainRoutingService.PreviewCase05(
                            _document,
                            _sourceId,
                            _targetId,
                            previewSettings),
                        _ => DrainRoutingService.PreviewCase06(
                            _document,
                            _sourceId,
                            _targetId,
                            previewSettings)
                    };
                    specialGeometryReady = routes.Count > 0;
                    specialGeometryNote = specialGeometryReady
                        ? _activeCase switch
                        {
                            4 => "Picked endpoint / trim point fits the Case 04 two-45 route",
                            5 => "Picked endpoint / trim point fits the Case 05 45° elbow route",
                            _ => "Pipe A is parallel to the main; pipe B turns 45° into the picked Y break point"
                        }
                        : $"No Case {_activeCase:00} route fits this selection";
                }
                catch (Exception exception)
                {
                    specialGeometryNote = exception.Message;
                }
            }
        }

        Find<TextBlock>("source_text").Text = Display(source);
        Find<TextBlock>("target_text").Text = Display(target) +
            (_targetPickPoint is not null
                ? _activeCase is 4 or 5
                    ? "\nClicked endpoint / trim point selected"
                    : "\nConnection point selected"
                : string.Empty);
        Find<TextBlock>("elbow_status_text").Text = branchRules.Elbows > 0
            ? $"{branchRules.Elbows} elbow rule(s) found"
            : "No elbow rule found";
        SetValidation("validation_source_text", sourceReady, sourceNote);
        bool targetPointReady = _activeCase is not (4 or 5 or 6) ||
            _targetPickPoint is not null;
        SetValidation(
            "validation_target_text",
            targetReady && targetPointReady,
            !targetReady
                ? "Pick a horizontal main."
                : targetPointReady
                    ? _activeCase is 4 or 5
                        ? "Main selected; the clicked endpoint or trim point will be used"
                        : "Main pipe and connection point selected"
                    : _activeCase is 4 or 5
                        ? "Click an open endpoint or an interior trim point on the main"
                        : "Pick a point directly on the main pipe");
        SetValidation(
            "validation_elbow_text",
            branchRules.Elbows > 0,
            $"{branchRules.Elbows} Branch Pipe Type elbow rule(s)");
        bool explicitY = JunctionTypes.SelectedItem is PipeTypeItem chosenY &&
            chosenY.Id != ElementId.InvalidElementId;
        Find<TextBlock>("stage2_mode_text").Text = _activeCase is 4 or 5
            ? "OPEN-END CONNECTION"
            : explicitY ? "SELECTED FAMILY / TYPE" : "AUTO - routing preferences";
        if (explicitY && _activeCase is not (4 or 5))
        {
            SetValidation("validation_junction_text", true,
                $"Selected fitting: {JunctionTypes.SelectedItem}. Three connectors and Y angle are checked before branch creation.");
        }
        else if (_activeCase == 6)
        {
            SetValidation(
                "validation_junction_text",
                autoYReady,
                autoYReady
                    ? $"Compatible Y found in {targetRules.Junctions} junction rule(s): {targetJunctions}{reducingSizeNote}"
                    : $"No compatible 3-connector Y in main Pipe Type routing rules. {targetJunctions}");
        }
        else if (_activeCase is 4 or 5)
        {
            SetValidation(
                "validation_junction_text",
                true,
                "Y fitting is not required for this open-end connection");
        }
        else
        {
            SetValidation(
                "validation_junction_text",
                autoYReady,
                autoYReady
                    ? $"Compatible Y found in {targetRules.Junctions} junction rule(s): {targetJunctions}{reducingSizeNote}"
                    : $"No compatible 3-connector Y in main Pipe Type routing rules. {targetJunctions}");
        }
        SetValidation(
            "validation_slope_text",
            slopeReady,
            _activeCase == 3
                ? "AUTO from the Y angle and main slope; see Preview for the calculated value"
                : _activeCase is 4 or 5 && slopeReady
                    ? $"AUTO from main: {slope:0.###}%"
                    : slopeReady
                        ? $"Slope {slope:0.###}% is valid"
                        : "Slope must be > 0 and ≤ 20%.");
        if (_activeCase == 4)
        {
            Case04MiddleLengthStatus.Text = case04MiddleLengthReady
                ? $"At least {case04MiddleLengthMm:0.###} mm of clear pipe will remain after both elbow takeouts."
                : "Enter a length from 0 to 10000 mm.";
            Case04MiddleLengthStatus.Foreground = Brush(
                case04MiddleLengthReady ? "#238A46" : "#B3261E");
            Case04MiddleLength.BorderBrush = Brush(
                case04MiddleLengthReady ? "#AEB6BF" : "#B3261E");
        }
        if (_activeCase == 6)
        {
            bool showGeometryFailure = sourceReady && targetReady &&
                slopeReady && rollReady && _targetPickPoint is not null &&
                !specialGeometryReady;
            YRollStatus.Text = !rollReady
                ? "Enter an angle from 0° to less than 89.9°."
                : specialGeometryReady
                    ? $"{YRoll.Text}° fits the available drop at this break point."
                    : showGeometryFailure
                        ? specialGeometryNote
                        : "Pick the device and main to calculate the maximum usable roll.";
            YRollStatus.Foreground = Brush(
                specialGeometryReady ? "#238A46" :
                showGeometryFailure || !rollReady ? "#B3261E" : "#68727D");
            YRoll.BorderBrush = Brush(
                showGeometryFailure || !rollReady ? "#B3261E" : "#AEB6BF");
            SetValidation(
                "validation_flow_text",
                rollReady && specialGeometryReady,
                !rollReady
                    ? "Y Up-Roll must be from 0° to less than 89.9°"
                    : specialGeometryNote);
        }
        else if (_activeCase is 4 or 5)
        {
            SetValidation(
                "validation_flow_text",
                specialGeometryReady,
                specialGeometryNote);
        }
        else
        {
            SetValidation(
                "validation_flow_text",
                targetReady,
                mainDirectionAutomatic
                    ? "Both plan sides evaluated; branch rises continuously from main to device"
                    : "Main is level — both plan sides are evaluated automatically");
        }
        bool yRequired = _activeCase is 1 or 2 or 3 or 6;
        bool junctionReady = !yRequired || explicitY || autoYReady;
        bool pipeTypeReady = _activeCase is 4 or 5
            ? targetType is not null
            : typeItem is not null;
        bool ready = sourceReady && targetReady && targetPointReady &&
            slopeReady && rollReady && case04MiddleLengthReady &&
            junctionReady && pipeTypeReady &&
            (_activeCase is not (4 or 5 or 6) || specialGeometryReady);
        Preview.IsEnabled = !_busy && ready;
        Create.IsEnabled = !_busy && ready;
    }

    private bool TargetHasCompatibleYRule(PipeType? pipeType)
    {
        if (pipeType is null || _compatibleYAngles.Count == 0)
            return false;
        RoutingPreferenceManager manager = pipeType.RoutingPreferenceManager;
        int count = manager.GetNumberOfRules(
            RoutingPreferenceRuleGroupType.Junctions);
        for (int index = 0; index < count; index++)
        {
            RoutingPreferenceRule rule = manager.GetRule(
                RoutingPreferenceRuleGroupType.Junctions,
                index);
            if (_compatibleYAngles.ContainsKey(rule.MEPPartId.CompatValue()))
                return true;
        }
        return false;
    }

    private PipeTypeItem? FirstCompatibleYRule(PipeType? pipeType)
    {
        if (pipeType is null)
            return null;
        RoutingPreferenceManager manager = pipeType.RoutingPreferenceManager;
        int count = manager.GetNumberOfRules(
            RoutingPreferenceRuleGroupType.Junctions);
        for (int index = 0; index < count; index++)
        {
            ElementId partId = manager.GetRule(
                RoutingPreferenceRuleGroupType.Junctions,
                index).MEPPartId;
            PipeTypeItem? match = _junctionTypes.FirstOrDefault(item =>
                item.Id == partId && item.FittingAngleDegrees.HasValue);
            if (match is not null)
                return match;
        }
        return null;
    }

    private DrainSettings ReadSettings()
    {
        if (_activeCase is 4 or 5)
        {
            Pipe target = _targetId is null
                ? throw new InvalidOperationException($"Select the Case {_activeCase:00} main pipe.")
                : _document.GetElement(_targetId) as Pipe
                    ?? throw new InvalidOperationException(
                        $"The selected Case {_activeCase:00} main pipe is unavailable.");
            double middleLengthMm = 200.0;
            if (_activeCase == 4 &&
                (!double.TryParse(Case04MiddleLength.Text, out middleLengthMm) ||
                 middleLengthMm < 0.0 || middleLengthMm > 10000.0))
                throw new InvalidOperationException(
                    "Case 04 middle pipe length must be from 0 to 10000 mm.");
            return new DrainSettings(
                MainSlopePercent(target),
                target.GetTypeId(),
                0.0,
                _targetPickPoint,
                Case04MiddlePipeLengthMm: middleLengthMm);
        }

        double slope;
        if (_activeCase == 3)
        {
            slope = 1.0;
        }
        else if (!double.TryParse(Slope.Text, out slope) || slope <= 0 || slope > 20)
        {
            throw new InvalidOperationException("Branch Slope must be greater than 0% and no more than 20%.");
        }
        PipeTypeItem type = PipeTypes.SelectedItem as PipeTypeItem
            ?? throw new InvalidOperationException("Select a Branch Pipe Type.");
        double roll = 0.0;
        if (_activeCase == 6 &&
            (!double.TryParse(YRoll.Text, out roll) || roll < 0.0 || roll >= 89.9))
            throw new InvalidOperationException(
                "Y Up-Roll must be from 0 degrees up to, but not including, 89.9 degrees.");
        PipeTypeItem junction = JunctionTypes.SelectedItem as PipeTypeItem
            ?? throw new InvalidOperationException("Select a Y fitting type.");
        IReadOnlyList<double> junctionAngles;
        if (junction.Id != ElementId.InvalidElementId &&
            !junction.AngleRequiresMeasurement &&
            junction.FittingAngleDegrees.HasValue)
        {
            junctionAngles = [junction.FittingAngleDegrees.Value];
        }
        else if (junction.Id != ElementId.InvalidElementId &&
                 junction.AngleRequiresMeasurement)
        {
            // Used only for the non-mutating WPF geometry preview. Create always
            // replaces this seed with the angle measured from the real connectors.
            junctionAngles = [45.0];
        }
        else
        {
            Pipe? main = _targetId is null
                ? null
                : _document.GetElement(_targetId) as Pipe;
            PipeType? mainType = main is null
                ? null
                : _document.GetElement(main.GetTypeId()) as PipeType;
            junctionAngles = CompatibleYAnglesIn(mainType);
        }
        return new DrainSettings(
            slope,
            type.Id,
            roll,
            _targetPickPoint,
            junction.Id,
            junctionAngles,
            _compatibleYAngles);
    }

    private IReadOnlyList<double> CompatibleYAnglesIn(PipeType? pipeType)
    {
        if (pipeType is null)
            return [];
        RoutingPreferenceManager manager = pipeType.RoutingPreferenceManager;
        int count = manager.GetNumberOfRules(
            RoutingPreferenceRuleGroupType.Junctions);
        var angles = new List<double>();
        for (int index = 0; index < count; index++)
        {
            ElementId partId = manager.GetRule(
                RoutingPreferenceRuleGroupType.Junctions,
                index).MEPPartId;
            if (_compatibleYAngles.TryGetValue(partId.CompatValue(), out double angle))
                angles.Add(angle);
        }
        return angles.Distinct().OrderBy(angle => angle).ToList();
    }

    private void ShowRoute(DrainRoute route, string message)
    {
        string caseDetail = _activeCase switch
        {
            2 => "Straight + near-main 45° elbow",
            3 => "Device above main + selected-Y vertical-plane diagonal",
            4 => $"Open main endpoint + two 45° turn • middle pipe ≥ {Case04MiddleLength.Text} mm",
            5 => "Case 01 gravity branch + 45 degree elbow at endpoint / trim point",
            6 => "Vertical drop + two 45° elbows + A parallel to main + 45° B to routed Y",
            _ => "Direct diagonal to Y"
        };
        Find<TextBlock>("plan_summary_text").Text =
            $"CASE {_activeCase:00} • {caseDetail} • Branch {DrainGeometry.ToMm(route.BranchPlanLength):0} mm • " +
            $"Rise {DrainGeometry.ToMm(route.BranchRise):0} mm • " +
            $"Slope {route.SlopePercent:0.####}% • " +
            $"Y {route.FittingAngleDegrees:0.#}° • Plan {route.PlanAngleDegrees:0.###}° • Flow-safe";
        Status.Text = message;
        Status.Foreground = Brush("#237A41");
    }

    private void SetValidation(string name, bool valid, string text)
    {
        TextBlock block = Find<TextBlock>(name);
        block.Text = $"{(valid ? "●" : "○")}  {text}";
        block.Foreground = Brush(valid ? "#238A46" : "#7A858F");
    }

    private void SetEnabled(bool enabled)
    {
        JunctionTypes.IsEnabled = enabled && _activeCase is not (4 or 5);
        PickRoute.IsEnabled = enabled;
        Slope.IsEnabled = enabled && _activeCase is not (3 or 4 or 5);
        YRoll.IsEnabled = false;
        Case04MiddleLength.IsEnabled = enabled && _activeCase == 4;
        PipeTypes.IsEnabled = enabled && _activeCase is not (4 or 5);
        if (enabled) Refresh();
        else
        {
            Preview.IsEnabled = false;
            Create.IsEnabled = false;
        }
    }

    private void ShowError(Exception exception)
    {
        Status.Text = exception.Message;
        Status.ToolTip = exception.ToString();
        Status.Foreground = Brush("#B3261E");
        if (!_window.IsVisible) _window.Show();
    }

    private void LoadImages()
    {
        Find<Image>("case_thumb_image").Source = LoadDrainBitmap("case01-thumb.png");
        for (int caseNumber = 2; caseNumber <= 4; caseNumber++)
        {
            Find<Image>($"case{caseNumber:00}_thumb_image").Source =
                LoadDrainBitmap($"case{caseNumber:00}-thumb.png");
        }
        Find<Image>("case05_thumb_image").Source = LoadDrainBitmap("case06-thumb.png");
        Find<Image>("case06_thumb_image").Source = LoadDrainBitmap("case05-thumb.png");
    }

    private static BitmapImage LoadDrainBitmap(string fileName)
    {
        string root = PluginOutputFolder();
        string path = Path.Combine(root, "Assets", "DrainConnection", fileName);
        if (File.Exists(path)) return LoadBitmap(path);

        System.Reflection.Assembly assembly = typeof(DrainConnectionController).Assembly;
        string? resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith("." + fileName, StringComparison.OrdinalIgnoreCase));
        if (resourceName is null)
            throw new FileNotFoundException(
                $"Drain Connection image '{fileName}' is missing from both the hot-reload folder and the plugin DLL.",
                path);

        using Stream resource = assembly.GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException($"Cannot open embedded Drain Connection image '{fileName}'.");
        using var copy = new MemoryStream();
        resource.CopyTo(copy);
        copy.Position = 0;

        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = copy;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static BitmapImage LoadBitmap(string path)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(path, UriKind.Absolute);
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static Window LoadWindow()
    {
        string root = PluginOutputFolder();
        string path = Path.Combine(root, "Ui", "DrainConnectionWindow.xaml");
        using FileStream stream = File.OpenRead(path);
        using XmlReader reader = XmlReader.Create(stream);
        return (Window)XamlReader.Load(reader);
    }

    private static string PluginOutputFolder()
    {
        string? hotReloadFolder = AppDomain.CurrentDomain.GetData(
            "FamilyMEP.HotReloadSourceFolder") as string;
        if (!string.IsNullOrWhiteSpace(hotReloadFolder) &&
            Directory.Exists(hotReloadFolder))
            return hotReloadFolder;
        return Path.GetDirectoryName(typeof(DrainConnectionController).Assembly.Location)
            ?? throw new InvalidOperationException("Plugin output folder is unavailable.");
    }

    private T Find<T>(string name) where T : FrameworkElement =>
        _window.FindName(name) as T
        ?? throw new InvalidOperationException($"UI control '{name}' was not found.");

    private UIDocument RequireUidoc(UIApplication app)
    {
        UIDocument uidoc = app.ActiveUIDocument
            ?? throw new InvalidOperationException("No active Revit project.");
        if (!uidoc.Document.Equals(_document))
            throw new InvalidOperationException("Return to the project where Drain Connection was opened.");
        return uidoc;
    }

    private static ElementId RequireId(ElementId? id, string label) =>
        id ?? throw new InvalidOperationException($"Select a {label}.");

    private static string Display(Element? element) =>
        element is null
            ? "Not selected"
            : $"{element.Category?.Name} — {element.Name} [Id {element.Id.CompatValue()}]";

    private static double MainSlopePercent(Pipe pipe)
    {
        if (pipe.Location is not LocationCurve location)
            throw new InvalidOperationException(
                "The selected main has no usable centerline.");
        XYZ start = location.Curve.GetEndPoint(0);
        XYZ end = location.Curve.GetEndPoint(1);
        double dx = end.X - start.X;
        double dy = end.Y - start.Y;
        double planLength = Math.Sqrt(dx * dx + dy * dy);
        if (planLength <= 1e-9)
            throw new InvalidOperationException(
                "Case 04 requires a non-vertical main.");
        return Math.Abs(end.Z - start.Z) / planLength * 100.0;
    }

    private static SolidColorBrush Brush(string hex) =>
        new((WpfColor)ColorConverter.ConvertFromString(hex));
}
