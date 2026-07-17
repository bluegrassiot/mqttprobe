using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Velopack;

namespace MqttProbe.WinUI;

/// <summary>
/// Replaces the WinUI-generated Main (DISABLE_XAML_GENERATED_MAIN) so the
/// Velopack hook runs before any XAML infrastructure. Velopack uses this to
/// finish installs/updates and to exit fast on hook invocations.
/// </summary>
public static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        VelopackApp.Build().Run();

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Microsoft.UI.Xaml.Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
    }
}
