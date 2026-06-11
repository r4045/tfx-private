using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Tfx;

public enum FileConflictChoice
{
    Overwrite,
    KeepBoth,
    Cancel,
}

/// <summary>
/// Asks the user how to resolve a paste-time name collision: overwrite the
/// existing entry, keep both (paste under a numbered name), or cancel the whole
/// operation. "Apply to all" reuses the chosen action for every remaining
/// collision in the same paste. Mirrors the code-built look of the other
/// dialogs (e.g. <see cref="ConfirmDialog"/>, <see cref="BookmarkDialog"/>).
/// </summary>
public sealed class FileConflictDialog : Window
{
    private readonly CheckBox _applyToAll;

    public FileConflictChoice Choice { get; private set; } = FileConflictChoice.Cancel;

    public bool ApplyToAll => _applyToAll.IsChecked == true;

    public FileConflictDialog(string name, bool isDirectory, bool allowOverwrite, bool canApplyToAll)
    {
        Title = Loc.T("Name conflict");
        Owner = Application.Current.MainWindow;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        MinWidth = 460;
        MaxWidth = 600;
        Background = new SolidColorBrush(Color.FromRgb(15, 19, 23));
        Foreground = new SolidColorBrush(Color.FromRgb(222, 230, 236));
        FontFamily = new FontFamily("Consolas, Yu Gothic UI");

        var root = new Grid { Margin = new Thickness(18) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var kind = isDirectory ? Loc.T("folder") : Loc.T("file");
        var message = new TextBlock
        {
            Text = Loc.F("The {0} \"{1}\" already exists in the destination.", kind, name),
            MaxWidth = 540,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14),
            Foreground = Foreground,
        };
        Grid.SetRow(message, 0);
        root.Children.Add(message);

        _applyToAll = new CheckBox
        {
            Content = Loc.T("Apply to all remaining conflicts"),
            Foreground = Foreground,
            Margin = new Thickness(0, 0, 0, 16),
            Visibility = canApplyToAll ? Visibility.Visible : Visibility.Collapsed,
        };
        Grid.SetRow(_applyToAll, 1);
        root.Children.Add(_applyToAll);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        if (allowOverwrite)
        {
            var overwrite = new Button
            {
                Content = Loc.T("Overwrite"),
                MinWidth = 96,
                Margin = new Thickness(0, 0, 8, 0),
            };
            overwrite.Click += (_, _) => CloseWith(FileConflictChoice.Overwrite);
            buttons.Children.Add(overwrite);
        }

        var keepBoth = new Button
        {
            Content = Loc.T("Keep both"),
            IsDefault = true,
            MinWidth = 96,
            Margin = new Thickness(0, 0, 8, 0),
        };
        keepBoth.Click += (_, _) => CloseWith(FileConflictChoice.KeepBoth);
        buttons.Children.Add(keepBoth);

        var cancel = new Button
        {
            Content = Loc.T("Cancel"),
            IsCancel = true,
            MinWidth = 96,
        };
        cancel.Click += (_, _) => CloseWith(FileConflictChoice.Cancel);
        buttons.Children.Add(cancel);

        Grid.SetRow(buttons, 2);
        root.Children.Add(buttons);
        Content = root;

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                CloseWith(FileConflictChoice.Cancel);
            }
        };
    }

    private void CloseWith(FileConflictChoice choice)
    {
        Choice = choice;
        // Cancel returns false so the caller can abort the whole paste; the two
        // actionable choices return true.
        DialogResult = choice != FileConflictChoice.Cancel;
    }
}
