namespace MqttProbe.Core.Services.Plugins.Packaging;

public sealed class PluginArchiveLimits
{
    public long MaxCompressedBytes { get; init; } = 33_554_432;

    public long MaxUncompressedBytes { get; init; } = 134_217_728;

    public int MaxEntries { get; init; } = 4_000;

    public double MaxCompressionRatio { get; init; } = 100;
}
