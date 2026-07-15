using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace Tfx;

public partial class MainWindow
{
    /// <summary>
    /// Brief description per built-in action, shown in the feature list
    /// (機能一覧). Co-located conceptually with <c>DefaultShortcutText</c>
    /// (MainWindow.Keyboard.cs): when a new action is added there, add its
    /// description here too. The feature list iterates the canonical
    /// <c>DefaultShortcutText</c> set, so an action missing from this table still
    /// appears — it just renders "(no description)" rather than being dropped,
    /// making the gap visible instead of silent. English / Japanese chosen by the
    /// same <c>Loc.IsJapanese</c> gate the rest of the UI uses; this stays a local
    /// table (not the global Loc dictionary) because it is feature-specific
    /// reference text, not reused chrome.
    /// </summary>
    private static readonly Dictionary<string, (string En, string Ja)> ShortcutCatalog =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["reload"] = ("Reload the active pane", "アクティブ ペインを再読み込み"),
            ["openTerminal"] = ("Open the external terminal at the current folder", "外部ターミナルを現在のフォルダーで開く"),
            ["togglePreview"] = ("Show/hide the preview pane", "プレビュー ペインの表示/非表示"),
            ["toggleFolderTree"] = ("Show/hide the folder tree (left sidebar)", "フォルダー ツリー（左サイドバー）の表示/非表示"),
            ["toggleRendered"] = ("Toggle rendered Markdown/HTML vs source in preview", "プレビューの Markdown/HTML レンダリング表示を切替"),
            ["loadExternalImages"] = ("Load external images in the current preview", "現在のプレビューで外部画像を読み込む"),
            ["toggleSplit"] = ("Toggle the two-pane split view", "2 ペイン分割表示を切替"),
            ["swapPanes"] = ("Swap the left and right panes", "左右のペインを入れ替え"),
            ["focusSearch"] = ("Move focus to the search box", "検索ボックスにフォーカス"),
            ["focusFilePane"] = ("Move focus to the active file list", "アクティブなファイル一覧にフォーカス"),
            ["focusTerminal"] = ("Move focus to the built-in terminal", "内蔵ターミナルにフォーカス"),
            ["toggleHidden"] = ("Show/hide hidden files", "隠しファイルの表示/非表示"),
            ["goBack"] = ("Go back in history", "履歴を戻る"),
            ["goForward"] = ("Go forward in history", "履歴を進む"),
            ["goUp"] = ("Go to the parent folder", "親フォルダーへ移動"),
            ["openItem"] = ("Open the selected item", "選択した項目を開く"),
            ["newFolder"] = ("Create a new folder", "新規フォルダーを作成"),
            ["newFile"] = ("Create a new file", "新規ファイルを作成"),
            ["rename"] = ("Rename the selected item", "選択した項目の名前を変更"),
            ["moveToTrash"] = ("Move selection to Recycle Bin (Shift = delete permanently)", "選択項目をごみ箱へ移動（Shift で完全削除）"),
            ["compressToZip"] = ("Compress the selection to a Zip", "選択項目を ZIP に圧縮"),
            ["extractZip"] = ("Extract the selected Zip archive(s)", "選択した ZIP を展開"),
            ["copyItems"] = ("Copy the selection", "選択項目をコピー"),
            ["cutItems"] = ("Cut the selection", "選択項目を切り取り"),
            ["pasteItems"] = ("Paste from the clipboard", "クリップボードから貼り付け"),
            ["selectAll"] = ("Select all items", "すべての項目を選択"),
            ["newTab"] = ("Open a new tab", "新しいタブを開く"),
            ["closeTab"] = ("Close the current tab", "現在のタブを閉じる"),
            ["nextTab"] = ("Switch to the next tab", "次のタブへ切替"),
            ["prevTab"] = ("Switch to the previous tab", "前のタブへ切替"),
            ["toggleTerminal"] = ("Show/hide the built-in terminal pane", "内蔵ターミナル ペインの表示/非表示"),
            ["syncCwd"] = ("Set the active file list to the terminal's folder", "アクティブなファイル一覧をターミナルの現在フォルダーに合わせる"),
            ["quit"] = ("Quit the application", "アプリケーションを終了"),
            ["changeMoveMode"] = ("Toggle move mode (Explorer / Vi)", "移動モード（Explorer / Vi）を切替"),
            ["openExplorer"] = ("Open the current folder in Explorer", "現在のフォルダーをエクスプローラーで開く"),
            ["moveClipboard"] = ("Navigate to the path on the clipboard", "クリップボードのパスへ移動"),
            ["addBookmark"] = ("Add the current folder to bookmarks", "現在のフォルダーをブックマークに追加"),
            ["pathToClipboard"] = ("Copy the current path to the clipboard", "現在のパスをクリップボードにコピー"),
            ["nameToClipboard"] = ("Copy the selected names (one per line) to the clipboard; no selection = the current folder name", "選択項目の名前をクリップボードにコピー（1 行 1 件。未選択なら現在のフォルダー名）"),
            ["nameNoExtToClipboard"] = ("Copy the selected names without their extension (folders are left untrimmed)", "選択項目の名前を拡張子なしでコピー（フォルダーはそのまま）"),
            ["openBookmarkDialog"] = ("Jump to a bookmark (quick jump; Shift = open in a new tab)", "ブックマークへジャンプ（クイック ジャンプ。Shift で新しいタブに開く）"),
            ["commandPalette"] = ("Open the command palette", "コマンド パレットを開く"),
            ["viCheatSheet"] = ("Show the Vi-mode key cheat sheet", "Vi モードのキー一覧を表示"),
            ["sortByModifiedFlat"] = ("Sort by date modified, newest first, files and folders mixed", "更新日時順（新しい順、フォルダー/ファイル混在）に並べ替え"),
        };

    /// <summary>
    /// Opens the settings menu under the toolbar's settings (gear) button. Two
    /// entries: "detail items" (the column visibility / order popup, the button's
    /// former direct action) and "feature list" (the read-only shortcut / command
    /// reference). Built as a plain <see cref="ContextMenu"/> the same way the
    /// file-pane menus are, so it inherits the app's menu styling.
    /// </summary>
    private void OpenSettingsMenu()
    {
        var menu = new ContextMenu
        {
            PlacementTarget = ColumnsButton,
            Placement = PlacementMode.Bottom,
        };

        var detail = new MenuItem { Header = Loc.T("Detail items") };
        detail.Click += (_, _) => OpenColumnsPopup();
        menu.Items.Add(detail);

        var features = new MenuItem { Header = Loc.T("Feature list") };
        features.Click += (_, _) => OpenShortcutListDialog();
        menu.Items.Add(features);

        // Attach to the button (matches how the file-pane fallback menu is
        // opened) so placement and the app's menu styling resolve correctly,
        // then open.
        ColumnsButton.ContextMenu = menu;
        menu.IsOpen = true;
    }

    private void OpenShortcutListDialog()
    {
        new ShortcutListDialog(BuildShortcutListEntries()).ShowDialog();
    }

    /// <summary>
    /// Assembles the feature-list rows: every built-in action (in
    /// <c>DefaultShortcutText</c> declaration order) with its current binding read
    /// live from <c>_shortcuts</c> — so a key left unbound by a config conflict
    /// shows as "(unbound)" rather than lying — followed by any user-defined
    /// <c>[[commands]]</c>. The key column is never hand-written; it always
    /// reflects the resolved bindings.
    /// </summary>
    private List<ShortcutListEntry> BuildShortcutListEntries()
    {
        var entries = new List<ShortcutListEntry>();

        var builtinGroup = Loc.T("Built-in commands");
        foreach (var (action, _) in DefaultShortcutText)
        {
            var key = _shortcuts.TryGetValue(action, out var shortcut) ? shortcut.DisplayText : "";
            entries.Add(new ShortcutListEntry(builtinGroup, action, BuiltInDescription(action), key));
        }

        if (_config.Commands.Count > 0)
        {
            var commandGroup = Loc.T("User commands");
            foreach (var command in _config.Commands)
            {
                var description = FirstLine(command.Run);
                entries.Add(new ShortcutListEntry(
                    commandGroup, command.Name, description, command.Shortcut?.DisplayText ?? ""));
            }
        }

        return entries;
    }

    private static string BuiltInDescription(string action) =>
        ShortcutCatalog.TryGetValue(action, out var d)
            ? (Loc.IsJapanese ? d.Ja : d.En)
            : "";

    /// <summary>First non-empty line of a command's run text, truncated for the list.</summary>
    private static string FirstLine(string? run)
    {
        if (string.IsNullOrWhiteSpace(run))
        {
            return "";
        }

        foreach (var raw in run.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }
            return line.Length > 80 ? line[..79] + "…" : line;
        }
        return "";
    }
}
