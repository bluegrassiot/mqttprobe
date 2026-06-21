using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using MqttProbe.Services.Security;

namespace MqttProbe.Web.Services;

public class DataProtectionSecretStorage : ISecretStorage
{
    private readonly IDataProtector _protector;
    private readonly string _storePath;
    private readonly ILogger<DataProtectionSecretStorage>? _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private Dictionary<string, string>? _store;
    private bool _loadFailed;

    public DataProtectionSecretStorage(IDataProtectionProvider provider, string storePath,
        ILogger<DataProtectionSecretStorage>? logger = null)
    {
        _protector = provider.CreateProtector("MqttProbe.Secrets.v1");
        _storePath = storePath;
        _logger = logger;
    }

    public async Task<string?> GetAsync(string key)
    {
        await _lock.WaitAsync();
        try
        {
            var store = await GetStoreAsync();
            store.TryGetValue(key, out var value);
            return value;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SetAsync(string key, string value)
    {
        await _lock.WaitAsync();
        try
        {
            var store = await GetStoreAsync();
            store[key] = value;
            await PersistStoreAsync(store);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task RemoveAsync(string key)
    {
        await _lock.WaitAsync();
        try
        {
            var store = await GetStoreAsync();
            store.Remove(key);
            await PersistStoreAsync(store);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<Dictionary<string, string>> GetStoreAsync()
    {
        if (_loadFailed)
            throw new InvalidOperationException("Secret store could not be loaded; mutations are blocked to prevent overwriting valid data.");
        return _store ??= await LoadStoreAsync();
    }

    private async Task<Dictionary<string, string>> LoadStoreAsync()
    {
        if (!File.Exists(_storePath)) return new();
        try
        {
            var cipherText = await File.ReadAllTextAsync(_storePath);
            var json = _protector.Unprotect(cipherText);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new();
        }
        catch (Exception ex)
        {
            _loadFailed = true;
            _logger?.LogWarning(ex, "Failed to load secret store from {Path}; mutations will be blocked until the file is repaired or removed.", _storePath);
            throw new InvalidOperationException("Secret store could not be loaded; mutations are blocked to prevent overwriting valid data.", ex);
        }
    }

    private async Task PersistStoreAsync(Dictionary<string, string> store)
    {
        var json = JsonSerializer.Serialize(store);
        var cipherText = _protector.Protect(json);
        await File.WriteAllTextAsync(_storePath, cipherText);
    }
}
