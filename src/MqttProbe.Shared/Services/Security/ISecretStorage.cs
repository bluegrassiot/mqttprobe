namespace MqttProbe.Services.Security;

public interface ISecretStorage
{
    public Task<string?> GetAsync(string key);
    public Task SetAsync(string key, string value);
    public Task RemoveAsync(string key);
}
