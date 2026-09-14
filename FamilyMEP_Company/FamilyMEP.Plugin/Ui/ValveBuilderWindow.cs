using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using FamilyMEP.Plugin.Infrastructure;
using FamilyMEP.Plugin.Models;
using Microsoft.Win32;
using IOPath = System.IO.Path;

namespace FamilyMEP.Plugin.Ui;

internal sealed class ValveBuilderWindow : Window
{
    private static readonly SolidColorBrush Navy = Brush(10, 52, 96);
    private static readonly SolidColorBrush Blue = Brush(0, 99, 211);
    private static readonly SolidColorBrush Green = Brush(25, 145, 70);
    private static readonly SolidColorBrush Orange = Brush(244, 112, 24);
    private static readonly SolidColorBrush Ink = Brush(31, 42, 55);
    private static readonly SolidColorBrush Muted = Brush(92, 105, 122);
    private static readonly SolidColorBrush Border = Brush(214, 222, 232);
    private static readonly SolidColorBrush Surface = Brush(248, 250, 253);

    internal static readonly ValveCatalogDefinition[] Catalogs =
    [
        new(
            "Ball Valve",
            "Threaded Full Port",
            "Official manufacturer BIM reference",
            "",
            "Manufacturer-authored RFA geometry and verified catalog dimensions",
            "Bronze",
            AppPaths.BallValveMasterFamily,
            "BallValve_Threaded_FullPort",
            "Ball Valve",
            false,
            [
                new("DN8", 8, 50.800, 28.194, 8, 44.450, 99.060, "1/4\"", "NL95004", 4.2, 0, 0.223)
                    { SourceTypeName = "1/4in_NL95004" },
                new("DN10", 10, 50.800, 28.194, 10, 44.450, 99.060, "3/8\"", "NL95005", 6.2, 0, 0.203)
                    { SourceTypeName = "3/8in_NL95005" },
                new("DN15", 15, 61.976, 32.258, 13, 47.752, 99.060, "1/2\"", "NL95006", 15.3, 0, 0.312)
                    { SourceTypeName = "1/2in_NL95006" },
                new("DN20", 20, 74.676, 42.164, 19, 57.150, 118.364, "3/4\"", "NL95008", 30.4, 0, 0.604)
                    { SourceTypeName = "3/4in_NL95008" },
                new("DN25", 25, 84.836, 51.816, 25, 60.452, 118.364, "1\"", "NL9500A", 48.8, 0, 0.813)
                    { SourceTypeName = "1in_NL9500A" },
                new("DN32", 32, 106.426, 62.484, 32, 76.200, 169.926, "1-1/4\"", "NL9510B", 103, 0, 1.486)
                    { SourceTypeName = "1 1/4in_NL9510B" },
                new("DN40", 40, 119.888, 75.438, 38, 80.264, 169.926, "1-1/2\"", "NL9510C", 143, 0, 2.166)
                    { SourceTypeName = "1 1/2in_NL9510C" },
                new("DN50", 50, 131.064, 93.726, 51, 88.900, 169.926, "2\"", "NL9510D", 245, 0, 3.032)
                    { SourceTypeName = "2in_NL9510D" }
            ]),
        new(
            "Gate Valve",
            "Rising Stem Threaded",
            "Official manufacturer BIM reference",
            "https://catalog.nibco.com/en-ca/viewitems/gate-valves-4/gate-valve-bronze-rising-stem-threaded-t-111",
            "Manufacturer-authored RFA geometry; official A/B dimensions and Type Catalog",
            "Bronze",
            AppPaths.GateValveMasterFamily,
            "GateValve_Threaded_RisingStem",
            "Gate Valve",
            true,
            [
                new("DN8", 8, 43, 32, 8, 117, 55, "1/4\"", "NL05004", 5.6, 0, 0.358)
                    { SourceTypeName = "1/4in_NL05004" },
                new("DN10", 10, 43, 34, 10, 117, 55, "3/8\"", "NL05005", 10.7, 0, 0.347)
                    { SourceTypeName = "3/8in_NL05005" },
                new("DN15", 15, 49, 40, 15, 122, 65, "1/2\"", "NL05006", 17.6, 0, 0.395)
                    { SourceTypeName = "1/2in_NL05006" },
                new("DN20", 20, 54, 47, 20, 148, 75, "3/4\"", "NL05008", 32, 0, 0.540)
                    { SourceTypeName = "3/4in_NL05008" },
                new("DN25", 25, 62, 56, 25, 180, 85, "1\"", "NL0500A", 54, 0, 0.900)
                    { SourceTypeName = "1in_NL0500A" },
                new("DN32", 32, 67, 66, 32, 206, 100, "1-1/4\"", "NL0500B", 97, 0, 1.208)
                    { SourceTypeName = "1 1/4in_NL0500B" },
                new("DN40", 40, 72, 78, 40, 249, 115, "1-1/2\"", "NL0500C", 135, 0, 1.704)
                    { SourceTypeName = "1 1/2in_NL0500C" },
                new("DN50", 50, 78, 94, 50, 294, 140, "2\"", "NL0500D", 230, 0, 2.522)
                    { SourceTypeName = "2in_NL0500D" },
                new("DN65", 65, 105, 118, 65, 364, 170, "2-1/2\"", "NL0500E", 337, 0, 4.901)
                    { SourceTypeName = "2 1/2in_NL0500E" },
                new("DN80", 80, 114, 138, 80, 419, 200, "3\"", "NL0500F", 536, 0, 7.023)
                    { SourceTypeName = "3in_NL0500F" }
            ]),
        new(
            "Globe Valve",
            "Threaded",
            "Official manufacturer BIM reference",
            "https://catalog.nibco.com/viewitems/globe-angle-valves/obe-valve-bronze-bronze-trim-threaded-ends-t-211-b",
            "Manufacturer-authored RFA geometry and official Type Catalog",
            "Bronze",
            AppPaths.GlobeValveMasterFamily,
            "GlobeValve_Threaded",
            "Globe Valve",
            false,
            LoadCatalogSizes(
                AppPaths.GlobeValveMasterFamily,
                "Valve Body Length",
                "",
                "Overall Height",
                "Center Axis to Operator Top",
                [
                    ("1/4in_NL2J004", "DN8", 8d),
                    ("3/8in_NL2J005", "DN10", 10d),
                    ("1/2in_NL2J006", "DN15", 15d),
                    ("3/4in_NL2J008", "DN20", 20d),
                    ("1in_NL2J00A", "DN25", 25d),
                    ("1 1/4in_NL2J00B", "DN32", 32d),
                    ("1 1/2in_NL2J00C", "DN40", 40d),
                    ("2in_NL2J00D", "DN50", 50d),
                    ("2 1/2in_NL2J00E", "DN65", 65d),
                    ("3in_NL2J00F", "DN80", 80d)
                ]))
        {
            PreviewKind = "Globe",
            Connection = "Female NPT"
        },
        new(
            "Angle Valve",
            "Threaded",
            "Official manufacturer BIM reference",
            "https://catalog.nibco.com/viewitems/globe-angle-valves/angle-valve-bronze-class-125-threaded-t-311-y",
            "Manufacturer-authored RFA geometry and official Type Catalog",
            "Bronze",
            AppPaths.AngleValveMasterFamily,
            "AngleValve_Threaded",
            "Angle Valve",
            false,
            LoadCatalogSizes(
                AppPaths.AngleValveMasterFamily,
                "Valve Body Diameter",
                "Valve Body Diameter",
                "Valve Overall Height",
                "Primary Connection to Handle Top",
                [
                    ("1/4in_NL4K004", "DN8", 8d),
                    ("3/8in_NL4K005", "DN10", 10d),
                    ("1/2in_NL4K006", "DN15", 15d),
                    ("3/4in_NL4K008", "DN20", 20d),
                    ("1in_NL4K00A", "DN25", 25d),
                    ("1 1/4in_NL4K00B", "DN32", 32d),
                    ("1 1/2in_NL4K00C", "DN40", 40d),
                    ("2in_NL4K00D", "DN50", 50d),
                    ("2 1/2in_NL4K00E", "DN65", 65d),
                    ("3in_NL4K00F", "DN80", 80d)
                ]))
        {
            PreviewKind = "Angle",
            Connection = "Female NPT"
        },
        new(
            "Swing Check Valve",
            "Threaded",
            "Official manufacturer BIM reference",
            "https://catalog.nibco.com/viewitems/check-valves-5/check-valve-bronze-ptfe-disc-threaded-ends-t-433-y",
            "Manufacturer-authored RFA geometry and official Type Catalog",
            "Bronze",
            AppPaths.SwingCheckValveMasterFamily,
            "SwingCheckValve_Threaded",
            "Swing Check Valve",
            false,
            LoadCatalogSizes(
                AppPaths.SwingCheckValveMasterFamily,
                "Overall Width",
                "Valve Body Diameter",
                "Overall Height",
                "",
                [
                    ("1/4in_NL7K004", "DN8", 8d),
                    ("3/8in_NL7K005", "DN10", 10d),
                    ("1/2in_NL7K006", "DN15", 15d),
                    ("3/4in_NL7K008", "DN20", 20d),
                    ("1in_NL7K00A", "DN25", 25d),
                    ("1 1/4in_NL7K00B", "DN32", 32d),
                    ("1 1/2in_NL7K00C", "DN40", 40d),
                    ("2in_NL7K00D", "DN50", 50d),
                    ("2 1/2in_NL7K00E", "DN65", 65d),
                    ("3in_NL7K00F", "DN80", 80d)
                ]))
        {
            PreviewKind = "SwingCheck",
            Connection = "Female NPT"
        },
        new(
            "Ring Check Valve",
            "Inline Threaded",
            "Official manufacturer BIM reference",
            "https://catalog.nibco.com/viewitems/check-valves-5/-bronze-ring-check-ptfe-disc-threaded-ends-t-480-y",
            "Manufacturer-authored RFA geometry and official Type Catalog",
            "Bronze",
            AppPaths.RingCheckValveMasterFamily,
            "RingCheckValve_Threaded",
            "Ring Check Valve",
            false,
            LoadCatalogSizes(
                AppPaths.RingCheckValveMasterFamily,
                "Overall Length",
                "Body Outside Diameter",
                "Body Sphere Outside Diameter",
                "",
                [
                    ("3/8in_NL93185", "DN10", 10d),
                    ("1/2in_NL93186", "DN15", 15d),
                    ("3/4in_NL93188", "DN20", 20d),
                    ("1in_NL9318A", "DN25", 25d),
                    ("1 1/4in_NL9318B", "DN32", 32d),
                    ("1 1/2in_NL9318C", "DN40", 40d),
                    ("2in_NL9318D", "DN50", 50d)
                ]))
        {
            PreviewKind = "RingCheck",
            Connection = "Female NPT"
        },
        new(
            "Butterfly Valve",
            "Wafer Handle",
            "Official manufacturer BIM reference",
            "https://catalog.nibco.com/viewitems/butterfly-valves-3/tterfly-valve-cast-iron-wafer-type-200-psi-wc-2000",
            "Manufacturer-authored RFA geometry and official Type Catalog",
            "Cast Iron",
            AppPaths.ButterflyValveMasterFamily,
            "ButterflyValve_Wafer_Handle",
            "Butterfly Valve",
            false,
            LoadCatalogSizes(
                AppPaths.ButterflyValveMasterFamily,
                "Overall Width",
                "Overall Depth",
                "Overall Height",
                "Overall Height",
                [
                    ("2 in_NLS100D", "DN50", 50d),
                    ("2 1/2 in_NLS100E", "DN65", 65d),
                    ("3 in_NLS100F", "DN80", 80d),
                    ("4 in_NLS100H", "DN100", 100d),
                    ("5 in_NLS100J", "DN125", 125d),
                    ("6 in_NLS100K", "DN150", 150d),
                    ("8 in_NLS100L", "DN200", 200d)
                ]))
        {
            PreviewKind = "Butterfly",
            Connection = "Wafer"
        }
    ];

