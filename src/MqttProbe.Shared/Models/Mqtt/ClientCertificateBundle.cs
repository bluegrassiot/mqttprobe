using System.Security.Cryptography.X509Certificates;

namespace MqttProbe.Models.Mqtt;

public record ClientCertificateBundle(X509Certificate2 Certificate);
