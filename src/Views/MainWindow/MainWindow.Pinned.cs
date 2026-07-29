using System.Collections.Specialized;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Path = System.IO.Path;

namespace Tfx;

public partial class MainWindow
{
    // ─── Bookmarks ──────────────────────────────
    // Grouped, collapsible sidebar entries persisted to bookmarks.json
    // (%APPDATA%\tfx). Group collapse state persists in settings.json;
    // clicking a leaf navigates the active pane.

    private void LoadBookmarks()
    {
        var loaded = BookmarkStore.Load(_bookmarksPath);
        if (loaded is null)
        {
            // First run with no bookmarks.json: seed once from the legacy
            // config.toml [[bookmarks]] so existing bookmarks aren't lost, then
            // persist. From here on the JSON file is the source of truth and the
            // config.toml entries are ignored.
            _bookmarks = new BookmarkStore();
            SeedBookmarksFromConfig();
            if (_bookmarks.Groups.Count > 0)
            {
                SaveBookmarks();
            }
        }
        else
        {
            _bookmarks = loaded;
        }

        RenderBookmarks();
    }

    /// <summary>One-time migration: copy config.toml [[bookmarks]] into the new store.</summary>
    private void SeedBookmarksFromConfig()
    {
        var byGroup = new Dictionary<string, BookmarkGroup>(StringComparer.Ordinal);
        foreach (var b in _config.Bookmarks)
        {
            var groupName = string.IsNullOrWhiteSpace(b.Group) ? "Bookmarks" : b.Group;
            if (!byGroup.TryGetValue(groupName, out var group))
            {
                group = new BookmarkGroup { Name = groupName };
                byGroup[groupName] = group;
                _bookmarks.Groups.Add(group);
            }
            group.Bookmarks.Add(new BookmarkEntry { Label = b.Label ?? "", Path = b.Path });
        }
    }

    private void SaveBookmarks()
    {
        try
        {
            _bookmarks.Save(_bookmarksPath);
        }
        catch (Exception ex)
        {
            SetStatus(Loc.F("Failed to save bookmarks: {0}", ex.Message));
        }
    }

    /// <summary>Rebuilds the sidebar TreeView from the in-memory store.</summary>
    private void RenderBookmarks()
    {
        BookmarksTree.Items.Clear();

        foreach (var group in _bookmarks.Groups)
        {
            if (group.Bookmarks.Count == 0)
            {
                continue;
            }
            var groupItem = new TreeViewItem
            {
                Header = group.Name,
                Tag = null,   // null Tag marks a group header (a leaf carries its path)
                IsExpanded = !_settings.CollapsedBookmarkGroups.Contains(group.Name, StringComparer.OrdinalIgnoreCase),
            };
            foreach (var b in group.Bookmarks)
            {
                var label = string.IsNullOrWhiteSpace(b.Label)
                    ? Path.GetFileName(b.Path.TrimEnd('\\', '/'))
                    : b.Label;
                if (string.IsNullOrEmpty(label))
                {
                    label = b.Path;
                }
                groupItem.Items.Add(new TreeViewItem
                {
                    Header = label,
                    Tag = b.Path,
                    ToolTip = b.Path,
                });
            }
            BookmarksTree.Items.Add(groupItem);
        }

        ApplySidebarSections();
    }

    /// <summary>
    /// Applies the left-sidebar section layout. Bookmarks and folders each get a
    /// header that is always shown plus a tree that can be collapsed away
    /// (state in <c>_settings.BookmarksCollapsed</c> / <c>FoldersCollapsed</c>);
    /// the inner GridSplitter appears only when BOTH trees are expanded and
    /// apportions vertical space between them. The bookmarks section is
    /// suppressed entirely when there are no bookmarks. Single source of truth,
    /// called from RenderBookmarks, the header click handlers, and startup.
    /// </summary>
    private void ApplySidebarSections()
    {
        var bookmarksAvailable = _bookmarks.Groups.Any(g => g.Bookmarks.Count > 0);
        var bookmarksShown = bookmarksAvailable && !_settings.BookmarksCollapsed;
        var foldersShown = !_settings.FoldersCollapsed;

        BookmarksHeaderBar.Visibility = bookmarksAvailable ? Visibility.Visible : Visibility.Collapsed;
        BookmarksTree.Visibility = bookmarksShown ? Visibility.Visible : Visibility.Collapsed;
        FolderTree.Visibility = foldersShown ? Visibility.Visible : Visibility.Collapsed;

        BookmarksChevron.Text = bookmarksShown ? "\u25BE" : "\u25B8";
        FoldersChevron.Text = foldersShown ? "\u25BE" : "\u25B8";

        if (bookmarksShown && foldersShown)
        {
            // Both expanded: bookmarks take a fixed (draggable) height, folders
            // take the remainder, splitter live between them.
            var height = _settings.BookmarkSectionHeight >= 60 ? _settings.BookmarkSectionHeight : 200;
            BookmarksRow.MinHeight = 48;
            BookmarksRow.Height = new GridLength(height);
            SidebarInnerSplitterRow.Height = new GridLength(5);
            SidebarInnerSplitter.Visibility = Visibility.Visible;
            FoldersRow.MinHeight = 48;
            FoldersRow.Height = new GridLength(1, GridUnitType.Star);
        }
        else
        {
            // At most one tree is expanded: no splitter; the expanded tree (if
            // any) fills, the other collapses to just its header.
            SidebarInnerSplitterRow.Height = new GridLength(0);
            SidebarInnerSplitter.Visibility = Visibility.Collapsed;
            BookmarksRow.MinHeight = 0;
            FoldersRow.MinHeight = 0;

            if (bookmarksShown)
            {
                BookmarksRow.Height = new GridLength(1, GridUnitType.Star);
                FoldersRow.Height = GridLength.Auto;
            }
            else
            {
                BookmarksRow.Height = GridLength.Auto;
                FoldersRow.Height = foldersShown ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
            }
        }
    }

