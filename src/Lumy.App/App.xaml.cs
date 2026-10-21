using Microsoft.UI.Xaml;

namespace Lumy.App;

public partial class App : Application
{
    private MainWindow? _window;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            CrashLog.Write(e.Exception);

            // Once the window is up, a failed thumbnail or a file that vanished mid-read is not worth losing
            // the session over: log it and keep going. Before that there is nothing to keep, so let it end.
            if (_window is not null)
                e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => CrashLog.Write(e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            CrashLog.Write(e.Exception);
            e.SetObserved();
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            // "Lumy.exe D:\Photos\Shoot" opens that folder; the installer's "Открыть в Lumy" folder menu runs this.
            // "Lumy.exe --self-test D:\Photos" opens it, clicks through every view and exits (used by CI).
            var arguments = Environment.GetCommandLineArgs().Skip(1).ToList();
            var folder = arguments.FirstOrDefault(Directory.Exists);
            _window = new MainWindow(folder, selfTest: arguments.Contains("--self-test"));
            _window.Activate();
        }
        catch (Exception e)
        {
            CrashLog.Write(e);
            throw;
        }
    }
}
