namespace MqttProbe.Services.Security;

public interface ISecretProtectionStatus
{
    public SecretProtectionMode Mode { get; }
}
