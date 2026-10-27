using Microsoft.UI.Xaml;

namespace ImageForge.App;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => CrashLog.Write("AppDomain", e.ExceptionObject);
        UnhandledException += (_, e) => CrashLog.Write("XAML", e.Exception);
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            _window = new MainWindow();
            _window.Activate();
        }
        catch (Exception ex)
        {
            CrashLog.Write("OnLaunched", ex);
            throw;
        }
    }
}
