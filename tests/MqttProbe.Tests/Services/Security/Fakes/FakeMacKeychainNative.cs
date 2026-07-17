using MqttProbe.Desktop.Services.Security;

namespace MqttProbe.Shared.Tests.Services.Security.Fakes;

public sealed class FakeMacKeychainNative : IMacKeychainNative
{
    public int CopyMatchingStatus { get; set; }
    public byte[]? CopyMatchingData { get; set; }
    public int AddStatus { get; set; }
    public int UpdateStatus { get; set; }
    public int DeleteStatus { get; set; }

    public string? LastService { get; private set; }
    public string? LastAccount { get; private set; }
    public int AddCallCount { get; private set; }
    public int UpdateCallCount { get; private set; }

    public int CopyMatching(string service, string account, out byte[]? data)
    {
        LastService = service;
        LastAccount = account;
        data = CopyMatchingData;
        return CopyMatchingStatus;
    }

    public int Add(string service, string account, ReadOnlySpan<byte> data)
    {
        LastService = service;
        LastAccount = account;
        AddCallCount++;
        return AddStatus;
    }

    public int Update(string service, string account, ReadOnlySpan<byte> data)
    {
        LastService = service;
        LastAccount = account;
        UpdateCallCount++;
        return UpdateStatus;
    }

    public int Delete(string service, string account)
    {
        LastService = service;
        LastAccount = account;
        return DeleteStatus;
    }
}
