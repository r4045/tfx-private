using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Tfx;

/// <summary>One row of the Vi-mode cheat sheet.</summary>
/// <param name="Group">Section header this row falls under.</param>
/// <param name="Key">The Vi key, e.g. "j" or "G".</param>
/// <param name="Description">What the key does.</param>
public sealed record ViCheatSheetEntry(string Group, string Key, string Description);

/// <summary>
/// Read-only cheat sheet of the Vi move-mode keys (j/k/h/l, g/G, a/n, c/x/p),
/// opened by the viCheatSheet shortcut (Shift+F1 by default) and dismissed with
/// Enter or Esc. Unlike the general feature list, these keys are fixed rather
/// than rebindable, so the layout is a plain key → action reference with no
/// "(unbound)" column and no config-action-id column. The rows are supplied by
/// the caller from ViKeyCatalog (MainWindow.Keyboard.cs), which is kept in sync
/// with the actual Vi key switch. Built programmatically in the same dark theme
/// as ShortcutListDialog.
/// </summary>
public sealed class ViCheatSheetDialog : Window
{
    private static readonly Color MutedFg = Color.FromRgb(143, 155, 168);
    private static readonly Color AccentFg = Color.FromRgb(126, 211, 164);
    private static readonly Color BorderColor = Color.FromRgb(47, 58, 67);

    public ViCheatSheetDialog(IReadOnlyList<ViCheatSheetEntry> entries)
    {
        Title = Loc.T("Vi mode keys");
        Owner = Application.Current.MainWindow;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        MinWidth = 360;
        MaxWidth = 720;
        MaxHeight = 720;
        Background = new SolidColorBrush(Color.FromRgb(15, 19, 23));
        Foreground = new SolidColorBrush(Color.FromRgb(222, 230, 236));
        FontFamily = new FontFamily("Consolas, Yu Gothic UI");

        var root = new DockPanel { Margin = new Thickness(16) };

        var hint = new TextBlock
        {
            Text = Loc.T("Close (Enter)"),
            Foreground = new SolidColorBrush(MutedFg),
            Margin = new Thickness(2, 12, 2, 0),
        };
        DockPanel.SetDock(hint, Dock.Bottom);
        root.Children.Add(hint);

        var table = new StackPanel();
        Grid.SetIsSharedSizeScope(table, true);

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
            MaxHeight = 620,
            Content = table,
        });

        Content = root;

        Loaded += (_, _) => Focus();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter || e.Key == Key.Escape)
            {
                Close();
                e.Handled = true;
            }
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
        Margin = new Thickness(0, 0, 0, 2),
        BorderBrush = new SolidColorBrush(BorderColor),
        BorderThickness = new Thickness(0, 0, 0, 1),
    };

    private Border BuildDataRow(ViCheatSheetEntry entry)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "vi_key" });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "vi_desc" });

        var key = new TextBlock
        {
            Text = entry.Key,
            Foreground = new SolidColorBrush(AccentFg),
            FontWeight = FontWeights.SemiBold,
            MinWidth = 40,
            Margin = new Thickness(0, 0, 24, 0),
        };
        Grid.SetColumn(key, 0);
        grid.Children.Add(key);

        var desc = new TextBlock
        {
            Text = entry.Description,
            Foreground = Foreground,
        };
        Grid.SetColumn(desc, 1);
        grid.Children.Add(desc);

        return new Border
        {
            Child = grid,
            Padding = new Thickness(6, 3, 6, 3),
        };
    }
}
