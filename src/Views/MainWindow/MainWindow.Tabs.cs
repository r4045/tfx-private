using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Path = System.IO.Path;

namespace Tfx;

public partial class MainWindow
{
    // Per-pane tab model. Each pane owns an ordered list of PaneTab plus the
    // index of the active one. The active tab's Path mirrors _leftPath /
    // _rightPath (the existing source of truth used everywhere else), and the
    // active tab's Back / Forward lists replace the former global _back /
    // _forward history (which had the latent bug of sharing one history across
    // both panes).
    private readonly List<PaneTab> _leftTabs = [];
    private readonly List<PaneTab> _rightTabs = [];
    private int _leftActiveTabIndex;
    private int _rightActiveTabIndex;

    private List<PaneTab> TabsOf(Pane pane) => pane == Pane.Left ? _leftTabs : _rightTabs;

    private int ActiveTabIndexOf(Pane pane) => pane == Pane.Left ? _leftActiveTabIndex : _rightActiveTabIndex;

    private void SetActiveTabIndex(Pane pane, int index)
    {
        if (pane == Pane.Left)
        {
            _leftActiveTabIndex = index;
        }
        else
        {
            _rightActiveTabIndex = index;
        }
    }

    /// <summary>
    /// The active tab of a pane, lazily seeding one tab from the pane's current
    /// path if the list is somehow empty (defensive — startup seeds explicitly).
    /// </summary>
    private PaneTab ActiveTab(Pane pane)
    {
        var tabs = TabsOf(pane);
        if (tabs.Count == 0)
        {
            tabs.Add(new PaneTab(PathOf(pane)));
            SetActiveTabIndex(pane, 0);
        }
        var idx = Math.Clamp(ActiveTabIndexOf(pane), 0, tabs.Count - 1);
        SetActiveTabIndex(pane, idx);
        return tabs[idx];
    }

    /// <summary>
    /// Seed exactly one tab per pane from the current _leftPath / _rightPath.
    /// Called once during construction after the initial Navigate calls have
    /// set those paths. Optionally restores additional tabs from settings.
    /// </summary>
    private void InitializeTabs(bool explicitLeftStart = false)
    {
        // When the left pane's folder came from an explicit source (a command-
        // line path or a meaningful working directory), open it as a single
        // fresh tab instead of restoring the saved tab set — otherwise the
        // restored active tab would override the requested startup folder. The
        // right pane always restores its saved tabs. Normal launches (Explorer
        // / Start menu), where the working directory is not meaningful, fall
        // through to the usual saved-tab restore.
        if (explicitLeftStart)
        {
            SeedPaneTabs(Pane.Left, [], 0, [], _leftPath);
        }
        else
        {
            SeedPaneTabs(Pane.Left, _settings.LeftTabs, _settings.LeftActiveTab, _settings.LeftPinnedTabs, _leftPath);
        }

        SeedPaneTabs(Pane.Right, _settings.RightTabs, _settings.RightActiveTab, _settings.RightPinnedTabs, _rightPath);
    }

    private void SeedPaneTabs(Pane pane, List<string> savedPaths, int savedActive, List<int> pinnedIndices, string fallbackPath)
    {
        var tabs = TabsOf(pane);
        tabs.Clear();

        // Restore only paths that still exist; always keep at least one tab. A
        // tab is pinned when its ORIGINAL index (position in the saved list) was
        // pinned — keyed on the source index, not the destination, because
        // unrestorable paths are skipped and would otherwise shift the mapping.
        for (var i = 0; i < savedPaths.Count; i++)
        {
            if (IsPathRestorable(savedPaths[i]))
            {
                tabs.Add(new PaneTab(savedPaths[i]) { Pinned = pinnedIndices.Contains(i) });
            }
        }
        if (tabs.Count == 0)
        {
            tabs.Add(new PaneTab(fallbackPath));
        }

        SetActiveTabIndex(pane, Math.Clamp(savedActive, 0, tabs.Count - 1));

        // Make the active tab's path the live pane path so the first listing
        // matches the restored tab rather than whatever Navigate seeded.
        var active = tabs[ActiveTabIndexOf(pane)];
        var previousPath = PathOf(pane);
        SetPathOf(pane, active.Path);

        // If the restored active tab points somewhere other than what the
        // initial Navigate already loaded (e.g. the saved active folder was
        // deleted and fell back), reload so the listing matches the tab.
        if (!string.Equals(previousPath, active.Path, StringComparison.OrdinalIgnoreCase))
        {
            Reload(GridOf(pane), active.SelectedName ?? "..");
        }

        RebuildTabStrip(pane);
    }

