using System.Security.Authentication;
using System.Text;
using MQTTnet;
using MQTTnet.Formatter;
using MQTTnet.Protocol;

namespace MqttProbe.Benchmarks.V5Traffic;

public static class V5TrafficRunner
{
    private static readonly TimeSpan _connectTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan _publishTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan _disconnectTimeout = TimeSpan.FromSeconds(5);
    private static readonly UTF8Encoding _strictUtf8 = new(false, true);

    public static async Task<int> RunAsync(V5TrafficOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        var output = new V5TrafficOutput(options);
        var validationError = Validate(options);
        if (validationError is not null)
        {
            output.Error($"ERROR: {validationError}");
            return 1;
        }

        if (ct.IsCancellationRequested)
        {
            output.Line("Cancelled.");
            return 130;
        }

        var scenarios = options.Scenarios.Select(V5TrafficScenarios.Get).ToArray();
        var messages = scenarios.Select(s => s.Build(options.Prefix)).ToArray();

        PrintHeader(output, options, scenarios.Length);
        PrintPlan(output, scenarios, messages);

        using var certificates = V5TrafficCertificates.Load(options);
        using var client = new MqttClientFactory().CreateMqttClient();
        var clientOptions = BuildClientOptions(options, certificates);

        output.Line("");
        output.Line("Connecting with MQTT v5...");
        MqttClientConnectResult connectResult;
        try
        {
            using var connectCts = CreateTimeout(ct, _connectTimeout);
            connectResult = await client.ConnectAsync(clientOptions, connectCts.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            output.Line("Cancelled.");
            return 130;
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            output.Line("Cancelled.");
            return 130;
        }
        catch (OperationCanceledException)
        {
            output.Error($"ERROR: connect timed out after {_connectTimeout.TotalSeconds:F0}s.");
            return 1;
        }
        catch (Exception ex)
        {
            output.Error($"ERROR: connect failed: {output.Exception(ex)}");
            return 1;
        }

        if (connectResult.ResultCode != MqttClientConnectResultCode.Success)
        {
            output.Error($"ERROR: broker rejected MQTT v5 connect: {connectResult.ResultCode}.");
            return 1;
        }

        if (connectResult.MaximumQoS < MqttQualityOfServiceLevel.AtLeastOnce)
        {
            output.Error(
                $"ERROR: broker maximum QoS is {connectResult.MaximumQoS}; this verb requires QoS 1.");
            return 1;
        }

        output.Line($"Connected. MQTT v5, broker maximum QoS: {connectResult.MaximumQoS}.");

        var exitCode = 0;
        var published = 0;
        var total = scenarios.Length * options.Repeat;

        try
        {
            var stop = false;
            for (var round = 1; round <= options.Repeat && !stop; round++)
            {
                for (var i = 0; i < scenarios.Length && !stop; i++)
                {
                    ct.ThrowIfCancellationRequested();

                    MqttClientPublishResult result;
                    try
                    {
                        using var publishCts = CreateTimeout(ct, _publishTimeout);
                        result = await client.PublishAsync(messages[i], publishCts.Token);
                    }
                    catch (Exception) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        output.Error(
                            $"ERROR: PUBACK timed out after {_publishTimeout.TotalSeconds:F0}s for {scenarios[i].CliName}.");
                        exitCode = 1;
                        stop = true;
                        break;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        output.Error(
                            $"ERROR: publish failed for {scenarios[i].CliName}: {output.Exception(ex)}");
                        exitCode = 1;
                        stop = true;
                        break;
                    }

                    if (!result.IsSuccess)
                    {
                        output.Error(
                            $"ERROR: broker rejected {scenarios[i].CliName} with {result.ReasonCode}.");
                        exitCode = 1;
                        stop = true;
                        break;
                    }

                    if (result.ReasonCode == MqttClientPublishReasonCode.NoMatchingSubscribers)
                    {
                        output.Line(
                            $"  WARN: {scenarios[i].CliName} received PUBACK NoMatchingSubscribers. No subscriber is listening.");
                    }

                    published++;
                    output.Line(
                        $"  [{published}/{total}] {scenarios[i].CliName} -> {messages[i].Topic} | PUBACK {result.ReasonCode}");

                    if (options.Interval > TimeSpan.Zero && published < total)
                        await Task.Delay(options.Interval, ct);
                }
            }

            if (exitCode == 0)
            {
                output.Line("");
                var messageLabel = published == 1 ? "message" : "messages";
                var scenariosLabel = scenarios.Length == 1 ? "scenario" : "scenarios";
                output.Line($"Published {published} {messageLabel} across {scenarios.Length} {scenariosLabel}.");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            output.Line("Cancelled.");
            exitCode = 130;
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            output.Line("Cancelled.");
            exitCode = 130;
        }
        catch (OperationCanceledException)
        {
            output.Error("ERROR: interval wait was cancelled unexpectedly.");
            exitCode = 1;
        }
        finally
        {
            if (!await DisconnectAsync(client, output, options))
                exitCode = exitCode == 0 ? 1 : exitCode;
        }

        return exitCode;
    }

    private static string? Validate(V5TrafficOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Host)
            || options.Host.Any(char.IsWhiteSpace)
            || options.Host.Any(char.IsControl)
            || options.Host.Contains("://", StringComparison.Ordinal)
            || options.Host.IndexOfAny(['@', '/', '\\', '?', '#']) >= 0)
        {
            return "host must be a hostname or IP address, not a URI or userinfo value.";
        }

