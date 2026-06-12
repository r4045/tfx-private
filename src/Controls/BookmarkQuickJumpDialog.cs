using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Path = System.IO.Path;

namespace Tfx;

/// <summary>
/// Keyboard-driven quick jump across every bookmark (the openBookmarkDialog
/// command, F8 by default). Each bookmark gets a two-letter mnemonic: the first
/// letter is its group's position among non-empty groups (a, b, c …) and the
/// second is the bookmark's position within that group (a, b, c …). Pressing the
/// first letter narrows to that group; pressing the second navigates the active
/// pane to that folder and closes the dialog. Esc closes without navigating;
/// Backspace clears a half-typed key. As an alternative to the mnemonics, the
/// ↑/↓ keys move a row cursor and Enter opens the highlighted row.
///
/// The mnemonics are POSITIONAL: they shift when groups or bookmarks are
/// reordered, or when an entry is inserted/removed ahead of others in
/// bookmarks.json. Appending is stable; inserting in the middle is not.
/// </summary>
public sealed class BookmarkQuickJumpDialog : Window
{
    private sealed class Row
    {
        public string Key = "";
        public string Path = "";
        public Border Container = null!;
    }

    private static readonly Color HighlightBg = Color.FromRgb(38, 56, 69);
    private static readonly Color SelectionBg = Color.FromRgb(54, 80, 98);
    private static readonly Color AccentFg = Color.FromRgb(126, 211, 164);
    private static readonly Color MutedFg = Color.FromRgb(143, 155, 168);
    private static readonly Color BorderColor = Color.FromRgb(47, 58, 67);

    private readonly List<Row> _rows = [];
    private readonly TextBlock _hint;
    private string _pending = "";
    private int _selectedIndex = -1;

    /// <summary>The chosen folder, or null when the dialog was cancelled.</summary>
    public string? SelectedPath { get; private set; }

    public BookmarkQuickJumpDialog(BookmarkStore store)
    {
        Title = Loc.T("Jump to bookmark");
        Owner = Application.Current.MainWindow;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        MinWidth = 560;
        MaxWidth = 1100;
        MaxHeight = 640;
        Background = new SolidColorBrush(Color.FromRgb(15, 19, 23));
        Foreground = new SolidColorBrush(Color.FromRgb(222, 230, 236));
        FontFamily = new FontFamily("Consolas, Yu Gothic UI");

        // Turn the IME off for this window so the two-letter mnemonics arrive as
        // plain Latin keys instead of being swallowed by an active composition.
        InputMethod.SetIsInputMethodEnabled(this, false);

        var root = new DockPanel { Margin = new Thickness(16) };

        _hint = new TextBlock
        {
            Foreground = new SolidColorBrush(MutedFg),
            Margin = new Thickness(2, 12, 2, 0),
        };
        DockPanel.SetDock(_hint, Dock.Bottom);
        root.Children.Add(_hint);

        var table = new StackPanel();
        Grid.SetIsSharedSizeScope(table, true);
        table.Children.Add(BuildHeaderRow());
        foreach (var (row, container) in BuildRows(store))
        {
            _rows.Add(row);
            table.Children.Add(container);
        }

        root.Children.Add(new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = 540,
            Content = table,
        });

        Content = root;
        UpdateHint();

        Loaded += (_, _) =>
        {
            Focus();
            if (_rows.Count > 0)
            {
                SetSelectedIndex(0);
            }
        };
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private IEnumerable<(Row Row, Border Container)> BuildRows(BookmarkStore store)
    {
        var groups = store.Groups.Where(g => g.Bookmarks.Count > 0).ToList();
        for (var gi = 0; gi < groups.Count; gi++)
        {
            var group = groups[gi];
            var groupLetter = gi < 26 ? ((char)('a' + gi)).ToString() : "";
            for (var bi = 0; bi < group.Bookmarks.Count; bi++)
            {
                var entry = group.Bookmarks[bi];
                // Past 26 groups or 26 entries we run out of letters; those rows
                // still display but carry an empty key (no keyboard mnemonic).
                var key = groupLetter.Length == 1 && bi < 26
                    ? groupLetter + (char)('a' + bi)
                    : "";
                var alias = string.IsNullOrWhiteSpace(entry.Label)
                    ? FolderNameOf(entry.Path)
                    : entry.Label;

                var row = new Row { Key = key, Path = entry.Path };
                row.Container = BuildDataRow(key, group.Name, alias, entry.Path);
                yield return (row, row.Container);
            }
        }
    }

    private static string FolderNameOf(string path)
    {
        var name = Path.GetFileName(path.TrimEnd('\\', '/'));
        return string.IsNullOrEmpty(name) ? path : name;
    }

    private Border BuildHeaderRow()
    {
        var grid = MakeRowGrid();
        grid.Children.Add(HeaderCell(Loc.T("Key"), 0));
        grid.Children.Add(HeaderCell(Loc.T("Group"), 1));
        grid.Children.Add(HeaderCell(Loc.T("Alias"), 2));
        grid.Children.Add(HeaderCell(Loc.T("Path"), 3));
        return new Border
        {
            Child = grid,
            Padding = new Thickness(6, 2, 6, 6),
            Margin = new Thickness(0, 0, 0, 4),
            BorderBrush = new SolidColorBrush(BorderColor),
            BorderThickness = new Thickness(0, 0, 0, 1),
        };
    }

