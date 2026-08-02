using MqttProbe.Services.Plugins.Packaging;

namespace MqttProbe.Maui.Services;

public class MauiPluginPackagePicker : IPluginPackagePicker
{
    private static readonly string[] _androidAnyType = ["*/*"];

    public async Task<byte[]?> PickPackageAsync(string title, string[] extensions, long maxBytes)
    {
        var fileTypes = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
        {
            // Many Android storage providers (Drive, Dropbox, some file managers and stock
            // Downloads providers) report zip files as application/octet-stream rather than
            // application/zip, which hides legitimate .zip files from the picker; matches the
            // certificate picker's approach of not filtering by MIME type on Android at all.
            { DevicePlatform.Android, _androidAnyType },
            { DevicePlatform.iOS, extensions.Select(ext => ext.ToLowerInvariant() switch
            {
                ".zip" => "public.zip-archive",
                _ => "public.data"
            }) },
            { DevicePlatform.MacCatalyst, extensions.Select(ext => ext.ToLowerInvariant() switch
            {
                ".zip" => "public.zip-archive",
                _ => "public.data"
            }) },
            { DevicePlatform.WinUI, extensions }
        });

        var options = new PickOptions
        {
            PickerTitle = title,
            FileTypes = fileTypes
        };

        var result = await FilePicker.Default.PickAsync(options);
        if (result is null)
            return null;

        using var stream = await result.OpenReadAsync();
        if (stream.CanSeek)
        {
            if (stream.Length > maxBytes)
                return null;
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            return ms.ToArray();
        }
        else
        {
            using var ms = new MemoryStream();
            var buffer = new byte[8192];
            long totalRead = 0;
            int bytesRead;
            while ((bytesRead = await stream.ReadAsync(buffer)) > 0)
            {
                totalRead += bytesRead;
                if (totalRead > maxBytes)
                    return null;
                await ms.WriteAsync(buffer.AsMemory(0, bytesRead));
            }
            return ms.ToArray();
        }
    }
}
