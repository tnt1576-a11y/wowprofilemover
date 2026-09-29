using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace WowProfileMover.App;

public sealed record ReviewRow(string Change, string File, string Category, string Size);

public sealed class ReviewWindow : Window
{
    public ReviewWindow(string title, string description, IEnumerable<ReviewRow> rows, IEnumerable<string> warnings, string confirmText, bool canApply)
    {
        Style = (Style)FindResource(typeof(Window));
        Title = title;
        Width = 880; Height = 690; MinWidth = 680; MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var grid = new Grid { Margin = new Thickness(26), Background = (Brush)FindResource("Bg") };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var heading = new StackPanel();
        heading.Children.Add(new TextBlock { Text = title, FontSize = 25, FontWeight = FontWeights.SemiBold });
        heading.Children.Add(new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap, Foreground = (Brush)FindResource("Muted"), Margin = new Thickness(0, 9, 0, 18) });
        grid.Children.Add(heading);
        var body = new Grid();
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var table = new DataGrid { ItemsSource = rows, SelectionMode = DataGridSelectionMode.Single };
        var rowStyle = new Style(typeof(DataGridRow));
        rowStyle.Setters.Add(new Setter(ToolTipProperty, new System.Windows.Data.Binding("File")));
        table.RowStyle = rowStyle;
        table.Columns.Add(Column("Action", "Change", 90));
        table.Columns.Add(new DataGridTextColumn { Header = "File", Binding = new System.Windows.Data.Binding("File"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        table.Columns.Add(Column("Category", "Category", 140));
        table.Columns.Add(Column("Size", "Size", 80));
        body.Children.Add(table);
        var warningPanel = new StackPanel { Margin = new Thickness(0, 15, 0, 0) };
        foreach (var warning in warnings)
            warningPanel.Children.Add(new TextBlock { Text = "•  " + warning, Foreground = new SolidColorBrush(Color.FromRgb(221, 198, 155)), FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 7) });
        var warningScroll = new ScrollViewer { Content = warningPanel, MaxHeight = 160, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(warningScroll, 1); body.Children.Add(warningScroll); Grid.SetRow(body, 1); grid.Children.Add(body);
        var footer = new StackPanel { Margin = new Thickness(0, 17, 0, 0) };
        var consent = new CheckBox { Content = "I reviewed the destination and the files above. WoW is fully closed.", FontSize = 12, IsEnabled = canApply };
        footer.Children.Add(consent);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        var cancel = new Button { Content = "Back", IsCancel = true, MinWidth = 90, Margin = new Thickness(0, 0, 10, 0) };
        var apply = new Button { Content = confirmText, Style = (Style)FindResource("PrimaryButton"), IsEnabled = false, MinWidth = 170 };
        consent.Checked += (_, _) => apply.IsEnabled = canApply;
        consent.Unchecked += (_, _) => apply.IsEnabled = false;
        apply.Click += (_, _) => { DialogResult = true; Close(); };
        buttons.Children.Add(cancel); buttons.Children.Add(apply); footer.Children.Add(buttons);
        Grid.SetRow(footer, 2); grid.Children.Add(footer);
        Content = grid;
        SourceInitialized += (_, _) => MainWindow.UseDarkTitleBar(this);
    }
    private static DataGridTextColumn Column(string title, string property, int width) => new() { Header = title, Binding = new System.Windows.Data.Binding(property), Width = width };
}
