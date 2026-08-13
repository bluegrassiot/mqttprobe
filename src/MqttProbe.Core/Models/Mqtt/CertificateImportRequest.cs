namespace MqttProbe.Core.Models.Mqtt;

public record CertificateImportRequest(
    CertificateInputMode Mode,
    byte[] CertificateBytes,
    byte[]? PrivateKeyBytes,
    string? Password,
    bool SkipCanonicalExport = false);
