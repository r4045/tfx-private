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

        var destGrid = _activeGrid;
        var destination = GetCurrentPath(destGrid);
        var failed = new List<string>();
        var shellItems = new List<FileTransferItem>();
        var externalCopies = new List<string>();
        var externalMoves = new List<string>();

        // When the user ticks "apply to all", the chosen action is reused for
        // every remaining collision instead of prompting again.
        FileConflictChoice? bulkChoice = null;
        var remaining = files.Length;

        // Planning (existence checks and conflict prompts) stays on the UI
        // thread; the actual copying runs in the background afterwards so a
        // large or network copy no longer freezes the window.
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
            if (!sourceIsDir && !File.Exists(source))
            {
                failed.Add(Path.GetFileName(source));
                continue;
            }

            // An external copier configured in [fileOperations] handles its
            // own conflicts, so hand it the source as-is.
            if (_config.FileOperations.Handles(isMove))
            {
                (isMove ? externalMoves : externalCopies).Add(source);
                continue;
            }

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
                        // Cancel aborts the whole paste (nothing has been
                        // copied yet, so nothing is half done).
                        return;
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

            // Two pasted sources resolving to the same new name would make the
            // shell prompt mid-copy; give the later one a fresh name instead.
            if (!overwrite && shellItems.Any(i => FsHelpers.SamePath(i.Target, target)))
            {
                target = NextAvailablePathExcluding(target, shellItems);
            }

            shellItems.Add(new FileTransferItem(source, destination, Path.GetFileName(target), isMove, overwrite));
        }

        _cutBuffer = [];

        _ = RunTransfersAsync(destGrid, destination, shellItems, externalCopies, externalMoves, failed);
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

    private void RefreshPanesAfterPaste(string? lastWrittenName, DataGrid? destGrid = null, bool recoverFocus = true)
    {
        // Refresh both panes (the source can be the other pane) and select the
        // most-recently written entry in the destination so the paste is visibly
        // reflected even on network shares, where the watcher-driven refresh is
        // disabled and only the periodic poll would otherwise catch up.
        destGrid ??= _activeGrid;
        var otherGrid = destGrid == LeftGrid ? RightGrid : LeftGrid;
        Reload(destGrid, lastWrittenName);
        Reload(otherGrid);

        // Pasting clears and repopulates the active pane (and a conflict dialog,
        // when shown, pulls focus away), so keyboard focus ends up off the
        // listing until the user clicks back in. Re-claim it (with retries)
        // once the async reload has settled and ApplyPendingSelection has
        // selected the pasted entry.
        if (recoverFocus)
        {
            StartListingFocusRecovery();
        }
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

    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// Renames through a modal dialog. This used to be an in-place DataGrid
    /// cell edit, which kept the grid in an EditItem transaction; a reload,
    /// sort or auto-refresh landing during that transaction makes WPF throw
    /// InvalidOperationException and took the whole app down. A dialog never
    /// puts the grid into edit mode, works in the icon view too, and lets us
    /// validate before touching the file system.
    /// </summary>
    private void StartRename(DataGrid grid, FileItem item)
    {
        if (item.IsParent)
        {
            return;
        }

        var directory = Path.GetDirectoryName(item.FullPath) ?? GetCurrentPath(grid);
        var dialog = new NamePromptDialog(
            Loc.T("Rename"),
            item.IsDirectory ? Loc.T("Folder name") : Loc.T("File name"),
            item.Name,
            selectStem: !item.IsDirectory,
            validate: text => ValidateRenameTarget(item, directory, text));

        if (dialog.ShowDialog() != true)
        {
            StartListingFocusRecovery();
            return;
        }

        var newName = dialog.EnteredText.Trim();
        if (string.Equals(newName, item.Name, StringComparison.Ordinal))
        {
            StartListingFocusRecovery();
            return;
        }

        var target = Path.Combine(directory, newName);
        try
        {
            MovePath(item.FullPath, target, item.IsDirectory);
        }
        catch (Exception ex)
        {
            SetStatus(Loc.F("Rename failed: {0}", ex.Message));
            StartListingFocusRecovery();
            return;
        }

        // Reload's selectName re-selects the renamed entry so the user keeps
        // their place; both panes reload because the other one may show the
        // same folder.
        Reload(grid, newName);
        Reload(grid == LeftGrid ? RightGrid : LeftGrid);
        StartListingFocusRecovery();
        SetStatus(Loc.F("Renamed to {0}", newName));
    }

    /// <summary>
    /// Returns an error message for an unusable new name, or null when the
    /// rename can go ahead. A case-only change ("readme.md" -> "README.md")
    /// is allowed even though the file system reports the target as existing.
    /// </summary>
    private static string? ValidateRenameTarget(FileItem item, string directory, string rawName)
    {
        var name = (rawName ?? "").Trim();
        if (name.Length == 0)
        {
            return Loc.T("Enter a name.");
        }

        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            name.Contains(Path.DirectorySeparatorChar) ||
            name.Contains(Path.AltDirectorySeparatorChar))
        {
            return Loc.T("A name can't contain any of these characters: \\ / : * ? \" < > |");
        }

        if (name is "." or ".." || name.EndsWith('.'))
        {
            return Loc.T("A name can't end with a period.");
        }

        var stem = name.Split('.')[0].TrimEnd();
        if (ReservedDeviceNames.Contains(stem))
        {
            return Loc.F("\"{0}\" is a reserved name on Windows.", stem);
        }

        if (string.Equals(name, item.Name, StringComparison.Ordinal) ||
            string.Equals(name, item.Name, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var target = Path.Combine(directory, name);
        if (File.Exists(target) || Directory.Exists(target))
        {
            return Loc.F("\"{0}\" already exists in this folder.", name);
        }

        return null;
    }

    /// <summary>
    /// Moves a file or folder to <paramref name="target"/> in the same
    /// folder. A case-only rename of a folder goes through a temporary name
    /// because some file systems (and older runtimes) reject a move whose
    /// source and destination differ only in case.
    /// </summary>
    private static void MovePath(string source, string target, bool isDirectory)
    {
        var caseOnly = string.Equals(source, target, StringComparison.OrdinalIgnoreCase);
        if (!isDirectory)
        {
            File.Move(source, target);
            return;
        }

        if (!caseOnly)
        {
            Directory.Move(source, target);
            return;
        }

        var temp = Path.Combine(Path.GetDirectoryName(source)!, ".tfx-rename-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.Move(source, temp);
        try
        {
            Directory.Move(temp, target);
        }
        catch
        {
            // Put it back under its original name rather than leaving the
            // temporary name behind.
            try { Directory.Move(temp, source); } catch { }
            throw;
        }
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
