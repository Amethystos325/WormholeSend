using System.Security.Cryptography;
using System.Text;
using System.Windows;

namespace WormholeSend;

public partial class App : Application
{
    AppController? controller;
    Mutex? instance;
    EventWaitHandle? showSignal;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var options = Options.Parse(e.Args);
        if (options.Log) Log.Enable();
        // One app per data directory. Starting it again opens the settings
        // of the one already running.
        if (!options.Fake)
        {
            var name = options.Home is { } home
                ? "WormholeSend-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(home.ToLowerInvariant())))[..12]
                : "WormholeSend";
            instance = new Mutex(true, $@"Local\{name}", out var first);
            showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\{name}-show");
            if (!first)
            {
                showSignal.Set();
                Shutdown();
                return;
            }
            ThreadPool.RegisterWaitForSingleObject(showSignal,
                (_, _) => Dispatcher.BeginInvoke(() => controller?.OpenSettings()), null, -1, executeOnlyOnce: false);
        }
        // Before any window exists: WPF-UI repaints the main window when it
        // applies a theme, and an island window must never be that.
        Wpf.Ui.Appearance.ApplicationThemeManager.ApplySystemTheme();
        controller = new AppController(options);
        controller.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        instance?.Dispose();
        showSignal?.Dispose();
        base.OnExit(e);
    }
}
