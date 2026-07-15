using UIKit;
using Velopack;

namespace MqttProbe
{
    public class Program
    {
        static void Main(string[] args)
        {
            VelopackApp.Build().Run();
            UIApplication.Main(args, null, typeof(AppDelegate));
        }
    }
}
