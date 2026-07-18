namespace MqttProbe.Services.Plugins;

public sealed class PluginDiagnosticEntry
{
    public required string Source { get; init; }
    public required DiagnosticSeverity Severity { get; init; }
    public required string Message { get; init; }
    public string? Details { get; init; }
}

public enum DiagnosticSeverity
{
    Info,
    Warning,
    Error
}

public sealed class PluginOverrideConfig
{
    public required string FormatId { get; init; }
    public required string Capability { get; init; }
    public required string PluginId { get; init; }
}
