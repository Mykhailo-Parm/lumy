using Microsoft.UI.Xaml;

namespace Lumy.App;

public partial class App : Application
{
    private MainWindow? _window;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) => CrashLog.Write(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => CrashLog.Write(e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => CrashLog.Write(e.Exception);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            // "Lumy.exe D:\Photos\Shoot" opens that folder; used by the Explorer context menu later on.
            var folder = Environment.GetCommandLineArgs().Skip(1).FirstOrDefault(Directory.Exists);
            _window = new MainWindow(folder);
            _window.Activate();
        }
        catch (Exception e)
        {
            CrashLog.Write(e);
            throw;
        }
    }
}
