namespace MqttProbe.Benchmarks.V5Traffic;

internal static class V5TrafficOptionsParser
{
    public static V5TrafficParseResult Parse(string[] args)
    {
        if (args.Any(a => a is "-h" or "--help"))
        {
            PrintHelp();
            return V5TrafficParseResult.Exit(0);
        }

        if (args.Any(a => a == "--list"))
        {
            PrintList();
            return V5TrafficParseResult.Exit(0);
        }

        var host = Environment.GetEnvironmentVariable("MQTT_BENCHMARK_HOST") ?? "localhost";
        var port = GetDefaultPort();
        var prefix = "benchmarks/v5";
        var useTls = false;
        var allowUntrusted = false;
        string? caPath = null;
        string? clientCertPath = null;
        string? username = Environment.GetEnvironmentVariable("MQTT_BENCHMARK_USER");
        var password = Environment.GetEnvironmentVariable("MQTT_BENCHMARK_PASSWORD");
        var scenarioTokens = new List<string>();
        var repeat = 1;
        var intervalMs = 1000;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--host":
                    if (!TryTakeValue(args, ref i, "--host", out host, out var hostError))
                        return Error(hostError);
                    break;
                case "--port":
                    if (!TryTakeValue(args, ref i, "--port", out var portText, out var portError))
                        return Error(portError);
                    if (!int.TryParse(portText, out port) || port is < 1 or > 65535)
                        return Error("invalid port. Use 1 through 65535.");
                    break;
                case "--prefix":
                    if (!TryTakeValue(args, ref i, "--prefix", out prefix, out var prefixError))
                        return Error(prefixError);
                    break;
                case "--scenario":
                    if (!TryTakeValue(args, ref i, "--scenario", out var scenarioText, out var scenarioError))
                        return Error(scenarioError);
                    var scenarios = scenarioText.Split(',',
                        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (scenarios.Length == 0)
                        return Error("--scenario requires at least one name.");
                    scenarioTokens.AddRange(scenarios);
                    break;
                case "--repeat":
                    if (!TryTakeValue(args, ref i, "--repeat", out var repeatText, out var repeatError))
                        return Error(repeatError);
                    if (!int.TryParse(repeatText, out repeat) || repeat is < 1 or > 1000)
                        return Error("invalid repeat. Use 1 through 1000.");
                    break;
                case "--interval":
                    if (!TryTakeValue(args, ref i, "--interval", out var intervalText, out var intervalError))
                        return Error(intervalError);
                    if (!int.TryParse(intervalText, out intervalMs) || intervalMs is < 0 or > 3_600_000)
                        return Error("invalid interval. Use milliseconds from 0 through 3600000.");
                    break;
                case "--tls":
                    useTls = true;
                    break;
                case "--allow-untrusted":
                    allowUntrusted = true;
                    break;
                case "--ca":
                    if (!TryTakeValue(args, ref i, "--ca", out caPath, out var caError))
                        return Error(caError);
                    break;
                case "--client-cert":
                    if (!TryTakeValue(args, ref i, "--client-cert", out clientCertPath, out var certError))
                        return Error(certError);
                    break;
                case "--username":
                    if (!TryTakeValue(args, ref i, "--username", out username, out var userError))
                        return Error(userError);
                    break;
                default:
                    return Error("unknown argument. Use --help for the option list.");
            }
        }

        host = host.Trim();
        if (host.Length == 0
            || host.Any(char.IsWhiteSpace)
            || host.Any(char.IsControl)
            || host.Contains("://", StringComparison.Ordinal)
            || host.IndexOfAny(['@', '/', '\\', '?', '#']) >= 0)
        {
            return Error("host must be a hostname or IP address, not a URI or userinfo value.");
        }

        prefix = prefix.Trim().TrimEnd('/');
        if (prefix.Length == 0)
            return Error("prefix must not be empty.");
        if (prefix.IndexOfAny(['#', '+']) >= 0 || prefix.Any(char.IsControl))
            return Error("prefix must be a literal topic prefix without wildcards or control characters.");

        if (username is not null && username.Length == 0)
            return Error("username must not be empty.");
        if (string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(password))
            return Error("MQTT_BENCHMARK_PASSWORD is set but no username was supplied. Use --username or MQTT_BENCHMARK_USER.");

        if (!useTls && (caPath is not null || clientCertPath is not null || allowUntrusted))
            return Error("--ca, --client-cert and --allow-untrusted require --tls.");
        if (caPath is not null && !File.Exists(caPath))
            return Error("CA certificate file not found.");
        if (clientCertPath is not null && !File.Exists(clientCertPath))
            return Error("client certificate file not found.");

        var selected = ResolveScenarios(scenarioTokens);
        if (selected is null)
            return Error("invalid scenario. Use --list for valid names.");

