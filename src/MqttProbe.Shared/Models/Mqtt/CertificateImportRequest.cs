namespace MqttProbe.Models.Mqtt;

public record CertificateImportRequest(
    CertificateInputMode Mode,
    byte[] CertificateBytes,
    byte[]? PrivateKeyBytes,
    string? Password);
