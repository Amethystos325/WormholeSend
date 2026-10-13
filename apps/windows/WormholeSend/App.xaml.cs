using System.Windows;

namespace WormholeSend;

public partial class App : Application
{
    AppController? controller;
    Mutex? instance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var options = Options.Parse(e.Args);
        if (options.Log) Log.Enable();
        // One island per session; check runs may overlap it.
        if (options.Demo is null && !options.SelfTest)
        {
            instance = new Mutex(true, @"Local\WormholeSend", out var first);
            if (!first)
            {
                Shutdown();
                return;
            }
        }
        controller = new AppController(options);
        controller.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        instance?.Dispose();
        base.OnExit(e);
    }
}
