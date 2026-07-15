using System.Diagnostics;
using System.Windows;

namespace Tfx;

public partial class MainWindow
{
    private void OpenTerminal()
    {
        var path = GetCurrentPath(_activeGrid);
        if (!TerminalLauncher.Launch(path, _settings.TerminalCommand, _settings.TerminalArguments, out var error))
        {
            SetStatus(Loc.F("Failed to launch terminal: {0}", error ?? string.Empty));
        }
    }

    private void OpenTerminalSettings()
    {
        var dialog = new TerminalSettingsDialog(_settings.TerminalCommand, _settings.TerminalArguments);
        if (dialog.ShowDialog() == true)
        {
            _settings.TerminalCommand = dialog.Command;
            _settings.TerminalArguments = dialog.Arguments;
            SaveSettings();
            SetStatus(Loc.T("Terminal settings updated"));
        }
    }

    private void RevealInExplorer()
    {
        var selected = SelectedItems(_activeGrid).FirstOrDefault(i => !i.IsParent);
        var currentPath = GetCurrentPath(_activeGrid);

        string argument;
        if (selected is not null && ArchivePath.TryParse(selected.FullPath, out var selArchive, out _))
        {
            argument = $"/select,\"{selArchive}\"";
        }
        else if (selected is not null)
        {
            argument = $"/select,\"{selected.FullPath}\"";
        }
        else if (ArchivePath.TryParse(currentPath, out var curArchive, out _))
        {
            argument = $"/select,\"{curArchive}\"";
        }
        else
        {
            argument = currentPath;
        }

        // Always invoke explorer.exe by its absolute path in the Windows
        // directory (e.g. C:\Windows\explorer.exe — it does NOT live in
        // System32) so we never race a `explorer.exe` on PATH / in CWD that an
        // attacker could plant.
        var windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var explorerExe = System.IO.Path.Combine(windowsDir, "explorer.exe");
        Process.Start(new ProcessStartInfo(explorerExe, argument) { UseShellExecute = true });
    }