    private void NewTabClick(object sender, System.Windows.RoutedEventArgs e) => NewTabInActivePane();

    /// <summary>
    /// Opens a new tab in the active pane at the same folder as the current
    /// tab and switches to it.
    /// </summary>
    private void NewTabInActivePane()
    {
        var pane = ActivePane;
        // The explicit new-tab command duplicates the current folder on purpose,
        // so it opts out of the same-folder de-duplication below (otherwise it
        // would always just re-focus the current tab and appear to do nothing).
        OpenNewTab(pane, PathOf(pane), focusExisting: false);
    }

    private void OpenNewTab(Pane pane, string path, bool focusExisting = true)
    {
        var tabs = TabsOf(pane);

        // If the folder is already open in a tab of this pane, focus that tab
        // instead of opening a duplicate. Skipped for the explicit new-tab
        // command (focusExisting: false), which is allowed to duplicate.
        if (focusExisting)
        {
            var existing = tabs.FindIndex(t => FsHelpers.SamePath(t.Path, path));
            if (existing >= 0)
            {
                if (existing != ActiveTabIndexOf(pane))
                {
                    RememberActiveTabSelection(pane);
                    ActivateTab(pane, existing);
                }
                return;
            }
        }

        RememberActiveTabSelection(pane);
        // Placement is config-driven ([tabs] newTabPosition): "rightmost"
        // (default) appends so the newest tab is always last and easy to spot;
        // "afterActive" inserts next to the current tab (the former behavior).
        var insertAt = _config.NewTabPosition.Equals("afterActive", StringComparison.OrdinalIgnoreCase)
            ? ActiveTabIndexOf(pane) + 1
            : tabs.Count;
        tabs.Insert(insertAt, new PaneTab(path));
        SetActiveTabIndex(pane, insertAt);
        ActivateTab(pane, insertAt);
    }

