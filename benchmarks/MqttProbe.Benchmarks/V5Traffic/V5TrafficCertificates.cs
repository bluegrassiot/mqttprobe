using System.Security.Cryptography.X509Certificates;

namespace MqttProbe.Benchmarks.V5Traffic;

internal sealed class V5TrafficCertificates : IDisposable
{
    private static readonly X509KeyStorageFlags _clientCertificateLoadFlags =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? X509KeyStorageFlags.DefaultKeySet
            : X509KeyStorageFlags.EphemeralKeySet;

    private V5TrafficCertificates(
        X509Certificate2Collection? trustChain,
        X509Certificate2Collection? clientCertificates)
    {
        TrustChain = trustChain;
        ClientCertificates = clientCertificates;
    }

    public X509Certificate2Collection? TrustChain { get; }
    public X509Certificate2Collection? ClientCertificates { get; }

    public static V5TrafficCertificates? Load(V5TrafficOptions options)
    {
        if (!options.UseTls)
            return null;

        X509Certificate2Collection? trustChain = null;
        X509Certificate2Collection? clientCertificates = null;

        try
        {
            if (options.CaCertificatePath is not null)
                trustChain = LoadCa(options.CaCertificatePath, options.CaCertificatePassword);

            if (options.ClientCertificatePath is not null)
            {
                var certificate = X509CertificateLoader.LoadPkcs12FromFile(
                    options.ClientCertificatePath,
                    options.ClientCertificatePassword,
                    _clientCertificateLoadFlags);
                clientCertificates = [certificate];
            }

            return new V5TrafficCertificates(trustChain, clientCertificates);
        }
        catch
        {
            DisposeCollection(trustChain);
            DisposeCollection(clientCertificates);
            throw;
        }
    }

    public void Dispose()
    {
        DisposeCollection(TrustChain);
        DisposeCollection(ClientCertificates);
    }

    private static X509Certificate2Collection LoadCa(string path, string? password)
    {
        var extension = Path.GetExtension(path);
        if (extension.Equals(".pfx", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".p12", StringComparison.OrdinalIgnoreCase))
        {
            return X509CertificateLoader.LoadPkcs12CollectionFromFile(
                path, password, X509KeyStorageFlags.EphemeralKeySet);
        }

        if (extension.Equals(".pem", StringComparison.OrdinalIgnoreCase))
        {
            var collection = new X509Certificate2Collection();
            collection.ImportFromPemFile(path);
            if (collection.Count == 0)
                throw new InvalidOperationException($"CA certificate file contains no certificates: {path}");
            return collection;
        }

        return [X509CertificateLoader.LoadCertificateFromFile(path)];
    }

    private static void DisposeCollection(X509Certificate2Collection? collection)
    {
        if (collection is null)
            return;

        foreach (var certificate in collection)
            certificate.Dispose();
    }
}
