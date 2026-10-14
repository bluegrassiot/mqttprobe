namespace MqttProbe.Core.Services.Platform;

public interface IAppInfoService
{
    public string GetVersion();
    public bool RequiresAuthentication { get; }
    public bool IsNative { get; }
    public bool IsOidcMode { get; }
    public string? ProviderDisplayName { get; }

    public bool SupportsPluginInstallation => true;

    // Package import is broader than executable plugins: a protobuf schema package is
    // data, so hosts that bar user-supplied code still import them. This narrows only
    // the binary half; the loader and inspector guards refuse assemblies independently.
    public bool SupportsAssemblyPlugins => true;
}
