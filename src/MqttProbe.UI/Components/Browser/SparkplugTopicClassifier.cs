namespace MqttProbe.UI.Components.Browser;

// Classification for the topic-tree grouping view. Derived from the shape the Core
// topology extractor understands (spBv1.0/<group>/<verb>/<node>[/<device>]) but kept in
// the UI: grouping is a presentation concern and Core exposes no shared classifier.
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
