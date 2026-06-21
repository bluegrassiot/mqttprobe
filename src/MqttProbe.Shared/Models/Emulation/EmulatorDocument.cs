namespace MqttProbe.Models.Emulation;

public class EmulatorDocument
{
    public int Version { get; set; } = 1;
    public int PublishIntervalMs { get; set; } = 500;
    public List<EmulatorNodeConfig> Nodes { get; set; } = [];
}
