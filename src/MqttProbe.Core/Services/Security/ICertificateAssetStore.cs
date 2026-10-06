using MqttProbe.Core.Models.Mqtt;

namespace MqttProbe.Core.Services.Security;

public interface ICertificateAssetStore
{
    public Task<string> ImportAsync(Guid ownerConnectionId, CertificateImportRequest request);
    public Task<ClientCertificateBundle?> LoadAsync(Guid ownerConnectionId, string assetId);
    public Task DeleteAsync(Guid ownerConnectionId, string assetId);
    public Task<IReadOnlyList<(Guid OwnerId, string AssetId)>> ListAssetsAsync();
    public Task<string?> DuplicateAsync(Guid sourceOwnerId, string sourceAssetId, Guid targetOwnerId);
    public string CertificatesDirectory { get; }
}
