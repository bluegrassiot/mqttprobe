using System.ComponentModel.DataAnnotations;

namespace MqttProbe.Core.Models.Mqtt;

public class MqttMessageSend
{
    [Required] public string Name { get; set; } = string.Empty;

    [Required] public string Payload { get; set; } = string.Empty;
}
