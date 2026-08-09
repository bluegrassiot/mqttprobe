using MqttProbe.Core.Models.Mqtt;

namespace MqttProbe.Core.Services.Security;

public interface ICertificateAssetImportPipeline
{
    public Task<(string AssetId, string TempPath)> ImportStagedAsync(Guid ownerConnectionId, CertificateImportRequest request);
    public Task<string> PublishAsync(string assetId, string tempPath);
}
