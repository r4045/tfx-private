using System.Linq;
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
    private static readonly Color RowSelectedBg = Color.FromArgb(40, 126, 211, 164);

    private enum KeySortState { None, Ascending, Descending }

    private IReadOnlyList<ShortcutListEntry> _entries = Array.Empty<ShortcutListEntry>();
    private StackPanel _table = null!;
    private KeySortState _keySortState = KeySortState.None;
    private ShortcutListEntry? _selectedEntry;

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

        _entries = entries;
        _table = new StackPanel();
        Grid.SetIsSharedSizeScope(_table, true);
        RebuildTable();

        var scroller = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = 600,
            Content = _table,
            // Focusable so the list takes keyboard focus on open and the arrow /
            // PageUp/PageDown keys scroll immediately, without a Tab press first.
            Focusable = true,
        };
        root.Children.Add(scroller);

        Content = root;

        // Focus the scroll area (not the Window) so scroll keys work right away.
        Loaded += (_, _) => scroller.Focus();
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
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "sc_key" });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "sc_desc" });
        return g;
    }

    private void RebuildTable()
    {
        _table.Children.Clear();
        _table.Children.Add(BuildHeaderRow());

        var groups = new List<(string Group, List<ShortcutListEntry> Items)>();
        foreach (var entry in _entries)
        {
            if (groups.Count == 0 || !string.Equals(groups[^1].Group, entry.Group, StringComparison.Ordinal))
            {
                groups.Add((entry.Group, new List<ShortcutListEntry>()));
            }
            groups[^1].Items.Add(entry);
        }

        foreach (var (group, items) in groups)
        {
            _table.Children.Add(BuildGroupHeader(group));
            var ordered = _keySortState == KeySortState.None
                ? items
                : SortByKey(items, ascending: _keySortState == KeySortState.Ascending);
            foreach (var entry in ordered)
            {
                _table.Children.Add(BuildDataRow(entry));
            }
        }
    }

    /// <summary>
    /// Sorts a group's rows by Key. Unbound (empty Key) rows always sink to the
    /// bottom regardless of direction, since an empty string sorting first would
    /// put them at the top on ascending and bury bound keys on descending.
    /// </summary>
    private static List<ShortcutListEntry> SortByKey(List<ShortcutListEntry> items, bool ascending)
    {
        var bound = items.Where(e => !string.IsNullOrWhiteSpace(e.Key));
        var unbound = items.Where(e => string.IsNullOrWhiteSpace(e.Key));
        bound = ascending
            ? bound.OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase)
            : bound.OrderByDescending(e => e.Key, StringComparer.OrdinalIgnoreCase);
        return bound.Concat(unbound).ToList();
    }

    private Border BuildHeaderRow()
    {
        var grid = MakeRowGrid();
        grid.Children.Add(HeaderCell(Loc.T("Item"), 0));
        grid.Children.Add(BuildKeyHeaderCell(1));
        grid.Children.Add(HeaderCell(Loc.T("Description"), 2));
        return new Border
        {
            Child = grid,
            Padding = new Thickness(6, 2, 6, 6),
            Margin = new Thickness(0, 0, 0, 4),
            BorderBrush = new SolidColorBrush(BorderColor),
            BorderThickness = new Thickness(0, 0, 0, 1),
        };
    }

    /// <summary>
    /// Clickable "Key" header. Three-state cycle on click: unsorted -&gt; ascending
    /// -&gt; descending -&gt; unsorted. Sorting is scoped to each group (Built-in /
    /// User commands stay separate); group headers themselves never move.
    /// </summary>
    private FrameworkElement BuildKeyHeaderCell(int column)
    {
        var arrow = _keySortState switch
        {
            KeySortState.Ascending => " \u25b2",
            KeySortState.Descending => " \u25bc",
            _ => "",
        };
        var isActive = _keySortState != KeySortState.None;
        var tb = new TextBlock
        {
            Text = Loc.T("Key") + arrow,
            Foreground = new SolidColorBrush(isActive ? AccentFg : MutedFg),
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 24, 0),
            Cursor = Cursors.Hand,
        };
        tb.MouseLeftButtonUp += (_, _) =>
        {
            _keySortState = _keySortState switch
            {
                KeySortState.None => KeySortState.Ascending,
                KeySortState.Ascending => KeySortState.Descending,
                _ => KeySortState.None,
            };
            RebuildTable();
        };
        Grid.SetColumn(tb, column);
        return tb;
    }

    /// <summary>
    /// Selects a row for visual confirmation only (no side effect). Repaints
    /// backgrounds in place rather than calling <see cref="RebuildTable"/> so the
    /// scroll position doesn't jump. Selection is single-row and survives a
    /// re-sort because <see cref="BuildDataRow"/> reads <see cref="_selectedEntry"/>
    /// at build time.
    /// </summary>
    private void SelectRow(ShortcutListEntry entry)
    {
        _selectedEntry = entry;
        foreach (var child in _table.Children)
        {
            if (child is Border { Tag: ShortcutListEntry rowEntry } border)
            {
                border.Background = new SolidColorBrush(rowEntry.Equals(entry) ? RowSelectedBg : Colors.Transparent);
            }
        }
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

        var hasKey = !string.IsNullOrWhiteSpace(entry.Key);
        var key = new TextBlock
        {
            Text = hasKey ? entry.Key : Loc.T("(unbound)"),
            Foreground = new SolidColorBrush(hasKey ? AccentFg : MutedFg),
            FontWeight = hasKey ? FontWeights.SemiBold : FontWeights.Normal,
            Margin = new Thickness(0, 0, 24, 0),
        };
        Grid.SetColumn(key, 1);
        grid.Children.Add(key);

        var hasDescription = !string.IsNullOrWhiteSpace(entry.Description);
        var desc = new TextBlock
        {
            Text = hasDescription ? entry.Description : Loc.T("No description"),
            Foreground = hasDescription ? Foreground : new SolidColorBrush(MutedFg),
            MaxWidth = 560,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(desc, 2);
        grid.Children.Add(desc);

        var border = new Border
        {
            Child = grid,
            Padding = new Thickness(6, 3, 6, 3),
            Tag = entry,
            Background = new SolidColorBrush(entry.Equals(_selectedEntry) ? RowSelectedBg : Colors.Transparent),
            Cursor = Cursors.Hand,
        };
        border.MouseLeftButtonUp += (_, _) => SelectRow(entry);
        return border;
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
