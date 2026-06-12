using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Path = System.IO.Path;

namespace Tfx;

public partial class MainWindow
{
    private void Navigate(DataGrid grid, string path, bool pushHistory, string? selectName = "..")
    {
        if (!IsNavigablePath(path))
        {
            SetStatus(Loc.F("Folder not found: {0}", path));
            return;
        }

        // Navigating cancels any in-flight subfolder search and clears the
        // search box so the new folder shows real contents.
        if (_subfolderSearchActive)
        {
            CancelSubfolderSearch();
            SearchBox.Text = "";
        }

        var pane0 = PaneOf(grid);
        var tab = ActiveTab(pane0);
        var current = GetCurrentPath(grid);
        if (pushHistory && IsNavigablePath(current) && !string.Equals(current, path, StringComparison.OrdinalIgnoreCase))
        {
            tab.Back.Add(current);
            tab.Forward.Clear();
        }

        tab.Path = path;
        if (grid == LeftGrid)
        {
            _leftPath = path;
        }
        else
        {
            _rightPath = path;
        }
        RebuildTabStrip(pane0);

        Reload(grid, selectName);
        UpdatePathText();
        if (grid == _activeGrid)
        {
            QueueFolderTreeSyncToActivePane();
        }
        UpdateWatcherForPane(PaneOf(grid));
        RefreshGitStatusForPane(PaneOf(grid));
        SaveSettings();
    }

