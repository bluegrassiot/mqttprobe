using System.Reflection;
using MqttProbe.Services.Platform;

namespace MqttProbe.Services;

public class DesktopAppInfoService : IAppInfoService
{
    public bool RequiresAuthentication => false;
    public bool IsNative => true;

    public string GetVersion() =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0";
}
