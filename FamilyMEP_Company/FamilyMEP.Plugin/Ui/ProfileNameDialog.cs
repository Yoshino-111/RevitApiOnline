using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FamilyMEP.Plugin.Ui;

internal sealed class ProfileNameDialog : Window
{
    private readonly TextBox _nameBox;

    public ProfileNameDialog(Window owner, string initialName = "")
    {
        Owner = owner;
        Title = "Save Project Profile";
        Width = 460;
        Height = 220;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(246, 248, 252));
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13;

        var root = new Grid { Margin = new Thickness(22) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(new TextBlock
        {
            Text = "Project profile name",
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(31, 42, 68))
        });
        var description = new TextBlock
        {
            Text = "Example: Project A, Hospital B, Office C",
            Foreground = new SolidColorBrush(Color.FromRgb(100, 112, 138)),
            Margin = new Thickness(0, 6, 0, 12)
        };
        Grid.SetRow(description, 1);
        root.Children.Add(description);
        _nameBox = new TextBox
        {
            Text = initialName,
            Height = 38,
            Padding = new Thickness(10, 7, 10, 7),
            VerticalContentAlignment = VerticalAlignment.Center
        };
        Grid.SetRow(_nameBox, 2);
        root.Children.Add(_nameBox);

        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0)
        };
        var cancel = new Button { Content = "Cancel", Width = 100, Height = 38, Margin = new Thickness(0, 0, 10, 0) };
        cancel.Click += (_, _) => { DialogResult = false; Close(); };
        var save = new Button
        {
            Content = "Save Profile",
            Width = 125,
            Height = 38,
            Background = new SolidColorBrush(Color.FromRgb(102, 55, 245)),
            Foreground = Brushes.White,
            FontWeight = FontWeights.SemiBold
        };
        save.Click += (_, _) => Accept();
        footer.Children.Add(cancel);
        footer.Children.Add(save);
        Grid.SetRow(footer, 3);
        root.Children.Add(footer);
        Content = root;

        Loaded += (_, _) => { _nameBox.Focus(); _nameBox.SelectAll(); };
        _nameBox.KeyDown += (_, args) => { if (args.Key == System.Windows.Input.Key.Enter) Accept(); };
    }

    public string ProfileName => _nameBox.Text.Trim();

    private void Accept()
    {
        if (string.IsNullOrWhiteSpace(ProfileName))
        {
            MessageBox.Show(this, "Enter a profile name.", "FamilyMEP");
            return;
        }
        DialogResult = true;
        Close();
    }
}