    private async void Reload(DataGrid grid, string? selectName = null)
    {
        var pane = PaneOf(grid);
        var path = GetCurrentPath(grid);
        var target = ItemsOf(pane);
        target.Clear();
        var loadLargeIcons = _settings.ViewMode == ViewMode.Icons;
        var loadSmallIcons = !loadLargeIcons;
        var options = new DirectoryLoadOptions(
            ShowHidden,
            loadSmallIcons,
            loadLargeIcons,
            IsFileColumnVisible("Owner"));
        SetPendingSelectionName(pane, selectName);
        var cts = ReplaceReloadToken(pane);

        try
        {
            var items = await Task.Run(() => DirectoryLoader.Load(path, options, cts.Token), cts.Token);
            if (cts.IsCancellationRequested || !string.Equals(GetCurrentPath(grid), path, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            target.Clear();
            const int batchSize = 200;
            for (var i = 0; i < items.Count; i++)
            {
                target.Add(items[i]);
                if ((i + 1) % batchSize == 0 && i + 1 < items.Count)
                {
                    await Dispatcher.Yield(DispatcherPriority.Background);
                    if (cts.IsCancellationRequested || !string.Equals(GetCurrentPath(grid), path, StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }
                }
            }

            ApplyPendingSelection(grid, pane);
            ApplyGitBadges(pane);

            // First successful reload of the left pane after startup: force
            // focus onto the ".." row (or the first entry at a drive root)
            // so the user can immediately use Up / Down. This is the reliable
            // event-driven path; the Loaded handler in MainWindow.xaml.cs is
            // a belt-and-braces backup.
            if (!_initialLeftFocusDone && grid == LeftGrid && grid.Items.Count > 0)
            {
                _initialLeftFocusDone = true;
                if (grid.SelectedItem is null)
                {
                    grid.SelectedIndex = 0;
                }
                FocusPane(Pane.Left);
            }

            UpdateStatus();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
        }
    }

    private CancellationTokenSource ReplaceReloadToken(Pane pane)
    {
        var next = new CancellationTokenSource();
        var previous = pane == Pane.Left ? _leftReloadCts : _rightReloadCts;
        previous?.Cancel();
        previous?.Dispose();

        if (pane == Pane.Left)
        {
            _leftReloadCts = next;
        }
        else
        {
            _rightReloadCts = next;
        }

        return next;
    }

    private bool IsFileColumnVisible(string id) =>
        _settings.VisibleFileColumns.Any(column => string.Equals(column, id, StringComparison.OrdinalIgnoreCase));

    private void SetPendingSelectionName(Pane pane, string? name)
    {
        if (pane == Pane.Left)
        {
            _leftPendingSelectionName = name;
        }
        else
        {
            _rightPendingSelectionName = name;
        }
    }

    private string? TakePendingSelectionName(Pane pane)
    {
        if (pane == Pane.Left)
        {
            var value = _leftPendingSelectionName;
            _leftPendingSelectionName = null;
            return value;
        }

        var rightValue = _rightPendingSelectionName;
        _rightPendingSelectionName = null;
        return rightValue;
    }

    private void ApplyPendingSelection(DataGrid grid, Pane pane)
    {
        var name = TakePendingSelectionName(pane);
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var source = ItemsOf(pane);
        var item = source.FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.CurrentCultureIgnoreCase));
        if (item is null)
        {
            return;
        }

        var iconView = IconViewOf(pane);
        _syncingSelection = true;
        try
        {
            grid.SelectedItems.Clear();
            grid.SelectedItem = item;
            grid.ScrollIntoView(item);

            iconView.SelectedItems.Clear();
            iconView.SelectedItem = item;
            iconView.ScrollIntoView(item);
        }
        finally
        {
            _syncingSelection = false;
        }

        if (grid == _activeGrid)
        {
            FocusSelectedListingItem(grid, iconView, item);
            SchedulePreviewUpdate(item);
        }
    }

    private void FocusSelectedListingItem(DataGrid grid, ListBox iconView, FileItem item)
    {
        var listing = _settings.ViewMode == ViewMode.Icons ? (Control)iconView : grid;
        listing.Focus();

        QueueSelectedListingItemFocus(grid, iconView, item, DispatcherPriority.Input);
        QueueSelectedListingItemFocus(grid, iconView, item, DispatcherPriority.ContextIdle);
        QueueSelectedListingItemFocus(grid, iconView, item, DispatcherPriority.ApplicationIdle);
    }

    private void QueueSelectedListingItemFocus(DataGrid grid, ListBox iconView, FileItem item, DispatcherPriority priority)
    {
        Dispatcher.BeginInvoke(() => FocusSelectedListingItemNow(grid, iconView, item), priority);
    }

    private void FocusSelectedListingItemNow(DataGrid grid, ListBox iconView, FileItem item)
    {
        if (grid != _activeGrid)
        {
            return;
        }

        if (_settings.ViewMode == ViewMode.Icons)
        {
            iconView.UpdateLayout();
            if (iconView.ItemContainerGenerator.ContainerFromItem(item) is ListBoxItem listBoxItem)
            {
                FocusElement(listBoxItem);
                return;
            }

            FocusElement(iconView);
            return;
        }

        grid.UpdateLayout();
        if (grid.ItemContainerGenerator.ContainerFromItem(item) is DataGridRow row)
        {
            if (FocusFileNameCell(grid, row, item))
            {
                return;
            }

            FocusElement(row);
            return;
        }

        FocusElement(grid);
    }

    private bool FocusFileNameCell(DataGrid grid, DataGridRow row, FileItem item)
    {
        var nameColumn = grid == LeftGrid ? LeftNameColumn : RightNameColumn;
        grid.CurrentCell = new DataGridCellInfo(item, nameColumn);
        grid.ScrollIntoView(item, nameColumn);
        row.ApplyTemplate();

        var presenter = FindVisualChild<DataGridCellsPresenter>(row);
        if (presenter is null)
        {
            grid.UpdateLayout();
            presenter = FindVisualChild<DataGridCellsPresenter>(row);
        }

        if (presenter?.ItemContainerGenerator.ContainerFromIndex(nameColumn.DisplayIndex) is not DataGridCell cell)
        {
            return false;
        }

        cell.IsSelected = true;
        FocusElement(cell);
        return Keyboard.FocusedElement == cell;
    }

    private void FocusActiveListing()
    {
        var iconView = IconViewOf(ActivePane);
        var selected = _settings.ViewMode == ViewMode.Icons
            ? iconView.SelectedItem as FileItem
            : _activeGrid.SelectedItem as FileItem;
        if (selected is not null)
        {
            FocusSelectedListingItemNow(_activeGrid, iconView, selected);
            return;
        }

        FocusElement(_settings.ViewMode == ViewMode.Icons ? iconView : _activeGrid);
    }

    private void MoveActiveListingSelection(Key key) =>
        MoveActiveListingSelection(key, ModifierKeys.None);

    private void MoveActiveListingSelection(Key key, ModifierKeys modifiers)
    {
        var iconView = IconViewOf(ActivePane);
        var icons = _settings.ViewMode == ViewMode.Icons;
        var items = icons ? iconView.Items : _activeGrid.Items;
        if (items.Count == 0)
        {
            FocusActiveListing();
            return;
        }

        var lastIndex = items.Count - 1;
        // Index of the first non-".." entry. Range selection never includes the
        // parent row (every selection consumer already filters IsParent).
        var firstSelectable = items[0] is FileItem first && first.IsParent ? Math.Min(1, lastIndex) : 0;

        // The "lead" is the moving end of keyboard navigation. Resolve it from the
        // tracked item (survives re-sorts) and fall back to the current selection.
        var current = icons ? iconView.SelectedItem : _activeGrid.SelectedItem;
        var leadIndex = IndexOfItem(items, _listingLeadItem);
        if (leadIndex < 0)
        {
            leadIndex = IndexOfItem(items, current as FileItem);
        }

        var step = key switch
        {
            Key.Up => -1,
            Key.PageUp => -10,
            Key.PageDown => 10,
            _ => 1, // Down
        };

        if (modifiers.HasFlag(ModifierKeys.Shift))
        {
            ExtendActiveListingSelection(icons, items, leadIndex, firstSelectable, lastIndex, step);
            return;
        }

        if (modifiers.HasFlag(ModifierKeys.Control))
        {
            MoveActiveListingLead(icons, items, leadIndex, firstSelectable, lastIndex, step);
            return;
        }

        int nextIndex;
        if (leadIndex < 0)
        {
            // No selection (e.g. just after a rename or focus loss): land on
            // the first item (which is always the ".." parent row when one
            // exists).
            nextIndex = 0;
        }
        else if (key == Key.Up && leadIndex == 0)
        {
            // ".." + Up wraps to the bottom of the listing.
            nextIndex = lastIndex;
        }
        else if (key == Key.Down && leadIndex == lastIndex)
        {
            // Last entry + Down wraps to "..".
            nextIndex = 0;
        }
        else
        {
            nextIndex = Math.Clamp(leadIndex + step, 0, lastIndex);
        }

        if (items[nextIndex] is not FileItem item)
        {
            FocusActiveListing();
            return;
        }

        _syncingSelection = true;
        try
        {
            _activeGrid.SelectedItems.Clear();
            _activeGrid.SelectedItem = item;
            _activeGrid.ScrollIntoView(item);

            iconView.SelectedItems.Clear();
            iconView.SelectedItem = item;
            iconView.ScrollIntoView(item);
        }
        finally
        {
            _syncingSelection = false;
        }

        // A modifier-free move re-anchors: a following Shift+Arrow grows the
        // range from here (Explorer behaviour).
        _listingAnchorItem = item;
        _listingLeadItem = item;

        FocusSelectedListingItemNow(_activeGrid, iconView, item);
        SchedulePreviewUpdate(item);
        UpdateStatus();
    }

    /// <summary>
    /// Shift+Arrow contiguous range selection, Explorer style: a fixed anchor
    /// plus a moving lead, no wrap-around, parent ("..") row excluded. Keeps the
    /// grid and icon view selections in sync so a view-mode switch is seamless.
    /// </summary>
    private void ExtendActiveListingSelection(bool icons, ItemCollection items, int leadIndex, int firstSelectable, int lastIndex, int step)
    {
        var iconView = IconViewOf(ActivePane);

        // Anchor the range. If the stored anchor was lost (a mouse click moved
        // the selection elsewhere, or the folder reloaded) re-anchor on the
        // current lead so the range starts where the user actually is.
        var anchorIndex = IndexOfItem(items, _listingAnchorItem);
        var anchorSelected = _listingAnchorItem is not null &&
            (icons ? iconView.SelectedItems : _activeGrid.SelectedItems).Contains(_listingAnchorItem);
        if (anchorIndex < 0 || !anchorSelected)
        {
            anchorIndex = leadIndex < 0 ? firstSelectable : Math.Clamp(leadIndex, firstSelectable, lastIndex);
            _listingAnchorItem = items[anchorIndex] as FileItem;
        }

        var baseIndex = leadIndex < 0 ? anchorIndex : leadIndex;
        var newLead = Math.Clamp(baseIndex + step, firstSelectable, lastIndex);

        var lo = Math.Min(anchorIndex, newLead);
        var hi = Math.Max(anchorIndex, newLead);

        _syncingSelection = true;
        try
        {
            _activeGrid.SelectedItems.Clear();
            iconView.SelectedItems.Clear();
            for (var i = lo; i <= hi; i++)
            {
                if (items[i] is not FileItem fi || fi.IsParent)
                {
                    continue;
                }
                _activeGrid.SelectedItems.Add(fi);
                iconView.SelectedItems.Add(fi);
            }

            if (items[newLead] is FileItem leadItem)
            {
                _activeGrid.ScrollIntoView(leadItem);
                iconView.ScrollIntoView(leadItem);
            }
        }
        finally
        {
            _syncingSelection = false;
        }

        _listingLeadItem = items[newLead] as FileItem;
        if (_listingLeadItem is not null)
        {
            FocusListingItemContainer(icons, _listingLeadItem);
        }

        SchedulePreviewUpdate((icons ? iconView.SelectedItems : _activeGrid.SelectedItems).OfType<FileItem>());
        UpdateStatus();
    }

    /// <summary>
    /// Moves the keyboard focus rectangle (DataGrid current cell / icon
    /// container) onto <paramref name="item"/> without touching the selection
    /// set. Used as the moving lead of a Shift range so the focused row is the
    /// one the next Shift+Arrow extends from.
    /// </summary>
    private void FocusListingItemContainer(bool icons, FileItem item)
    {
        if (icons)
        {
            var iconView = IconViewOf(ActivePane);
            iconView.UpdateLayout();
            if (iconView.ItemContainerGenerator.ContainerFromItem(item) is ListBoxItem container)
            {
                FocusElement(container);
                return;
            }
            FocusElement(iconView);
            return;
        }

        var grid = _activeGrid;
        var nameColumn = grid == LeftGrid ? LeftNameColumn : RightNameColumn;
        grid.CurrentCell = new DataGridCellInfo(item, nameColumn);
        grid.ScrollIntoView(item, nameColumn);
        grid.UpdateLayout();
        if (grid.ItemContainerGenerator.ContainerFromItem(item) is DataGridRow row)
        {
            var presenter = FindVisualChild<DataGridCellsPresenter>(row);
            if (presenter?.ItemContainerGenerator.ContainerFromIndex(nameColumn.DisplayIndex) is DataGridCell cell)
            {
                FocusElement(cell);
                return;
            }
            FocusElement(row);
            return;
        }
        FocusElement(grid);
    }

    private static int IndexOfItem(ItemCollection items, FileItem? item)
    {
        if (item is null)
        {
            return -1;
        }
        for (var i = 0; i < items.Count; i++)
        {
            if (ReferenceEquals(items[i], item))
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>
    /// Ctrl+Arrow: moves the keyboard focus (lead) without touching the
    /// selection, Explorer style. Pair with Ctrl+Space to toggle the focused
    /// item. The ".." row is skipped (it is never selectable).
    /// </summary>
    private void MoveActiveListingLead(bool icons, ItemCollection items, int leadIndex, int firstSelectable, int lastIndex, int step)
    {
        var baseIndex = leadIndex < 0 ? firstSelectable : Math.Clamp(leadIndex, firstSelectable, lastIndex);
        var newLead = Math.Clamp(baseIndex + step, firstSelectable, lastIndex);
        if (items[newLead] is not FileItem item)
        {
            return;
        }

        _listingLeadItem = item;
        if (icons)
        {
            IconViewOf(ActivePane).ScrollIntoView(item);
        }
        else
        {
            _activeGrid.ScrollIntoView(item);
        }
        FocusListingItemContainer(icons, item);
    }

    /// <summary>
    /// Ctrl+Space: toggles the focused (lead) item in/out of the selection set
    /// and re-anchors the range there, mirroring Explorer.
    /// </summary>
    private void ToggleActiveListingLeadSelection()
    {
        var icons = _settings.ViewMode == ViewMode.Icons;
        var iconView = IconViewOf(ActivePane);
        var items = icons ? iconView.Items : _activeGrid.Items;

        var leadIndex = IndexOfItem(items, _listingLeadItem);
        if (leadIndex < 0)
        {
            var sel = icons ? iconView.SelectedItem : _activeGrid.SelectedItem;
            leadIndex = IndexOfItem(items, sel as FileItem);
        }
        if (leadIndex < 0 || items[leadIndex] is not FileItem item || item.IsParent)
        {
            return;
        }

        var inGrid = _activeGrid.SelectedItems.Contains(item);
        var inIcon = iconView.SelectedItems.Contains(item);
        var isSelected = icons ? inIcon : inGrid;

        _syncingSelection = true;
        try
        {
            if (isSelected)
            {
                _activeGrid.SelectedItems.Remove(item);
                iconView.SelectedItems.Remove(item);
            }
            else
            {
                if (!inGrid)
                {
                    _activeGrid.SelectedItems.Add(item);
                }
                if (!inIcon)
                {
                    iconView.SelectedItems.Add(item);
                }
            }
        }
        finally
        {
            _syncingSelection = false;
        }

        _listingAnchorItem = item;
        _listingLeadItem = item;
        FocusListingItemContainer(icons, item);
        SchedulePreviewUpdate((icons ? iconView.SelectedItems : _activeGrid.SelectedItems).OfType<FileItem>());
        UpdateStatus();
    }

    /// <summary>
    /// The item the single-target keyboard actions (Enter / F2) should act on:
    /// the lead/focus item when it is still in the listing (it diverges from the
    /// selection only while Ctrl+Arrow is moving the focus), otherwise the
    /// current selection.
    /// </summary>
    private FileItem? ActiveListingCurrentItem()
    {
        var icons = _settings.ViewMode == ViewMode.Icons;
        var items = icons ? IconViewOf(ActivePane).Items : _activeGrid.Items;
        if (IndexOfItem(items, _listingLeadItem) >= 0)
        {
            return _listingLeadItem;
        }
        return icons ? IconViewOf(ActivePane).SelectedItem as FileItem : _activeGrid.SelectedItem as FileItem;
    }

    /// <summary>
    /// Vi g / G: selects the first (toTop) or last item in the active listing,
    /// mirroring the selection/focus/scroll path of MoveActiveListingSelection.
    /// </summary>
    private void MoveActiveListingToEdge(bool toTop)
    {
        var iconView = IconViewOf(ActivePane);
        var items = _settings.ViewMode == ViewMode.Icons ? iconView.Items : _activeGrid.Items;
        if (items.Count == 0)
        {
            FocusActiveListing();
            return;
        }

        var index = toTop ? 0 : items.Count - 1;
        if (items[index] is not FileItem item)
        {
            FocusActiveListing();
            return;
        }

        _syncingSelection = true;
        try
        {
            _activeGrid.SelectedItems.Clear();
            _activeGrid.SelectedItem = item;
            _activeGrid.ScrollIntoView(item);

            iconView.SelectedItems.Clear();
            iconView.SelectedItem = item;
            iconView.ScrollIntoView(item);
        }
        finally
        {
            _syncingSelection = false;
        }

        FocusSelectedListingItemNow(_activeGrid, iconView, item);
        SchedulePreviewUpdate(item);
        UpdateStatus();
    }

    private static void FocusElement(IInputElement element)
    {
        if (element is Control control)
        {
            control.Focus();
            FocusManager.SetFocusedElement(FocusManager.GetFocusScope(control), control);
        }

        Keyboard.Focus(element);
    }

    private void NavigateParent()
    {
        var current = GetCurrentPath(_activeGrid);
        if (ArchivePath.TryParse(current, out var archive, out var inner))
        {
            var parent = ArchivePath.GetParent(current);
            if (string.IsNullOrEmpty(parent))
            {
                return;
            }
            var selectName = string.IsNullOrEmpty(inner)
                ? Path.GetFileName(archive)
                : (inner.TrimEnd('/').Split('/').LastOrDefault() ?? "");
            Navigate(_activeGrid, parent, true, selectName);
            return;
        }

        var parentDir = Directory.GetParent(current);
        if (parentDir is not null)
        {
            Navigate(_activeGrid, parentDir.FullName, true, Path.GetFileName(current.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
        }
    }

    private static bool IsNavigablePath(string path)
    {
        if (ArchivePath.TryParse(path, out var archive, out _))
        {
            return File.Exists(archive);
        }
        return Directory.Exists(path);
    }

    private void NavigateBack()
    {
        var tab = ActiveTab(ActivePane);
        if (tab.Back.Count == 0)
        {
            return;
        }

        var current = GetCurrentPath(_activeGrid);
        var path = tab.Back[^1];
        tab.Back.RemoveAt(tab.Back.Count - 1);
        tab.Forward.Add(current);
        Navigate(_activeGrid, path, false);
    }

    private void NavigateForward()
    {
        var tab = ActiveTab(ActivePane);
        if (tab.Forward.Count == 0)
        {
            return;
        }

        var current = GetCurrentPath(_activeGrid);
        var path = tab.Forward[^1];
        tab.Forward.RemoveAt(tab.Forward.Count - 1);
        tab.Back.Add(current);
        Navigate(_activeGrid, path, false);
    }

    private void Back_Click(object sender, RoutedEventArgs e) => NavigateBack();

    private void Forward_Click(object sender, RoutedEventArgs e) => NavigateForward();

    private void Parent_Click(object sender, RoutedEventArgs e) => NavigateParent();

    private void Reload_Click(object sender, RoutedEventArgs e)
    {
        Reload(LeftGrid);
        Reload(RightGrid);
    }
}
