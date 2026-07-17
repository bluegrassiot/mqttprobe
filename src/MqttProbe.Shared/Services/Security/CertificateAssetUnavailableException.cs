namespace MqttProbe.Services.Security;

public class CertificateAssetUnavailableException : Exception
{
    public string AssetId { get; }

    public CertificateAssetUnavailableException(string assetId)
        : base($"The configured client certificate (asset {assetId}) is unavailable or corrupt.")
    {
        AssetId = assetId;
    }
}
