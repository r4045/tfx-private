using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Tfx;

/// <summary>
/// One selectable row in the command palette.
/// </summary>
/// <param name="Group">Section header this row falls under (e.g. "Built-in commands").</param>
/// <param name="Title">Primary, human-readable label (the action's description, or its
/// id / a command's name when no description exists).</param>
/// <param name="SubText">Dimmed secondary text: the config.toml [shortcuts] action id for a
/// built-in, or the first line of a user command's run text. Also searched by the filter.</param>
/// <param name="KeyText">Currently bound key combo (e.g. "Ctrl+P"), or empty when unbound.</param>
/// <param name="Tag">Opaque dispatch token, set by the caller: an action-id string for a
/// built-in, or the <see cref="UserCommand"/> for a user-defined command. The dialog never
/// inspects it — it only hands the chosen row's Tag back to the caller.</param>
public sealed record PaletteRow(string Group, string Title, string SubText, string KeyText, object Tag);

/// <summary>
/// Executable command palette (the commandPalette command, Ctrl+P by default): a single
/// keyboard-driven list of every runnable built-in action plus the user-defined
/// <c>[[commands]]</c> that match the current context. Type to filter by a case-insensitive
/// substring over each row's title and id/sub text (so both "reload" and "再読み込み" find
/// the same row); ↑/↓ move the cursor; Enter runs the highlighted row; Esc closes without
/// running anything.
///
/// The dialog is intentionally dumb: it neither knows nor cares what a row *does*. It
/// surfaces a selection and returns the chosen row's <see cref="PaletteRow.Tag"/> via
/// <see cref="Selected"/>; the owner (MainWindow) maps that back to an action and runs it.
/// This mirrors how <see cref="BookmarkQuickJumpDialog"/> returns a path the owner then
/// navigates to. IME stays enabled (unlike the bookmark quick-jump) so Japanese descriptions
/// remain searchable. Built in the same dark theme as the other dialogs.
/// </summary>
public sealed class CommandPaletteDialog : Window
{
    private static readonly Color SelectionBg = Color.FromRgb(54, 80, 98);
    private static readonly Color AccentFg = Color.FromRgb(126, 211, 164);
    private static readonly Color MutedFg = Color.FromRgb(143, 155, 168);
    private static readonly Color BorderColor = Color.FromRgb(47, 58, 67);

    private readonly IReadOnlyList<PaletteRow> _all;
    private readonly TextBox _filterBox;
    private readonly StackPanel _list;
    private readonly TextBlock _hint;

    // Rows currently shown (after the active filter), parallel to their row Borders.
    private readonly List<PaletteRow> _visible = [];
    private readonly List<Border> _visibleBorders = [];
    private int _selectedIndex = -1;

    /// <summary>The chosen row, or null when the dialog was cancelled (Esc / close).</summary>
    public PaletteRow? Selected { get; private set; }

    public CommandPaletteDialog(IReadOnlyList<PaletteRow> rows)
    {
        _all = rows;

        Title = Loc.T("Command palette");
        Owner = Application.Current.MainWindow;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Width = 640;
        Height = 480;
        Background = new SolidColorBrush(Color.FromRgb(15, 19, 23));
        Foreground = new SolidColorBrush(Color.FromRgb(222, 230, 236));
        FontFamily = new FontFamily("Consolas, Yu Gothic UI");

        var root = new DockPanel { Margin = new Thickness(16) };

        _filterBox = new TextBox
        {
            Background = new SolidColorBrush(Color.FromRgb(23, 28, 33)),
            Foreground = Foreground,
            CaretBrush = new SolidColorBrush(AccentFg),
            BorderBrush = new SolidColorBrush(BorderColor),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 6, 8, 6),
            FontSize = 14,
        };
        DockPanel.SetDock(_filterBox, Dock.Top);
        _filterBox.TextChanged += (_, _) => ApplyFilter(_filterBox.Text);
        root.Children.Add(_filterBox);

        _hint = new TextBlock
        {
            Foreground = new SolidColorBrush(MutedFg),
            Margin = new Thickness(2, 10, 2, 0),
            Text = Loc.T("Type to filter · ↑↓ to move · Enter to run · Esc to close"),
        };
        DockPanel.SetDock(_hint, Dock.Bottom);
        root.Children.Add(_hint);

