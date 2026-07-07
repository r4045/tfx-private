using System.Windows;
using System.Windows.Threading;

namespace Tfx;

public partial class MainWindow
{
    // Built-in actions reachable from the command palette: action id → (is it runnable
    // in the current context, how to run it). This is the ONE new piece the executable
    // palette needs — the id→behavior mapping that otherwise lives only, implicitly, inside
    // the Window_KeyDown if/else chain. Titles, descriptions, and key text are NOT duplicated
    // here: they are read live from ShortcutCatalog (descriptions) and _shortcuts (keys),
    // the same sources the read-only feature list uses.
    //
    // Curation: only *command-like* actions appear. Pure focus / navigation primitives
    // (focusSearch, focusFilePane, focusTerminal, goBack, goForward, goUp, openItem) are
    // deliberately excluded — running them from a list with no pane focus is meaningless —
    // as is commandPalette itself. Every id here MUST exist in DefaultShortcutText;
    // ValidateCommandPaletteActions() enforces that at startup.
    //
    // Each Run mirrors the corresponding Window_KeyDown branch exactly, so a palette
    // invocation and a key press do the same thing. The dispatch chain itself is left
    // untouched (folding it into a shared registry loop is a separate, later step that must
    // not disturb the load-bearing tunnel/bubble ordering of the keyboard handler).
    private Dictionary<string, (Func<bool> Enabled, Action Run)>? _commandPaletteActions;

    private Dictionary<string, (Func<bool> Enabled, Action Run)> CommandPaletteActions =>
        _commandPaletteActions ??= BuildCommandPaletteActions();

    private Dictionary<string, (Func<bool> Enabled, Action Run)> BuildCommandPaletteActions()
    {
        bool HasSelection() => ActiveSelectedItems().Any(i => !i.IsParent);

        return new Dictionary<string, (Func<bool>, Action)>(StringComparer.OrdinalIgnoreCase)
        {
            ["reload"] = (() => true, () => Reload(_activeGrid)),
            ["openTerminal"] = (() => !InArchiveContext, OpenTerminal),
            ["togglePreview"] = (() => true, TogglePreview),
            ["toggleFolderTree"] = (() => true, ToggleFolderTree),
            ["toggleRendered"] = (() => RenderedToggle.IsVisible, () =>
            {
                RenderedToggle.IsChecked = RenderedToggle.IsChecked != true;
                RenderedToggle_Click(RenderedToggle, new RoutedEventArgs());
            }),
            ["loadExternalImages"] = (() => LoadImagesButton.IsVisible,
                () => LoadImages_Click(LoadImagesButton, new RoutedEventArgs())),
            ["toggleSplit"] = (() => true, ToggleSplit),
            ["swapPanes"] = (() => true, SwapPanes),
            ["toggleHidden"] = (() => true, ToggleHidden),
            ["newFolder"] = (() => !InArchiveContext, NewFolder),
            ["newFile"] = (() => !InArchiveContext, NewFile),
            ["rename"] = (() => !InArchiveContext
                                && ActiveListingCurrentItem() is FileItem f && !f.IsParent, () =>
            {
                if (ActiveListingCurrentItem() is FileItem item && !item.IsParent)
                {
                    StartRename(_activeGrid, item);
                }
            }),
            // The palette has no Shift modifier, so it always uses the safe (Recycle Bin)
            // path — never the permanent delete the Shift+Delete key chord reaches.
            ["moveToTrash"] = (() => !InArchiveContext && HasSelection(), MoveSelectionToTrash),
            ["compressToZip"] = (() => !InArchiveContext && HasSelection(), CompressSelection),
            ["extractZip"] = (() => !InArchiveContext && HasSelection(), ExtractSelectedArchives),
            ["copyItems"] = (HasSelection, () => CopySelection(false)),
            ["cutItems"] = (() => !InArchiveContext && HasSelection(), () => CopySelection(true)),
            ["pasteItems"] = (() => !InArchiveContext, PasteIntoActivePane),
            ["selectAll"] = (() => true, () =>
            {
                if (_settings.ViewMode == ViewMode.Icons)
                {
                    IconViewOf(ActivePane).SelectAll();
                }
                else
                {
                    _activeGrid.SelectAll();
                }
            }),
            ["newTab"] = (() => true, NewTabInActivePane),
            ["closeTab"] = (() => true, CloseActiveTab),
            ["nextTab"] = (() => true, () => CycleTab(1)),
            ["prevTab"] = (() => true, () => CycleTab(-1)),
            ["toggleTerminal"] = (() => true, ToggleTerminalPane),
            ["syncCwd"] = (() => true, () => TerminalSyncCwd_Click(TerminalSyncCwdButton, new RoutedEventArgs())),
            ["changeMoveMode"] = (() => true, ToggleMoveMode),
            ["openExplorer"] = (() => true, OpenCurrentFolderInExplorer),
            ["moveClipboard"] = (() => true, MoveToClipboardPath),
            ["addBookmark"] = (() => true, AddCurrentFolderBookmarkViaDialog),
            ["pathToClipboard"] = (() => true, PathToClipboard),
            ["openBookmarkDialog"] = (() => true, OpenBookmarkQuickJump),
            ["quit"] = (() => true, Close),
            ["sortByModifiedFlat"] = (() => true, SortActivePaneByModifiedFlat),
        };
    }

