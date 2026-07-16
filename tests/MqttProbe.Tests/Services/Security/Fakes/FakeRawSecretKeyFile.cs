using MqttProbe.Desktop.Services.Security;

namespace MqttProbe.Shared.Tests.Services.Security.Fakes;

public sealed class FakeRawSecretKeyFile : IRawSecretKeyFile
{
    public byte[]? Bytes { get; set; }
    public bool DeleteShouldFail { get; set; }
    public bool Exists => Bytes is not null;

    public byte[]? TryRead()
    {
        if (Bytes is null)
        {
            return null;
        }

        if (Bytes.Length != MasterKeyConstants.KeySize)
        {
            throw new SecretStorageException("Raw key file has invalid length.");
        }

        return (byte[])Bytes.Clone();
    }

    public void Write(ReadOnlySpan<byte> key)
    {
        if (key.Length != MasterKeyConstants.KeySize)
        {
            throw new SecretStorageException("Raw key must be 32 bytes.");
        }

        Bytes = key.ToArray();
    }

    public bool TryDelete()
    {
        if (DeleteShouldFail)
        {
            return false;
        }

        Bytes = null;
        return true;
    }
}
