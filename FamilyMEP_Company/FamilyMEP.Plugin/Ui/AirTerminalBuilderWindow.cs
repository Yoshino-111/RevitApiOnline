using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FamilyMEP.Plugin.Infrastructure;
using FamilyMEP.Plugin.Models;
using Microsoft.Win32;

namespace FamilyMEP.Plugin.Ui;

internal sealed class AirTerminalBuilderWindow : Window
{
    private readonly AirTerminalDefinition _definition;
    private readonly ListBox _types;
    private readonly TextBox _outputPath;
    private readonly CheckBox _openAfterCreate;

    public AirTerminalBuilderRequest Request { get; private set; } = null!;

    public AirTerminalBuilderWindow(
        AirTerminalDefinition definition,
        AirTerminalInspection inspection)
    {
        _definition = definition;
        AppPaths.EnsureCreated();

        Title = $"FamilyMEP - {definition.DisplayName}";
        Width = 920;
        Height = 680;
        MinWidth = 820;
        MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(245, 248, 252));
        FontFamily = new FontFamily("Segoe UI");

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Content = root;

        Border header = Card(
            new StackPanel
            {
                Children =
                {
                    Text("FamilyMEP  —  Create Air Terminal Family", 24, FontWeights.SemiBold, Navy),
                    Text(
                        "Official manufacturer RFA geometry • internal Type catalog • metric output",
                        13,
                        FontWeights.Normal,
                        Muted)
                }
            },
            new Thickness(0),
            Brushes.White);
        header.Padding = new Thickness(24, 18, 24, 18);
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        var body = new Grid { Margin = new Thickness(22, 18, 22, 16) };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4, GridUnitType.Star) });
        Grid.SetRow(body, 1);
        root.Children.Add(body);

        var left = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
        Grid.SetColumn(left, 0);
        body.Children.Add(left);

        var productContent = new StackPanel();
        productContent.Children.Add(Text(definition.DisplayName, 20, FontWeights.SemiBold, Navy));
        productContent.Children.Add(Text(definition.Description, 13, FontWeights.Normal, Muted));
        productContent.Children.Add(InfoRow("Airflow role", definition.AirflowRole));
        productContent.Children.Add(InfoRow("Category", inspection.CategoryName));
        productContent.Children.Add(InfoRow("Duct connectors", inspection.ConnectorCount.ToString()));
        productContent.Children.Add(InfoRow("Source Types", inspection.TypeNames.Count.ToString()));
        var links = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 12, 0, 0)
        };
        links.Children.Add(LinkButton("Official product page", definition.ProductUrl));
        links.Children.Add(LinkButton("Open catalog PDF", definition.CatalogUrl));
        productContent.Children.Add(links);
        left.Children.Add(Card(productContent, new Thickness(0, 0, 0, 14)));

        var typeContent = new StackPanel();
        typeContent.Children.Add(Text("Official Family Types", 17, FontWeights.SemiBold, Navy));
        typeContent.Children.Add(Text(
            "Chọn Type mở mặc định. Tất cả Type chính hãng vẫn được giữ trong family đầu ra.",
            12,
            FontWeights.Normal,
            Muted));
        _types = new ListBox
        {
            ItemsSource = inspection.TypeNames,
            SelectedIndex = 0,
            MinHeight = 220,
            MaxHeight = 285,
            Margin = new Thickness(0, 12, 0, 0),
            BorderBrush = new SolidColorBrush(Color.FromRgb(199, 211, 226)),
            Background = Brushes.White
        };
        typeContent.Children.Add(_types);
        left.Children.Add(Card(typeContent, new Thickness(0)));

        var right = new StackPanel { Margin = new Thickness(12, 0, 0, 0) };
        Grid.SetColumn(right, 1);
        body.Children.Add(right);

        var workflow = new StackPanel();
        workflow.Children.Add(Text("Output workflow", 17, FontWeights.SemiBold, Navy));
        workflow.Children.Add(CheckLine("Keep official manufacturer-authored geometry"));
        workflow.Children.Add(CheckLine("Preserve every internal source Type"));
        workflow.Children.Add(CheckLine("Keep native duct connector association"));
        workflow.Children.Add(CheckLine("Convert display units to millimetres"));
        workflow.Children.Add(CheckLine("Rename editable custom parameters to FT_*"));
        workflow.Children.Add(CheckLine("Prefix embedded lookup tables with Family name"));
        workflow.Children.Add(CheckLine("Clear manufacturer metadata and use neutral gray"));
        right.Children.Add(Card(workflow, new Thickness(0, 0, 0, 14)));

        var output = new StackPanel();
        output.Children.Add(Text("Output RFA", 17, FontWeights.SemiBold, Navy));
        _outputPath = new TextBox
        {
            Text = Path.Combine(
                AppPaths.GeneratedAirTerminalFolder,
                $"{definition.OutputFamilyName}.rfa"),
            Margin = new Thickness(0, 10, 0, 8),
            Padding = new Thickness(8),
            BorderBrush = new SolidColorBrush(Color.FromRgb(199, 211, 226))
        };
        output.Children.Add(_outputPath);
        var outputActions = new StackPanel { Orientation = Orientation.Horizontal };
        var browse = SecondaryButton("Browse...");
        browse.Click += (_, _) => BrowseOutput();
        outputActions.Children.Add(browse);
        _openAfterCreate = new CheckBox
        {
            Content = "Open generated Family after creation",
            IsChecked = true,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(14, 0, 0, 0),
            Foreground = new SolidColorBrush(Navy)
        };
        outputActions.Children.Add(_openAfterCreate);
        output.Children.Add(outputActions);
        right.Children.Add(Card(output, new Thickness(0)));

        var footer = new Grid
        {
            Background = Brushes.White,
            Margin = new Thickness(0),
        };
        footer.ColumnDefinitions.Add(new ColumnDefinition());
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.Children.Add(Text(
            "The official RFA is copied; the source file is never modified.",
            12,
            FontWeights.Normal,
            Muted,
            new Thickness(24, 19, 0, 0)));
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 12, 22, 12)
        };
        var cancel = SecondaryButton("Cancel");
        cancel.MinWidth = 96;
        cancel.Click += (_, _) => Close();
        var create = PrimaryButton("Create RFA");
        create.MinWidth = 150;
        create.Margin = new Thickness(10, 0, 0, 0);
        create.Click += (_, _) => Accept();
        buttons.Children.Add(cancel);
        buttons.Children.Add(create);
        Grid.SetColumn(buttons, 1);
        footer.Children.Add(buttons);
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);
    }

    private void BrowseOutput()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Save generated Air Terminal Family",
            Filter = "Revit Family (*.rfa)|*.rfa",
            FileName = Path.GetFileName(_outputPath.Text),
            InitialDirectory = Path.GetDirectoryName(_outputPath.Text)
        };
        if (dialog.ShowDialog(this) == true)
            _outputPath.Text = dialog.FileName;
    }

    private void Accept()
    {
        string? typeName = _types.SelectedItem as string;
        if (string.IsNullOrWhiteSpace(typeName))
        {
            MessageBox.Show(
                this,
                "Select one official Family Type.",
                "FamilyMEP",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }
        if (string.IsNullOrWhiteSpace(_outputPath.Text))
        {
            MessageBox.Show(
                this,
                "Select an output RFA path.",
                "FamilyMEP",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        Request = new AirTerminalBuilderRequest(
            _definition,
            typeName,
            _outputPath.Text.Trim(),
            _openAfterCreate.IsChecked == true);
        DialogResult = true;
    }

    private static Border Card(
        UIElement content,
        Thickness margin,
        Brush? background = null) =>
        new()
        {
            Child = content,
            Background = background ?? Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(218, 227, 238)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(18),
            Margin = margin
        };

    private static TextBlock Text(
        string value,
        double size,
        FontWeight weight,
        Color color,
        Thickness? margin = null) =>
        new()
        {
            Text = value,
            FontSize = size,
            FontWeight = weight,
            Foreground = new SolidColorBrush(color),
            TextWrapping = TextWrapping.Wrap,
            Margin = margin ?? new Thickness(0, 0, 0, 5)
        };

    private static Grid InfoRow(string label, string value)
    {
        var row = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(125) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.Children.Add(Text(label, 12, FontWeights.SemiBold, Muted));
        TextBlock valueText = Text(value, 12, FontWeights.Normal, Navy);
        Grid.SetColumn(valueText, 1);
        row.Children.Add(valueText);
        return row;
    }

    private static CheckBox CheckLine(string text) =>
        new()
        {
            Content = text,
            IsChecked = true,
            IsEnabled = false,
            Foreground = new SolidColorBrush(Navy),
            Margin = new Thickness(0, 9, 0, 0)
        };

    private static Button LinkButton(string text, string url)
    {
        Button button = SecondaryButton(text);
        button.Margin = new Thickness(0, 0, 8, 0);
        button.Click += (_, _) =>
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch { }
        };
        return button;
    }

    private static Button PrimaryButton(string text) =>
        new()
        {
            Content = text,
            Height = 38,
            Padding = new Thickness(18, 0, 18, 0),
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Color.FromRgb(0, 101, 204)),
            Foreground = Brushes.White,
            FontWeight = FontWeights.SemiBold
        };

    private static Button SecondaryButton(string text) =>
        new()
        {
            Content = text,
            Height = 34,
            Padding = new Thickness(12, 0, 12, 0),
            Background = Brushes.White,
            Foreground = new SolidColorBrush(Navy),
            BorderBrush = new SolidColorBrush(Color.FromRgb(174, 190, 210)),
            BorderThickness = new Thickness(1)
        };

    private static readonly Color Navy = Color.FromRgb(13, 51, 92);
    private static readonly Color Muted = Color.FromRgb(88, 108, 132);
}
