using MqttProbe.Services.Security;

namespace MqttProbe.Desktop.Services;

public class DesktopCertificateFilePicker : ICertificateFilePicker
{
    private readonly IPhotinoWindowAccessor _accessor;

    public DesktopCertificateFilePicker(IPhotinoWindowAccessor accessor)
    {
        _accessor = accessor;
    }

    public async Task<byte[]?> PickFileAsync(string title, string[] extensions, long maxBytes)
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
            throw new CertificateImportException("File exceeds maximum size of 1 MB.");

        return await File.ReadAllBytesAsync(filePath);
    }
}
