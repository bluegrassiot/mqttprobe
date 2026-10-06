namespace MqttProbe.Core.Services.Platform;

public interface IAppInfoService
{
    public string GetVersion();
    public bool RequiresAuthentication { get; }
    public bool IsNative { get; }
    public bool IsOidcMode { get; }
    public string? ProviderDisplayName { get; }
}