        if (options.Port is < 1 or > 65535)
            return "port must be between 1 and 65535.";

        if (string.IsNullOrWhiteSpace(options.Prefix)
            || options.Prefix.IndexOfAny(['#', '+']) >= 0
            || options.Prefix.Any(char.IsControl))
        {
            return "prefix must be a literal topic prefix without wildcards or control characters.";
        }

        if (options.Scenarios is not { Count: > 0 })
            return "at least one scenario is required.";

        if (options.Repeat is < 1 or > 1000)
            return "repeat must be from 1 through 1000.";

        if (options.Interval < TimeSpan.Zero || options.Interval > TimeSpan.FromHours(1))
            return "interval must be from 0 through 3600000 milliseconds.";

        if (options.Username is not null && options.Username.Length == 0)
            return "username must not be empty.";

        if (string.IsNullOrEmpty(options.Username) && !string.IsNullOrEmpty(options.Password))
            return "a password was supplied without a username.";

        if (!options.UseTls
            && (options.CaCertificatePath is not null
                || options.ClientCertificatePath is not null
                || options.AllowUntrustedCertificate))
        {
            return "CA, client certificate and untrusted certificate options require TLS.";
        }

        return null;
    }

    private static MqttClientOptions BuildClientOptions(
        V5TrafficOptions options, V5TrafficCertificates? certificates)
    {
        var builder = new MqttClientOptionsBuilder()
            .WithTcpServer(options.Host, options.Port)
            .WithClientId($"mqttprobe-v5-traffic-{Guid.NewGuid():N}")
            .WithProtocolVersion(MqttProtocolVersion.V500)
            .WithCleanStart(true)
            .WithTimeout(_connectTimeout)
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(15));

        if (!string.IsNullOrEmpty(options.Username))
            builder = builder.WithCredentials(options.Username, options.Password ?? string.Empty);

        if (options.UseTls)
        {
            builder = builder.WithTlsOptions(tls =>
            {
                tls.WithSslProtocols(SslProtocols.Tls12 | SslProtocols.Tls13); // DevSkim: ignore DS440020,DS112836,DS440001

                if (options.AllowUntrustedCertificate)
                {
                    tls.WithAllowUntrustedCertificates(true)
                        .WithCertificateValidationHandler(_ => true);
                }
                else if (certificates?.TrustChain is not null)
                {
                    tls.WithTrustChain(certificates.TrustChain);
                }

                if (certificates?.ClientCertificates is not null)
                    tls.WithClientCertificates(certificates.ClientCertificates);
            });
        }

        return builder.Build();
    }

    private static async Task<bool> DisconnectAsync(
        IMqttClient client, V5TrafficOutput output, V5TrafficOptions options)
    {
        if (!client.IsConnected)
            return true;

        using var disconnectCts = new CancellationTokenSource(_disconnectTimeout);
        try
        {
            await client.DisconnectAsync(
                new MqttClientDisconnectOptionsBuilder()
                    .WithReason(MqttClientDisconnectOptionsReason.NormalDisconnection)
                    .Build(),
                disconnectCts.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            output.Error($"ERROR: disconnect timed out after {_disconnectTimeout.TotalSeconds:F0}s.");
            return false;
        }
        catch (Exception ex)
        {
            output.Error($"ERROR: disconnect failed: {output.Exception(ex)}");
            return false;
        }
    }

    private static CancellationTokenSource CreateTimeout(CancellationToken ct, TimeSpan timeout)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        return cts;
    }

    private static void PrintHeader(
        V5TrafficOutput output, V5TrafficOptions options, int scenarioCount)
    {
        var tls = !options.UseTls
            ? "disabled"
            : options.AllowUntrustedCertificate
                ? "enabled, certificate validation disabled"
                : "enabled, certificate validation required";
        var auth = options.Username is null
            ? "anonymous"
            : options.Password is null
                ? "username configured"
                : "username and password configured";

        output.Line("MQTT v5 traffic generator");
        output.Line($"  Broker    : {options.Host}:{options.Port}");
        output.Line($"  TLS       : {tls}");
        output.Line($"  Auth      : {auth}");
        output.Line($"  Prefix    : {options.Prefix.TrimEnd('/')}");
        output.Line($"  Scenarios : {scenarioCount}");
        output.Line($"  Repeat    : {options.Repeat}");
        output.Line($"  Interval  : {options.Interval.TotalMilliseconds:F0} ms");
        output.Line($"  Payload   : {Encoding.UTF8.GetByteCount(V5TrafficScenarios.PayloadJson)} bytes UTF-8 JSON");
        output.Line($"  {V5TrafficScenarios.PayloadJson}");
        output.Line("  QoS       : 1");
        output.Line("  Retain    : false");
    }

    private static void PrintPlan(
        V5TrafficOutput output,
        IReadOnlyList<V5TrafficScenarioDefinition> scenarios,
        IReadOnlyList<MqttApplicationMessage> messages)
    {
        output.Line("");
        output.Line("Plan:");

        for (var i = 0; i < scenarios.Count; i++)
        {
            var scenario = scenarios[i];
            var message = messages[i];

            output.Line($"  {i + 1}. {scenario.CliName}");
            output.Line($"     Topic       : {message.Topic}");
            output.Line($"     Payload     : {message.Payload.Length} bytes UTF-8 JSON");
            output.Line(
                $"     QoS/retain  : {(int)message.QualityOfServiceLevel}/{(message.Retain ? "true" : "false")}");

            PrintContentType(output, scenario, message);
            PrintCorrelation(output, message);
            PrintOtherProperties(output, message);
            PrintUserProperties(output, scenario, message);
        }
    }

    private static void PrintContentType(
        V5TrafficOutput output, V5TrafficScenarioDefinition scenario, MqttApplicationMessage message)
    {
        if (scenario.Name == V5TrafficScenarioName.EmptyProperties)
        {
            output.Line(
                "     ContentType : not sent, empty ContentType is unsupported by the MQTTnet 5.2 encoder");
            return;
        }

        if (message.ContentType is null)
        {
            output.Line("     ContentType : absent");
            return;
        }

        var bytes = Encoding.UTF8.GetByteCount(message.ContentType);
        output.Line(
            $"     ContentType : {bytes} bytes \"{Truncate(Escape(message.ContentType), 120)}\"");
    }

    private static void PrintCorrelation(V5TrafficOutput output, MqttApplicationMessage message)
    {
        if (message.CorrelationData is null)
        {
            output.Line("     Correlation : absent");
            return;
        }

        var correlation = message.CorrelationData;
        output.Line($"     Correlation : {correlation.Length} bytes");

        if (correlation.Length == 0)
        {
            output.Line("     Empty       : property 0x09 with length 0");
            return;
        }

        if (correlation.Length <= 64)
            output.Line($"     Hex         : {Convert.ToHexStringLower(correlation)}");

        var text = TryUtf8(correlation);
        if (text is not null)
            output.Line($"     Text        : \"{Truncate(Escape(text), 120)}\"");
    }

    private static void PrintOtherProperties(V5TrafficOutput output, MqttApplicationMessage message)
    {
        if (message.ResponseTopic is not null)
            output.Line($"     Response    : {message.ResponseTopic}");

        if (message.PayloadFormatIndicator != MqttPayloadFormatIndicator.Unspecified)
            output.Line($"     Format      : {message.PayloadFormatIndicator}");

        if (message.MessageExpiryInterval > 0)
            output.Line($"     Expiry      : {message.MessageExpiryInterval} s");
    }

    private static void PrintUserProperties(
        V5TrafficOutput output, V5TrafficScenarioDefinition scenario, MqttApplicationMessage message)
    {
        var properties = message.UserProperties;
        if (properties is not { Count: > 0 })
        {
            output.Line("     User props  : 0");
            return;
        }

        output.Line($"     User props  : {properties.Count}");

        var listAll = properties.Count <= 8
            || scenario.Name == V5TrafficScenarioName.DuplicateUserProperties;
        if (!listAll)
        {
            output.Line($"     First       : {FormatProperty(properties[0])}");
            output.Line($"     Last        : {FormatProperty(properties[^1])}");
            return;
        }

        foreach (var property in properties)
            output.Line($"       {FormatProperty(property)}");
    }

    private static string FormatProperty(MQTTnet.Packets.MqttUserProperty property) =>
        $"{property.Name}={Truncate(Escape(_strictUtf8.GetString(property.ValueBuffer.Span)), 80)}";

    private static string? TryUtf8(byte[] bytes)
    {
        try
        {
            return _strictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal);

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "...";
}
