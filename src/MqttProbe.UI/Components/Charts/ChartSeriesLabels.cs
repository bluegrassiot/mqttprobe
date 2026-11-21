using MqttProbe.Core.Models.Chart;

namespace MqttProbe.UI.Components.Charts;

public static class ChartSeriesLabels
{
    public static string LastPathSegment(string jsonPath)
    {
        if (jsonPath == ChartFieldPaths.Root) return "value";
        var dot = jsonPath.LastIndexOf('.');
        return dot >= 0 ? jsonPath[(dot + 1)..] : jsonPath;
    }

    public static string FromSelection(string? suggestedName, string jsonPath) =>
        string.IsNullOrWhiteSpace(suggestedName) ? LastPathSegment(jsonPath) : suggestedName;
}
