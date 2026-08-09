using MqttProbe.Core.Services.Plugins.Packaging;

namespace MqttProbe.Web.Services;

// Mirrors WebCertificateFilePicker: the web host uses InputFile for uploads, so this
// always resolves to a no-op rather than leaving IPluginPackagePicker unregistered,
// which would throw the moment anything (a stray injection, a future code path) resolved it.
public class WebPluginPackagePicker : IPluginPackagePicker
{
    public Task<byte[]?> PickPackageAsync(string title, string[] extensions, long maxBytes)
        => Task.FromResult<byte[]?>(null);
}
