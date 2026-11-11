using UIKit;
#if !MQTTPROBE_MAC_APP_STORE
using Velopack;
#endif

namespace MqttProbe
{
    public class Program
    {
        static void Main(string[] args)
        {
#if !MQTTPROBE_MAC_APP_STORE
            VelopackApp.Build().Run();
#endif
            UIApplication.Main(args, null, typeof(AppDelegate));
        }
    }
}
