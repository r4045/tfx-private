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
        if (!_bookmarks.Groups.Any(g => g.Bookmarks.Count > 0))
        {
            BookmarksHeader.Visibility = Visibility.Collapsed;
            BookmarksTree.Visibility = Visibility.Collapsed;
            return;
        }
        BookmarksHeader.Visibility = Visibility.Visible;
        BookmarksTree.Visibility = Visibility.Visible;

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

        var groupName = dialog.EnteredText.Trim();
        if (string.IsNullOrEmpty(groupName))
        {
            groupName = "Bookmarks";
        }

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

        group.Bookmarks.Add(new BookmarkEntry { Label = "", Path = path });
        SaveBookmarks();
        RenderBookmarks();
        SetStatus(Loc.F("Bookmarked {0}", path));
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
