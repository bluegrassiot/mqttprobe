namespace MqttProbe.Desktop.Services.Security;

public interface ILinuxLibsecretNative
{
    public int Lookup(string schemaName, IReadOnlyDictionary<string, string> attributes,
        out string? password, out string? errorMessage);

    public int Store(string schemaName, IReadOnlyDictionary<string, string> attributes,
        string label, string password, out string? errorMessage);

    public int Clear(string schemaName, IReadOnlyDictionary<string, string> attributes,
        out string? errorMessage);
}
