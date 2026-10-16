namespace MqttProbe.Core.Models.Mqtt;

internal static class SparkplugTopicClassifier
{
    public const string Namespace = "spBv1.0";

    private static readonly HashSet<string> _nodeVerbs =
        new(StringComparer.Ordinal) { "NBIRTH", "NDEATH", "NDATA", "NCMD" };

    private static readonly HashSet<string> _deviceVerbs =
        new(StringComparer.Ordinal) { "DBIRTH", "DDEATH", "DDATA", "DCMD" };

    public static bool IsNodeVerb(string verb) => _nodeVerbs.Contains(verb);

    public static bool IsDeviceVerb(string verb) => _deviceVerbs.Contains(verb);
}
