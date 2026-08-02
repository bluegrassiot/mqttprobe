namespace MqttProbe.Services.Plugins.Contracts;

public interface IPayloadEncoder
{
    public string FormatId { get; }
    public byte[] Encode(PayloadEncoderRequest request);
}

public sealed class PayloadEncoderRequest
{
    public required string Topic { get; init; }
    public required string FormatId { get; init; }
    public required IReadOnlyDictionary<string, object> Metrics { get; init; }
    public DateTime? TimestampUtc { get; init; }
}
