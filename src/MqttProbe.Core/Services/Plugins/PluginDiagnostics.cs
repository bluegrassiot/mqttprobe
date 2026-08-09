namespace MqttProbe.Core.Services.Plugins;

public sealed class PluginDiagnosticEntry
{
    public required string Source { get; init; }
    public required DiagnosticSeverity Severity { get; init; }
    public required string Message { get; init; }
    public string? Details { get; init; }
    public string? SourcePath { get; init; }
}

public enum DiagnosticSeverity
{
    Info,
    Warning,
    Error
}

public sealed class PluginOverrideConfig
{
    public string FormatId { get; init; } = "";
    public string Capability { get; init; } = "";
    public string PluginId { get; init; } = "";
}