    private void BookmarksHeader_Click(object sender, MouseButtonEventArgs e)
    {
        // Only meaningful when bookmarks exist; otherwise the header isn't shown.
        if (!_bookmarks.Groups.Any(g => g.Bookmarks.Count > 0))
        {
            return;
        }
        _settings.BookmarksCollapsed = !_settings.BookmarksCollapsed;
        ApplySidebarSections();
        SaveSettings();
    }

    private void FoldersHeader_Click(object sender, MouseButtonEventArgs e)
    {
        _settings.FoldersCollapsed = !_settings.FoldersCollapsed;
        ApplySidebarSections();
        SaveSettings();
    }

    private void SidebarInnerSplitter_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        if (BookmarksRow.ActualHeight > 0)
        {
            _settings.BookmarkSectionHeight = BookmarksRow.ActualHeight;
        }
        SaveSettings();
    }

    /// <summary>
    /// Adds a folder to the bookmarks, prompting for the target group (an
    /// existing name adds into it; a new name creates a group). Persists and
    /// refreshes the sidebar.
    /// </summary>
    private void AddBookmarkForPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || ArchivePath.Contains(path))
        {
            return;
        }

        var defaultGroup = _bookmarks.Groups.LastOrDefault()?.Name ?? "Bookmarks";
        var dialog = new NamePromptDialog(Loc.T("Add bookmark"), Loc.T("Group"), defaultGroup);
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        AddBookmarkEntry(dialog.EnteredText, "", path);
    }

    /// <summary>
    /// Adds an entry to a bookmark group (creating the group if needed), with
    /// de-duplication within the group. Shared by the right-click "Add to
    /// bookmarks..." (group-only) and the addBookmark dialog (group + alias).
    /// </summary>
    private void AddBookmarkEntry(string groupName, string label, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }
        groupName = string.IsNullOrWhiteSpace(groupName) ? "Bookmarks" : groupName.Trim();

        var group = _bookmarks.Groups.FirstOrDefault(g => string.Equals(g.Name, groupName, StringComparison.OrdinalIgnoreCase));
        if (group is null)
        {
            group = new BookmarkGroup { Name = groupName };
            _bookmarks.Groups.Add(group);
        }

        if (group.Bookmarks.Any(b => FsHelpers.SamePath(b.Path, path)))
        {
            SetStatus(Loc.F("Already bookmarked: {0}", path));
            return;
        }

        group.Bookmarks.Add(new BookmarkEntry { Label = label.Trim(), Path = path });
        SaveBookmarks();
        RenderBookmarks();
        SetStatus(Loc.F("Bookmarked {0}", path));
    }

    /// <summary>
    /// The addBookmark command: opens the three-field dialog (group, alias,
    /// path) pre-filled with the active pane's current folder, then registers
    /// it on confirm.
    /// </summary>
    private void AddCurrentFolderBookmarkViaDialog()
    {
        var path = GetCurrentPath(_activeGrid);
        if (string.IsNullOrWhiteSpace(path) || ArchivePath.Contains(path))
        {
            return;
        }

        var defaultGroup = _bookmarks.Groups.LastOrDefault()?.Name ?? "Bookmarks";
        var groupNames = _bookmarks.Groups.Select(g => g.Name).ToList();
        var folderName = Path.GetFileName(path.TrimEnd('\\', '/'));
        if (string.IsNullOrEmpty(folderName))
        {
            folderName = path;
        }
        var dialog = new BookmarkDialog(Loc.T("Add bookmark"), groupNames, defaultGroup, folderName, path);
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var finalPath = string.IsNullOrWhiteSpace(dialog.Path) ? path : dialog.Path;
        AddBookmarkEntry(dialog.Group, dialog.Alias, finalPath);
    }

    /// <summary>
    /// Edits one existing bookmark, reached with Ctrl on the committing keystroke
    /// in the quick-jump dialog (Ctrl+Enter, or Ctrl + the second mnemonic
    /// letter). Reopens the three-field dialog pre-filled from
    /// <paramref name="entry"/>.
    ///
    /// Confirming rewrites the entry in place. Changing the group MOVES it to the
    /// END of the target group (creating the group when the name is new) and
    /// drops the source group once it empties — the same placement a freshly
    /// added bookmark gets in <see cref="AddBookmarkEntry"/>, so edit and add
    /// never disagree about where an entry lands.
    /// </summary>
    private void EditBookmarkEntry(BookmarkGroup sourceGroup, BookmarkEntry entry)
    {
        var groupNames = _bookmarks.Groups.Select(g => g.Name).ToList();
        var dialog = new BookmarkDialog(
            Loc.T("Edit bookmark"), groupNames, sourceGroup.Name, entry.Label, entry.Path, Loc.T("Save"));
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        // A blank field means "leave it alone" rather than "clear it" — except
        // the alias, where blank is a real value (fall back to the folder name).
        var newPath = string.IsNullOrWhiteSpace(dialog.Path) ? entry.Path : dialog.Path;
        var newGroupName = string.IsNullOrWhiteSpace(dialog.Group) ? sourceGroup.Name : dialog.Group;

        var target = _bookmarks.Groups.FirstOrDefault(
            g => string.Equals(g.Name, newGroupName, StringComparison.OrdinalIgnoreCase));

        // Same one-path-per-group rule adding enforces. The entry being edited is
        // excluded from the check so re-saving it unchanged (an alias-only edit)
        // isn't rejected as a duplicate of itself.
        if (target is not null &&
            target.Bookmarks.Any(b => !ReferenceEquals(b, entry) && FsHelpers.SamePath(b.Path, newPath)))
        {
            SetStatus(Loc.F("Already bookmarked: {0}", newPath));
            return;
        }

        entry.Label = dialog.Alias;
        entry.Path = newPath;

        if (!ReferenceEquals(target, sourceGroup))
        {
            sourceGroup.Bookmarks.Remove(entry);
            if (target is null)
            {
                target = new BookmarkGroup { Name = newGroupName };
                _bookmarks.Groups.Add(target);
            }
            target.Bookmarks.Add(entry);
            _bookmarks.Groups.RemoveAll(g => g.Bookmarks.Count == 0);
        }

        SaveBookmarks();
        RenderBookmarks();
        SetStatus(Loc.F("Bookmark updated: {0}", newPath));
    }

    /// <summary>
    /// The openBookmarkDialog command (F8 by default): opens a keyboard-driven
    /// quick-jump list of every bookmark. Each entry carries a two-letter
    /// mnemonic (group letter + entry letter); typing it navigates the active
    /// pane to that folder. The modifier held on the keystroke that commits the
    /// choice (the second mnemonic letter, or Enter) redirects it: Shift opens
    /// the bookmark in a new tab, Ctrl edits it instead of going there. Esc
    /// cancels.
    /// </summary>
    private void OpenBookmarkQuickJump()
    {
        if (!_bookmarks.Groups.Any(g => g.Bookmarks.Count > 0))
        {
            SetStatus(Loc.T("No bookmarks yet."));
            return;
        }

        var dialog = new BookmarkQuickJumpDialog(_bookmarks);
        if (dialog.ShowDialog() == true && dialog.SelectedPath is { } target)
        {
            if (dialog.RequestEdit)
            {
                // Ctrl: don't navigate anywhere, edit the chosen entry. The
                // dialog is already closed, so the edit dialog owns the screen
                // alone rather than stacking on top of the list.
                if (dialog.SelectedGroup is { } group && dialog.SelectedEntry is { } entry)
                {
                    EditBookmarkEntry(group, entry);
                }
                return;
            }

            if (dialog.OpenInNewTab)
            {
                // Same path as middle-clicking a sidebar bookmark, including its
                // de-duplication: a folder already open in this pane focuses its
                // existing tab rather than opening a second one.
                OpenNewTab(ActivePane, target);
                return;
            }

            // Same path as a sidebar click: navigate the active pane, not
            // existence-checked, so an offline share fails through Navigate's
            // normal error path.
            Navigate(_activeGrid, target, true);
        }
    }

    private void BookmarksTree_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var item = BookmarkItemFromSource(e.OriginalSource as DependencyObject);
        if (item is null)
        {
            return;
        }
        item.IsSelected = true;

        var menu = new ContextMenu();
        if (item.Tag is string && item.Items.Count == 0)
        {
            var remove = new MenuItem { Header = Loc.T("Remove bookmark") };
            remove.Click += (_, _) => RemoveBookmarkLeaf(item);
            menu.Items.Add(remove);
        }
        else if (item.Tag is null && item.Items.Count > 0)
        {
            var removeGroup = new MenuItem { Header = Loc.T("Remove group") };
            removeGroup.Click += (_, _) => RemoveBookmarkGroup(item.Header as string);
            menu.Items.Add(removeGroup);
        }

        if (menu.Items.Count > 0)
        {
            BookmarksTree.ContextMenu = menu;
        }
        else
        {
            e.Handled = true;
        }
    }

    private void RemoveBookmarkLeaf(TreeViewItem leaf)
    {
        if (leaf.Tag is not string path)
        {
            return;
        }

        var groupName = (leaf.Parent as TreeViewItem)?.Header as string;
        var group = groupName is null
            ? null
            : _bookmarks.Groups.FirstOrDefault(g => string.Equals(g.Name, groupName, StringComparison.Ordinal));

        if (group is not null)
        {
            group.Bookmarks.RemoveAll(b => FsHelpers.SamePath(b.Path, path));
        }
        else
        {
            // Parent header didn't resolve — remove the path from wherever it is.
            foreach (var g in _bookmarks.Groups)
            {
                g.Bookmarks.RemoveAll(b => FsHelpers.SamePath(b.Path, path));
            }
        }
        _bookmarks.Groups.RemoveAll(g => g.Bookmarks.Count == 0);

        SaveBookmarks();
        RenderBookmarks();
    }

    private void RemoveBookmarkGroup(string? groupName)
    {
        if (string.IsNullOrEmpty(groupName))
        {
            return;
        }
        _bookmarks.Groups.RemoveAll(g => string.Equals(g.Name, groupName, StringComparison.Ordinal));
        SaveBookmarks();
        RenderBookmarks();
    }

    private void BookmarksTree_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle)
        {
            return;
        }
        // Swallow the middle button so the TreeView doesn't start autoscroll.
        e.Handled = true;

        var item = BookmarkItemFromSource(e.OriginalSource as DependencyObject);
        if (item is null || item.Tag is not string path || item.Items.Count != 0)
        {
            return;
        }
        // Middle-click a bookmark leaf → open it in a new tab in the active pane
        // (and switch to it), mirroring middle-click in the file list.
        OpenNewTab(ActivePane, path);
    }

    private void BookmarksTree_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // Let the expander triangle toggle the group on its own.
        if (IsWithinExpander(e.OriginalSource as DependencyObject))
        {
            return;
        }
        var item = BookmarkItemFromSource(e.OriginalSource as DependencyObject);
        if (item is null)
        {
            return;
        }
        if (item.Tag is string path && item.Items.Count == 0)
        {
            // Leaf bookmark → navigate the active pane. Not existence-checked, so
            // an offline share fails through the normal Navigate error path.
            Navigate(_activeGrid, path, true);
        }
        else if (item.Tag is null && item.Items.Count > 0)
        {
            // Single click anywhere on a group header toggles it.
            item.IsExpanded = !item.IsExpanded;
        }
    }

    private void BookmarksGroup_ExpandedOrCollapsed(object sender, RoutedEventArgs e)
    {
        // Persist only group (parent) state. Groups carry a null Tag; leaf
        // bookmarks carry their path and never expand.
        if (e.OriginalSource is not TreeViewItem item || item.Tag is not null || item.Header is not string group)
        {
            return;
        }
        var collapsed = _settings.CollapsedBookmarkGroups;
        var idx = collapsed.FindIndex(g => string.Equals(g, group, StringComparison.OrdinalIgnoreCase));
        if (!item.IsExpanded && idx < 0)
        {
            collapsed.Add(group);
            SaveSettings();
        }
        else if (item.IsExpanded && idx >= 0)
        {
            collapsed.RemoveAt(idx);
            SaveSettings();
        }
    }

    private static TreeViewItem? BookmarkItemFromSource(DependencyObject? source)
    {
        while (source is not null and not TreeViewItem)
        {
            source = VisualTreeHelper.GetParent(source);
        }
        return source as TreeViewItem;
    }

    private static bool IsWithinExpander(DependencyObject? source)
    {
        while (source is not null and not TreeViewItem)
        {
            if (source is System.Windows.Controls.Primitives.ToggleButton)
            {
                return true;
            }
            source = VisualTreeHelper.GetParent(source);
        }
        return false;
    }
}
