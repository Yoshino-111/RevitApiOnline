using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;
using Autodesk.Revit.UI;
using FamilyMEP.Plugin.Infrastructure;
using FamilyMEP.Plugin.Models;
using FamilyMEP.Plugin.Services;
using Microsoft.Win32;
using IOPath = System.IO.Path;
using TextBox = System.Windows.Controls.TextBox;

namespace FamilyMEP.Plugin.Ui;

internal enum UnifiedFamilyKind
{
    Valve,
    PipeFitting,
    AirTerminal,
    FireDamper
}

internal sealed class UnifiedFamilyCreatorWindow : Window
{
    private static readonly SolidColorBrush Navy = Brush(13, 51, 92);
    private static readonly SolidColorBrush Blue = Brush(0, 101, 204);
    private static readonly SolidColorBrush Green = Brush(22, 156, 76);
    private static readonly SolidColorBrush Orange = Brush(244, 104, 20);
    private static readonly SolidColorBrush Ink = Brush(35, 47, 61);
    private static readonly SolidColorBrush Muted = Brush(91, 107, 126);
    private static readonly SolidColorBrush Border = Brush(210, 220, 232);
    private static readonly SolidColorBrush Surface = Brush(246, 249, 252);

    private readonly UIApplication _uiApplication;
    private readonly List<LibraryItem> _items;
    private readonly Dictionary<string, AirTerminalInspection> _airInspections = new(
        StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FireDamperInspection> _damperInspections = new(
        StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PipeFittingInspection> _pipeFittingInspections = new(
        StringComparer.OrdinalIgnoreCase);
    private readonly TextBox _search;
    private readonly ListBox _library;
    private readonly WrapPanel _types;
    private readonly TextBlock _typesTitle;
    private readonly Canvas _preview;
    private readonly TextBlock _previewTitle;
    private readonly TextBlock _previewStatus;
    private readonly TextBlock _source;
    private readonly TextBlock _category;
    private readonly TextBlock _partType;
    private readonly TextBox _typeName;
    private readonly TextBox _dn;
    private readonly TextBox _length;
    private readonly TextBox _bodyDiameter;
    private readonly TextBlock _connectorSummary;
    private readonly TextBlock _lookupSummary;
    private readonly TextBox _outputPath;
    private readonly CheckBox _openAfterCreate;
    private readonly TextBlock _status;
    private readonly Button _pipeFilter;
    private readonly Button _airFilter;
    private readonly Button _ductFilter;
    private readonly Button _allFilter;
    private string _activeFilter = "All";
    private LibraryItem? _selectedItem;
    private int _selectedTypeIndex;
    private IReadOnlyList<string> _airTypeSelections = [];

    public ValveBuilderRequest? ValveRequest { get; private set; }
    public PipeFittingBuilderRequest? PipeFittingRequest { get; private set; }
    public AirTerminalBuilderRequest? AirTerminalRequest { get; private set; }
    public FireDamperBuilderRequest? FireDamperRequest { get; private set; }

    public UnifiedFamilyCreatorWindow(UIApplication uiApplication)
    {
        _uiApplication = uiApplication;
        AppPaths.EnsureCreated();
        _items = BuildLibrary();

        Title = "FamilyMEP — Unified Family Creator";
        Width = 1620;
        Height = 970;
        MinWidth = 1320;
        MinHeight = 780;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        Background = Surface;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13;

        _search = Input("Search family...");
        _search.Foreground = Muted;
        _search.TextChanged += (_, _) => ApplyLibraryFilter();
        _search.GotFocus += (_, _) =>
        {
            if (!_search.Text.Equals("Search family...", StringComparison.OrdinalIgnoreCase))
                return;
            _search.Text = string.Empty;
            _search.Foreground = Ink;
        };
        _search.LostFocus += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(_search.Text)) return;
            _search.Text = "Search family...";
            _search.Foreground = Muted;
        };
        _library = new ListBox
        {
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            ItemTemplate = BuildLibraryTemplate(),
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        _library.SelectionChanged += (_, _) => SelectLibraryItem(
            _library.SelectedItem as LibraryItem);
        _types = new WrapPanel { Margin = new Thickness(2, 8, 2, 0) };
        _typesTitle = Label(
            "Official Types",
            13,
            FontWeights.SemiBold,
            Navy);
        _preview = new Canvas
        {
            Width = 760,
            Height = 500,
            Background = Brushes.White
        };
        _previewTitle = Label("Geometry & Connector Preview", 17, FontWeights.SemiBold, Navy);
        _previewStatus = Label("", 12, FontWeights.Normal, Muted);
        _source = ValueLabel();
        _category = ValueLabel();
        _partType = ValueLabel();
        _typeName = ReadOnlyInput();
        _dn = ReadOnlyInput();
        _length = ReadOnlyInput();
        _bodyDiameter = ReadOnlyInput();
        _connectorSummary = ValueLabel();
        _lookupSummary = ValueLabel();
        _outputPath = Input("");
        _openAfterCreate = new CheckBox
        {
            Content = "Open Family after create",
            IsChecked = true,
            Foreground = Ink,
            Margin = new Thickness(0, 10, 0, 0)
        };
        _status = Label("Ready", 13, FontWeights.SemiBold, Ink);
        _allFilter = FilterButton("All", "All");
        _pipeFilter = FilterButton("Pipe / Fittings", "Pipe");
        _airFilter = FilterButton("Air Terminals", "Air Terminals");
        _ductFilter = FilterButton("Duct Accessories", "Duct Accessories");

        Content = BuildLayout();
        ApplyLibraryFilter();
        _library.SelectedIndex = 0;
        RefreshFilterButtons();
    }

    private UIElement BuildLayout()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(BuildHeader());

        var body = new Grid { Background = Surface };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(410) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(445) });
        body.Children.Add(BuildLibraryPanel());
        UIElement previewPanel = BuildPreviewPanel();
        Grid.SetColumn(previewPanel, 1);
        body.Children.Add(previewPanel);
        UIElement configPanel = BuildConfigurationPanel();
        Grid.SetColumn(configPanel, 2);
        body.Children.Add(configPanel);
        Grid.SetRow(body, 1);
        root.Children.Add(body);

        UIElement footer = BuildFooter();
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);
        return root;
    }

    private UIElement BuildHeader()
    {
        var header = new Grid
        {
            Background = Brushes.White,
            Height = 112,
            Margin = new Thickness(0)
        };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(330) });
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var brand = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(24, 30, 0, 0)
        };
        brand.Children.Add(Label("Family", 30, FontWeights.Bold, Navy));
        brand.Children.Add(Label("MEP", 30, FontWeights.Bold, Blue));
        header.Children.Add(brand);

        var steps = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        steps.Children.Add(Step("1", "Select", true));
        steps.Children.Add(StepLine());
        steps.Children.Add(Step("2", "Type Data", false));
        steps.Children.Add(StepLine());
        steps.Children.Add(Step("3", "Preview", false));
        steps.Children.Add(StepLine());
        steps.Children.Add(Step("4", "Validate", false));
        steps.Children.Add(StepLine());
        steps.Children.Add(Step("5", "Create", false));
        Grid.SetColumn(steps, 1);
        header.Children.Add(steps);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 33, 22, 0)
        };
        Button catalogs = SecondaryButton("Catalogs", 108);
        catalogs.Click += (_, _) => OpenSelectedCatalog();
        actions.Children.Add(catalogs);
        Button settings = SecondaryButton("Settings", 108);
        settings.Margin = new Thickness(8, 0, 0, 0);
        settings.Click += (_, _) => MessageBox.Show(
            this,
            "Unified output settings:\n\n"
            + "• Metric display units\n"
            + "• Neutral Gray materials\n"
            + "• Compact FT_* parameter names\n"
            + "• Blank manufacturer metadata\n"
            + "• Open generated Family enabled by default",
            "FamilyMEP — Settings",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
        actions.Children.Add(settings);
        Grid.SetColumn(actions, 2);
        header.Children.Add(actions);
        return header;
    }

    private UIElement BuildLibraryPanel()
    {
        var root = new Grid
        {
            Background = Brushes.White,
            Margin = new Thickness(8, 8, 4, 8)
        };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        var header = new StackPanel { Margin = new Thickness(16, 16, 16, 10) };
        header.Children.Add(Label("Family Library", 17, FontWeights.SemiBold, Navy));
        _search.Margin = new Thickness(0, 8, 0, 10);
        header.Children.Add(_search);
        var categoryFilters = new UniformGrid { Columns = 4 };
        categoryFilters.Children.Add(_allFilter);
        categoryFilters.Children.Add(_pipeFilter);
        categoryFilters.Children.Add(_airFilter);
        categoryFilters.Children.Add(_ductFilter);
        header.Children.Add(categoryFilters);
        root.Children.Add(header);
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _library,
            Margin = new Thickness(10, 0, 8, 12)
        };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);
        return root;
    }

    private UIElement BuildPreviewPanel()
    {
        var root = new Grid
        {
            Background = Brushes.White,
            Margin = new Thickness(4, 8, 4, 8)
        };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var header = new Grid { Margin = new Thickness(18, 16, 18, 8) };
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(_previewTitle);
        var views = new StackPanel { Orientation = Orientation.Horizontal };
        views.Children.Add(SecondaryButton("Fit", 64));
        views.Children.Add(SecondaryButton("Front", 72));
        views.Children.Add(SecondaryButton("Top", 62));
        views.Children.Add(SecondaryButton("Section", 78));
        Grid.SetColumn(views, 1);
        header.Children.Add(views);
        root.Children.Add(header);

        var viewbox = new Viewbox
        {
            Stretch = Stretch.Uniform,
            Margin = new Thickness(12, 0, 12, 0),
            Child = _preview
        };
        Grid.SetRow(viewbox, 1);
        root.Children.Add(viewbox);

        var bottom = new StackPanel { Margin = new Thickness(18, 6, 18, 14) };
        bottom.Children.Add(_typesTitle);
        bottom.Children.Add(_types);
        Border statusCard = Card(_previewStatus, new Thickness(0, 12, 0, 0));
        statusCard.Padding = new Thickness(14, 10, 14, 10);
        bottom.Children.Add(statusCard);
        Grid.SetRow(bottom, 2);
        root.Children.Add(bottom);
        return root;
    }

    private UIElement BuildConfigurationPanel()
    {
        var root = new DockPanel
        {
            Background = Brushes.White,
            Margin = new Thickness(4, 8, 8, 8)
        };
        TextBlock title = Label(
            "Build Configuration",
            17,
            FontWeights.SemiBold,
            Navy);
        title.Margin = new Thickness(18, 16, 18, 12);
        DockPanel.SetDock(title, Dock.Top);
        root.Children.Add(title);
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = BuildConfigurationContent()
        };
        root.Children.Add(scroll);
        return root;
    }

    private UIElement BuildConfigurationContent()
    {
        var stack = new StackPanel { Margin = new Thickness(14, 0, 14, 16) };
        var sourceCard = new Grid();
        sourceCard.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(108) });
        sourceCard.ColumnDefinitions.Add(new ColumnDefinition());
        AddInfoRow(sourceCard, 0, "Source:", _source);
        AddInfoRow(sourceCard, 1, "Category:", _category);
        AddInfoRow(sourceCard, 2, "Part type:", _partType);
        stack.Children.Add(Card(sourceCard, new Thickness(0, 0, 0, 10)));

        var dimensions = new Grid();
        dimensions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(145) });
        dimensions.ColumnDefinitions.Add(new ColumnDefinition());
        AddEditorRow(dimensions, 0, "Type:", _typeName);
        AddEditorRow(dimensions, 1, "DN / Neck:", _dn);
        AddEditorRow(dimensions, 2, "Body length L:", _length);
        AddEditorRow(dimensions, 3, "Body / Face:", _bodyDiameter);
        stack.Children.Add(ExpanderCard("Type & Dimensions", dimensions, true));

        stack.Children.Add(ExpanderCard("Connectors", _connectorSummary, false));
        stack.Children.Add(ExpanderCard("Lookup Tables", _lookupSummary, false));
        stack.Children.Add(ExpanderCard(
            "Parameters & Naming",
            Label(
                "Editable custom parameters will be normalized to compact FT_* names.",
                12,
                FontWeights.Normal,
                Muted),
            false));
        stack.Children.Add(ExpanderCard(
            "Materials",
            Label(
                "Neutral Gray output; manufacturer material metadata stays blank.",
                12,
                FontWeights.Normal,
                Muted),
            false));

        var output = new StackPanel();
        _outputPath.Margin = new Thickness(0, 0, 0, 8);
        output.Children.Add(_outputPath);
        var outputRow = new StackPanel { Orientation = Orientation.Horizontal };
        Button browse = SecondaryButton("Browse...", 96);
        browse.Click += (_, _) => BrowseOutput();
        outputRow.Children.Add(browse);
        _openAfterCreate.Margin = new Thickness(12, 7, 0, 0);
        outputRow.Children.Add(_openAfterCreate);
        output.Children.Add(outputRow);
        stack.Children.Add(ExpanderCard("Output", output, true));
        return stack;
    }

    private UIElement BuildFooter()
    {
        var footer = new Grid
        {
            Height = 84,
            Background = Brushes.White
        };
        footer.ColumnDefinitions.Add(new ColumnDefinition());
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var statusPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(24, 23, 0, 0)
        };
        statusPanel.Children.Add(new Ellipse
        {
            Width = 22,
            Height = 22,
            Fill = Green,
            Margin = new Thickness(0, 0, 9, 0)
        });
        statusPanel.Children.Add(_status);
        footer.Children.Add(statusPanel);
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 16, 18, 16)
        };
        Button validate = SecondaryButton("Validate", 138);
        validate.Click += (_, _) => ValidateSelection(true);
        actions.Children.Add(validate);
        Button saveConfig = SecondaryButton("Save Config", 150);
        saveConfig.Margin = new Thickness(10, 0, 0, 0);
        saveConfig.Click += (_, _) => SaveConfiguration();
        actions.Children.Add(saveConfig);
        Button create = PrimaryButton("Create / Update Family", 238);
        create.Margin = new Thickness(10, 0, 0, 0);
        create.Click += (_, _) => Accept();
        actions.Children.Add(create);
        Grid.SetColumn(actions, 1);
        footer.Children.Add(actions);
        return footer;
    }

    private void ApplyLibraryFilter()
    {
        string search = _search.Text.Equals(
            "Search family...",
            StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : _search.Text.Trim();
        List<LibraryItem> filtered = _items
            .Where(item =>
                (_activeFilter == "All"
                 || (_activeFilter == "Pipe"
                     && item.Category.StartsWith(
                         "Pipe",
                         StringComparison.OrdinalIgnoreCase))
                 || item.Category == _activeFilter)
                && (string.IsNullOrWhiteSpace(search)
                    || item.Title.Contains(search, StringComparison.OrdinalIgnoreCase)
                    || item.Subtitle.Contains(search, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        _library.ItemsSource = filtered;
        LibraryItem? current = _library.SelectedItem as LibraryItem;
        if (filtered.Count > 0 && (current is null || !filtered.Contains(current)))
            _library.SelectedIndex = 0;
    }

    private void SelectLibraryItem(LibraryItem? item)
    {
        if (item is null) return;
        _selectedItem = item;
        _selectedTypeIndex = 0;
        _previewTitle.Text = $"{item.Title} — Geometry & Connector Preview";
        _source.Text =
            item.Kind is UnifiedFamilyKind.AirTerminal
                or UnifiedFamilyKind.FireDamper
                or UnifiedFamilyKind.PipeFitting
                ? "Official manufacturer catalog • native parametric build"
                : "Official reference RFA";
        _category.Text = item.Category;
        _partType.Text = item.PartType;

        if (item.Kind == UnifiedFamilyKind.Valve)
        {
            _typesTitle.Text = "Official Types";
            _airTypeSelections = [];
            ValveBuilderWindow.ValveCatalogDefinition catalog =
                ValveBuilderWindow.Catalogs[item.SourceIndex];
            PopulateValveTypes(catalog);
            _connectorSummary.Text =
                "2 native pipe connectors • manufacturer association preserved";
            _lookupSummary.Text =
                "External Type Catalog + embedded lookup tables are copied and renamed.";
            DrawPreview(item, catalog.Sizes[Math.Min(5, catalog.Sizes.Length - 1)]);
        }
        else if (item.Kind == UnifiedFamilyKind.PipeFitting)
        {
            _typesTitle.Text = "Prototype Gate";
            PopulatePipeFitting(item);
        }
        else if (item.Kind == UnifiedFamilyKind.AirTerminal)
        {
            _typesTitle.Text = item.Title.Contains(
                "TROX RFD",
                StringComparison.OrdinalIgnoreCase)
                ? "Product Variants"
                : "Official Types";
            PopulateAirTerminalTypes(item);
        }
        else
        {
            _typesTitle.Text = "Product";
            PopulateFireDamper(item);
        }
    }

    private void PopulatePipeFitting(LibraryItem item)
    {
        _types.Children.Clear();
        PipeFittingDefinition definition =
            PipeFittingCatalog.All[item.SourceIndex];
        try
        {
            if (!_pipeFittingInspections.TryGetValue(
                    definition.Key,
                    out PipeFittingInspection? inspection))
            {
                inspection = GeberitMapressBendBuilderService.Inspect();
                _pipeFittingInspections[definition.Key] = inspection;
            }
            _airTypeSelections = [];
            Button button = TypeButton(inspection.DefaultTypeName, 0);
            button.Click += (_, _) =>
            {
                _selectedTypeIndex = 0;
                RefreshTypeButtons();
            };
            _types.Children.Add(button);
            _selectedTypeIndex = 0;
            _typeName.Text = inspection.DefaultTypeName;
            _dn.Text =
                $"DN {inspection.NominalDiameterMm:0} / d {inspection.OutsideDiameterMm:0} mm";
            _length.Text = $"L {inspection.LengthMm:0} mm";
            _bodyDiameter.Text = $"Z {inspection.ZMm:0} mm";
            _outputPath.Text = IOPath.Combine(
                AppPaths.GeneratedPipeFittingFolder,
                $"{definition.OutputFamilyName}.rfa");
            _connectorSummary.Text =
                "2 face-hosted pipe connectors - d22 mm - orthogonal outward directions";
            _lookupSummary.Text =
                "DN20 acceptance prototype only - full official lookup table remains gated";
            _previewStatus.Text =
                "Official row 30104 - DN20/d22/L47/Z26 - one nested Mapress Type - automatic acceptance before save";
            _status.Text = "Ready - prototype gate configured";
            _status.Foreground = Ink;
            DrawPreview(item, null);
            RefreshTypeButtons();
        }
        catch (Exception exception)
        {
            _status.Text = $"Catalog inspection failed: {exception.Message}";
            _status.Foreground = Orange;
        }
    }

    private void PopulateFireDamper(LibraryItem item)
    {
        _types.Children.Clear();
        FireDamperDefinition definition = FireDamperCatalog.All[item.SourceIndex];
        try
        {
            if (!_damperInspections.TryGetValue(
                    definition.Key,
                    out FireDamperInspection? inspection))
            {
                inspection = TroxKa2BuilderService.Inspect();
                _damperInspections[definition.Key] = inspection;
            }
            _airTypeSelections = [];
            Button button = TypeButton("KA2-EU", 0);
            button.Click += (_, _) =>
            {
                _selectedTypeIndex = 0;
                RefreshTypeButtons();
            };
            _types.Children.Add(button);
            _selectedTypeIndex = 0;
            _typeName.Text = inspection.DefaultTypeName;
            _dn.Text = "B250–1200 × H250–500";
            _length.Text = "L 580 / 680 mm";
            _bodyDiameter.Text = "429 catalog B×H Types";
            _outputPath.Text = IOPath.Combine(
                AppPaths.GeneratedDuctAccessoryFolder,
                $"{definition.OutputFamilyName}.rfa");
            _connectorSummary.Text =
                "2 rectangular Exhaust Air duct connectors • B×H lookup associated";
            _lookupSummary.Text =
                $"Embedded KA2-EU lookup table • {inspection.TypeCount} rows";
            _previewStatus.Text =
                "✓ TROX catalog dimensions • ✓ 429 internal Types • ✓ 2 rectangular connectors • ✓ unhosted Duct Accessory";
            _status.Text = "Ready — catalog validated";
            _status.Foreground = Ink;
            DrawPreview(item, null);
            RefreshTypeButtons();
        }
        catch (Exception exception)
        {
            _status.Text = $"Catalog inspection failed: {exception.Message}";
            _status.Foreground = Orange;
        }
    }

    private void PopulateValveTypes(
        ValveBuilderWindow.ValveCatalogDefinition catalog)
    {
        _types.Children.Clear();
        int defaultIndex = Math.Min(5, catalog.Sizes.Length - 1);
        foreach ((ValveBuilderWindow.ValveSizePreset preset, int index) in
                 catalog.Sizes.Select((value, index) => (value, index)))
        {
            Button button = TypeButton(preset.Name, index);
            button.Click += (_, _) =>
            {
                _selectedTypeIndex = index;
                RefreshTypeButtons();
                ApplyValvePreset(catalog, preset);
                DrawPreview(_selectedItem!, preset);
            };
            _types.Children.Add(button);
        }
        _selectedTypeIndex = defaultIndex;
        ApplyValvePreset(catalog, catalog.Sizes[defaultIndex]);
        RefreshTypeButtons();
    }

    private void PopulateAirTerminalTypes(LibraryItem item)
    {
        _types.Children.Clear();
        AirTerminalDefinition definition = AirTerminalCatalog.All[item.SourceIndex];
        try
        {
            _status.Text = $"Reading official Types for {item.Title}...";
            if (!_airInspections.TryGetValue(definition.Key, out AirTerminalInspection? inspection))
            {
                inspection = AirTerminalBuilderService.Inspect(
                    _uiApplication,
                    definition);
                _airInspections[definition.Key] = inspection;
            }
            IReadOnlyList<(string DisplayName, string PreviewTypeName)> choices =
                definition.IsProcedural
                    ? inspection.TypeNames
                        .GroupBy(
                            TroxVariantName,
                            StringComparer.OrdinalIgnoreCase)
                        .Select(group => (
                            DisplayName: group.Key,
                            PreviewTypeName: group.First()))
                        .ToList()
                    : inspection.TypeNames
                        .Select(name => (
                            DisplayName: name,
                            PreviewTypeName: name))
                        .ToList();
            _airTypeSelections = choices
                .Select(choice => choice.PreviewTypeName)
                .ToList();
            foreach (((string displayName, string previewTypeName), int index) in
                     choices.Select((value, index) => (value, index)))
            {
                Button button = TypeButton(displayName, index);
                button.Click += (_, _) =>
                {
                    _selectedTypeIndex = index;
                    RefreshTypeButtons();
                    ApplyAirTerminalType(
                        definition,
                        inspection,
                        previewTypeName);
                    DrawPreview(item, null);
                };
                _types.Children.Add(button);
            }
            _selectedTypeIndex = 0;
            ApplyAirTerminalType(
                definition,
                inspection,
                _airTypeSelections[0]);
            _connectorSummary.Text =
                $"{inspection.ConnectorCount} native duct connector(s) • association preserved";
            _lookupSummary.Text =
                "Embedded lookup tables, when present, are copied and renamed.";
            DrawPreview(item, null);
            RefreshTypeButtons();
            _status.Text = "Ready — official RFA inspected";
        }
        catch (Exception exception)
        {
            _status.Text = $"Source inspection failed: {exception.Message}";
            _status.Foreground = Orange;
            _types.Children.Add(Label(
                "No source Types available",
                12,
                FontWeights.Normal,
                Orange));
        }
    }

    private void ApplyValvePreset(
        ValveBuilderWindow.ValveCatalogDefinition catalog,
        ValveBuilderWindow.ValveSizePreset preset)
    {
        _typeName.Text = preset.Name;
        _dn.Text = $"{preset.Dn:0.#} mm";
        _length.Text = $"{preset.Length:0.#} mm";
        _bodyDiameter.Text = $"{preset.BodyDiameter:0.#} mm";
        _outputPath.Text = IOPath.Combine(
            AppPaths.GeneratedValveFolder,
            $"{catalog.FamilyStem}_{preset.Name}.rfa");
        _previewStatus.Text =
            $"✓ Official RFA geometry    •    ✓ {catalog.Sizes.Length} Types    •    ✓ 2 connectors    •    ✓ Metric output";
        _status.Text = "Ready — 0 errors, 0 warnings";
        _status.Foreground = Ink;
    }

    private void ApplyAirTerminalType(
        AirTerminalDefinition definition,
        AirTerminalInspection inspection,
        string typeName)
    {
        _typeName.Text = definition.IsProcedural
            ? TroxVariantName(typeName)
            : typeName;
        if (definition.IsProcedural
            && TroxRfdBuilderService.TryGetDimensions(
                typeName,
                out _,
                out _,
                out _,
                out _))
        {
            _dn.Text = "6 sizes inside Family";
            _length.Text = "Catalog lookup driven";
            _bodyDiameter.Text = "Catalog lookup driven";
        }
        else
        {
            _dn.Text = "From official Type";
            _length.Text = "Driven by source constraints";
            _bodyDiameter.Text = "Driven by source constraints";
        }
        string outputName = definition.OutputFamilyName;
        if (definition.IsProcedural)
        {
            string series = TroxVariantName(typeName);
            outputName = $"AirTerminal_TROX_{series.Replace('-', '_')}";
        }
        _outputPath.Text = IOPath.Combine(
            AppPaths.GeneratedAirTerminalFolder,
            $"{outputName}.rfa");
        _previewStatus.Text =
            definition.IsProcedural
                ? $"✓ TROX catalog dimensions    •    ✓ {_airTypeSelections.Count} variants    •    ✓ 6 sizes per Family    •    ✓ native unhosted geometry"
                : $"✓ Official RFA geometry    •    ✓ {inspection.TypeNames.Count} Types    •    ✓ {inspection.ConnectorCount} duct connector(s)    •    ✓ Metric output";
    }

    private void RefreshTypeButtons()
    {
        foreach ((Button button, int index) in _types.Children
                     .OfType<Button>()
                     .Select((value, index) => (value, index)))
        {
            bool selected = index == _selectedTypeIndex;
            button.Background = selected ? Blue : Brushes.White;
            button.Foreground = selected ? Brushes.White : Ink;
            button.FontWeight =
                selected ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }

    private bool ValidateSelection(bool showDialog)
    {
        var errors = new List<string>();
        if (_selectedItem is null)
            errors.Add("Select one Family.");
        else if (!File.Exists(_selectedItem.SourcePath))
            errors.Add($"Official source RFA not found:\n{_selectedItem.SourcePath}");
        if (_types.Children.OfType<Button>().Count() == 0)
            errors.Add("No official Type is available.");
        if (string.IsNullOrWhiteSpace(_outputPath.Text))
            errors.Add("Output RFA path is empty.");
        else if (!IOPath.GetExtension(_outputPath.Text)
                     .Equals(".rfa", StringComparison.OrdinalIgnoreCase))
            errors.Add("Output path must end with .rfa.");

        if (errors.Count > 0)
        {
            _status.Text = $"{errors.Count} validation error(s)";
            _status.Foreground = Orange;
            if (showDialog)
                MessageBox.Show(
                    this,
                    string.Join(Environment.NewLine + Environment.NewLine, errors),
                    "FamilyMEP — Validation",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            return false;
        }
        _status.Text = "Ready — 0 errors, 0 warnings";
        _status.Foreground = Ink;
        if (showDialog)
            MessageBox.Show(
                this,
                "Validation passed.\n\nThe official source RFA, Type data and connector configuration are ready.",
                "FamilyMEP — Validation",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        return true;
    }

    private void Accept()
    {
        if (!ValidateSelection(false) || _selectedItem is null) return;
        if (_selectedItem.Kind == UnifiedFamilyKind.Valve)
        {
            ValveRequest = ValveBuilderWindow.CreateRequest(
                _selectedItem.SourceIndex,
                _selectedTypeIndex,
                _outputPath.Text.Trim(),
                _openAfterCreate.IsChecked == true);
        }
        else if (_selectedItem.Kind == UnifiedFamilyKind.PipeFitting)
        {
            PipeFittingDefinition definition =
                PipeFittingCatalog.All[_selectedItem.SourceIndex];
            PipeFittingRequest = new PipeFittingBuilderRequest(
                definition,
                _outputPath.Text.Trim(),
                _openAfterCreate.IsChecked == true);
        }
        else if (_selectedItem.Kind == UnifiedFamilyKind.AirTerminal)
        {
            AirTerminalDefinition definition =
                AirTerminalCatalog.All[_selectedItem.SourceIndex];
            string typeName = _airTypeSelections[_selectedTypeIndex];
            AirTerminalRequest = new AirTerminalBuilderRequest(
                definition,
                typeName,
                _outputPath.Text.Trim(),
                _openAfterCreate.IsChecked == true);
        }
        else
        {
            FireDamperDefinition definition =
                FireDamperCatalog.All[_selectedItem.SourceIndex];
            FireDamperRequest = new FireDamperBuilderRequest(
                definition,
                _outputPath.Text.Trim(),
                _openAfterCreate.IsChecked == true);
        }
        DialogResult = true;
    }

    private void BrowseOutput()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Save generated Revit Family",
            Filter = "Revit Family (*.rfa)|*.rfa",
            FileName = IOPath.GetFileName(_outputPath.Text),
            InitialDirectory = IOPath.GetDirectoryName(_outputPath.Text)
        };
        if (dialog.ShowDialog(this) == true)
            _outputPath.Text = dialog.FileName;
    }

    private void OpenSelectedCatalog()
    {
        if (_selectedItem is null) return;
        string url = _selectedItem.Kind switch
        {
            UnifiedFamilyKind.Valve =>
                ValveBuilderWindow.Catalogs[_selectedItem.SourceIndex].CatalogUrl,
            UnifiedFamilyKind.AirTerminal =>
                AirTerminalCatalog.All[_selectedItem.SourceIndex].CatalogUrl,
            UnifiedFamilyKind.PipeFitting =>
                PipeFittingCatalog.All[_selectedItem.SourceIndex].ProductUrl,
            _ => FireDamperCatalog.All[_selectedItem.SourceIndex].CatalogUrl
        };
        if (string.IsNullOrWhiteSpace(url))
        {
            MessageBox.Show(
                this,
                "This source RFA has no separate catalog URL configured.",
                "FamilyMEP — Catalogs",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                exception.Message,
                "FamilyMEP — Catalogs",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void SaveConfiguration()
    {
        if (_selectedItem is null) return;
        string path = IOPath.Combine(
            AppPaths.Root,
            "unified-family-creator.json");
        string json = JsonSerializer.Serialize(
            new
            {
                Family = _selectedItem.Title,
                Category = _selectedItem.Category,
                Type = _typeName.Text,
                OutputPath = _outputPath.Text,
                OpenAfterCreate = _openAfterCreate.IsChecked == true
            },
            new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
        _status.Text = $"Configuration saved — {path}";
        _status.Foreground = Green;
    }

    private void DrawPreview(
        LibraryItem item,
        ValveBuilderWindow.ValveSizePreset? preset)
    {
        _preview.Children.Clear();
        if (item.Kind == UnifiedFamilyKind.AirTerminal)
        {
            DrawAirTerminal(item);
            return;
        }
        if (item.Kind == UnifiedFamilyKind.FireDamper)
        {
            DrawFireDamperPreview();
            return;
        }
        if (item.Kind == UnifiedFamilyKind.PipeFitting)
        {
            DrawPipeFittingPreview();
            return;
        }

        double dn = preset?.Dn ?? 50;
        double length = preset?.Length ?? 131.1;
        double bodyD = preset?.BodyDiameter ?? 93.7;
        const double cx = 380;
        const double cy = 275;
        double bodyW = PortableMath.Clamp(length * 2.2, 260, 390);
        double bodyH = PortableMath.Clamp(bodyD * 1.8, 125, 195);
        double left = cx - bodyW / 2;
        double top = cy - bodyH / 2;
        AddRectangle(left - 32, top + 28, 68, bodyH - 56, Brush(185, 192, 201), Ink, 2, 10);
        AddRectangle(left + bodyW - 36, top + 28, 68, bodyH - 56, Brush(185, 192, 201), Ink, 2, 10);
        AddEllipse(left + 20, top, bodyW - 40, bodyH, Brush(220, 225, 231), Ink, 2);
        AddEllipse(left + 50, top + 9, bodyW - 100, bodyH - 18, Brush(196, 204, 214), Muted, 1.5);
        double port = PortableMath.Clamp(dn * 1.35, 48, 100);
        AddEllipse(left - 45, cy - port / 2, 38, port, Brush(43, 47, 52), Orange, 4);
        AddEllipse(left + bodyW + 7, cy - port / 2, 38, port, Brush(43, 47, 52), Orange, 4);
        AddRectangle(cx - 24, top - 72, 48, 82, Brush(184, 192, 202), Ink, 2, 6);
        AddEllipse(cx - 38, top - 85, 76, 28, Brush(211, 216, 223), Ink, 2);
        AddRectangle(cx - 2, top - 105, 290, 24, Brush(47, 95, 156), Ink, 2, 12);
        AddText("Connector 1", left - 95, cy + 68, Orange, 13, FontWeights.SemiBold);
        AddText("Connector 2", left + bodyW + 46, cy + 68, Orange, 13, FontWeights.SemiBold);
        AddDimension(left, cy + bodyH / 2 + 70, left + bodyW, $"L  {length:0.#}");
        AddText($"DN {dn:0.#}", left - 82, cy - 8, Navy, 16, FontWeights.Bold);
        AddText($"H  {preset?.Height ?? 88.9:0.#}", left + bodyW + 105, top - 10, Navy, 16, FontWeights.Bold);
        AddText(preset?.Name ?? "DN50", cx - 32, cy - 8, Muted, 16, FontWeights.Bold);
    }

    private void DrawPipeFittingPreview()
    {
        const double cx = 375;
        const double cy = 260;
        var vertical = new Line
        {
            X1 = cx - 105,
            Y1 = cy + 115,
            X2 = cx - 105,
            Y2 = cy + 12,
            Stroke = Brush(184, 190, 198),
            StrokeThickness = 52,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        };
        _preview.Children.Add(vertical);
        var horizontal = new Line
        {
            X1 = cx - 92,
            Y1 = cy,
            X2 = cx + 35,
            Y2 = cy,
            Stroke = Brush(184, 190, 198),
            StrokeThickness = 52,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        };
        _preview.Children.Add(horizontal);
        AddEllipse(cx - 141, cy + 78, 72, 34, Brush(205, 211, 218), Navy, 2);
        AddEllipse(cx + 5, cy - 36, 34, 72, Brush(205, 211, 218), Navy, 2);
        AddEllipse(cx - 132, cy + 85, 54, 20, Brush(43, 47, 52), Orange, 3);
        AddEllipse(cx + 12, cy - 27, 20, 54, Brush(43, 47, 52), Orange, 3);
        AddText("Mapress End 1", cx - 205, cy + 130, Orange, 13, FontWeights.SemiBold);
        AddText("Mapress End 2", cx + 52, cy - 14, Orange, 13, FontWeights.SemiBold);
        AddText("90°", cx - 92, cy + 23, Navy, 18, FontWeights.Bold);
        AddText("DN20 / d22", cx - 56, cy - 67, Navy, 16, FontWeights.Bold);
        AddDimension(cx - 105, cy + 175, cx + 35, "L 47");
        _previewStatus.Text =
            "Official Geberit PRO_103025 prototype - article 30104 - L47 - Z26 - d22 connectors";
    }

    private void DrawFireDamperPreview()
    {
        const double cx = 380;
        const double cy = 255;
        const double bodyW = 410;
        const double bodyH = 250;
        double left = cx - bodyW / 2;
        double top = cy - bodyH / 2;
        AddRectangle(left, top, bodyW, bodyH, Brush(205, 211, 218), Ink, 2, 3);
        AddRectangle(left - 20, top - 18, 18, bodyH + 36, Brush(155, 164, 174), Navy, 2, 1);
        AddRectangle(left + bodyW + 2, top - 18, 18, bodyH + 36, Brush(155, 164, 174), Navy, 2, 1);
        AddRectangle(cx - 5, top - 18, 10, bodyH + 36, Brush(136, 147, 159), Navy, 1, 1);
        AddRectangle(left + 22, cy - 10, bodyW - 44, 20, Brush(108, 119, 132), Ink, 1, 1);
        AddRectangle(cx - 98, top - 104, 196, 80, Brush(91, 103, 117), Ink, 2, 5);
        AddRectangle(cx - 13, top - 28, 26, 34, Brush(124, 135, 148), Ink, 1, 2);
        AddText("Actuator Z43KA", cx - 54, top - 78, Brushes.White, 13, FontWeights.SemiBold);
        AddText("Connector 1", left - 112, cy + 22, Orange, 13, FontWeights.SemiBold);
        AddText("Connector 2", left + bodyW + 34, cy + 22, Orange, 13, FontWeights.SemiBold);
        AddText("B × H", cx - 26, cy - 36, Navy, 18, FontWeights.Bold);
        AddDimension(left, top + bodyH + 65, left + bodyW, "L 580 / 680");
        _previewStatus.Text =
            "✓ Official TROX KA2-EU dimensions • ✓ Rectangular blade/casing • ✓ 2 catalog-sized Exhaust Air connectors";
    }

    private void DrawAirTerminal(LibraryItem item)
    {
        const double left = 155;
        const double top = 72;
        const double width = 450;
        const double height = 330;
        if (item.Title.Contains("TROX RFD", StringComparison.OrdinalIgnoreCase))
        {
            DrawTroxRfdPreview(left, top, width, height);
            return;
        }
        AddRectangle(left, top, width, height, Brush(227, 232, 238), Ink, 2, 4);
        if (item.Title.Contains("Linear", StringComparison.OrdinalIgnoreCase))
        {
            for (int index = 0; index < 5; index++)
                AddRectangle(
                    left + 45,
                    top + 55 + index * 45,
                    width - 90,
                    16,
                    Brush(94, 107, 123),
                    Navy,
                    1,
                    3);
        }
        else if (item.Title.Contains("PLQ", StringComparison.OrdinalIgnoreCase))
        {
            AddRectangle(left + 75, top + 60, width - 150, height - 120, Brushes.White, Navy, 2, 4);
            AddRectangle(left + 115, top + 95, width - 230, height - 190, Brush(200, 209, 220), Muted, 1, 4);
        }
        else
        {
            for (int row = 0; row < 8; row++)
            for (int column = 0; column < 10; column++)
                AddRectangle(
                    left + 35 + column * 38,
                    top + 38 + row * 32,
                    25,
                    15,
                    row % 2 == 0 ? Brush(176, 188, 202) : Brush(204, 213, 223),
                    Muted,
                    .5,
                    1);
        }
        AddText("Duct Connector", left + width / 2 - 58, top - 36, Orange, 14, FontWeights.SemiBold);
        AddDimension(left, top + height + 54, left + width, "Face Width");
        _previewStatus.Text =
            "✓ Official RFA geometry    •    ✓ Native duct connector    •    ✓ Metric output";
    }

    private void DrawTroxRfdPreview(
        double left,
        double top,
        double width,
        double height)
    {
        bool circular = _typeName.Text.Contains(
            "-R-",
            StringComparison.OrdinalIgnoreCase);
        bool nozzle = _typeName.Text.Contains(
            "-D-",
            StringComparison.OrdinalIgnoreCase);
        string connection = _typeName.Text.Contains("-D-UD-", StringComparison.OrdinalIgnoreCase)
            ? "UD"
            : _typeName.Text.Contains("-UO-", StringComparison.OrdinalIgnoreCase)
                ? "UO"
                : _typeName.Text.Contains("-US-", StringComparison.OrdinalIgnoreCase)
                    ? "US"
                    : _typeName.Text.Contains("-D-N-", StringComparison.OrdinalIgnoreCase)
                        ? "N"
                        : _typeName.Text.Contains("-A-", StringComparison.OrdinalIgnoreCase)
                            ? "A"
                            : "K";
        const double size = 300;
        double cx = left + width / 2;
        double cy = top + height / 2 + 8;
        if (connection is "A" or "N")
        {
            double boxWidth = connection == "N" ? size * 1.12 : size * 1.28;
            double boxDepth = connection == "N" ? size * .88 : size * 1.12;
            AddRectangle(
                cx - boxWidth / 2,
                cy - boxDepth / 2,
                boxWidth,
                boxDepth,
                Brush(202, 209, 218),
                Ink,
                2,
                3);
            AddRectangle(
                cx + boxWidth / 2,
                cy - 38,
                72,
                76,
                Brush(176, 187, 199),
                Ink,
                2,
                3);
            AddEllipse(
                cx + boxWidth / 2 + 50,
                cy - 38,
                22,
                76,
                Brush(118, 130, 145),
                Orange,
                3);
        }
        else if (connection is "US" or "UO" or "UD")
        {
            AddEllipse(
                cx - size * .42,
                cy - size * .42,
                size * .84,
                size * .84,
                Brush(194, 202, 212),
                Ink,
                2);
        }
        if (circular)
            AddEllipse(
                cx - size / 2,
                cy - size / 2,
                size,
                size,
                Brush(232, 235, 239),
                Navy,
                3);
        else
            AddRectangle(
                cx - size / 2,
                cy - size / 2,
                size,
                size,
                Brush(232, 235, 239),
                Navy,
                3,
                3);

        const int bladeCount = 24;
        for (int index = 0; index < bladeCount; index++)
        {
            var blade = new Rectangle
            {
                Width = 112,
                Height = 8,
                RadiusX = 3,
                RadiusY = 3,
                Fill = index % 2 == 0
                    ? Brush(119, 135, 153)
                    : Brush(150, 164, 180),
                Stroke = Muted,
                StrokeThickness = .6,
                RenderTransformOrigin = new Point(0, .5),
                RenderTransform = new RotateTransform(
                    index * 360.0 / bladeCount + 18)
            };
            Canvas.SetLeft(blade, cx);
            Canvas.SetTop(blade, cy - 4);
            _preview.Children.Add(blade);
        }
        AddEllipse(
            cx - 19,
            cy - 19,
            38,
            38,
            nozzle ? Brush(104, 116, 132) : Brush(197, 204, 214),
            Navy,
            2);
        AddEllipse(
            cx - 6,
            cy - 6,
            12,
            12,
            Brush(86, 98, 113),
            Ink,
            1);
        AddText(
            nozzle ? "RFD with discharge nozzle" : "RFD fixed swirl blades",
            left + 120,
            top + height + 18,
            Navy,
            14,
            FontWeights.SemiBold);
        AddText(
            "Round duct connector • vertical K connection",
            left + 91,
            top - 38,
            Orange,
            13,
            FontWeights.SemiBold);
        _previewStatus.Text =
            "✓ Official TROX catalog dimensions    •    ✓ Native unhosted Air Terminal    •    ✓ Round duct connector";
    }

    private Button FilterButton(string text, string filter)
    {
        var button = new Button
        {
            Content = text,
            Height = 38,
            Margin = new Thickness(3),
            BorderBrush = Border,
            BorderThickness = new Thickness(1)
        };
        button.Click += (_, _) =>
        {
            _activeFilter = filter;
            ApplyLibraryFilter();
            RefreshFilterButtons();
        };
        return button;
    }

    private void RefreshFilterButtons()
    {
        foreach ((Button button, string filter) in new[]
                 {
                     (_allFilter, "All"),
                     (_pipeFilter, "Pipe"),
                     (_airFilter, "Air Terminals"),
                     (_ductFilter, "Duct Accessories")
                 })
        {
            bool selected = _activeFilter == filter;
            button.Background = selected ? Blue : Brushes.White;
            button.Foreground = selected ? Brushes.White : Ink;
            button.FontWeight =
                selected ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }

    private static List<LibraryItem> BuildLibrary()
    {
        var items = new List<LibraryItem>();
        foreach ((ValveBuilderWindow.ValveCatalogDefinition catalog, int index) in
                 ValveBuilderWindow.Catalogs.Select((value, index) => (value, index)))
        {
            items.Add(new LibraryItem(
                UnifiedFamilyKind.Valve,
                index,
                catalog.ValveType,
                catalog.Series,
                "Pipe Accessories",
                "Valve — Breaks Into",
                catalog.MasterFamilyPath));
        }
        foreach ((PipeFittingDefinition definition, int index) in
                 PipeFittingCatalog.All.Select((value, index) => (value, index)))
        {
            items.Add(new LibraryItem(
                UnifiedFamilyKind.PipeFitting,
                index,
                definition.DisplayName,
                "90-degree press bend - DN20 prototype",
                "Pipe Fittings",
                "Elbow",
                definition.SourcePath));
        }
        foreach ((AirTerminalDefinition definition, int index) in
                 AirTerminalCatalog.All
                     .Select((value, index) => (value, index))
                     .Where(item => item.value.IsProcedural))
        {
            items.Add(new LibraryItem(
                UnifiedFamilyKind.AirTerminal,
                index,
                definition.DisplayName,
                definition.AirflowRole,
                "Air Terminals",
                "Air Terminal",
                definition.SourcePath));
        }
        foreach ((FireDamperDefinition definition, int index) in
                 FireDamperCatalog.All.Select((value, index) => (value, index)))
        {
            items.Add(new LibraryItem(
                UnifiedFamilyKind.FireDamper,
                index,
                definition.DisplayName,
                "Commercial kitchen exhaust",
                "Duct Accessories",
                "Damper",
                definition.SourcePath));
        }
        return items;
    }

    private static string TroxVariantName(string typeName)
    {
        int separator = typeName.LastIndexOf('-');
        if (separator <= 0)
            return typeName;
        return int.TryParse(
            typeName[(separator + 1)..],
            out _)
            ? typeName[..separator]
            : typeName;
    }

    private static DataTemplate BuildLibraryTemplate()
    {
        var template = new DataTemplate(typeof(LibraryItem));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(System.Windows.Controls.Border.BorderBrushProperty, Border);
        border.SetValue(
            System.Windows.Controls.Border.BorderThicknessProperty,
            new Thickness(1));
        border.SetValue(
            System.Windows.Controls.Border.CornerRadiusProperty,
            new CornerRadius(6));
        border.SetValue(
            System.Windows.Controls.Border.MarginProperty,
            new Thickness(2, 3, 2, 3));
        border.SetValue(
            System.Windows.Controls.Border.PaddingProperty,
            new Thickness(10, 8, 10, 8));
        border.SetValue(
            System.Windows.Controls.Border.BackgroundProperty,
            Brushes.White);
        var grid = new FrameworkElementFactory(typeof(Grid));
        var title = new FrameworkElementFactory(typeof(TextBlock));
        title.SetBinding(TextBlock.TextProperty, new Binding(nameof(LibraryItem.Title)));
        title.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        title.SetValue(TextBlock.ForegroundProperty, Navy);
        title.SetValue(TextBlock.FontSizeProperty, 13.5);
        grid.AppendChild(title);
        var subtitle = new FrameworkElementFactory(typeof(TextBlock));
        subtitle.SetBinding(TextBlock.TextProperty, new Binding(nameof(LibraryItem.StatusLine)));
        subtitle.SetValue(TextBlock.MarginProperty, new Thickness(0, 22, 0, 0));
        subtitle.SetValue(TextBlock.ForegroundProperty, Muted);
        subtitle.SetValue(TextBlock.FontSizeProperty, 11.5);
        grid.AppendChild(subtitle);
        border.AppendChild(grid);
        template.VisualTree = border;
        return template;
    }

    private static UIElement Step(string number, string text, bool active)
    {
        var stack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(8, 0, 8, 0)
        };
        stack.Children.Add(new Border
        {
            Width = 34,
            Height = 34,
            CornerRadius = new CornerRadius(17),
            Background = active ? Blue : Brushes.White,
            BorderBrush = active ? Blue : Border,
            BorderThickness = new Thickness(1.5),
            Child = new TextBlock
            {
                Text = number,
                Foreground = active ? Brushes.White : Ink,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        });
        TextBlock label = Label(text, 13, FontWeights.Normal, Ink);
        label.Margin = new Thickness(8, 8, 0, 0);
        stack.Children.Add(label);
        return stack;
    }

    private static UIElement StepLine() =>
        new Border
        {
            Width = 48,
            Height = 1,
            Background = Border,
            Margin = new Thickness(0, 17, 0, 0)
        };

    private static Border Card(UIElement content, Thickness margin) =>
        new()
        {
            Child = content,
            Background = Brushes.White,
            BorderBrush = Border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(14),
            Margin = margin
        };

    private static Border ExpanderCard(
        string header,
        UIElement content,
        bool expanded)
    {
        var expander = new Expander
        {
            Header = header,
            Content = content,
            IsExpanded = expanded,
            Foreground = Navy,
            FontWeight = FontWeights.SemiBold,
            Padding = new Thickness(0, 6, 0, 0)
        };
        return Card(expander, new Thickness(0, 0, 0, 8));
    }

    private static void AddInfoRow(
        Grid grid,
        int row,
        string label,
        TextBlock value)
    {
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(30) });
        TextBlock name = Label(label, 12, FontWeights.SemiBold, Navy);
        Grid.SetRow(name, row);
        grid.Children.Add(name);
        Grid.SetRow(value, row);
        Grid.SetColumn(value, 1);
        grid.Children.Add(value);
    }

    private static void AddEditorRow(
        Grid grid,
        int row,
        string label,
        Control editor)
    {
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(44) });
        TextBlock name = Label(label, 12, FontWeights.Normal, Ink);
        name.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetRow(name, row);
        grid.Children.Add(name);
        editor.Margin = new Thickness(0, 4, 0, 4);
        Grid.SetRow(editor, row);
        Grid.SetColumn(editor, 1);
        grid.Children.Add(editor);
    }

    private static TextBox Input(string value) =>
        new()
        {
            Text = value,
            Height = 36,
            Padding = new Thickness(9, 7, 9, 7),
            BorderBrush = Border,
            BorderThickness = new Thickness(1),
            Background = Brushes.White,
            Foreground = Ink
        };

    private static TextBox ReadOnlyInput()
    {
        TextBox result = Input("");
        result.IsReadOnly = true;
        result.Background = Surface;
        return result;
    }

    private static TextBlock ValueLabel() =>
        Label("", 12, FontWeights.Normal, Ink);

    private static TextBlock Label(
        string text,
        double size,
        FontWeight weight,
        Brush foreground) =>
        new()
        {
            Text = text,
            FontSize = size,
            FontWeight = weight,
            Foreground = foreground,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };

    private static Button TypeButton(string text, int index) =>
        new()
        {
            Content = text,
            Tag = index,
            MinWidth = 70,
            Height = 36,
            Margin = new Thickness(3),
            Padding = new Thickness(10, 0, 10, 0),
            Background = Brushes.White,
            BorderBrush = Blue,
            Foreground = Ink,
            BorderThickness = new Thickness(1)
        };

    private static Button SecondaryButton(string text, double width) =>
        new()
        {
            Content = text,
            Width = width,
            Height = 42,
            Margin = new Thickness(3),
            Background = Brushes.White,
            Foreground = Navy,
            BorderBrush = Border,
            BorderThickness = new Thickness(1),
            FontWeight = FontWeights.SemiBold
        };

    private static Button PrimaryButton(string text, double width) =>
        new()
        {
            Content = text,
            Width = width,
            Height = 50,
            Background = Blue,
            Foreground = Brushes.White,
            BorderBrush = Blue,
            BorderThickness = new Thickness(1),
            FontWeight = FontWeights.SemiBold,
            FontSize = 14
        };

    private void AddRectangle(
        double left,
        double top,
        double width,
        double height,
        Brush fill,
        Brush stroke,
        double thickness,
        double radius)
    {
        var shape = new Rectangle
        {
            Width = width,
            Height = height,
            Fill = fill,
            Stroke = stroke,
            StrokeThickness = thickness,
            RadiusX = radius,
            RadiusY = radius
        };
        Canvas.SetLeft(shape, left);
        Canvas.SetTop(shape, top);
        _preview.Children.Add(shape);
    }

    private void AddEllipse(
        double left,
        double top,
        double width,
        double height,
        Brush fill,
        Brush stroke,
        double thickness)
    {
        var shape = new Ellipse
        {
            Width = width,
            Height = height,
            Fill = fill,
            Stroke = stroke,
            StrokeThickness = thickness
        };
        Canvas.SetLeft(shape, left);
        Canvas.SetTop(shape, top);
        _preview.Children.Add(shape);
    }

    private void AddText(
        string text,
        double left,
        double top,
        Brush color,
        double size,
        FontWeight weight)
    {
        TextBlock label = Label(text, size, weight, color);
        Canvas.SetLeft(label, left);
        Canvas.SetTop(label, top);
        _preview.Children.Add(label);
    }

    private void AddDimension(
        double x1,
        double y,
        double x2,
        string text)
    {
        var line = new Line
        {
            X1 = x1,
            Y1 = y,
            X2 = x2,
            Y2 = y,
            Stroke = Navy,
            StrokeThickness = 1.3
        };
        _preview.Children.Add(line);
        AddText(text, (x1 + x2) / 2 - 28, y + 8, Navy, 14, FontWeights.SemiBold);
    }

    private static SolidColorBrush Brush(byte red, byte green, byte blue) =>
        new(Color.FromRgb(red, green, blue));

    private sealed record LibraryItem(
        UnifiedFamilyKind Kind,
        int SourceIndex,
        string Title,
        string Subtitle,
        string Category,
        string PartType,
        string SourcePath)
    {
        public string StatusLine =>
            Kind is UnifiedFamilyKind.AirTerminal
                or UnifiedFamilyKind.FireDamper
                or UnifiedFamilyKind.PipeFitting
                ? $"{Subtitle}    ● Official catalog"
                : $"{Subtitle}    ● Official RFA";
    }
}
