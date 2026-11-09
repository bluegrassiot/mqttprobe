using MqttProbe.Core.Models.Mqtt;

namespace MqttProbe.UI.Components.Browser;

internal static class TopicBranchTargetDisplay
{
    public static string Label(TopicBranchTarget target) =>
        target.Kind == TopicBranchKind.Raw ? RawLabel(target.Path) : SparkplugLabel(target);

    private static string RawLabel(string? path) => path switch
    {
        null => "(topic)",
        "" => "(empty first level)",
        _ => $"\"{path}\""
    };

    private static string SparkplugLabel(TopicBranchTarget target)
    {
        if (target.Scope is not { } scope)
            return "(scope)";

        var path = scope.Kind switch
        {
            SparkplugSelectionKind.Device => $"{scope.GroupPath}/{scope.NodeId}/{scope.DeviceId}",
            _ => $"{scope.GroupPath}/{scope.NodeId}"
        };
        return target.MessageType is null ? path : $"{path}/{target.MessageType}";
    }
}
