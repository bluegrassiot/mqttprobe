using MqttProbe.Desktop.Services.Security;

namespace MqttProbe.Shared.Tests.Services.Security.Fakes;

public sealed class FakeLinuxLibsecretNative : ILinuxLibsecretNative
{
    public int LookupCode { get; set; } = 1;
    public string? LookupPassword { get; set; }
    public string? LookupError { get; set; }
    public int StoreCode { get; set; } = 0;
    public string? StoreError { get; set; }
    public int LookupCallCount { get; private set; }
    public int StoreCallCount { get; private set; }

    public int Lookup(string schemaName, IReadOnlyDictionary<string, string> attributes,
        out string? password, out string? errorMessage)
    {
        LookupCallCount++;
        password = LookupPassword;
        errorMessage = LookupError;
        return LookupCode;
    }

    public int Store(string schemaName, IReadOnlyDictionary<string, string> attributes,
        string label, string password, out string? errorMessage)
    {
        StoreCallCount++;
        errorMessage = StoreError;
        if (StoreCode == 0)
            LookupPassword = password;
        return StoreCode;
    }

    public int Clear(string schemaName, IReadOnlyDictionary<string, string> attributes,
        out string? errorMessage)
    {
        errorMessage = null;
        return 0;
    }
}