    /// <summary>
    /// Opens the command palette (commandPalette, Ctrl+P by default), then runs whatever the
    /// user picked. The palette lists every currently-runnable built-in action plus the
    /// user-defined commands that match the active context, and is purely a chooser — the
    /// chosen row is dispatched here, where the action methods live.
    /// </summary>
    private void OpenCommandPalette()
    {
        var rows = BuildPaletteRows();
        if (rows.Count == 0)
        {
            SetStatus(Loc.T("No commands available"));
            return;
        }

        var dialog = new CommandPaletteDialog(rows);
        if (dialog.ShowDialog() == true && dialog.Selected is { } row)
        {
            DispatchPaletteSelection(row.Tag);
        }
    }

    /// <summary>
    /// Builds the palette rows in feature-list order: built-in actions first (only those that
    /// are command-like AND runnable in the current context), then user-defined commands whose
    /// filters match the active selection / git context. Titles come from ShortcutCatalog and
    /// key text from _shortcuts — never hand-written here, so they can't drift from the real
    /// bindings.
    /// </summary>
    private List<PaletteRow> BuildPaletteRows()
    {
        var rows = new List<PaletteRow>();

        var builtinGroup = Loc.T("Built-in commands");
        foreach (var (action, _) in DefaultShortcutText)
        {
            if (!CommandPaletteActions.TryGetValue(action, out var entry) || !entry.Enabled())
            {
                continue;
            }

            var key = _shortcuts.TryGetValue(action, out var shortcut) ? shortcut.DisplayText : "";
            var title = BuiltInDescription(action);
            if (string.IsNullOrWhiteSpace(title))
            {
                title = action;
            }
            rows.Add(new PaletteRow(builtinGroup, title, action, key, action));
        }

        if (_config.Commands.Count > 0)
        {
            var selection = ActiveSelectedItems().Where(i => !i.IsParent).ToList();
            var isGitRepo = GetGitRoot(ActivePane) is not null;
            var commandGroup = Loc.T("User commands");
            foreach (var command in _config.Commands)
            {
                if (!CommandRunner.Matches(command, selection, isGitRepo))
                {
                    continue;
                }
                rows.Add(new PaletteRow(
                    commandGroup, command.Name, FirstLine(command.Run), command.Shortcut?.DisplayText ?? "", command));
            }
        }

        return rows;
    }

    /// <summary>
    /// Runs the action behind a chosen palette row. A string tag is a built-in action id
    /// (run via <see cref="CommandPaletteActions"/>); a <see cref="UserCommand"/> tag is a
    /// user command (run via the same path as its context-menu / shortcut entry). Focus is
    /// returned to the active listing afterwards so the next keystroke lands in the pane
    /// rather than nowhere — the palette stole focus while it was open.
    /// </summary>
    private void DispatchPaletteSelection(object tag)
    {
        switch (tag)
        {
            case string id when CommandPaletteActions.TryGetValue(id, out var entry):
                if (entry.Enabled())
                {
                    entry.Run();
                }
                break;
            case UserCommand command:
                ExecuteUserCommand(command, ActiveSelectedItems().Where(i => !i.IsParent).ToList());
                break;
        }

        Dispatcher.BeginInvoke(FocusActiveFilePane, DispatcherPriority.ApplicationIdle);
    }

    /// <summary>
    /// Startup guardrail: every id in <see cref="CommandPaletteActions"/> must be a real
    /// action in <c>DefaultShortcutText</c>. A mismatch means a shortcut was renamed or
    /// removed without updating the palette map — a developer error that would otherwise show
    /// up only as a silently missing or dead palette entry. Surfaced through the same
    /// <c>_config.Errors</c> channel as config-parse warnings, so it's loud at launch. The
    /// reverse direction is intentionally NOT checked: many DefaultShortcutText ids (the
    /// focus / navigation primitives) are deliberately absent from the palette.
    /// </summary>
    private void ValidateCommandPaletteActions()
    {
        foreach (var id in CommandPaletteActions.Keys)
        {
            if (!DefaultShortcutText.ContainsKey(id))
            {
                _config.Errors.Add($"commandPalette: action '{id}' has no entry in DefaultShortcutText.");
            }
        }
    }
}
