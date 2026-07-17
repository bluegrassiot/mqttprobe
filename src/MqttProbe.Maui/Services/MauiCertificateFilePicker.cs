using MqttProbe.Services.Security;

namespace MqttProbe.Maui.Services;

public class MauiCertificateFilePicker : ICertificateFilePicker
{
    public async Task<byte[]?> PickFileAsync(string title, string[] extensions, long maxBytes)
    {
        var fileTypes = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
        {
            { DevicePlatform.Android, new[] { "*/*" } },
            { DevicePlatform.iOS, extensions.Select(ext => ext.ToLowerInvariant() switch
            {
                ".pfx" => "com.rsa.pkcs-12",
                ".p12" => "com.rsa.pkcs-12",
                ".pem" => "public.x509-certificate",
                ".crt" => "public.x509-certificate",
                _ => "public.data"
            }) },
            { DevicePlatform.MacCatalyst, extensions.Select(ext => ext.ToLowerInvariant() switch
            {
                ".pfx" => "com.rsa.pkcs-12",
                ".p12" => "com.rsa.pkcs-12",
                ".pem" => "public.x509-certificate",
                ".crt" => "public.x509-certificate",
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
                throw new CertificateImportException("File exceeds maximum size of 1 MB.");
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
                    throw new CertificateImportException("File exceeds maximum size of 1 MB.");
                ms.Write(buffer, 0, bytesRead);
            }
            return ms.ToArray();
        }
    }
}
