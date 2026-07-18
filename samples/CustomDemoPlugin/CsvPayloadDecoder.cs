using System.Text;
using System.Text.Json;
using MQTTnet.Client;
using MqttProbe.Services.Plugins.Contracts;

namespace CustomDemoPlugin;

public sealed class CsvPayloadDecoder : IPayloadDecoder
{
    public string FormatId => "csv";

    public DecodedPayloadEnvelope Decode(MqttApplicationMessageReceivedEventArgs e)
    {
        var topic = e.ApplicationMessage.Topic;
        var segment = e.ApplicationMessage.PayloadSegment;
        var raw = segment.Array is null ? [] : segment.ToArray();

        if (raw.Length == 0)
        {
            return DecodedPayloadEnvelope.CreateSuccess(FormatId, topic, raw, string.Empty);
        }

        var text = Encoding.UTF8.GetString(raw);
        var lines = text.Split('\n');

        // Find header line
        string[]? headers = null;
        var headerIndex = -1;
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimEnd('\r').Trim();
            if (trimmed.Length == 0)
                continue;

            headers = trimmed.Split(',');
            headerIndex = i;
            break;
        }

        if (headers is null || headers.Length < 2)
        {
            return DecodedPayloadEnvelope.CreateFailure(
                FormatId, topic, raw, "No valid header row found (expected comma-separated columns).");
        }

        // Parse data rows
        var rows = new List<Dictionary<string, string>>();
        for (var i = headerIndex + 1; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimEnd('\r').Trim();
            if (trimmed.Length == 0)
                continue;

            var columns = trimmed.Split(',');
            var row = new Dictionary<string, string>();
            for (var c = 0; c < headers.Length; c++)
            {
                var key = headers[c].Trim();
                var value = c < columns.Length ? columns[c].Trim() : string.Empty;
                row[key] = value;
            }
            rows.Add(row);
        }

        if (rows.Count == 0)
        {
            return DecodedPayloadEnvelope.CreateFailure(
                FormatId, topic, raw, "Header found but no data rows present.");
        }

        // Build JSON array of objects as DisplayText so PayloadBrowser JSON tree works
        var displayText = JsonSerializer.Serialize(rows, _serializerOptions);

        return DecodedPayloadEnvelope.CreateSuccess(FormatId, topic, raw, displayText);
    }

    private static readonly JsonSerializerOptions _serializerOptions = new()
    {
        WriteIndented = false
    };
}
