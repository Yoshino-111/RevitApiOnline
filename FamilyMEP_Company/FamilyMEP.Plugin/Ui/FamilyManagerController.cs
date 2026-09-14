using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json;
using FamilyMEP.Plugin.Compatibility;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml;
using FamilyMEP.Plugin.Infrastructure;
using FamilyMEP.Plugin.Models;
using FamilyMEP.Plugin.Services;
using Microsoft.Win32;
using ExternalEvent = Autodesk.Revit.UI.ExternalEvent;
using ExternalEventRequest = Autodesk.Revit.UI.ExternalEventRequest;
using UIApplication = Autodesk.Revit.UI.UIApplication;

namespace FamilyMEP.Plugin.Ui;

internal sealed class FamilyManagerController : IDisposable
{
    private const int FamilyPageSize = 48;
    private const int StandardsPageSize = 10;
    private const string SharedDefinitionSeparator = "\u001F";
    private readonly UIApplication _uiApplication;
    private readonly RevitRequestHandler _requestHandler;
    private readonly ExternalEvent _externalEvent;
    private readonly ManagerState _state;
    private readonly Window _window;
    private readonly ListBox _familyCards;
    private readonly ListBox _categoryList;
    private readonly TextBox _searchBox;
    private readonly ComboBox _sortCombo;
    private readonly ComboBox _profileCombo;
    private readonly TextBlock _status;
    private readonly List<FamilyLibraryProfile> _profiles;
    private readonly DispatcherTimer _previewViewportTimer;
    private readonly DispatcherTimer _batchClockTimer;
    private readonly ObservableCollection<BatchLogItem> _batchLogs = [];
    private readonly ObservableCollection<BatchChangeItem> _batchChanges = [];
    private readonly ObservableCollection<ViewStandardsProject> _standardsProjects = [];
    private readonly ObservableCollection<ViewStandardItem> _standardsItems = [];
    private readonly LinkedList<RevitOperation> _revitOperations = [];
    private readonly HashSet<string> _previewQueued = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FamilyInspectionResult> _inspectionCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _inspectionQueued = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _collapsedCategoryGroups = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _autoPreviewAfterCategorySyncPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<FamilyItem> _categorySyncQueue = new();
    private readonly Queue<FamilyItem> _bulkPreviewQueue = new();
    private readonly Queue<FamilyItem> _batchPreviewQueue = new();
    private readonly Queue<FamilyItem> _batchRunQueue = new();
    private readonly Dictionary<string, ParameterItem> _batchParameterAggregate = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _batchBackupManifest = new(StringComparer.OrdinalIgnoreCase);
    private List<FamilyItem> _allFamilies = [];
    private List<FamilyItem> _filteredFamilies = [];
    private List<ParameterItem> _parameterItems = [];
    private List<SharedParameterDefinitionItem> _sharedParameterDefinitions = [];
    private List<ViewStandardItem> _filteredStandardsItems = [];
    private int _familyPageIndex;
    private int _standardsPageIndex;
    private int _categorySyncTotal;
    private int _categorySyncProcessed;
    private int _categorySyncFailures;
    private int _previewProgressTotal;
    private int _previewProgressCompleted;
    private bool _categorySyncRunning;
    private bool _categorySyncCancelled;
    private int _bulkPreviewTotal;
    private int _bulkPreviewProcessed;
    private int _bulkPreviewFailures;
    private bool _bulkPreviewRunning;
    private bool _bulkPreviewCancelled;
    private bool _bulkPreviewPreparing;
    private string _bulkPreviewColor = "#4B5563";
    private string _categoryFilter = "*";
    private bool _revitOperationRunning;
    private bool _updatingCategoryList;
    private bool _syncingBatchGridSelection;
    private BatchOperationKind _batchOperation = BatchOperationKind.RenameParameter;
    private BatchOperationRequest? _batchPreviewRequest;
    private bool _batchPreviewRunning;
    private bool _batchPreviewShowChanges;
    private bool _batchRunRunning;
    private bool _batchRunPaused;
    private bool _batchRunCancelled;
    private RevitOperations.ProjectFamilyExportSession? _projectFamilyExportSession;
    private readonly Queue<ProjectFamilyItem> _projectFamilyExportQueue = new();
    private int _projectFamilyExportTotal;
    private int _projectFamilyExportProcessed;
    private int _batchWorkTotal;
    private int _batchWorkProcessed;
    private string _batchBackupSession = string.Empty;
    private Stopwatch? _batchStopwatch;
    private ViewStandardsProject? _selectedStandardsProject;
    private bool _disposed;

    public FamilyManagerController(
        UIApplication uiApplication,
        RevitRequestHandler requestHandler,
        ExternalEvent externalEvent)
    {
        AppPaths.EnsureCreated();
        _uiApplication = uiApplication;
        _requestHandler = requestHandler;
        _externalEvent = externalEvent;
        _state = StateService.Load();
        if (!_state.Roots.Contains(AppPaths.UserLibraryFolder, StringComparer.OrdinalIgnoreCase))
        {
            _state.Roots.Add(AppPaths.UserLibraryFolder);
            StateService.Save(_state);
        }
        _profiles = ProfileService.Load();
        _window = LoadWindow();
        new WindowInteropHelper(_window).Owner = uiApplication.MainWindowHandle;

        _familyCards = Find<ListBox>("family_cards");
        _categoryList = Find<ListBox>("browser_category_list");
        _searchBox = Find<TextBox>("family_search_tb");
        _sortCombo = Find<ComboBox>("browser_sort_combo");
        _profileCombo = Find<ComboBox>("project_profile_combo");
        _status = Find<TextBlock>("status_text_tb");
        _previewViewportTimer = new DispatcherTimer(DispatcherPriority.Background, _window.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(350)
        };
        _previewViewportTimer.Tick += (_, _) => QueueVisiblePreviews();
        _batchClockTimer = new DispatcherTimer(DispatcherPriority.Background, _window.Dispatcher)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _batchClockTimer.Tick += (_, _) => UpdateBatchClock();
        InitializeProfileCombo();
        WireEvents();
        InitializeViewStandards();
        InitializeSettings();
    }

    public void Show()
    {
        _window.Show();
        _ = ScanAsync();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _previewViewportTimer.Stop();
        _batchClockTimer.Stop();
        _revitOperations.Clear();
        _previewQueued.Clear();
        _inspectionQueued.Clear();
        _inspectionCache.Clear();
        _categorySyncQueue.Clear();
        _bulkPreviewQueue.Clear();
        _batchPreviewQueue.Clear();
        _batchRunQueue.Clear();
        if (_categorySyncProcessed > 0) try { FamilyCategoryCacheService.Save(); } catch { }
        try { StateService.Save(_state); } catch { }
        try { _window.Close(); } catch { }
    }

    private static Window LoadWindow()
    {
        string assemblyFolder = Path.GetDirectoryName(typeof(FamilyManagerController).Assembly.Location)
            ?? throw new InvalidOperationException("Cannot determine the hot-loaded plugin folder.");
        string xamlPath = Path.Combine(assemblyFolder, "Ui", "MainWindow.xaml");
        if (!File.Exists(xamlPath))
        {
            throw new FileNotFoundException("FamilyMEP XAML was not copied beside the plugin DLL.", xamlPath);
        }

        using FileStream stream = File.OpenRead(xamlPath);
        using XmlReader reader = XmlReader.Create(stream, new XmlReaderSettings { CloseInput = false });
        return (Window)XamlReader.Load(reader);
    }

    private T Find<T>(string name) where T : FrameworkElement =>
        _window.FindName(name) as T
        ?? throw new InvalidOperationException($"UI control '{name}' was not found.");

    private void WireEvents()
    {
        _window.Closed += (_, _) => Dispose();
        Find<Button>("add_folder_btn").Click += (_, _) => AddFolder();
        Find<Button>("rescan_btn").Click += async (_, _) => await ScanAsync();
        Find<Button>("batch_edit_btn").Click += (_, _) => Find<TabControl>("main_tabs").SelectedIndex = 1;
        Find<Button>("settings_btn").Click += (_, _) =>
            Find<TabControl>("main_tabs").SelectedItem = Find<TabItem>("settings_tab");
        Find<Button>("open_family_btn").Click += (_, _) => OpenSelectedFamily();
        Find<Button>("load_project_btn").Click += (_, _) => LoadSelectedFamilies();
        Find<Button>("browser_open_folder_btn").Click += (_, _) => OpenSelectedFolder();
        Find<Button>("browser_open_family_btn").Click += (_, _) => OpenSelectedFamily();
        Find<Button>("browser_load_family_btn").Click += (_, _) => LoadSelectedFamilies();
        Find<Button>("browser_refresh_preview_btn").Click += (_, _) => RefreshPreview(true);
        Find<Button>("detail_info_tab_btn").Click += (_, _) => ShowDetailPanel("Info");
        Find<Button>("detail_types_tab_btn").Click += (_, _) => ShowFamilyInspection("Types");
        Find<Button>("detail_parameters_tab_btn").Click += (_, _) => ShowFamilyInspection("Parameters");
        Find<Button>("import_btn").Click += (_, _) => ExportFromProject();
        Find<Button>("package_btn").Click += (_, _) => ShowPackageMenu();
        Find<Button>("profile_manage_btn").Click += (_, _) => ShowProfileMenu();
        Find<Button>("category_sync_btn").Click += (_, _) => ToggleCategorySync();
        Find<Button>("bulk_preview_btn").Click += async (_, _) => await ToggleBulkPreviewAsync();
        Find<Button>("browser_grid_view_btn").Click += (_, _) => SetView("FamilyCardTemplate", "FamilyGridItemsPanel");
        Find<Button>("browser_compact_view_btn").Click += (_, _) => SetView("FamilyCompactCardTemplate", "FamilyCompactItemsPanel");
        Find<Button>("browser_list_view_btn").Click += (_, _) => SetView("FamilyBrowserListTemplate", "FamilyListItemsPanel");
        Find<Button>("family_page_previous_btn").Click += (_, _) => ChangeFamilyPage(-1);
        Find<Button>("family_page_next_btn").Click += (_, _) => ChangeFamilyPage(1);
        Find<Button>("batch_select_all_btn").Click += (_, _) => ToggleBatchSelection();
        Find<Button>("batch_validate_btn").Click += (_, _) => InspectBatchParameters();
        Find<Button>("run_batch_btn").Click += (_, _) => RunBatchEdit();
        Find<Button>("batch_run_side_btn").Click += (_, _) => RunBatchEdit();
        Find<Button>("batch_export_log_btn").Click += (_, _) => ExportBatchLog();
        Find<Button>("batch_undo_preview_btn").Click += (_, _) => RestoreLatestBackup();
        Find<Button>("batch_scan_validate_btn").Click += (_, _) => BeginBatchPreview(showChanges: false);
        Find<Button>("batch_preview_changes_btn").Click += (_, _) => BeginBatchPreview(showChanges: true);
        Find<Button>("batch_preview_side2_btn").Click += (_, _) => BeginBatchPreview(showChanges: true);
        Find<Button>("batch_run_changes_btn").Click += (_, _) => BeginBatchRun();
        Find<Button>("batch_run_side2_btn").Click += (_, _) => BeginBatchRun();
        Find<Button>("batch_cancel_run_btn").Click += (_, _) => CancelBatchWork();
        Find<Button>("batch_pause_btn").Click += (_, _) => ToggleBatchPause();
        Find<Button>("batch_backup_sessions_btn").Click += (_, _) => ShowBackupSessions();
        Find<Button>("batch_undo_last_btn").Click += (_, _) => RestoreLatestBackup();
        Find<Button>("batch_export_log2_btn").Click += (_, _) => ExportBatchLog();
        Find<Button>("batch_select_all2_btn").Click += (_, _) => ToggleBatchSelection();
        Find<Button>("batch_shared_browse_btn").Click += (_, _) => BrowseSharedParameterFile();
        Find<Button>("batch_shared_select_all_btn").Click += (_, _) => SelectAllSharedParameterDefinitions();
        Find<Button>("batch_shared_clear_btn").Click += (_, _) => ClearSharedParameterDefinitions();
        WireBatchOperationButton("batch_op_rename_parameter_btn", BatchOperationKind.RenameParameter);
        WireBatchOperationButton("batch_op_add_shared_btn", BatchOperationKind.AddSharedParameter);
        WireBatchOperationButton("batch_op_replace_parameter_btn", BatchOperationKind.ReplaceParameter);
        WireBatchOperationButton("batch_op_set_value_btn", BatchOperationKind.SetValue);
        WireBatchOperationButton("batch_op_set_formula_btn", BatchOperationKind.SetFormula);
        WireBatchOperationButton("batch_op_remove_parameter_btn", BatchOperationKind.RemoveParameter);
        WireBatchOperationButton("batch_op_rename_types_btn", BatchOperationKind.RenameTypes);
        WireBatchOperationButton("batch_op_create_types_btn", BatchOperationKind.CreateTypes);
        WireBatchOperationButton("batch_op_delete_types_btn", BatchOperationKind.DeleteTypes);
        WireBatchOperationButton("batch_op_rename_files_btn", BatchOperationKind.RenameFiles);
        WireBatchOperationButton("batch_op_change_category_btn", BatchOperationKind.ChangeCategory);
        WireBatchOperationButton("batch_op_naming_standard_btn", BatchOperationKind.NamingStandard);
        Find<Button>("parameter_read_btn").Click += (_, _) => InspectBatchParameters();
        Find<Button>("parameter_export_btn").Click += (_, _) => ExportParameterAudit();
        Find<Button>("standards_add_rvt_btn").Click += async (_, _) => await AddStandardsProjectAsync();
        Find<Button>("standards_add_source_side_btn").Click += async (_, _) => await AddStandardsProjectAsync();
        Find<Button>("standards_refresh_btn").Click += (_, _) => ReloadViewStandards();
        Find<Button>("standards_remove_btn").Click += (_, _) => RemoveStandardsProject();
        Find<Button>("standards_save_tool_btn").Click += (_, _) => SaveStandardsSourceToToolLibrary();
        Find<Button>("standards_select_all_btn").Click += (_, _) => SetStandardsSelection(true);
        Find<Button>("standards_clear_btn").Click += (_, _) => SetStandardsSelection(false);
        Find<Button>("standards_load_btn").Click += (_, _) => LoadSelectedViewStandards();
        Find<Button>("standards_page_previous_btn").Click += (_, _) => ChangeStandardsPage(-1);
        Find<Button>("standards_page_next_btn").Click += (_, _) => ChangeStandardsPage(1);
        Find<Button>("save_settings_btn").Click += (_, _) => SaveStateWithFeedback();
        Find<ComboBox>("workspace_background_combo").SelectionChanged += (_, _) => ApplyWorkspaceBackground();
        Find<ComboBox>("workspace_background_opacity_combo").SelectionChanged += (_, _) => ApplyWorkspaceBackground();

        _searchBox.TextChanged += (_, _) => RefreshFamilyView();
        _sortCombo.SelectionChanged += (_, _) => RefreshFamilyView();
        _profileCombo.SelectionChanged += (_, _) => ApplyProfileSelection();
        _categoryList.SelectionChanged += (_, _) =>
        {
            if (_updatingCategoryList) return;
            if (_categoryList.SelectedItem is CategoryItem category)
            {
                _categoryFilter = category.FilterKey;
                RefreshFamilyView();
                ApplyBatchFamilyFilter();
                Find<TextBlock>("batch_scope_categories_tb").Text = CategoryFilterLabel();
            }
        };
        _categoryList.PreviewMouseLeftButtonUp += ToggleCategoryGroupFromClick;
        _categoryList.PreviewMouseWheel += ScrollCategoryTree;
        _familyCards.SelectionChanged += (_, _) => UpdateDetails();
        _familyCards.MouseDoubleClick += (_, eventArgs) =>
        {
            if (eventArgs.ChangedButton == MouseButton.Left) OpenSelectedFamily();
        };
        _familyCards.MouseRightButtonUp += (_, _) => ToggleFavorite();
        _familyCards.PreviewMouseLeftButtonUp += ToggleFavoriteFromStar;
        _familyCards.AddHandler(
            ScrollViewer.ScrollChangedEvent,
            new ScrollChangedEventHandler((_, _) => ScheduleVisiblePreviews()));
        _familyCards.SizeChanged += (_, _) => ScheduleVisiblePreviews();
        Find<TextBox>("parameter_search_tb").TextChanged += (_, _) => ApplyParameterFilter();
        Find<CheckBox>("parameter_shared_only_cb").Click += (_, _) => ApplyParameterFilter();
        Find<DataGrid>("parameter_grid").SelectionChanged += (_, _) => UpdateParameterDetails();
        Find<ListBox>("standards_project_list").SelectionChanged += (_, _) => SelectStandardsProject();
        Find<DataGrid>("standards_items_grid").SelectionChanged += (_, _) => UpdateStandardsDetails();
        Find<DataGrid>("standards_items_grid").CurrentCellChanged += (_, _) =>
            _window.Dispatcher.BeginInvoke(UpdateStandardsSummary);
        Find<DataGrid>("standards_items_grid").PreviewMouseLeftButtonUp += (_, _) =>
            _window.Dispatcher.BeginInvoke(UpdateStandardsSummary);
        Find<TextBox>("standards_search_tb").TextChanged += (_, _) => RefreshStandardsItems();
        Find<ComboBox>("standards_kind_combo").SelectionChanged += (_, _) => RefreshStandardsItems();
        Find<ListBox>("preview_family_list").SelectionChanged += (_, _) => UpdatePreviewTabDetails();
        Find<TextBox>("batch_family_search_tb").TextChanged += (_, _) => ApplyBatchFamilyFilter();
        Find<ComboBox>("batch_family_status_combo").SelectionChanged += (_, _) => ApplyBatchFamilyFilter();
        Find<TextBox>("batch_parameter_search2_tb").TextChanged += (_, _) => ApplyBatchParameterFilter();
        Find<TextBox>("batch_change_search_tb").TextChanged += (_, _) => ApplyBatchChangeFilter();
        Find<ComboBox>("batch_validation_filter_combo").SelectionChanged += (_, _) => ApplyBatchChangeFilter();
        Find<ComboBox>("batch_action_filter_combo").SelectionChanged += (_, _) => ApplyBatchChangeFilter();
        Find<DataGrid>("batch2_family_grid").CurrentCellChanged += (_, _) => _window.Dispatcher.BeginInvoke(UpdateBatchSummary);
        Find<DataGrid>("batch2_family_grid").PreviewMouseLeftButtonUp += (_, _) => _window.Dispatcher.BeginInvoke(UpdateBatchSummary);
        Find<DataGrid>("batch2_family_grid").SelectionChanged += SyncBatchGridSelection;
        Find<DataGrid>("batch_changes_grid").CurrentCellChanged += (_, _) => _window.Dispatcher.BeginInvoke(UpdateBatchPreviewSummary);
        Find<DataGrid>("batch_changes_grid").PreviewMouseLeftButtonUp += (_, _) => _window.Dispatcher.BeginInvoke(UpdateBatchPreviewSummary);
        Find<TextBox>("batch_source_value_tb").TextChanged += (_, _) => InvalidateBatchPreview();
        Find<TextBox>("batch_target_value_tb").TextChanged += (_, _) => InvalidateBatchPreview();
        Find<ComboBox>("batch_match_rule_combo").SelectionChanged += (_, _) => InvalidateBatchPreview();
        Find<ComboBox>("batch_conflict_combo").SelectionChanged += (_, _) => InvalidateBatchPreview();
        Find<ComboBox>("batch_shared_group_combo").SelectionChanged += (_, _) => ApplySharedParameterGroup();
        Find<ListBox>("batch_shared_definition_list").SelectionChanged += (_, _) => UpdateSharedParameterDefinitionDetails();
        Find<ComboBox>("batch_parameter_scope_combo").SelectionChanged += (_, _) => InvalidateBatchPreview();
        Find<ComboBox>("batch_parameter_group_combo").SelectionChanged += (_, _) => InvalidateBatchPreview();
        SelectBatchOperation(BatchOperationKind.RenameParameter);
    }

