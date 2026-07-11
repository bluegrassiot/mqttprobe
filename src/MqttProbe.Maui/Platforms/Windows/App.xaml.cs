using Microsoft.UI.Xaml;

namespace MqttProbe.WinUI;

public partial class App : MauiWinUIApplication
{
    public App()
    {
        this.InitializeComponent();
        this.UnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

    private static void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
#if DEBUG
        System.Diagnostics.Debugger.Break();
        e.Handled = false;
#else
        e.Handled = true;
#endif
    }

    private static void OnDomainUnhandledException(object sender, System.UnhandledExceptionEventArgs e)
    {
#if DEBUG
        System.Diagnostics.Debugger.Break();
#endif
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
#if DEBUG
        System.Diagnostics.Debugger.Break();
#endif
        e.SetObserved();
    }
}
