using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace Tfx;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    // A burst of UI-thread exceptions usually means a loop (e.g. a throwing
    // layout / render callback). Keeping the app alive then just spams the
    // user, so after this many within the window we let the process die.
    private const int MaxHandledInWindow = 5;
    private static readonly TimeSpan HandledWindow = TimeSpan.FromSeconds(10);
    private readonly Queue<DateTime> _recentHandled = new();
    private bool _showingErrorDialog;

    protected override void OnStartup(StartupEventArgs e)
    {
        RegisterCrashHandlers();
        base.OnStartup(e);

        var options = StartupOptions.Parse(e.Args);
        if (options.ShowHelp || options.HasError)
        {
            options.WriteHelp();
            Shutdown(options.HasError ? 1 : 0);
            return;
        }

        new MainWindow(options).Show();
    }

    private void RegisterCrashHandlers()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        // Exceptions on non-UI threads cannot be recovered from; at least
        // leave the stack trace behind before the process goes away.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            CrashLog.Write(args.IsTerminating ? "AppDomain (terminating)" : "AppDomain", args.ExceptionObject as Exception);

        // Fire-and-forget tasks that faulted. Observing them keeps them from
        // escalating, and the log tells us they happened.
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            CrashLog.Write("UnobservedTask", args.Exception);
            args.SetObserved();
        };
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        CrashLog.Write("Dispatcher", e.Exception);

        var now = DateTime.Now;
        _recentHandled.Enqueue(now);
        while (_recentHandled.Count > 0 && now - _recentHandled.Peek() > HandledWindow)
        {
            _recentHandled.Dequeue();
        }

        if (_recentHandled.Count > MaxHandledInWindow)
        {
            // Too many in a row: let it terminate (already logged).
            return;
        }

        // A UI-thread exception from an event handler (a key press, a menu
        // click, a refresh tick) normally leaves the app usable, so keep it
        // running instead of closing the window and losing the user's tabs.
        e.Handled = true;

        if (_showingErrorDialog)
        {
            return;
        }

        try
        {
            _showingErrorDialog = true;
            var message = Loc.F("An unexpected error occurred. tfx will keep running.\n\n{0}\n\nDetails were written to:\n{1}",
                e.Exception.Message, CrashLog.LogPath);
            if (MainWindow is { IsLoaded: true } owner)
            {
                MessageBox.Show(owner, message, "tfx", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else
            {
                MessageBox.Show(message, "tfx", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch
        {
        }
        finally
        {
            _showingErrorDialog = false;
        }
    }
}
