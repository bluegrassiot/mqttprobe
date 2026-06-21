using MqttProbe.Services.Platform;

namespace MqttProbe.Services;

public class AppInfoService : IAppInfoService
{
    public bool RequiresAuthentication => false;
    public bool IsNative => true;

    public string GetVersion() => AppInfo.VersionString;
}
