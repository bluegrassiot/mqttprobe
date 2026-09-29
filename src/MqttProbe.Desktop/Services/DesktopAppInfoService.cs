using System.Reflection;
using MqttProbe.Core.Services.Platform;

namespace MqttProbe.Desktop.Services;

public class DesktopAppInfoService : IAppInfoService
{
    public bool RequiresAuthentication => false;
    public bool IsNative => true;
    public bool IsOidcMode => false;
    public string? ProviderDisplayName => null;

    public string GetVersion() =>
        AppVersionResolver.Resolve(
            () => Assembly.GetExecutingAssembly().GetName().Version?.ToString());
}
