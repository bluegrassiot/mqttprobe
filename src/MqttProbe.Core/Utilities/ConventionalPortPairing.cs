using MqttProbe.Core.Models.Mqtt;

namespace MqttProbe.Core.Utilities;

public static class ConventionalPortPairing
{
    public static int ApplyForTlsChange(Protocol protocol, int currentPort, bool useTls)
    {
        return (protocol, useTls) switch
        {
            (Protocol.Mqtt, true) when currentPort == 1883 => 8883,
            (Protocol.Mqtt, false) when currentPort == 8883 => 1883,
            (Protocol.WebSocket, true) when currentPort == 8083 => 8084,
            (Protocol.WebSocket, false) when currentPort == 8084 => 8083,
            _ => currentPort,
        };
    }

    public static int ApplyForProtocolChange(Protocol newProtocol, int currentPort, bool useTls)
    {
        return (newProtocol, useTls, currentPort) switch
        {
            (Protocol.WebSocket, false, 1883) => 8083,
            (Protocol.Mqtt, false, 8083) => 1883,
            (Protocol.WebSocket, true, 8883) => 8084,
            (Protocol.Mqtt, true, 8084) => 8883,
            _ => currentPort,
        };
    }
}