    /// <summary>
    /// Opens the active pane's current folder in a new Windows Explorer window.
    /// Unlike <see cref="RevealInExplorer"/> this opens the folder itself and
    /// selects nothing. In an archive view it falls back to the real folder that
    /// contains the archive file (the archive's inner path isn't a shell
    /// location Explorer can open).
    /// </summary>
    private void OpenCurrentFolderInExplorer()
    {
        var currentPath = GetCurrentPath(_activeGrid);

        var folder = ArchivePath.TryParse(currentPath, out var archive, out _)
            ? System.IO.Path.GetDirectoryName(archive) ?? ""
            : currentPath;

        if (string.IsNullOrEmpty(folder) || !System.IO.Directory.Exists(folder))
        {
            return;
        }

        // Invoke explorer.exe by its absolute path in the Windows directory so we
        // never race an explorer.exe planted on PATH / in the CWD.
        var windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var explorerExe = System.IO.Path.Combine(windowsDir, "explorer.exe");
        try
        {
            Process.Start(new ProcessStartInfo(explorerExe, $"\"{folder}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            SetStatus(Loc.F("Failed to open Explorer: {0}", ex.Message));
        }
    }

    /// <summary>
    /// Navigates the active pane to a path taken from the clipboard. Prefers a
    /// text path (e.g. TFX's "Copy current path", or a path copied from
    /// elsewhere); falls back to the first entry of a file-drop list (files
    /// copied in Explorer). A folder is opened directly; a file opens its parent
    /// folder with the file selected.
    /// </summary>
    private void MoveToClipboardPath()
    {
        var raw = TryGetClipboardPath();
        if (string.IsNullOrWhiteSpace(raw))
        {
            SetStatus(Loc.T("No path on the clipboard"));
            return;
        }

        var path = raw.Trim().Trim('"');

        if (System.IO.Directory.Exists(path))
        {
            Navigate(_activeGrid, path, true, "..");
            return;
        }
        if (System.IO.File.Exists(path))
        {
            var dir = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && System.IO.Directory.Exists(dir))
            {
                Navigate(_activeGrid, dir, true, System.IO.Path.GetFileName(path));
                return;
            }
        }
        SetStatus(Loc.F("Path not found: {0}", path));
    }

    private static string? TryGetClipboardPath()
    {
        try
        {
            if (Clipboard.ContainsText())
            {
                var text = Clipboard.GetText();
                // Use the first non-empty line, in case several were copied.
                var firstLine = text?.Split('\n', '\r').FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));
                if (!string.IsNullOrWhiteSpace(firstLine))
                {
                    return firstLine;
                }
            }
            if (Clipboard.ContainsFileDropList())
            {
                var files = Clipboard.GetFileDropList();
                if (files.Count > 0)
                {
                    return files[0];
                }
            }
        }
        catch
        {
            // The clipboard can throw if another process holds it open; treat
            // as empty.
        }
        return null;
    }

    private void ToggleHidden()
    {
        ShowHidden = !ShowHidden;
        HiddenButton.IsChecked = ShowHidden;
        Reload(LeftGrid);
        Reload(RightGrid);
        LoadDrives();
        QueueFolderTreeSyncToActivePane();
        SetStatus(ShowHidden ? Loc.T("Hidden files visible") : Loc.T("Hidden files hidden"));
    }

    private void Terminal_Click(object sender, RoutedEventArgs e) => OpenTerminal();

    private void Hidden_Click(object sender, RoutedEventArgs e) => ToggleHidden();

    /// <summary>
    /// Copies one path to the clipboard as text (pathToClipboard, F10 by
    /// default). With items selected, copies the first selected entry's full
    /// path; the ".." parent row is ignored so it never wins over a real
    /// selection. With nothing selected (or only ".."), copies the active pane's
    /// current folder. Note: "first" is selection order (DataGrid.SelectedItems),
    /// not display order — they differ when the user ctrl-clicks out of sequence.
    /// </summary>
    private void PathToClipboard()
    {
        var first = ActiveSelectedItems().FirstOrDefault(i => !i.IsParent);
        var path = first?.FullPath ?? GetCurrentPath(_activeGrid);
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            Clipboard.SetText(path);
            SetStatus(Loc.F("Copied path: {0}", path));
        }
        catch (Exception ex)
        {
            // Another process can hold the clipboard open; surface the failure
            // instead of letting the exception escape the key handler.
            SetStatus(Loc.F("Failed to copy path: {0}", ex.Message));
        }
    }

    /// <summary>
    /// Copies the selected entries' names — not their paths — to the clipboard as
    /// text (nameToClipboard, Shift+F9 by default; nameNoExtToClipboard,
    /// Ctrl+Shift+F9, for the extension-less form). Unlike
    /// <see cref="PathToClipboard"/>, which copies a single path, EVERY selected
    /// entry is copied, one per line, so the result can be pasted as a list; the
    /// ".." parent row is ignored. With nothing selected (or only "..") the
    /// active pane's current folder name is copied — the name-only counterpart of
    /// PathToClipboard's current-folder fallback. Line order is selection order
    /// (DataGrid.SelectedItems), not display order; they differ when the user
    /// ctrl-clicks out of sequence.
    /// <paramref name="includeExtension"/> = false strips the extension from
    /// FILES only: a dot in a folder name is part of the name (e.g. "v1.2.3"),
    /// not an extension, so folders — and the current-folder fallback — are never
    /// trimmed.
    /// </summary>
    private void NamesToClipboard(bool includeExtension)
    {
        var items = ActiveSelectedItems().Where(i => !i.IsParent).ToList();

        var names = items.Count > 0
            ? items.Select(i => (includeExtension || i.IsDirectory)
                ? i.Name
                : System.IO.Path.GetFileNameWithoutExtension(i.Name)).ToList()
            : [CurrentFolderName()];

        names.RemoveAll(string.IsNullOrEmpty);
        if (names.Count == 0)
        {
            return;
        }

        try
        {
            Clipboard.SetText(string.Join(Environment.NewLine, names));
            SetStatus(names.Count == 1
                ? Loc.F("Copied name: {0}", names[0])
                : Loc.F("Copied {0} name(s)", names.Count));
        }
        catch (Exception ex)
        {
            // Another process can hold the clipboard open; surface the failure
            // instead of letting the exception escape the key handler.
            SetStatus(Loc.F("Failed to copy name: {0}", ex.Message));
        }
    }

    /// <summary>
    /// The active pane's current folder name — the last path segment. Inside an
    /// archive view that is the inner folder's name, or the archive file's own
    /// name at the archive root (the "::" path form is not a real file-system
    /// path, so Path.GetFileName cannot be used on it directly). Empty at a drive
    /// root, which has no name.
    /// </summary>
    private string CurrentFolderName()
    {
        var current = GetCurrentPath(_activeGrid);
        if (ArchivePath.TryParse(current, out var archive, out var inner))
        {
            return string.IsNullOrEmpty(inner)
                ? System.IO.Path.GetFileName(archive)
                : (inner.TrimEnd('/').Split('/').LastOrDefault() ?? "");
        }

        return System.IO.Path.GetFileName(current.TrimEnd(
            System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
    }
}