    private Border BuildDataRow(string key, string group, string alias, string path)
    {
        var grid = MakeRowGrid();

        var keyCell = new TextBlock
        {
            Text = key.Length == 2 ? key : "·",
            Foreground = new SolidColorBrush(key.Length == 2 ? AccentFg : MutedFg),
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 18, 0),
        };
        Grid.SetColumn(keyCell, 0);
        grid.Children.Add(keyCell);

        grid.Children.Add(Cell(group, 1, Foreground));
        grid.Children.Add(Cell(alias, 2, Foreground));

        var pathCell = Cell(path, 3, new SolidColorBrush(MutedFg));
        pathCell.Margin = new Thickness(0);
        pathCell.MaxWidth = 560;
        pathCell.TextTrimming = TextTrimming.CharacterEllipsis;
        pathCell.ToolTip = path;
        grid.Children.Add(pathCell);

        return new Border
        {
            Child = grid,
            Padding = new Thickness(6, 3, 6, 3),
            CornerRadius = new CornerRadius(3),
            Background = Brushes.Transparent,
        };
    }

    private static Grid MakeRowGrid()
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "bk_key" });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "bk_group" });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "bk_alias" });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "bk_path" });
        return g;
    }

    private static TextBlock HeaderCell(string text, int column)
    {
        var tb = new TextBlock
        {
            Text = text,
            Foreground = new SolidColorBrush(MutedFg),
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 18, 0),
        };
        Grid.SetColumn(tb, column);
        return tb;
    }

    private static TextBlock Cell(string text, int column, Brush foreground)
    {
        var tb = new TextBlock
        {
            Text = text,
            Foreground = foreground,
            Margin = new Thickness(0, 0, 18, 0),
        };
        Grid.SetColumn(tb, column);
        return tb;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                SelectedPath = null;
                DialogResult = false;
                e.Handled = true;
                return;
            case Key.Back:
                if (_pending.Length > 0)
                {
                    _pending = "";
                    UpdateHighlight();
                    UpdateHint();
                }
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
                if (_selectedIndex >= 0 && _selectedIndex < _rows.Count)
                {
                    SelectedPath = _rows[_selectedIndex].Path;
                    DialogResult = true;
                }
                e.Handled = true;
                return;
        }

        if (!TryLetter(e, out var c))
        {
            return; // ignore non-letter keys (Left/Right, PageUp/Down reach the ScrollViewer)
        }
        e.Handled = true;

        var candidate = _pending + c;
        if (_rows.Any(r => r.Key.Length == 2 && r.Key.StartsWith(candidate, StringComparison.Ordinal)))
        {
            _pending = candidate;
        }
        else if (_rows.Any(r => r.Key.Length == 2 && r.Key[0] == c))
        {
            // The combination doesn't exist — treat this key as a fresh first
            // letter so a mistyped group letter can simply be corrected.
            _pending = c.ToString();
        }
        else
        {
            return; // not a valid group letter at all; keep current state
        }

        var exact = _rows.FirstOrDefault(r => r.Key == _pending);
        if (_pending.Length == 2 && exact is not null)
        {
            SelectedPath = exact.Path;
            DialogResult = true;
            return;
        }

        UpdateHighlight();
        UpdateHint();
    }

    private static bool TryLetter(KeyEventArgs e, out char c)
    {
        var key = e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;
        if (key is >= Key.A and <= Key.Z)
        {
            c = (char)('a' + (key - Key.A));
            return true;
        }
        c = '\0';
        return false;
    }

    private void MoveSelection(int delta)
    {
        if (_rows.Count == 0)
        {
            return;
        }
        // Arrow keys switch from mnemonic mode to browse mode: drop any
        // half-typed key so the dimming clears and the cursor stands alone.
        if (_pending.Length > 0)
        {
            _pending = "";
            UpdateHint();
        }
        var next = _selectedIndex < 0
            ? (delta > 0 ? 0 : _rows.Count - 1)
            : _selectedIndex + delta;
        SetSelectedIndex(next);
    }

    private void SetSelectedIndex(int index)
    {
        if (_rows.Count == 0)
        {
            _selectedIndex = -1;
            return;
        }
        _selectedIndex = Math.Clamp(index, 0, _rows.Count - 1);
        UpdateHighlight();
        _rows[_selectedIndex].Container.BringIntoView();
    }

    private void UpdateHighlight()
    {
        for (var i = 0; i < _rows.Count; i++)
        {
            var row = _rows[i];
            var match = _pending.Length == 0 || row.Key.StartsWith(_pending, StringComparison.Ordinal);
            row.Container.Opacity = match ? 1.0 : 0.32;

            if (i == _selectedIndex)
            {
                // The arrow-key cursor wins over the mnemonic group highlight so
                // the active row stays unambiguous, even mid-typing.
                row.Container.Background = new SolidColorBrush(SelectionBg);
            }
            else
            {
                row.Container.Background = _pending.Length == 1 && match
                    ? new SolidColorBrush(HighlightBg)
                    : Brushes.Transparent;
            }
        }
    }

    private void UpdateHint()
    {
        if (_rows.Count == 0)
        {
            _hint.Text = Loc.T("No bookmarks yet.");
            return;
        }
        _hint.Text = _pending.Length == 0
            ? Loc.T("2-letter key or ↑↓ Enter to jump · Esc to close")
            : Loc.F("Pending: {0}_ · Esc to close", _pending);
    }
}