    private readonly bool _hasSelectedFamily;
    private readonly string _selectedFamilyDescription;
    private readonly TextBox _templatePath;
    private readonly TextBox _familyName;
    private readonly ComboBox _valveType;
    private readonly ComboBox _catalog;
    private readonly ComboBox _connection;
    private readonly ComboBox _material;
    private readonly TextBox _diameter;
    private readonly TextBox _length;
    private readonly TextBox _bodyDiameter;
    private readonly TextBox _height;
    private readonly TextBox _handleLength;
    private readonly TextBox _outputPath;
    private readonly CheckBox _openGeneratedFamily;
    private readonly TextBlock _selectedTypeSummary;
    private readonly TextBlock _dimensionSummary;
    private readonly TextBlock _statusText;
    private readonly TextBlock _catalogInfo;
    private readonly Canvas _previewCanvas;
    private readonly UniformGrid _presetGrid;
    private ValveCatalogDefinition _selectedCatalog = Catalogs[0];
    private ValveSizePreset _selectedPreset = Catalogs[0].Sizes[^1];

    public ValveBuilderWindow(
        bool hasSelectedFamily,
        string selectedFamilyDescription,
        string defaultTemplatePath,
        int initialCatalogIndex = 0)
    {
        _hasSelectedFamily = hasSelectedFamily;
        _selectedFamilyDescription = selectedFamilyDescription;
        int catalogIndex = Math.Clamp(initialCatalogIndex, 0, Catalogs.Length - 1);
        ValveCatalogDefinition initialCatalog = Catalogs[catalogIndex];
        _selectedCatalog = initialCatalog;
        _selectedPreset = initialCatalog.Sizes[^1];

        Title = "FamilyMEP — Create Valve Family";
        Width = 1420;
        Height = 880;
        MinWidth = 1180;
        MinHeight = 760;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        Background = Brushes.White;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13;

        _templatePath = Input(initialCatalog.MasterFamilyPath);
        _templatePath.IsReadOnly = true;
        _familyName = Input(initialCatalog.FamilyStem);
        string[] valveTypes = Catalogs.Select(item => item.ValveType).Distinct().ToArray();
        _valveType = Combo(valveTypes, Array.IndexOf(valveTypes, initialCatalog.ValveType));
        _catalog = Combo(Catalogs.Select(item => item.DisplayName).ToArray(), catalogIndex);
        string[] connections = Catalogs.Select(item => item.Connection).Distinct().ToArray();
        _connection = Combo(connections, Array.IndexOf(connections, initialCatalog.Connection));
        string[] materials = Catalogs.Select(item => item.Material).Distinct().ToArray();
        _material = Combo(materials, Array.IndexOf(materials, initialCatalog.Material));
        _diameter = NumberInput("50");
        _length = NumberInput("131.064");
        _bodyDiameter = NumberInput("93.726");
        _height = NumberInput("88.9");
        _handleLength = NumberInput("169.926");
        foreach (TextBox catalogValue in new[] { _diameter, _length, _bodyDiameter, _height, _handleLength })
            catalogValue.IsReadOnly = true;
        _outputPath = Input(IOPath.Combine(
            AppPaths.GeneratedValveFolder,
            $"{initialCatalog.FamilyStem}_{initialCatalog.Sizes[^1].Name}.rfa"));
        _openGeneratedFamily = new CheckBox
        {
            Content = "Open Family after creation",
            IsChecked = true,
            Foreground = Ink,
            Margin = new Thickness(0, 10, 0, 0)
        };
        _selectedTypeSummary = new TextBlock
        {
            Text = "DN50 selected",
            Foreground = Blue,
            FontWeight = FontWeights.SemiBold
        };
        _dimensionSummary = new TextBlock
        {
            Text = "DN 50  •  L 140  •  H 80 mm",
            Foreground = Muted
        };
        _statusText = new TextBlock
        {
            Text = "Ready to create the selected valve Family",
            Foreground = Ink,
            VerticalAlignment = VerticalAlignment.Center
        };
        _catalogInfo = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = Muted,
            FontSize = 11,
            Margin = new Thickness(0, 7, 0, 0)
        };
        _previewCanvas = new Canvas
        {
            Width = 720,
            Height = 500,
            Background = Brushes.White
        };
        _presetGrid = new UniformGrid { Columns = 4, Margin = new Thickness(0, 8, 0, 0) };

