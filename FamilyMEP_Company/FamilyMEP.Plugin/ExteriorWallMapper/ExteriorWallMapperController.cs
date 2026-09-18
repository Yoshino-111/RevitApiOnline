using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExternalService;
using Autodesk.Revit.UI;
using FamilyMEP.Plugin.Infrastructure;
using Microsoft.Win32;
using RevitExternalEvent = Autodesk.Revit.UI.ExternalEvent;
using ExternalEventRequest = Autodesk.Revit.UI.ExternalEventRequest;
using WpfComboBox = System.Windows.Controls.ComboBox;
using WpfSlider = System.Windows.Controls.Slider;
using WpfTextBox = System.Windows.Controls.TextBox;
using RevitReference = Autodesk.Revit.DB.Reference;
using WpfColor = System.Windows.Media.Color;

namespace FamilyMEP.Plugin.ExteriorWallMapper;

internal sealed class ExteriorWallMapperController : IDisposable
{
    private const string All = "(All)";
    private const string FocusViewName = "Exterior Wall Mapper - Focus";
    // Kept only to remove Generic Model previews created by older tool builds.
    private const string LegacyPreviewApplicationId = "FamilyMEP.ExteriorWallMapper.Preview";
    private const string SharedWallPreviewApplicationId = "FamilyMEP.ExteriorWallMapper.SharedWallPreview";
    // Legacy cleanup only. Current builds never create a focus DirectShape.
    private const string FocusPreviewApplicationId = "FamilyMEP.ExteriorWallMapper.FocusPreview";
    private readonly UIApplication _uiApplication;
    private readonly Document _document;
    private readonly RevitRequestHandler _handler;
    private readonly RevitExternalEvent _externalEvent;
    private readonly Window _window;
    private readonly ObservableCollection<ExteriorWallRow> _rows = [];
    private readonly ObservableCollection<RoomOrientationSummary> _roomSummaries = [];
    private readonly ObservableCollection<ExteriorWallRow> _detailRows = [];
    private readonly ObservableCollection<ArchitecturalWallTypeGroup> _architecturalWallGroups = [];
    private readonly ObservableCollection<WallThicknessGroup> _thicknessGroups = [];
    private readonly ObservableCollection<WallThicknessSpaceItem> _thicknessSpaces = [];
    private readonly ObservableCollection<ReplacementBatch> _batches = [];
    private readonly ObservableCollection<LinearBatchPlan> _linearPlans = [];
    private readonly ObservableCollection<LinearRoomFaceItem> _linearFaces = [];
    private readonly ObservableCollection<RoomWallRoomSummary> _roomWallRooms = [];
    private readonly ObservableCollection<RoomWallInventoryItem> _roomWallInventory = [];
    private readonly ObservableCollection<ExteriorWallLayerItem> _roomWallLayers = [];
    private readonly ObservableCollection<WallTypeGroupItem> _wallTypeGroups = [];
    private readonly ObservableCollection<WallTypeOccurrenceItem> _wallTypeOccurrences = [];
    private readonly List<string> _warnings = [];
    private readonly DispatcherTimer _contextTransparencyTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(220)
    };
    private readonly Dictionary<long, ContextTransparencySnapshot> _contextTransparencySnapshots = [];
    private ICollectionView? _rowView;
    private ExteriorWallScanResult? _lastScan;
    private SectionBoxSnapshot? _sectionBoxSnapshot;
    private long? _isolationViewId;
    private ExteriorWallFocusGraphicsServer? _focusGraphics;
    private string? _navigatorLevel;
    private string? _selectedRoomWallSpaceKey;
    private bool _updatingRoomWallInventory;
    private bool _oneComponentForAllEwa;
    private string _globalEwaComponent = string.Empty;
    private bool _updatingGlobalEwaUi;
    private bool _applyingGlobalEwaTarget;
    private int _contextTransparencyPercent;
    private bool _busy;
    private bool _disposed;

    internal ExteriorWallMapperController(
        UIApplication uiApplication,
        RevitRequestHandler handler,
        RevitExternalEvent externalEvent)
    {
        _uiApplication = uiApplication;
        _handler = handler;
        _externalEvent = externalEvent;
        _document = uiApplication.ActiveUIDocument?.Document
            ?? throw new InvalidOperationException("Open a Revit project before starting Exterior Wall Mapper.");
        _window = LoadWindow();
        new WindowInteropHelper(_window).Owner = uiApplication.MainWindowHandle;
        WireEvents();
        InitializeUi();
    }

    internal void Show() => _window.Show();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _contextTransparencyTimer.Stop();
        _focusGraphics?.Clear();
        foreach (ReplacementBatch batch in _batches)
            batch.PropertyChanged -= OnBatchPropertyChanged;
        try { _window.Close(); } catch { }
    }

    private void WireEvents()
    {
        _window.Closed += (_, _) =>
        {
            _contextTransparencyTimer.Stop();
            _disposed = true;
        };
        Find<Button>("scan_btn").Click += (_, _) => Scan();
        Find<Button>("export_btn").Click += (_, _) => ExportExcel();
        Find<Button>("focus_element_btn").Click += (_, _) => FocusElementFromInput();
        Find<Button>("focus_plan_btn").Click += (_, _) => FocusElementFromInput(focusInFloorPlan: true);
        Find<WpfTextBox>("element_id_textbox").KeyDown += (_, args) =>
        {
            if (args.Key != System.Windows.Input.Key.Enter) return;
            args.Handled = true;
            FocusElementFromInput();
        };
        ScrollViewer sidebar = Find<ScrollViewer>("sidebar_scroll");
        sidebar.PreviewMouseWheel += (_, args) =>
        {
            sidebar.ScrollToVerticalOffset(sidebar.VerticalOffset - args.Delta);
            args.Handled = true;
        };
        _contextTransparencyTimer.Tick += (_, _) =>
        {
            _contextTransparencyTimer.Stop();
            ApplyContextTransparencyFromUi();
        };
        Find<WpfSlider>("context_transparency_slider").ValueChanged += (_, args) =>
        {
            _contextTransparencyPercent = (int)Math.Round(args.NewValue);
            Find<TextBlock>("context_transparency_text").Text = $"{_contextTransparencyPercent}%";
            _contextTransparencyTimer.Stop();
            if (_isolationViewId.HasValue && !_disposed) _contextTransparencyTimer.Start();
        };
        Find<Button>("clear_filters_btn").Click += (_, _) => ClearFilters();
        Find<TreeView>("space_navigator").SelectedItemChanged += (_, _) => NavigatorSelectionChanged();
        foreach (WpfComboBox combo in FilterCombos()) combo.SelectionChanged += (_, _) => RefreshFilter();
        Find<DataGrid>("room_summary_grid").SelectionChanged += (_, _) => UpdateSelectionStatus();
        Find<DataGrid>("walls_grid").SelectionChanged += (_, _) => UpdateSelectionStatus();
        Find<DataGrid>("architectural_walls_grid").SelectionChanged += (_, _) => UpdateSelectionStatus();
        Find<DataGrid>("thickness_groups_grid").SelectionChanged += (_, _) =>
        {
            RebuildThicknessSpaces();
            UpdateSelectionStatus();
        };
        Find<DataGrid>("thickness_spaces_grid").SelectionChanged += (_, _) => UpdateSelectionStatus();
        Find<DataGrid>("linear_plan_grid").SelectionChanged += (_, _) =>
        {
            RebuildLinearFaces();
            UpdateSelectionStatus();
        };
        Find<DataGrid>("linear_faces_grid").SelectionChanged += (_, _) => UpdateSelectionStatus();
        Find<DataGrid>("room_wall_rooms_grid").SelectionChanged += (_, _) => RoomWallRoomSelectionChanged();
        Find<DataGrid>("room_wall_inventory_grid").SelectionChanged += (_, _) =>
        {
            RebuildSelectedRoomWallLayers();
            UpdateSelectionStatus();
        };
        Find<DataGrid>("room_wall_layers_grid").AddHandler(
            Button.ClickEvent,
            new RoutedEventHandler(RoomWallLayerButtonClicked));
        Find<DataGrid>("wall_type_groups_grid").SelectionChanged += (_, _) => RebuildWallTypeOccurrences();
        Find<WpfComboBox>("room_wall_scope_combo").SelectionChanged += (_, _) => RebuildRoomWallInventory();
        Find<WpfComboBox>("room_wall_type_combo").SelectionChanged += (_, _) => RebuildRoomWallInventoryDetails();
        Find<TabControl>("linear_kind_tabs").SelectionChanged += (_, _) =>
        {
            RebuildLinearPlans();
            UpdateSelectionStatus();
        };
        Find<Button>("copy_linear_plan_btn").Click += (_, _) => CopyLinearFindReplacePlan();
        Find<CheckBox>("global_ewa_checkbox").Checked += (_, _) => GlobalEwaModeChanged();
        Find<CheckBox>("global_ewa_checkbox").Unchecked += (_, _) => GlobalEwaModeChanged();
        Find<WpfTextBox>("global_ewa_component_textbox").TextChanged += (_, _) => GlobalEwaComponentChanged();
        Find<TabControl>("detail_tabs").SelectionChanged += (_, _) => UpdateSelectionStatus();
        Find<TabControl>("results_tabs").SelectionChanged += (_, _) => UpdateSelectionStatus();
        Find<DataGrid>("batches_grid").SelectionChanged += (_, _) =>
        {
            RebuildDetailOccurrences();
            UpdateSelectionStatus();
        };
    }

    private void InitializeUi()
    {
        Find<TextBlock>("project_text").Text = string.IsNullOrWhiteSpace(_document.PathName)
            ? _document.Title
            : Path.GetFileName(_document.PathName);
        _rowView = CollectionViewSource.GetDefaultView(_rows);
        _rowView.Filter = FilterRow;
        _rowView.SortDescriptions.Add(new SortDescription(nameof(ExteriorWallRow.Level), ListSortDirection.Ascending));
        _rowView.SortDescriptions.Add(new SortDescription(nameof(ExteriorWallRow.SpaceNumber), ListSortDirection.Ascending));
        _rowView.SortDescriptions.Add(new SortDescription(nameof(ExteriorWallRow.Orientation), ListSortDirection.Ascending));
        _rowView.SortDescriptions.Add(new SortDescription(nameof(ExteriorWallRow.ThicknessMm), ListSortDirection.Ascending));
        Find<DataGrid>("room_summary_grid").ItemsSource = _roomSummaries;
        Find<DataGrid>("walls_grid").ItemsSource = _detailRows;
        Find<DataGrid>("batches_grid").ItemsSource = _batches;
        Find<DataGrid>("architectural_walls_grid").ItemsSource = _architecturalWallGroups;
        Find<DataGrid>("thickness_groups_grid").ItemsSource = _thicknessGroups;
        Find<DataGrid>("thickness_spaces_grid").ItemsSource = _thicknessSpaces;
        Find<DataGrid>("linear_plan_grid").ItemsSource = _linearPlans;
        Find<DataGrid>("linear_faces_grid").ItemsSource = _linearFaces;
        Find<DataGrid>("room_wall_rooms_grid").ItemsSource = _roomWallRooms;
        Find<DataGrid>("room_wall_inventory_grid").ItemsSource = _roomWallInventory;
        Find<DataGrid>("room_wall_layers_grid").ItemsSource = _roomWallLayers;
        Find<DataGrid>("wall_type_groups_grid").ItemsSource = _wallTypeGroups;
        Find<DataGrid>("wall_type_occurrences_grid").ItemsSource = _wallTypeOccurrences;
        Find<WpfComboBox>("room_wall_scope_combo").ItemsSource = new[]
        {
            "ALL ROOMS",
            "ROOMS WITH SUN-EXPOSED EWA",
            "ROOMS WITHOUT SUN-EXPOSED EWA"
        };
        Find<WpfComboBox>("room_wall_scope_combo").SelectedIndex = 0;
        Find<WpfComboBox>("room_wall_type_combo").ItemsSource = new[]
        {
            "All LINEAR types",
            "EWA",
            "IWA",
            "EWI",
            "IWI",
            "ED",
            "ID"
        };
        Find<WpfComboBox>("room_wall_type_combo").SelectedIndex = 0;
        foreach (WpfComboBox combo in FilterCombos())
        {
            combo.ItemsSource = new[] { All };
            combo.SelectedIndex = 0;
        }
        SetEnabled(true);
        UpdateGlobalEwaControls();
        UpdateMetrics();
    }

    private void Scan()
    {
        ExteriorWallScanResult? result = null;
        Queue(
            application =>
            {
                CleanupTemporaryPreviewState(application);
                result = ExteriorWallScanner.Scan(_document);
            },
            "Scanning MEP Spaces and linked IFC/Revit wall boundaries...",
            () => ApplyScan(result ?? throw new InvalidOperationException("The scan returned no result.")));
    }

    private void ApplyScan(ExteriorWallScanResult result)
    {
        _lastScan = result;
        _warnings.Clear();
        _warnings.AddRange(result.Warnings);
        _rows.Clear();
        foreach (ExteriorWallRow row in result.Rows) _rows.Add(row);
        RebuildBatches();
        ApplyGlobalEwaTarget();
        PopulateFilters();
        RebuildRoomOrientationSummaries();
        RebuildArchitecturalWalls();
        RebuildThicknessGroups();
        RebuildLinearPlans();
        RebuildRoomWallInventory();
        UpdateMetrics();
        SetEnabled(true);

        string categorySummary = string.Join(", ", _rows
            .GroupBy(row => row.LinearCategory)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => $"{group.Key} {group.Count():N0}"));
        string summary = $"Found {_rows.Count:N0} LINEAR component group(s) across {result.SpaceCount:N0} MEP Space(s)" +
                         (categorySummary.Length == 0 ? "." : $" ({categorySummary}).");
        if (_warnings.Count > 0) summary += $" {_warnings.Count:N0} scan warning(s) need review.";
        if (_rows.Count == 0)
        {
            string? diagnostics = _warnings.FirstOrDefault(message =>
                message.StartsWith("Scan diagnostics:", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(diagnostics)) summary += $" {diagnostics}";
        }
        SetStatus(summary, _warnings.Count == 0 ? "#14966A" : "#B66A00", string.Join(Environment.NewLine, _warnings));
        if (_rows.Count == 0 && _warnings.Count > 0)
        {
            MessageBox.Show(
                _window,
                string.Join(Environment.NewLine + Environment.NewLine, _warnings),
                "Exterior Wall Mapper - Scan diagnostics",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }

    private void RebuildBatches()
    {
        foreach (ReplacementBatch old in _batches)
            old.PropertyChanged -= OnBatchPropertyChanged;
        _batches.Clear();
        var indexes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (IGrouping<(string LinearCategory, string AssemblyKey), ExteriorWallRow> group in _rows
            .GroupBy(row => (row.LinearCategory, row.AssemblyKey))
            .OrderBy(group => group.Key.LinearCategory, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(group => group.Sum(row => row.ExteriorAreaM2)))
        {
            ExteriorWallRow sample = group.First();
            int index = indexes.GetValueOrDefault(group.Key.LinearCategory) + 1;
            indexes[group.Key.LinearCategory] = index;
            string batchId = $"{group.Key.LinearCategory}-{index:00}";
            string sourceId = $"A-{group.Key.LinearCategory}-{index:00}";
            ExteriorWallRow[] batchRows = group.ToArray();
            foreach (ExteriorWallRow row in batchRows) row.BatchId = batchId;
            string status = batchRows.All(row => row.Status == "Ready")
                ? "Ready"
                : batchRows.Any(row => row.Status == "Unmapped")
                    ? "Unmapped"
                    : "Review";
            var batch = new ReplacementBatch
            {
                BatchId = batchId,
                LinearCategory = group.Key.LinearCategory,
                AssemblyKey = group.Key.AssemblyKey,
                SourceAssembly = $"{sourceId} ({sample.ThicknessMm:0.#} mm, {sample.LayerCount} layers)",
                LayerDescription = sample.LayerDescription,
                Layers = sample.Layers,
                RecommendedLayers = sample.RecommendedLayers,
                RecommendedLayerDescription = sample.RecommendedLayerDescription,
                WallCount = batchRows.Sum(row => row.WallCount),
                ExteriorAreaM2 = batchRows.Sum(row => row.ExteriorAreaM2),
                Status = status,
                Rows = batchRows
            };
            batch.PropertyChanged += OnBatchPropertyChanged;
            _batches.Add(batch);
        }
        Find<TextBlock>("batch_count_text").Text = $"{_batches.Count:N0} batches";
        Find<DataGrid>("walls_grid").Items.Refresh();
        RebuildDetailOccurrences();
    }

    private void OnBatchPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName != nameof(ReplacementBatch.TargetLinear) || sender is not ReplacementBatch batch)
            return;
        foreach (ExteriorWallRow row in batch.Rows) row.TargetLinear = batch.TargetLinear;
        // A global EWA edit updates every exterior-wall batch. Defer the plan
        // notification until the bulk operation is complete so typing one
        // component code does not refresh the same DataGrid hundreds of times.
        if (!_applyingGlobalEwaTarget)
        {
            foreach (LinearBatchPlan plan in _linearPlans.Where(plan =>
                plan.Batches.Any(item => ReferenceEquals(item, batch))))
                plan.NotifyTargetChanged();
        }
        if (_oneComponentForAllEwa && !_applyingGlobalEwaTarget &&
            batch.LinearCategory == LinearComponentKinds.ExteriorWall)
        {
            string[] targets = _batches
                .Where(item => item.LinearCategory == LinearComponentKinds.ExteriorWall)
                .Select(item => item.TargetLinear)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (targets.Length <= 1)
            {
                _globalEwaComponent = targets.FirstOrDefault() ?? string.Empty;
                UpdateGlobalEwaControls();
            }
        }
    }

    private void RebuildRoomOrientationSummaries()
    {
        _roomSummaries.Clear();
        IEnumerable<ExteriorWallRow> visibleRows = _rowView?.Cast<ExteriorWallRow>() ?? [];
        foreach (var group in visibleRows
            .GroupBy(row => new
            {
                row.SpaceId,
                row.SpaceGroupLabel,
                row.Level,
                row.Orientation
            })
            .OrderBy(group => group.Key.Level, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(group => group.Key.SpaceGroupLabel, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(group => group.Key.Orientation, StringComparer.OrdinalIgnoreCase))
        {
            ExteriorWallRow[] rows = group.ToArray();
            _roomSummaries.Add(new RoomOrientationSummary
            {
                Level = group.Key.Level,
                SpaceGroupLabel = group.Key.SpaceGroupLabel,
                Orientation = group.Key.Orientation,
                LinearCategories = string.Join(", ", rows.Select(row => row.LinearCategory)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)),
                WallGroupCount = rows.Select(row => row.AssemblyKey)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count(),
                ArchitecturalWallTypeCount = rows
                    .SelectMany(row => row.References.Select(reference => new
                    {
                        Type = reference.ElementName.Trim().ToUpperInvariant(),
                        Thickness = Math.Round(row.ThicknessMm, 1),
                        row.LayerCount
                    }))
                    .Distinct()
                    .Count(),
                MinimumLayerCount = rows.Min(row => row.LayerCount),
                MaximumLayerCount = rows.Max(row => row.LayerCount),
                Rows = rows
            });
        }
    }

    private void RebuildDetailOccurrences()
    {
        ExteriorWallRow[] visibleRows = _rowView?.Cast<ExteriorWallRow>().ToArray() ?? [];
        ReplacementBatch[] selectedBatches = Find<DataGrid>("batches_grid")
            .SelectedItems
            .Cast<ReplacementBatch>()
            .ToArray();
        IEnumerable<ExteriorWallRow> source = selectedBatches.Length > 0
            ? selectedBatches
                .SelectMany(batch => batch.Rows)
                .Where(row => FilterRow(row))
                .Distinct()
            : visibleRows;
        _detailRows.Clear();
        foreach (ExteriorWallRow row in source) _detailRows.Add(row);
        TextBlock empty = Find<TextBlock>("detail_empty_text");
        empty.Visibility = selectedBatches.Length > 0 && _detailRows.Count == 0
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;
        empty.Text = selectedBatches.Length > 0
            ? "This structure group has no occurrences inside the current Level / Space / Orientation filters."
            : string.Empty;
    }

    private void RebuildArchitecturalWalls()
    {
        _architecturalWallGroups.Clear();
        ExteriorWallRow[] visibleRows = _rowView?.Cast<ExteriorWallRow>().ToArray() ?? [];
        var entries = visibleRows.SelectMany(row => row.References.Select(reference => new
        {
            Row = row,
            Reference = reference,
            NormalizedType = reference.ElementName.Trim().ToUpperInvariant(),
            RoundedThickness = Math.Round(row.ThicknessMm, 1)
        }));
        foreach (var group in entries
            .GroupBy(item => new
            {
                item.NormalizedType,
                item.RoundedThickness,
                item.Row.LayerCount,
                item.Reference.LinkPath
            })
            .OrderByDescending(group => group.Key.RoundedThickness)
            .ThenBy(group => group.Key.NormalizedType, StringComparer.CurrentCultureIgnoreCase))
        {
            var uniqueReferences = group
                .Select(item => item.Reference)
                .DistinctBy(item => $"{item.RootLinkInstanceId}|{item.LinkPath}|{item.LinkedElementId}")
                .ToArray();
            ExteriorWallRow[] rows = group.Select(item => item.Row).Distinct().ToArray();
            _architecturalWallGroups.Add(new ArchitecturalWallTypeGroup
            {
                TypeName = group.First().Reference.ElementName,
                ThicknessMm = group.Key.RoundedThickness,
                LayerCount = group.Key.LayerCount,
                LinkChain = group.Key.LinkPath,
                Rows = rows,
                References = uniqueReferences
            });
        }
        int uniqueElementCount = _architecturalWallGroups
            .SelectMany(group => group.References)
            .DistinctBy(item => $"{item.RootLinkInstanceId}|{item.LinkPath}|{item.LinkedElementId}")
            .Count();
        Find<TextBlock>("architectural_wall_count_text").Text =
            $"{_architecturalWallGroups.Count:N0} type group(s) / {uniqueElementCount:N0} element(s)";
    }

    private void RebuildThicknessGroups()
    {
        _thicknessGroups.Clear();
        ExteriorWallRow[] visibleRows = _rowView?.Cast<ExteriorWallRow>().ToArray() ?? [];
        var entries = visibleRows.SelectMany(row =>
        {
            IEnumerable<ExteriorWallLayerItem> layers = row.Layers.Count > 0
                ? row.Layers
                : [new ExteriorWallLayerItem(1, row.LayerDescription, row.ThicknessMm, false, true)];
            return layers
                .Where(layer => layer.ThicknessMm > 0.2)
                .Select(layer => new
                {
                    Row = row,
                    Layer = layer,
                    RoundedThickness = Math.Round(layer.ThicknessMm, 0)
                });
        });

        foreach (var group in entries
            .GroupBy(item => item.RoundedThickness)
            .OrderByDescending(group => group.Key))
        {
            ExteriorWallRow[] rows = group.Select(item => item.Row).Distinct().ToArray();
            LinkedWallReference[] references = rows
                .SelectMany(row => row.References)
                .DistinctBy(item => $"{item.RootLinkInstanceId}|{item.LinkPath}|{item.LinkedElementId}")
                .ToArray();
            string[] layerTypes = group
                .Select(item => item.Layer.Name)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .OrderBy(value => value, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
            WallThicknessSpaceItem[] spaces = group
                .GroupBy(item => item.Row.SpaceId)
                .Select(spaceGroup =>
                {
                    ExteriorWallRow row = spaceGroup.First().Row;
                    return new WallThicknessSpaceItem
                    {
                        Row = row,
                        MatchingLayerTypes = string.Join(", ", spaceGroup
                            .Select(item => item.Layer.Name)
                            .Distinct(StringComparer.CurrentCultureIgnoreCase)
                            .OrderBy(value => value, StringComparer.CurrentCultureIgnoreCase))
                    };
                })
                .OrderBy(item => item.Level, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(item => item.SpaceNumber, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
            _thicknessGroups.Add(new WallThicknessGroup
            {
                ThicknessMm = group.Key,
                LayerTypes = string.Join(", ", layerTypes),
                LayerTypeCount = layerTypes.Length,
                Rows = rows,
                References = references,
                Spaces = spaces
            });
        }
        Find<TextBlock>("thickness_group_count_text").Text =
            $"{_thicknessGroups.Count:N0} thickness group(s)";
        RebuildThicknessSpaces();
    }

    private void RebuildThicknessSpaces()
    {
        _thicknessSpaces.Clear();
        WallThicknessGroup[] selectedGroups = Find<DataGrid>("thickness_groups_grid")
            .SelectedItems
            .Cast<WallThicknessGroup>()
            .ToArray();
        IEnumerable<WallThicknessSpaceItem> source = selectedGroups.Length > 0
            ? selectedGroups.SelectMany(group => group.Spaces)
            : [];
        foreach (WallThicknessSpaceItem item in source
            .DistinctBy(item => $"{item.Row.SpaceId}|{item.MatchingLayerTypes}"))
            _thicknessSpaces.Add(item);
        Find<TextBlock>("thickness_space_count_text").Text =
            selectedGroups.Length == 0
                ? "Select a thickness group above"
                : $"{_thicknessSpaces.Count:N0} Space(s)";
    }

    private void GlobalEwaModeChanged()
    {
        if (_updatingGlobalEwaUi) return;
        _oneComponentForAllEwa = Find<CheckBox>("global_ewa_checkbox").IsChecked == true;
        if (_oneComponentForAllEwa)
            Find<TabControl>("linear_kind_tabs").SelectedIndex = 0;
        ApplyGlobalEwaTarget();
        UpdateGlobalEwaControls();
        RebuildLinearPlans();
        SetStatus(
            _oneComponentForAllEwa
                ? "ONE COMPONENT FOR ALL EWA is active. Enter a LINEAR component such as B533."
                : "Global EWA mode is off; exterior wall constructions are listed separately.",
            _oneComponentForAllEwa ? "#14966A" : "#657187");
    }

    private void GlobalEwaComponentChanged()
    {
        if (_updatingGlobalEwaUi) return;
        _globalEwaComponent = Find<WpfTextBox>("global_ewa_component_textbox").Text.Trim();
        if (!_oneComponentForAllEwa) return;
        ApplyGlobalEwaTarget();
        RebuildLinearFaces();
    }

    private void ApplyGlobalEwaTarget()
    {
        if (!_oneComponentForAllEwa) return;
        _applyingGlobalEwaTarget = true;
        try
        {
            foreach (ReplacementBatch batch in _batches.Where(batch =>
                batch.LinearCategory == LinearComponentKinds.ExteriorWall))
                batch.TargetLinear = _globalEwaComponent;
        }
        finally
        {
            _applyingGlobalEwaTarget = false;
        }
        foreach (LinearBatchPlan plan in _linearPlans.Where(plan => plan.IsGlobalEwa))
            plan.NotifyTargetChanged();
    }

    private void UpdateGlobalEwaControls()
    {
        _updatingGlobalEwaUi = true;
        try
        {
            Find<CheckBox>("global_ewa_checkbox").IsChecked = _oneComponentForAllEwa;
            WpfTextBox target = Find<WpfTextBox>("global_ewa_component_textbox");
            target.IsEnabled = _oneComponentForAllEwa;
            if (!string.Equals(target.Text, _globalEwaComponent, StringComparison.Ordinal))
                target.Text = _globalEwaComponent;
        }
        finally
        {
            _updatingGlobalEwaUi = false;
        }
    }

    private void RebuildLinearPlans()
    {
        _linearPlans.Clear();
        string linearCategory = SelectedLinearCategory();
        bool globalEwa = linearCategory == LinearComponentKinds.ExteriorWall && _oneComponentForAllEwa;
        IEnumerable<ExteriorWallRow> scopedRows = globalEwa
            ? _rows
            : _rowView?.Cast<ExteriorWallRow>() ?? [];
        ExteriorWallRow[] visibleRows = scopedRows
            .Where(row => row.LinearCategory == linearCategory)
            .ToArray();
        Dictionary<string, ReplacementBatch> batchByAssembly = _batches.ToDictionary(
            batch => $"{batch.LinearCategory}|{batch.AssemblyKey}",
            StringComparer.OrdinalIgnoreCase);
        var structureCounts = visibleRows
            .GroupBy(row => $"{row.SpaceId}|{row.Orientation}", StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Select(row => batchByAssembly.TryGetValue(
                        $"{row.LinearCategory}|{row.AssemblyKey}", out ReplacementBatch? batch)
                        ? LinearPlanSignature(batch, [row])
                        : row.AssemblyKey)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count(),
                StringComparer.OrdinalIgnoreCase);

        var visibleBatches = _batches
            .Where(batch => batch.LinearCategory == linearCategory)
            .Select(batch => new
            {
                Batch = batch,
                Rows = globalEwa ? batch.Rows.ToArray() : batch.Rows.Where(FilterRow).ToArray()
            })
            .Where(item => item.Rows.Length > 0)
            .GroupBy(item => globalEwa ? "GLOBAL-EWA" : LinearPlanSignature(item.Batch, item.Rows))
            .OrderByDescending(group => group.Sum(item => item.Rows.Sum(row => row.ExteriorAreaM2)))
            .ToArray();
        int planIndex = 1;
        foreach (var batchGroup in visibleBatches)
        {
            ReplacementBatch[] batches = batchGroup.Select(item => item.Batch).ToArray();
            ExteriorWallRow[] rows = batchGroup.SelectMany(item => item.Rows).Distinct().ToArray();
            string planId = globalEwa ? "EWA-ALL" : $"L-{planIndex:00}";
            LinearRoomFaceItem[] faces = rows
                .GroupBy(row => new
                {
                    row.SpaceId,
                    row.Level,
                    row.SpaceNumber,
                    row.SpaceName,
                    row.Orientation
                })
                .Select(group => new LinearRoomFaceItem
                {
                    BatchId = planId,
                    LinearCategory = linearCategory,
                    IsGlobalEwa = globalEwa,
                    SpaceId = group.Key.SpaceId,
                    Level = group.Key.Level,
                    SpaceNumber = group.Key.SpaceNumber,
                    SpaceName = group.Key.SpaceName,
                    Orientation = group.Key.Orientation,
                    ExteriorAreaM2 = group.Sum(row => row.ExteriorAreaM2),
                    StructureCount = structureCounts.GetValueOrDefault(
                        $"{group.Key.SpaceId}|{group.Key.Orientation}", 1),
                    Rows = group.ToArray()
                })
                .OrderBy(face => face.Level, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(face => face.SpaceNumber, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(face => face.Orientation, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            _linearPlans.Add(new LinearBatchPlan
            {
                PlanId = planId,
                LinearCategory = linearCategory,
                IsGlobalEwa = globalEwa,
                Batches = batches,
                Rows = rows,
                Faces = faces
            });
            planIndex++;
        }

        Find<TextBlock>("linear_plan_count_text").Text =
            globalEwa
                ? $"EWA: 1 global component / {_linearPlans.Sum(plan => plan.RoomFaceCount):N0} room face(s)"
                : $"{linearCategory}: {_linearPlans.Count:N0} component batch(es) / {_linearPlans.Sum(plan => plan.RoomFaceCount):N0} room face(s)";
        RebuildLinearFaces();
    }

    private string SelectedLinearCategory() => Find<TabControl>("linear_kind_tabs").SelectedIndex switch
    {
        1 => LinearComponentKinds.ExteriorWindow,
        2 => LinearComponentKinds.InteriorWall,
        3 => LinearComponentKinds.InteriorWindow,
        4 => LinearComponentKinds.ExteriorDoor,
        5 => LinearComponentKinds.InteriorDoor,
        _ => LinearComponentKinds.ExteriorWall
    };

    private static string LinearPlanSignature(ReplacementBatch batch, IReadOnlyList<ExteriorWallRow> rows)
    {
        IReadOnlyList<ExteriorWallLayerItem> layers = batch.RecommendedLayers.Count > 0
            ? batch.RecommendedLayers
            : batch.Layers;
        string layerSignature = string.Join("|", layers.Select(layer =>
            $"{layer.Name.Trim().ToUpperInvariant()}:{Math.Round(layer.ThicknessMm, 0):0}"));
        double totalThickness = rows.Count == 0 ? 0 : rows.Max(row => row.ThicknessMm);
        return $"{batch.LinearCategory}|{Math.Round(totalThickness, 0):0}|{layerSignature}";
    }

    private void RebuildLinearFaces()
    {
        _linearFaces.Clear();
        LinearBatchPlan[] selectedPlans = Find<DataGrid>("linear_plan_grid")
            .SelectedItems
            .Cast<LinearBatchPlan>()
            .ToArray();
        foreach (LinearRoomFaceItem face in selectedPlans
            .SelectMany(plan => plan.Faces)
            .OrderBy(face => face.BatchId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(face => face.Level, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(face => face.SpaceNumber, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(face => face.Orientation, StringComparer.OrdinalIgnoreCase))
            _linearFaces.Add(face);

        Find<TextBlock>("linear_face_count_text").Text = selectedPlans.Length == 0
            ? "Select a component batch above"
            : $"{_linearFaces.Count:N0} expected LINEAR room face(s)";
        Find<TextBlock>("linear_find_replace_text").Text = selectedPlans.Length == 0
            ? "Select a batch to generate the LINEAR Find & Replace instructions."
            : string.Join(Environment.NewLine, selectedPlans.Select(plan => $"{plan.BatchId}: {plan.FindReplaceSummary}"));
    }

    private void CopyLinearFindReplacePlan()
    {
        LinearBatchPlan[] selectedPlans = Find<DataGrid>("linear_plan_grid")
            .SelectedItems
            .Cast<LinearBatchPlan>()
            .ToArray();
        if (selectedPlans.Length == 0) selectedPlans = _linearPlans.ToArray();
        if (selectedPlans.Length == 0)
        {
            SetStatus("Scan exterior walls before copying a LINEAR plan.", "#B3261E");
            return;
        }

        var text = new System.Text.StringBuilder();
        text.AppendLine("LINEAR FIND & REPLACE PLAN");
        text.AppendLine("Replace field: U-value (select the component Bxxx/Fxxx from Material Table)");
        text.AppendLine("Do not replace Component Type unless an Interior/Exterior classification is wrong.");
        foreach (LinearBatchPlan plan in selectedPlans)
        {
            text.AppendLine();
            text.AppendLine($"{plan.LinearCategory} | {plan.BatchId} | {plan.TotalThicknessDisplay} | Target: " +
                            $"{(string.IsNullOrWhiteSpace(plan.TargetLinear) ? "<select Bxxx>" : plan.TargetLinear)} | " +
                            $"{plan.RoomFaceCount} room face(s) | {plan.Status}");
            text.AppendLine(plan.ComponentProposal);
            if (plan.IsGlobalEwa)
            {
                text.AppendLine($"  Project scope: Component Type=Exterior Wall AND Adjoining=Exterior; " +
                                $"Replace U-value={plan.TargetLinear}; expected {plan.RoomFaceCount} EWA room face(s). " +
                                "No thickness or orientation filter is required.");
                foreach (LinearRoomFaceItem face in plan.Faces)
                    text.AppendLine($"    - {face.Level} | {face.SpaceDisplay} | {face.Orientation} | area {face.ExteriorAreaDisplay} m2");
                continue;
            }
            foreach (IGrouping<(string Level, string Orientation), LinearRoomFaceItem> scope in plan.Faces
                .GroupBy(face => (face.Level, face.Orientation))
                .OrderBy(group => group.Key.Level, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(group => group.Key.Orientation, StringComparer.OrdinalIgnoreCase))
            {
                string componentType = LinearComponentKinds.DisplayName(plan.LinearCategory);
                string adjoining = LinearComponentKinds.IsExterior(plan.LinearCategory)
                    ? "Exterior"
                    : "Adjacent Room";
                text.AppendLine($"  Scope {scope.Key.Level} / {scope.Key.Orientation}: " +
                                $"Component Type={componentType} AND Adjoining={adjoining}; " +
                                $"expected {scope.Count()} hit(s)");
                foreach (LinearRoomFaceItem face in scope)
                    text.AppendLine($"    - {face.SpaceDisplay} | area {face.ExteriorAreaDisplay} m2 | {face.MatchStatus}");
            }
        }
        Clipboard.SetText(text.ToString());
        SetStatus($"Copied {selectedPlans.Length:N0} LINEAR Find & Replace batch(es) to the clipboard.", "#14966A");
    }

    private void PopulateFilters()
    {
        PopulateNavigator();
        SetComboItems(Find<WpfComboBox>("orientation_filter_combo"), _rows.Select(row => row.Orientation));
        SetComboItems(Find<WpfComboBox>("status_filter_combo"), _rows.Select(row => row.Status));
    }

    private void PopulateNavigator()
    {
        TreeView tree = Find<TreeView>("space_navigator");
        tree.Items.Clear();
        var allItem = new TreeViewItem
        {
            Header = $"ALL LEVELS  ({_rows.Select(row => row.Level).Distinct().Count():N0})",
            Tag = new NavigatorSelection(null),
            IsExpanded = true
        };
        tree.Items.Add(allItem);

        foreach (IGrouping<string, ExteriorWallRow> level in _rows
            .GroupBy(row => row.Level)
            .OrderBy(group => group.Key, StringComparer.CurrentCultureIgnoreCase))
        {
            int spaceCount = level.Select(row => row.SpaceId).Distinct().Count();
            var levelItem = new TreeViewItem
            {
                Header = $"{level.Key}  ({spaceCount:N0} spaces)",
                Tag = new NavigatorSelection(level.Key)
            };
            tree.Items.Add(levelItem);
        }
        _navigatorLevel = null;
        allItem.IsSelected = true;
    }

    private void NavigatorSelectionChanged()
    {
        if (Find<TreeView>("space_navigator").SelectedItem is not TreeViewItem item ||
            item.Tag is not NavigatorSelection selection)
            return;
        _navigatorLevel = selection.Level;
        RefreshFilter();
        string scope = selection.Level ?? "all levels";
        SetStatus($"Showing {scope}.", "#1155CC");
    }

    private static void SetComboItems(WpfComboBox combo, IEnumerable<string> values)
    {
        string[] items = new[] { All }
            .Concat(values.Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .OrderBy(value => value, StringComparer.CurrentCultureIgnoreCase))
            .ToArray();
        combo.ItemsSource = items;
        combo.SelectedIndex = 0;
    }

    private bool FilterRow(object item)
    {
        if (item is not ExteriorWallRow row) return false;
        return (_navigatorLevel is null || string.Equals(_navigatorLevel, row.Level, StringComparison.CurrentCultureIgnoreCase)) &&
            Matches(Find<WpfComboBox>("orientation_filter_combo"), row.Orientation) &&
            Matches(Find<WpfComboBox>("status_filter_combo"), row.Status);
    }

    private static bool Matches(WpfComboBox combo, string value)
    {
        string selected = combo.SelectedItem as string ?? All;
        return selected == All || string.Equals(selected, value, StringComparison.CurrentCultureIgnoreCase);
    }

    private void RefreshFilter()
    {
        _rowView?.Refresh();
        RebuildRoomOrientationSummaries();
        RebuildDetailOccurrences();
        RebuildArchitecturalWalls();
        RebuildThicknessGroups();
        RebuildLinearPlans();
        RebuildRoomWallInventory();
        RebuildWallTypeGroups();
        UpdateVisibleCount();
    }

    private void RebuildWallTypeGroups()
    {
        ExteriorWallRow[] visibleRows = _rowView?.Cast<ExteriorWallRow>().ToArray() ?? [];
        ExteriorWallRow[] openings = visibleRows
            .Where(row => LinearComponentKinds.IsOpening(row.LinearCategory))
            .ToArray();
        var grouped = visibleRows
            .Where(row => row.LinearCategory is LinearComponentKinds.ExteriorWall or LinearComponentKinds.InteriorWall)
            .GroupBy(row => new
            {
                row.LinearCategory,
                Thickness = Math.Round(row.ThicknessMm, 1),
                Materials = WallMaterialSignature(row)
            })
            .OrderBy(group => group.Key.LinearCategory, StringComparer.OrdinalIgnoreCase)
            .ThenBy(group => group.Key.Thickness)
            .ThenBy(group => group.Key.Materials, StringComparer.CurrentCultureIgnoreCase)
            .Select((group, index) =>
            {
                ExteriorWallRow[] walls = group.ToArray();
                ExteriorWallRow[] associated = openings
                    .Where(opening => walls.Any(wall => OpeningMatchesWall(wall, opening)))
                    .Distinct()
                    .ToArray();
                return new WallTypeGroupItem
                {
                    GroupId = $"WG-{index + 1:000}",
                    Rows = walls,
                    AssociatedOpenings = associated
                };
            })
            .ToArray();

        string? selectedId = (Find<DataGrid>("wall_type_groups_grid").SelectedItem as WallTypeGroupItem)?.GroupId;
        _wallTypeGroups.Clear();
        foreach (WallTypeGroupItem item in grouped) _wallTypeGroups.Add(item);
        Find<DataGrid>("wall_type_groups_grid").SelectedItem = grouped.FirstOrDefault(item => item.GroupId == selectedId)
            ?? grouped.FirstOrDefault();
        Find<TextBlock>("wall_type_group_count_text").Text =
            $"{grouped.Length:N0} matching material/thickness group(s)";
        RebuildWallTypeOccurrences();
    }

    private void RebuildWallTypeOccurrences()
    {
        _wallTypeOccurrences.Clear();
        if (Find<DataGrid>("wall_type_groups_grid").SelectedItem is not WallTypeGroupItem group)
        {
            Find<TextBlock>("wall_type_occurrence_count_text").Text = "Select a wall group above";
            return;
        }
        foreach (ExteriorWallRow wall in group.Rows
                     .OrderBy(row => row.Level, StringComparer.CurrentCultureIgnoreCase)
                     .ThenBy(row => row.SpaceNumber, StringComparer.CurrentCultureIgnoreCase)
                     .ThenBy(row => row.Orientation, StringComparer.OrdinalIgnoreCase))
        {
            ExteriorWallRow[] associated = group.AssociatedOpenings
                .Where(opening => OpeningMatchesWall(wall, opening))
                .ToArray();
            _wallTypeOccurrences.Add(new WallTypeOccurrenceItem
            {
                Wall = wall,
                AssociatedOpenings = associated
            });
        }
        Find<TextBlock>("wall_type_occurrence_count_text").Text =
            $"{group.GroupId} · {_wallTypeOccurrences.Count:N0} room occurrence(s) · {group.OpeningCount:N0} associated opening element(s)";
    }

    private static string WallMaterialSignature(ExteriorWallRow row) => row.Layers.Count == 0
        ? $"{NormalizeGroupText(row.LayerDescription)}|{Math.Round(row.ThicknessMm, 1):0.0}"
        : string.Join("|", row.Layers
            .OrderBy(layer => layer.Rank)
            .Select(layer => $"{NormalizeGroupText(layer.Name)}:{Math.Round(layer.ThicknessMm, 1):0.0}"));

    private static string NormalizeGroupText(string value) =>
        string.Join(" ", (value ?? string.Empty).Trim().ToUpperInvariant()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static bool OpeningMatchesWall(ExteriorWallRow wall, ExteriorWallRow opening)
    {
        if (wall.SpaceId != opening.SpaceId ||
            !string.Equals(wall.Orientation, opening.Orientation, StringComparison.OrdinalIgnoreCase))
            return false;
        if (wall.References.Count == 0 || opening.References.Count == 0) return true;
        double tolerance = UnitUtils.ConvertToInternalUnits(150.0, UnitTypeId.Millimeters);
        return wall.References.Any(wallReference => opening.References.Any(openingReference =>
            wallReference.RootLinkInstanceId == openingReference.RootLinkInstanceId &&
            wallReference.MinX <= openingReference.MaxX + tolerance &&
            wallReference.MaxX >= openingReference.MinX - tolerance &&
            wallReference.MinY <= openingReference.MaxY + tolerance &&
            wallReference.MaxY >= openingReference.MinY - tolerance &&
            wallReference.MinZ <= openingReference.MaxZ + tolerance &&
            wallReference.MaxZ >= openingReference.MinZ - tolerance));
    }

    private void RebuildRoomWallInventory()
    {
        if (_rowView is null) return;
        int scope = Find<WpfComboBox>("room_wall_scope_combo").SelectedIndex;
        ExteriorWallRow[] wallRows = _rowView.Cast<ExteriorWallRow>().ToArray();
        RoomWallRoomSummary[] rooms = wallRows
            .GroupBy(row => (row.SpaceId, row.Level, row.SpaceNumber, row.SpaceName))
            .Select(group => new RoomWallRoomSummary
            {
                SpaceId = group.Key.SpaceId,
                Level = group.Key.Level,
                SpaceNumber = group.Key.SpaceNumber,
                SpaceName = group.Key.SpaceName,
                Rows = group.ToArray()
            })
            .Where(room => scope switch
            {
                1 => room.HasEwa,
                2 => !room.HasEwa,
                _ => true
            })
            .OrderBy(room => room.Level, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(room => room.SpaceNumber, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        _updatingRoomWallInventory = true;
        try
        {
            _roomWallRooms.Clear();
            foreach (RoomWallRoomSummary room in rooms) _roomWallRooms.Add(room);
            RoomWallRoomSummary? selected = rooms.FirstOrDefault(room =>
                string.Equals(room.SpaceKey, _selectedRoomWallSpaceKey, StringComparison.OrdinalIgnoreCase))
                ?? rooms.FirstOrDefault();
            Find<DataGrid>("room_wall_rooms_grid").SelectedItem = selected;
            _selectedRoomWallSpaceKey = selected?.SpaceKey;
        }
        finally
        {
            _updatingRoomWallInventory = false;
        }

        int ewaRooms = rooms.Count(room => room.HasEwa);
        Find<TextBlock>("room_wall_room_count_text").Text =
            $"{rooms.Length:N0} room(s) · {ewaRooms:N0} with EWA";
        RebuildRoomWallInventoryDetails();
    }

    private void RoomWallRoomSelectionChanged()
    {
        if (_updatingRoomWallInventory) return;
        _selectedRoomWallSpaceKey = (Find<DataGrid>("room_wall_rooms_grid").SelectedItem as RoomWallRoomSummary)?.SpaceKey;
        RebuildRoomWallInventoryDetails();
        UpdateSelectionStatus();
    }

    private void RebuildRoomWallInventoryDetails()
    {
        RoomWallRoomSummary? room = Find<DataGrid>("room_wall_rooms_grid").SelectedItem as RoomWallRoomSummary;
        int typeFilter = Find<WpfComboBox>("room_wall_type_combo").SelectedIndex;
        RoomWallInventoryItem[] items = (room?.Rows ?? [])
            .Select(row => new RoomWallInventoryItem { Row = row })
            .Where(item => typeFilter switch
            {
                1 => item.BoundaryCode == LinearComponentKinds.ExteriorWall,
                2 => item.BoundaryCode == LinearComponentKinds.InteriorWall,
                3 => item.BoundaryCode == LinearComponentKinds.ExteriorWindow,
                4 => item.BoundaryCode == LinearComponentKinds.InteriorWindow,
                5 => item.BoundaryCode == LinearComponentKinds.ExteriorDoor,
                6 => item.BoundaryCode == LinearComponentKinds.InteriorDoor,
                _ => true
            })
            .OrderBy(item => item.BoundaryCode, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Orientation, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.ThicknessMm)
            .ToArray();

        _roomWallInventory.Clear();
        foreach (RoomWallInventoryItem item in items) _roomWallInventory.Add(item);
        _roomWallLayers.Clear();
        Find<TextBlock>("room_wall_layer_title").Text = "SELECT A WALL ASSEMBLY ABOVE";
        Find<TextBlock>("room_wall_inventory_count_text").Text = room is null
            ? "Select a room above"
            : $"{room.SpaceNumber} · EWA {room.EwaSideCount:N0}, IWA {room.IwaSideCount:N0}, openings {room.OpeningCount:N0} · {items.Length:N0} component row(s) shown";
    }

    private void RebuildSelectedRoomWallLayers()
    {
        _roomWallLayers.Clear();
        if (Find<DataGrid>("room_wall_inventory_grid").SelectedItem is not RoomWallInventoryItem item)
        {
            Find<TextBlock>("room_wall_layer_title").Text = "SELECT A WALL ASSEMBLY ABOVE";
            return;
        }

        foreach (ExteriorWallLayerItem layer in item.Row.Layers.OrderBy(layer => layer.Rank))
            _roomWallLayers.Add(layer);
        Find<TextBlock>("room_wall_layer_title").Text =
            $"{item.SpaceNumber} · {item.BoundaryCode} {item.Orientation} · {item.Thickness} · {item.LayerCount:N0} LAYER(S)";
    }

    private void ClearFilters()
    {
        foreach (WpfComboBox combo in FilterCombos()) combo.SelectedIndex = 0;
        Find<WpfComboBox>("room_wall_scope_combo").SelectedIndex = 0;
        Find<WpfComboBox>("room_wall_type_combo").SelectedIndex = 0;
        _navigatorLevel = null;
        if (Find<TreeView>("space_navigator").Items.Count > 0 &&
            Find<TreeView>("space_navigator").Items[0] is TreeViewItem allItem)
            allItem.IsSelected = true;
        RefreshFilter();
    }

    private IEnumerable<WpfComboBox> FilterCombos()
    {
        yield return Find<WpfComboBox>("orientation_filter_combo");
        yield return Find<WpfComboBox>("status_filter_combo");
    }

    private void UpdateMetrics()
    {
        double totalArea = _rows
            .Where(row => row.LinearCategory == LinearComponentKinds.ExteriorWall)
            .Sum(row => row.ExteriorAreaM2);
        int reviewCount = _rows.Count(row => row.Status != "Ready");
        Find<TextBlock>("total_area_text").Text = $"{totalArea:N2} m²";
        Find<TextBlock>("unique_assemblies_text").Text = _batches.Count.ToString("N0");
        Find<TextBlock>("needs_review_text").Text = reviewCount == 0
            ? "0"
            : $"{reviewCount:N0} ({reviewCount * 100.0 / Math.Max(_rows.Count, 1):0.#}%)";
        int loaded = _lastScan?.LoadedLinkCount ?? 0;
        int total = _lastScan?.TotalLinkCount ?? 0;
        Find<TextBlock>("links_scanned_text").Text = $"{loaded} / {total}";
        Find<TextBlock>("links_scanned_header_text").Text = $"{loaded} / {total}";
        TextBlock health = Find<TextBlock>("link_health_text");
        health.Text = total == 0
            ? "●  No links found"
            : loaded == total
                ? "●  All links loaded"
                : $"●  {total - loaded} link(s) unloaded";
        health.Foreground = Brush(total > 0 && loaded == total ? "#14966A" : "#F2A11B");
        UpdateVisibleCount();
    }

    private void UpdateVisibleCount()
    {
        int visible = _rowView?.Cast<object>().Count() ?? 0;
        Find<TextBlock>("row_count_text").Text =
            $"{_roomSummaries.Count:N0} room/orientation · {visible:N0} LINEAR component occurrence(s)";
    }

    private void UpdateSelectionStatus()
    {
        int activeTab = Find<TabControl>("results_tabs").SelectedIndex;
        int detailTab = Find<TabControl>("detail_tabs").SelectedIndex;
        int summaryCount = Find<DataGrid>("room_summary_grid").SelectedItems.Count;
        int rowCount = Find<DataGrid>("walls_grid").SelectedItems.Count;
        int batchCount = Find<DataGrid>("batches_grid").SelectedItems.Count;
        int architecturalCount = Find<DataGrid>("architectural_walls_grid").SelectedItems.Count;
        int thicknessCount = Find<DataGrid>("thickness_groups_grid").SelectedItems.Count;
        int thicknessSpaceCount = Find<DataGrid>("thickness_spaces_grid").SelectedItems.Count;
        int linearPlanCount = Find<DataGrid>("linear_plan_grid").SelectedItems.Count;
        int linearFaceCount = Find<DataGrid>("linear_faces_grid").SelectedItems.Count;
        int roomWallCount = Find<DataGrid>("room_wall_inventory_grid").SelectedItems.Count;
        int wallTypeGroupCount = Find<DataGrid>("wall_type_groups_grid").SelectedItems.Count;
        string? message = activeTab switch
        {
            0 when summaryCount > 0 => $"Selected {summaryCount:N0} Room/Orientation scope(s). Open LINEAR BATCH PLAN to prepare the filtered Find & Replace scope.",
            1 when detailTab == 0 && batchCount + rowCount > 0 => $"Selected {batchCount:N0} structure group(s) and {rowCount:N0} occurrence(s).",
            1 when detailTab == 1 && architecturalCount > 0 => $"Selected {architecturalCount:N0} architectural type group(s).",
            1 when detailTab == 2 && thicknessCount > 0 => $"Selected {thicknessCount:N0} thickness group(s), covering {thicknessSpaceCount:N0} selected Space row(s).",
            2 when linearPlanCount > 0 => $"Selected {linearPlanCount:N0} {SelectedLinearCategory()} component batch(es), covering {_linearFaces.Count:N0} expected room face(s); {linearFaceCount:N0} face row(s) selected.",
            3 when roomWallCount > 0 => $"Selected {roomWallCount:N0} room-wall assembly row(s). Black rows identify walls shared by two Spaces.",
            4 when wallTypeGroupCount > 0 => $"Selected {wallTypeGroupCount:N0} grouped wall type(s), covering {_wallTypeOccurrences.Count:N0} Space occurrence(s).",
            _ => null
        };
        if (message is not null) SetStatus(message, "#657187");
    }

    private void ExportExcel()
    {
        if (_rows.Count == 0)
        {
            SetStatus("Scan exterior walls before exporting Excel.", "#B3261E");
            return;
        }
        var dialog = new SaveFileDialog
        {
            Title = "Export Exterior Wall Mapper workbook",
            Filter = "Excel workbook (*.xlsx)|*.xlsx",
            FileName = $"Exterior-Wall-Mapper-{DateTime.Now:yyyyMMdd-HHmm}.xlsx",
            AddExtension = true,
            DefaultExt = ".xlsx"
        };
        if (dialog.ShowDialog(_window) != true) return;
        try
        {
            ExteriorWallExcelExporter.Export(dialog.FileName, _rows, _batches, _linearPlans, _warnings);
            SetStatus($"Excel workbook exported: {dialog.FileName}", "#14966A");
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private void HighlightExteriorWalls()
    {
        // Level -> Space and the two combo filters define the visual verification
        // scope. A stale grid selection must never make an "all exterior walls"
        // action unexpectedly highlight only one assembly.
        RoomOrientationSummary[] selectedSummaries = Find<DataGrid>("room_summary_grid")
            .SelectedItems
            .Cast<RoomOrientationSummary>()
            .ToArray();
        ReplacementBatch[] selectedBatches = Find<DataGrid>("batches_grid")
            .SelectedItems
            .Cast<ReplacementBatch>()
            .ToArray();
        ArchitecturalWallTypeGroup[] selectedArchitecturalWalls = Find<DataGrid>("architectural_walls_grid")
            .SelectedItems
            .Cast<ArchitecturalWallTypeGroup>()
            .ToArray();
        WallThicknessGroup[] selectedThicknessGroups = Find<DataGrid>("thickness_groups_grid")
            .SelectedItems
            .Cast<WallThicknessGroup>()
            .ToArray();
        WallThicknessSpaceItem[] selectedThicknessSpaces = Find<DataGrid>("thickness_spaces_grid")
            .SelectedItems
            .Cast<WallThicknessSpaceItem>()
            .ToArray();
        ExteriorWallRow[] selectedDetailRows = Find<DataGrid>("walls_grid")
            .SelectedItems
            .Cast<ExteriorWallRow>()
            .ToArray();
        LinearBatchPlan[] selectedLinearPlans = Find<DataGrid>("linear_plan_grid")
            .SelectedItems
            .Cast<LinearBatchPlan>()
            .ToArray();
        LinearRoomFaceItem[] selectedLinearFaces = Find<DataGrid>("linear_faces_grid")
            .SelectedItems
            .Cast<LinearRoomFaceItem>()
            .ToArray();
        int activeTab = Find<TabControl>("results_tabs").SelectedIndex;
        int detailTab = Find<TabControl>("detail_tabs").SelectedIndex;
        List<ExteriorWallRow> visibleRows;
        LinkedWallReference[] explicitReferences = [];
        if (activeTab == 0)
        {
            visibleRows = selectedSummaries.Length > 0
                ? selectedSummaries.SelectMany(summary => summary.Rows).Distinct().ToList()
                : _rowView?.Cast<ExteriorWallRow>().ToList() ?? [];
        }
        else if (activeTab == 1)
        {
            if (detailTab == 0)
            {
                visibleRows = selectedDetailRows.Length > 0
                    ? selectedDetailRows.Distinct().ToList()
                    : selectedBatches.Length > 0
                        ? selectedBatches.SelectMany(batch => batch.Rows).Distinct().ToList()
                        : _detailRows.ToList();
            }
            else if (detailTab == 1)
            {
                visibleRows = selectedArchitecturalWalls.Length > 0
                    ? selectedArchitecturalWalls.SelectMany(item => item.Rows).Distinct().ToList()
                    : _rowView?.Cast<ExteriorWallRow>().ToList() ?? [];
                if (selectedArchitecturalWalls.Length > 0)
                {
                    explicitReferences = selectedArchitecturalWalls
                        .SelectMany(item => item.References)
                        .DistinctBy(item => $"{item.RootLinkInstanceId}|{item.LinkPath}|{item.LinkedElementId}")
                        .ToArray();
                }
            }
            else
            {
                visibleRows = selectedThicknessSpaces.Length > 0
                    ? selectedThicknessSpaces.Select(item => item.Row).Distinct().ToList()
                    : selectedThicknessGroups.Length > 0
                        ? selectedThicknessGroups.SelectMany(group => group.Rows).Distinct().ToList()
                        : _rowView?.Cast<ExteriorWallRow>().ToList() ?? [];
                if (selectedThicknessSpaces.Length > 0)
                {
                    explicitReferences = selectedThicknessSpaces
                        .SelectMany(item => item.Row.References)
                        .DistinctBy(item => $"{item.RootLinkInstanceId}|{item.LinkPath}|{item.LinkedElementId}")
                        .ToArray();
                }
                else if (selectedThicknessGroups.Length > 0)
                {
                    explicitReferences = selectedThicknessGroups
                        .SelectMany(group => group.References)
                        .DistinctBy(item => $"{item.RootLinkInstanceId}|{item.LinkPath}|{item.LinkedElementId}")
                        .ToArray();
                }
            }
        }
        else
        {
            visibleRows = selectedLinearFaces.Length > 0
                ? selectedLinearFaces.SelectMany(face => face.Rows).Distinct().ToList()
                : selectedLinearPlans.Length > 0
                    ? selectedLinearPlans.SelectMany(plan => plan.Rows).Distinct().ToList()
                    : (_rowView?.Cast<ExteriorWallRow>() ?? [])
                        .Where(row => row.LinearCategory == SelectedLinearCategory())
                        .ToList();
        }
        if (visibleRows.Count == 0)
        {
            SetStatus("No matching LINEAR components are available in the current filter scope.", "#B3261E");
            return;
        }

        HighlightOutcome? outcome = null;
        Queue(
            app => outcome = HighlightInRevit(
                app,
                visibleRows,
                explicitReferences),
            $"Selecting linked components from {visibleRows.Count:N0} filtered result row(s)...",
            () =>
            {
                HighlightOutcome value = outcome ?? new HighlightOutcome(0, 0, "No highlight result.");
                SetStatus(value.Message, value.HighlightedReferenceCount > 0 ? "#14966A" : "#B3261E");
            });
    }

    private void RoomWallLayerButtonClicked(object sender, RoutedEventArgs args)
    {
        if (args.OriginalSource is not Button { Tag: ExteriorWallLayerItem layer }) return;
        args.Handled = true;
        Find<WpfTextBox>("element_id_textbox").Text = layer.SourceElementId;
        FocusElementById(layer.SourceElementId, layer.SourceLinkPath);
    }

    private void FocusElementFromInput(bool focusInFloorPlan = false) =>
        FocusElementById(Find<WpfTextBox>("element_id_textbox").Text, null, focusInFloorPlan);

    private void FocusElementById(
        string? rawElementId,
        string? preferredLinkPath,
        bool focusInFloorPlan = false)
    {
        string value = rawElementId?.Trim() ?? string.Empty;
        long[] elementIds = value
            .Split([';', ',', '\r', '\n', '\t', ' '], StringSplitOptions.RemoveEmptyEntries)
            .Select(token => long.TryParse(token.Trim(), out long id) ? id : 0)
            .Where(id => id > 0)
            .Distinct()
            .ToArray();
        if (elementIds.Length == 0)
        {
            SetStatus(
                "Enter one or more positive Element IDs separated by semicolon, comma, space or a new line.",
                "#B3261E");
            Find<WpfTextBox>("element_id_textbox").Focus();
            Find<WpfTextBox>("element_id_textbox").SelectAll();
            return;
        }

        HashSet<long> requestedIds = elementIds.ToHashSet();
        FocusOccurrence[] allMatches = _rows
            .SelectMany(row => row.References
                .Where(reference => requestedIds.Contains(reference.LinkedElementId))
                .Select(reference => new FocusOccurrence(row, reference)))
            .DistinctBy(item => $"{item.Row.SpaceId}|{item.Reference.RootLinkInstanceId}|" +
                $"{item.Reference.LinkPath}|{item.Reference.LinkedElementId}")
            .ToArray();
        if (allMatches.Length == 0)
        {
            SetStatus(
                $"None of the {elementIds.Length:N0} Element ID(s) are in the current scan. " +
                "Run SCAN LINEAR COMPONENTS again or verify the IDs/link file.",
                "#B3261E");
            return;
        }

        FocusOccurrence[] matches = allMatches;
        RoomWallInventoryItem[] selectedWallRows = Find<DataGrid>("room_wall_inventory_grid")
            .SelectedItems
            .Cast<RoomWallInventoryItem>()
            .ToArray();
        // A single-ID focus can use the currently selected room-wall row to
        // disambiguate a shared wall. A pasted group must always show every ID.
        if (elementIds.Length == 1 && selectedWallRows.Length > 0)
        {
            FocusOccurrence[] selectedScope = matches
                .Where(item => selectedWallRows.Any(selected =>
                    selected.Row.SpaceId == item.Row.SpaceId &&
                    string.Equals(selected.Row.AssemblyKey, item.Row.AssemblyKey, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(selected.Row.Orientation, item.Row.Orientation, StringComparison.OrdinalIgnoreCase)))
                .ToArray();
            if (selectedScope.Length > 0) matches = selectedScope;
        }
        if (!string.IsNullOrWhiteSpace(preferredLinkPath))
        {
            FocusOccurrence[] preferred = matches
                .Where(item => string.Equals(
                    item.Reference.LinkPath,
                    preferredLinkPath,
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (preferred.Length > 0) matches = preferred;
        }

        FocusOutcome? outcome = null;
        Queue(
            application => outcome = FocusElementInRevit(application, elementIds, matches, focusInFloorPlan),
            focusInFloorPlan
                ? $"Locating {elementIds.Length:N0} linked Element ID(s) in a Floor Plan..."
                : $"Locating {elementIds.Length:N0} linked Element ID(s) in 3D...",
            () =>
            {
                FocusOutcome valueResult = outcome ?? new FocusOutcome(false, "No focus result.");
                SetStatus(valueResult.Message, valueResult.Success ? "#14966A" : "#B3261E");
            });
    }

    private FocusOutcome FocusElementInRevit(
        UIApplication application,
        IReadOnlyList<long> elementIds,
        IReadOnlyList<FocusOccurrence> occurrences,
        bool focusInFloorPlan)
    {
        UIDocument uidoc = RequireUidoc(application);
        HashSet<long> selectedHostElementIds = uidoc.Selection
            .GetElementIds()
            .Select(id => id.CompatValue())
            .ToHashSet();
        FocusOccurrence[] selectedSpaceMatches = elementIds.Count == 1
            ? occurrences.Where(item => selectedHostElementIds.Contains(item.Row.SpaceId)).ToArray()
            : [];
        FocusOccurrence[] resolvedOccurrences = selectedSpaceMatches.Length > 0
            ? selectedSpaceMatches
            : occurrences.ToArray();
        string[] matchingRooms = resolvedOccurrences
            .Select(item => item.Row.SpaceNumber)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(value => value, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        LinkedWallReference[] uniqueReferences = resolvedOccurrences
            .Select(item => item.Reference)
            // A physical linked wall can bound two Spaces. Those room-face rows
            // must resolve to one source element, not become an ambiguous focus.
            .DistinctBy(reference =>
                $"{reference.RootLinkInstanceId}|{reference.LinkPath}|{reference.LinkedElementId}")
            .ToArray();
        if (uniqueReferences.Length == 0)
            return new FocusOutcome(false, "The requested Element IDs have no resolvable linked geometry.");

        long[] resolvedIds = uniqueReferences
            .Select(reference => reference.LinkedElementId)
            .Distinct()
            .OrderBy(id => id)
            .ToArray();
        long[] missingIds = elementIds.Except(resolvedIds).OrderBy(id => id).ToArray();
        string idLabel = resolvedIds.Length == 1
            ? $"Element ID {resolvedIds[0]}"
            : $"{resolvedIds.Length:N0} Element IDs";

        View focusView;
        ViewPlan? focusPlan = null;
        if (focusInFloorPlan)
        {
            focusPlan = FindMatchingFloorPlan(uidoc, resolvedOccurrences);
            if (focusPlan is null)
                return new FocusOutcome(
                    false,
                    $"No host Floor Plan was found for {idLabel}. Open a Floor Plan on the Space level and try again.");
            focusView = focusPlan;
        }
        else
        {
            focusView = GetOrCreateFocusView(uidoc);
        }
        if (uidoc.ActiveView.Id != focusView.Id) uidoc.ActiveView = focusView;

        var selectableReferences = new List<RevitReference>();
        foreach (LinkedWallReference reference in uniqueReferences)
        {
            // CreateLinkReference across multiple nested links is accepted by
            // some Revit builds but selects only the parent RVT link. Do not show
            // that misleading blue link bounding box; the red transient geometry
            // below identifies the exact nested source element instead.
            if (reference.NestedLinkInstancePath.Count > 0) continue;
            if (_document.GetElement(PortableApi.ElementId(reference.RootLinkInstanceId)) is not RevitLinkInstance link) continue;
            if (TryCreateActualLinkedReference(link, reference, out RevitReference? actualReference))
                selectableReferences.Add(actualReference!);
        }

        using (var transaction = new Transaction(_document, "Exterior Wall Mapper - focus linked element"))
        {
            transaction.Start();
            if (focusView.IsTemporaryHideIsolateActive())
                focusView.DisableTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate);
            DeleteTemporaryPreviewsInOpenTransaction();
            if (focusView is View3D focus3D)
            {
                // The red DirectContext surface is not reliably rendered in a
                // wireframe view. This is a dedicated focus view, so use a shaded
                // style without changing any of the user's working views.
                focus3D.DisplayStyle = DisplayStyle.ShadingWithEdges;
                // FOCUS must retain the complete model context. Older builds isolated
                // the parent link. Keep all nearby model elements visible, but crop
                // tightly around this room occurrence so the requested wall is clear.
                ApplySectionBox(focus3D, uniqueReferences);
                OrientFocusView(focus3D, uniqueReferences);
            }
            else if (_sectionBoxSnapshot is not null)
            {
                RestoreSectionBox();
            }
            RestoreContextTransparencyExceptInOpenTransaction(focusView.Id.CompatValue());
            ApplyContextTransparencyInOpenTransaction(focusView, _contextTransparencyPercent);
            transaction.Commit();
        }
        _isolationViewId = focusView.Id.CompatValue();

        bool submittedLinkedSelection = false;
        if (selectableReferences.Count > 0)
        {
            try
            {
                uidoc.Selection.SetReferences(selectableReferences);
                // GetElementIds() does not report linked references reliably. A
                // successful SetReferences call is the correct success signal.
                submittedLinkedSelection = true;
            }
            catch
            {
                uidoc.Selection.SetElementIds([]);
            }
        }
        else uidoc.Selection.SetElementIds([]);

        // Floor plans need a horizontal cut-plane footprint. Reusing a vertical
        // 3D mesh can be clipped completely by the plan view range, especially
        // for imported IFC DirectShapes. The footprint uses the same exact
        // scanned element bounds that drive the successful 3D focus.
        FocusTriangle[] focusTriangles = focusPlan is not null
            ? CreatePlanFocusTriangles(focusPlan, uniqueReferences).ToArray()
            : ExtractFocusTriangles(uniqueReferences).ToArray();
        bool usedBoundingEnvelope = false;
        if (focusTriangles.Length == 0)
        {
            // Some IFC DirectShape/imported elements can be read during the
            // boundary scan but return an empty GeometryElement when Revit asks
            // for their geometry again from a nested link.  The scan already
            // stored the exact host-coordinate bounds of that source element.
            // Draw those bounds transiently so FOCUS always has an unambiguous
            // red target without creating a proxy element in the Space model.
            focusTriangles = uniqueReferences
                .SelectMany(reference => CreateBoundingBoxFocusTriangles(reference.ToBoundingBox()))
                .ToArray();
            usedBoundingEnvelope = focusTriangles.Length > 0;
        }
        EnsureFocusGraphicsRegistered();
        _focusGraphics?.SetGeometry(focusView, focusTriangles);

        uidoc.RefreshActiveView();
        ZoomToReferences(uidoc, uniqueReferences);
        uidoc.RefreshActiveView();

        string linkLabel = string.Join(", ", uniqueReferences
            .Select(reference => reference.LinkPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.CurrentCultureIgnoreCase));
        string duplicateNote = uniqueReferences.Length > 1
            ? $" {uniqueReferences.Length:N0} matching link occurrence(s) were framed."
            : string.Empty;
        string missingNote = missingIds.Length > 0
            ? $" {missingIds.Length:N0} requested ID(s) were not found in this scan: {string.Join("; ", missingIds)}."
            : string.Empty;
        string selectionNote = focusTriangles.Length > 0
            ? focusPlan is not null
                ? $" The linked element footprint is highlighted red in Floor Plan '{focusPlan.Name}' " +
                  $"({focusTriangles.Length:N0} transient triangles; renderer calls: {_focusGraphics?.RenderCallCount ?? 0}); " +
                  "no element was created in the Space file."
                : usedBoundingEnvelope
                    ? $" The IFC did not expose its mesh a second time, so the scanned source-element envelope is highlighted red " +
                      $"({focusTriangles.Length:N0} transient triangles; renderer calls: {_focusGraphics?.RenderCallCount ?? 0}); " +
                      "no element was created in the Space file."
                    : $" The source geometry is highlighted red directly from the link ({focusTriangles.Length:N0} mesh triangles; " +
                      $"renderer calls: {_focusGraphics?.RenderCallCount ?? 0}); no element was created in the Space file."
            : " Revit did not expose drawable source geometry; the section box still frames its scanned room occurrence.";
        if (!string.IsNullOrWhiteSpace(_focusGraphics?.LastRenderError))
            selectionNote += $" Renderer error: {_focusGraphics.LastRenderError}";
        if (!submittedLinkedSelection)
            selectionNote += " This nested link chain cannot populate Revit Properties automatically; TAB/click the red wall in the view to inspect the linked source.";
        return new FocusOutcome(
            true,
            $"Focused {idLabel}" +
            (resolvedIds.Length == 1 && matchingRooms.Length == 1
                ? $" at room {matchingRooms[0]}"
                : resolvedIds.Length == 1 && matchingRooms.Length > 1
                    ? $" shared by rooms {string.Join(", ", matchingRooms)}"
                    : string.Empty) +
            (linkLabel.Length > 0 ? $" in {linkLabel}." : ".") +
            duplicateNote +
            missingNote +
            selectionNote);
    }

    private View3D GetOrCreateFocusView(UIDocument uidoc)
    {
        View3D? existing = new FilteredElementCollector(_document)
            .OfClass(typeof(View3D))
            .Cast<View3D>()
            .Where(view => !view.IsTemplate && !view.IsPerspective)
            .Where(view => string.Equals(view.Name, FocusViewName, StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();
        if (existing is not null) return existing;

        ViewFamilyType viewType = new FilteredElementCollector(_document)
            .OfClass(typeof(ViewFamilyType))
            .Cast<ViewFamilyType>()
            .First(type => type.ViewFamily == ViewFamily.ThreeDimensional);
        using var transaction = new Transaction(_document, "Exterior Wall Mapper - create focus view");
        transaction.Start();
        View3D created = View3D.CreateIsometric(_document, viewType.Id);
        created.Name = FocusViewName;
        created.DetailLevel = ViewDetailLevel.Fine;
        created.DisplayStyle = DisplayStyle.Shading;
        transaction.Commit();
        return created;
    }

    private ViewPlan? FindMatchingFloorPlan(
        UIDocument uidoc,
        IReadOnlyList<FocusOccurrence> occurrences)
    {
        ElementId targetLevelId = ElementId.InvalidElementId;
        foreach (FocusOccurrence occurrence in occurrences)
        {
            ElementId? levelId = _document.GetElement(PortableApi.ElementId(occurrence.Row.SpaceId))?.LevelId;
            if (levelId is null || levelId == ElementId.InvalidElementId) continue;
            targetLevelId = levelId;
            break;
        }

        static bool IsUsableFloorPlan(ViewPlan view) =>
            !view.IsTemplate &&
            view.ViewType is ViewType.FloorPlan or ViewType.EngineeringPlan;

        if (uidoc.ActiveView is ViewPlan activePlan &&
            IsUsableFloorPlan(activePlan) &&
            (targetLevelId == ElementId.InvalidElementId || activePlan.GenLevel?.Id == targetLevelId))
            return activePlan;

        return new FilteredElementCollector(_document)
            .OfClass(typeof(ViewPlan))
            .Cast<ViewPlan>()
            .Where(IsUsableFloorPlan)
            .Where(view => targetLevelId == ElementId.InvalidElementId || view.GenLevel?.Id == targetLevelId)
            .OrderByDescending(view => view.Name.Contains("Space", StringComparison.OrdinalIgnoreCase))
            .ThenBy(view => view.Name, StringComparer.CurrentCultureIgnoreCase)
            .FirstOrDefault();
    }

    private static void OrientFocusView(
        View3D view,
        IReadOnlyList<LinkedWallReference> references)
    {
        double minX = references.Min(item => item.MinX);
        double minY = references.Min(item => item.MinY);
        double minZ = references.Min(item => item.MinZ);
        double maxX = references.Max(item => item.MaxX);
        double maxY = references.Max(item => item.MaxY);
        double maxZ = references.Max(item => item.MaxZ);
        XYZ center = new(
            (minX + maxX) * 0.5,
            (minY + maxY) * 0.5,
            (minZ + maxZ) * 0.5);
        double diagonal = new XYZ(maxX - minX, maxY - minY, maxZ - minZ).GetLength();
        double minimumDistance = UnitUtils.ConvertToInternalUnits(6000.0, UnitTypeId.Millimeters);
        double distance = Math.Max(minimumDistance, diagonal * 3.0);

        XYZ forward = new XYZ(1.0, 1.0, -0.72).Normalize();
        XYZ right = forward.CrossProduct(XYZ.BasisZ).Normalize();
        XYZ up = right.CrossProduct(forward).Normalize();
        XYZ eye = center - (forward * distance);
        view.SetOrientation(new ViewOrientation3D(eye, up, forward));
    }

    private HighlightOutcome HighlightInRevit(
        UIApplication application,
        IReadOnlyList<ExteriorWallRow> rows,
        IReadOnlyList<LinkedWallReference> explicitlySelectedReferences)
    {
        UIDocument uidoc = RequireUidoc(application);
        // Migrate the model away from the old host-Generic-Model workflow before
        // selecting anything. New builds never create replacement geometry.
        DeleteLegacyPreviewsIfPresent();
        IEnumerable<LinkedWallReference> referenceSource = explicitlySelectedReferences.Count > 0
            ? explicitlySelectedReferences
            : rows.SelectMany(row => row.References);
        LinkedWallReference[] references = referenceSource
            .DistinctBy(reference => $"{reference.RootLinkInstanceId}|{reference.LinkPath}|{reference.LinkedElementId}")
            .ToArray();
        ElementId[] rootLinkIds = references
            .Select(reference => PortableApi.ElementId(reference.RootLinkInstanceId))
            .Where(id => _document.GetElement(id) is RevitLinkInstance)
            .Distinct()
            .ToArray();
        if (rootLinkIds.Length == 0)
            return new HighlightOutcome(0, 0, "No loaded parent link is available for the current result scope.");

        var selectableReferences = new List<RevitReference>();
        int directCount = 0;
        int nestedUnsupportedCount = 0;
        foreach (LinkedWallReference item in references)
        {
            if (item.NestedLinkInstancePath.Count > 0)
            {
                nestedUnsupportedCount++;
                continue;
            }
            if (_document.GetElement(PortableApi.ElementId(item.RootLinkInstanceId)) is not RevitLinkInstance link) continue;
            if (TryCreateActualLinkedReference(link, item, out RevitReference? actualReference))
            {
                selectableReferences.Add(actualReference!);
                directCount++;
            }
        }
        if (selectableReferences.Count > 0)
        {
            uidoc.Selection.SetReferences(selectableReferences);
            uidoc.RefreshActiveView();
        }
        else uidoc.Selection.SetElementIds([]);

        if (selectableReferences.Count == 0)
        {
            return new HighlightOutcome(
                rootLinkIds.Length,
                0,
                nestedUnsupportedCount > 0
                    ? $"The {nestedUnsupportedCount:N0} filtered wall reference(s) are inside a nested link. Revit can only select the parent RVT Link from the Space host, not the inner Wall/Part. Link the architectural source file directly into the Space model to enable real element selection."
                    : "No directly linked architectural Wall/Part reference could be resolved for this scope.");
        }

        int unresolvedDirectCount = references.Length - nestedUnsupportedCount - selectableReferences.Count;
        string limitation = unresolvedDirectCount == 0
            ? "All directly linked exterior walls are selected."
            : $"{unresolvedDirectCount:N0} unloaded or invalid direct reference(s) could not be selected.";
        return new HighlightOutcome(
            rootLinkIds.Length,
            selectableReferences.Count,
            $"Selected {directCount:N0} actual directly linked architectural element(s). " +
            (nestedUnsupportedCount > 0
                ? $"Skipped {nestedUnsupportedCount:N0} nested reference(s), because Revit would select only their parent RVT Link. "
                : string.Empty) +
            $"{limitation} No Generic Models were created.");
    }

    private bool TryCreateActualLinkedReference(
        RevitLinkInstance rootLink,
        LinkedWallReference item,
        out RevitReference? result)
    {
        result = null;
        Document? currentDocument = rootLink.GetLinkDocument();
        if (currentDocument is null) return false;

        var nestedInstances = new List<RevitLinkInstance>();
        foreach (long nestedId in item.NestedLinkInstancePath)
        {
            if (currentDocument.GetElement(PortableApi.ElementId(nestedId)) is not RevitLinkInstance nested)
                return false;
            nestedInstances.Add(nested);
            currentDocument = nested.GetLinkDocument();
            if (currentDocument is null) return false;
        }

        Element? target = currentDocument.GetElement(PortableApi.ElementId(item.LinkedElementId));
        if (target is null) return false;

        try
        {
            // Build the reference from the deepest IFC/RVT document back through
            // every containing RevitLinkInstance. This mirrors a user's TAB-select
            // through a nested link and lets Revit populate the Properties palette.
            RevitReference linkedReference = new(target);
            for (int index = nestedInstances.Count - 1; index >= 0; index--)
                linkedReference = linkedReference.CreateLinkReference(nestedInstances[index]);
            result = linkedReference.CreateLinkReference(rootLink);
            return true;
        }
        catch
        {
            result = null;
            return false;
        }
    }

    private void EnsureFocusGraphicsRegistered()
    {
        if (_focusGraphics is not null) return;
        var server = new ExteriorWallFocusGraphicsServer();
        ExternalService service = ExternalServiceRegistry.GetService(
            ExternalServices.BuiltInExternalServices.DirectContext3DService);
        if (service is not MultiServerService multiServer)
            throw new InvalidOperationException("Revit DirectContext3D service is unavailable.");
        multiServer.AddServer(server);
        // DirectContext3D is activated at application scope. Document-scoped
        // activation is not consistently honored by Revit's graphics pipeline.
        IList<Guid> activeServers = multiServer.GetActiveServerIds().ToList();
        if (!activeServers.Contains(server.GetServerId()))
        {
            activeServers.Add(server.GetServerId());
            multiServer.SetActiveServers(activeServers);
        }
        _focusGraphics = server;
    }

    private IEnumerable<FocusTriangle> ExtractFocusTriangles(
        IReadOnlyList<LinkedWallReference> references)
    {
        foreach (LinkedWallReference reference in references
            .DistinctBy(item => $"{item.RootLinkInstanceId}|{item.LinkPath}|{item.LinkedElementId}"))
        {
            if (!TryResolveLinkedElement(reference, out Element? element)) continue;
            GeometryElement? geometry;
            try
            {
                geometry = element!.get_Geometry(new Options
                {
                    DetailLevel = ViewDetailLevel.Fine,
                    IncludeNonVisibleObjects = true,
                    ComputeReferences = false
                });
            }
            catch
            {
                continue;
            }
            if (geometry is null) continue;
            foreach (FocusTriangle triangle in CollectFocusTriangles(geometry, reference.SourceToHost))
                yield return triangle;
        }
    }

    private bool TryResolveLinkedElement(LinkedWallReference reference, out Element? element)
    {
        element = null;
        if (_document.GetElement(PortableApi.ElementId(reference.RootLinkInstanceId)) is not RevitLinkInstance rootLink)
            return false;
        Document? sourceDocument = rootLink.GetLinkDocument();
        if (sourceDocument is null) return false;
        foreach (long nestedId in reference.NestedLinkInstancePath)
        {
            if (sourceDocument.GetElement(PortableApi.ElementId(nestedId)) is not RevitLinkInstance nested)
                return false;
            sourceDocument = nested.GetLinkDocument();
            if (sourceDocument is null) return false;
        }
        element = sourceDocument.GetElement(PortableApi.ElementId(reference.LinkedElementId));
        return element is not null;
    }

    private static IEnumerable<FocusTriangle> CollectFocusTriangles(
        GeometryElement geometry,
        Autodesk.Revit.DB.Transform sourceToHost)
    {
        foreach (GeometryObject geometryObject in geometry)
        {
            if (geometryObject is GeometryInstance instance)
            {
                GeometryElement? instanceGeometry = null;
                try { instanceGeometry = instance.GetInstanceGeometry(); }
                catch { /* Some imported symbols do not expose instance geometry. */ }
                if (instanceGeometry is not null)
                {
                    foreach (FocusTriangle triangle in CollectFocusTriangles(instanceGeometry, sourceToHost))
                        yield return triangle;
                }
                continue;
            }

            if (geometryObject is Solid solid && solid.Faces.Size > 0)
            {
                foreach (Face face in solid.Faces)
                {
                    Mesh? mesh = null;
                    try { mesh = face.Triangulate(); }
                    catch { /* Ignore invalid IFC faces. */ }
                    if (mesh is null) continue;
                    foreach (FocusTriangle triangle in TriangulateMesh(mesh, sourceToHost))
                        yield return triangle;
                }
                continue;
            }

            if (geometryObject is Mesh directMesh)
            {
                foreach (FocusTriangle triangle in TriangulateMesh(directMesh, sourceToHost))
                    yield return triangle;
            }
        }
    }

    private static IEnumerable<FocusTriangle> TriangulateMesh(
        Mesh mesh,
        Autodesk.Revit.DB.Transform sourceToHost)
    {
        for (int index = 0; index < mesh.NumTriangles; index++)
        {
            MeshTriangle sourceTriangle = mesh.get_Triangle(index);
            XYZ a = sourceToHost.OfPoint(sourceTriangle.get_Vertex(0));
            XYZ b = sourceToHost.OfPoint(sourceTriangle.get_Vertex(1));
            XYZ c = sourceToHost.OfPoint(sourceTriangle.get_Vertex(2));
            XYZ cross = (b - a).CrossProduct(c - a);
            if (cross.GetLength() < 1e-9) continue;
            yield return new FocusTriangle(a, b, c, cross.Normalize());
        }
    }

    private static IEnumerable<FocusTriangle> CreateBoundingBoxFocusTriangles(BoundingBoxXYZ box)
    {
        XYZ min = box.Min;
        XYZ max = box.Max;
        if (min is null || max is null) yield break;
        if (max.X - min.X < 1e-7 || max.Y - min.Y < 1e-7 || max.Z - min.Z < 1e-7)
            yield break;

        XYZ p000 = new(min.X, min.Y, min.Z);
        XYZ p100 = new(max.X, min.Y, min.Z);
        XYZ p110 = new(max.X, max.Y, min.Z);
        XYZ p010 = new(min.X, max.Y, min.Z);
        XYZ p001 = new(min.X, min.Y, max.Z);
        XYZ p101 = new(max.X, min.Y, max.Z);
        XYZ p111 = new(max.X, max.Y, max.Z);
        XYZ p011 = new(min.X, max.Y, max.Z);

        // Counter-clockwise winding when viewed from outside the box.  Keeping
        // the outward normal also lets the DirectContext renderer offset each
        // face slightly and avoid z-fighting with the linked IFC surface.
        foreach (FocusTriangle triangle in CreateFocusQuad(p000, p010, p110, p100)) yield return triangle; // bottom
        foreach (FocusTriangle triangle in CreateFocusQuad(p001, p101, p111, p011)) yield return triangle; // top
        foreach (FocusTriangle triangle in CreateFocusQuad(p000, p100, p101, p001)) yield return triangle; // min Y
        foreach (FocusTriangle triangle in CreateFocusQuad(p010, p011, p111, p110)) yield return triangle; // max Y
        foreach (FocusTriangle triangle in CreateFocusQuad(p000, p001, p011, p010)) yield return triangle; // min X
        foreach (FocusTriangle triangle in CreateFocusQuad(p100, p110, p111, p101)) yield return triangle; // max X
    }

    private IEnumerable<FocusTriangle> CreatePlanFocusTriangles(
        ViewPlan plan,
        IReadOnlyList<LinkedWallReference> references)
    {
        double cutElevation = GetPlanCutElevation(plan, references);
        double minimumWidth = UnitUtils.ConvertToInternalUnits(10.0, UnitTypeId.Millimeters);
        foreach (LinkedWallReference reference in references)
        {
            BoundingBoxXYZ box = reference.ToBoundingBox();
            double minX = box.Min.X;
            double minY = box.Min.Y;
            double maxX = Math.Max(box.Max.X, minX + minimumWidth);
            double maxY = Math.Max(box.Max.Y, minY + minimumWidth);
            double z = Math.Max(box.Min.Z + 1e-5, Math.Min(box.Max.Z - 1e-5, cutElevation));
            if (box.Max.Z - box.Min.Z < 2e-5) z = box.Min.Z;

            XYZ a = new(minX, minY, z);
            XYZ b = new(maxX, minY, z);
            XYZ c = new(maxX, maxY, z);
            XYZ d = new(minX, maxY, z);
            foreach (FocusTriangle triangle in CreateFocusQuad(a, b, c, d))
                yield return triangle;
        }
    }

    private double GetPlanCutElevation(
        ViewPlan plan,
        IReadOnlyList<LinkedWallReference> references)
    {
        double fallback = references.Count == 0
            ? plan.GenLevel?.Elevation ?? 0.0
            : references.Average(item => (item.MinZ + item.MaxZ) * 0.5);
        try
        {
            using PlanViewRange range = plan.GetViewRange();
            ElementId cutLevelId = range.GetLevelId(PlanViewPlane.CutPlane);
            Level? cutLevel = _document.GetElement(cutLevelId) as Level ?? plan.GenLevel;
            return (cutLevel?.Elevation ?? fallback) + range.GetOffset(PlanViewPlane.CutPlane);
        }
        catch
        {
            return fallback;
        }
    }

    private static IEnumerable<FocusTriangle> CreateFocusQuad(XYZ a, XYZ b, XYZ c, XYZ d)
    {
        XYZ cross = (b - a).CrossProduct(c - a);
        if (cross.GetLength() < 1e-9) yield break;
        XYZ normal = cross.Normalize();
        yield return new FocusTriangle(a, b, c, normal);
        yield return new FocusTriangle(a, c, d, normal);
    }

    [Obsolete("Legacy cleanup compatibility only; current focus uses transient linked graphics.")]
    private ElementId CreateRedFocusPreview(
        View3D view,
        long sourceElementId,
        IReadOnlyList<LinkedWallReference> references)
    {
        var solids = new List<GeometryObject>();
        foreach (LinkedWallReference reference in references)
        {
            Solid? marker = CreateBoundingBoxSolid(reference.ToBoundingBox());
            if (marker is not null) solids.Add(marker);
        }
        if (solids.Count == 0) return ElementId.InvalidElementId;

        ElementId wallCategory = new(BuiltInCategory.OST_Walls);
        ElementId previewCategory = DirectShape.IsValidCategoryId(wallCategory, _document)
            ? wallCategory
            : PortableApi.ElementId(BuiltInCategory.OST_GenericModel);
        DirectShape preview = DirectShape.CreateElement(
            _document,
            previewCategory);
        preview.ApplicationId = FocusPreviewApplicationId;
        preview.ApplicationDataId = sourceElementId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        preview.SetShape(solids);
        try { preview.Name = $"FOCUS — linked Element ID {sourceElementId}"; }
        catch { /* Some DirectShape categories expose a read-only Name. */ }
        Parameter? mark = preview.get_Parameter(BuiltInParameter.ALL_MODEL_MARK);
        if (mark?.IsReadOnly == false)
            mark.Set(sourceElementId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Parameter? comments = preview.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
        if (comments?.IsReadOnly == false)
        {
            string sourceLinks = string.Join("; ", references
                .Select(item => item.LinkPath)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.CurrentCultureIgnoreCase));
            string sourceNames = string.Join("; ", references
                .Select(item => item.ElementName)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.CurrentCultureIgnoreCase));
            comments.Set($"Temporary focus proxy | Source Element ID: {sourceElementId} | Source: {sourceNames} | Link: {sourceLinks}");
        }

        var red = new Autodesk.Revit.DB.Color(230, 28, 36);
        var overrides = new OverrideGraphicSettings();
        overrides.SetProjectionLineColor(red);
        overrides.SetProjectionLineWeight(8);
        overrides.SetSurfaceTransparency(35);
        FillPatternElement? solidFill = new FilteredElementCollector(_document)
            .OfClass(typeof(FillPatternElement))
            .Cast<FillPatternElement>()
            .FirstOrDefault(item => item.GetFillPattern().IsSolidFill);
        if (solidFill is not null)
        {
            overrides.SetSurfaceForegroundPatternId(solidFill.Id);
            overrides.SetSurfaceForegroundPatternColor(red);
            overrides.SetCutForegroundPatternId(solidFill.Id);
            overrides.SetCutForegroundPatternColor(red);
        }
        view.SetElementOverrides(preview.Id, overrides);
        return preview.Id;
    }

    private static Solid? CreateBoundingBoxSolid(BoundingBoxXYZ bounds)
    {
        double minimum = UnitUtils.ConvertToInternalUnits(10.0, UnitTypeId.Millimeters);
        double minX = bounds.Min.X;
        double minY = bounds.Min.Y;
        double minZ = bounds.Min.Z;
        double maxX = Math.Max(bounds.Max.X, minX + minimum);
        double maxY = Math.Max(bounds.Max.Y, minY + minimum);
        double maxZ = Math.Max(bounds.Max.Z, minZ + minimum);
        try
        {
            XYZ p0 = new(minX, minY, minZ);
            XYZ p1 = new(maxX, minY, minZ);
            XYZ p2 = new(maxX, maxY, minZ);
            XYZ p3 = new(minX, maxY, minZ);
            CurveLoop loop = CurveLoop.Create([
                Line.CreateBound(p0, p1),
                Line.CreateBound(p1, p2),
                Line.CreateBound(p2, p3),
                Line.CreateBound(p3, p0)]);
            return GeometryCreationUtilities.CreateExtrusionGeometry(
                [loop],
                XYZ.BasisZ,
                maxZ - minZ);
        }
        catch
        {
            return null;
        }
    }

    private void DeleteTemporaryPreviewsInOpenTransaction()
    {
        ElementId[] stale = new FilteredElementCollector(_document)
            .OfClass(typeof(DirectShape))
            .Cast<DirectShape>()
            .Where(shape =>
                string.Equals(shape.ApplicationId, LegacyPreviewApplicationId, StringComparison.Ordinal) ||
                string.Equals(shape.ApplicationId, SharedWallPreviewApplicationId, StringComparison.Ordinal) ||
                string.Equals(shape.ApplicationId, FocusPreviewApplicationId, StringComparison.Ordinal))
            .Select(shape => shape.Id)
            .ToArray();
        if (stale.Length > 0) _document.Delete(stale);
    }

    private void DeleteLegacyPreviewsIfPresent()
    {
        if (!HasTemporaryPreviews()) return;
        using var transaction = new Transaction(_document, "Exterior Wall Mapper - remove legacy Generic Model previews");
        transaction.Start();
        DeleteTemporaryPreviewsInOpenTransaction();
        transaction.Commit();
    }

    private void ApplySectionBox(View3D view, IReadOnlyList<LinkedWallReference> references)
    {
        if (references.Count == 0) return;
        if (_sectionBoxSnapshot is not null && _sectionBoxSnapshot.ViewId != view.Id.CompatValue())
            RestoreSectionBox();
        if (_sectionBoxSnapshot is null)
        {
            _sectionBoxSnapshot = new SectionBoxSnapshot(
                view.Id.CompatValue(),
                view.IsSectionBoxActive,
                CloneBox(view.GetSectionBox()));
        }

        double margin = UnitUtils.ConvertToInternalUnits(500.0, UnitTypeId.Millimeters);
        var crop = new BoundingBoxXYZ
        {
            Min = new XYZ(
                references.Min(item => item.MinX) - margin,
                references.Min(item => item.MinY) - margin,
                references.Min(item => item.MinZ) - margin),
            Max = new XYZ(
                references.Max(item => item.MaxX) + margin,
                references.Max(item => item.MaxY) + margin,
                references.Max(item => item.MaxZ) + margin)
        };
        view.SetSectionBox(crop);
        view.IsSectionBoxActive = true;
    }

    private void RestoreSectionBox()
    {
        if (_sectionBoxSnapshot is null) return;
        if (_document.GetElement(PortableApi.ElementId(_sectionBoxSnapshot.ViewId)) is View3D view && !view.IsTemplate)
        {
            view.SetSectionBox(CloneBox(_sectionBoxSnapshot.Box));
            view.IsSectionBoxActive = _sectionBoxSnapshot.WasActive;
        }
        _sectionBoxSnapshot = null;
    }

    private static BoundingBoxXYZ CloneBox(BoundingBoxXYZ source) => new()
    {
        Min = source.Min,
        Max = source.Max,
        Transform = source.Transform
    };

    private static void ZoomToReferences(UIDocument uidoc, IReadOnlyList<LinkedWallReference> references)
    {
        if (references.Count == 0) return;
        double minX = references.Min(item => item.MinX);
        double minY = references.Min(item => item.MinY);
        double minZ = references.Min(item => item.MinZ);
        double maxX = references.Max(item => item.MaxX);
        double maxY = references.Max(item => item.MaxY);
        double maxZ = references.Max(item => item.MaxZ);
        double margin = UnitUtils.ConvertToInternalUnits(1200.0, UnitTypeId.Millimeters);
        XYZ min = new(minX - margin, minY - margin, minZ - margin);
        XYZ max = new(maxX + margin, maxY + margin, maxZ + margin);
        UIView? uiView = uidoc.GetOpenUIViews().FirstOrDefault(item => item.ViewId == uidoc.ActiveView.Id);
        uiView?.ZoomAndCenterRectangle(min, max);
    }

    private void ApplyContextTransparencyFromUi()
    {
        if (!_isolationViewId.HasValue)
        {
            SetStatus("Focus one or more Element IDs before adjusting context transparency.", "#B36B00");
            return;
        }

        int transparency = PortableMath.Clamp(_contextTransparencyPercent, 0, 90);
        Queue(
            application =>
            {
                UIDocument uidoc = RequireUidoc(application);
                View view = _document.GetElement(PortableApi.ElementId(_isolationViewId.Value)) as View
                    ?? uidoc.ActiveView;
                using var transaction = new Transaction(
                    _document,
                    "Exterior Wall Mapper - context transparency");
                transaction.Start();
                RestoreContextTransparencyExceptInOpenTransaction(view.Id.CompatValue());
                ApplyContextTransparencyInOpenTransaction(view, transparency);
                transaction.Commit();
                if (uidoc.ActiveView.Id != view.Id) uidoc.ActiveView = view;
                uidoc.RefreshActiveView();
            },
            $"Setting Focus view context transparency to {transparency}%...",
            () => SetStatus(
                transparency == 0
                    ? "Focus view context restored to normal."
                    : $"Model context is {transparency}% transparent; the red linked-element highlight remains opaque.",
                "#14966A"));
    }

    private void ApplyContextTransparencyInOpenTransaction(View view, int transparency)
    {
        transparency = PortableMath.Clamp(transparency, 0, 90);
        if (transparency == 0)
        {
            RestoreContextTransparencyInOpenTransaction(view.Id.CompatValue());
            return;
        }

        if (!_contextTransparencySnapshots.TryGetValue(view.Id.CompatValue(), out ContextTransparencySnapshot? snapshot))
        {
            var categoryOverrides = new Dictionary<long, OverrideGraphicSettings>();
            foreach (Category category in _document.Settings.Categories)
            {
                if (category.CategoryType != CategoryType.Model) continue;
                try
                {
                    OverrideGraphicSettings original = view.GetCategoryOverrides(category.Id);
                    var faded = new OverrideGraphicSettings(original);
                    faded.SetSurfaceTransparency(transparency);
                    view.SetCategoryOverrides(category.Id, faded);
                    categoryOverrides[category.Id.CompatValue()] = original;
                }
                catch
                {
                    // Some analytical or internal categories cannot be overridden
                    // in every view type. Continue with the visible model categories.
                }
            }

            var elementOverrides = new Dictionary<long, OverrideGraphicSettings>();
            foreach (RevitLinkInstance link in new FilteredElementCollector(_document)
                .OfClass(typeof(RevitLinkInstance))
                .Cast<RevitLinkInstance>())
            {
                try
                {
                    OverrideGraphicSettings original = view.GetElementOverrides(link.Id);
                    var faded = new OverrideGraphicSettings(original);
                    faded.SetSurfaceTransparency(transparency);
                    view.SetElementOverrides(link.Id, faded);
                    elementOverrides[link.Id.CompatValue()] = original;
                }
                catch { }
            }
            _contextTransparencySnapshots[view.Id.CompatValue()] = new ContextTransparencySnapshot(
                categoryOverrides,
                elementOverrides);
            return;
        }

        foreach ((long categoryId, OverrideGraphicSettings original) in snapshot.CategoryOverrides)
        {
            try
            {
                var faded = new OverrideGraphicSettings(original);
                faded.SetSurfaceTransparency(transparency);
                view.SetCategoryOverrides(PortableApi.ElementId(categoryId), faded);
            }
            catch { }
        }
        foreach ((long elementId, OverrideGraphicSettings original) in snapshot.ElementOverrides)
        {
            try
            {
                var faded = new OverrideGraphicSettings(original);
                faded.SetSurfaceTransparency(transparency);
                view.SetElementOverrides(PortableApi.ElementId(elementId), faded);
            }
            catch { }
        }
    }

    private void RestoreContextTransparencyExceptInOpenTransaction(long retainedViewId)
    {
        foreach (long viewId in _contextTransparencySnapshots.Keys
            .Where(viewId => viewId != retainedViewId)
            .ToArray())
            RestoreContextTransparencyInOpenTransaction(viewId);
    }

    private void RestoreContextTransparencyInOpenTransaction(long viewId)
    {
        if (!_contextTransparencySnapshots.Remove(viewId, out ContextTransparencySnapshot? snapshot)) return;
        if (_document.GetElement(PortableApi.ElementId(viewId)) is not View view || view.IsTemplate) return;
        foreach ((long categoryId, OverrideGraphicSettings original) in snapshot.CategoryOverrides)
        {
            try { view.SetCategoryOverrides(PortableApi.ElementId(categoryId), original); }
            catch { }
        }
        foreach ((long elementId, OverrideGraphicSettings original) in snapshot.ElementOverrides)
        {
            try { view.SetElementOverrides(PortableApi.ElementId(elementId), original); }
            catch { }
        }
    }

    private void RestoreAllContextTransparencyInOpenTransaction()
    {
        foreach (long viewId in _contextTransparencySnapshots.Keys.ToArray())
            RestoreContextTransparencyInOpenTransaction(viewId);
    }

    private void ResetTemporaryView()
    {
        Queue(
            application =>
            {
                UIDocument uidoc = RequireUidoc(application);
                View view = _isolationViewId.HasValue
                    ? _document.GetElement(PortableApi.ElementId(_isolationViewId.Value)) as View ?? uidoc.ActiveView
                    : uidoc.ActiveView;
                bool hasPreviews = HasTemporaryPreviews();
                if (view.IsTemporaryHideIsolateActive() || _sectionBoxSnapshot is not null || hasPreviews ||
                    _contextTransparencySnapshots.Count > 0)
                {
                    using var transaction = new Transaction(_document, "Exterior Wall Mapper - reset located view");
                    transaction.Start();
                    if (view.IsTemporaryHideIsolateActive())
                        view.DisableTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate);
                    RestoreSectionBox();
                    RestoreAllContextTransparencyInOpenTransaction();
                    DeleteTemporaryPreviewsInOpenTransaction();
                    transaction.Commit();
                }
                _focusGraphics?.Clear();
                _isolationViewId = null;
                uidoc.Selection.SetElementIds([]);
                uidoc.RefreshActiveView();
            },
            "Clearing wall QA highlights...",
            () => SetStatus("Wall QA highlights were cleared and the previous view state was restored.", "#14966A"));
    }

    private bool HasTemporaryPreviews() => new FilteredElementCollector(_document)
        .OfClass(typeof(DirectShape))
        .Cast<DirectShape>()
        .Any(shape =>
            string.Equals(shape.ApplicationId, LegacyPreviewApplicationId, StringComparison.Ordinal) ||
            string.Equals(shape.ApplicationId, SharedWallPreviewApplicationId, StringComparison.Ordinal) ||
            string.Equals(shape.ApplicationId, FocusPreviewApplicationId, StringComparison.Ordinal));

    private void CleanupTemporaryPreviewState(UIApplication application)
    {
        bool hasPreviews = HasTemporaryPreviews();
        if (!hasPreviews && _sectionBoxSnapshot is null && !_isolationViewId.HasValue &&
            _contextTransparencySnapshots.Count == 0) return;
        View? view = _isolationViewId.HasValue
            ? _document.GetElement(PortableApi.ElementId(_isolationViewId.Value)) as View
            : application.ActiveUIDocument?.Document.Equals(_document) == true
                ? application.ActiveUIDocument.ActiveView
                : null;
        using var transaction = new Transaction(_document, "Exterior Wall Mapper - clean temporary previews");
        transaction.Start();
        if (view?.IsTemporaryHideIsolateActive() == true)
            view.DisableTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate);
        RestoreSectionBox();
        RestoreAllContextTransparencyInOpenTransaction();
        DeleteTemporaryPreviewsInOpenTransaction();
        transaction.Commit();
        _focusGraphics?.Clear();
        _isolationViewId = null;
        if (application.ActiveUIDocument?.Document.Equals(_document) == true)
        {
            application.ActiveUIDocument.Selection.SetElementIds([]);
            application.ActiveUIDocument.RefreshActiveView();
        }
    }

    private void Queue(Action<UIApplication> request, string status, Action completed)
    {
        if (_busy) return;
        _busy = true;
        SetEnabled(false);
        SetStatus(status, "#1155CC");
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
            throw new InvalidOperationException("Another Revit API request is already running.");
        }
        ExternalEventRequest result = _externalEvent.Raise();
        if (result is not ExternalEventRequest.Accepted and not ExternalEventRequest.Pending)
        {
            _busy = false;
            SetEnabled(true);
            throw new InvalidOperationException($"Revit rejected the request: {result}.");
        }
    }

    private void SetEnabled(bool enabled)
    {
        Find<Button>("scan_btn").IsEnabled = enabled;
        Find<Button>("export_btn").IsEnabled = enabled && _rows.Count > 0;
        Find<Button>("focus_element_btn").IsEnabled = enabled && _rows.Count > 0;
        Find<Button>("focus_plan_btn").IsEnabled = enabled && _rows.Count > 0;
        Find<WpfTextBox>("element_id_textbox").IsEnabled = enabled;
        Find<WpfSlider>("context_transparency_slider").IsEnabled = enabled;
        Find<DataGrid>("room_summary_grid").IsEnabled = enabled;
        Find<DataGrid>("walls_grid").IsEnabled = enabled;
        Find<DataGrid>("batches_grid").IsEnabled = enabled;
        Find<DataGrid>("architectural_walls_grid").IsEnabled = enabled;
        Find<DataGrid>("thickness_groups_grid").IsEnabled = enabled;
        Find<DataGrid>("thickness_spaces_grid").IsEnabled = enabled;
        Find<DataGrid>("linear_plan_grid").IsEnabled = enabled;
        Find<DataGrid>("linear_faces_grid").IsEnabled = enabled;
        Find<DataGrid>("room_wall_rooms_grid").IsEnabled = enabled;
        Find<DataGrid>("room_wall_inventory_grid").IsEnabled = enabled;
        Find<DataGrid>("room_wall_layers_grid").IsEnabled = enabled;
        Find<DataGrid>("wall_type_groups_grid").IsEnabled = enabled;
        Find<DataGrid>("wall_type_occurrences_grid").IsEnabled = enabled;
        Find<WpfComboBox>("room_wall_scope_combo").IsEnabled = enabled;
        Find<WpfComboBox>("room_wall_type_combo").IsEnabled = enabled;
        Find<CheckBox>("black_shared_rows_checkbox").IsEnabled = enabled;
        Find<Button>("copy_linear_plan_btn").IsEnabled = enabled && _linearPlans.Count > 0;
        Find<CheckBox>("global_ewa_checkbox").IsEnabled = enabled;
        Find<WpfTextBox>("global_ewa_component_textbox").IsEnabled = enabled && _oneComponentForAllEwa;
    }

    private void SetStatus(string message, string color, string? tooltip = null)
    {
        TextBlock status = Find<TextBlock>("status_text");
        status.Text = message;
        status.Foreground = Brush(color);
        status.ToolTip = tooltip ?? message;
    }

    private sealed record SectionBoxSnapshot(long ViewId, bool WasActive, BoundingBoxXYZ Box);

    private sealed record NavigatorSelection(string? Level);

    private sealed record ContextTransparencySnapshot(
        IReadOnlyDictionary<long, OverrideGraphicSettings> CategoryOverrides,
        IReadOnlyDictionary<long, OverrideGraphicSettings> ElementOverrides);

    private void ShowError(Exception exception)
    {
        SetStatus(exception.Message, "#B3261E", exception.ToString());
        if (!_window.IsVisible) _window.Show();
    }

    private UIDocument RequireUidoc(UIApplication application)
    {
        UIDocument uidoc = application.ActiveUIDocument
            ?? throw new InvalidOperationException("No active Revit project.");
        if (!uidoc.Document.Equals(_document))
            throw new InvalidOperationException("Return to the Revit project where Exterior Wall Mapper was opened.");
        return uidoc;
    }

    private static Window LoadWindow()
    {
        string root = Path.GetDirectoryName(typeof(ExteriorWallMapperController).Assembly.Location)
            ?? throw new InvalidOperationException("Plugin output folder is unavailable.");
        string path = Path.Combine(root, "Ui", "ExteriorWallMapperWindow.xaml");
        using FileStream stream = File.OpenRead(path);
        using XmlReader reader = XmlReader.Create(stream);
        return (Window)XamlReader.Load(reader);
    }

    private T Find<T>(string name) where T : FrameworkElement =>
        _window.FindName(name) as T
        ?? throw new InvalidOperationException($"UI control '{name}' was not found.");

    private static SolidColorBrush Brush(string hex) =>
        new((WpfColor)ColorConverter.ConvertFromString(hex));

    private sealed record HighlightOutcome(
        int LinkCount,
        int HighlightedReferenceCount,
        string Message);

    private sealed record FocusOutcome(bool Success, string Message);

    private sealed record FocusOccurrence(ExteriorWallRow Row, LinkedWallReference Reference);
}
