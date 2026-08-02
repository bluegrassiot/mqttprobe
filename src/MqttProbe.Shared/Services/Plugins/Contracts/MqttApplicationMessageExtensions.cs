using System.Buffers;
using System.Runtime.InteropServices;
using MQTTnet;

namespace MqttProbe.Services.Plugins.Contracts;

/// <summary>
/// Bridges the MQTTnet 5 payload model to the contiguous <see cref="ArraySegment{T}"/>
/// the payload plugins and stores were written against. MQTTnet 4 exposed
/// <c>MqttApplicationMessage.GetPayloadSegment()</c> (an <see cref="ArraySegment{T}"/>);
/// MQTTnet 5 exposes <c>Payload</c> as a <see cref="System.Buffers.ReadOnlySequence{T}"/>.
/// </summary>
public static class MqttApplicationMessageExtensions
{
    /// <summary>
    /// Returns the message payload as a contiguous <see cref="ArraySegment{T}"/>.
    /// Zero-copy when the payload is a single array-backed segment; otherwise copies once.
    /// </summary>
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