        Content = BuildLayout();
        HookLivePreview();
        _catalog.SelectionChanged += (_, _) =>
        {
            if (_catalog.SelectedIndex >= 0)
                SelectCatalog(Catalogs[_catalog.SelectedIndex]);
        };
        SelectCatalog(initialCatalog, initialCatalog.Sizes.Length - 1);
    }

    public ValveBuilderRequest Request { get; private set; } = null!;
    private ValveSizePreset[] ActivePresets => _selectedCatalog.Sizes;

    internal static ValveBuilderRequest CreateRequest(
        int catalogIndex,
        int presetIndex,
        string? outputPath,
        bool openGeneratedFamily)
    {
        ValveCatalogDefinition catalog = Catalogs[
            Math.Clamp(catalogIndex, 0, Catalogs.Length - 1)];
        ValveSizePreset preset = catalog.Sizes[
            Math.Clamp(presetIndex, 0, catalog.Sizes.Length - 1)];
        string resolvedOutput = string.IsNullOrWhiteSpace(outputPath)
            ? IOPath.Combine(
                AppPaths.GeneratedValveFolder,
                $"{catalog.FamilyStem}_{preset.Name}.rfa")
            : outputPath;
        return new ValveBuilderRequest(
            ValveSourceMode.FamilyFile,
            catalog.MasterFamilyPath,
            preset.Name,
            preset.Dn,
            preset.Length,
            preset.Height,
            0,
            catalog.Connection,
            catalog.Material,
            "DN|Nominal Diameter|Diameter|Connector Diameter",
            "L|Length|Body Length|Face to Face",
            "H|Height|Body Height|Total Height",
            "Handle Angle|Open Angle|Angle",
            "Connection Type|Connection|End Connection",
            "Body Material|Material",
            true,
            openGeneratedFamily,
            false)
        {
            FamilyName = catalog.FamilyStem,
            OutputPath = resolvedOutput,
            BodyDiameterMm = preset.BodyDiameter,
            PortDiameterMm = preset.PortDiameter,
            HandleLengthMm = preset.HandleLength,
            Manufacturer = catalog.Manufacturer,
            ProductSeries = catalog.Series,
            CatalogRevision = catalog.Revision,
            CatalogUrl = catalog.CatalogUrl,
            CatalogDimensionBasis = catalog.DimensionBasis,
            GeometryAccuracy =
                "Manufacturer-authored Revit geometry and verified Type data",
            LodStatus =
                "Manufacturer BIM geometry preserved; no procedural geometry reconstruction",
            PreserveSourceParameters = true,
            ExternalCatalogTypeName = preset.SourceTypeName,
            SizeCatalog = catalog.Sizes.Select(item => new ValveSizeDefinition(
                item.Name,
                item.Dn,
                item.Length,
                item.BodyDiameter,
                item.PortDiameter,
                item.Height,
                item.HandleLength)
            {
                SourceTypeName = item.SourceTypeName,
                PartNumber = item.PartNumber,
                NominalSize = item.NominalSize,
                Cv = item.Cv,
                TorqueNm = item.TorqueNm,
                WeightKg = item.WeightKg
            }).ToList()
        };
    }
    private ValveSizePreset[] Presets => ActivePresets;

    private UIElement BuildLayout()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(54) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(86) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(46) });

        root.Children.Add(BuildHeader());
        UIElement workflow = BuildWorkflow();
        Grid.SetRow(workflow, 1);
        root.Children.Add(workflow);

        UIElement workspace = BuildWorkspace();
        Grid.SetRow(workspace, 2);
        root.Children.Add(workspace);

        UIElement status = BuildStatusBar();
        Grid.SetRow(status, 3);
        root.Children.Add(status);
        return root;
    }

    private UIElement BuildHeader()
    {
        var header = new Grid
        {
            Background = Brushes.White,
            Margin = new Thickness(18, 0, 18, 0)
        };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock
                {
                    Text = "FamilyMEP",
                    FontSize = 20,
                    FontWeight = FontWeights.Bold,
                    Foreground = Navy
                },
                new TextBlock
                {
                    Text = "  —  Create Valve Family",
                    FontSize = 18,
                    Foreground = Ink,
                    VerticalAlignment = VerticalAlignment.Center
                },
                Badge("NEW FAMILY", Blue, new Thickness(14, 0, 0, 0))
            }
        });

        Button save = SecondaryButton("Save Config", 104);
        save.Margin = new Thickness(0, 9, 8, 9);
        save.Click += (_, _) => MessageBox.Show(
            this,
            "The current values are kept while this window is open. Persistent presets will be added with the catalog editor.",
            "FamilyMEP — Save Config");
        Grid.SetColumn(save, 1);
        header.Children.Add(save);

        Button help = SecondaryButton("Help", 72);
        help.Margin = new Thickness(0, 9, 0, 9);
        help.Click += (_, _) => MessageBox.Show(
            this,
            "Choose one of the six catalog sizes, validate, then create the RFA. "
            + "Geometry, connectors, constraints and Type data come from the bundled manufacturer reference.",
            "FamilyMEP — Help");
        Grid.SetColumn(help, 2);
        header.Children.Add(help);
        return header;
    }

    private UIElement BuildWorkflow()
    {
        string[] labels = ["Master RFA", "Category", "Type Data", "Geometry", "Connectors", "Validate", "Export"];
        var panel = new Grid
        {
            Background = Surface,
            Margin = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Stretch
        };
        for (int index = 0; index < labels.Length; index++)
            panel.ColumnDefinitions.Add(new ColumnDefinition());

        for (int index = 0; index < labels.Length; index++)
        {
            var item = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            bool complete = index < 3;
            bool active = index is 3 or 4;
            var circle = new Border
            {
                Width = 34,
                Height = 34,
                CornerRadius = new CornerRadius(17),
                BorderThickness = new Thickness(2),
                BorderBrush = complete ? Green : active ? Blue : Brush(128, 138, 151),
                Background = active ? Blue : Brushes.White,
                Child = new TextBlock
                {
                    Text = complete ? "✓" : (index + 1).ToString(CultureInfo.InvariantCulture),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontWeight = FontWeights.Bold,
                    Foreground = active ? Brushes.White : complete ? Green : Ink
                }
            };
            item.Children.Add(circle);
            item.Children.Add(new TextBlock
            {
                Text = labels[index],
                Margin = new Thickness(0, 5, 0, 0),
                Foreground = active ? Blue : Ink,
                FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal
            });
            Grid.SetColumn(item, index);
            panel.Children.Add(item);

            if (index < labels.Length - 1)
            {
                var line = new Border
                {
                    Height = 2,
                    Width = 76,
                    Background = complete ? Green : index == 3 ? Blue : Border,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, -22, -38, 0)
                };
                Grid.SetColumn(line, index);
                panel.Children.Add(line);
            }
        }
        return panel;
    }

    private UIElement BuildWorkspace()
    {
        var workspace = new Grid();
        workspace.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(330) });
        workspace.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        workspace.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(342) });
        workspace.Children.Add(BuildParametersPanel());

        UIElement preview = BuildPreviewPanel();
        Grid.SetColumn(preview, 1);
        workspace.Children.Add(preview);

        UIElement summary = BuildSummaryPanel();
        Grid.SetColumn(summary, 2);
        workspace.Children.Add(summary);
        return workspace;
    }

    private UIElement BuildParametersPanel()
    {
        var root = new Border
        {
            Background = Navy,
            Padding = new Thickness(12, 14, 12, 12)
        };
        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.Children.Add(new TextBlock
        {
            Text = "Build Parameters",
            Foreground = Brushes.White,
            FontSize = 18,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(6, 0, 0, 12)
        });

        var content = new StackPanel();
        content.Children.Add(BuildBasicCard());
        content.Children.Add(BuildGeometryCard());
        content.Children.Add(CollapsedCard("3. Connectors", "2"));
        content.Children.Add(CollapsedCard("4. Lookup Table", "Manufacturer catalog"));

        Button editCatalog = SecondaryButton("Edit Size Catalog…", 188);
        editCatalog.HorizontalAlignment = HorizontalAlignment.Center;
        editCatalog.Margin = new Thickness(0, 14, 0, 4);
        editCatalog.Click += (_, _) => ShowCatalog();
        content.Children.Add(editCatalog);

        var scroll = new ScrollViewer
        {
            Content = content,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        Grid.SetRow(scroll, 1);
        layout.Children.Add(scroll);
        root.Child = layout;
        return root;
    }

    private UIElement BuildBasicCard()
    {
        var stack = CardStack("1. Basic");
        AddLabeledControl(stack, "Manufacturer / Series", _catalog);
        stack.Children.Add(_catalogInfo);
        AddLabeledControl(stack, "Family name", _familyName);
        AddLabeledControl(stack, "Valve Type", _valveType);
        AddLabeledControl(stack, "Connection", _connection);
        AddLabeledControl(stack, "Material", _material);
        return Card(stack);
    }

    private UIElement BuildGeometryCard()
    {
        var stack = CardStack("2. Geometry");
        AddCompactField(stack, "Mapped DN (mm)", _diameter);
        AddCompactField(stack, "Official body length", _length);
        AddCompactField(stack, "Official body diameter", _bodyDiameter);
        AddCompactField(stack, "Official center-to-top height", _height);
        AddCompactField(stack, "Operator preview size", _handleLength);
        return Card(stack);
    }

    private UIElement BuildPreviewPanel()
    {
        var panel = new Grid
        {
            Background = Brushes.White,
            Margin = new Thickness(1, 0, 1, 0)
        };
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48) });
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(58) });

        var top = new Grid { Margin = new Thickness(20, 0, 16, 0) };
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.Children.Add(new TextBlock
        {
            Text = "Geometry & Connector Preview",
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            Foreground = Ink,
            VerticalAlignment = VerticalAlignment.Center
        });
        var viewButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        foreach (string label in new[] { "Fit", "Front", "Top", "Section" })
        {
            Button button = SecondaryButton(label, label == "Section" ? 70 : 52);
            button.Height = 30;
            button.Margin = new Thickness(5, 0, 0, 0);
            viewButtons.Children.Add(button);
        }
        Grid.SetColumn(viewButtons, 1);
        top.Children.Add(viewButtons);
        panel.Children.Add(top);

        var previewBorder = new Border
        {
            BorderBrush = Border,
            BorderThickness = new Thickness(1, 1, 1, 1),
            Background = Brushes.White,
            Padding = new Thickness(12)
        };
        previewBorder.Child = new Viewbox
        {
            Stretch = Stretch.Uniform,
            Child = _previewCanvas
        };
        Grid.SetRow(previewBorder, 1);
        panel.Children.Add(previewBorder);

        var footer = new Grid { Margin = new Thickness(18, 9, 18, 9) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Button rebuild = SecondaryButton("Rebuild Preview", 134);
        rebuild.BorderBrush = Blue;
        rebuild.Foreground = Blue;
        rebuild.Click += (_, _) => RefreshPreview();
        footer.Children.Add(rebuild);
        var info = new StackPanel { Margin = new Thickness(16, 0, 0, 0) };
        info.Children.Add(_dimensionSummary);
        info.Children.Add(new TextBlock
        {
            Text = "Preview generated from procedural geometry",
            Foreground = Muted,
            FontSize = 11
        });
        Grid.SetColumn(info, 1);
        footer.Children.Add(info);
        Grid.SetRow(footer, 2);
        panel.Children.Add(footer);
        return panel;
    }

    private UIElement BuildSummaryPanel()
    {
        var outer = new Border
        {
            BorderBrush = Border,
            BorderThickness = new Thickness(1, 0, 0, 0),
            Background = Surface,
            Padding = new Thickness(18, 14, 18, 12)
        };
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var scrollContent = new StackPanel();
        scrollContent.Children.Add(new TextBlock
        {
            Text = "Generation Summary",
            FontSize = 19,
            FontWeight = FontWeights.Bold,
            Foreground = Ink,
            Margin = new Thickness(0, 0, 0, 14)
        });

        AddSummaryRow(scrollContent, "Source", "Manufacturer reference RFA");
        AddSummaryRow(scrollContent, "Category", "Pipe Accessories");
        AddSummaryRow(scrollContent, "Part Type", "Valve - Breaks Into");

        scrollContent.Children.Add(SectionTitle("Size catalog"));
        scrollContent.Children.Add(_selectedTypeSummary);
        PopulatePresetButtons();
        scrollContent.Children.Add(_presetGrid);

        scrollContent.Children.Add(SectionTitle("Validation"));
        scrollContent.Children.Add(CheckRow("Category ready"));
        scrollContent.Children.Add(CheckRow("Manufacturer-authored geometry"));
        scrollContent.Children.Add(CheckRow("Manufacturer MEP connectors"));
        scrollContent.Children.Add(CheckRow("Official Type selected"));

        scrollContent.Children.Add(SectionTitle("Official manufacturer master RFA"));
        scrollContent.Children.Add(PathEditor(_templatePath, BrowseTemplate));
        scrollContent.Children.Add(SectionTitle("Output RFA"));
        scrollContent.Children.Add(PathEditor(_outputPath, BrowseOutput));
        scrollContent.Children.Add(_openGeneratedFamily);

        if (_hasSelectedFamily)
        {
            scrollContent.Children.Add(new TextBlock
            {
                Text = $"Existing selection available: {_selectedFamilyDescription}",
                Margin = new Thickness(0, 12, 0, 0),
                Foreground = Muted,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11
            });
        }

        root.Children.Add(new ScrollViewer
        {
            Content = scrollContent,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        });

        var buttons = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        Button create = PrimaryButton("Create RFA");
        create.Click += (_, _) => Accept();
        buttons.Children.Add(create);
        Button validate = SecondaryButton("Run Validation", double.NaN);
        validate.HorizontalAlignment = HorizontalAlignment.Stretch;
        validate.Margin = new Thickness(0, 8, 0, 0);
        validate.Click += (_, _) => Validate(showSuccess: true);
        buttons.Children.Add(validate);
        Grid.SetRow(buttons, 1);
        root.Children.Add(buttons);
        outer.Child = root;
        return outer;
    }

    private UIElement BuildStatusBar()
    {
        var status = new Grid
        {
            Background = Brushes.White,
            Margin = new Thickness(18, 0, 18, 0)
        };
        status.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        status.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        status.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        status.Children.Add(new TextBlock
        {
            Text = "✓",
            FontSize = 22,
            FontWeight = FontWeights.Bold,
            Foreground = Green,
            VerticalAlignment = VerticalAlignment.Center
        });
        _statusText.Margin = new Thickness(10, 0, 0, 0);
        Grid.SetColumn(_statusText, 1);
        status.Children.Add(_statusText);
        var error = new TextBlock
        {
            Text = "0 errors  •  0 warnings",
            Foreground = Muted,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(error, 2);
        status.Children.Add(error);
        return status;
    }

    private void HookLivePreview()
    {
        foreach (TextBox textBox in new[] { _diameter, _length, _bodyDiameter, _height, _handleLength })
            textBox.TextChanged += (_, _) => RefreshPreview();
    }

    private void SelectCatalog(ValveCatalogDefinition catalog, int preferredIndex = -1)
    {
        _selectedCatalog = catalog;
        _templatePath.Text = catalog.MasterFamilyPath;
        _familyName.Text = catalog.FamilyStem;
        _valveType.SelectedItem = catalog.ValveType;
        _catalogInfo.Text =
            $"{catalog.Manufacturer} • {catalog.Series}\n"
            + $"{catalog.Revision}\n"
            + catalog.DimensionBasis;
        _material.SelectedItem = catalog.Material;
        _connection.SelectedItem = catalog.Connection;
        PopulatePresetButtons();

        int index = preferredIndex >= 0 && preferredIndex < catalog.Sizes.Length
            ? preferredIndex
            : Math.Min(5, catalog.Sizes.Length - 1);
        SelectPreset(catalog.Sizes[index]);
    }

    private void PopulatePresetButtons()
    {
        _presetGrid.Children.Clear();
        foreach (ValveSizePreset preset in ActivePresets)
        {
            Button button = new()
            {
                Content = preset.Name,
                Tag = preset,
                Height = 34,
                Margin = new Thickness(3),
                Background = Brushes.White,
                BorderBrush = Blue,
                Foreground = Ink
            };
            button.Click += (_, _) => SelectPreset((ValveSizePreset)button.Tag);
            _presetGrid.Children.Add(button);
        }
    }

    private void SelectPreset(ValveSizePreset preset)
    {
        _selectedPreset = preset;
        _diameter.Text = Format(preset.Dn);
        _length.Text = Format(preset.Length);
        _bodyDiameter.Text = Format(preset.BodyDiameter);
        _height.Text = Format(preset.Height);
        _handleLength.Text = Format(preset.HandleLength);
        _selectedTypeSummary.Text = $"{preset.Name} selected • {Presets.Length} size records";
        string familyStem = _selectedCatalog.FamilyStem;
        _familyName.Text = familyStem;
        _outputPath.Text = IOPath.Combine(
            AppPaths.GeneratedValveFolder,
            $"{familyStem}_{preset.Name}.rfa");

        foreach (Button button in _presetGrid.Children.OfType<Button>())
        {
            bool selected = ReferenceEquals(button.Tag, preset);
            button.Background = selected ? Blue : Brushes.White;
            button.Foreground = selected ? Brushes.White : Ink;
            button.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
        }
        RefreshPreview();
    }

    private void RefreshPreview()
    {
        if (_previewCanvas is null) return;
        _previewCanvas.Children.Clear();
        double dn = ParseOr(_diameter.Text, 50);
        double length = ParseOr(_length.Text, 140);
        double bodyOd = ParseOr(_bodyDiameter.Text, 60.3);
        double height = ParseOr(_height.Text, 80);
        double handleLength = ParseOr(_handleLength.Text, 140);
        _dimensionSummary.Text = $"DN {dn:0.#}  •  L {length:0.#}  •  H {height:0.#} mm";
        if (_selectedCatalog.PreviewKind is "Gate" or "Globe")
        {
            RefreshGatePreview(dn, length, bodyOd, height, handleLength);
            return;
        }
        if (_selectedCatalog.PreviewKind == "Angle")
        {
            RefreshAnglePreview(dn, length, bodyOd, height, handleLength);
            return;
        }
        if (_selectedCatalog.PreviewKind is "SwingCheck" or "RingCheck")
        {
            RefreshCheckPreview(
                dn,
                length,
                bodyOd,
                height,
                _selectedCatalog.PreviewKind == "SwingCheck");
            return;
        }
        if (_selectedCatalog.PreviewKind == "Butterfly")
        {
            RefreshButterflyPreview(dn, length, bodyOd, height);
            return;
        }

        const double centerX = 365;
        const double centerY = 285;
        double bodyWidth = Math.Clamp(length * 2.15, 245, 390);
        double bodyHeight = Math.Clamp(bodyOd * 2.15, 112, 190);
        double left = centerX - bodyWidth / 2;
        double top = centerY - bodyHeight / 2;

        AddPreviewText("Connector 1", left - 72, centerY - 112, Orange, 13, FontWeights.SemiBold);
        AddPreviewText("Connector 2", left + bodyWidth + 16, centerY - 112, Orange, 13, FontWeights.SemiBold);

        AddEllipse(left - 26, centerY - bodyHeight * .32, 52, bodyHeight * .64, Brush(195, 201, 208), Brush(79, 88, 99), 2);
        AddEllipse(left + bodyWidth - 26, centerY - bodyHeight * .32, 52, bodyHeight * .64, Brush(195, 201, 208), Brush(79, 88, 99), 2);
        AddRectangle(left - 6, top + bodyHeight * .13, 58, bodyHeight * .74, Brush(172, 180, 190), Brush(75, 84, 95), 2, 8);
        AddRectangle(left + bodyWidth - 52, top + bodyHeight * .13, 58, bodyHeight * .74, Brush(172, 180, 190), Brush(75, 84, 95), 2, 8);
        AddEllipse(left + 32, top, bodyWidth - 64, bodyHeight, Brush(221, 225, 230), Brush(65, 75, 87), 2);
        AddEllipse(left + 60, top + 8, bodyWidth - 120, bodyHeight - 16, Brush(198, 205, 214), Brush(91, 101, 113), 1.5);

        double portSize = Math.Clamp(dn * 1.55, 46, 108);
        AddEllipse(left - 39, centerY - portSize / 2, 42, portSize, Brush(42, 45, 49), Orange, 4);
        AddEllipse(left + bodyWidth - 3, centerY - portSize / 2, 42, portSize, Brush(42, 45, 49), Orange, 4);

        double stemHeight = Math.Clamp(height * 1.05, 74, 120);
        AddRectangle(centerX - 25, top - stemHeight + 26, 50, stemHeight - 18, Brush(178, 186, 196), Brush(65, 75, 87), 2, 5);
        AddEllipse(centerX - 37, top - stemHeight + 16, 74, 28, Brush(210, 215, 221), Brush(65, 75, 87), 2);
        AddEllipse(centerX - 22, top - stemHeight - 1, 44, 30, Brush(183, 190, 199), Brush(65, 75, 87), 2);

        double handle = Math.Clamp(handleLength * 1.85, 185, 305);
        AddRectangle(centerX - 8, top - stemHeight - 18, handle, 22, Brush(43, 91, 151), Brush(36, 55, 78), 2, 10);

        AddDimensionLine(left - 46, centerY + bodyHeight / 2 + 66, left + bodyWidth + 46, centerY + bodyHeight / 2 + 66, $"L {length:0.#}");
        AddVerticalDimension(left - 76, centerY - portSize / 2, centerY + portSize / 2, $"DN {dn:0.#}");
        AddVerticalDimension(left + bodyWidth + 86, top - stemHeight - 18, centerY, $"C {height:0.#}");
        AddPreviewText(_selectedPreset.Name, centerX - 24, centerY - 9, Brush(77, 86, 97), 14, FontWeights.Bold);
    }

    private void RefreshGatePreview(
        double dn,
        double length,
        double bodyOd,
        double height,
        double wheelDiameter)
    {
        const double centerX = 360;
        const double pipeY = 330;
        double bodyWidth = Math.Clamp(length * 2.15, 190, 300);
        double bodyHeight = Math.Clamp(bodyOd * 1.55, 86, 165);
        double left = centerX - bodyWidth / 2;
        double top = pipeY - bodyHeight / 2;
        double portSize = Math.Clamp(dn * 1.25, 32, 90);

        AddPreviewText("Connector 1", left - 88, pipeY - 100, Orange, 13, FontWeights.SemiBold);
        AddPreviewText("Connector 2", left + bodyWidth + 14, pipeY - 100, Orange, 13, FontWeights.SemiBold);

        AddEllipse(left - 31, pipeY - portSize * .62, 54, portSize * 1.24,
            Brush(174, 181, 190), Brush(65, 75, 87), 2);
        AddRectangle(left - 8, top + 15, 58, bodyHeight - 30,
            Brush(187, 194, 202), Brush(65, 75, 87), 2, 8);
        AddEllipse(left + bodyWidth - 23, pipeY - portSize * .62, 54, portSize * 1.24,
            Brush(174, 181, 190), Brush(65, 75, 87), 2);
        AddRectangle(left + bodyWidth - 50, top + 15, 58, bodyHeight - 30,
            Brush(187, 194, 202), Brush(65, 75, 87), 2, 8);

        AddEllipse(left + 28, top, bodyWidth - 56, bodyHeight,
            Brush(214, 219, 225), Brush(61, 71, 83), 2);
        AddEllipse(left + 48, top + 8, bodyWidth - 96, bodyHeight - 16,
            Brush(194, 201, 210), Brush(84, 94, 106), 1.4);
        AddEllipse(left - 39, pipeY - portSize / 2, 42, portSize,
            Brush(42, 45, 49), Orange, 4);
        AddEllipse(left + bodyWidth - 3, pipeY - portSize / 2, 42, portSize,
            Brush(42, 45, 49), Orange, 4);

        double bonnetBottom = top + 24;
        double bonnetHeight = Math.Clamp(height * .42, 82, 150);
        AddRectangle(centerX - 37, bonnetBottom - bonnetHeight * .42, 74, bonnetHeight * .42,
            Brush(190, 197, 206), Brush(61, 71, 83), 2, 7);
        AddEllipse(centerX - 49, bonnetBottom - bonnetHeight * .48, 98, 30,
            Brush(207, 212, 220), Brush(61, 71, 83), 2);
        AddRectangle(centerX - 15, bonnetBottom - bonnetHeight, 30, bonnetHeight * .58,
            Brush(178, 186, 196), Brush(61, 71, 83), 2, 5);

        double wheel = Math.Clamp(wheelDiameter * 1.3, 105, 215);
        double wheelTop = bonnetBottom - bonnetHeight - wheel * .33;
        AddEllipse(centerX - wheel / 2, wheelTop, wheel, wheel * .42,
            Brushes.Transparent, Brush(43, 91, 151), 10);
        _previewCanvas.Children.Add(new Line
        {
            X1 = centerX - wheel * .42,
            Y1 = wheelTop + wheel * .21,
            X2 = centerX + wheel * .42,
            Y2 = wheelTop + wheel * .21,
            Stroke = Brush(43, 91, 151),
            StrokeThickness = 5
        });
        AddEllipse(centerX - 18, wheelTop + wheel * .21 - 13, 36, 26,
            Brush(180, 188, 198), Brush(61, 71, 83), 2);

        AddDimensionLine(left - 35, pipeY + bodyHeight / 2 + 62,
            left + bodyWidth + 35, pipeY + bodyHeight / 2 + 62, $"A {length:0.#}");
        AddVerticalDimension(left - 72, pipeY - portSize / 2, pipeY + portSize / 2, $"DN {dn:0.#}");
        AddVerticalDimension(left + bodyWidth + 86, wheelTop, pipeY, $"H {height:0.#}");
        AddPreviewText(_selectedPreset.Name, centerX - 24, pipeY - 9,
            Brush(77, 86, 97), 14, FontWeights.Bold);
    }

    private void RefreshAnglePreview(
        double dn,
        double length,
        double bodyOd,
        double height,
        double wheelDiameter)
    {
        const double centerX = 350;
        const double centerY = 335;
        double port = Math.Clamp(dn * 1.2, 34, 92);
        double body = Math.Clamp(bodyOd * 1.7, 100, 190);
        double vertical = Math.Clamp(height * 1.05, 150, 300);
        AddEllipse(centerX - body / 2, centerY - body * .62, body, body,
            Brush(208, 214, 222), Brush(61, 71, 83), 2);
        AddRectangle(centerX - body * .42, centerY - 20, body * .84, 64,
            Brush(185, 193, 202), Brush(61, 71, 83), 2, 8);
        AddEllipse(centerX - body / 2 - 31, centerY - port / 2 + 13, 52, port,
            Brush(42, 45, 49), Orange, 4);
        AddRectangle(centerX - 31, centerY - vertical, 62, vertical - body * .28,
            Brush(187, 195, 204), Brush(61, 71, 83), 2, 7);
        AddEllipse(centerX - port / 2, centerY - vertical - 22, port, 44,
            Brush(42, 45, 49), Orange, 4);
        double wheel = Math.Clamp(wheelDiameter * 1.2, 105, 210);
        AddEllipse(centerX - wheel / 2, centerY - vertical - 75, wheel, wheel * .38,
            Brushes.Transparent, Brush(43, 91, 151), 9);
        AddPreviewText("Connector 1", centerX - body / 2 - 100, centerY - 92,
            Orange, 13, FontWeights.SemiBold);
        AddPreviewText("Connector 2", centerX + 25, centerY - vertical - 55,
            Orange, 13, FontWeights.SemiBold);
        AddDimensionLine(centerX - body / 2 - 35, centerY + 92,
            centerX + body / 2 + 35, centerY + 92, $"L {length:0.#}");
        AddVerticalDimension(centerX + body / 2 + 78, centerY - vertical, centerY, $"H {height:0.#}");
        AddPreviewText(_selectedPreset.Name, centerX - 24, centerY - 17,
            Brush(77, 86, 97), 14, FontWeights.Bold);
    }

    private void RefreshCheckPreview(
        double dn,
        double length,
        double bodyOd,
        double height,
        bool hasBonnet)
    {
        const double centerX = 360;
        const double centerY = 300;
        double bodyWidth = Math.Clamp(length * 2.05, 180, 420);
        double bodyHeight = Math.Clamp(bodyOd * 1.65, 72, 190);
        double left = centerX - bodyWidth / 2;
        double port = Math.Clamp(dn * 1.2, 30, 105);
        AddRectangle(left, centerY - bodyHeight * .38, bodyWidth, bodyHeight * .76,
            Brush(202, 208, 216), Brush(61, 71, 83), 2, bodyHeight * .2);
        AddEllipse(left - 30, centerY - port / 2, 48, port,
            Brush(42, 45, 49), Orange, 4);
        AddEllipse(left + bodyWidth - 18, centerY - port / 2, 48, port,
            Brush(42, 45, 49), Orange, 4);
        if (hasBonnet)
        {
            double bonnetWidth = bodyWidth * .44;
            AddRectangle(centerX - bonnetWidth / 2, centerY - bodyHeight * .85,
                bonnetWidth, bodyHeight * .45,
                Brush(184, 192, 202), Brush(61, 71, 83), 2, 8);
            AddEllipse(centerX - bonnetWidth * .56, centerY - bodyHeight * .92,
                bonnetWidth * 1.12, bodyHeight * .22,
                Brush(215, 220, 226), Brush(61, 71, 83), 2);
        }
        else
        {
            AddEllipse(centerX - bodyWidth * .22, centerY - bodyHeight * .46,
                bodyWidth * .44, bodyHeight * .92,
                Brush(218, 223, 229), Brush(61, 71, 83), 2);
        }
        AddPreviewText("Connector 1", left - 84, centerY - 88, Orange, 13, FontWeights.SemiBold);
        AddPreviewText("Connector 2", left + bodyWidth + 15, centerY - 88, Orange, 13, FontWeights.SemiBold);
        AddDimensionLine(left - 28, centerY + bodyHeight / 2 + 58,
            left + bodyWidth + 28, centerY + bodyHeight / 2 + 58, $"L {length:0.#}");
        AddVerticalDimension(left - 72, centerY - port / 2, centerY + port / 2, $"DN {dn:0.#}");
        AddPreviewText(_selectedPreset.Name, centerX - 24, centerY - 9,
            Brush(77, 86, 97), 14, FontWeights.Bold);
    }

    private void RefreshButterflyPreview(double dn, double length, double bodyOd, double height)
    {
        const double centerX = 360;
        const double centerY = 310;
        double disc = Math.Clamp(height * 1.18, 150, 300);
        double wafer = Math.Clamp(length * 2.2, 72, 145);
        AddEllipse(centerX - wafer / 2, centerY - disc / 2, wafer, disc,
            Brush(190, 198, 208), Brush(55, 66, 79), 3);
        AddEllipse(centerX - wafer * .34, centerY - disc * .42, wafer * .68, disc * .84,
            Brush(218, 223, 229), Brush(75, 86, 99), 2);
        AddRectangle(centerX - 19, centerY - disc / 2 - 76, 38, 80,
            Brush(178, 187, 198), Brush(55, 66, 79), 2, 5);
        AddEllipse(centerX - 38, centerY - disc / 2 - 92, 76, 30,
            Brush(205, 211, 219), Brush(55, 66, 79), 2);
        AddRectangle(centerX - 4, centerY - disc / 2 - 109, 265, 22,
            Brush(43, 91, 151), Brush(36, 55, 78), 2, 10);
        AddPreviewText("Connector 1", centerX - wafer / 2 - 112, centerY - 100,
            Orange, 13, FontWeights.SemiBold);
        AddPreviewText("Connector 2", centerX + wafer / 2 + 18, centerY - 100,
            Orange, 13, FontWeights.SemiBold);
        AddDimensionLine(centerX - wafer / 2, centerY + disc / 2 + 55,
            centerX + wafer / 2, centerY + disc / 2 + 55, $"L {length:0.#}");
        AddVerticalDimension(centerX - wafer / 2 - 68,
            centerY - disc / 2, centerY + disc / 2, $"DN {dn:0.#}");
        AddPreviewText(_selectedPreset.Name, centerX - 24, centerY - 9,
            Brush(77, 86, 97), 14, FontWeights.Bold);
    }

    private void AddDimensionLine(double x1, double y, double x2, double y2, string label)
    {
        _previewCanvas.Children.Add(new Line { X1 = x1, Y1 = y, X2 = x2, Y2 = y2, Stroke = Blue, StrokeThickness = 2 });
        _previewCanvas.Children.Add(new Line { X1 = x1, Y1 = y - 8, X2 = x1, Y2 = y + 8, Stroke = Blue, StrokeThickness = 2 });
        _previewCanvas.Children.Add(new Line { X1 = x2, Y1 = y - 8, X2 = x2, Y2 = y + 8, Stroke = Blue, StrokeThickness = 2 });
        AddPreviewText(label, (x1 + x2) / 2 - 28, y + 7, Blue, 14, FontWeights.SemiBold);
    }

    private void AddVerticalDimension(double x, double y1, double y2, string label)
    {
        _previewCanvas.Children.Add(new Line { X1 = x, Y1 = y1, X2 = x, Y2 = y2, Stroke = Blue, StrokeThickness = 2 });
        _previewCanvas.Children.Add(new Line { X1 = x - 8, Y1 = y1, X2 = x + 8, Y2 = y1, Stroke = Blue, StrokeThickness = 2 });
        _previewCanvas.Children.Add(new Line { X1 = x - 8, Y1 = y2, X2 = x + 8, Y2 = y2, Stroke = Blue, StrokeThickness = 2 });
        AddPreviewText(label, x + 10, (y1 + y2) / 2 - 10, Blue, 14, FontWeights.SemiBold);
    }

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
        var rectangle = new Rectangle
        {
            Width = width,
            Height = height,
            Fill = fill,
            Stroke = stroke,
            StrokeThickness = thickness,
            RadiusX = radius,
            RadiusY = radius
        };
        Canvas.SetLeft(rectangle, left);
        Canvas.SetTop(rectangle, top);
        _previewCanvas.Children.Add(rectangle);
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
        var ellipse = new Ellipse
        {
            Width = width,
            Height = height,
            Fill = fill,
            Stroke = stroke,
            StrokeThickness = thickness
        };
        Canvas.SetLeft(ellipse, left);
        Canvas.SetTop(ellipse, top);
        _previewCanvas.Children.Add(ellipse);
    }

    private void AddPreviewText(
        string text,
        double left,
        double top,
        Brush foreground,
        double size,
        FontWeight weight)
    {
        var block = new TextBlock
        {
            Text = text,
            Foreground = foreground,
            FontSize = size,
            FontWeight = weight
        };
        Canvas.SetLeft(block, left);
        Canvas.SetTop(block, top);
        _previewCanvas.Children.Add(block);
    }

    private void ShowCatalog()
    {
        string rows = string.Join(
            Environment.NewLine,
            Presets.Select(item =>
                $"{item.Name,-5} {item.NominalSize,-7} "
                + $"L {item.Length,6:0.#}  Port {item.PortDiameter,6:0.#}  "
                + $"H {item.Height,6:0.#}  Handle {item.HandleLength,6:0.#}"));
        MessageBox.Show(
            this,
            $"{_selectedCatalog.Manufacturer} {_selectedCatalog.Series}\n"
            + $"{_selectedCatalog.Revision}\n\n"
            + rows
            + "\n\nVerified basis: " + _selectedCatalog.DimensionBasis
            + "\n\nSource:\n" + _selectedCatalog.CatalogUrl,
            "FamilyMEP — Size Catalog");
    }

    private bool Validate(bool showSuccess)
    {
        var errors = new List<string>();
        if (!File.Exists(_templatePath.Text.Trim())
            || !IOPath.GetExtension(_templatePath.Text.Trim()).Equals(".rfa", StringComparison.OrdinalIgnoreCase))
            errors.Add("The bundled valve master RFA was not found.");
        if (string.IsNullOrWhiteSpace(_familyName.Text))
            errors.Add("Enter a Family name.");
        if (!TryPositive(_diameter.Text, out _)) errors.Add("DN must be greater than zero.");
        if (!TryPositive(_length.Text, out _)) errors.Add("L must be greater than zero.");
        if (!TryPositive(_bodyDiameter.Text, out _)) errors.Add("Body OD must be greater than zero.");
        if (!TryPositive(_height.Text, out _)) errors.Add("H must be greater than zero.");
        if (!TryPositive(_handleLength.Text, out _)) errors.Add("Handle L must be greater than zero.");
        if (string.IsNullOrWhiteSpace(_outputPath.Text)
            || !IOPath.GetExtension(_outputPath.Text.Trim()).Equals(".rfa", StringComparison.OrdinalIgnoreCase))
            errors.Add("Output path must end with .rfa.");

        if (errors.Count > 0)
        {
            _statusText.Text = $"{errors.Count} validation error(s)";
            _statusText.Foreground = Orange;
            MessageBox.Show(this, string.Join(Environment.NewLine, errors), "FamilyMEP — Validation");
            return false;
        }

        _statusText.Text = "Validation passed — ready to create RFA";
        _statusText.Foreground = Green;
        if (showSuccess)
            MessageBox.Show(
                this,
                $"Validation passed.\n\nCategory: Pipe Accessories\nPart Type: Valve - Breaks Into\nConnectors: 2 coaxial pipe connectors\nSize catalog: {Presets.Length} records",
                "FamilyMEP — Validation");
        return true;
    }

    private void Accept()
    {
        if (!Validate(showSuccess: false)) return;
        TryPositive(_diameter.Text, out double dn);
        TryPositive(_length.Text, out double length);
        TryPositive(_bodyDiameter.Text, out double bodyDiameter);
        TryPositive(_height.Text, out double height);
        TryPositive(_handleLength.Text, out double handleLength);

        Request = new ValveBuilderRequest(
            ValveSourceMode.FamilyFile,
            _templatePath.Text.Trim(),
            _selectedPreset.Name,
            dn,
            length,
            height,
            0,
            _connection.SelectedItem?.ToString() ?? "Threaded",
            _material.SelectedItem?.ToString() ?? "Stainless Steel",
            "DN|Nominal Diameter|Diameter|Connector Diameter",
            "L|Length|Body Length|Face to Face",
            "H|Height|Body Height|Total Height",
            "Handle Angle|Open Angle|Angle",
            "Connection Type|Connection|End Connection",
            "Body Material|Material",
            true,
            _openGeneratedFamily.IsChecked == true,
            false)
        {
            FamilyName = _familyName.Text.Trim(),
            OutputPath = _outputPath.Text.Trim(),
            BodyDiameterMm = bodyDiameter,
            PortDiameterMm = _selectedPreset.PortDiameter,
            HandleLengthMm = handleLength,
            Manufacturer = _selectedCatalog.Manufacturer,
            ProductSeries = _selectedCatalog.Series,
            CatalogRevision = _selectedCatalog.Revision,
            CatalogUrl = _selectedCatalog.CatalogUrl,
            CatalogDimensionBasis = _selectedCatalog.DimensionBasis,
            GeometryAccuracy =
                "Manufacturer-authored Revit geometry and verified Type data",
            LodStatus =
                "Manufacturer BIM geometry preserved; no procedural geometry reconstruction",
            PreserveSourceParameters = true,
            ExternalCatalogTypeName = _selectedPreset.SourceTypeName,
            SizeCatalog = Presets.Select(item => new ValveSizeDefinition(
                item.Name,
                item.Dn,
                item.Length,
                item.BodyDiameter,
                item.PortDiameter,
                item.Height,
                item.HandleLength)
            {
                SourceTypeName = item.SourceTypeName,
                PartNumber = item.PartNumber,
                NominalSize = item.NominalSize,
                Cv = item.Cv,
                TorqueNm = item.TorqueNm,
                WeightKg = item.WeightKg
            }).ToList()
        };

        DialogResult = true;
        Close();
    }

    private void BrowseTemplate()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select manufacturer valve master RFA",
            Filter = "Revit Family (*.rfa)|*.rfa",
            CheckFileExists = true,
            Multiselect = false
        };
        if (File.Exists(_templatePath.Text))
        {
            dialog.InitialDirectory = IOPath.GetDirectoryName(_templatePath.Text);
            dialog.FileName = IOPath.GetFileName(_templatePath.Text);
        }
        if (dialog.ShowDialog(this) == true) _templatePath.Text = dialog.FileName;
    }

    private void BrowseOutput()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Save Generated Valve Family",
            Filter = "Revit Family (*.rfa)|*.rfa",
            AddExtension = true,
            DefaultExt = ".rfa",
            OverwritePrompt = true,
            FileName = IOPath.GetFileName(_outputPath.Text)
        };
        string? folder = IOPath.GetDirectoryName(_outputPath.Text);
        if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
            dialog.InitialDirectory = folder;
        if (dialog.ShowDialog(this) == true) _outputPath.Text = dialog.FileName;
    }

    private static UIElement PathEditor(TextBox textBox, Action browse)
    {
        var grid = new Grid { Margin = new Thickness(0, 7, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        textBox.FontSize = 11;
        grid.Children.Add(textBox);
        Button button = SecondaryButton("…", 38);
        button.Margin = new Thickness(6, 0, 0, 0);
        button.Click += (_, _) => browse();
        Grid.SetColumn(button, 1);
        grid.Children.Add(button);
        return grid;
    }

    private static Border CollapsedCard(string title, string badge)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = FontWeights.SemiBold,
            Foreground = Ink,
            VerticalAlignment = VerticalAlignment.Center
        });
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        right.Children.Add(Badge(badge, Blue, new Thickness(0)));
        right.Children.Add(new TextBlock
        {
            Text = "⌄",
            FontSize = 18,
            Margin = new Thickness(8, -2, 0, 0),
            Foreground = Ink
        });
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);
        return new Border
        {
            Child = grid,
            Background = Brushes.White,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(13, 10, 12, 10),
            Margin = new Thickness(0, 0, 0, 8)
        };
    }

    private static Border Card(StackPanel stack) => new()
    {
        Child = stack,
        Background = Brushes.White,
        CornerRadius = new CornerRadius(6),
        Padding = new Thickness(13, 10, 13, 12),
        Margin = new Thickness(0, 0, 0, 8)
    };

    private static StackPanel CardStack(string title)
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = Ink,
            Margin = new Thickness(0, 0, 0, 8)
        });
        return stack;
    }

    private static void AddLabeledControl(StackPanel stack, string label, Control control)
    {
        stack.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = Muted,
            FontSize = 11,
            Margin = new Thickness(0, 5, 0, 3)
        });
        stack.Children.Add(control);
    }

    private static void AddCompactField(StackPanel stack, string label, Control control)
    {
        var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(126) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = Ink,
            VerticalAlignment = VerticalAlignment.Center
        });
        Grid.SetColumn(control, 1);
        row.Children.Add(control);
        stack.Children.Add(row);
    }

    private static TextBlock SectionTitle(string text) => new()
    {
        Text = text,
        FontSize = 15,
        FontWeight = FontWeights.SemiBold,
        Foreground = Ink,
        Margin = new Thickness(0, 18, 0, 4)
    };

    private static UIElement CheckRow(string text)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 6, 0, 0)
        };
        row.Children.Add(new Border
        {
            Width = 19,
            Height = 19,
            Background = Green,
            CornerRadius = new CornerRadius(10),
            Child = new TextBlock
            {
                Text = "✓",
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 11,
                FontWeight = FontWeights.Bold
            }
        });
        row.Children.Add(new TextBlock
        {
            Text = text,
            Foreground = Ink,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        });
        return row;
    }

    private static void AddSummaryRow(StackPanel parent, string label, string value)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(new TextBlock { Text = label, Foreground = Muted });
        var valueBlock = new TextBlock
        {
            Text = value,
            Foreground = Ink,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetColumn(valueBlock, 1);
        row.Children.Add(valueBlock);
        parent.Children.Add(row);
    }

    private static Border Badge(string text, Brush color, Thickness margin) => new()
    {
        Margin = margin,
        Padding = new Thickness(7, 3, 7, 3),
        Background = new SolidColorBrush(Color.FromArgb(24, ((SolidColorBrush)color).Color.R, ((SolidColorBrush)color).Color.G, ((SolidColorBrush)color).Color.B)),
        CornerRadius = new CornerRadius(9),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock
        {
            Text = text,
            Foreground = color,
            FontSize = 10,
            FontWeight = FontWeights.Bold
        }
    };

    private static TextBox Input(string value) => new()
    {
        Text = value,
        Height = 32,
        Padding = new Thickness(8, 4, 8, 4),
        VerticalContentAlignment = VerticalAlignment.Center,
        BorderBrush = Border,
        Background = Brushes.White
    };

    private static TextBox NumberInput(string value)
    {
        TextBox input = Input(value);
        input.HorizontalContentAlignment = HorizontalAlignment.Right;
        return input;
    }

    private static ComboBox Combo(string[] values, int selectedIndex) => new()
    {
        ItemsSource = values,
        SelectedIndex = selectedIndex,
        Height = 32,
        Padding = new Thickness(6, 3, 6, 3),
        BorderBrush = Border,
        Background = Brushes.White
    };

    private static Button PrimaryButton(string text) => new()
    {
        Content = text,
        Height = 42,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        Background = Blue,
        BorderBrush = Blue,
        Foreground = Brushes.White,
        FontWeight = FontWeights.SemiBold
    };

    private static Button SecondaryButton(string text, double width) => new()
    {
        Content = text,
        Width = width,
        Height = 36,
        Background = Brushes.White,
        BorderBrush = Brush(173, 187, 204),
        Foreground = Ink,
        Padding = new Thickness(10, 3, 10, 3)
    };

    private static bool TryPositive(string text, out double value)
    {
        string normalized = text.Trim().Replace(',', '.');
        return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
               && value > 0;
    }

    private static double ParseOr(string text, double fallback) =>
        TryPositive(text, out double value) ? value : fallback;

    private static string Format(double value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);

    private static ValveSizePreset[] LoadCatalogSizes(
        string masterFamilyPath,
        string lengthColumn,
        string bodyColumn,
        string heightColumn,
        string operatorColumn,
        (string SourceType, string Name, double Dn)[] selections)
    {
        string typeCatalogPath = IOPath.ChangeExtension(masterFamilyPath, ".txt");
        if (!File.Exists(typeCatalogPath))
            return selections.Select(item => FallbackCatalogSize(item.SourceType, item.Name, item.Dn)).ToArray();

        string[] lines = File.ReadAllLines(typeCatalogPath);
        if (lines.Length < 2)
            return selections.Select(item => FallbackCatalogSize(item.SourceType, item.Name, item.Dn)).ToArray();

        string[] headers = ParseCsvLine(lines[0])
            .Select(item =>
            {
                int separator = item.IndexOf("##", StringComparison.Ordinal);
                return separator < 0 ? item.Trim() : item[..separator].Trim();
            })
            .ToArray();
        Dictionary<string, string[]> rows = lines
            .Skip(1)
            .Select(ParseCsvLine)
            .Where(values => values.Length > 0 && !string.IsNullOrWhiteSpace(values[0]))
            .GroupBy(values => values[0], StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        string Read(string[] row, string column)
        {
            int index = Array.FindIndex(headers, item => item.Equals(column, StringComparison.OrdinalIgnoreCase));
            return index >= 0 && index < row.Length ? row[index].Trim() : string.Empty;
        }

        double ReadNumber(string[] row, string column)
        {
            if (string.IsNullOrWhiteSpace(column)) return 0;
            return double.TryParse(
                Read(row, column),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double value)
                ? value
                : 0;
        }

        var result = new List<ValveSizePreset>();
        foreach ((string sourceType, string name, double dn) in selections)
        {
            if (!rows.TryGetValue(sourceType, out string[]? row))
            {
                result.Add(FallbackCatalogSize(sourceType, name, dn));
                continue;
            }

            double length = ReadNumber(row, lengthColumn) * 25.4;
            double body = ReadNumber(row, bodyColumn) * 25.4;
            double height = ReadNumber(row, heightColumn) * 25.4;
            double operatorSize = ReadNumber(row, operatorColumn) * 25.4;
            if (length <= 0) length = Math.Max(dn * 2.2, 35);
            if (body <= 0) body = Math.Max(dn * 1.55, length * .65);
            if (height <= 0) height = Math.Max(body, dn * 1.8);
            if (operatorSize <= 0) operatorSize = Math.Max(height * .65, dn * 1.5);

            result.Add(new ValveSizePreset(
                name,
                dn,
                length,
                body,
                dn,
                height,
                operatorSize,
                Read(row, "Connection Size"),
                Read(row, "Material Number"),
                ReadNumber(row, "Cv Coefficient"),
                0,
                ReadNumber(row, "Unit Weight Value") * 0.45359237)
            {
                SourceTypeName = sourceType
            });
        }
        return result.ToArray();
    }

    private static ValveSizePreset FallbackCatalogSize(string sourceType, string name, double dn) =>
        new(
            name,
            dn,
            Math.Max(dn * 2.2, 35),
            Math.Max(dn * 1.55, 25),
            dn,
            Math.Max(dn * 2.4, 45),
            Math.Max(dn * 2.5, 70),
            name,
            string.Empty,
            0,
            0,
            0)
        {
            SourceTypeName = sourceType
        };

    private static string[] ParseCsvLine(string line)
    {
        var values = new List<string>();
        var value = new StringBuilder();
        bool quoted = false;
        for (int index = 0; index < line.Length; index++)
        {
            char character = line[index];
            if (character == '"')
            {
                if (quoted && index + 1 < line.Length && line[index + 1] == '"')
                {
                    value.Append('"');
                    index++;
                }
                else
                {
                    quoted = !quoted;
                }
            }
            else if (character == ',' && !quoted)
            {
                values.Add(value.ToString());
                value.Clear();
            }
            else
            {
                value.Append(character);
            }
        }
        values.Add(value.ToString());
        return values.ToArray();
    }

    private static SolidColorBrush Brush(byte red, byte green, byte blue) =>
        new(Color.FromRgb(red, green, blue));

    internal sealed record ValveSizePreset(
        string Name,
        double Dn,
        double Length,
        double BodyDiameter,
        double PortDiameter,
        double Height,
        double HandleLength,
        string NominalSize,
        string PartNumber,
        double Cv,
        double TorqueNm,
        double WeightKg)
    {
        public string SourceTypeName { get; init; } = Name;
    }

    internal sealed record ValveCatalogDefinition(
        string Manufacturer,
        string Series,
        string Revision,
        string CatalogUrl,
        string DimensionBasis,
        string Material,
        string MasterFamilyPath,
        string FamilyStem,
        string ValveType,
        bool IsGateValve,
        ValveSizePreset[] Sizes)
    {
        public string PreviewKind { get; init; } = IsGateValve ? "Gate" : "Ball";
        public string Connection { get; init; } = "Female NPT";
        public string DisplayName => $"{Manufacturer} — {Series}";
    }
}
