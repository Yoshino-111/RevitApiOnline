using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using FamilyMEP.Plugin.Models;

namespace FamilyMEP.Plugin.Ui;

internal sealed class FamilySelectionDialog : Window
{
    private const int FamilyPageSize = 150;
    private readonly ObservableCollection<FamilyPickerItem> _families;
    private readonly ObservableCollection<FamilyPickerItem> _visibleFamilies = [];
    private readonly ObservableCollection<CategorySelectionItem> _categories;
    private readonly ComboBox? _projectCombo;
    private readonly TextBlock _familyPageText;
    private readonly TextBlock _selectionCount;
    private int _familyPageIndex;
    private bool _selectionCountPending;
    private bool _suppressSelectionCount;

    public FamilySelectionDialog(
        Window owner,
        string title,
        string description,
        IEnumerable<FamilyPickerItem> families,
        IReadOnlyCollection<ProjectDocumentItem>? projects = null,
        string? acceptText = null)
    {
        Owner = owner;
        Title = title;
        Width = 940;
        Height = 680;
        MinWidth = 760;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        Background = new SolidColorBrush(Color.FromRgb(246, 248, 252));
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13;

        _families = new ObservableCollection<FamilyPickerItem>(families);
        _categories = new ObservableCollection<CategorySelectionItem>(
            _families.GroupBy(item => item.Category)
                .OrderBy(group => group.Key, StringComparer.CurrentCultureIgnoreCase)
                .Select(group => new CategorySelectionItem
                {
                    Name = group.Key,
                    Count = group.Count(),
                    Selected = group.Any(item => item.Selected)
                }));
        foreach (CategorySelectionItem category in _categories)
        {
            category.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(CategorySelectionItem.Selected))
                    ApplyCategorySelection();
            };
        }

        var root = new Grid { Margin = new Thickness(18) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        header.Children.Add(new TextBlock
        {
            Text = description,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(31, 42, 68)),
            TextWrapping = TextWrapping.Wrap
        });
        if (projects is not null)
        {
            var projectRow = new Grid { Margin = new Thickness(0, 12, 0, 0) };
            projectRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            projectRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            projectRow.Children.Add(new TextBlock
            {
                Text = "Destination project:",
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = FontWeights.SemiBold
            });
            _projectCombo = new ComboBox
            {
                ItemsSource = projects,
                DisplayMemberPath = nameof(ProjectDocumentItem.DisplayName),
                SelectedIndex = projects.Count > 0 ? 0 : -1,
                MinHeight = 34,
                Padding = new Thickness(8, 4, 8, 4)
            };
            Grid.SetColumn(_projectCombo, 1);
            projectRow.Children.Add(_projectCombo);
            header.Children.Add(projectRow);
        }
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(280) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(content, 1);

        Border categoryPanel = CreatePanel();
        var categoryGrid = new Grid();
        categoryGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        categoryGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        categoryGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        categoryGrid.Children.Add(new TextBlock
        {
            Text = "1. Choose categories",
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 10)
        });
        var categoryTable = new DataGrid
        {
            ItemsSource = _categories,
            AutoGenerateColumns = false,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            SelectionMode = DataGridSelectionMode.Extended
        };
        categoryTable.Columns.Add(new DataGridCheckBoxColumn
        {
            Header = "Use",
            Binding = new Binding(nameof(CategorySelectionItem.Selected))
            {
                Mode = BindingMode.TwoWay,
                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
            },
            Width = 52
        });
        categoryTable.Columns.Add(new DataGridTextColumn
        {
            Header = "Category",
            Binding = new Binding(nameof(CategorySelectionItem.Name)),
            Width = new DataGridLength(1, DataGridLengthUnitType.Star)
        });
        categoryTable.Columns.Add(new DataGridTextColumn
        {
            Header = "Count",
            Binding = new Binding(nameof(CategorySelectionItem.Count)),
            Width = 58
        });
        Grid.SetRow(categoryTable, 1);
        categoryGrid.Children.Add(categoryTable);
        var applyCategories = new Button
        {
            Content = "Apply checked categories",
            MinHeight = 36,
            Margin = new Thickness(0, 10, 0, 0)
        };
        applyCategories.Click += (_, _) => ApplyCategorySelection();
        Grid.SetRow(applyCategories, 2);
        categoryGrid.Children.Add(applyCategories);
        categoryPanel.Child = categoryGrid;
        content.Children.Add(categoryPanel);

        Border familyPanel = CreatePanel();
        var familyGrid = new Grid();
        familyGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        familyGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        familyGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        familyGrid.Children.Add(new TextBlock
        {
            Text = "2. Confirm individual families",
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 10)
        });
        var familyTable = new DataGrid
        {
            ItemsSource = _visibleFamilies,
            AutoGenerateColumns = false,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            EnableRowVirtualization = true,
            EnableColumnVirtualization = true
        };
        VirtualizingPanel.SetIsVirtualizing(familyTable, true);
        VirtualizingPanel.SetVirtualizationMode(familyTable, VirtualizationMode.Recycling);
        ScrollViewer.SetCanContentScroll(familyTable, true);
        familyTable.Columns.Add(new DataGridCheckBoxColumn
        {
            Header = "Load",
            Binding = new Binding(nameof(FamilyPickerItem.Selected))
            {
                Mode = BindingMode.TwoWay,
                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
            },
            Width = 58
        });
        familyTable.Columns.Add(new DataGridTextColumn
        {
            Header = "Family name",
            Binding = new Binding(nameof(FamilyPickerItem.Name)),
            Width = new DataGridLength(1, DataGridLengthUnitType.Star)
        });
        familyTable.Columns.Add(new DataGridTextColumn
        {
            Header = "Category",
            Binding = new Binding(nameof(FamilyPickerItem.Category)),
            Width = 220
        });
        Grid.SetRow(familyTable, 1);
        familyGrid.Children.Add(familyTable);

        var familyPager = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        familyPager.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        familyPager.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        familyPager.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var previousPage = new Button { Content = "‹", Width = 42, MinHeight = 32 };
        previousPage.Click += (_, _) => ChangeFamilyPage(-1);
        familyPager.Children.Add(previousPage);
        _familyPageText = new TextBlock
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromRgb(91, 103, 126))
        };
        Grid.SetColumn(_familyPageText, 1);
        familyPager.Children.Add(_familyPageText);
        var nextPage = new Button { Content = "›", Width = 42, MinHeight = 32 };
        nextPage.Click += (_, _) => ChangeFamilyPage(1);
        Grid.SetColumn(nextPage, 2);
        familyPager.Children.Add(nextPage);
        Grid.SetRow(familyPager, 2);
        familyGrid.Children.Add(familyPager);

        familyPanel.Child = familyGrid;
        Grid.SetColumn(familyPanel, 2);
        content.Children.Add(familyPanel);
        root.Children.Add(content);

        var footer = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _selectionCount = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        foreach (FamilyPickerItem item in _families)
        {
            item.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(FamilyPickerItem.Selected)) ScheduleSelectionCountUpdate();
            };
        }
        UpdateSelectionCount();
        footer.Children.Add(_selectionCount);
        var cancel = new Button { Content = "Cancel", Width = 110, MinHeight = 38, Margin = new Thickness(0, 0, 10, 0) };
        cancel.Click += (_, _) => { DialogResult = false; Close(); };
        Grid.SetColumn(cancel, 1);
        footer.Children.Add(cancel);
        var accept = new Button
        {
            Content = acceptText ?? (projects is null ? "Export selected" : "Load selected"),
            Width = 145,
            MinHeight = 38,
            Background = new SolidColorBrush(Color.FromRgb(102, 55, 245)),
            Foreground = Brushes.White,
            FontWeight = FontWeights.SemiBold
        };
        accept.Click += (_, _) => Accept();
        Grid.SetColumn(accept, 2);
        footer.Children.Add(accept);
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);

        Content = root;
        RefreshFamilyPage();
    }

    public IReadOnlyList<FamilyPickerItem> SelectedFamilies =>
        _families.Where(item => item.Selected).ToList();

    public ProjectDocumentItem? SelectedProject => _projectCombo?.SelectedItem as ProjectDocumentItem;

    private void ApplyCategorySelection()
    {
        HashSet<string> selected = _categories.Where(item => item.Selected)
            .Select(item => item.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _suppressSelectionCount = true;
        try
        {
            foreach (FamilyPickerItem family in _families)
            {
                family.Selected = selected.Contains(family.Category);
            }
        }
        finally
        {
            _suppressSelectionCount = false;
        }
        UpdateSelectionCount();
    }

    private void ChangeFamilyPage(int direction)
    {
        int pageCount = Math.Max(1, (int)Math.Ceiling(_families.Count / (double)FamilyPageSize));
        _familyPageIndex = Math.Max(0, Math.Min(pageCount - 1, _familyPageIndex + direction));
        RefreshFamilyPage();
    }

    private void RefreshFamilyPage()
    {
        int pageCount = Math.Max(1, (int)Math.Ceiling(_families.Count / (double)FamilyPageSize));
        _familyPageIndex = Math.Max(0, Math.Min(pageCount - 1, _familyPageIndex));
        int start = _familyPageIndex * FamilyPageSize;
        int end = Math.Min(_families.Count, start + FamilyPageSize);
        _visibleFamilies.Clear();
        for (int index = start; index < end; index++) _visibleFamilies.Add(_families[index]);
        _familyPageText.Text = _families.Count == 0
            ? "No families"
            : $"Page {_familyPageIndex + 1:N0} / {pageCount:N0}  ·  {start + 1:N0}-{end:N0} of {_families.Count:N0}";
    }

    private void ScheduleSelectionCountUpdate()
    {
        if (_suppressSelectionCount || _selectionCountPending) return;
        _selectionCountPending = true;
        Dispatcher.BeginInvoke(() =>
        {
            _selectionCountPending = false;
            UpdateSelectionCount();
        });
    }

    private void UpdateSelectionCount() =>
        _selectionCount.Text = $"Selected: {_families.Count(item => item.Selected):N0} / {_families.Count:N0}";

    private void Accept()
    {
        if (_projectCombo is not null && SelectedProject is null)
        {
            MessageBox.Show(this, "Choose a destination project.", "FamilyMEP");
            return;
        }
        if (SelectedFamilies.Count == 0)
        {
            MessageBox.Show(this, "Choose at least one category or family.", "FamilyMEP");
            return;
        }
        DialogResult = true;
        Close();
    }

    private static Border CreatePanel() => new()
    {
        Background = Brushes.White,
        BorderBrush = new SolidColorBrush(Color.FromRgb(113, 134, 163)),
        BorderThickness = new Thickness(1.8),
        CornerRadius = new CornerRadius(7),
        Padding = new Thickness(14)
    };
}
