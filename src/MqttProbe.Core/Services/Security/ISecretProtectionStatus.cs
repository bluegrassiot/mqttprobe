namespace MqttProbe.Core.Services.Security;

public interface ISecretProtectionStatus
{
    public SecretProtectionMode Mode { get; }
}
