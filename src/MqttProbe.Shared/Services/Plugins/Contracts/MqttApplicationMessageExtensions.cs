using System.Buffers;
using System.Runtime.InteropServices;
using MQTTnet;

namespace MqttProbe.Services.Plugins.Contracts;

// Bridges the MQTTnet 5 payload model to the contiguous ArraySegment the payload plugins and
// stores were written against. MQTTnet 4 exposed MqttApplicationMessage.GetPayloadSegment()
// as an ArraySegment; MQTTnet 5 exposes Payload as a ReadOnlySequence.
public static class MqttApplicationMessageExtensions
{
    // Zero-copy when the payload is a single array-backed segment; otherwise copies once.
    public static ArraySegment<byte> GetPayloadSegment(this MqttApplicationMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var payload = message.Payload;
        if (payload.IsEmpty)
            return ArraySegment<byte>.Empty;

        if (payload.IsSingleSegment && MemoryMarshal.TryGetArray(payload.First, out var segment))
            return segment;

        return new ArraySegment<byte>(payload.ToArray());
    }
}
