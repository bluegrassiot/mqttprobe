using MqttProbe.Core.Services.Plugins.Pipeline;

namespace MqttProbe.Core.Services.Plugins.Registry;

public sealed class FormatDisplayNames(PayloadPipeline pipeline) : IFormatDisplayNames
{
    public string? GetDisplayName(string? formatId)
    {
        if (string.IsNullOrWhiteSpace(formatId))
            return null;

        var map = pipeline.Registry.FormatDisplayNamesById;
        if (map.TryGetValue(formatId, out var name) && !string.IsNullOrWhiteSpace(name))
            return name.Trim();

        return formatId;
    }
}
