using UIKit;
#if !MQTTPROBE_MAC_APP_STORE
using Velopack;
#endif

namespace MqttProbe;

public static class Program
{
    private static void Main(string[] args)
    {
#if !MQTTPROBE_MAC_APP_STORE
        VelopackApp.Build().Run();
#endif
        UIApplication.Main(args, null, typeof(MacCatalystApplication));
    }
}