        _list = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        Grid.SetIsSharedSizeScope(_list, true);
        root.Children.Add(new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _list,
        });

        Content = root;
        ApplyFilter("");

        Loaded += (_, _) => _filterBox.Focus();
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Selected = null;
                DialogResult = false;
                e.Handled = true;
                return;
            case Key.Up:
                MoveSelection(-1);
                e.Handled = true;
                return;
            case Key.Down:
                MoveSelection(1);
                e.Handled = true;
                return;
            case Key.Enter:
                if (_selectedIndex >= 0 && _selectedIndex < _visible.Count)
                {
                    Selected = _visible[_selectedIndex];
                    DialogResult = true;
                }
                e.Handled = true;
                return;
        }
        // Everything else (printable keys, Backspace, IME composition) falls through
        // to the focused filter TextBox, which drives ApplyFilter via TextChanged.
    }

    /// <summary>
    /// Rebuilds the visible list for <paramref name="query"/> — a case-insensitive substring
    /// over each row's title and sub text — preserving the caller's row order and inserting a
    /// group header whenever the group changes. Resets the cursor to the first match.
    /// </summary>
    private void ApplyFilter(string query)
    {
        _list.Children.Clear();
        _visible.Clear();
        _visibleBorders.Clear();

        var q = query.Trim();
        string? currentGroup = null;
        foreach (var row in _all)
        {
            if (q.Length > 0 && !Contains(row.Title, q) && !Contains(row.SubText, q))
            {
                continue;
            }

            if (!string.Equals(row.Group, currentGroup, StringComparison.Ordinal))
            {
                currentGroup = row.Group;
                _list.Children.Add(BuildGroupHeader(row.Group));
            }

            var border = BuildRow(row);
            _visible.Add(row);
            _visibleBorders.Add(border);
            _list.Children.Add(border);
        }

        if (_visible.Count == 0)
        {
            _list.Children.Add(new TextBlock
            {
                Text = Loc.T("No matching commands"),
                Foreground = new SolidColorBrush(MutedFg),
                Margin = new Thickness(6, 6, 6, 6),
            });
            _selectedIndex = -1;
            return;
        }

        SetSelectedIndex(0);
    }

    private static bool Contains(string haystack, string needle) =>
        haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;

    private void MoveSelection(int delta)
    {
        if (_visible.Count == 0)
        {
            return;
        }
        var next = _selectedIndex < 0
            ? (delta > 0 ? 0 : _visible.Count - 1)
            : _selectedIndex + delta;
        SetSelectedIndex(Math.Clamp(next, 0, _visible.Count - 1));
    }

    private void SetSelectedIndex(int index)
    {
        _selectedIndex = index;
        for (var i = 0; i < _visibleBorders.Count; i++)
        {
            _visibleBorders[i].Background = i == _selectedIndex
                ? new SolidColorBrush(SelectionBg)
                : Brushes.Transparent;
        }
        if (_selectedIndex >= 0 && _selectedIndex < _visibleBorders.Count)
        {
            _visibleBorders[_selectedIndex].BringIntoView();
        }
    }

    private static Grid MakeRowGrid()
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "cp_key" });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        return g;
    }

    private Border BuildGroupHeader(string text) => new()
    {
        Child = new TextBlock
        {
            Text = text,
            Foreground = new SolidColorBrush(AccentFg),
            FontWeight = FontWeights.Bold,
        },
        Padding = new Thickness(6, 10, 6, 4),
    };

    private Border BuildRow(PaletteRow row)
    {
        var grid = MakeRowGrid();

        // Left column: the shortcut key, aligned across rows via the shared-size
        // group so every key lines up. Unbound rows show a dim placeholder so the
        // title column starts at the same x instead of shifting left.
        var hasKey = !string.IsNullOrWhiteSpace(row.KeyText);
        var key = new TextBlock
        {
            Text = hasKey ? row.KeyText : "·",
            Foreground = new SolidColorBrush(hasKey ? AccentFg : MutedFg),
            FontWeight = hasKey ? FontWeights.SemiBold : FontWeights.Normal,
            Margin = new Thickness(0, 0, 18, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(key, 0);
        grid.Children.Add(key);

        // Right column: title with a dimmed sub (id / run preview) on the same line.
        var label = new TextBlock
        {
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        label.Inlines.Add(new System.Windows.Documents.Run(row.Title) { Foreground = Foreground });
        if (!string.IsNullOrWhiteSpace(row.SubText))
        {
            label.Inlines.Add(new System.Windows.Documents.Run("   " + row.SubText)
            {
                Foreground = new SolidColorBrush(MutedFg),
            });
        }
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);

        return new Border
        {
            Child = grid,
            Padding = new Thickness(6, 4, 6, 4),
            CornerRadius = new CornerRadius(3),
            Background = Brushes.Transparent,
        };
    }
}
