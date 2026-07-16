namespace MqttProbe.Desktop.Services.Security;

public sealed class UnavailableSecretKeyProtector : ISecretKeyProtector
{
    public Task<SecretKeyLoadResult> LoadAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<SecretKeyLoadResult>(
            new SecretKeyLoadResult.FacilityUnavailable("No OS secret key backend for this platform."));

    public Task<SecretKeyStoreResult> StoreAsync(
        ReadOnlyMemory<byte> key, CancellationToken cancellationToken = default) =>
        Task.FromResult<SecretKeyStoreResult>(
            new SecretKeyStoreResult.FacilityUnavailable("No OS secret key backend for this platform."));
}