    /// <summary>
    /// Middle-clicking a folder in a listing opens it in a new tab (browser-
    /// style) and switches to it. Files and the ".." parent row are ignored.
    /// Wired to both panes' DataGrid and icon view via PreviewMouseDown, so a
    /// single handler covers Details and Icons views.
    /// </summary>
    private void Listing_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle || sender is not DependencyObject view)
        {
            return;
        }
        if (!TryGetListingItem(e.OriginalSource as DependencyObject, out var item)
            || item.IsParent
            || !item.IsDirectory)
        {
            return;
        }

        var grid = SideOf(view);
        UpdateActivePane(grid);
        OpenNewTab(PaneOf(grid), item.FullPath);
        e.Handled = true;
    }

    /// <summary>
    /// Closes the tab at <paramref name="index"/> in <paramref name="pane"/>.
    /// When the last tab of a pane is closed, the pane is hidden (split off);
    /// the left pane never fully disappears — it always keeps one tab.
    /// </summary>
    private void CloseTab(Pane pane, int index)
    {
        var tabs = TabsOf(pane);
        if (index < 0 || index >= tabs.Count)
        {
            return;
        }

        // Pinned tabs never close: Ctrl+W, middle-click and the × button (hidden
        // while pinned) all route here, so one guard covers every path.
        if (tabs[index].Pinned)
        {
            SetStatus(Loc.T("Pinned tab — unpin to close (F6)"));
            return;
        }

        if (tabs.Count == 1)
        {
            // Last tab. The right pane collapses to single-pane view; the left
            // pane has nowhere to go, so closing its last tab is a no-op.
            if (pane == Pane.Right && RightPaneColumn.Width.Value > 0)
            {
                SetSplitVisible(false);
                SaveSettings();
            }
            return;
        }

        tabs.RemoveAt(index);
        var newActive = Math.Clamp(ActiveTabIndexOf(pane) > index
            ? ActiveTabIndexOf(pane) - 1
            : ActiveTabIndexOf(pane), 0, tabs.Count - 1);
        SetActiveTabIndex(pane, newActive);
        ActivateTab(pane, newActive);
    }

    private void CloseActiveTab()
    {
        var pane = ActivePane;
        CloseTab(pane, ActiveTabIndexOf(pane));
    }

    /// <summary>
    /// F6 (toggleTabPin): flips the pinned state of the active pane's active tab.
    /// </summary>
    private void ToggleActiveTabPin()
    {
        var pane = ActivePane;
        ToggleTabPin(pane, ActiveTabIndexOf(pane));
    }

    /// <summary>
    /// Flips a tab's pinned state in place. Pinning never reorders the tab; it
    /// only changes how navigation (new tab vs in-place) and closing behave.
    /// </summary>
    private void ToggleTabPin(Pane pane, int index)
    {
        var tabs = TabsOf(pane);
        if (index < 0 || index >= tabs.Count)
        {
            return;
        }

        var tab = tabs[index];
        tab.Pinned = !tab.Pinned;
        SetStatus(Loc.F(tab.Pinned ? "Pinned tab: {0}" : "Unpinned tab: {0}", TabTitle(tab.Path)));
        RebuildTabStrip(pane);
        SaveSettings();
    }

    /// <summary>Positions of the pinned tabs within <paramref name="tabs"/>, for persistence.</summary>
    private static List<int> PinnedTabIndices(List<PaneTab> tabs)
    {
        var indices = new List<int>();
        for (var i = 0; i < tabs.Count; i++)
        {
            if (tabs[i].Pinned)
            {
                indices.Add(i);
            }
        }
        return indices;
    }

    private void CycleTab(int direction)
    {
        var pane = ActivePane;
        var tabs = TabsOf(pane);
        if (tabs.Count <= 1)
        {
            return;
        }
        var next = (ActiveTabIndexOf(pane) + direction + tabs.Count) % tabs.Count;
        SetActiveTabIndex(pane, next);
        ActivateTab(pane, next);
    }

    /// <summary>
    /// Switches the pane to the tab at <paramref name="index"/>: persists the
    /// outgoing tab's selection, points the pane path at the incoming tab, and
    /// reloads the listing restoring the remembered selection.
    /// </summary>
    private void ActivateTab(Pane pane, int index)
    {
        var tabs = TabsOf(pane);
        if (index < 0 || index >= tabs.Count)
        {
            return;
        }

        SetActiveTabIndex(pane, index);
        var tab = tabs[index];
        SetPathOf(pane, tab.Path);

        var grid = GridOf(pane);
        Reload(grid, tab.SelectedName ?? "..");
        UpdatePathText();
        if (pane == ActivePane)
        {
            QueueFolderTreeSyncToActivePane();
        }
        UpdateWatcherForPane(pane);
        RefreshGitStatusForPane(pane);
        RebuildTabStrip(pane);
        SaveSettings();
    }

    /// <summary>
    /// Saves the current selection's name into the active tab so switching back
    /// later restores it. Called before leaving a tab.
    /// </summary>
    private void RememberActiveTabSelection(Pane pane)
    {
        var grid = GridOf(pane);
        if (grid.SelectedItem is FileItem item && !item.IsParent)
        {
            ActiveTab(pane).SelectedName = item.Name;
        }
    }

    // ─── Tab strip UI ─────────────────────────────────────────────────────

    private ItemsControl TabStripOf(Pane pane) => pane == Pane.Left ? LeftTabStrip : RightTabStrip;

    /// <summary>
    /// Rebuilds the tab chips for a pane. The strip is hidden entirely when the
    /// pane only has one tab, matching the "show only when 2+ tabs" decision.
    /// </summary>
    private void RebuildTabStrip(Pane pane)
    {
        var strip = TabStripOf(pane);
        var tabs = TabsOf(pane);
        strip.Items.Clear();

        if (tabs.Count < 2)
        {
            strip.Visibility = Visibility.Collapsed;
            return;
        }

        strip.Visibility = Visibility.Visible;
        var activeIndex = ActiveTabIndexOf(pane);
        for (var i = 0; i < tabs.Count; i++)
        {
            strip.Items.Add(BuildTabChip(pane, tabs[i], i, i == activeIndex));
        }
    }

    private Border BuildTabChip(Pane pane, PaneTab tab, int index, bool active)
    {
        var fg = (Brush)FindResource("TfxForeground");
        var muted = (Brush)FindResource("TfxMuted");
        var border = (Brush)FindResource("TfxBorder");
        var activeBg = (Brush)FindResource("TfxPanelActive");
        var inactiveBg = (Brush)FindResource("TfxPanel");

        var label = new TextBlock
        {
            Text = TabTitle(tab.Path),
            Foreground = active ? fg : muted,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 140,
            Margin = tab.Pinned ? new Thickness(4, 0, 6, 0) : new Thickness(8, 0, 4, 0)
        };

        var close = new Button
        {
            Content = "", // Segoe Fluent "ChromeClose"
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 9,
            Width = 16,
            Height = 16,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 0, 4, 0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = muted,
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = Loc.T("Close tab (Ctrl+W)")
        };
        close.Click += (_, e) =>
        {
            e.Handled = true;
            CloseTab(pane, index);
        };

        var content = new DockPanel { LastChildFill = false };

        if (tab.Pinned)
        {
            // Pinned: leading pin glyph, no close button (Ctrl+W / middle-click
            // are also suppressed in CloseTab) so the tab reads as fixed.
            var pin = new TextBlock
            {
                Text = "\uE840", // Segoe Fluent "Pinned"
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 10,
                Foreground = active ? fg : muted,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0)
            };
            DockPanel.SetDock(pin, Dock.Left);
            content.Children.Add(pin);
        }
        else
        {
            DockPanel.SetDock(close, Dock.Right);
            content.Children.Add(close);
        }

        DockPanel.SetDock(label, Dock.Left);
        content.Children.Add(label);

        var chip = new Border
        {
            Background = active ? activeBg : inactiveBg,
            BorderBrush = border,
            BorderThickness = new Thickness(1, 1, 1, active ? 0 : 1),
            CornerRadius = new CornerRadius(5, 5, 0, 0),
            Margin = new Thickness(0, 0, 3, 0),
            Padding = new Thickness(2, 3, 2, 3),
            Cursor = Cursors.Hand,
            Child = content,
            ToolTip = tab.Path
        };

        // Right-click toggles pin (label reflects the current state).
        var menu = new ContextMenu();
        var pinItem = new MenuItem { Header = Loc.T(tab.Pinned ? "Unpin tab" : "Pin tab") };
        pinItem.Click += (_, _) => ToggleTabPin(pane, index);
        menu.Items.Add(pinItem);
        chip.ContextMenu = menu;

        chip.MouseLeftButtonUp += (_, _) =>
        {
            if (index != ActiveTabIndexOf(pane))
            {
                RememberActiveTabSelection(pane);
                ActivateTab(pane, index);
            }
            UpdateActivePane(GridOf(pane));
        };
        // Middle-click closes the tab, like a browser.
        chip.MouseDown += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Middle)
            {
                e.Handled = true;
                CloseTab(pane, index);
            }
        };
        return chip;
    }

    private static string TabTitle(string path)
    {
        if (ArchivePath.TryParse(path, out var archive, out var inner))
        {
            var leaf = string.IsNullOrEmpty(inner)
                ? Path.GetFileName(archive)
                : inner.TrimEnd('/').Split('/')[^1];
            return string.IsNullOrEmpty(leaf) ? Path.GetFileName(archive) : leaf;
        }
        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));
        return string.IsNullOrEmpty(name) ? path : name;
    }
}
