using System.Text;
using System.Text.Unicode;
using MQTTnet;
using MqttProbe.Services.Plugins.Contracts;

namespace CustomDemoPlugin;

public sealed class CsvPayloadDetector : IPayloadDetector
{
    public string FormatId => "csv";
    public int Priority => 550;

    public bool CanDetect(MqttApplicationMessageReceivedEventArgs e)
    {
        var segment = e.ApplicationMessage.GetPayloadSegment();
        if (segment.Count == 0)
            return false;

        var bytes = segment.Array.AsSpan(segment.Offset, segment.Count);
        if (!Utf8.IsValid(bytes))
            return false;

        var text = Encoding.UTF8.GetString(bytes).Trim();
        if (text.Length == 0)
            return false;

        // Reject JSON and XML
        if (text[0] is '{' or '[' or '<')
            return false;

        // Must contain at least one newline (multi-line CSV)
        if (!text.Contains('\n'))
            return false;

        var lines = text.Split('\n');
        var dataLines = 0;

        foreach (var line in lines)
        {
            var trimmed = line.TrimEnd('\r').Trim();
            if (trimmed.Length == 0)
                continue;

            if (!trimmed.Contains(','))
                return false;

            dataLines++;
        }

        // Need at least a header line and one data row, each with ≥2 comma-separated columns
        if (dataLines < 2)
            return false;

        // Verify first non-empty line has ≥2 columns
        var firstLine = Array.Find(lines, l => l.TrimEnd('\r').Trim().Length > 0);
        if (firstLine is null)
            return false;

        var firstColumns = firstLine.TrimEnd('\r').Split(',');
        return firstColumns.Length >= 2;
    }
}
