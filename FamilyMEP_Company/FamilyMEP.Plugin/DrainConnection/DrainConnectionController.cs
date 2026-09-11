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
    private ElementId? _sourceId;
    private ElementId? _targetId;
    private XYZ? _targetPickPoint;
    private int _activeCase = 1;
    private bool _busy;
    private bool _disposed;

    private Button PickRoute => Find<Button>("pick_route_btn");
    private Button Preview => Find<Button>("preview_btn");
    private Button Create => Find<Button>("create_btn");
    private WpfTextBox Slope => Find<WpfTextBox>("slope_tb");
    private WpfTextBox YRoll => Find<WpfTextBox>("y_roll_tb");
    private WpfComboBox PipeTypes => Find<WpfComboBox>("pipe_type_combo");
    private TextBlock Status => Find<TextBlock>("status_text");
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
        Slope.TextChanged += (_, _) => Refresh();
        YRoll.TextChanged += (_, _) => Refresh();
        PipeTypes.SelectionChanged += (_, _) => Refresh();
    }

    private void SelectCase(int caseNumber)
    {
        if (_busy || caseNumber is < 1 or > 6) return;
        _activeCase = caseNumber;
        bool case02 = caseNumber == 2;
        bool case03 = caseNumber == 3;
        bool case04 = caseNumber == 4;
        bool case05 = caseNumber == 5;
        bool case06 = caseNumber == 6;
        bool autoFromMain = case04 || case06;
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
        Find<TextBlock>("slope_label").Text = autoFromMain
            ? "Branch Slope — AUTO FROM MAIN"
            : "Branch Slope (%)";
        Find<TextBlock>("pipe_type_label").Text = autoFromMain
            ? "Pipe Type / Diameter — AUTO FROM MAIN"
            : "Branch Pipe Type";
        Slope.IsReadOnly = autoFromMain;
        Slope.IsEnabled = !autoFromMain;
        Slope.Visibility = autoFromMain
            ? System.Windows.Visibility.Collapsed
            : System.Windows.Visibility.Visible;
        Find<TextBlock>("slope_label").Visibility = Slope.Visibility;
        PipeTypes.IsEnabled = !autoFromMain;
        PipeTypes.Visibility = autoFromMain
            ? System.Windows.Visibility.Collapsed
            : System.Windows.Visibility.Visible;
        Find<TextBlock>("pipe_type_label").Visibility = PipeTypes.Visibility;
        Find<TextBlock>("pick_help_text").Text = case04 || case06
            ? "One command: pick the device first, then click near the required OPEN END of the horizontal/sloped main pipe."
            : "One command: pick the device first, then click the exact connection point on the horizontal/sloped main pipe.";
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
        YRoll.IsEnabled = case05;
        YRoll.Visibility = case05
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;
        Find<TextBlock>("y_roll_label").Visibility = YRoll.Visibility;
        Find<TextBlock>("y_roll_label").Foreground =
            Brush(case05 ? "#20262D" : "#7A858F");
        Find<TextBlock>("case_help_text").Text = caseNumber switch
        {
            2 => "Case 02 runs straight toward the main, turns through one 45° elbow, then uses a short diagonal leg into the manually placed and rolled routing Y.",
            3 => "Case 03 requires the device to be above the main in plan. It creates a standing pipe, one 45° elbow, and one diagonal vertical-plane leg into the routing Y; the Branch Slope field is not used for this case.",
            4 => "Case 04 connects to an existing open main endpoint. It uses two 45° elbows for the plan turn and does not break the main or create a Y fitting.",
            5 => "Case 05 creates the verified rolled pipe geometry, breaks the main at the exact picked point, then inserts a reducing Y from the main Pipe Type Junction routing preference. At 0°, A and B use Branch Slope. Above 0°, B follows the entered up-roll.",
            6 => "Case 06 uses the Case 01 gravity route but connects directly to the open main endpoint with a 45 degree elbow. DN, Pipe Type, and slope come from the main; no Y is created.",
            _ => "After Stage 1 is committed, the tool breaks the main exactly at the branch endpoint and places, rotates, and connects the Y from the selected main Pipe Type Junction routing preference."
        };
        Find<TextBlock>("stage2_label_text").Text = case04
            ? "Case 04 Output"
            : case05
                ? "Case 05 Routed Y Output"
                : case06
                    ? "Case 06 Endpoint Elbow Output"
                    : "Stage 2 — Routed Y Fitting";
        Find<TextBlock>("stage2_mode_text").Text = case04
            ? "MAIN DN + MAIN SLOPE"
            : case06
                ? "MAIN DN + MAIN SLOPE + 45° ELBOW"
                : "AUTO — routing preference fit";
        Find<TextBlock>("elbow_label_text").Text =
            case05 ? "Case 05 Fittings" : "45° Elbows";
        Find<TextBlock>("y_roll_label").Text =
            "Y Up-Roll Around Main (°) — 0° = A and B use Branch Slope";
        Create.Content = case05
            ? "CREATE CASE 05 CONNECTION"
            : $"CREATE CASE {caseNumber:00} CONNECTION";
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
                    string targetPrompt = _activeCase is 4 or 6
                        ? "2/2 - Click near the required OPEN END of the horizontal or sloped main pipe."
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
        if (_activeCase is 4 or 6 && target is not null)
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
        else
        {
            slopeReady = double.TryParse(Slope.Text, out slope) &&
                slope > 0 && slope <= 20;
        }
        if (_activeCase is 4 or 6 && slopeReady)
        {
            string automaticSlope = slope.ToString("0.###");
            if (!string.Equals(Slope.Text, automaticSlope, StringComparison.Ordinal))
                Slope.Text = automaticSlope;
        }
        double yRoll = 0.0;
        bool rollReady = _activeCase != 5 ||
            (double.TryParse(YRoll.Text, out yRoll) &&
             yRoll >= 0.0 && yRoll < 89.9);
        PipeTypeItem? typeItem = PipeTypes.SelectedItem as PipeTypeItem;
        PipeType? targetType = target is null ? null : _document.GetElement(target.GetTypeId()) as PipeType;
        PipeType? branchType = _activeCase is 4 or 6
            ? targetType
            : typeItem is null
                ? null
                : _document.GetElement(typeItem.Id) as PipeType;
        var branchRules = DrainRoutingService.RoutingRuleCounts(branchType);
        var targetRules = DrainRoutingService.RoutingRuleCounts(targetType);
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

        bool case04GeometryReady = true;
        string case04GeometryNote = "Open endpoint route not checked";
        if (_activeCase == 4)
        {
            case04GeometryReady = false;
            if (sourceReady && targetReady && slopeReady &&
                targetType is not null && _targetPickPoint is not null &&
                _sourceId is not null && _targetId is not null)
            {
                try
                {
                    IReadOnlyList<DrainRoute> routes =
                        DrainRoutingService.PreviewCase04(
                            _document,
                            _sourceId,
                            _targetId,
                            new DrainSettings(
                                slope,
                                targetType.Id,
                                0.0,
                                _targetPickPoint));
                    case04GeometryReady = routes.Count > 0;
                    case04GeometryNote = case04GeometryReady
                        ? "Nearest open endpoint to the picked point fits Case 04"
                        : "No two-45 endpoint route fits this device position";
                }
                catch (Exception exception)
                {
                    case04GeometryNote = exception.Message;
                }
            }
        }

        Find<TextBlock>("source_text").Text = Display(source);
        Find<TextBlock>("target_text").Text = Display(target) +
            (_targetPickPoint is not null
                ? _activeCase is 4 or 6
                    ? "\nNearest open endpoint to picked point"
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
                    ? _activeCase is 4 or 6
                        ? "Main selected; nearest open endpoint will be used"
                        : "Main pipe and connection point selected"
                    : _activeCase is 4 or 6
                        ? "Click near the required open endpoint of the main"
                        : "Pick a point directly on the main pipe");
        SetValidation(
            "validation_elbow_text",
            branchRules.Elbows > 0,
            $"{branchRules.Elbows} Branch Pipe Type elbow rule(s)");
        if (_activeCase == 5)
        {
            SetValidation(
                "validation_junction_text",
                targetRules.Junctions > 0,
                $"{targetRules.Junctions} junction rule(s): {targetJunctions}{reducingSizeNote}");
        }
        else if (_activeCase is 4 or 6)
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
                targetRules.Junctions > 0,
                $"{targetRules.Junctions} junction rule(s): {targetJunctions}{reducingSizeNote}");
        }
        SetValidation(
            "validation_slope_text",
            slopeReady,
            _activeCase is 4 or 6 && slopeReady
                ? $"AUTO from main: {slope:0.###}%"
                : slopeReady
                    ? $"Slope {slope:0.###}% is valid"
                    : "Slope must be > 0 and ≤ 20%.");
        if (_activeCase == 5)
        {
            SetValidation(
                "validation_flow_text",
                rollReady,
                rollReady
                    ? $"Y Up-Roll {YRoll.Text}° — " +
                      (Math.Abs(yRoll) <= 1e-8
                          ? "A and B use Branch Slope"
                          : "B follows the Y branch connector")
                    : "Y Up-Roll must be from 0° to less than 89.9°");
        }
        else if (_activeCase == 4)
        {
            SetValidation(
                "validation_flow_text",
                case04GeometryReady,
                case04GeometryNote);
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
        bool junctionReady = _activeCase != 5 || targetRules.Junctions > 0;
        bool pipeTypeReady = _activeCase is 4 or 6
            ? targetType is not null
            : typeItem is not null;
        bool ready = sourceReady && targetReady && targetPointReady &&
            slopeReady && rollReady && junctionReady && pipeTypeReady;
        Preview.IsEnabled = !_busy && ready;
        Create.IsEnabled = !_busy && ready;
    }

    private DrainSettings ReadSettings()
    {
        if (_activeCase is 4 or 6)
        {
            Pipe target = _targetId is null
                ? throw new InvalidOperationException($"Select the Case {_activeCase:00} main pipe.")
                : _document.GetElement(_targetId) as Pipe
                    ?? throw new InvalidOperationException(
                        $"The selected Case {_activeCase:00} main pipe is unavailable.");
            return new DrainSettings(
                MainSlopePercent(target),
                target.GetTypeId(),
                0.0,
                _targetPickPoint);
        }

        if (!double.TryParse(Slope.Text, out double slope) || slope <= 0 || slope > 20)
            throw new InvalidOperationException("Branch Slope must be greater than 0% and no more than 20%.");
        PipeTypeItem type = PipeTypes.SelectedItem as PipeTypeItem
            ?? throw new InvalidOperationException("Select a Branch Pipe Type.");
        double roll = 0.0;
        if (_activeCase == 5 &&
            (!double.TryParse(YRoll.Text, out roll) || roll < 0.0 || roll >= 89.9))
            throw new InvalidOperationException(
                "Y Up-Roll must be from 0 degrees up to, but not including, 89.9 degrees.");
        return new DrainSettings(slope, type.Id, roll, _targetPickPoint);
    }

    private void ShowRoute(DrainRoute route, string message)
    {
        string caseDetail = _activeCase switch
        {
            2 => "Straight + near-main 45° elbow",
            3 => "Device above main + vertical-plane 45° diagonal",
            4 => "Open main endpoint + two 45° turn",
            5 => $"Vertical + double 45° + TOP-parallel A + 45° A/B elbow + reducing Y • A slope {Slope.Text}% • Up-roll {YRoll.Text}°",
            6 => "Case 01 gravity branch + 45 degree elbow at open main endpoint",
            _ => "Direct diagonal to Y"
        };
        Find<TextBlock>("plan_summary_text").Text =
            $"CASE {_activeCase:00} • {caseDetail} • Branch {DrainGeometry.ToMm(route.BranchPlanLength):0} mm • " +
            $"Rise {DrainGeometry.ToMm(route.BranchRise):0} mm • " +
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
        PickRoute.IsEnabled = enabled;
        Slope.IsEnabled = enabled && _activeCase is not (4 or 6);
        YRoll.IsEnabled = enabled && _activeCase == 5;
        PipeTypes.IsEnabled = enabled && _activeCase is not (4 or 6);
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
        string root = Path.GetDirectoryName(typeof(DrainConnectionController).Assembly.Location)
            ?? throw new InvalidOperationException("Plugin output folder is unavailable.");
        BitmapImage plan = LoadBitmap(Path.Combine(root, "Assets", "DrainConnection", "case01-plan-realistic.png"));
        BitmapImage detail = LoadBitmap(Path.Combine(root, "Assets", "DrainConnection", "case01-fitting-realistic.png"));
        Find<Image>("case_thumb_image").Source = plan;
        Find<Image>("plan_image").Source = plan;
        Find<Image>("detail_image").Source = detail;
    }

    private static BitmapImage LoadBitmap(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Drain Connection image asset is missing.", path);
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
        string root = Path.GetDirectoryName(typeof(DrainConnectionController).Assembly.Location)
            ?? throw new InvalidOperationException("Plugin output folder is unavailable.");
        string path = Path.Combine(root, "Ui", "DrainConnectionWindow.xaml");
        using FileStream stream = File.OpenRead(path);
        using XmlReader reader = XmlReader.Create(stream);
        return (Window)XamlReader.Load(reader);
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
            : $"{element.Category?.Name} — {element.Name} [Id {element.Id.Value}]";

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
