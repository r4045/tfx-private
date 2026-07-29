using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Tfx;

/// <summary>
/// Three-field dialog for adding OR editing a bookmark: the group, an alias
/// (display label, which may be left blank to fall back to the folder name),
/// and the path (pre-filled, editable). Mirrors the look of the other
/// code-built dialogs (e.g. <see cref="TerminalSettingsDialog"/>).
///
/// The two callers differ only in the pre-filled values and the confirm-button
/// label (<c>okText</c>): the addBookmark command seeds it from the current
/// folder and says "Add"; the quick-jump dialog's Ctrl-commit (edit) seeds it
/// from an existing entry and says "Save". All the interpretation — blank alias
/// means folder name, an unknown group name creates that group — lives in the
/// caller, not here.
/// </summary>
public sealed class BookmarkDialog : Window
{
    private readonly ComboBox _groupBox;
    private readonly TextBox _aliasBox;
    private readonly TextBox _pathBox;

    public BookmarkDialog(
        string title,
        IEnumerable<string> groups,
        string defaultGroup,
        string defaultAlias,
        string path,
        string? okText = null)
    {
        Title = title;
        Owner = Application.Current.MainWindow;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        MinWidth = 520;
        Background = new SolidColorBrush(Color.FromRgb(15, 19, 23));
        Foreground = new SolidColorBrush(Color.FromRgb(222, 230, 236));
        FontFamily = new FontFamily("Consolas, Yu Gothic UI");

        var root = new Grid { Margin = new Thickness(16) };
        for (var i = 0; i < 7; i++)
        {
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        var groupLabel = MakeLabel(Loc.T("Group (pick an existing one or type a new name)"));
        Grid.SetRow(groupLabel, 0);
        root.Children.Add(groupLabel);

        _groupBox = new ComboBox
        {
            IsEditable = true,
            MinWidth = 480,
            Style = (Style)Application.Current.FindResource("TfxComboBox"),
        };
        foreach (var g in groups)
        {
            _groupBox.Items.Add(g);
        }
        _groupBox.Text = defaultGroup;
        Grid.SetRow(_groupBox, 1);
        root.Children.Add(_groupBox);

        var aliasLabel = MakeLabel(Loc.T("Alias (leave blank to use the folder name)"));
        aliasLabel.Margin = new Thickness(0, 14, 0, 6);
        Grid.SetRow(aliasLabel, 2);
        root.Children.Add(aliasLabel);

        _aliasBox = MakeTextBox(defaultAlias);
        Grid.SetRow(_aliasBox, 3);
        root.Children.Add(_aliasBox);

        var pathLabel = MakeLabel(Loc.T("Path"));
        pathLabel.Margin = new Thickness(0, 14, 0, 6);
        Grid.SetRow(pathLabel, 4);
        root.Children.Add(pathLabel);

        _pathBox = MakeTextBox(path);
        Grid.SetRow(_pathBox, 5);
        root.Children.Add(_pathBox);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0)
        };

        var ok = new Button
        {
            Content = string.IsNullOrWhiteSpace(okText) ? Loc.T("Add") : okText,
            IsDefault = true,
            MinWidth = 76,
            Margin = new Thickness(0, 0, 8, 0)
        };
        ok.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(ok);

        var cancel = new Button
        {
            Content = Loc.T("Cancel"),
            IsCancel = true,
            MinWidth = 76
        };
        buttons.Children.Add(cancel);

        Grid.SetRow(buttons, 6);
        root.Children.Add(buttons);

        Content = root;

        Loaded += (_, _) =>
        {
            // Path is pre-filled and alias defaults to the folder name, so start
            // in the group field — the thing the user most often sets.
            _groupBox.Focus();
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                DialogResult = false;
            }
        };
    }

    public string Group => _groupBox.Text?.Trim() ?? string.Empty;
    public string Alias => _aliasBox.Text?.Trim() ?? string.Empty;
    public string Path => _pathBox.Text?.Trim() ?? string.Empty;

    private TextBlock MakeLabel(string text) => new()
    {
        Text = text,
        Margin = new Thickness(0, 0, 0, 6),
        Foreground = Foreground
    };

    private TextBox MakeTextBox(string text) => new()
    {
        Text = text ?? string.Empty,
        MinWidth = 480,
        Padding = new Thickness(6, 3, 6, 3),
        Background = new SolidColorBrush(Color.FromRgb(13, 16, 19)),
        Foreground = Foreground,
        CaretBrush = new SolidColorBrush(Color.FromRgb(126, 211, 164)),
        BorderBrush = new SolidColorBrush(Color.FromRgb(47, 58, 67))
    };
}
