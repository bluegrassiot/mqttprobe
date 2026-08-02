namespace MqttProbe.Models.Emulation;

public enum EmulatorNodeType { SparkplugB, Generic }

// CA1720: member names match type names (Double, Int64, Boolean) by design — this
// enum is JSON-serialised into the on-disk emulator config, so renaming members
// would break existing config files. Do not rename.
#pragma warning disable CA1720
public enum MetricValueType { Double, Int64, Boolean }
#pragma warning restore CA1720

public enum WaveformKind
{
    Sine, Ramp, RandomWalk, Constant,
    FixedBoolean, Toggle, RandomBoolean
}

public class EmulatorMetricConfig
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Metric-1";
    public MetricValueType ValueType { get; set; } = MetricValueType.Double;
    public WaveformKind Waveform { get; set; } = WaveformKind.Sine;
    public double Min { get; set; }
    public double Max { get; set; } = 100;
    public double PeriodSeconds { get; set; } = 60;
    public double StepAmplitude { get; set; } = 1;
    public double ConstantValue { get; set; }
    public bool BooleanValue { get; set; }
    public double TrueProbability { get; set; } = 0.5;
}

public class EmulatorDeviceConfig
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string DeviceId { get; set; } = "Device-1";
    public List<EmulatorMetricConfig> Metrics { get; set; } = [];
}

public class EmulatorNodeConfig
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public EmulatorNodeType Type { get; set; } = EmulatorNodeType.SparkplugB;
    public string GroupId { get; set; } = "Plant1";
    public string NodeId { get; set; } = "Node-1";
    public string PayloadFormatId { get; set; } = "json";
    public string TopicTemplate { get; set; } = "{group}/{node}/{device}/{metric}";
    public List<EmulatorDeviceConfig> Devices { get; set; } = [];
    public bool UseMetricAliases { get; set; }
}
