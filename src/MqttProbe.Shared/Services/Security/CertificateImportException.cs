namespace MqttProbe.Services.Security;

public class CertificateImportException : Exception
{
    public CertificateImportException(string message) : base(message) { }
    public CertificateImportException(string message, Exception innerException) : base(message, innerException) { }
}