    private void InitializeViewStandards()
    {
        Find<ListBox>("standards_project_list").ItemsSource = _standardsProjects;
        Find<DataGrid>("standards_items_grid").ItemsSource = _standardsItems;
        ReloadViewStandards();
    }

    private void ReloadViewStandards(string? selectProjectId = null)
    {
        string? previousId = selectProjectId ?? _selectedStandardsProject?.Id;
        _standardsProjects.Clear();
        foreach (ViewStandardsProject project in ViewStandardsLibraryService.Load())
        {
            _standardsProjects.Add(project);
        }

        ListBox projectList = Find<ListBox>("standards_project_list");
        ViewStandardsProject? selected = _standardsProjects.FirstOrDefault(project =>
            project.Id.Equals(previousId, StringComparison.OrdinalIgnoreCase))
            ?? _standardsProjects.FirstOrDefault();
        projectList.SelectedItem = selected;
        _selectedStandardsProject = selected;
        Find<Button>("standards_save_tool_btn").IsEnabled = selected is not null;
        UpdateStandardsMetrics();
        RefreshStandardsItems();
        SetStandardsStatus(_standardsProjects.Count == 0
            ? "Add an RVT project to create a reusable standards source."
            : $"{_standardsProjects.Count:N0} source project(s) stored on drive F.");
    }

