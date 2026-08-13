namespace MqttProbe.Core.Services.Plugins.Packaging;

public interface IPluginPackagePicker
{
    public Task<byte[]?> PickPackageAsync(string title, string[] extensions, long maxBytes);
}
