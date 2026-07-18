using MqttProbe.Benchmarks;
using MqttProbe.Benchmarks.Models;

if (args.Length > 0 && args[0] is "-h" or "--help" or "help")
{
    PrintUsage();
    return;
}

if (args.Length > 0 && !args[0].Equals("publish", StringComparison.OrdinalIgnoreCase))
{
    Console.WriteLine($"ERROR: unknown command '{args[0]}'. Use 'publish' or '--help'.");
    Environment.ExitCode = 1;
    return;
}

// No args or "publish" — proceed to publish path
var flagStart = args.Length > 0 && args[0].Equals("publish", StringComparison.OrdinalIgnoreCase) ? 1 : 0;

var host = Environment.GetEnvironmentVariable("MQTT_BENCHMARK_HOST") ?? "localhost";
var portEnv = Environment.GetEnvironmentVariable("MQTT_BENCHMARK_PORT");
var port = int.TryParse(portEnv, out var p) && p > 0 ? p : 1883;
var count = 100;
var rate = 0;
var concurrency = 1;
HashSet<string>? formats = null;

var hostSupplied = false;
var portSupplied = false;
var countSupplied = false;
var rateSupplied = false;
var concurrencySupplied = false;
var formatsSupplied = false;

for (var i = flagStart; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--host" when i + 1 < args.Length:
            host = args[++i];
            hostSupplied = true;
            break;
        case "--port" when i + 1 < args.Length:
            if (!int.TryParse(args[++i], out port) || port <= 0)
            { Console.WriteLine($"ERROR: invalid port '{args[i]}'."); Environment.ExitCode = 1; return; }
            portSupplied = true;
            break;
        case "--count" when i + 1 < args.Length:
            if (!int.TryParse(args[++i], out count) || count <= 0)
            { Console.WriteLine($"ERROR: invalid count '{args[i]}'."); Environment.ExitCode = 1; return; }
            countSupplied = true;
            break;
        case "--rate" when i + 1 < args.Length:
            if (!int.TryParse(args[++i], out rate) || rate < 0)
            { Console.WriteLine($"ERROR: invalid rate '{args[i]}'."); Environment.ExitCode = 1; return; }
            rateSupplied = true;
            break;
        case "--concurrency" when i + 1 < args.Length:
            if (!int.TryParse(args[++i], out concurrency) || concurrency < 1)
            { Console.WriteLine($"ERROR: invalid concurrency '{args[i]}'."); Environment.ExitCode = 1; return; }
            concurrencySupplied = true;
            break;
        case "--format" when i + 1 < args.Length:
            formats = args[++i]
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            formatsSupplied = true;
            break;
        default:
            Console.WriteLine($"ERROR: unknown flag '{args[i]}'.");
            Environment.ExitCode = 1; return;
    }
}

var allSupplied = hostSupplied && portSupplied && countSupplied && rateSupplied && concurrencySupplied && formatsSupplied;

if (!allSupplied)
{
    if (!InteractivePrompt.IsInteractive)
    {
        // stdin redirected — use defaults silently for missing values
    }
    else
    {
        Console.WriteLine("MqttProbe Payload Publisher");
        Console.WriteLine("Press Enter to accept defaults.\n");

        if (!hostSupplied)
            host = InteractivePrompt.PromptHost(host);
        if (!portSupplied)
            port = InteractivePrompt.PromptPort(port);
        if (!formatsSupplied)
            formats = InteractivePrompt.PromptFormats(formats);
        if (!countSupplied)
            count = InteractivePrompt.PromptCount(count);
        if (!rateSupplied)
            rate = InteractivePrompt.PromptRate(rate);
        if (!concurrencySupplied)
            concurrency = InteractivePrompt.PromptConcurrency(concurrency);

        InteractivePrompt.PromptReady(host, port, count, rate, concurrency, formats);
    }
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

try
{
    var exitCode = await PayloadPublisher.RunAsync(host, port, count, rate, concurrency, formats, cts.Token);
    Environment.ExitCode = exitCode;
}
catch (OperationCanceledException)
{
    Console.WriteLine("Publisher cancelled.");
    Environment.ExitCode = 130;
}
catch (Exception ex)
{
    Console.WriteLine($"ERROR: {ex.Message}");
    Environment.ExitCode = 1;
}

return;

static void PrintUsage()
{
    var formatNames = string.Join(", ", Enum.GetNames<PayloadFormat>());

    Console.WriteLine("MqttProbe Payload Publisher");
    Console.WriteLine();
    Console.WriteLine("Usage:");
    Console.WriteLine("  dotnet run -c Release -- publish [options]");
    Console.WriteLine();
    Console.WriteLine("Running without any flags launches interactive mode.");
    Console.WriteLine();
    Console.WriteLine("Options:");
    Console.WriteLine("  --host <hostname>       MQTT broker hostname (default: localhost, or MQTT_BENCHMARK_HOST env var)");
    Console.WriteLine("  --port <port>           MQTT broker port (default: 1883, or MQTT_BENCHMARK_PORT env var)");
    Console.WriteLine("  --count <n>             Messages per format (default: 100)");
    Console.WriteLine("  --rate <n>              Messages per second, 0 = unlimited (default: 0)");
    Console.WriteLine("  --concurrency <n>       Max in-flight publishes (default: 1)");
    Console.WriteLine("  --format <names>        Comma-separated format names (default: all)");
    Console.WriteLine();
    Console.WriteLine($"Available formats: {formatNames}");
    Console.WriteLine();
    Console.WriteLine("Examples:");
    Console.WriteLine("  dotnet run -c Release --publish");
    Console.WriteLine("  dotnet run -c Release --publish --count 500 --rate 200");
    Console.WriteLine("  dotnet run -c Release --publish --concurrency 8 --count 10000");
    Console.WriteLine("  dotnet run -c Release --publish --host 192.168.1.100 --port 1883");
    Console.WriteLine("  dotnet run -c Release --publish --format Sparkplug,Json");
}