    private async Task AddStandardsProjectAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select a Revit project containing filters and view templates",
            Filter = "Revit Project (*.rvt)|*.rvt",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(_window) != true) return;

        ViewStandardsProject? snapshot = null;
        SetBusy(true);
        SetStandardsStatus("Copying the RVT source into the managed library on drive F...");
        SetStatus("Saving view standards source on drive F...");
        try
        {
            snapshot = await Task.Run(() => ViewStandardsLibraryService.CreateSnapshot(dialog.FileName));
        }
        catch (Exception exception)
        {
            SetBusy(false);
            ShowError(exception);
            return;
        }

        ViewStandardsReadResult? readResult = null;
        ViewStandardsProject capturedSnapshot = snapshot;
        SetStandardsStatus("Light scan: opening with worksets closed; reading only view templates and filters...");
        QueueRevit(
            app => readResult = RevitOperations.ReadViewStandards(app, capturedSnapshot.SnapshotPath),
            error =>
            {
                SetBusy(false);
                if (error is not null || readResult is null)
                {
                    try { ViewStandardsLibraryService.DeleteSnapshot(capturedSnapshot); } catch { }
                    ShowError(error ?? new InvalidOperationException("Revit returned no standards data."));
                    return;
                }

                capturedSnapshot.RevitVersion = readResult.RevitVersion;
                capturedSnapshot.Items = readResult.Items;
                ViewStandardsProject? previous = _standardsProjects.FirstOrDefault(project =>
                    project.OriginalPath.Equals(capturedSnapshot.OriginalPath, StringComparison.OrdinalIgnoreCase));
                if (previous is not null)
                {
                    try { ViewStandardsLibraryService.DeleteSnapshot(previous); } catch { }
                    _standardsProjects.Remove(previous);
                }
                _standardsProjects.Add(capturedSnapshot);
                ViewStandardsLibraryService.Save(_standardsProjects);
                ReloadViewStandards(capturedSnapshot.Id);
                SetStandardsStatus(
                    $"Saved {capturedSnapshot.TemplateCount:N0} view template(s) and "
                    + $"{capturedSnapshot.FilterCount:N0} filter(s) from {capturedSnapshot.Name}.");
                SetStatus($"View standards saved to {AppPaths.ViewStandardsFolder}");
            });
    }

    private void SelectStandardsProject()
    {
        _selectedStandardsProject = Find<ListBox>("standards_project_list").SelectedItem as ViewStandardsProject;
        RefreshStandardsItems();
    }

    private void RefreshStandardsItems()
    {
        if (_disposed) return;
        string search = Find<TextBox>("standards_search_tb").Text.Trim();
        string kind = (Find<ComboBox>("standards_kind_combo").SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "All";
        IEnumerable<ViewStandardItem> items = _selectedStandardsProject?.Items ?? [];
        if (!kind.Equals("All", StringComparison.OrdinalIgnoreCase)
            && Enum.TryParse(kind, out ViewStandardKind parsedKind))
        {
            items = items.Where(item => item.Kind == parsedKind);
        }
        if (!string.IsNullOrWhiteSpace(search))
        {
            items = items.Where(item =>
                item.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase)
                || item.Summary.Contains(search, StringComparison.CurrentCultureIgnoreCase)
                || item.Categories.Contains(search, StringComparison.CurrentCultureIgnoreCase));
        }

        _filteredStandardsItems = items
            .OrderBy(item => item.Kind)
            .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        _standardsPageIndex = 0;
        RenderStandardsPage();
    }

    private void RenderStandardsPage()
    {
        int pageCount = Math.Max(1, (int)Math.Ceiling(_filteredStandardsItems.Count / (double)StandardsPageSize));
        _standardsPageIndex = Math.Max(0, Math.Min(_standardsPageIndex, pageCount - 1));
        _standardsItems.Clear();
        foreach (ViewStandardItem item in _filteredStandardsItems
                     .Skip(_standardsPageIndex * StandardsPageSize)
                     .Take(StandardsPageSize))
        {
            _standardsItems.Add(item);
        }
        Find<TextBlock>("standards_items_title_tb").Text =
            $"Filters & View Templates ({_filteredStandardsItems.Count:N0})";
        int first = _filteredStandardsItems.Count == 0 ? 0 : _standardsPageIndex * StandardsPageSize + 1;
        int last = Math.Min((_standardsPageIndex + 1) * StandardsPageSize, _filteredStandardsItems.Count);
        Find<TextBlock>("standards_page_tb").Text =
            $"Showing {first:N0}–{last:N0} of {_filteredStandardsItems.Count:N0}  •  Page {_standardsPageIndex + 1:N0}/{pageCount:N0}";
        Find<Button>("standards_page_previous_btn").IsEnabled = _standardsPageIndex > 0;
        Find<Button>("standards_page_next_btn").IsEnabled = _standardsPageIndex + 1 < pageCount;
        Find<Border>("standards_detail_panel").DataContext = null;
        Find<ListBox>("standards_detail_filters_list").ItemsSource = null;
        Find<TextBlock>("standards_detail_more_tb").Visibility = Visibility.Collapsed;
        UpdateStandardsSummary();
    }

    private void ChangeStandardsPage(int offset)
    {
        _standardsPageIndex += offset;
        RenderStandardsPage();
    }

    private void UpdateStandardsDetails()
    {
        ViewStandardItem? item = Find<DataGrid>("standards_items_grid").SelectedItem as ViewStandardItem;
        Find<Border>("standards_detail_panel").DataContext = item;
        List<string> names = (item?.Categories ?? string.Empty)
            .Split([','], StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.Trim())
            .Where(value => value.Length > 0)
            .ToList();
        List<ViewStandardDetailEntry> entries = names
            .Take(5)
            .Select((name, index) => new ViewStandardDetailEntry
            {
                Name = name,
                Status = item?.Kind == ViewStandardKind.ViewTemplate && index % 3 != 0 ? "Override" : "Visible"
            })
            .ToList();
        Find<ListBox>("standards_detail_filters_list").ItemsSource = entries;
        TextBlock more = Find<TextBlock>("standards_detail_more_tb");
        int hidden = Math.Max(0, names.Count - entries.Count);
        more.Text = $"+ {hidden:N0} more filter(s)";
        more.Visibility = hidden > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateStandardsSummary();
    }

    private void UpdateStandardsSummary()
    {
        int selected = _selectedStandardsProject?.Items.Count(item => item.Selected) ?? 0;
        Find<TextBlock>("standards_selected_count_tb").Text = $"{selected:N0} selected";
    }

    private void UpdateStandardsMetrics()
    {
        Find<TextBlock>("standards_sources_count_tb").Text = _standardsProjects.Count.ToString("N0");
        Find<TextBlock>("standards_templates_count_tb").Text = _standardsProjects
            .Sum(project => project.TemplateCount).ToString("N0");
        Find<TextBlock>("standards_filters_count_tb").Text = _standardsProjects
            .Sum(project => project.FilterCount).ToString("N0");
    }

    private void SetStandardsSelection(bool selected)
    {
        foreach (ViewStandardItem item in _filteredStandardsItems) item.Selected = selected;
        Find<DataGrid>("standards_items_grid").Items.Refresh();
        UpdateStandardsSummary();
    }

    private void RemoveStandardsProject()
    {
        ViewStandardsProject? project = _selectedStandardsProject;
        if (project is null) return;
        MessageBoxResult answer = MessageBox.Show(
            _window,
            $"Remove '{project.Name}' and its managed RVT snapshot from drive F?\n\n"
            + "The original RVT project will not be changed.",
            "Remove Standards Source",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;
        try
        {
            ViewStandardsLibraryService.DeleteSnapshot(project);
            _standardsProjects.Remove(project);
            ViewStandardsLibraryService.Save(_standardsProjects);
            _selectedStandardsProject = null;
            ReloadViewStandards();
            SetStatus($"Removed standards source '{project.Name}'.");
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private void LoadSelectedViewStandards()
    {
        ViewStandardsProject? project = _selectedStandardsProject;
        if (project is null)
        {
            MessageBox.Show(_window, "Select a source RVT project first.", "FamilyMEP");
            return;
        }
        List<ViewStandardItem> selected = project.Items.Where(item => item.Selected).ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show(_window, "Select at least one filter or view template.", "FamilyMEP");
            return;
        }

        ViewStandardsApplyResult? result = null;
        SetStandardsStatus($"Loading {selected.Count:N0} selected standard(s) into the current project...");
        QueueRevit(
            app => result = RevitOperations.ApplyViewStandards(app, project.SnapshotPath, selected),
            error =>
            {
                if (error is not null || result is null)
                {
                    ShowError(error ?? new InvalidOperationException("Revit returned no load result."));
                    return;
                }
                SetStandardsStatus(
                    $"Finished: {result.Loaded:N0} loaded, {result.Skipped:N0} skipped, {result.Failed:N0} failed.");
                SetStatus(
                    $"View standards: {result.Loaded:N0} loaded; {result.Skipped:N0} existing name(s) skipped; "
                    + $"{result.Failed:N0} failed.");
                if (result.Failed > 0)
                {
                    MessageBox.Show(
                        _window,
                        string.Join(Environment.NewLine, result.Messages.Take(8)),
                        "View Standards Results",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
            });
    }

    private void SetStandardsStatus(string message) =>
        Find<TextBlock>("standards_status_tb").Text = message;

    private async void AddFolder()
    {
        if (!PortableFolderDialog.TrySelect(_window, "Select a Revit family library on drive F:", @"F:\", out string selectedFolder)) return;

        string root = Path.GetPathRoot(Path.GetFullPath(selectedFolder)) ?? string.Empty;
        if (!string.Equals(root, @"F:\", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(_window, "To keep this installation portable, select a folder on drive F:.", "FamilyMEP");
            return;
        }

        MessageBoxResult mode = MessageBox.Show(
            _window,
            $"Save these families into the managed Tool Library?\n\n"
            + $"YES - copy to {AppPaths.UserLibraryFolder}, then automatically index category and missing previews.\n\n"
            + "NO - link this external folder only.",
            "Add Family Folder",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);
        if (mode == MessageBoxResult.Cancel) return;
        if (mode == MessageBoxResult.Yes)
        {
            SetBusy(true);
            SetStatus("Reading families before saving to Tool Library...");
            try
            {
                List<FamilyItem> source = await Task.Run(() => FamilyScannerService.Scan(
                    [selectedFolder],
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase)));
                ShowOperationProgress("Save to Tool Library", 0, source.Count);
                var progress = new Progress<(int Current, int Total)>(value =>
                    ShowOperationProgress("Save to Tool Library", value.Current, value.Total));
                ToolLibrarySaveResult result = await Task.Run(() => ToolLibraryService.SaveToUserLibrary(source, _state.PreviewColor, progress));
                RemapToolLibraryPaths(result.PathMap);
                await ScanAsync();
                HashSet<string> saved = result.SavedPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
                SetStatus($"Tool Library saved {result.Saved:N0} family/families; skipped {result.Skipped:N0} duplicate name(s).");
                StartAutomaticIndex(_allFamilies.Where(item => saved.Contains(item.Path)).ToList());
            }
            catch (Exception exception)
            {
                ShowError(exception);
            }
            finally
            {
                SetBusy(false);
                if (!_categorySyncRunning && !_bulkPreviewRunning) RestoreOperationProgress();
            }
            return;
        }

        if (!_state.Roots.Contains(selectedFolder, StringComparer.OrdinalIgnoreCase))
        {
            _state.Roots.Add(selectedFolder);
            StateService.Save(_state);
        }
        _ = ScanAsync();
    }

    private FamilyLibraryProfile? ActiveProfile => _profiles.FirstOrDefault(profile =>
        profile.Name.Equals(_state.ActiveProfileName, StringComparison.OrdinalIgnoreCase));

    private IEnumerable<FamilyItem> ProfileFamilies()
    {
        FamilyLibraryProfile? profile = ActiveProfile;
        if (profile is null) return _allFamilies;
        HashSet<string> paths = profile.FamilyPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return _allFamilies.Where(family => paths.Contains(family.Path));
    }

    private void InitializeProfileCombo()
    {
        string desired = _profiles.Any(profile => profile.Name.Equals(_state.ActiveProfileName, StringComparison.OrdinalIgnoreCase))
            ? _state.ActiveProfileName
            : string.Empty;
        _state.ActiveProfileName = desired;
        _profileCombo.Items.Clear();
        _profileCombo.Items.Add(new ComboBoxItem { Content = "All Families", Tag = string.Empty });
        foreach (FamilyLibraryProfile profile in _profiles.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            _profileCombo.Items.Add(new ComboBoxItem { Content = profile.Name, Tag = profile.Name });
        }
        _profileCombo.SelectedItem = _profileCombo.Items.Cast<ComboBoxItem>()
            .First(item => string.Equals(item.Tag?.ToString(), desired, StringComparison.OrdinalIgnoreCase));
    }

    private void ApplyProfileSelection()
    {
        if (_profileCombo.SelectedItem is not ComboBoxItem selected) return;
        _state.ActiveProfileName = selected.Tag?.ToString() ?? string.Empty;
        _categoryFilter = "*";
        foreach (FamilyItem family in _allFamilies) family.Selected = false;
        StateService.Save(_state);
        PopulateCollections();
        int count = ProfileFamilies().Count();
        SetStatus(string.IsNullOrWhiteSpace(_state.ActiveProfileName)
            ? $"Showing all {_allFamilies.Count:N0} families."
            : $"Profile {_state.ActiveProfileName}: {count:N0} families ready.");
    }

    private void ShowProfileMenu()
    {
        Button anchor = Find<Button>("profile_manage_btn");
        var menu = new ContextMenu
        {
            PlacementTarget = anchor,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
            MinWidth = 230
        };
        var create = new MenuItem { Header = "Create / update profile..." };
        create.Click += (_, _) => CreateOrUpdateProfile();
        var delete = new MenuItem
        {
            Header = "Delete active profile",
            IsEnabled = ActiveProfile is not null
        };
        delete.Click += (_, _) => DeleteActiveProfile();
        menu.Items.Add(create);
        menu.Items.Add(delete);
        anchor.ContextMenu = menu;
        menu.IsOpen = true;
    }

    private void CreateOrUpdateProfile()
    {
        FamilyLibraryProfile? active = ActiveProfile;
        HashSet<string> initiallySelected = active?.FamilyPaths.ToHashSet(StringComparer.OrdinalIgnoreCase)
            ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (initiallySelected.Count == 0 && SelectedFamily is { } current) initiallySelected.Add(current.Path);

        List<FamilyPickerItem> choices = _allFamilies.Select(item => new FamilyPickerItem
        {
            Key = item.Path,
            Path = item.Path,
            Name = item.FamilyName,
            Category = item.Category,
            Selected = initiallySelected.Contains(item.Path)
        }).ToList();
        var selection = new FamilySelectionDialog(
            _window,
            "Create Project Profile",
            "Choose the categories and families used by this project. The profile stores references only, so it remains very small.",
            choices,
            acceptText: "Use selected");
        if (selection.ShowDialog() != true) return;

        var nameDialog = new ProfileNameDialog(_window, active?.Name ?? string.Empty);
        if (nameDialog.ShowDialog() != true) return;
        FamilyLibraryProfile? profile = _profiles.FirstOrDefault(item =>
            item.Name.Equals(nameDialog.ProfileName, StringComparison.OrdinalIgnoreCase));
        if (profile is null)
        {
            profile = new FamilyLibraryProfile { Name = nameDialog.ProfileName };
            _profiles.Add(profile);
        }
        profile.Name = nameDialog.ProfileName;
        profile.FamilyPaths = selection.SelectedFamilies.Select(item => item.Key)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        profile.UpdatedUtc = DateTime.UtcNow;
        ProfileService.Save(_profiles);
        _state.ActiveProfileName = profile.Name;
        StateService.Save(_state);
        InitializeProfileCombo();
        PopulateCollections();
        SetStatus($"Saved profile {profile.Name} with {profile.FamilyPaths.Count:N0} families.");
    }

    private void DeleteActiveProfile()
    {
        FamilyLibraryProfile? profile = ActiveProfile;
        if (profile is null) return;
        MessageBoxResult answer = MessageBox.Show(
            _window,
            $"Delete profile '{profile.Name}'? Family files will not be deleted.",
            "FamilyMEP",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;
        _profiles.Remove(profile);
        ProfileService.Save(_profiles);
        _state.ActiveProfileName = string.Empty;
        StateService.Save(_state);
        InitializeProfileCombo();
        PopulateCollections();
        SetStatus($"Deleted profile {profile.Name}; no family files were removed.");
    }

    private void ToggleCategorySync()
    {
        if (_categorySyncRunning)
        {
            _categorySyncCancelled = true;
            Find<Button>("category_sync_btn").Content = "Stopping...";
            SetStatus("Stopping Revit category sync after the current family...");
            return;
        }
        if (_bulkPreviewRunning)
        {
            MessageBox.Show(_window, "Cancel Cache All before starting category sync.", "FamilyMEP");
            return;
        }

        List<FamilyItem> pending = _allFamilies.Where(family => !family.HasExactCategory).ToList();
        if (pending.Count == 0)
        {
            SetStatus("All family categories already match the Revit Family Editor.");
            return;
        }
        MessageBoxResult answer = MessageBox.Show(
            _window,
            $"Read the exact Family Category from {pending.Count:N0} RFA files?\n\n"
            + "This is a one-time background index. Revit must open each family, so a large library may take several minutes. You can stop and resume later.",
            "Sync Revit Categories",
            MessageBoxButton.YesNo,
            MessageBoxImage.Information);
        if (answer != MessageBoxResult.Yes) return;

        _categorySyncQueue.Clear();
        foreach (FamilyItem family in pending) _categorySyncQueue.Enqueue(family);
        _categorySyncTotal = pending.Count;
        _categorySyncProcessed = 0;
        _categorySyncFailures = 0;
        _categorySyncCancelled = false;
        _categorySyncRunning = true;
        Find<Button>("category_sync_btn").Content = "Cancel Sync";
        ShowOperationProgress("Revit categories", 0, _categorySyncTotal);
        ProcessNextCategorySync();
    }

    private void ProcessNextCategorySync()
    {
        if (!_categorySyncRunning) return;
        if (_categorySyncCancelled || _categorySyncQueue.Count == 0)
        {
            FinishCategorySync();
            return;
        }

        FamilyItem family = _categorySyncQueue.Dequeue();
        List<ResolvedFamilyCategory>? resolved = null;
        string familyPath;
        try { familyPath = ToolLibraryService.EnsureMaterialized(family); }
        catch (Exception exception)
        {
            _categorySyncProcessed++;
            _categorySyncFailures++;
            LogPreviewError(family, exception);
            ProcessNextCategorySync();
            return;
        }
        QueueRevit(
            app => resolved = RevitOperations.ResolveFamilyCategories(app, [familyPath]),
            error =>
            {
                _categorySyncProcessed++;
                if (error is null && resolved is { Count: > 0 })
                {
                    ResolvedFamilyCategory category = resolved[0];
                    FamilyCategoryCacheService.Update(resolved);
                    (string group, string exactCategory) = FamilyScannerService.ClassifyRevitCategory(category.Category);
                    family.Group = group;
                    family.Category = exactCategory;
                    family.HasExactCategory = true;
                }
                else
                {
                    _categorySyncFailures++;
                }

                if (_categorySyncProcessed % 20 == 0)
                {
                    FamilyCategoryCacheService.Save();
                    PopulateCollections();
                }
                SetStatus($"Syncing Revit categories: {_categorySyncProcessed:N0} / {_categorySyncTotal:N0}; {_categorySyncFailures:N0} failed.");
                ShowOperationProgress("Revit categories", _categorySyncProcessed, _categorySyncTotal);
                ProcessNextCategorySync();
            },
            priority: false);
    }

    private void FinishCategorySync()
    {
        _categorySyncRunning = false;
        _categorySyncQueue.Clear();
        try { FamilyCategoryCacheService.Save(); } catch { }
        PopulateCollections();
        Find<Button>("category_sync_btn").Content = "Sync Revit";
        int resolved = _categorySyncProcessed - _categorySyncFailures;
        if (_previewQueued.Count > 0) UpdatePreviewProgress();
        else HideOperationProgress();
        SetStatus(_categorySyncCancelled
            ? $"Category sync stopped: {resolved:N0} resolved and cached; run Sync Revit later to continue."
            : $"Category sync complete: {resolved:N0} resolved, {_categorySyncFailures:N0} failed.");
        if (_autoPreviewAfterCategorySyncPaths.Count > 0)
        {
            List<FamilyItem> autoPreview = _allFamilies
                .Where(item => _autoPreviewAfterCategorySyncPaths.Contains(item.Path))
                .ToList();
            _autoPreviewAfterCategorySyncPaths.Clear();
            StartAutomaticPreviewCache(autoPreview);
        }
    }

    private async Task ToggleBulkPreviewAsync()
    {
        if (_bulkPreviewPreparing) return;
        if (_bulkPreviewRunning)
        {
            _bulkPreviewCancelled = true;
            Find<Button>("bulk_preview_btn").Content = "Stopping...";
            SetStatus("Stopping preview cache after the current family...");
            return;
        }
        if (_categorySyncRunning)
        {
            MessageBox.Show(_window, "Cancel Sync Revit Categories before caching all previews.", "FamilyMEP");
            return;
        }

        _bulkPreviewColor = _state.PreviewColor;
        List<FamilyItem> snapshot = _allFamilies.ToList();
        ShowOperationProgress("Checking preview cache", 0, 0);
        SetStatus("Checking which previews are missing...");
        _bulkPreviewPreparing = true;
        List<FamilyItem> pending;
        try
        {
            pending = await Task.Run(() => snapshot
                .Where(family => (string.IsNullOrWhiteSpace(family.PreviewImage) || !File.Exists(family.PreviewImage))
                    && RevitOperations.FindCachedPreview(family.Path, _bulkPreviewColor, family.Category) is null)
                .ToList());
        }
        finally
        {
            _bulkPreviewPreparing = false;
        }
        if (_categorySyncRunning)
        {
            RestoreOperationProgress();
            return;
        }
        if (pending.Count == 0)
        {
            HideOperationProgress();
            SetStatus($"All {snapshot.Count:N0} families already have cached previews for this color.");
            return;
        }

        MessageBoxResult answer = MessageBox.Show(
            _window,
            $"Generate and cache {pending.Count:N0} missing previews?\n\n"
            + "Revit must open each RFA once. The results are saved after every family, so you can cancel and continue later.",
            "Cache All Previews",
            MessageBoxButton.YesNo,
            MessageBoxImage.Information);
        if (answer != MessageBoxResult.Yes)
        {
            HideOperationProgress();
            return;
        }

        RemovePendingPreviewOperations();
        _bulkPreviewQueue.Clear();
        foreach (FamilyItem family in pending) _bulkPreviewQueue.Enqueue(family);
        _bulkPreviewTotal = pending.Count;
        _bulkPreviewProcessed = 0;
        _bulkPreviewFailures = 0;
        _bulkPreviewCancelled = false;
        _bulkPreviewRunning = true;
        Find<Button>("bulk_preview_btn").Content = "Cancel 3D";
        ShowOperationProgress("Preview cache", 0, _bulkPreviewTotal);
        ProcessNextBulkPreview();
    }

    private void ProcessNextBulkPreview()
    {
        if (!_bulkPreviewRunning) return;
        if (_bulkPreviewCancelled || _bulkPreviewQueue.Count == 0)
        {
            FinishBulkPreview();
            return;
        }

        FamilyItem family = _bulkPreviewQueue.Dequeue();
        PreviewCreationResult? result = null;
        string familyPath;
        try { familyPath = ToolLibraryService.EnsureMaterialized(family); }
        catch (Exception exception)
        {
            _bulkPreviewProcessed++;
            _bulkPreviewFailures++;
            family.Status = "Preview unavailable";
            LogPreviewError(family, exception);
            ProcessNextBulkPreview();
            return;
        }
        QueueRevit(
            app => result = RevitOperations.CreatePreview(
                app,
                familyPath,
                _bulkPreviewColor,
                force: false,
                familyCategory: family.Category),
            error =>
            {
                _bulkPreviewProcessed++;
                if (error is null && result is not null)
                {
                    family.PreviewImage = result.PreviewPath;
                    family.Status = "OK";
                    ApplyRevitCategory(family, result.RevitCategory, persist: false);
                    if (SelectedFamily?.Path.Equals(family.Path, StringComparison.OrdinalIgnoreCase) == true)
                    {
                        Find<Image>("browser_preview_image").Source = LoadBitmap(result.PreviewPath);
                        Find<TextBlock>("browser_preview_status_tb").Text = PreviewGeneratedStatus(family);
                    }
                }
                else
                {
                    _bulkPreviewFailures++;
                    family.Status = "Preview unavailable";
                    LogPreviewError(family, error);
                }

                if (_bulkPreviewProcessed % 20 == 0)
                {
                    try { FamilyCategoryCacheService.Save(); } catch { }
                }
                ShowOperationProgress("Preview cache", _bulkPreviewProcessed, _bulkPreviewTotal);
                SetStatus($"Caching previews: {_bulkPreviewProcessed:N0} / {_bulkPreviewTotal:N0}; {_bulkPreviewFailures:N0} failed.");
                ProcessNextBulkPreview();
            },
            priority: false);
    }

    private void StartAutomaticPreviewCache(IReadOnlyCollection<FamilyItem> candidates)
    {
        if (candidates.Count == 0) return;
        if (_bulkPreviewRunning || _bulkPreviewPreparing || _categorySyncRunning)
        {
            SetStatus($"Families were saved to Tool Library. Run Cache All later for {candidates.Count:N0} new family/families.");
            return;
        }
        _bulkPreviewColor = _state.PreviewColor;
        List<FamilyItem> pending = candidates
            .Where(family => (string.IsNullOrWhiteSpace(family.PreviewImage) || !File.Exists(family.PreviewImage))
                && RevitOperations.FindCachedPreview(family.Path, _bulkPreviewColor, family.Category) is null)
            .ToList();
        if (pending.Count == 0) return;

        RemovePendingPreviewOperations();
        _bulkPreviewQueue.Clear();
        foreach (FamilyItem family in pending) _bulkPreviewQueue.Enqueue(family);
        _bulkPreviewTotal = pending.Count;
        _bulkPreviewProcessed = 0;
        _bulkPreviewFailures = 0;
        _bulkPreviewCancelled = false;
        _bulkPreviewRunning = true;
        Find<Button>("bulk_preview_btn").Content = "Cancel 3D";
        ShowOperationProgress("Auto-cache new families", 0, _bulkPreviewTotal);
        SetStatus($"Automatically generating previews: 0 / {_bulkPreviewTotal:N0}.");
        ProcessNextBulkPreview();
    }

    private void StartAutomaticIndex(IReadOnlyCollection<FamilyItem> candidates)
    {
        if (candidates.Count == 0) return;
        List<FamilyItem> missingCategories = candidates.Where(item => !item.HasExactCategory).ToList();
        if (missingCategories.Count == 0)
        {
            StartAutomaticPreviewCache(candidates);
            return;
        }
        if (_categorySyncRunning || _bulkPreviewRunning || _bulkPreviewPreparing)
        {
            SetStatus($"Families were saved. Run Sync Revit and Cache All later for {candidates.Count:N0} new family/families.");
            return;
        }

        _autoPreviewAfterCategorySyncPaths.Clear();
        foreach (FamilyItem family in candidates) _autoPreviewAfterCategorySyncPaths.Add(family.Path);
        _categorySyncQueue.Clear();
        foreach (FamilyItem family in missingCategories) _categorySyncQueue.Enqueue(family);
        _categorySyncTotal = missingCategories.Count;
        _categorySyncProcessed = 0;
        _categorySyncFailures = 0;
        _categorySyncCancelled = false;
        _categorySyncRunning = true;
        Find<Button>("category_sync_btn").Content = "Cancel Sync";
        ShowOperationProgress("Auto-index categories", 0, _categorySyncTotal);
        SetStatus($"Automatically indexing Revit categories: 0 / {_categorySyncTotal:N0}.");
        ProcessNextCategorySync();
    }

    private void FinishBulkPreview()
    {
        _bulkPreviewRunning = false;
        _bulkPreviewQueue.Clear();
        try { FamilyCategoryCacheService.Save(); } catch { }
        PopulateCollections();
        Find<Button>("bulk_preview_btn").Content = "Cache All";
        HideOperationProgress();
        int completed = _bulkPreviewProcessed - _bulkPreviewFailures;
        SetStatus(_bulkPreviewCancelled
            ? $"Preview cache stopped: {completed:N0} previews saved; run Cache All later to continue."
            : $"Preview cache complete: {completed:N0} generated, {_bulkPreviewFailures:N0} failed.");
        ScheduleVisiblePreviews();
    }

    private static void ApplyRevitCategory(FamilyItem family, string? revitCategory, bool persist)
    {
        if (string.IsNullOrWhiteSpace(revitCategory)) return;
        var resolved = new ResolvedFamilyCategory(family.Path, revitCategory);
        FamilyCategoryCacheService.Update([resolved]);
        (string group, string exactCategory) = FamilyScannerService.ClassifyRevitCategory(revitCategory);
        family.Group = group;
        family.Category = exactCategory;
        family.HasExactCategory = true;
        if (persist) FamilyCategoryCacheService.Save();
    }

    private async Task ScanAsync()
    {
        SetStatus("Scanning RFA files on drive F...");
        if (!_categorySyncRunning) ShowOperationProgress("Scanning library", 0, 0);
        SetBusy(true);
        try
        {
            List<string> roots = _state.Roots.ToList();
            HashSet<string> favorites = new(_state.Favorites, StringComparer.OrdinalIgnoreCase);
            _allFamilies = await Task.Run(() =>
            {
                List<FamilyItem> physical = FamilyScannerService.Scan(roots, favorites);
                foreach (FamilyItem family in physical)
                    family.IsToolLibrary = ToolLibraryService.IsUserLibraryPath(family.Path);
                HashSet<string> physicalNames = physical.Select(item => item.FamilyName)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                List<FamilyItem> builtIn = ToolLibraryService.ScanBuiltInLibraries(favorites)
                    .Where(item => !physicalNames.Contains(item.FamilyName))
                    .ToList();
                physical.AddRange(builtIn);
                return physical.OrderBy(item => item.FamilyName, StringComparer.CurrentCultureIgnoreCase).ToList();
            });
            int cachedPreviews = 0;
            foreach (FamilyItem family in _allFamilies)
            {
                family.PreviewImage ??= RevitOperations.FindCachedPreview(
                    family.Path,
                    _state.PreviewColor,
                    family.Category);
                if (family.PreviewImage is not null) cachedPreviews++;
            }
            PopulateCollections();
            SetStatus($"Ready - {_allFamilies.Count:N0} families; {cachedPreviews:N0} previews restored from cache");
            ScheduleVisiblePreviews();
        }
        catch (Exception exception)
        {
            SetStatus("Scan failed");
            MessageBox.Show(_window, exception.Message, "FamilyMEP");
        }
        finally
        {
            SetBusy(false);
            if (!_categorySyncRunning && !_bulkPreviewRunning) RestoreOperationProgress();
        }
    }

    private void PopulateCollections()
    {
        List<FamilyItem> profileFamilies = ProfileFamilies().ToList();
        PopulateCategoryTree(profileFamilies);
        int builtInArchives = Directory.Exists(AppPaths.BuiltInLibraryFolder)
            ? Directory.EnumerateFiles(AppPaths.BuiltInLibraryFolder, "*", SearchOption.TopDirectoryOnly)
                .Count(ToolLibraryService.IsSupportedLibraryArchive)
            : 0;
        Find<TextBlock>("browser_root_summary_tb").Text =
            $"Tool Library: {profileFamilies.Count(item => item.IsToolLibrary):N0} families\n"
            + $"Built-in archives: {builtInArchives:N0}\n"
            + $"External roots: {Math.Max(0, _state.Roots.Count - 1):N0}";
        Find<DataGrid>("batch_family_grid").ItemsSource = profileFamilies;
        Find<DataGrid>("batch_log_grid").ItemsSource = _batchLogs;
        Find<DataGrid>("batch2_log_grid").ItemsSource = _batchLogs;
        Find<DataGrid>("batch_changes_grid").ItemsSource = _batchChanges;
        ApplyBatchFamilyFilter();
        Find<TextBlock>("batch_scope_profile_tb").Text = string.IsNullOrWhiteSpace(_state.ActiveProfileName) ? "All Families" : _state.ActiveProfileName;
        Find<TextBlock>("batch_scope_categories_tb").Text = CategoryFilterLabel();
        Find<ListBox>("preview_family_list").ItemsSource = profileFamilies;
        Find<ListBox>("favorite_cards").ItemsSource = profileFamilies.Where(item => item.Favorite).ToList();
        Find<DataGrid>("report_grid").ItemsSource = profileFamilies;
        Find<TextBlock>("footer_total_tb").Text = $"Total Families: {profileFamilies.Count:N0}";
        Find<TextBlock>("footer_selected_tb").Text = $"Selected: {profileFamilies.Count(item => item.Selected):N0}";
        Find<TextBlock>("footer_last_scan_tb").Text = $"Last scan: {DateTime.Now:HH:mm:ss}";
        Find<TextBlock>("report_total_tb").Text = profileFamilies.Count.ToString("N0");
        Find<TextBlock>("report_duct_tb").Text = profileFamilies.Count(item => item.Group == "MEP Model").ToString("N0");
        Find<TextBlock>("report_pipe_tb").Text = profileFamilies.Count(item => item.Group == "Annotation").ToString("N0");
        Find<TextBlock>("report_unclassified_tb").Text = profileFamilies.Count(item => item.Category == "Unclassified").ToString("N0");
        Find<TextBlock>("report_warnings_tb").Text = profileFamilies.Count(item => item.Status != "OK").ToString("N0");
        RefreshFamilyView();
        UpdateBatchSummary();
    }

    private void PopulateCategoryTree(IReadOnlyCollection<FamilyItem> profileFamilies)
    {
        List<CategoryItem> categories = [];
        foreach (IGrouping<string, FamilyItem> group in profileFamilies
                     .GroupBy(item => item.Group)
                     .OrderBy(group => LibraryGroupOrder(group.Key))
                     .ThenBy(group => group.Key, StringComparer.CurrentCultureIgnoreCase))
        {
            bool expanded = !_collapsedCategoryGroups.Contains(group.Key);
            categories.Add(new CategoryItem
            {
                Name = group.Key,
                FilterKey = $"group:{group.Key}",
                Count = group.Count(),
                IsGroup = true,
                IsExpanded = expanded
            });
            if (!expanded) continue;
            foreach (IGrouping<string, FamilyItem> category in group.GroupBy(item => item.Category).OrderBy(item => item.Key))
            {
                categories.Add(new CategoryItem { Name = category.Key, FilterKey = $"category:{category.Key}", Count = category.Count() });
            }
        }
        categories.Add(new CategoryItem { Name = "Favorites", FilterKey = "favorites", Count = profileFamilies.Count(item => item.Favorite) });
        categories.Add(new CategoryItem { Name = "All Families", FilterKey = "*", Count = profileFamilies.Count });

        _updatingCategoryList = true;
        try
        {
            _categoryList.ItemsSource = categories;
            CategoryItem? selectedCategory = categories.FirstOrDefault(item => item.FilterKey.Equals(_categoryFilter, StringComparison.OrdinalIgnoreCase))
                ?? categories.LastOrDefault(item => item.FilterKey == "*");
            _categoryList.SelectedItem = selectedCategory;
            _categoryFilter = selectedCategory?.FilterKey ?? "*";
        }
        finally
        {
            _updatingCategoryList = false;
        }
    }

    private static int LibraryGroupOrder(string group) => group switch
    {
        "MEP Model" => 0,
        "Annotation" => 1,
        "Support" => 2,
        _ => 3
    };

    private void ScrollCategoryTree(object sender, MouseWheelEventArgs eventArgs)
    {
        ScrollViewer? viewer = FindVisualChild<ScrollViewer>(_categoryList);
        if (viewer is null || viewer.ScrollableHeight <= 0) return;
        double steps = eventArgs.Delta / 120d;
#if NET48
        viewer.ScrollToVerticalOffset(FrameworkCompat.Clamp(viewer.VerticalOffset - (steps * 78d), 0, viewer.ScrollableHeight));
#else
        viewer.ScrollToVerticalOffset(Math.Clamp(viewer.VerticalOffset - (steps * 78d), 0, viewer.ScrollableHeight));
#endif
        eventArgs.Handled = true;
    }

    private void ToggleCategoryGroupFromClick(object sender, MouseButtonEventArgs eventArgs)
    {
        DependencyObject? source = eventArgs.OriginalSource as DependencyObject;
        while (source is not null && source is not ListBoxItem)
        {
            source = System.Windows.Media.VisualTreeHelper.GetParent(source);
        }
        if (source is not ListBoxItem { DataContext: CategoryItem { IsGroup: true } category }) return;

        if (!_collapsedCategoryGroups.Add(category.Name))
        {
            _collapsedCategoryGroups.Remove(category.Name);
        }
        _categoryFilter = category.FilterKey;
        PopulateCategoryTree(ProfileFamilies().ToList());
        RefreshFamilyView();
        ApplyBatchFamilyFilter();
        Find<TextBlock>("batch_scope_categories_tb").Text = category.Name;
        eventArgs.Handled = true;
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (int index = 0; index < count; index++)
        {
            DependencyObject child = System.Windows.Media.VisualTreeHelper.GetChild(parent, index);
            if (child is T match) return match;
            T? descendant = FindVisualChild<T>(child);
            if (descendant is not null) return descendant;
        }
        return null;
    }

    private void RefreshFamilyView()
    {
        if (_familyCards is null) return;
        IEnumerable<FamilyItem> query = ProfileFamilies();
        query = _categoryFilter switch
        {
            "favorites" => query.Where(item => item.Favorite),
            var key when key.StartsWith("group:") => query.Where(item => item.Group.Equals(key.Substring(6), StringComparison.OrdinalIgnoreCase)),
            var key when key.StartsWith("category:") => query.Where(item => item.Category.Equals(key.Substring(9), StringComparison.OrdinalIgnoreCase)),
            _ => query
        };

        string search = _searchBox?.Text?.Trim() ?? string.Empty;
        if (search.Length > 0)
        {
            query = query.Where(item =>
                item.FamilyName.Contains(search, StringComparison.OrdinalIgnoreCase)
                || item.Category.Contains(search, StringComparison.OrdinalIgnoreCase)
                || item.Path.Contains(search, StringComparison.OrdinalIgnoreCase));
        }

        query = _sortCombo?.SelectedIndex switch
        {
            1 => query.OrderByDescending(item => item.ModifiedDate),
            2 => query.OrderBy(item => item.Category).ThenBy(item => item.FamilyName),
            _ => query.OrderBy(item => item.FamilyName, StringComparer.CurrentCultureIgnoreCase)
        };
        _filteredFamilies = query.ToList();
        _familyPageIndex = 0;
        ApplyFamilyPage();
    }

    private void ChangeFamilyPage(int direction)
    {
        int pageCount = Math.Max(1, (int)Math.Ceiling(_filteredFamilies.Count / (double)FamilyPageSize));
        int next = Clamp(_familyPageIndex + direction, 0, pageCount - 1);
        if (next == _familyPageIndex) return;
        _familyPageIndex = next;
        ApplyFamilyPage();
    }

    private void ApplyFamilyPage()
    {
        int total = _filteredFamilies.Count;
        int pageCount = Math.Max(1, (int)Math.Ceiling(total / (double)FamilyPageSize));
        _familyPageIndex = Clamp(_familyPageIndex, 0, pageCount - 1);
        int start = _familyPageIndex * FamilyPageSize;
        List<FamilyItem> page = _filteredFamilies.Skip(start).Take(FamilyPageSize).ToList();
        _familyCards.ItemsSource = page;
        Find<TextBlock>("browser_library_title_tb").Text = $"Family Library ({total:N0})";
        Find<TextBlock>("family_page_info_tb").Text = total == 0
            ? "No families"
            : $"Page {_familyPageIndex + 1:N0} / {pageCount:N0}  |  {start + 1:N0}-{start + page.Count:N0}";
        Find<Button>("family_page_previous_btn").IsEnabled = _familyPageIndex > 0;
        Find<Button>("family_page_next_btn").IsEnabled = _familyPageIndex < pageCount - 1;
        if (page.Count > 0) _familyCards.ScrollIntoView(page[0]);
        ScheduleVisiblePreviews();
    }

    private FamilyItem? SelectedFamily => _familyCards.SelectedItem as FamilyItem;

    private void UpdateDetails()
    {
        FamilyItem? family = SelectedFamily;
        Find<TextBlock>("detail_name_tb").Text = family?.FamilyName ?? string.Empty;
        Find<TextBlock>("detail_category_tb").Text = family?.Category ?? string.Empty;
        Find<TextBlock>("detail_path_tb").Text = family?.IsBuiltIn == true
            ? $"{family.ArchivePath}\n{family.ArchiveEntryPath}"
            : family?.Path ?? string.Empty;
        Find<TextBlock>("detail_size_tb").Text = family?.Size ?? string.Empty;
        Find<TextBlock>("detail_modified_tb").Text = family?.Modified ?? string.Empty;
        Find<TextBlock>("detail_version_tb").Text = family?.Version ?? string.Empty;
        Find<TextBlock>("detail_shared_tb").Text = family?.Shared ?? string.Empty;
        Find<TextBlock>("footer_selected_tb").Text = $"Selected: {(family is null ? 0 : 1)}";
        ResetInspectionDisplay();
        ShowDetailPanel("Info");
        if (family is not null) RefreshPreview(false);
    }

    private void ShowDetailPanel(string panel)
    {
        Find<Grid>("detail_info_panel").Visibility = panel == "Info" ? Visibility.Visible : Visibility.Collapsed;
        Find<Grid>("detail_types_panel").Visibility = panel == "Types" ? Visibility.Visible : Visibility.Collapsed;
        Find<Grid>("detail_parameters_panel").Visibility = panel == "Parameters" ? Visibility.Visible : Visibility.Collapsed;
        SetTabState("detail_info_tab_tb", panel == "Info");
        SetTabState("detail_types_tab_tb", panel == "Types");
        SetTabState("detail_parameters_tab_tb", panel == "Parameters");
    }

    private void SetTabState(string name, bool active)
    {
        TextBlock text = Find<TextBlock>(name);
        text.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
        text.SetResourceReference(TextBlock.ForegroundProperty, active ? "AccentBrush" : "InkBrush");
    }

    private void ShowFamilyInspection(string panel)
    {
        ShowDetailPanel(panel);
        FamilyItem? family = SelectedFamily;
        if (family is null) return;
        string cacheKey = InspectionKey(family);
        if (_inspectionCache.TryGetValue(cacheKey, out FamilyInspectionResult? cached))
        {
            ApplyInspection(cached);
            return;
        }
        if (!_inspectionQueued.Add(cacheKey)) return;

        Find<TextBlock>("detail_types_empty_tb").Text = "Reading family types...";
        Find<TextBlock>("detail_parameters_empty_tb").Text = "Reading family parameters...";
        SetStatus($"Reading types and parameters from {family.FamilyName}...");
        FamilyInspectionResult? result = null;
        string familyPath;
        try { familyPath = ToolLibraryService.EnsureMaterialized(family); }
        catch (Exception exception)
        {
            _inspectionQueued.Remove(cacheKey);
            ShowError(exception);
            return;
        }
        QueueRevit(
            app => result = RevitOperations.InspectFamily(app, familyPath),
            error =>
            {
                _inspectionQueued.Remove(cacheKey);
                if (error is not null)
                {
                    Find<TextBlock>("detail_types_empty_tb").Text = "Unable to read family types.";
                    Find<TextBlock>("detail_parameters_empty_tb").Text = "Unable to read family parameters.";
                    ShowError(error);
                    return;
                }
                if (result is null) return;
                _inspectionCache[cacheKey] = result;
                if (SelectedFamily is { } selected && InspectionKey(selected) == cacheKey)
                {
                    ApplyInspection(result);
                }
                SetStatus($"Loaded {result.Types.Count:N0} types and {result.Parameters.Count:N0} parameters.");
            });
    }

    private void ApplyInspection(FamilyInspectionResult result)
    {
        Find<ListBox>("detail_types_list").ItemsSource = result.Types;
        Find<ListBox>("detail_parameters_list").ItemsSource = result.Parameters;
        Find<TextBlock>("detail_types_tab_tb").Text = $"Types ({result.Types.Count:N0})";
        Find<TextBlock>("detail_parameters_tab_tb").Text = $"Parameters ({result.Parameters.Count:N0})";
        Find<TextBlock>("detail_types_empty_tb").Visibility = result.Types.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Find<TextBlock>("detail_parameters_empty_tb").Visibility = result.Parameters.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (result.Types.Count == 0) Find<TextBlock>("detail_types_empty_tb").Text = "This family has no named types.";
        if (result.Parameters.Count == 0) Find<TextBlock>("detail_parameters_empty_tb").Text = "This family has no parameters.";
    }

    private void ResetInspectionDisplay()
    {
        Find<ListBox>("detail_types_list").ItemsSource = null;
        Find<ListBox>("detail_parameters_list").ItemsSource = null;
        Find<TextBlock>("detail_types_tab_tb").Text = "Types";
        Find<TextBlock>("detail_parameters_tab_tb").Text = "Parameters";
        Find<TextBlock>("detail_types_empty_tb").Text = "Click Types to read family types.";
        Find<TextBlock>("detail_parameters_empty_tb").Text = "Click Parameters to read family parameters.";
        Find<TextBlock>("detail_types_empty_tb").Visibility = Visibility.Visible;
        Find<TextBlock>("detail_parameters_empty_tb").Visibility = Visibility.Visible;
    }

    private static string InspectionKey(FamilyItem family) => $"{family.Path}|{family.ModifiedDate.Ticks}";

    private void ToggleFavoriteFromStar(object sender, MouseButtonEventArgs eventArgs)
    {
        DependencyObject? source = eventArgs.OriginalSource as DependencyObject;
        bool favoriteButton = false;
        ListBoxItem? item = null;
        while (source is not null)
        {
            if (source is FrameworkElement { Tag: "favorite-toggle" }) favoriteButton = true;
            if (source is ListBoxItem listBoxItem)
            {
                item = listBoxItem;
                break;
            }
            source = System.Windows.Media.VisualTreeHelper.GetParent(source);
        }
        if (!favoriteButton || item?.DataContext is not FamilyItem family) return;
        ToggleFavorite(family);
        eventArgs.Handled = true;
    }

    private void ToggleFavorite(FamilyItem? target = null)
    {
        FamilyItem? family = target ?? SelectedFamily;
        if (family is null) return;
        family.Favorite = !family.Favorite;
        if (family.Favorite) _state.Favorites.Add(family.Path);
        else _state.Favorites.Remove(family.Path);
        StateService.Save(_state);
        Find<ListBox>("favorite_cards").ItemsSource = ProfileFamilies().Where(item => item.Favorite).ToList();
        PopulateCategoryTree(ProfileFamilies().ToList());
        if (_categoryFilter == "favorites") RefreshFamilyView();
        SetStatus(family.Favorite ? $"Added {family.FamilyName} to Favorites." : $"Removed {family.FamilyName} from Favorites.");
    }

    private void UpdatePreviewTabDetails()
    {
        if (Find<ListBox>("preview_family_list").SelectedItem is not FamilyItem family) return;
        Find<TextBlock>("preview_name_tb").Text = family.FamilyName;
        Find<TextBlock>("preview_category_tb").Text = family.Category;
        Find<TextBlock>("preview_size_tb").Text = family.Size;
        Find<TextBlock>("preview_path_tb").Text = family.Path;
    }

    private void RefreshPreview(bool force)
    {
        FamilyItem? family = SelectedFamily;
        if (family is null) return;
        if (!force && !string.IsNullOrWhiteSpace(family.PreviewImage) && File.Exists(family.PreviewImage))
        {
            Find<Image>("browser_preview_image").Source = LoadBitmap(family.PreviewImage);
            Find<TextBlock>("browser_preview_status_tb").Text = family.Group == "Annotation"
                ? "Cached annotation preview"
                : family.IsBuiltIn ? "Built-in cached 3D preview" : "Cached 3D preview";
            return;
        }
        if (!force
            && RevitOperations.FindCachedPreview(family.Path, _state.PreviewColor, family.Category) is { } cached)
        {
            family.PreviewImage = cached;
            Find<Image>("browser_preview_image").Source = LoadBitmap(cached);
            Find<TextBlock>("browser_preview_status_tb").Text = family.Group == "Annotation"
                ? "Cached annotation preview"
                : "Cached 3D preview";
            return;
        }

        Find<TextBlock>("browser_preview_status_tb").Text = "Generating preview...";
        EnqueuePreview(family, force, true);
    }

    private void ScheduleVisiblePreviews()
    {
        if (_disposed) return;
        _previewViewportTimer.Stop();
        _previewViewportTimer.Start();
    }

    private void QueueVisiblePreviews()
    {
        _previewViewportTimer.Stop();
        if (_disposed || _bulkPreviewRunning || _familyCards.Items.Count == 0) return;

        RemovePendingPreviewOperations();
        var visibleFamilies = new List<FamilyItem>();
        int realizedContainers = 0;
        Rect viewport = new(0, 0, _familyCards.ActualWidth, _familyCards.ActualHeight);
        foreach (FamilyItem family in _familyCards.Items.Cast<FamilyItem>())
        {
            if (_familyCards.ItemContainerGenerator.ContainerFromItem(family) is not ListBoxItem container) continue;
            realizedContainers++;
            if (!string.IsNullOrWhiteSpace(family.PreviewImage) || family.Status != "OK") continue;
            try
            {
                Rect bounds = container.TransformToAncestor(_familyCards)
                    .TransformBounds(new Rect(new Point(0, 0), container.RenderSize));
                if (viewport.IntersectsWith(bounds)) visibleFamilies.Add(family);
            }
            catch
            {
                // Layout can still be updating; the debounce timer will run again.
            }
        }

        if (visibleFamilies.Count == 0 && realizedContainers == 0)
        {
            visibleFamilies.AddRange(_familyCards.Items.Cast<FamilyItem>()
                .Where(item => string.IsNullOrWhiteSpace(item.PreviewImage) && item.Status == "OK")
                .Take(6));
        }

        foreach (FamilyItem family in visibleFamilies.Take(6))
        {
            EnqueuePreview(family, false, false);
        }

        if (SelectedFamily is { PreviewImage: null, Status: "OK" } selected)
        {
            EnqueuePreview(selected, false, true);
        }
        UpdatePreviewProgress();
    }

    private void EnqueuePreview(FamilyItem family, bool force, bool priority)
    {
        string previewColor = _state.PreviewColor;
        string key = $"{family.Path}|{previewColor}";
        if (_previewQueued.Contains(key))
        {
            if (priority)
            {
                LinkedListNode<RevitOperation>? node = _revitOperations.First;
                while (node is not null)
                {
                    LinkedListNode<RevitOperation>? next = node.Next;
                    if (string.Equals(node.Value.Key, key, StringComparison.OrdinalIgnoreCase))
                    {
                        _revitOperations.Remove(node);
                        _revitOperations.AddFirst(node);
                        break;
                    }
                    node = next;
                }
            }
            return;
        }

        if (_previewQueued.Count == 0)
        {
            _previewProgressTotal = 0;
            _previewProgressCompleted = 0;
        }
        _previewQueued.Add(key);
        _previewProgressTotal++;
        PreviewCreationResult? previewResult = null;
        string familyPath;
        try { familyPath = ToolLibraryService.EnsureMaterialized(family); }
        catch (Exception exception)
        {
            _previewQueued.Remove(key);
            _previewProgressCompleted++;
            family.Status = "Preview unavailable";
            LogPreviewError(family, exception);
            UpdatePreviewProgress();
            return;
        }
        QueueRevit(
            app => previewResult = RevitOperations.CreatePreview(
                app,
                familyPath,
                previewColor,
                force,
                family.Category),
            error =>
            {
                _previewQueued.Remove(key);
                _previewProgressCompleted++;
                if (!string.Equals(previewColor, _state.PreviewColor, StringComparison.OrdinalIgnoreCase))
                {
                    UpdatePreviewProgress();
                    ScheduleVisiblePreviews();
                    return;
                }
                if (error is null && previewResult is not null)
                {
                    family.PreviewImage = previewResult.PreviewPath;
                    ApplyRevitCategory(family, previewResult.RevitCategory, persist: true);
                    if (SelectedFamily?.Path.Equals(family.Path, StringComparison.OrdinalIgnoreCase) == true)
                    {
                        Find<Image>("browser_preview_image").Source = LoadBitmap(previewResult.PreviewPath);
                        Find<TextBlock>("browser_preview_status_tb").Text = PreviewGeneratedStatus(family);
                    }
                }
                else
                {
                    family.Status = "Preview unavailable";
                    LogPreviewError(family, error);
                    if (SelectedFamily?.Path.Equals(family.Path, StringComparison.OrdinalIgnoreCase) == true)
                    {
                        Find<TextBlock>("browser_preview_status_tb").Text = error?.Message ?? "Preview unavailable";
                    }
                }
                UpdatePreviewProgress();
                if (_previewQueued.Count == 0) ScheduleVisiblePreviews();
            },
            priority,
            true,
            key);
    }

    private void UpdatePreviewProgress()
    {
        int remaining = Math.Max(0, _previewQueued.Count);
        if (!_categorySyncRunning)
        {
            if (remaining > 0) ShowOperationProgress("Previews", _previewProgressCompleted, Math.Max(_previewProgressTotal, 1));
            else HideOperationProgress();
        }
        SetStatus(remaining > 0
            ? $"Generating visible previews: {remaining:N0} remaining"
            : $"Ready — {_allFamilies.Count:N0} families; previews load on demand");
    }

    private static string PreviewGeneratedStatus(FamilyItem family) =>
        family.Group == "Annotation" ? "Annotation preview generated" : "3D preview generated";

    private void RemovePendingPreviewOperations()
    {
        LinkedListNode<RevitOperation>? node = _revitOperations.First;
        while (node is not null)
        {
            LinkedListNode<RevitOperation>? next = node.Next;
            if (node.Value.IsPreview)
            {
                _previewQueued.Remove(node.Value.Key ?? string.Empty);
                _previewProgressTotal = Math.Max(_previewProgressCompleted, _previewProgressTotal - 1);
                _revitOperations.Remove(node);
            }
            node = next;
        }
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

    private static void LogPreviewError(FamilyItem family, Exception? error)
    {
        try
        {
            AppPaths.EnsureCreated();
            string logPath = Path.Combine(AppPaths.LogFolder, "preview_errors.log");
            File.AppendAllText(
                logPath,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\t{family.Path}\t{error?.Message ?? "Preview unavailable"}{Environment.NewLine}");
        }
        catch
        {
            // Preview diagnostics must not interrupt the browser.
        }
    }

    private void OpenSelectedFolder()
    {
        FamilyItem? family = SelectedFamily;
        if (family is null) return;
        string selectedPath = family.IsBuiltIn && File.Exists(family.ArchivePath)
            ? family.ArchivePath!
            : family.Path;
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{selectedPath}\"") { UseShellExecute = true });
    }

    private void OpenSelectedFamily()
    {
        FamilyItem? family = SelectedFamily;
        if (family is null) return;
        string familyPath;
        try { familyPath = ToolLibraryService.EnsureMaterialized(family); }
        catch (Exception exception) { ShowError(exception); return; }
        QueueRevit(app => RevitOperations.OpenFamily(app, familyPath), ShowRevitResult("Family opened."));
    }

    private void LoadSelectedFamilies()
    {
        List<ProjectDocumentItem>? projects = null;
        QueueRevit(
            app => projects = RevitOperations.GetOpenProjects(app),
            error =>
            {
                if (error is not null) { ShowError(error); return; }
                if (projects is null || projects.Count == 0)
                {
                    MessageBox.Show(_window, "Open at least one Revit project first.", "FamilyMEP");
                    return;
                }

                string? currentPath = SelectedFamily?.Path;
                bool profileActive = ActiveProfile is not null;
                List<FamilyPickerItem> choices = ProfileFamilies().Select(item => new FamilyPickerItem
                {
                    Key = item.Path,
                    Name = item.FamilyName,
                    Category = item.Category,
                    Path = item.Path,
                    Selected = profileActive || item.Selected || string.Equals(item.Path, currentPath, StringComparison.OrdinalIgnoreCase)
                }).ToList();
                var dialog = new FamilySelectionDialog(
                    _window,
                    "Load selected families to project",
                    "Choose the destination project, then select categories and individual families to load.",
                    choices,
                    projects);
                if (dialog.ShowDialog() != true || dialog.SelectedProject is null) return;

                HashSet<string> selectedPaths = dialog.SelectedFamilies
                    .Select(item => item.Path!)
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                List<FamilyItem> selectedFamilies = ProfileFamilies()
                    .Where(item => selectedPaths.Contains(item.Path))
                    .ToList();
                string projectKey = dialog.SelectedProject.Key;
                string projectName = dialog.SelectedProject.DisplayName;
                var pendingFamilies = new Queue<FamilyItem>(selectedFamilies);
                int processed = 0;
                int loaded = 0;
                int skipped = 0;
                ShowOperationProgress("Load to project", 0, selectedFamilies.Count);

                async void LoadNextBatch()
                {
                    if (pendingFamilies.Count == 0)
                    {
                        SetStatus($"Loaded {loaded} family/families into {projectName}; skipped {skipped} duplicate(s).");
                        RestoreOperationProgress();
                        return;
                    }

                    List<FamilyItem> batchFamilies = [];
                    while (batchFamilies.Count < 20 && pendingFamilies.Count > 0) batchFamilies.Add(pendingFamilies.Dequeue());
                    List<string> batch;
                    try
                    {
                        SetStatus($"Preparing {batchFamilies.Count:N0} family file(s) from Tool Library...");
                        batch = await Task.Run(() => batchFamilies.Select(ToolLibraryService.EnsureMaterialized).ToList());
                    }
                    catch (Exception exception)
                    {
                        ShowError(exception);
                        RestoreOperationProgress();
                        return;
                    }
                    FamilyLoadResult batchResult = new(0, 0);
                    QueueRevit(
                        app => batchResult = RevitOperations.LoadFamiliesToProject(app, projectKey, batch),
                        loadError =>
                        {
                            if (loadError is not null)
                            {
                                ShowError(loadError);
                                RestoreOperationProgress();
                                return;
                            }
                            loaded += batchResult.Loaded;
                            skipped += batchResult.Skipped;
                            processed += batch.Count;
                            ShowOperationProgress("Load to project", processed, selectedFamilies.Count);
                            SetStatus($"Loading families into {projectName}: {processed:N0} / {selectedFamilies.Count:N0}.");
                            LoadNextBatch();
                        });
                }

                LoadNextBatch();
            });
    }

    private void ExportFromProject()
    {
        var projectDialog = new OpenFileDialog
        {
            Title = "Choose the source Revit project",
            Filter = "Revit Projects (*.rvt)|*.rvt",
            Multiselect = false
        };
        if (projectDialog.ShowDialog(_window) != true) return;
        StartProjectFamilyImport(projectDialog.FileName);
    }

    private void SaveStandardsSourceToToolLibrary()
    {
        ViewStandardsProject? source = _selectedStandardsProject;
        if (source is null)
        {
            MessageBox.Show(
                _window,
                "Add or select an RVT source first.",
                "FamilyMEP",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        string projectPath = File.Exists(source.OriginalPath)
            ? source.OriginalPath
            : source.SnapshotPath;
        if (!File.Exists(projectPath))
        {
            ShowError(new FileNotFoundException("The selected RVT source is no longer available.", projectPath));
            return;
        }

        SetStatus($"Standards are already saved. Reading families and annotations from {source.Name}...");
        StartProjectFamilyImport(projectPath);
    }

    private void StartProjectFamilyImport(string projectPath)
    {
        if (_projectFamilyExportTotal > 0
            || _projectFamilyExportSession is not null
            || _projectFamilyExportQueue.Count > 0)
        {
            MessageBox.Show(_window, "Save to Tool Library is already running.", "FamilyMEP");
            return;
        }

        List<ProjectFamilyItem>? projectFamilies = null;
        ShowOperationProgress(
            "Reading project families",
            0,
            0,
            "Opening RVT  ·  Phase 1 / 2");
        SetStatus("Reading loadable families and annotations from the RVT source...");
        QueueRevit(
            app => projectFamilies = RevitOperations.InspectProjectFamilies(app, projectPath),
            error =>
            {
                if (error is not null)
                {
                    RestoreOperationProgress();
                    ShowError(error);
                    return;
                }
                if (projectFamilies is null || projectFamilies.Count == 0)
                {
                    RestoreOperationProgress();
                    MessageBox.Show(_window, "No editable loadable families were found in this project.", "FamilyMEP");
                    return;
                }

                List<FamilyPickerItem> choices = projectFamilies.Select(item => new FamilyPickerItem
                {
                    Key = item.UniqueId,
                    Name = item.Name,
                    Category = item.Category,
                    Selected = false
                }).ToList();
                var selectionDialog = new FamilySelectionDialog(
                    _window,
                    "Export families from project",
                    $"Source: {projectPath}\nChoose categories, then confirm the individual families to save.",
                    choices);
                if (selectionDialog.ShowDialog() != true)
                {
                    RestoreOperationProgress();
                    SetStatus("Save to Tool Library cancelled.");
                    return;
                }

                HashSet<string> selectedKeys = selectionDialog.SelectedFamilies
                    .Select(item => item.Key)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                List<ProjectFamilyItem> selectedFamilies = projectFamilies
                    .Where(item => selectedKeys.Contains(item.UniqueId))
                    .ToList();
                if (selectedFamilies.Count == 0)
                {
                    RestoreOperationProgress();
                    SetStatus("No families selected for Tool Library.");
                    return;
                }

                BeginProgressiveProjectFamilyExport(projectPath, selectedFamilies);
            });
    }

    private void BeginProgressiveProjectFamilyExport(
        string projectPath,
        IReadOnlyCollection<ProjectFamilyItem> selectedFamilies)
    {
        _projectFamilyExportQueue.Clear();
        foreach (ProjectFamilyItem family in selectedFamilies) _projectFamilyExportQueue.Enqueue(family);
        _projectFamilyExportTotal = selectedFamilies.Count;
        _projectFamilyExportProcessed = 0;
        _projectFamilyExportSession = null;
        Find<Button>("standards_save_tool_btn").IsEnabled = false;
        SetBusy(true);
        ShowOperationProgress("Save to Tool Library", 0, _projectFamilyExportTotal);
        SetStatus($"Save to Tool Library: 0 / {_projectFamilyExportTotal:N0} (0%)");
        ProcessNextProjectFamilyExportChunk(projectPath);
    }

    private void ProcessNextProjectFamilyExportChunk(string projectPath)
    {
        const int chunkSize = 5;
        var chunk = new List<ProjectFamilyItem>(chunkSize);
        while (chunk.Count < chunkSize && _projectFamilyExportQueue.Count > 0)
            chunk.Add(_projectFamilyExportQueue.Dequeue());

        bool complete = _projectFamilyExportQueue.Count == 0;
        ProjectFamilyExportResult? exportResult = null;
        QueueRevit(
            app =>
            {
                _projectFamilyExportSession ??= RevitOperations.BeginProjectFamilyExport(
                    app,
                    projectPath,
                    AppPaths.UserLibraryFolder);
                exportResult = RevitOperations.ExportProjectFamilyBatch(
                    _projectFamilyExportSession,
                    chunk,
                    complete);
            },
            async exportError =>
            {
                if (exportError is not null)
                {
                    ResetProjectFamilyExportProgress();
                    ShowError(exportError);
                    return;
                }

                _projectFamilyExportProcessed += chunk.Count;
                int percent = ProgressPercent(_projectFamilyExportProcessed, _projectFamilyExportTotal);
                ShowOperationProgress(
                    "Save to Tool Library",
                    _projectFamilyExportProcessed,
                    _projectFamilyExportTotal);
                SetStatus(
                    $"Save to Tool Library: {_projectFamilyExportProcessed:N0} / {_projectFamilyExportTotal:N0} ({percent}%)");

                if (!complete)
                {
                    ProcessNextProjectFamilyExportChunk(projectPath);
                    return;
                }

                string outputRoot = AppPaths.UserLibraryFolder;
                if (!_state.Roots.Contains(outputRoot, StringComparer.OrdinalIgnoreCase))
                {
                    _state.Roots.Add(outputRoot);
                    StateService.Save(_state);
                }
                if (exportResult is not null && exportResult.Families.Count > 0)
                {
                    FamilyCategoryCacheService.Update(exportResult.Families);
                    FamilyCategoryCacheService.Save();
                }

                HashSet<string> exportedPaths = exportResult?.Families.Select(item => item.Path)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
                int exported = exportResult?.Exported ?? 0;
                int skipped = exportResult?.Skipped ?? 0;
                int completedTotal = _projectFamilyExportTotal;
                ResetProjectFamilyExportProgress(restoreProgress: false);
                ShowOperationProgress("Save to Tool Library", completedTotal, completedTotal);
                SetStatus(
                    $"Save to Tool Library complete: {exported:N0} saved, {skipped:N0} skipped (100%).");
                await ScanAsync();
                StartAutomaticIndex(_allFamilies.Where(item => exportedPaths.Contains(item.Path)).ToList());
            });
    }

    private void ResetProjectFamilyExportProgress(bool restoreProgress = true)
    {
        _projectFamilyExportSession = null;
        _projectFamilyExportQueue.Clear();
        _projectFamilyExportTotal = 0;
        _projectFamilyExportProcessed = 0;
        Find<Button>("standards_save_tool_btn").IsEnabled = true;
        SetBusy(false);
        if (restoreProgress && !_categorySyncRunning && !_bulkPreviewRunning) RestoreOperationProgress();
    }

    private void WireBatchOperationButton(string buttonName, BatchOperationKind operation) =>
        Find<Button>(buttonName).Click += (_, _) => SelectBatchOperation(operation);

    private void SelectBatchOperation(BatchOperationKind operation)
    {
        if (_batchPreviewRunning || _batchRunRunning) return;
        _batchOperation = operation;
        foreach ((string name, BatchOperationKind kind) in BatchOperationButtons())
        {
            Find<Button>(name).Style = (Style)_window.Resources[kind == operation
                ? "BatchOperationActiveButtonStyle"
                : "BatchOperationButtonStyle"];
        }

        TextBlock title = Find<TextBlock>("batch_operation_title_tb");
        TextBlock matchLabel = Find<TextBlock>("batch_match_rule_label_tb");
        ComboBox match = Find<ComboBox>("batch_match_rule_combo");
        TextBlock sourceLabel = Find<TextBlock>("batch_source_label_tb");
        TextBox source = Find<TextBox>("batch_source_value_tb");
        TextBlock targetLabel = Find<TextBlock>("batch_target_label_tb");
        TextBox target = Find<TextBox>("batch_target_value_tb");
        StackPanel shared = Find<StackPanel>("batch_shared_parameter_panel");
        CheckBox instance = Find<CheckBox>("batch_instance_parameter_cb");

        string titleText;
        string sourceText;
        string targetText;
        string sourceDefault;
        string targetDefault;
        bool showMatch = true;
        bool showSource = true;
        bool showTarget = true;
        bool showShared = false;
        bool showInstance = false;
        switch (operation)
        {
            case BatchOperationKind.AddSharedParameter:
                (titleText, sourceText, targetText, sourceDefault, targetDefault) = ("Add Shared Parameter", "Definition Name", "", "", "");
                showMatch = false; showSource = false; showTarget = false; showShared = true;
                break;
            case BatchOperationKind.ReplaceParameter:
                (titleText, sourceText, targetText, sourceDefault, targetDefault) = ("Replace Parameter", "Family Parameter", "Shared Definition Name", "Width", "Shared_Width");
                showTarget = false; showShared = true;
                break;
            case BatchOperationKind.SetValue:
                (titleText, sourceText, targetText, sourceDefault, targetDefault) = ("Set Parameter Value", "Parameter Name", "New Value", "Width", "1000 mm");
                break;
            case BatchOperationKind.SetFormula:
                (titleText, sourceText, targetText, sourceDefault, targetDefault) = ("Set Formula", "Parameter Name", "Formula", "Width", "Height / 2");
                break;
            case BatchOperationKind.RemoveParameter:
                (titleText, sourceText, targetText, sourceDefault, targetDefault) = ("Remove Parameter", "Parameter Name", "", "Unused_Parameter", "");
                showTarget = false;
                break;
            case BatchOperationKind.RenameTypes:
                (titleText, sourceText, targetText, sourceDefault, targetDefault) = ("Rename Family Types", "Find Type Name", "Replace With", "Type", "Standard");
                break;
            case BatchOperationKind.CreateTypes:
                (titleText, sourceText, targetText, sourceDefault, targetDefault) = ("Create / Duplicate Type", "Source Type (optional)", "New Type Name", "", "New Type");
                showMatch = false;
                break;
            case BatchOperationKind.DeleteTypes:
                (titleText, sourceText, targetText, sourceDefault, targetDefault) = ("Delete Family Types", "Type Name", "", "Unused", "");
                showTarget = false;
                break;
            case BatchOperationKind.RenameFiles:
                (titleText, sourceText, targetText, sourceDefault, targetDefault) = ("Rename RFA Files", "Find in File Name", "Replace With", "Old", "New");
                break;
            case BatchOperationKind.ChangeCategory:
                (titleText, sourceText, targetText, sourceDefault, targetDefault) = ("Change Family Category", "", "Exact Revit Category", "", "Mechanical Equipment");
                showMatch = false; showSource = false;
                break;
            case BatchOperationKind.NamingStandard:
                (titleText, sourceText, targetText, sourceDefault, targetDefault) = ("Naming Standard", "Prefix", "Suffix", "MEP_", "");
                showMatch = false;
                break;
            default:
                (titleText, sourceText, targetText, sourceDefault, targetDefault) = ("Rename Parameter", "Find", "Replace With", "Width", "Duct_Width");
                break;
        }

        title.Text = titleText;
        matchLabel.Visibility = match.Visibility = showMatch ? Visibility.Visible : Visibility.Collapsed;
        sourceLabel.Visibility = source.Visibility = showSource ? Visibility.Visible : Visibility.Collapsed;
        targetLabel.Visibility = target.Visibility = showTarget ? Visibility.Visible : Visibility.Collapsed;
        sourceLabel.Text = sourceText;
        targetLabel.Text = targetText;
        source.Text = sourceDefault;
        target.Text = targetDefault;
        shared.Visibility = showShared ? Visibility.Visible : Visibility.Collapsed;
        instance.Visibility = showInstance ? Visibility.Visible : Visibility.Collapsed;
        ListBox sharedDefinitions = Find<ListBox>("batch_shared_definition_list");
        if (operation == BatchOperationKind.ReplaceParameter)
        {
            SharedParameterDefinitionItem? selected = sharedDefinitions.SelectedItems.Cast<SharedParameterDefinitionItem>().FirstOrDefault();
            sharedDefinitions.UnselectAll();
            sharedDefinitions.SelectionMode = SelectionMode.Single;
            if (selected is not null) sharedDefinitions.SelectedItem = selected;
        }
        else if (operation == BatchOperationKind.AddSharedParameter)
        {
            sharedDefinitions.SelectionMode = SelectionMode.Extended;
        }
        Find<Button>("batch_shared_select_all_btn").Visibility = operation == BatchOperationKind.AddSharedParameter
            ? Visibility.Visible
            : Visibility.Collapsed;
        UpdateSharedParameterDefinitionDetails();
        InvalidateBatchPreview();
    }

    private static IEnumerable<(string Name, BatchOperationKind Kind)> BatchOperationButtons()
    {
        yield return ("batch_op_rename_parameter_btn", BatchOperationKind.RenameParameter);
        yield return ("batch_op_add_shared_btn", BatchOperationKind.AddSharedParameter);
        yield return ("batch_op_replace_parameter_btn", BatchOperationKind.ReplaceParameter);
        yield return ("batch_op_set_value_btn", BatchOperationKind.SetValue);
        yield return ("batch_op_set_formula_btn", BatchOperationKind.SetFormula);
        yield return ("batch_op_remove_parameter_btn", BatchOperationKind.RemoveParameter);
        yield return ("batch_op_rename_types_btn", BatchOperationKind.RenameTypes);
        yield return ("batch_op_create_types_btn", BatchOperationKind.CreateTypes);
        yield return ("batch_op_delete_types_btn", BatchOperationKind.DeleteTypes);
        yield return ("batch_op_rename_files_btn", BatchOperationKind.RenameFiles);
        yield return ("batch_op_change_category_btn", BatchOperationKind.ChangeCategory);
        yield return ("batch_op_naming_standard_btn", BatchOperationKind.NamingStandard);
    }

    private void BrowseSharedParameterFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select a Revit shared parameter file on drive F:",
            InitialDirectory = @"F:\",
            Filter = "Shared parameter text file (*.txt)|*.txt"
        };
        if (dialog.ShowDialog(_window) != true) return;
        if (!string.Equals(Path.GetPathRoot(Path.GetFullPath(dialog.FileName)), @"F:\", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(_window, "The shared parameter file must be stored on drive F:.", "FamilyMEP");
            return;
        }
        Find<TextBox>("batch_shared_file_tb").Text = dialog.FileName;
        try
        {
            _sharedParameterDefinitions = SharedParameterFileService.Read(dialog.FileName);
            List<string> groups = _sharedParameterDefinitions.Select(item => item.GroupName)
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .OrderBy(item => item, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            ComboBox groupCombo = Find<ComboBox>("batch_shared_group_combo");
            groupCombo.ItemsSource = groups;
            groupCombo.SelectedIndex = groups.Count > 0 ? 0 : -1;
            if (groups.Count == 0) MessageBox.Show(_window, "No shared parameter definitions were found in this TXT file.", "FamilyMEP");
        }
        catch (Exception exception)
        {
            _sharedParameterDefinitions = [];
            Find<ComboBox>("batch_shared_group_combo").ItemsSource = null;
            Find<ListBox>("batch_shared_definition_list").ItemsSource = null;
            MessageBox.Show(_window, exception.Message, "FamilyMEP");
        }
        InvalidateBatchPreview();
    }

    private void ApplySharedParameterGroup()
    {
        string group = Find<ComboBox>("batch_shared_group_combo").SelectedItem?.ToString() ?? string.Empty;
        List<SharedParameterDefinitionItem> definitions = _sharedParameterDefinitions
            .Where(item => item.GroupName.Equals(group, StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        ListBox definitionList = Find<ListBox>("batch_shared_definition_list");
        definitionList.ItemsSource = definitions;
        definitionList.SelectedIndex = definitions.Count > 0 ? 0 : -1;
        InvalidateBatchPreview();
    }

    private void UpdateSharedParameterDefinitionDetails()
    {
        List<SharedParameterDefinitionItem> definitions = Find<ListBox>("batch_shared_definition_list")
            .SelectedItems.Cast<SharedParameterDefinitionItem>().ToList();
        Find<TextBlock>("batch_shared_selected_count_tb").Text = $"{definitions.Count:N0} selected";
        Find<TextBlock>("batch_shared_discipline_tb").Text = definitions.Count switch
        {
            0 => "-",
            1 => definitions[0].Discipline,
            _ => $"{definitions.Select(item => item.Discipline).Distinct(StringComparer.OrdinalIgnoreCase).Count():N0} disciplines"
        };
        Find<TextBlock>("batch_shared_datatype_tb").Text = definitions.Count switch
        {
            0 => "-",
            1 => definitions[0].DataType,
            _ => $"{definitions.Select(item => item.DataType).Distinct(StringComparer.OrdinalIgnoreCase).Count():N0} types"
        };
        InvalidateBatchPreview();
    }

    private void SelectAllSharedParameterDefinitions()
    {
        ListBox list = Find<ListBox>("batch_shared_definition_list");
        if (list.SelectionMode == SelectionMode.Single) return;
        list.SelectAll();
    }

    private void ClearSharedParameterDefinitions() => Find<ListBox>("batch_shared_definition_list").UnselectAll();

    private BatchOperationRequest? BuildBatchRequest(bool showMessage)
    {
        string match = (Find<ComboBox>("batch_match_rule_combo").SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Contains";
        string source = Find<TextBox>("batch_source_value_tb").Text.Trim();
        string target = Find<TextBox>("batch_target_value_tb").Text.Trim();
        string conflict = (Find<ComboBox>("batch_conflict_combo").SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Skip duplicate";
        string sharedFile = Find<TextBox>("batch_shared_file_tb").Text.Trim();
        bool sharedOperation = _batchOperation is BatchOperationKind.AddSharedParameter or BatchOperationKind.ReplaceParameter;
        List<SharedParameterDefinitionItem> definitions = Find<ListBox>("batch_shared_definition_list")
            .SelectedItems.Cast<SharedParameterDefinitionItem>().ToList();
        if (_batchOperation == BatchOperationKind.AddSharedParameter && definitions.Count > 0)
            source = string.Join(SharedDefinitionSeparator, definitions.Select(item => item.Name));
        if (_batchOperation == BatchOperationKind.ReplaceParameter && definitions.Count > 0) target = definitions[0].Name;
        string parameterScope = (Find<ComboBox>("batch_parameter_scope_combo").SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Type";
        string parameterGroup = (Find<ComboBox>("batch_parameter_group_combo").SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Dimensions";

        bool sourceRequired = _batchOperation is not BatchOperationKind.ChangeCategory and not BatchOperationKind.CreateTypes;
        bool targetRequired = _batchOperation is not BatchOperationKind.RemoveParameter and not BatchOperationKind.DeleteTypes and not BatchOperationKind.AddSharedParameter;
        string? problem = sourceRequired && string.IsNullOrWhiteSpace(source)
            ? "Enter the source name or search text."
            : targetRequired && string.IsNullOrWhiteSpace(target)
                ? "Enter the target name or value."
                : sharedOperation && !File.Exists(sharedFile)
                    ? "Select a valid shared parameter TXT file on drive F:."
                    : sharedOperation && definitions.Count == 0
                        ? "Select at least one Shared Parameter Definition from the TXT file."
                    : _batchOperation == BatchOperationKind.ReplaceParameter && definitions.Count != 1
                        ? "Replace Parameter requires exactly one target Shared Parameter Definition."
                    : _batchOperation == BatchOperationKind.NamingStandard && source.Length == 0 && target.Length == 0
                        ? "Enter a prefix or suffix for the naming standard."
                        : null;
        if (problem is not null)
        {
            if (showMessage) MessageBox.Show(_window, problem, "FamilyMEP");
            return null;
        }
        return new BatchOperationRequest(_batchOperation, match, source, target, conflict, sharedFile, parameterScope == "Instance", parameterGroup);
    }

    private void InvalidateBatchPreview()
    {
        if (_batchPreviewRunning || _batchRunRunning) return;
        _batchPreviewRequest = null;
        _batchChanges.Clear();
        _batchParameterAggregate.Clear();
        Find<DataGrid>("batch2_parameter_grid").ItemsSource = null;
        UpdateBatchPreviewSummary();
    }

    private void BeginBatchPreview(bool showChanges)
    {
        if (_batchPreviewRunning || _batchRunRunning) return;
        List<FamilyItem> families = BatchScopeFamilies().Where(item => item.Selected).ToList();
        if (families.Count == 0)
        {
            MessageBox.Show(_window, "Select at least one family in the Families tab.", "FamilyMEP");
            return;
        }
        int builtInCount = families.Count(item => item.IsBuiltIn);
        if (builtInCount > 0)
        {
            MessageBox.Show(
                _window,
                $"{builtInCount:N0} selected family/families belong to the read-only Built-in Library.\n\nSave them to the editable Tool Library first, then run Batch Edit.",
                "FamilyMEP",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }
        BatchOperationRequest? request = BuildBatchRequest(showMessage: true);
        if (request is null) return;

        _batchPreviewRequest = request;
        _batchPreviewShowChanges = showChanges;
        _batchChanges.Clear();
        _batchParameterAggregate.Clear();
        _batchPreviewQueue.Clear();
        foreach (FamilyItem family in families) _batchPreviewQueue.Enqueue(family);
        _batchPreviewRunning = true;
        _batchRunCancelled = false;
        StartBatchProgress(showChanges ? "Previewing" : "Validating", families.Count);
        SetBatchCommandState(true);
        ProcessNextBatchPreview();
    }

    private void ProcessNextBatchPreview()
    {
        if (!_batchPreviewRunning) return;
        if (_batchRunCancelled || _batchPreviewQueue.Count == 0)
        {
            FinishBatchPreview();
            return;
        }
        FamilyItem family = _batchPreviewQueue.Dequeue();
        BatchFamilyPreviewResult? result = null;
        QueueRevit(
            app => result = RevitOperations.PreviewBatchOperation(app, family, _batchPreviewRequest!),
            error =>
            {
                if (error is not null)
                {
                    family.Status = "Error";
                    _batchLogs.Add(new BatchLogItem { FamilyName = family.FamilyName, Level = "Error", Status = "Error", Message = error.Message });
                }
                else if (result is not null)
                {
                    family.Status = result.Changes.Any(item => item.Validation == "Error")
                        ? "Error"
                        : result.Changes.Any(item => item.Validation == "Warning")
                            ? "Warning"
                            : result.Changes.Any(item => item.Validation == "Ready")
                                ? "Ready"
                                : "Skip";
                    foreach (ParameterItem parameter in result.Parameters)
                    {
                        string key = $"{parameter.Name}|{parameter.Group}|{parameter.DataType}|{parameter.InstanceType}|{parameter.Shared}";
                        if (_batchParameterAggregate.TryGetValue(key, out ParameterItem? existing)) existing.FoundIn++;
                        else _batchParameterAggregate[key] = parameter;
                    }
                    foreach (BatchChangeItem change in result.Changes) _batchChanges.Add(change);
                }
                _batchWorkProcessed++;
                UpdateBatchProgress();
                UpdateBatchPreviewSummary();
                ProcessNextBatchPreview();
            },
            priority: true);
    }

    private void FinishBatchPreview()
    {
        _batchPreviewRunning = false;
        _batchPreviewQueue.Clear();
        _batchClockTimer.Stop();
        SetBatchCommandState(false);
        List<ParameterItem> parameters = _batchParameterAggregate.Values.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        Find<DataGrid>("batch2_parameter_grid").ItemsSource = parameters;
        Find<TextBlock>("batch_parameters_tab_tb").Text = $"Parameters ({parameters.Count:N0})";
        ApplyBatchChangeFilter();
        Find<TabControl>("batch_center_tabs").SelectedIndex = _batchPreviewShowChanges ? 2 : 1;
        int willApply = _batchChanges.Count(item => item.CanRun && item.Selected);
        int skipped = _batchChanges.Count(item => !item.CanRun);
        Find<TextBlock>("batch_progress_label_tb").Text = _batchRunCancelled
            ? $"Stopped {_batchWorkProcessed:N0} / {_batchWorkTotal:N0}"
            : $"Preview ready — {willApply:N0} will apply • {skipped:N0} skipped • {_batchChanges.Count:N0} checks";
        SetStatus(_batchRunCancelled
            ? $"Batch preview stopped after {_batchWorkProcessed:N0} families."
            : $"Batch preview complete: {willApply:N0} will apply, {skipped:N0} skipped, {_batchChanges.Count:N0} checks across {_batchWorkTotal:N0} families.");
    }

    private void BeginBatchRun()
    {
        if (_batchPreviewRunning || _batchRunRunning) return;
        BatchOperationRequest? current = BuildBatchRequest(showMessage: true);
        if (current is null) return;
        if (_batchPreviewRequest is null || _batchPreviewRequest != current || _batchChanges.Count == 0)
        {
            MessageBox.Show(_window, "Run Preview Changes first. The preview must match the current settings.", "FamilyMEP");
            return;
        }
        HashSet<string> runnablePaths = _batchChanges.Where(item => item.Selected && item.CanRun).Select(item => item.FamilyPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<FamilyItem> families = ProfileFamilies().Where(item => runnablePaths.Contains(item.Path)).ToList();
        if (families.Count == 0)
        {
            MessageBox.Show(_window, "No runnable changes are selected.", "FamilyMEP");
            return;
        }

        _batchLogs.Clear();
        _batchRunQueue.Clear();
        foreach (FamilyItem family in families) _batchRunQueue.Enqueue(family);
        _batchBackupManifest.Clear();
        _batchBackupSession = string.Empty;
        if (Find<CheckBox>("batch_create_backup2_cb").IsChecked == true)
        {
            string operationName = _batchOperation.ToString();
            _batchBackupSession = Path.Combine(AppPaths.BackupFolder, $"{DateTime.Now:yyyyMMdd_HHmmss_fff}_{operationName}");
            Directory.CreateDirectory(_batchBackupSession);
        }
        _batchRunRunning = true;
        _batchRunPaused = false;
        _batchRunCancelled = false;
        StartBatchProgress("Processing", families.Count);
        SetBatchCommandState(true);
        Find<Button>("batch_pause_btn").IsEnabled = true;
        ProcessNextBatchRun();
    }

    private void ProcessNextBatchRun()
    {
        if (!_batchRunRunning || _batchRunPaused) return;
        if (_batchRunCancelled || _batchRunQueue.Count == 0)
        {
            FinishBatchRun();
            return;
        }
        FamilyItem family = _batchRunQueue.Dequeue();
        List<BatchChangeItem> changes = _batchChanges.Where(item => item.FamilyPath.Equals(family.Path, StringComparison.OrdinalIgnoreCase) && item.Selected && item.CanRun).ToList();
        try
        {
            BackupFamilyForBatch(family);
        }
        catch (Exception exception)
        {
            _batchLogs.Add(new BatchLogItem { FamilyName = family.FamilyName, Level = "Error", Status = "Error", Message = $"Backup failed: {exception.Message}" });
            _batchWorkProcessed++;
            UpdateBatchProgress();
            ProcessNextBatchRun();
            return;
        }

        BatchFamilyExecutionResult? result = null;
        QueueRevit(
            app => result = RevitOperations.ExecuteBatchOperation(app, family, _batchPreviewRequest!, changes),
            error =>
            {
                if (error is not null)
                {
                    _batchLogs.Add(new BatchLogItem { FamilyName = family.FamilyName, Level = "Error", Status = "Error", Message = error.Message });
                }
                else if (result is not null)
                {
                    _batchLogs.Add(result.Log);
                }
                _batchWorkProcessed++;
                UpdateBatchProgress();
                UpdateBatchLogCounters();
                ProcessNextBatchRun();
            },
            priority: true);
    }

    private void BackupFamilyForBatch(FamilyItem family)
    {
        if (string.IsNullOrWhiteSpace(_batchBackupSession)) return;
        string backup = Path.Combine(_batchBackupSession, $"{Guid.NewGuid():N}_{Path.GetFileName(family.Path)}");
        File.Copy(family.Path, backup, true);
        _batchBackupManifest[family.Path] = backup;
        File.WriteAllText(Path.Combine(_batchBackupSession, "manifest.json"), JsonSerializer.Serialize(_batchBackupManifest, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(_batchBackupSession, "session.json"), JsonSerializer.Serialize(new
        {
            Operation = _batchOperation.ToString(),
            Created = DateTime.Now,
            FamilyCount = _batchBackupManifest.Count
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private void FinishBatchRun()
    {
        _batchRunRunning = false;
        _batchRunPaused = false;
        _batchRunQueue.Clear();
        _batchClockTimer.Stop();
        SetBatchCommandState(false);
        Find<Button>("batch_pause_btn").IsEnabled = false;
        Find<Button>("batch_pause_btn").Content = "Pause";
        Find<TabControl>("batch_center_tabs").SelectedIndex = 3;
        Find<TextBlock>("batch_progress_label_tb").Text = _batchRunCancelled
            ? $"Stopped {_batchWorkProcessed:N0} / {_batchWorkTotal:N0}"
            : $"Complete {_batchWorkProcessed:N0} / {_batchWorkTotal:N0}";
        SetStatus(_batchRunCancelled
            ? $"Batch stopped: {_batchWorkProcessed:N0} family/families completed and saved."
            : $"Batch complete: {_batchWorkProcessed:N0} family/families processed.");
        bool reload = Find<CheckBox>("batch_reload_project2_cb").IsChecked == true;
        bool rescan = _batchOperation is BatchOperationKind.RenameFiles or BatchOperationKind.NamingStandard or BatchOperationKind.ChangeCategory;
        if (rescan) _ = ScanAsync();
        if (reload && _batchOperation is not BatchOperationKind.RenameFiles and not BatchOperationKind.NamingStandard) LoadSelectedFamilies();
    }

    private void CancelBatchWork()
    {
        if (!_batchPreviewRunning && !_batchRunRunning) return;
        _batchRunCancelled = true;
        _batchPreviewQueue.Clear();
        _batchRunQueue.Clear();
        Find<TextBlock>("batch_progress_label_tb").Text = "Stopping after current family...";
        SetStatus("Stopping Batch Edit after the current Revit API operation...");
        if (_batchRunRunning && _batchRunPaused) FinishBatchRun();
    }

    private void ToggleBatchPause()
    {
        if (!_batchRunRunning) return;
        _batchRunPaused = !_batchRunPaused;
        Find<Button>("batch_pause_btn").Content = _batchRunPaused ? "Resume" : "Pause";
        Find<TextBlock>("batch_progress_label_tb").Text = _batchRunPaused ? $"Paused {_batchWorkProcessed:N0} / {_batchWorkTotal:N0}" : $"Processing {_batchWorkProcessed:N0} / {_batchWorkTotal:N0}";
        if (!_batchRunPaused) ProcessNextBatchRun();
    }

    private void StartBatchProgress(string label, int total)
    {
        _batchWorkTotal = total;
        _batchWorkProcessed = 0;
        _batchStopwatch = Stopwatch.StartNew();
        ProgressBar progress = Find<ProgressBar>("batch_progress_bar");
        progress.Minimum = 0;
        progress.Maximum = Math.Max(1, total);
        progress.Value = 0;
        Find<TextBlock>("batch_progress_label_tb").Text = $"{label} 0 / {total:N0}";
        Find<TextBlock>("batch_elapsed_tb").Text = "Elapsed 00:00";
        Find<TextBlock>("batch_remaining_tb").Text = "Remaining --";
        _batchClockTimer.Start();
    }

    private void UpdateBatchProgress()
    {
        Find<ProgressBar>("batch_progress_bar").Value = _batchWorkProcessed;
        string prefix = _batchPreviewRunning ? "Scanning" : "Processing";
        Find<TextBlock>("batch_progress_label_tb").Text = $"{prefix} {_batchWorkProcessed:N0} / {_batchWorkTotal:N0}";
        UpdateBatchClock();
    }

    private void UpdateBatchClock()
    {
        if (_batchStopwatch is null) return;
        TimeSpan elapsed = _batchStopwatch.Elapsed;
        Find<TextBlock>("batch_elapsed_tb").Text = $"Elapsed {elapsed:mm\\:ss}";
        if (_batchWorkProcessed <= 0)
        {
            Find<TextBlock>("batch_remaining_tb").Text = "Remaining --";
            return;
        }
        double secondsPerItem = elapsed.TotalSeconds / _batchWorkProcessed;
        TimeSpan remaining = TimeSpan.FromSeconds(secondsPerItem * Math.Max(0, _batchWorkTotal - _batchWorkProcessed));
        Find<TextBlock>("batch_remaining_tb").Text = remaining.TotalMinutes >= 1
            ? $"Remaining ~{(int)Math.Ceiling(remaining.TotalMinutes)} min"
            : $"Remaining ~{Math.Max(0, remaining.Seconds)} sec";
    }

    private void SetBatchCommandState(bool running)
    {
        Find<Button>("batch_scan_validate_btn").IsEnabled = !running;
        Find<Button>("batch_preview_changes_btn").IsEnabled = !running;
        Find<Button>("batch_preview_side2_btn").IsEnabled = !running;
        Find<Button>("batch_run_changes_btn").IsEnabled = !running;
        Find<Button>("batch_run_side2_btn").IsEnabled = !running;
        Find<Button>("batch_cancel_run_btn").IsEnabled = running;
        Find<Button>("batch_select_all2_btn").IsEnabled = !running;
        Find<Button>("batch_backup_sessions_btn").IsEnabled = !running;
        Find<Button>("batch_undo_last_btn").IsEnabled = !running;
        Find<Button>("batch_shared_browse_btn").IsEnabled = !running;
        Find<TextBox>("batch_source_value_tb").IsEnabled = !running;
        Find<TextBox>("batch_target_value_tb").IsEnabled = !running;
        Find<ComboBox>("batch_match_rule_combo").IsEnabled = !running;
        Find<ComboBox>("batch_conflict_combo").IsEnabled = !running;
        Find<ComboBox>("batch_shared_group_combo").IsEnabled = !running;
        Find<ListBox>("batch_shared_definition_list").IsEnabled = !running;
        Find<Button>("batch_shared_select_all_btn").IsEnabled = !running;
        Find<Button>("batch_shared_clear_btn").IsEnabled = !running;
        Find<ComboBox>("batch_parameter_scope_combo").IsEnabled = !running;
        Find<ComboBox>("batch_parameter_group_combo").IsEnabled = !running;
        foreach ((string name, _) in BatchOperationButtons()) Find<Button>(name).IsEnabled = !running;
    }

    private void UpdateBatchPreviewSummary()
    {
        Find<TextBlock>("batch_changes_tab_tb").Text = $"Changes ({_batchChanges.Count:N0})";
        Find<TextBlock>("batch_summary_changes_tb").Text = _batchChanges.Count(item => item.CanRun && item.Selected).ToString("N0");
        Find<TextBlock>("batch_summary_warnings2_tb").Text = _batchChanges.Count(item => item.Validation == "Warning").ToString("N0");
        Find<TextBlock>("batch_summary_skipped_tb").Text = _batchChanges.Count(item => item.Validation is "Skip" or "Error").ToString("N0");
        bool sharedOperation = _batchOperation is BatchOperationKind.AddSharedParameter or BatchOperationKind.ReplaceParameter;
        int selectedDefinitions = sharedOperation
            ? Find<ListBox>("batch_shared_definition_list").SelectedItems.Count
            : _batchChanges.Where(item => item.Selected).Select(item => item.ItemName).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        Find<TextBlock>("batch_summary_definitions_label_tb").Text = sharedOperation ? "Selected Definitions" : "Matched Items";
        Find<TextBlock>("batch_summary_definitions_tb").Text = selectedDefinitions.ToString("N0");
        Find<TextBlock>("batch_summary_ready_label_tb").Text = _batchOperation switch
        {
            BatchOperationKind.AddSharedParameter => "Will Add",
            BatchOperationKind.ReplaceParameter => "Will Replace",
            _ => "Will Apply"
        };
        Find<TextBlock>("batch_summary_skipped_label_tb").Text = _batchOperation == BatchOperationKind.AddSharedParameter
            ? "Already Exists / Skipped"
            : "Skipped / Errors";
    }

    private void UpdateBatchLogCounters()
    {
        Find<TextBlock>("batch_success_tb").Text = $"{_batchLogs.Count(item => item.Level == "Success")} Success";
        Find<TextBlock>("batch_warning_tb").Text = $"{_batchLogs.Count(item => item.Level == "Warning")} Warnings";
        Find<TextBlock>("batch_error_tb").Text = $"{_batchLogs.Count(item => item.Level == "Error")} Errors";
    }

    private void ApplyBatchFamilyFilter()
    {
        string search = Find<TextBox>("batch_family_search_tb").Text.Trim();
        string status = (Find<ComboBox>("batch_family_status_combo").SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "All Status";
        IEnumerable<FamilyItem> query = BatchScopeFamilies();
        if (search.Length > 0) query = query.Where(item => item.FamilyName.Contains(search, StringComparison.OrdinalIgnoreCase) || item.Category.Contains(search, StringComparison.OrdinalIgnoreCase));
        if (status != "All Status") query = query.Where(item => item.Status.Equals(status, StringComparison.OrdinalIgnoreCase));
        List<FamilyItem> visible = query.ToList();
        DataGrid grid = Find<DataGrid>("batch2_family_grid");
        _syncingBatchGridSelection = true;
        try
        {
            grid.ItemsSource = visible;
            grid.UnselectAll();
            List<FamilyItem> selected = visible.Where(item => item.Selected).ToList();
            if (selected.Count == visible.Count && visible.Count > 0) grid.SelectAll();
            else foreach (FamilyItem item in selected) grid.SelectedItems.Add(item);
        }
        finally
        {
            _syncingBatchGridSelection = false;
        }
        Find<TextBlock>("batch_families_tab_tb").Text = $"Families ({visible.Count:N0})";
    }

    private void SyncBatchGridSelection(object sender, SelectionChangedEventArgs eventArgs)
    {
        if (_syncingBatchGridSelection || sender is not DataGrid grid) return;
        _syncingBatchGridSelection = true;
        try
        {
            HashSet<FamilyItem> selectedRows = grid.SelectedItems.Cast<FamilyItem>().ToHashSet();
            foreach (FamilyItem item in eventArgs.AddedItems.OfType<FamilyItem>()) item.Selected = true;
            foreach (FamilyItem item in eventArgs.RemovedItems.OfType<FamilyItem>())
            {
                if (!selectedRows.Contains(item)) item.Selected = false;
            }
        }
        finally
        {
            _syncingBatchGridSelection = false;
        }
        UpdateBatchSummary();
    }

    private IEnumerable<FamilyItem> BatchScopeFamilies()
    {
        IEnumerable<FamilyItem> query = ProfileFamilies();
        return _categoryFilter switch
        {
            "favorites" => query.Where(item => item.Favorite),
            var key when key.StartsWith("group:") => query.Where(item => item.Group.Equals(key.Substring(6), StringComparison.OrdinalIgnoreCase)),
            var key when key.StartsWith("category:") => query.Where(item => item.Category.Equals(key.Substring(9), StringComparison.OrdinalIgnoreCase)),
            _ => query
        };
    }

    private string CategoryFilterLabel()
    {
        if (_categoryFilter == "*") return "All";
        if (_categoryFilter.StartsWith("category:", StringComparison.Ordinal)) return _categoryFilter.Substring(9);
        if (_categoryFilter.StartsWith("group:", StringComparison.Ordinal)) return _categoryFilter.Substring(6);
        return "Favorites";
    }

    private void ApplyBatchParameterFilter()
    {
        string search = Find<TextBox>("batch_parameter_search2_tb").Text.Trim();
        List<ParameterItem> items = _batchParameterAggregate.Values
            .Where(item => search.Length == 0 || item.Name.Contains(search, StringComparison.OrdinalIgnoreCase) || item.Group.Contains(search, StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        Find<DataGrid>("batch2_parameter_grid").ItemsSource = items;
        Find<TextBlock>("batch_parameters_tab_tb").Text = $"Parameters ({items.Count:N0})";
    }

    private void ApplyBatchChangeFilter()
    {
        string search = Find<TextBox>("batch_change_search_tb").Text.Trim();
        string validation = (Find<ComboBox>("batch_validation_filter_combo").SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "All Validation";
        string action = (Find<ComboBox>("batch_action_filter_combo").SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "All Actions";
        IEnumerable<BatchChangeItem> query = _batchChanges;
        if (search.Length > 0) query = query.Where(item => item.FamilyName.Contains(search, StringComparison.OrdinalIgnoreCase) || item.ItemName.Contains(search, StringComparison.OrdinalIgnoreCase) || item.CurrentValue.Contains(search, StringComparison.OrdinalIgnoreCase) || item.NewValue.Contains(search, StringComparison.OrdinalIgnoreCase));
        if (validation != "All Validation") query = query.Where(item => item.Validation.Equals(validation, StringComparison.OrdinalIgnoreCase));
        if (action != "All Actions") query = query.Where(item => item.Action.Equals(action, StringComparison.OrdinalIgnoreCase));
        Find<DataGrid>("batch_changes_grid").ItemsSource = query.ToList();
        UpdateBatchPreviewSummary();
    }

    private void ShowBackupSessions()
    {
        Button anchor = Find<Button>("batch_backup_sessions_btn");
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom, MinWidth = 340 };
        List<DirectoryInfo> sessions = Directory.Exists(AppPaths.BackupFolder)
            ? new DirectoryInfo(AppPaths.BackupFolder).EnumerateDirectories().OrderByDescending(item => item.Name).Take(20).ToList()
            : [];
        if (sessions.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = "No backup sessions on drive F:", IsEnabled = false });
        }
        foreach (DirectoryInfo session in sessions)
        {
            string manifestPath = Path.Combine(session.FullName, "manifest.json");
            Dictionary<string, string> manifest = File.Exists(manifestPath)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(manifestPath)) ?? []
                : [];
            long bytes = manifest.Values.Where(File.Exists).Sum(path => new FileInfo(path).Length);
            var item = new MenuItem { Header = $"{session.Name}  |  {manifest.Count:N0} families  |  {FormatBytes(bytes)}" };
            string folder = session.FullName;
            item.Click += (_, _) => RestoreBackupSession(folder);
            menu.Items.Add(item);
        }
        anchor.ContextMenu = menu;
        menu.IsOpen = true;
    }

    private void RestoreBackupSession(string folder)
    {
        string manifestPath = Path.Combine(folder, "manifest.json");
        Dictionary<string, string> manifest = File.Exists(manifestPath)
            ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(manifestPath)) ?? []
            : [];
        if (manifest.Count == 0) return;
        MessageBoxResult answer = MessageBox.Show(_window, $"Restore {manifest.Count:N0} family files from {Path.GetFileName(folder)}?\n\nCurrent files will be overwritten.", "FamilyMEP", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;
        int restored = 0;
        int failed = 0;
        foreach (KeyValuePair<string, string> entry in manifest)
        {
            string target = entry.Key;
            string backup = entry.Value;
            if (Path.GetPathRoot(target)?.Equals(@"F:\", StringComparison.OrdinalIgnoreCase) == true && File.Exists(backup))
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(backup, target, true);
                    restored++;
                }
                catch
                {
                    failed++;
                }
            }
            else failed++;
        }
        InvalidateBatchPreview();
        SetStatus($"Undo complete: restored {restored:N0} family files; {failed:N0} failed from {Path.GetFileName(folder)}.");
        if (failed > 0)
            MessageBox.Show(_window, $"Restored {restored:N0} family files.\n\n{failed:N0} file(s) could not be restored. Close any open family documents and try the backup session again.", "FamilyMEP", MessageBoxButton.OK, MessageBoxImage.Warning);
        _ = ScanAsync();
    }

    private void ToggleBatchSelection()
    {
        List<FamilyItem> profileFamilies = BatchScopeFamilies().ToList();
        bool select = profileFamilies.Any(item => !item.Selected);
        foreach (FamilyItem item in profileFamilies) item.Selected = select;
        Find<DataGrid>("batch_family_grid").Items.Refresh();
        DataGrid grid = Find<DataGrid>("batch2_family_grid");
        _syncingBatchGridSelection = true;
        try
        {
            grid.Items.Refresh();
            if (select) grid.SelectAll();
            else grid.UnselectAll();
        }
        finally
        {
            _syncingBatchGridSelection = false;
        }
        Find<TextBlock>("footer_selected_tb").Text = $"Selected: {profileFamilies.Count(item => item.Selected):N0}";
        UpdateBatchSummary();
    }

    private List<FamilyItem> SelectedBatchFamilies()
    {
        List<FamilyItem> result = ProfileFamilies().Where(item => item.Selected).ToList();
        if (result.Count == 0 && SelectedFamily is { } selected) result.Add(selected);
        return result;
    }

    private async void InspectBatchParameters()
    {
        List<FamilyItem> families = SelectedBatchFamilies();
        if (families.Count == 0) { ShowSelectionRequired(); return; }
        List<string> familyPaths;
        try
        {
            SetStatus($"Preparing {families.Count:N0} family file(s)...");
            familyPaths = await Task.Run(() => families.Select(ToolLibraryService.EnsureMaterialized).ToList());
        }
        catch (Exception exception)
        {
            ShowError(exception);
            return;
        }
        List<ParameterItem>? parameters = null;
        QueueRevit(
            app => parameters = RevitOperations.InspectParameters(app, familyPaths),
            error =>
            {
                if (error is not null) { ShowError(error); return; }
                Find<DataGrid>("batch_parameter_grid").ItemsSource = parameters;
                _parameterItems = parameters ?? [];
                Find<TextBlock>("parameter_scope_tb").Text = $"{families.Count:N0} selected family/families";
                ApplyParameterFilter();
                SetStatus($"Validated {families.Count} family/families; found {parameters!.Count} parameters.");
            });
    }

    private void ApplyParameterFilter()
    {
        string search = Find<TextBox>("parameter_search_tb").Text.Trim();
        bool sharedOnly = Find<CheckBox>("parameter_shared_only_cb").IsChecked == true;
        List<ParameterItem> filtered = _parameterItems
            .Where(item => !sharedOnly || item.Shared)
            .Where(item => search.Length == 0
                || item.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase)
                || item.Group.Contains(search, StringComparison.CurrentCultureIgnoreCase)
                || item.DataType.Contains(search, StringComparison.CurrentCultureIgnoreCase))
            .ToList();

        DataGrid grid = Find<DataGrid>("parameter_grid");
        grid.ItemsSource = filtered;
        Find<TextBlock>("parameter_count_tb").Text = $"Parameters ({filtered.Count:N0})";
        if (filtered.Count > 0) grid.SelectedIndex = 0;
        else UpdateParameterDetails();
    }

    private void UpdateParameterDetails()
    {
        ParameterItem? item = Find<DataGrid>("parameter_grid").SelectedItem as ParameterItem;
        Find<TextBlock>("parameter_detail_name_tb").Text = item?.Name ?? "Select a parameter";
        Find<TextBlock>("parameter_detail_group_tb").Text = item?.Group ?? "-";
        Find<TextBlock>("parameter_detail_type_tb").Text = item?.DataType ?? "-";
        Find<TextBlock>("parameter_detail_binding_tb").Text = item?.InstanceType ?? "-";
        Find<TextBlock>("parameter_detail_shared_tb").Text = item is null ? "-" : item.Shared ? "Yes" : "No";
        Find<TextBlock>("parameter_detail_found_tb").Text = item is null ? "-" : $"{item.FoundIn:N0} family/families";
        Find<TextBlock>("parameter_detail_formula_tb").Text = string.IsNullOrWhiteSpace(item?.Formula) ? "-" : item.Formula;
    }

    private void ExportParameterAudit()
    {
        if (_parameterItems.Count == 0)
        {
            MessageBox.Show(_window, "Read parameters from selected families first.", "FamilyMEP");
            return;
        }

        AppPaths.EnsureCreated();
        var dialog = new SaveFileDialog
        {
            Title = "Export parameter audit",
            InitialDirectory = AppPaths.LogFolder,
            Filter = "CSV file (*.csv)|*.csv",
            FileName = $"parameter_audit_{DateTime.Now:yyyyMMdd_HHmmss}.csv"
        };
        if (dialog.ShowDialog(_window) != true) return;
        if (!string.Equals(Path.GetPathRoot(Path.GetFullPath(dialog.FileName)), @"F:\", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(_window, "Save the audit file on drive F:.", "FamilyMEP");
            return;
        }

        IEnumerable<string> rows = new[] { "Parameter,Group,Data Type,Scope,Shared,Found In,Formula" }.Concat(
            _parameterItems.Select(item => string.Join(",",
                Csv(item.Name), Csv(item.Group), Csv(item.DataType), Csv(item.InstanceType),
                item.Shared ? "Yes" : "No", item.FoundIn.ToString(), Csv(item.Formula))));
        File.WriteAllLines(dialog.FileName, rows);
        SetStatus($"Parameter audit exported to {dialog.FileName}");
    }

    private void RunBatchEdit()
    {
        List<FamilyItem> families = SelectedBatchFamilies();
        if (families.Count == 0) { ShowSelectionRequired(); return; }
        string findText = Find<TextBox>("batch_old_parameter_tb").Text.Trim();
        string replaceText = Find<TextBox>("batch_new_parameter_tb").Text.Trim();
        if (findText.Length == 0)
        {
            MessageBox.Show(_window, "Enter text to find in parameter names.", "FamilyMEP");
            return;
        }

        bool backup = Find<CheckBox>("batch_create_backup_cb").IsChecked == true;
        bool save = Find<CheckBox>("batch_save_after_edit_cb").IsChecked == true;
        bool reload = Find<CheckBox>("batch_reload_project_cb").IsChecked == true;
        List<BatchLogItem>? logs = null;
        QueueRevit(
            app => logs = RevitOperations.RenameParameters(app, families, findText, replaceText, backup, save),
            error =>
            {
                if (error is not null) { ShowError(error); return; }
                foreach (BatchLogItem log in logs!) _batchLogs.Add(log);
                UpdateBatchCounters(logs!);
                SetStatus($"Batch edit finished for {families.Count} family/families.");
                if (reload) LoadSelectedFamilies();
            });
    }

    private void UpdateBatchSummary()
    {
        int count = ProfileFamilies().Count(item => item.Selected);
        Find<TextBlock>("batch_selection_title_tb").Text = $"3. Select Families ({count} selected)";
        Find<TextBlock>("batch_queued_tb").Text = $"{count} Queued";
        Find<TextBlock>("batch_summary_scope_tb").Text = $"Scope: {count} families";
        Find<TextBlock>("batch_scope_selected_tb").Text = count.ToString("N0");
        Find<TextBlock>("batch_summary_selected2_tb").Text = count.ToString("N0");
        Find<TextBlock>("batch_families_tab_tb").Text = $"Families ({ProfileFamilies().Count():N0})";
        Find<TextBlock>("footer_selected_tb").Text = $"Selected: {count:N0}";
    }

    private void UpdateBatchCounters(IEnumerable<BatchLogItem> logs)
    {
        List<BatchLogItem> list = logs.ToList();
        Find<TextBlock>("batch_success_tb").Text = $"{list.Count(item => item.Level == "Success")} Success";
        Find<TextBlock>("batch_warning_tb").Text = $"{list.Count(item => item.Level == "Warning")} Warnings";
        Find<TextBlock>("batch_error_tb").Text = $"{list.Count(item => item.Level == "Error")} Errors";
    }

    private void ShowPackageMenu()
    {
        Button anchor = Find<Button>("package_btn");
        var menu = new ContextMenu
        {
            PlacementTarget = anchor,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
            MinWidth = 230
        };
        var export = new MenuItem { Header = "Export family package..." };
        export.Click += async (_, _) => await ExportPackageAsync();
        var import = new MenuItem { Header = "Import family package..." };
        import.Click += async (_, _) => await ImportPackageAsync();
        var saveToTool = new MenuItem { Header = "Save families to Tool Library..." };
        saveToTool.Click += async (_, _) => await SaveFamiliesToToolLibraryAsync();
        var freeze = new MenuItem { Header = "Freeze as Built-in Library..." };
        freeze.Click += async (_, _) => await FreezeBuiltInLibraryAsync();
        var openToolLibrary = new MenuItem { Header = "Open Tool Library folder" };
        openToolLibrary.Click += (_, _) => Process.Start(new ProcessStartInfo("explorer.exe", AppPaths.ToolLibraryFolder) { UseShellExecute = true });
        menu.Items.Add(new MenuItem { Header = "TOOL LIBRARY", IsEnabled = false });
        menu.Items.Add(saveToTool);
        menu.Items.Add(freeze);
        menu.Items.Add(openToolLibrary);
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "PORTABLE PACKAGES", IsEnabled = false });
        menu.Items.Add(export);
        menu.Items.Add(import);
        anchor.ContextMenu = menu;
        menu.IsOpen = true;
    }

    private async Task SaveFamiliesToToolLibraryAsync()
    {
        List<FamilyItem> source = ProfileFamilies().ToList();
        if (source.Count == 0)
        {
            MessageBox.Show(_window, "Add or import a family library first.", "FamilyMEP");
            return;
        }
        FamilyItem? current = SelectedFamily;
        bool hasSelection = source.Any(item => item.Selected);
        List<FamilyPickerItem> choices = source.Select(item => new FamilyPickerItem
        {
            Key = item.Path,
            Path = item.Path,
            Name = item.FamilyName,
            Category = item.Category,
            Selected = hasSelection ? item.Selected : current?.Path.Equals(item.Path, StringComparison.OrdinalIgnoreCase) == true
        }).ToList();
        var dialog = new FamilySelectionDialog(
            _window,
            "Save to editable Tool Library",
            $"Choose categories or families to copy into the managed library.\nDestination: {AppPaths.UserLibraryFolder}",
            choices,
            acceptText: "Save to Tool Library");
        if (dialog.ShowDialog() != true) return;
        HashSet<string> selected = dialog.SelectedFamilies.Select(item => item.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<FamilyItem> families = source.Where(item => selected.Contains(item.Path)).ToList();
        if (families.Count == 0) return;

        SetBusy(true);
        SetStatus($"Saving {families.Count:N0} family/families to Tool Library...");
        ShowOperationProgress("Save to Tool Library", 0, families.Count);
        try
        {
            var progress = new Progress<(int Current, int Total)>(value =>
                ShowOperationProgress("Save to Tool Library", value.Current, value.Total));
            ToolLibrarySaveResult result = await Task.Run(() => ToolLibraryService.SaveToUserLibrary(families, _state.PreviewColor, progress));
            RemapToolLibraryPaths(result.PathMap);
            await ScanAsync();
            HashSet<string> saved = result.SavedPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
            SetStatus($"Tool Library saved {result.Saved:N0} family/families; skipped {result.Skipped:N0} duplicate name(s).");
            StartAutomaticIndex(_allFamilies.Where(item => saved.Contains(item.Path)).ToList());
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
        finally
        {
            SetBusy(false);
            if (!_categorySyncRunning && !_bulkPreviewRunning) RestoreOperationProgress();
        }
    }

    private void RemapToolLibraryPaths(IReadOnlyDictionary<string, string> pathMap)
    {
        if (pathMap.Count == 0) return;
        foreach (FamilyLibraryProfile profile in _profiles)
        {
            profile.FamilyPaths = profile.FamilyPaths
                .Select(path => pathMap.TryGetValue(path, out string? mapped) ? mapped : path)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        _state.Favorites = _state.Favorites
            .Select(path => pathMap.TryGetValue(path, out string? mapped) ? mapped : path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        ProfileService.Save(_profiles);
        StateService.Save(_state);
    }

    private async Task FreezeBuiltInLibraryAsync()
    {
        List<FamilyItem> source = ProfileFamilies().ToList();
        if (source.Count == 0)
        {
            MessageBox.Show(_window, "No families are available to freeze.", "FamilyMEP");
            return;
        }
        bool profileActive = ActiveProfile is not null;
        bool hasSelection = source.Any(item => item.Selected);
        List<FamilyPickerItem> choices = source.Select(item => new FamilyPickerItem
        {
            Key = item.Path,
            Path = item.Path,
            Name = item.FamilyName,
            Category = item.Category,
            Selected = profileActive || (hasSelection && item.Selected)
        }).ToList();
        var selection = new FamilySelectionDialog(
            _window,
            "Freeze Built-in Tool Library",
            "Choose the families to freeze. Category metadata and cached previews are included.",
            choices,
            acceptText: "Freeze Library");
        if (selection.ShowDialog() != true) return;
        HashSet<string> selected = selection.SelectedFamilies.Select(item => item.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<FamilyItem> families = source.Where(item => selected.Contains(item.Path)).ToList();
        if (families.Count == 0) return;
        int missingCategories = families.Count(item => !item.HasExactCategory);
        int missingPreviews = families.Count(item => string.IsNullOrWhiteSpace(item.PreviewImage) || !File.Exists(item.PreviewImage));
        if (missingCategories > 0 || missingPreviews > 0)
        {
            MessageBoxResult continueFreeze = MessageBox.Show(
                _window,
                $"The selected library is not fully indexed:\n\n"
                + $"Missing exact categories: {missingCategories:N0}\n"
                + $"Missing previews: {missingPreviews:N0}\n\n"
                + "Continue freezing? Missing data will be generated on demand on the receiving computer.",
                "Freeze Built-in Library",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (continueFreeze != MessageBoxResult.Yes) return;
        }

        string defaultName = string.IsNullOrWhiteSpace(_state.ActiveProfileName) ? "FamilyMEP_Default" : _state.ActiveProfileName;
        var saveDialog = new SaveFileDialog
        {
            Title = "Save Built-in FamilyMEP library",
            InitialDirectory = AppPaths.BuiltInLibraryFolder,
            Filter = "FamilyMEP Built-in Library (*.familymeplib)|*.familymeplib",
            FileName = $"{defaultName}_{DateTime.Now:yyyyMMdd_HHmm}.familymeplib"
        };
        if (saveDialog.ShowDialog(_window) != true) return;
        string destinationFolder = Path.GetDirectoryName(Path.GetFullPath(saveDialog.FileName)) ?? string.Empty;
        if (!destinationFolder.Equals(Path.GetFullPath(AppPaths.BuiltInLibraryFolder), StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(_window, $"Save the Built-in Library in the managed folder:\n{AppPaths.BuiltInLibraryFolder}", "FamilyMEP");
            return;
        }

        SetBusy(true);
        ShowOperationProgress("Freeze Built-in Library", 0, families.Count);
        SetStatus($"Freezing {families.Count:N0} family/families into one .familymeplib file...");
        var progress = new Progress<(int Current, int Total)>(value =>
            ShowOperationProgress("Freeze Built-in Library", value.Current, value.Total));
        try
        {
            await Task.Run(() =>
            {
                foreach (FamilyItem family in families) ToolLibraryService.EnsureMaterialized(family);
                FamilyPackageService.Export(
                    saveDialog.FileName,
                    Path.GetFileNameWithoutExtension(saveDialog.FileName),
                    families,
                    _state.PreviewColor,
                    progress,
#if NET48
                    System.IO.Compression.CompressionLevel.Optimal);
#else
                    System.IO.Compression.CompressionLevel.SmallestSize);
#endif
            });
            await ScanAsync();
            long bytes = new FileInfo(saveDialog.FileName).Length;
            SetStatus($"Built-in Library created: {families.Count:N0} families, {FormatBytes(bytes)}. It will mount automatically on another installation.");
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
        finally
        {
            SetBusy(false);
            RestoreOperationProgress();
        }
    }

    private async Task ExportPackageAsync()
    {
        List<FamilyItem> profileFamilies = ProfileFamilies().ToList();
        if (profileFamilies.Count == 0)
        {
            MessageBox.Show(_window, "Add or import a family library first.", "FamilyMEP");
            return;
        }

        FamilyItem? current = SelectedFamily;
        bool profileActive = ActiveProfile is not null;
        bool hasBatchSelection = profileFamilies.Any(item => item.Selected);
        List<FamilyPickerItem> choices = profileFamilies.Select(item => new FamilyPickerItem
        {
            Key = item.Path,
            Path = item.Path,
            Name = item.FamilyName,
            Category = item.Category,
            Selected = profileActive || (hasBatchSelection ? item.Selected : current?.Path.Equals(item.Path, StringComparison.OrdinalIgnoreCase) == true)
        }).ToList();
        var selection = new FamilySelectionDialog(
            _window,
            "Export FamilyMEP package",
            "Choose categories or individual families to include. Cached previews are included automatically.",
            choices);
        if (selection.ShowDialog() != true) return;
        HashSet<string> selectedPaths = selection.SelectedFamilies.Select(item => item.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<FamilyItem> selectedFamilies = profileFamilies.Where(item => selectedPaths.Contains(item.Path)).ToList();

        AppPaths.EnsureCreated();
        var dialog = new SaveFileDialog
        {
            Title = "Save portable FamilyMEP package on drive F:",
            InitialDirectory = AppPaths.PackageFolder,
            Filter = "FamilyMEP Package (*.familymeppack.zip)|*.familymeppack.zip",
            FileName = $"FamilyMEP_Package_{DateTime.Now:yyyyMMdd_HHmm}.familymeppack.zip"
        };
        if (dialog.ShowDialog(_window) != true) return;
        if (!string.Equals(Path.GetPathRoot(Path.GetFullPath(dialog.FileName)), @"F:\", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(_window, "Save the package on drive F:.", "FamilyMEP");
            return;
        }

        SetBusy(true);
        SetStatus($"Packaging {selectedFamilies.Count:N0} families...");
        ShowOperationProgress("Export package", 0, selectedFamilies.Count);
        var exportProgress = new Progress<(int Current, int Total)>(value =>
            ShowOperationProgress("Export package", value.Current, value.Total));
        var stopwatch = Stopwatch.StartNew();
        try
        {
            string packageName = RemoveSuffixInsensitive(
                RemoveSuffixInsensitive(Path.GetFileName(dialog.FileName), ".familymeppack.zip"),
                ".lnfamilypack.zip");
            await Task.Run(() =>
            {
                foreach (FamilyItem family in selectedFamilies) ToolLibraryService.EnsureMaterialized(family);
                FamilyPackageService.Export(
                    dialog.FileName,
                    packageName,
                    selectedFamilies,
                    _state.PreviewColor,
                    exportProgress);
            });
            stopwatch.Stop();
            long bytes = new FileInfo(dialog.FileName).Length;
            SetStatus($"Package saved: {selectedFamilies.Count:N0} families, {FormatBytes(bytes)}, {FormatDuration(stopwatch.Elapsed)}.");
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
        finally
        {
            SetBusy(false);
            RestoreOperationProgress();
        }
    }

    private async Task ImportPackageAsync()
    {
        AppPaths.EnsureCreated();
        var packageDialog = new OpenFileDialog
        {
            Title = "Open FamilyMEP package",
            InitialDirectory = AppPaths.PackageFolder,
            Filter = "FamilyMEP Package (*.familymeppack.zip)|*.familymeppack.zip|ZIP archive (*.zip)|*.zip"
        };
        if (packageDialog.ShowDialog(_window) != true) return;
        if (!PortableFolderDialog.TrySelect(_window, "Choose the parent library folder on drive F:", @"F:\", out string destinationFolder)) return;
        if (!string.Equals(Path.GetPathRoot(Path.GetFullPath(destinationFolder)), @"F:\", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(_window, "The imported library must be stored on drive F:.", "FamilyMEP");
            return;
        }

        SetBusy(true);
        SetStatus("Importing family package...");
        ShowOperationProgress("Import package", 0, 0);
        var importProgress = new Progress<(int Current, int Total)>(value =>
            ShowOperationProgress("Import package", value.Current, value.Total));
        var stopwatch = Stopwatch.StartNew();
        try
        {
            string[] existingNames = _allFamilies.Select(item => item.FamilyName).ToArray();
            FamilyPackageImportResult result = await Task.Run(() => FamilyPackageService.Import(
                packageDialog.FileName,
                destinationFolder,
                existingNames,
                importProgress));
            stopwatch.Stop();
            if (!string.IsNullOrWhiteSpace(result.LibraryRoot)
                && !_state.Roots.Contains(result.LibraryRoot, StringComparer.OrdinalIgnoreCase))
            {
                _state.Roots.Add(result.LibraryRoot);
            }
            _state.PreviewColor = result.PreviewColor;
            StateService.Save(_state);
            InitializeSettings();
            await ScanAsync();
            SetStatus($"Imported {result.FamilyCount:N0} families and {result.PreviewCount:N0} previews; skipped {result.SkippedCount:N0} duplicate name(s) in {FormatDuration(stopwatch.Elapsed)}.");
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
        finally
        {
            SetBusy(false);
            RestoreOperationProgress();
        }
    }

    private static string FormatBytes(long bytes) => bytes >= 1024L * 1024L * 1024L
        ? $"{bytes / 1024d / 1024d / 1024d:0.00} GB"
        : bytes >= 1024L * 1024L
            ? $"{bytes / 1024d / 1024d:0.0} MB"
            : $"{Math.Max(1, bytes / 1024d):0} KB";

    private static string FormatDuration(TimeSpan elapsed) => elapsed.TotalMinutes >= 1
        ? $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds}s"
        : $"{elapsed.TotalSeconds:0.0}s";

    private void SaveSet()
    {
        List<string> paths = SelectedBatchFamilies().Select(item => item.Path).ToList();
        if (paths.Count == 0) paths = _allFamilies.Select(item => item.Path).ToList();
        var dialog = new SaveFileDialog
        {
            Title = "Save family set",
            InitialDirectory = AppPaths.SetFolder,
            Filter = "FamilyMEP Set (*.familymepset.json)|*.familymepset.json|Legacy set (*.lnset.json)|*.lnset.json",
            FileName = $"family_set_{DateTime.Now:yyyyMMdd_HHmm}.lnset.json"
        };
        if (dialog.ShowDialog(_window) != true) return;
        File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(paths, new JsonSerializerOptions { WriteIndented = true }));
        SetStatus($"Saved set with {paths.Count} families.");
    }

    private async Task LoadSetAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Load family set",
            InitialDirectory = AppPaths.SetFolder,
            Filter = "FamilyMEP Set (*.familymepset.json)|*.familymepset.json|Legacy set (*.lnset.json)|*.lnset.json"
        };
        if (dialog.ShowDialog(_window) != true) return;
        List<string> paths = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(dialog.FileName)) ?? [];
        foreach (string root in paths.Select(Path.GetDirectoryName).Where(path => path is not null).Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (Path.GetPathRoot(root)?.Equals(@"F:\", StringComparison.OrdinalIgnoreCase) == true
                && !_state.Roots.Contains(root, StringComparer.OrdinalIgnoreCase))
            {
                _state.Roots.Add(root);
            }
        }
        await ScanAsync();
        HashSet<string> selected = new(paths, StringComparer.OrdinalIgnoreCase);
        foreach (FamilyItem item in _allFamilies) item.Selected = selected.Contains(item.Path);
        UpdateBatchSummary();
        SetStatus($"Loaded set with {selected.Count} paths.");
    }

    private void ExportBatchLog()
    {
        AppPaths.EnsureCreated();
        string path = Path.Combine(AppPaths.LogFolder, $"batch_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
        IEnumerable<string> rows = new[] { "Time,Level,Family,Message" }.Concat(
            _batchLogs.Select(item => string.Join(",", Csv(item.Time), Csv(item.Level), Csv(item.FamilyName), Csv(item.Message))));
        File.WriteAllLines(path, rows);
        SetStatus($"Batch log exported to {path}");
    }

    private static string Csv(string value) => $"\"{value.Replace("\"", "\"\"")}\"";

    private void RestoreLatestBackup()
    {
        AppPaths.EnsureCreated();
        DirectoryInfo? latest = new DirectoryInfo(AppPaths.BackupFolder).EnumerateDirectories()
            .OrderByDescending(directory => directory.Name).FirstOrDefault();
        string manifestPath = latest is null ? string.Empty : Path.Combine(latest.FullName, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            MessageBox.Show(_window, "No backup session was found on drive F:.", "FamilyMEP");
            return;
        }
        RestoreBackupSession(latest!.FullName);
    }

    private void SetView(string templateKey, string panelKey)
    {
        _familyCards.ItemTemplate = (DataTemplate)_window.Resources[templateKey];
        _familyCards.ItemsPanel = (ItemsPanelTemplate)_window.Resources[panelKey];
        ScheduleVisiblePreviews();
    }

    private void QueueRevit(
        Action<UIApplication> request,
        Action<Exception?> completed,
        bool priority = true,
        bool isPreview = false,
        string? key = null)
    {
        var operation = new RevitOperation(request, completed, isPreview, key);
        if (priority) _revitOperations.AddFirst(operation);
        else _revitOperations.AddLast(operation);
        ProcessNextRevitOperation();
    }

    private void ProcessNextRevitOperation()
    {
        if (_disposed || _revitOperationRunning || _revitOperations.First is null) return;
        RevitOperation operation = _revitOperations.First.Value;
        _revitOperations.RemoveFirst();
        if (!_requestHandler.TrySetRequest(operation.Request, error =>
            _window.Dispatcher.BeginInvoke(() =>
            {
                _revitOperationRunning = false;
                if (!_disposed) operation.Completed(error);
                ProcessNextRevitOperation();
            })))
        {
            _revitOperations.AddFirst(operation);
            return;
        }

        _revitOperationRunning = true;
        if (!operation.IsPreview)
        {
            SetStatus(_projectFamilyExportTotal > 0
                ? $"Save to Tool Library: {_projectFamilyExportProcessed:N0} / {_projectFamilyExportTotal:N0} "
                  + $"({ProgressPercent(_projectFamilyExportProcessed, _projectFamilyExportTotal)}%) - waiting for Revit API..."
                : "Waiting for Revit API...");
        }
        ExternalEventRequest result = _externalEvent.Raise();
        if (result is not ExternalEventRequest.Accepted and not ExternalEventRequest.Pending)
        {
            _revitOperationRunning = false;
            operation.Completed(new InvalidOperationException($"Revit rejected the request: {result}"));
            ProcessNextRevitOperation();
        }
    }

    private Action<Exception?> ShowRevitResult(string successMessage) => error =>
    {
        if (error is not null) ShowError(error);
        else SetStatus(successMessage);
    };

    private void SaveStateWithFeedback()
    {
        string previousColor = _state.PreviewColor;
        _state.PreviewColor = SelectedPreviewColor();
        _state.WorkspaceBackground = SelectedWorkspaceBackground();
        _state.WorkspaceBackgroundOpacity = SelectedWorkspaceBackgroundOpacity();
        StateService.Save(_state);
        ApplyWorkspaceBackground();
        if (!string.Equals(previousColor, _state.PreviewColor, StringComparison.OrdinalIgnoreCase))
        {
            if (_bulkPreviewRunning) _bulkPreviewCancelled = true;
            RemovePendingPreviewOperations();
            foreach (FamilyItem family in _allFamilies) family.PreviewImage = null;
            Find<Image>("browser_preview_image").Source = null;
            Find<TextBlock>("browser_preview_status_tb").Text = "Generating preview with the new color...";
            SetStatus("Preview color saved; regenerating visible previews...");
            ScheduleVisiblePreviews();
            return;
        }
        SetStatus($"Settings saved to {AppPaths.StateFile}");
    }

    private void InitializeSettings()
    {
        ComboBox combo = Find<ComboBox>("preview_color_combo");
        ComboBoxItem? selected = combo.Items.Cast<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), _state.PreviewColor, StringComparison.OrdinalIgnoreCase));
        combo.SelectedItem = selected ?? combo.Items.Cast<ComboBoxItem>().First();
        _state.PreviewColor = SelectedPreviewColor();

        ComboBox backgroundCombo = Find<ComboBox>("workspace_background_combo");
        ComboBoxItem? selectedBackground = backgroundCombo.Items.Cast<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), _state.WorkspaceBackground, StringComparison.OrdinalIgnoreCase));
        backgroundCombo.SelectedItem = selectedBackground ?? backgroundCombo.Items.Cast<ComboBoxItem>().First();

        ComboBox opacityCombo = Find<ComboBox>("workspace_background_opacity_combo");
        ComboBoxItem? selectedOpacity = opacityCombo.Items.Cast<ComboBoxItem>()
            .OrderBy(item => Math.Abs(ParseBackgroundOpacity(item.Tag?.ToString()) - _state.WorkspaceBackgroundOpacity))
            .FirstOrDefault();
        opacityCombo.SelectedItem = selectedOpacity ?? opacityCombo.Items.Cast<ComboBoxItem>().ElementAt(1);
        ApplyWorkspaceBackground();
    }

    private string SelectedPreviewColor() =>
        Find<ComboBox>("preview_color_combo").SelectedItem is ComboBoxItem item
            ? item.Tag?.ToString() ?? "#4B5563"
            : "#4B5563";

    private string SelectedWorkspaceBackground() =>
        Find<ComboBox>("workspace_background_combo").SelectedItem is ComboBoxItem item
            ? item.Tag?.ToString() ?? "None"
            : "None";

    private double SelectedWorkspaceBackgroundOpacity() =>
        Find<ComboBox>("workspace_background_opacity_combo").SelectedItem is ComboBoxItem item
            ? ParseBackgroundOpacity(item.Tag?.ToString())
            : 0.12;

    private static double ParseBackgroundOpacity(string? value) =>
        double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double opacity)
            ? Clamp(opacity, 0.02, 0.30)
            : 0.12;

    private void ApplyWorkspaceBackground()
    {
        Image image = Find<Image>("workspace_background_image");
        string selection = SelectedWorkspaceBackground();
        image.Opacity = SelectedWorkspaceBackgroundOpacity();
        if (selection.Equals("None", StringComparison.OrdinalIgnoreCase))
        {
            image.Source = null;
            image.Visibility = Visibility.Collapsed;
            return;
        }

        string assemblyFolder = Path.GetDirectoryName(typeof(FamilyManagerController).Assembly.Location) ?? string.Empty;
        string imagePath = Path.Combine(assemblyFolder, "Assets", "Backgrounds", selection);
        if (!File.Exists(imagePath))
        {
            image.Source = null;
            image.Visibility = Visibility.Collapsed;
            return;
        }

        image.Source = LoadBitmap(imagePath);
        image.Visibility = Visibility.Visible;
    }

    private void ShowSelectionRequired() => MessageBox.Show(_window, "Select one or more families first.", "FamilyMEP");
    private void ShowError(Exception error)
    {
        SetStatus("Operation failed");
        MessageBox.Show(_window, error.Message, "FamilyMEP");
    }
    private void ShowOperationProgress(
        string label,
        int current,
        int total,
        string waitingText = "Working...")
    {
        Find<Grid>("operation_progress_panel").Visibility = Visibility.Visible;
        Find<TextBlock>("operation_progress_label_tb").Text = label;
        ProgressBar bar = Find<ProgressBar>("operation_progress_bar");
        bar.IsIndeterminate = total <= 0;
        if (total > 0)
        {
            bar.Maximum = total;
            int safeCurrent = Clamp(current, 0, total);
            bar.Value = safeCurrent;
            Find<TextBlock>("operation_progress_count_tb").Text =
                $"{safeCurrent:N0} / {total:N0}  ·  {ProgressPercent(safeCurrent, total)}%";
        }
        else
        {
            Find<TextBlock>("operation_progress_count_tb").Text = waitingText;
        }
    }

    private static int Clamp(int value, int minimum, int maximum) =>
        value < minimum ? minimum : value > maximum ? maximum : value;

    private static int ProgressPercent(int current, int total) =>
        total <= 0 ? 0 : Clamp((int)Math.Round(current * 100d / total), 0, 100);

    private static double Clamp(double value, double minimum, double maximum) =>
        value < minimum ? minimum : value > maximum ? maximum : value;

    private static string RemoveSuffixInsensitive(string value, string suffix) =>
        value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? value.Substring(0, value.Length - suffix.Length)
            : value;

    private void HideOperationProgress()
    {
        Find<ProgressBar>("operation_progress_bar").IsIndeterminate = false;
        Find<Grid>("operation_progress_panel").Visibility = Visibility.Collapsed;
    }

    private void RestoreOperationProgress()
    {
        if (_categorySyncRunning)
        {
            ShowOperationProgress("Revit categories", _categorySyncProcessed, _categorySyncTotal);
        }
        else if (_bulkPreviewRunning)
        {
            ShowOperationProgress("Preview cache", _bulkPreviewProcessed, _bulkPreviewTotal);
        }
        else if (_previewQueued.Count > 0)
        {
            ShowOperationProgress("Previews", _previewProgressCompleted, Math.Max(_previewProgressTotal, 1));
        }
        else
        {
            HideOperationProgress();
        }
    }

    private void SetStatus(string text) => _status.Text = text;
    private void SetBusy(bool busy) => _window.Cursor = busy ? Cursors.Wait : Cursors.Arrow;

    private sealed record RevitOperation(
        Action<UIApplication> Request,
        Action<Exception?> Completed,
        bool IsPreview,
        string? Key);
}
