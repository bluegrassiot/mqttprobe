#if IOS
using Foundation;
using MqttProbe.Services.Security;
using Security;

namespace MqttProbe.Maui.Services;

public class iOSCertificateEnvelopeKeyStore : ICertificateEnvelopeKeyStore
{
    private const string ServiceName = "MqttProbe.CertEnvelopes";

    public Task<string?> GetAsync(string key)
    {
        var query = new SecRecord(SecKind.GenericPassword)
        {
            Service = ServiceName,
            Account = key
        };

        var result = SecKeyChain.QueryAsRecord(query, out var status);
        if (status == SecStatusCode.Success && result?.ValueData is not null)
        {
            return Task.FromResult<string?>(
                System.Text.Encoding.UTF8.GetString(result.ValueData.ToArray()));
        }

        if (status != SecStatusCode.ItemNotFound)
            throw new InvalidOperationException($"Keychain query failed with status {status}");

        return Task.FromResult<string?>(null);
    }

    public Task SetAsync(string key, string value)
    {
        RemoveFromKeychain(key);

        var record = new SecRecord(SecKind.GenericPassword)
        {
            Service = ServiceName,
            Account = key,
            ValueData = NSData.FromArray(System.Text.Encoding.UTF8.GetBytes(value)),
            Accessible = SecAccessible.WhenUnlockedThisDeviceOnly
        };

        var status = SecKeyChain.Add(record);
        if (status != SecStatusCode.Success)
            throw new InvalidOperationException($"Keychain write failed with status {status}");

        return Task.CompletedTask;
    }

    public Task RemoveAsync(string key)
    {
        RemoveFromKeychain(key);
        return Task.CompletedTask;
    }

    private static void RemoveFromKeychain(string key)
    {
        var record = new SecRecord(SecKind.GenericPassword)
        {
            Service = ServiceName,
            Account = key
        };
        var status = SecKeyChain.Remove(record);
        if (status != SecStatusCode.Success && status != SecStatusCode.ItemNotFound)
            throw new InvalidOperationException($"Keychain remove failed with status {status}");
    }
}
#endif
