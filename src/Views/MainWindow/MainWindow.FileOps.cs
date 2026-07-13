using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualBasic.FileIO;
using VbFileSystem = Microsoft.VisualBasic.FileIO.FileSystem;
using Path = System.IO.Path;

namespace Tfx;

public partial class MainWindow
{
    private void OpenItem(DataGrid grid, FileItem item)
    {
        if (item.IsParent)
        {
            var current = GetCurrentPath(grid);
            string selectName;
            if (ArchivePath.TryParse(current, out var archive, out var inner))
            {
                selectName = string.IsNullOrEmpty(inner)
                    ? Path.GetFileName(archive)
                    : (inner.TrimEnd('/').Split('/').LastOrDefault() ?? "");
            }
            else
            {
                selectName = Path.GetFileName(current.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            }
            Navigate(grid, item.FullPath, true, selectName);
            return;
        }

        if (item.IsDirectory)
        {
            Navigate(grid, item.FullPath, true, "..");
            return;
        }

        if (ArchivePath.TryParse(item.FullPath, out var archiveFile, out var entryPath))
        {
            try
            {
                var realPath = ArchiveBrowser.ExtractEntryToTemp(archiveFile, entryPath, EnsureArchiveTempRoot(), CancellationToken.None);
                Process.Start(new ProcessStartInfo(realPath) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                SetStatus(ex.Message);
            }
            return;
        }

        if (ArchivePath.IsZipFile(item.FullPath) && File.Exists(item.FullPath))
        {
            Navigate(grid, ArchivePath.Combine(item.FullPath, ""), true, "..");
            return;
        }

        if (TryOpenWithConfiguredApp(item.FullPath))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(item.FullPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
        }
    }

    private bool TryOpenWithConfiguredApp(string path)
    {
        var extension = AppConfig.NormalizeExtension(Path.GetExtension(path));
        if (extension.Length == 0 || !_config.OpenWith.TryGetValue(extension, out var app) || string.IsNullOrWhiteSpace(app))
        {
            return false;
        }

        try
        {
            var expandedApp = Environment.ExpandEnvironmentVariables(AppConfig.ExpandUserPath(app));
            var safePath = "\"" + path.Replace("\"", "\"\"") + "\"";
            Process.Start(new ProcessStartInfo(expandedApp, safePath) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            SetStatus(Loc.F("Open with failed: {0}", ex.Message));
            return true;
        }
    }

    private string EnsureArchiveTempRoot()
    {
        if (!string.IsNullOrEmpty(_archiveTempRoot))
        {
            return _archiveTempRoot!;
        }
        // Before creating this session's folder, opportunistically sweep
        // leftovers from previous tfx runs that crashed before they could
        // delete their temp folders. Best-effort: anything currently held
        // open by another tfx process is silently skipped.
        try
        {
            var parent = Path.Combine(Path.GetTempPath(), "tfx");
            if (Directory.Exists(parent))
            {
                foreach (var stale in Directory.EnumerateDirectories(parent, "archive-*"))
                {
                    try { Directory.Delete(stale, recursive: true); } catch { }
                }
            }
        }
        catch
        {
        }
        var root = Path.Combine(Path.GetTempPath(), "tfx", "archive-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        _archiveTempRoot = root;
        return root;
    }

    private void CopySelection(bool cut)
    {
        var paths = ActiveSelectedItems().Where(i => !i.IsParent).Select(i => i.FullPath).ToArray();
        if (paths.Length == 0)
        {
            return;
        }

        var collection = new StringCollection();
        collection.AddRange(paths);
        try
        {
            Clipboard.SetFileDropList(collection);
        }
        catch (Exception ex)
        {
            // Another process (clipboard history, clipboard managers, RDP
            // sync) can hold the clipboard open; WPF then throws
            // CLIPBRD_E_CANT_OPEN even after its internal retries. Surface
            // the failure instead of letting it escape the key handler and
            // kill the app.
            SetStatus(cut ? Loc.F("Cut failed: {0}", ex.Message) : Loc.F("Copy failed: {0}", ex.Message));
            return;
        }
        _cutBuffer = cut ? paths : [];
        ClipboardDiag.Log($"copy: cut={cut}, {paths.Length} path(s): {string.Join(" | ", paths)}");
        ClipboardDiag.LogFormats("copy: clipboard state after SetFileDropList");
        SetStatus(cut ? Loc.F("Cut {0} item(s)", paths.Length) : Loc.F("Copied {0} item(s)", paths.Length));
    }

    private void PasteIntoActivePane()
    {
        ClipboardDiag.LogFormats("paste: clipboard state");
        string[] files;
        try
        {
            if (!Clipboard.ContainsFileDropList())
            {
                // No CF_HDROP does not mean no files: the RDP clipboard
                // (rdpclip), Outlook attachments and Explorer's zip folders
                // publish copied files as virtual FileGroupDescriptorW /
                // FileContents streams, because real source paths don't
                // exist on this machine.
                PasteVirtualFilesIntoActivePane();
                return;
            }
            files = Clipboard.GetFileDropList().Cast<string>().ToArray();
        }
        catch (Exception ex)
        {
            // Reading the clipboard fails with CLIPBRD_E_CANT_OPEN while
            // another process holds it open; report instead of crashing.
            SetStatus(Loc.F("Paste failed: {0}", ex.Message));
            return;
        }
        if (files.Length == 0)
        {
            return;
        }

        var destination = GetCurrentPath(_activeGrid);
        var succeeded = 0;
        var failed = new List<string>();
        var leftBehind = new List<string>();
        string? lastWrittenName = null;

        // When the user ticks "apply to all", the chosen action is reused for
        // every remaining collision instead of prompting again.
        FileConflictChoice? bulkChoice = null;
        var remaining = files.Length;

        foreach (var source in files)
        {
            remaining--;
            var requestedTarget = Path.Combine(destination, Path.GetFileName(source));
            var isMove = _cutBuffer.Contains(source, StringComparer.OrdinalIgnoreCase);

            // Moving an item onto itself is a no-op.
            if (isMove && FsHelpers.SamePath(source, requestedTarget))
            {
                continue;
            }

            var sourceIsDir = Directory.Exists(source);
            // Copy into the same folder: the user means "duplicate". Overwriting
            // an item with itself is impossible, so skip the prompt and always
            // keep both under a numbered name (matches Explorer's behaviour).
            var selfCopy = !isMove && FsHelpers.SamePath(source, requestedTarget);
            var collides = !selfCopy && (File.Exists(requestedTarget) || Directory.Exists(requestedTarget));

            string target;
            var overwrite = false;

            if (collides)
            {
                var choice = bulkChoice ?? FileConflictChoice.KeepBoth;
                if (bulkChoice is null)
                {
                    var dialog = new FileConflictDialog(
                        Path.GetFileName(requestedTarget),
                        sourceIsDir,
                        allowOverwrite: true,
                        canApplyToAll: remaining > 0);
                    if (dialog.ShowDialog() != true)
                    {
                        // Cancel aborts the whole paste.
                        break;
                    }
                    choice = dialog.Choice;
                    if (dialog.ApplyToAll)
                    {
                        bulkChoice = choice;
                    }
                }

                if (choice == FileConflictChoice.Overwrite)
                {
                    target = requestedTarget;
                    overwrite = true;
                }
                else
                {
                    target = FsHelpers.NextAvailableDatedPath(requestedTarget);
                }
            }
            else
            {
                target = selfCopy ? FsHelpers.NextAvailableDatedPath(requestedTarget) : requestedTarget;
            }

            try
            {
                if (sourceIsDir)
                {
                    if (isMove)
                    {
                        // Replace-on-overwrite: MoveDirectory won't merge over an
                        // existing folder, so clear the target first.
                        if (overwrite && Directory.Exists(target))
                        {
                            Directory.Delete(target, recursive: true);
                        }
                        // VbFileSystem.MoveDirectory falls back to copy + delete
                        // across volumes, where Directory.Move would throw IOException.
                        VbFileSystem.MoveDirectory(source, target);
                    }
                    else
                    {
                        VbFileSystem.CopyDirectory(source, target, overwrite);
                    }
                }
                else if (File.Exists(source))
                {
                    if (isMove)
                    {
                        File.Move(source, target, overwrite);
                    }
                    else
                    {
                        File.Copy(source, target, overwrite);
                    }
                }
                else
                {
                    failed.Add(Path.GetFileName(source));
                    continue;
                }

                // Post-move verification: if a move claimed to succeed but the
                // source still exists, the underlying copy-then-delete swallowed
                // a delete failure. Track for user feedback.
                if (isMove && (File.Exists(source) || Directory.Exists(source)))
                {
                    leftBehind.Add(Path.GetFileName(source));
                }
                else
                {
                    succeeded++;
                }

                lastWrittenName = Path.GetFileName(target);
            }
            catch (Exception ex)
            {
                failed.Add($"{Path.GetFileName(source)} ({ex.Message})");
            }
        }

        _cutBuffer = [];

        RefreshPanesAfterPaste(lastWrittenName);

        if (failed.Count == 0 && leftBehind.Count == 0)
        {
            SetStatus(Loc.F("Pasted {0} item(s)", succeeded));
        }
        else if (leftBehind.Count > 0)
        {
            SetStatus(Loc.F("Pasted {0}; source remained for: {1}", succeeded, string.Join(", ", leftBehind)));
        }
        else
        {
            SetStatus(Loc.F("Pasted {0}; failed: {1}", succeeded, string.Join(", ", failed)));
        }
    }

    /// <summary>
    /// Pastes virtual files (FileGroupDescriptorW + FileContents) from the
    /// clipboard — the shape the RDP clipboard and other stream-backed
    /// sources use instead of CF_HDROP. Always a copy: the source files are
    /// not on this machine, so cut/move semantics cannot apply.
    /// </summary>
    private void PasteVirtualFilesIntoActivePane()
    {
        using var clipboard = VirtualFileClipboard.TryOpen();
        if (clipboard is null)
        {
            ClipboardDiag.Log("paste: no FileDrop and no virtual files on clipboard");
            SetStatus(Loc.T("No pasteable files on the clipboard"));
            return;
        }

        ClipboardDiag.Log($"paste: {clipboard.Entries.Count} virtual entries: " +
            string.Join(" | ", clipboard.Entries.Select(e => e.RelativePath + (e.IsDirectory ? "\\" : ""))));

        var destination = GetCurrentPath(_activeGrid);

        // Group entries by their top-level name so conflicts prompt once per
        // pasted item, matching the CF_HDROP path above.
        var groups = new List<VirtualPasteGroup>();
        var byName = new Dictionary<string, VirtualPasteGroup>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in clipboard.Entries)
        {
            var separator = entry.RelativePath.IndexOf('\\');
            var topName = separator < 0 ? entry.RelativePath : entry.RelativePath[..separator];
            if (!byName.TryGetValue(topName, out var group))
            {
                group = new VirtualPasteGroup(topName);
                byName.Add(topName, group);
                groups.Add(group);
            }
            group.IsDirectory |= separator >= 0 || entry.IsDirectory;
            group.Members.Add(entry);
        }

        var succeeded = 0;
        var failed = new List<string>();
        string? lastWrittenName = null;
        FileConflictChoice? bulkChoice = null;
        var remaining = groups.Count;

        foreach (var group in groups)
        {
            remaining--;
            var requestedTarget = Path.Combine(destination, group.TopName);
            var collides = File.Exists(requestedTarget) || Directory.Exists(requestedTarget);

            var target = requestedTarget;
            if (collides)
            {
                var choice = bulkChoice ?? FileConflictChoice.KeepBoth;
                if (bulkChoice is null)
                {
                    var dialog = new FileConflictDialog(
                        group.TopName,
                        group.IsDirectory,
                        allowOverwrite: true,
                        canApplyToAll: remaining > 0);
                    if (dialog.ShowDialog() != true)
                    {
                        // Cancel aborts the whole paste.
                        break;
                    }
                    choice = dialog.Choice;
                    if (dialog.ApplyToAll)
                    {
                        bulkChoice = choice;
                    }
                }

                // Overwrite needs no special handling here: files are
                // rewritten in place (FileMode.Create) and directories are
                // merged, matching Explorer's behaviour.
                if (choice != FileConflictChoice.Overwrite)
                {
                    target = FsHelpers.NextAvailablePath(requestedTarget);
                }
            }

            var targetTop = Path.GetFileName(target);
            try
            {
                foreach (var entry in group.Members)
                {
                    var mapped = Path.Combine(destination, targetTop + entry.RelativePath[group.TopName.Length..]);
                    if (entry.IsDirectory)
                    {
                        Directory.CreateDirectory(mapped);
                    }
                    else
                    {
                        // Descriptor order is not guaranteed to list parent
                        // folders first, so create them per file.
                        Directory.CreateDirectory(Path.GetDirectoryName(mapped)!);
                        clipboard.ExtractToFile(entry, mapped);
                    }
                }

                succeeded++;
                lastWrittenName = targetTop;
            }
            catch (Exception ex)
            {
                failed.Add($"{group.TopName} ({ex.Message})");
            }
        }

        RefreshPanesAfterPaste(lastWrittenName);

        SetStatus(failed.Count == 0
            ? Loc.F("Pasted {0} item(s)", succeeded)
            : Loc.F("Pasted {0}; failed: {1}", succeeded, string.Join(", ", failed)));
    }

    private sealed class VirtualPasteGroup(string topName)
    {
        public string TopName { get; } = topName;
        public bool IsDirectory { get; set; }
        public List<VirtualFileEntry> Members { get; } = [];
    }

    private void RefreshPanesAfterPaste(string? lastWrittenName)
    {
        // Refresh both panes (the source can be the other pane) and select the
        // most-recently written entry in the destination so the paste is visibly
        // reflected even on network shares, where the watcher-driven refresh is
        // disabled and only the periodic poll would otherwise catch up.
        var destGrid = _activeGrid;
        var otherGrid = destGrid == LeftGrid ? RightGrid : LeftGrid;
        Reload(destGrid, lastWrittenName);
        Reload(otherGrid);

        // Pasting clears and repopulates the active pane (and a conflict dialog,
        // when shown, pulls focus away), so keyboard focus ends up off the
        // listing until the user clicks back in. Re-claim it (with retries)
        // once the async reload has settled and ApplyPendingSelection has
        // selected the pasted entry.
        StartListingFocusRecovery();
    }

    private void MoveSelectionToTrash()
    {
        var items = ActiveSelectedItems().Where(i => !i.IsParent).ToArray();
        if (items.Length == 0)
        {
            return;
        }

        if (!Confirm(Loc.F("Move {0} item(s) to Recycle Bin?", items.Length), Loc.T("Move to Recycle Bin")))
        {
            return;
        }

        foreach (var item in items)
        {
            try
            {
                if (item.IsDirectory)
                {
                    VbFileSystem.DeleteDirectory(item.FullPath, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                }
                else
                {
                    VbFileSystem.DeleteFile(item.FullPath, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                }
            }
            catch (Exception ex)
            {
                SetStatus(ex.Message);
            }
        }

        Reload(LeftGrid);
        Reload(RightGrid);
    }

    private void NewFolder()
    {
        var name = PromptForName(Loc.T("New Folder"), Loc.T("Folder name"), Loc.T("New Folder"));
        if (!TryNormalizeNewItemName(name, out var itemName))
        {
            return;
        }

        try
        {
            var path = FsHelpers.NextAvailablePath(Path.Combine(GetCurrentPath(_activeGrid), itemName));
            Directory.CreateDirectory(path);
            Reload(_activeGrid);
            SetStatus(Loc.F("Created {0}", path));
        }
        catch (Exception ex)
        {
            SetStatus(Loc.F("New folder failed: {0}", ex.Message));
        }
    }

    private void NewFile()
    {
        var name = PromptForName(Loc.T("New File"), Loc.T("File name"), Loc.T("New File.txt"));
        if (!TryNormalizeNewItemName(name, out var itemName))
        {
            return;
        }

        try
        {
            var path = FsHelpers.NextAvailablePath(Path.Combine(GetCurrentPath(_activeGrid), itemName));
            File.WriteAllBytes(path, []);
            Reload(_activeGrid);
            SetStatus(Loc.F("Created {0}", path));
        }
        catch (Exception ex)
        {
            SetStatus(Loc.F("New file failed: {0}", ex.Message));
        }
    }

    private string? PromptForName(string title, string label, string defaultValue)
    {
        var dialog = new NamePromptDialog(title, label, defaultValue);
        return dialog.ShowDialog() == true ? dialog.EnteredText : null;
    }

    private static bool Confirm(string message, string confirmText)
    {
        var dialog = new ConfirmDialog("tfx", message, confirmText);
        return dialog.ShowDialog() == true;
    }

    private bool TryNormalizeNewItemName(string? rawName, out string name)
    {
        name = (rawName ?? "").Trim();
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            name.Contains(Path.DirectorySeparatorChar) ||
            name.Contains(Path.AltDirectorySeparatorChar))
        {
            SetStatus(Loc.F("Invalid name: {0}", name));
            return false;
        }

        return true;
    }

    private void StartRename(DataGrid grid, FileItem item)
    {
        var nameColumn = grid == LeftGrid ? LeftNameColumn : RightNameColumn;
        grid.IsReadOnly = false;
        grid.CurrentCell = new DataGridCellInfo(item, nameColumn);
        grid.BeginEdit();
    }

    private void Grid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        if (sender is not DataGrid grid)
        {
            return;
        }

        Dispatcher.BeginInvoke(() => grid.IsReadOnly = true, DispatcherPriority.Background);

        if (e.EditAction != DataGridEditAction.Commit)
        {
            return;
        }

        if (e.Row.Item is not FileItem item || item.IsParent)
        {
            return;
        }

        var nameColumn = grid == LeftGrid ? LeftNameColumn : RightNameColumn;
        if (e.Column != nameColumn)
        {
            return;
        }

        var tb = e.EditingElement as TextBox ?? FindVisualChild<TextBox>(e.EditingElement);
        if (tb is null)
        {
            return;
        }

        var newName = (tb.Text ?? "").Trim();
        if (string.IsNullOrEmpty(newName) || newName == item.Name)
        {
            return;
        }

        var directory = Path.GetDirectoryName(item.FullPath) ?? GetCurrentPath(grid);
        var target = FsHelpers.NextAvailablePath(Path.Combine(directory, newName));

        try
        {
            if (item.IsDirectory)
            {
                Directory.Move(item.FullPath, target);
            }
            else
            {
                File.Move(item.FullPath, target);
            }
        }
        catch (Exception ex)
        {
            SetStatus(Loc.F("Rename failed: {0}", ex.Message));
            return;
        }

        // Restore selection on the renamed entry after the reload so the
        // user keeps their place (and so arrow keys keep navigating).
        // The name MUST go through Reload's selectName parameter: Reload
        // unconditionally overwrites the pane's pending selection name with
        // its own argument, so a prior SetPendingSelectionName here would be
        // wiped to null before ApplyPendingSelection ever saw it — which is
        // why the renamed entry lost both selection and keyboard focus.
        var renamedName = Path.GetFileName(target);

        Dispatcher.BeginInvoke(() =>
        {
            Reload(grid, renamedName);
            Reload(grid == LeftGrid ? RightGrid : LeftGrid);

            // Renaming rebuilds the listing (and ends a cell edit), both of
            // which can strand keyboard focus outside the pane; re-claim it
            // the same way paste does.
            StartListingFocusRecovery();
        }, DispatcherPriority.Background);
    }

    private void RenameTextBox_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox tb)
        {
            return;
        }

        tb.Focus();
        var text = tb.Text ?? "";

        if (tb.DataContext is FileItem item && !item.IsDirectory)
        {
            var dot = text.LastIndexOf('.');
            if (dot > 0)
            {
                tb.Select(0, dot);
                return;
            }
        }

        tb.SelectAll();
    }

    private void DeletePermanently()
    {
        var items = ActiveSelectedItems().Where(i => !i.IsParent).ToArray();
        if (items.Length == 0)
        {
            return;
        }

        var msg = items.Length == 1
            ? Loc.F("Permanently delete \"{0}\"? This cannot be undone.", items[0].Name)
            : Loc.F("Permanently delete {0} item(s)? This cannot be undone.", items.Length);

        if (!Confirm(msg, Loc.T("Delete permanently")))
        {
            return;
        }

        foreach (var item in items)
        {
            try
            {
                if (item.IsDirectory)
                {
                    Directory.Delete(item.FullPath, recursive: true);
                }
                else
                {
                    File.Delete(item.FullPath);
                }
            }
            catch (Exception ex)
            {
                SetStatus(ex.Message);
            }
        }

        Reload(LeftGrid);
        Reload(RightGrid);
    }

    private void CompressSelection()
    {
        var items = ActiveSelectedItems().Where(i => !i.IsParent).ToArray();
        if (items.Length == 0)
        {
            return;
        }

        var directory = GetCurrentPath(_activeGrid);
        var baseName = items.Length == 1
            ? Path.GetFileNameWithoutExtension(items[0].Name)
            : Loc.T("Archive");
        var zipPath = FsHelpers.NextAvailablePath(Path.Combine(directory, $"{baseName}.zip"));

        try
        {
            using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
            foreach (var item in items)
            {
                if (item.IsDirectory)
                {
                    AddDirectoryToArchive(archive, item.FullPath, item.Name);
                }
                else
                {
                    archive.CreateEntryFromFile(item.FullPath, item.Name, CompressionLevel.Optimal);
                }
            }

            Reload(_activeGrid);
            SetStatus(Loc.F("Created {0}", zipPath));
        }
        catch (Exception ex)
        {
            SetStatus(Loc.F("Compress failed: {0}", ex.Message));
            try
            {
                if (File.Exists(zipPath))
                {
                    File.Delete(zipPath);
                }
            }
            catch
            {
            }
        }
    }

    private void ExtractSelectedArchives()
    {
        var archives = ActiveSelectedItems()
            .Where(i => !i.IsParent && !i.IsDirectory && Path.GetExtension(i.FullPath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (archives.Length == 0)
        {
            SetStatus(Loc.T("Select one or more .zip files to extract"));
            return;
        }

        foreach (var archiveItem in archives)
        {
            var destination = FsHelpers.NextAvailablePath(Path.Combine(
                GetCurrentPath(_activeGrid),
                Path.GetFileNameWithoutExtension(archiveItem.Name)));

            try
            {
                Directory.CreateDirectory(destination);
                ZipFile.ExtractToDirectory(archiveItem.FullPath, destination);
            }
            catch (Exception ex)
            {
                SetStatus(Loc.F("Extract failed: {0}", ex.Message));
                return;
            }
        }

        Reload(_activeGrid);
        SetStatus(Loc.F("Extracted {0} archive(s)", archives.Length));
    }

    private static void AddDirectoryToArchive(ZipArchive archive, string sourceDirectory, string entryRoot)
    {
        var files = Directory.EnumerateFiles(sourceDirectory, "*", System.IO.SearchOption.AllDirectories);
        var wroteAnyFile = false;

        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(sourceDirectory, file);
            var entryName = Path.Combine(entryRoot, relative).Replace(Path.DirectorySeparatorChar, '/');
            archive.CreateEntryFromFile(file, entryName, CompressionLevel.Optimal);
            wroteAnyFile = true;
        }

        if (!wroteAnyFile)
        {
            archive.CreateEntry(entryRoot.TrimEnd('/', '\\') + "/");
        }
    }

    private static T? FindVisualChild<T>(DependencyObject? parent) where T : DependencyObject
    {
        if (parent is null)
        {
            return null;
        }
        if (parent is T match)
        {
            return match;
        }

        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var found = FindVisualChild<T>(VisualTreeHelper.GetChild(parent, i));
            if (found is not null)
            {
                return found;
            }
        }
        return null;
    }

    private static T? FindVisualAncestor<T>(DependencyObject? child) where T : DependencyObject
    {
        var node = child;
        while (node is not null)
        {
            if (node is T match)
            {
                return match;
            }

            node = VisualTreeHelper.GetParent(node);
        }

        return null;
    }

}
