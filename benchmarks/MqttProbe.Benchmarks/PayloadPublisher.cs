using System.Diagnostics;
using MQTTnet;
using MQTTnet.Protocol;
using MqttProbe.Benchmarks.Models;

namespace MqttProbe.Benchmarks;

public static class PayloadPublisher
{
    public static async Task<int> RunAsync(
        string host, int port, int count, int rate, int concurrency,
        IReadOnlySet<string>? formats, CancellationToken ct)
    {
        var allFormats = Enum.GetValues<PayloadFormat>();
        var selectedFormats = ResolveFormats(allFormats, formats);
        if (selectedFormats is null)
            return 1;

        Console.WriteLine($"Payload Publisher");
        Console.WriteLine($"  Broker     : {host}:{port}");
        Console.WriteLine($"  Count      : {count} messages per format");
        Console.WriteLine($"  Rate       : {rate} msg/s (0 = unlimited)");
        Console.WriteLine($"  Concurrency: {concurrency}");
        Console.WriteLine($"  Formats    : {string.Join(", ", selectedFormats)}");
        Console.WriteLine();

        using var mqttClient = new MqttClientFactory().CreateMqttClient();

        var options = new MqttClientOptionsBuilder()
            .WithTcpServer(host, port)
            .WithClientId($"mqttprobe-publisher-{Guid.NewGuid():N}")
            .WithTimeout(TimeSpan.FromSeconds(10))
            .Build();

        Console.WriteLine("Connecting to broker...");
        var connectResult = await mqttClient.ConnectAsync(options, ct);
        if (connectResult.ResultCode != MqttClientConnectResultCode.Success)
        {
            Console.WriteLine($"ERROR: failed to connect to broker ({connectResult.ResultCode}).");
            return 1;
        }
        Console.WriteLine("Connected.");
        Console.WriteLine();

        try
        {
            var totalSent = 0;
            var totalElapsed = Stopwatch.StartNew();

            foreach (var format in selectedFormats)
            {
                ct.ThrowIfCancellationRequested();
                await PublishFormatAsync(mqttClient, format, count, rate, concurrency, ct);
                totalSent += count;
            }

            totalElapsed.Stop();

            Console.WriteLine();
            Console.WriteLine("─".PadRight(50, '─'));
            Console.WriteLine($"Total : {totalSent} messages in {totalElapsed.Elapsed.TotalSeconds:F1}s ({totalSent / totalElapsed.Elapsed.TotalSeconds:F0} msg/s)");

            return 0;
        }
        finally
        {
            if (mqttClient.IsConnected)
                await mqttClient.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build(), CancellationToken.None);
        }
    }

    private static PayloadFormat[]? ResolveFormats(PayloadFormat[] allFormats, IReadOnlySet<string>? formats)
    {
        if (formats is not { Count: > 0 })
            return allFormats;

        var validNames = allFormats.Select(f => f.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var invalid = formats.Where(f => !validNames.Contains(f)).ToArray();

        if (invalid.Length > 0)
        {
            Console.WriteLine($"ERROR: invalid format(s): {string.Join(", ", invalid)}");
            Console.WriteLine($"Valid formats: {string.Join(", ", validNames.OrderBy(n => n))}");
            return null;
        }

        var selected = allFormats.Where(f => formats.Contains(f.ToString())).ToArray();
        if (selected.Length == 0)
        {
            Console.WriteLine($"ERROR: no formats matched. Valid formats: {string.Join(", ", validNames.OrderBy(n => n))}");
            return null;
        }

        return selected;
    }

    private static async Task PublishFormatAsync(
        IMqttClient mqttClient,
        PayloadFormat format,
        int count,
        int rate,
        int concurrency,
        CancellationToken ct)
    {
        var payload = PayloadFactory.CreateSample(format);
        var topic = GetTopic(format);

        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtMostOnce)
            .Build();

        var delay = rate > 0 ? TimeSpan.FromSeconds(1.0 / rate) : TimeSpan.Zero;

        if (concurrency <= 1)
        {
            var sw = Stopwatch.StartNew();
            for (var i = 0; i < count; i++)
            {
                ct.ThrowIfCancellationRequested();
                await mqttClient.PublishAsync(message, ct);

                if (delay > TimeSpan.Zero)
                {
                    var remaining = delay - sw.Elapsed;
                    if (remaining > TimeSpan.Zero)
                        await Task.Delay(remaining, ct);
                    sw.Restart();
                }
            }
        }
        else
        {
            using var semaphore = new SemaphoreSlim(concurrency);
            var tasks = new Task[count];
            var sw = Stopwatch.StartNew();

            for (var i = 0; i < count; i++)
            {
                ct.ThrowIfCancellationRequested();

                if (delay > TimeSpan.Zero)
                {
                    var remaining = delay - sw.Elapsed;
                    if (remaining > TimeSpan.Zero)
                        await Task.Delay(remaining, ct);
                    sw.Restart();
                }

                await semaphore.WaitAsync(ct);
                tasks[i] = Task.Run(async () =>
                {
                    try { await mqttClient.PublishAsync(message, ct); }
                    finally { semaphore.Release(); }
                }, ct);
            }

            await Task.WhenAll(tasks);
        }

        Console.WriteLine($"  {format,-12}  {count} messages  →  {topic}");
    }

    private static string GetTopic(PayloadFormat format) => format switch
    {
        PayloadFormat.Empty => "benchmarks/payloads/empty",
        PayloadFormat.Sparkplug => "spBv1.0/bench/DDATA/publisher",
        PayloadFormat.MessagePack => "benchmarks/payloads/msgpack",
        PayloadFormat.Binary => "benchmarks/payloads/binary",
        PayloadFormat.Json => "benchmarks/payloads/json",
        PayloadFormat.Xml => "benchmarks/payloads/xml",
        PayloadFormat.Hex => "benchmarks/payloads/hex",
        PayloadFormat.Base64 => "benchmarks/payloads/base64",
        PayloadFormat.PlainText => "benchmarks/payloads/plaintext",
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, null)
    };
}
