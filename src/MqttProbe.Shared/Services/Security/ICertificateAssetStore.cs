using MqttProbe.Models.Mqtt;

namespace MqttProbe.Services.Security;

public interface ICertificateAssetStore
{
    public Task<string> ImportAsync(Guid ownerConnectionId, CertificateImportRequest request);
    public Task<ClientCertificateBundle?> LoadAsync(Guid ownerConnectionId, string assetId);
    public Task DeleteAsync(Guid ownerConnectionId, string assetId);
    public Task<IReadOnlyList<(Guid OwnerId, string AssetId)>> ListAssetsAsync();
    public string CertificatesDirectory { get; }
}
