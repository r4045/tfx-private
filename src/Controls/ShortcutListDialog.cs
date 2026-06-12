using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Tfx;

/// <summary>One row of the feature list (機能一覧).</summary>
/// <param name="Group">Section header this row falls under.</param>
/// <param name="Name">The config.toml [shortcuts] action key (or a user command's name).</param>
/// <param name="Description">Brief, human-readable description. Empty renders as "(no description)".</param>
/// <param name="Key">Currently bound key combo, e.g. "Ctrl+Shift+T". Empty renders as "(unbound)".</param>
public sealed record ShortcutListEntry(string Group, string Name, string Description, string Key);

/// <summary>
/// Read-only reference of every command reachable by keyboard: the built-in
/// (rebindable) actions and any user-defined [[commands]], each with a brief
/// description and the key currently bound to it. The "Item" column is the
/// config.toml [shortcuts] action key, so the list doubles as a rebinding
/// reference. The key column is supplied live by the caller, so it never drifts
/// from the actual bindings. Esc (or Close) dismisses.
///
/// Built programmatically in the same dark theme as the other dialogs
/// (BookmarkQuickJumpDialog / TerminalSettingsDialog).
/// </summary>
public sealed class ShortcutListDialog : Window
{
    private static readonly Color MutedFg = Color.FromRgb(143, 155, 168);
    private static readonly Color AccentFg = Color.FromRgb(126, 211, 164);
    private static readonly Color BorderColor = Color.FromRgb(47, 58, 67);

    public ShortcutListDialog(IReadOnlyList<ShortcutListEntry> entries)
    {
        Title = Loc.T("Feature list");
        Owner = Application.Current.MainWindow;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.CanResize;
        MinWidth = 560;
        MaxWidth = 1100;
        MaxHeight = 720;
        Background = new SolidColorBrush(Color.FromRgb(15, 19, 23));
        Foreground = new SolidColorBrush(Color.FromRgb(222, 230, 236));
        FontFamily = new FontFamily("Consolas, Yu Gothic UI");

        var root = new DockPanel { Margin = new Thickness(16) };

        var hint = new TextBlock
        {
            Text = Loc.T("Close (Esc)"),
            Foreground = new SolidColorBrush(MutedFg),
            Margin = new Thickness(2, 12, 2, 0),
        };
        DockPanel.SetDock(hint, Dock.Bottom);
        root.Children.Add(hint);

        var table = new StackPanel();
        Grid.SetIsSharedSizeScope(table, true);
        table.Children.Add(BuildHeaderRow());

        string? currentGroup = null;
        foreach (var entry in entries)
        {
            if (!string.Equals(entry.Group, currentGroup, StringComparison.Ordinal))
            {
                currentGroup = entry.Group;
                table.Children.Add(BuildGroupHeader(entry.Group));
            }
            table.Children.Add(BuildDataRow(entry));
        }

        root.Children.Add(new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = 600,
            Content = table,
        });

        Content = root;

        Loaded += (_, _) => Focus();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close();
                e.Handled = true;
            }
        };
    }

    private static Grid MakeRowGrid()
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "sc_name" });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "sc_desc" });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "sc_key" });
        return g;
    }

    private Border BuildHeaderRow()
    {
        var grid = MakeRowGrid();
        grid.Children.Add(HeaderCell(Loc.T("Item"), 0));
        grid.Children.Add(HeaderCell(Loc.T("Description"), 1));
        grid.Children.Add(HeaderCell(Loc.T("Key"), 2));
        return new Border
        {
            Child = grid,
            Padding = new Thickness(6, 2, 6, 6),
            Margin = new Thickness(0, 0, 0, 4),
            BorderBrush = new SolidColorBrush(BorderColor),
            BorderThickness = new Thickness(0, 0, 0, 1),
        };
    }

    private Border BuildGroupHeader(string text) => new()
    {
        Child = new TextBlock
        {
            Text = text,
            Foreground = new SolidColorBrush(AccentFg),
            FontWeight = FontWeights.Bold,
        },
        Padding = new Thickness(6, 12, 6, 4),
    };

    private Border BuildDataRow(ShortcutListEntry entry)
    {
        var grid = MakeRowGrid();

        var name = new TextBlock
        {
            Text = entry.Name,
            Foreground = Foreground,
            Margin = new Thickness(0, 0, 24, 0),
        };
        Grid.SetColumn(name, 0);
        grid.Children.Add(name);

        var hasDescription = !string.IsNullOrWhiteSpace(entry.Description);
        var desc = new TextBlock
        {
            Text = hasDescription ? entry.Description : Loc.T("No description"),
            Foreground = hasDescription ? Foreground : new SolidColorBrush(MutedFg),
            Margin = new Thickness(0, 0, 24, 0),
            MaxWidth = 560,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(desc, 1);
        grid.Children.Add(desc);

        var hasKey = !string.IsNullOrWhiteSpace(entry.Key);
        var key = new TextBlock
        {
            Text = hasKey ? entry.Key : Loc.T("(unbound)"),
            Foreground = new SolidColorBrush(hasKey ? AccentFg : MutedFg),
            FontWeight = hasKey ? FontWeights.SemiBold : FontWeights.Normal,
        };
        Grid.SetColumn(key, 2);
        grid.Children.Add(key);

        return new Border
        {
            Child = grid,
            Padding = new Thickness(6, 3, 6, 3),
        };
    }

    private static TextBlock HeaderCell(string text, int column)
    {
        var tb = new TextBlock
        {
            Text = text,
            Foreground = new SolidColorBrush(MutedFg),
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 24, 0),
        };
        Grid.SetColumn(tb, column);
        return tb;
    }
}
