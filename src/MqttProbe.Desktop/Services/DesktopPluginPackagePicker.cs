using MqttProbe.Core.Services.Plugins.Packaging;

namespace MqttProbe.Desktop.Services;

public class DesktopPluginPackagePicker : IPluginPackagePicker
{
    private readonly IPhotinoWindowAccessor _accessor;

    public DesktopPluginPackagePicker(IPhotinoWindowAccessor accessor)
    {
        _accessor = accessor;
    }

    public async Task<byte[]?> PickPackageAsync(string title, string[] extensions, long maxBytes)
    {
        var window = _accessor.Window
            ?? throw new InvalidOperationException("PhotinoWindow not yet initialized.");

        var filters = extensions.Select(ext => (ext.TrimStart('.'), new[] { $"*{ext}" })).ToArray();
        var files = await window.ShowOpenFileAsync(title, null, false, filters);

        if (files is not { Length: > 0 })
            return null;

        var filePath = files[0];
        var fileInfo = new FileInfo(filePath);
        if (fileInfo.Length > maxBytes)
            return null;

        return await File.ReadAllBytesAsync(filePath);
    }
}