        var options = new V5TrafficOptions
        {
            Host = host,
            Port = port,
            Prefix = prefix,
            UseTls = useTls,
            AllowUntrustedCertificate = allowUntrusted,
            CaCertificatePath = caPath,
            CaCertificatePassword = Environment.GetEnvironmentVariable("MQTT_BENCHMARK_CA_PASSWORD"),
            ClientCertificatePath = clientCertPath,
            ClientCertificatePassword = Environment.GetEnvironmentVariable("MQTT_BENCHMARK_CLIENT_CERT_PASSWORD"),
            Username = username,
            Password = password,
            Scenarios = selected,
            Repeat = repeat,
            Interval = TimeSpan.FromMilliseconds(intervalMs),
        };

        return V5TrafficParseResult.Run(options);
    }

    private static IReadOnlyList<V5TrafficScenarioName>? ResolveScenarios(List<string> tokens)
    {
        if (tokens.Count == 0 || tokens.Any(t => t.Equals("all", StringComparison.OrdinalIgnoreCase)))
            return V5TrafficScenarios.AllNames;

        var selected = new HashSet<V5TrafficScenarioName>();
        foreach (var token in tokens)
        {
            if (!V5TrafficScenarios.TryGet(token, out var scenario))
                return null;
            selected.Add(scenario.Name);
        }

        return V5TrafficScenarios.AllNames.Where(selected.Contains).ToArray();
    }

    private static bool TryTakeValue(
        string[] args, ref int index, string flag, out string value, out string error)
    {
        if (index + 1 >= args.Length || args[index + 1].StartsWith('-'))
        {
            value = string.Empty;
            error = $"{flag} requires a value.";
            return false;
        }

        value = args[++index];
        error = string.Empty;
        return true;
    }

    private static int GetDefaultPort()
    {
        var text = Environment.GetEnvironmentVariable("MQTT_BENCHMARK_PORT");
        return int.TryParse(text, out var port) && port is >= 1 and <= 65535 ? port : 1883;
    }

    private static V5TrafficParseResult Error(string message)
    {
        Console.Error.WriteLine($"ERROR: {message}");
        return V5TrafficParseResult.Exit(1);
    }

    private static void PrintList()
    {
        Console.WriteLine("MQTT v5 traffic scenarios, in publish order:");
        foreach (var scenario in V5TrafficScenarios.All)
            Console.WriteLine($"  {scenario.CliName,-26} {scenario.Summary}");
        Console.WriteLine();
        Console.WriteLine("Use --scenario <name> to select one or more names, or --scenario all for every scenario.");
    }

    private static void PrintHelp()
    {
        Console.WriteLine("MQTT v5 traffic generator");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  dotnet run --project benchmarks\\MqttProbe.Benchmarks -c Release -- v5-traffic [options]");
        Console.WriteLine();
        Console.WriteLine("Runs only with MQTT v5. There is no fallback to MQTT 3.1.1.");
        Console.WriteLine("Publishes fixed JSON payloads with QoS 1 and retain disabled. This verb does not time throughput.");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --host <host>          Broker hostname (default: MQTT_BENCHMARK_HOST or localhost)");
        Console.WriteLine("  --port <port>          Broker port (default: MQTT_BENCHMARK_PORT or 1883)");
        Console.WriteLine("  --prefix <prefix>      Topic prefix (default: benchmarks/v5)");
        Console.WriteLine("  --scenario <names>     Comma-separated names, or all (default: all)");
        Console.WriteLine("  --list                 List scenarios and exit");
        Console.WriteLine("  --repeat <n>           Publish each scenario n times, 1 through 1000 (default: 1)");
        Console.WriteLine("  --interval <ms>        Delay between publishes in milliseconds (default: 1000)");
        Console.WriteLine("  --tls                  Enable TLS 1.2 or 1.3 with certificate validation");
        Console.WriteLine("  --ca <path>            Custom CA certificate, PEM/DER/PFX, requires --tls");
        Console.WriteLine("  --client-cert <path>   Client certificate PFX, requires --tls");
        Console.WriteLine("  --allow-untrusted      Disable TLS certificate validation, requires --tls");
        Console.WriteLine("  --username <name>      Broker username (default: MQTT_BENCHMARK_USER)");
        Console.WriteLine("  --help                 Show this help and exit");
        Console.WriteLine();
        Console.WriteLine("Credentials and certificate passwords are read from environment variables:");
        Console.WriteLine("  MQTT_BENCHMARK_USER, MQTT_BENCHMARK_PASSWORD, MQTT_BENCHMARK_CA_PASSWORD, MQTT_BENCHMARK_CLIENT_CERT_PASSWORD");
        Console.WriteLine("Passwords are never printed.");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  dotnet run --project benchmarks\\MqttProbe.Benchmarks -c Release -- v5-traffic --list");
        Console.WriteLine("  dotnet run --project benchmarks\\MqttProbe.Benchmarks -c Release -- v5-traffic --host localhost --port 1883");
        Console.WriteLine("  dotnet run --project benchmarks\\MqttProbe.Benchmarks -c Release -- v5-traffic --scenario duplicate-user-properties --repeat 3 --interval 500");
        Console.WriteLine("  dotnet run --project benchmarks\\MqttProbe.Benchmarks -c Release -- v5-traffic --tls --port 8883 --ca ca.pem --client-cert client.pfx");
    }
}
