using MQTTnet.Protocol;

namespace MqttProbe.UI.Utilities;

public static class MqttQosDisplay
{
    public static string Format(MqttQualityOfServiceLevel qos) => qos switch
    {
        MqttQualityOfServiceLevel.AtMostOnce => "0 · At most once",
        MqttQualityOfServiceLevel.AtLeastOnce => "1 · At least once",
        MqttQualityOfServiceLevel.ExactlyOnce => "2 · Exactly once",
        _ => $"{(int)qos} · {qos}"
    };
}
