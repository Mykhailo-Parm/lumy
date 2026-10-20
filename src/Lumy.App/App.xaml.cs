using Microsoft.UI.Xaml;

namespace Lumy.App;

public partial class App : Application
{
    private MainWindow? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // "Lumy.exe D:\Photos\Shoot" opens that folder; used by the Explorer context menu later on.
        var folder = Environment.GetCommandLineArgs().Skip(1).FirstOrDefault(Directory.Exists);
        _window = new MainWindow(folder);
        _window.Activate();
    }
}
