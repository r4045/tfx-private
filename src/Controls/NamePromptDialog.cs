using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Tfx;

public sealed class NamePromptDialog : Window
{
    private readonly TextBox _textBox;
    private readonly TextBlock _errorBlock;
    private readonly Func<string, string?>? _validate;

    /// <param name="selectStem">
    /// Select only the part before the last '.' on open (file rename), so
    /// typing replaces the name but keeps the extension.
    /// </param>
    /// <param name="validate">
    /// Returns an error message to keep the dialog open, or null to accept.
    /// </param>
    public NamePromptDialog(string title, string label, string defaultValue,
        bool selectStem = false, Func<string, string?>? validate = null)
    {
        _validate = validate;
        Title = title;
        Owner = Application.Current.MainWindow;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        MinWidth = 380;
        Background = new SolidColorBrush(Color.FromRgb(15, 19, 23));
        Foreground = new SolidColorBrush(Color.FromRgb(222, 230, 236));
        FontFamily = new FontFamily("Consolas, Yu Gothic UI");

        var root = new Grid { Margin = new Thickness(16) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var labelBlock = new TextBlock
        {
            Text = label,
            Margin = new Thickness(0, 0, 0, 8),
            Foreground = Foreground
        };
        Grid.SetRow(labelBlock, 0);
        root.Children.Add(labelBlock);

        _textBox = new TextBox
        {
            Text = defaultValue,
            MinWidth = 340,
            Padding = new Thickness(6, 3, 6, 3),
            Background = new SolidColorBrush(Color.FromRgb(13, 16, 19)),
            Foreground = Foreground,
            CaretBrush = new SolidColorBrush(Color.FromRgb(126, 211, 164)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(47, 58, 67))
        };
        Grid.SetRow(_textBox, 1);
        root.Children.Add(_textBox);

        _errorBlock = new TextBlock
        {
            Margin = new Thickness(0, 6, 0, 0),
            Foreground = new SolidColorBrush(Color.FromRgb(248, 113, 113)),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 340,
            HorizontalAlignment = HorizontalAlignment.Left,
            Visibility = Visibility.Collapsed
        };
        Grid.SetRow(_errorBlock, 2);
        root.Children.Add(_errorBlock);
        _textBox.TextChanged += (_, _) => _errorBlock.Visibility = Visibility.Collapsed;

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0)
        };

        var ok = new Button
        {
            Content = Loc.T("OK"),
            IsDefault = true,
            MinWidth = 76,
            Margin = new Thickness(0, 0, 8, 0)
        };
        ok.Click += (_, _) => Accept();
        buttons.Children.Add(ok);

        var cancel = new Button
        {
            Content = Loc.T("Cancel"),
            IsCancel = true,
            MinWidth = 76
        };
        buttons.Children.Add(cancel);

        Grid.SetRow(buttons, 3);
        root.Children.Add(buttons);
        Content = root;

        Loaded += (_, _) =>
        {
            _textBox.Focus();
            var dot = selectStem ? _textBox.Text.LastIndexOf('.') : -1;
            if (dot > 0)
            {
                _textBox.Select(0, dot);
            }
            else
            {
                _textBox.SelectAll();
            }
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                DialogResult = false;
            }
        };
    }

    public string EnteredText => _textBox.Text;

    private void Accept()
    {
        var error = _validate?.Invoke(_textBox.Text);
        if (!string.IsNullOrEmpty(error))
        {
            _errorBlock.Text = error;
            _errorBlock.Visibility = Visibility.Visible;
            _textBox.Focus();
            return;
        }

        DialogResult = true;
    }
}
