namespace MqttProbe.Desktop.Services.Security;

public abstract record SecretKeyLoadResult
{
    public sealed record Found : SecretKeyLoadResult
    {
        public byte[] Key { get; }

        public Found(byte[] key)
        {
            if (key is null || key.Length != MasterKeyConstants.KeySize)
                throw new ArgumentException(
                    $"Master key must be exactly {MasterKeyConstants.KeySize} bytes.", nameof(key));
            Key = key;
        }
    }

    public sealed record NotFound : SecretKeyLoadResult;
    public sealed record FacilityUnavailable(string? Detail = null) : SecretKeyLoadResult;
    public sealed record UnexpectedFailure(Exception Error) : SecretKeyLoadResult;
}

public abstract record SecretKeyStoreResult
{
    public sealed record Stored : SecretKeyStoreResult;
    public sealed record FacilityUnavailable(string? Detail = null) : SecretKeyStoreResult;
    public sealed record UnexpectedFailure(Exception Error) : SecretKeyStoreResult;
}
