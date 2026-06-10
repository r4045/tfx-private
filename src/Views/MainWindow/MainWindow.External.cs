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

    private void CopySelectedPath(IReadOnlyList<FileItem> selection)
    {
        if (selection.Count != 1)
        {
            return;
        }

        Clipboard.SetText(selection[0].FullPath);
        SetStatus(Loc.F("Copied path: {0}", selection[0].FullPath));
    }
}
