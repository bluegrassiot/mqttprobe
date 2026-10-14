namespace MqttProbe.UI.Components.Browser;

// Kept in the UI rather than Core: grouping is a presentation concern and Core exposes no
// shared classifier for the spBv1.0/<group>/<verb>/<node>[/<device>] shape.
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
