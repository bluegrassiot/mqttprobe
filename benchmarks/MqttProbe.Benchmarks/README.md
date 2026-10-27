# MqttProbe Benchmarks

Two standalone tools built on MQTTnet 5.2:

- `publish` sends timed payloads in many formats.
- `v5-traffic` sends fixed MQTT v5 property scenarios for UI and broker checks.

Neither tool depends on MqttProbe.UI. The publisher compile-links a generated Sparkplug B protobuf source file from MqttProbe.Core.

## Quick start

```powershell
# Start a broker
docker run --rm -d -p 1883:1883 eclipse-mosquitto:2 `
  sh -c "printf 'allow_anonymous true\nlistener 1883\n' > /tmp/m.conf && mosquitto -c /tmp/m.conf"

# Timed publish, interactive mode
dotnet run --project benchmarks\MqttProbe.Benchmarks -c Release -- publish

# MQTT v5 traffic, every scenario
dotnet run --project benchmarks\MqttProbe.Benchmarks -c Release -- v5-traffic

# List the v5 scenarios without connecting
dotnet run --project benchmarks\MqttProbe.Benchmarks -c Release -- v5-traffic --list
```

## MQTT v5 traffic generator

`v5-traffic` publishes one fixed JSON payload through ten property scenarios. It forces MQTT v5, uses QoS 1, disables retain, and does not report throughput timing. The `publish` verb remains the timed path.

```json
{"temperature":22.5,"pressure":1013.25,"flowRate":12.8,"status":"active"}
```

The payload is 73 UTF-8 bytes in every scenario. The point of the verb is the MQTT v5 property set, not the payload contents.

If a broker only accepts MQTT 3.1.1, the connection is rejected with `UnsupportedProtocolVersion` and the run exits nonzero. There is no downgrade path.

### Commands for each scenario

Run these from the repository root:

```powershell
dotnet run --project benchmarks\MqttProbe.Benchmarks -c Release -- v5-traffic --scenario all-six
dotnet run --project benchmarks\MqttProbe.Benchmarks -c Release -- v5-traffic --scenario duplicate-user-properties
dotnet run --project benchmarks\MqttProbe.Benchmarks -c Release -- v5-traffic --scenario binary-correlation
dotnet run --project benchmarks\MqttProbe.Benchmarks -c Release -- v5-traffic --scenario whitespace-correlation
dotnet run --project benchmarks\MqttProbe.Benchmarks -c Release -- v5-traffic --scenario empty-properties
dotnet run --project benchmarks\MqttProbe.Benchmarks -c Release -- v5-traffic --scenario absent-properties
dotnet run --project benchmarks\MqttProbe.Benchmarks -c Release -- v5-traffic --scenario long-content-type
dotnet run --project benchmarks\MqttProbe.Benchmarks -c Release -- v5-traffic --scenario user-properties-12
dotnet run --project benchmarks\MqttProbe.Benchmarks -c Release -- v5-traffic --scenario user-properties-40
dotnet run --project benchmarks\MqttProbe.Benchmarks -c Release -- v5-traffic --scenario long-properties
```

Run all scenarios in publish order:

```powershell
dotnet run --project benchmarks\MqttProbe.Benchmarks -c Release -- v5-traffic --scenario all
```

Run one scenario three times with half a second between messages:

```powershell
dotnet run --project benchmarks\MqttProbe.Benchmarks -c Release -- v5-traffic --scenario duplicate-user-properties --repeat 3 --interval 500
```

Use a custom broker and topic prefix:

```powershell
dotnet run --project benchmarks\MqttProbe.Benchmarks -c Release -- v5-traffic --host 192.168.1.100 --port 1883 --prefix lab/v5
```

### Options

| Flag | Default | Description |
|---|---|---|
| `--host` | `localhost` or `MQTT_BENCHMARK_HOST` | Broker hostname or IP address |
| `--port` | `1883` or `MQTT_BENCHMARK_PORT` | Broker port, 1 through 65535 |
| `--prefix` | `benchmarks/v5` | Literal topic prefix. Wildcards are rejected |
| `--scenario` | `all` | One name, several comma-separated names, or `all` |
| `--repeat` | `1` | Messages per scenario, 1 through 1000 |
| `--interval` | `1000` | Milliseconds between publishes, 0 through 3600000 |
| `--list` | | Print scenario names and summaries, then exit |
| `--tls` | off | Enable TLS 1.2 or 1.3 with certificate validation |
| `--ca` | | Custom CA file in PEM, DER, or PFX form. Requires `--tls` |
| `--client-cert` | | Client certificate PFX. Requires `--tls` |
| `--allow-untrusted` | off | Disable TLS certificate validation. Requires `--tls` |
| `--username` | `MQTT_BENCHMARK_USER` | Broker username |
| `--help` | | Print help, then exit |

Passwords are never accepted as command line arguments. The verb reads them from the environment:

| Variable | Purpose |
|---|---|
| `MQTT_BENCHMARK_PASSWORD` | Broker password |
| `MQTT_BENCHMARK_CA_PASSWORD` | Custom CA PFX password |
| `MQTT_BENCHMARK_CLIENT_CERT_PASSWORD` | Client certificate PFX password |

The verb never prints these values. It prints only whether authentication is configured, and it redacts configured usernames, passwords, and certificate passwords from all output.

### TLS and authentication

TLS is off by default so the existing `MQTT_BENCHMARK_HOST` and `MQTT_BENCHMARK_PORT` defaults keep working. `--tls` enables TLS 1.2 and 1.3 with normal certificate validation. A custom CA can be a single PEM or DER certificate, a PEM bundle, or a PFX. A client certificate must be a PFX. The port default stays 1883, so pass `--port 8883` for the usual TLS listener.

```powershell
dotnet run --project benchmarks\MqttProbe.Benchmarks -c Release -- v5-traffic --tls --port 8883 --ca .\certs\ca.pem

$env:MQTT_BENCHMARK_CLIENT_CERT_PASSWORD='<client-pfx-password>'
dotnet run --project benchmarks\MqttProbe.Benchmarks -c Release -- v5-traffic --tls --port 8883 --ca .\certs\ca.pem --client-cert .\certs\client.pfx
```

Use `--allow-untrusted` only for a lab broker with a self-signed certificate that you do not want to trust explicitly.

For password authentication, set the username and password before the run:

```powershell
$env:MQTT_BENCHMARK_USER='probe'
$env:MQTT_BENCHMARK_PASSWORD='<password>'
dotnet run --project benchmarks\MqttProbe.Benchmarks -c Release -- v5-traffic --host broker.example --port 8883 --tls --ca .\certs\ca.pem
```

### Topic and expected content

The default topic for a scenario is `benchmarks/v5/<scenario>`. The table below uses that prefix.

| Scenario | Topic suffix | ContentType | Correlation | User properties |
|---|---|---|---|---|
| `all-six` | `all-six` | 16 bytes, `application/json` | 19 bytes, `all-six-correlation` | 2 |
| `duplicate-user-properties` | `duplicate-user-properties` | absent | absent | 6 |
| `binary-correlation` | `binary-correlation` | absent | 8 bytes | 0 |
| `whitespace-correlation` | `whitespace-correlation` | absent | 29 bytes | 0 |
| `empty-properties` | `empty-properties` | unsupported, not sent | 0 bytes | 0 |
| `absent-properties` | `absent-properties` | absent | absent | 0 |
| `long-content-type` | `long-content-type` | 287 bytes | absent | 0 |
| `user-properties-12` | `user-properties-12` | absent | absent | 12 |
| `user-properties-40` | `user-properties-40` | absent | absent | 40 |
| `long-properties` | `long-properties` | absent | 256 bytes | 3 |

`all-six` fills all six property categories: ContentType, CorrelationData, ResponseTopic, PayloadFormatIndicator, MessageExpiryInterval, and UserProperties.

- ContentType: `application/json` (16 bytes)
- CorrelationData: `all-six-correlation` (19 bytes, hex `616c6c2d7369782d636f7272656c6174696f6e`)
- ResponseTopic: `benchmarks/v5/all-six/reply`
- PayloadFormatIndicator: `CharacterData`
- MessageExpiryInterval: `60` seconds
- UserProperties: `source=mqttprobe`, `suite=v5-traffic`

`duplicate-user-properties` sends these names in this exact order:

1. `trace=first`
2. `trace=second`
3. `trace=third`
4. `region=eu-west`
5. `region=us-east`
6. `sample=alpha`

`binary-correlation` sends 8 raw bytes that are not valid UTF-8:

```text
00 ff 10 80 c3 28 01 fe
```

`whitespace-correlation` sends 29 bytes. The text form is shown with escapes:

```text
"  alpha  beta\t\tgamma\r\ndelta  "
```

The hex form is:

```text
20 20 61 6c 70 68 61 20 20 62 65 74 61 09 09 67 61 6d 6d 61 0d 0a 64 65 6c 74 61 20 20
```

It has two leading spaces, two trailing spaces, two repeated spaces between `alpha` and `beta`, two tabs between `beta` and `gamma`, and CR LF between `gamma` and `delta`.

`empty-properties` sends a zero-length CorrelationData. The MQTT v5 property is present as property `0x09` with length 0. The same scenario asks for an empty ContentType, but the MQTTnet 5.2 encoder omits empty ContentType values. This verb treats that case as unsupported and prints that the property is not sent. It does not claim the broker received an empty ContentType.

`absent-properties` sends no MQTT v5 properties at all.

`long-content-type` sends this 287-byte value:

```text
application/vnd.mqttprobe.xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx+json
```

`user-properties-12` sends 12 properties named `metric-01` through `metric-12`, with values `value-01` through `value-12`.

`user-properties-40` sends 40 properties named `metric-01` through `metric-40`, with values `value-01` through `value-40`.

`long-properties` sends a 256-byte correlation value made of `long-correlation:` followed by 239 `c` characters. It also sends three user properties:

| Name | Name bytes | Value | Value bytes |
|---|---|---|---|
| `acceptance-long-property-name-01-` plus 48 `n` | 81 | `value-01-` plus 120 `v` | 129 |
| `acceptance-long-property-name-02-` plus 48 `m` | 81 | `value-02-` plus 120 `w` | 129 |
| `acceptance-long-property-name-03-` plus 48 `o` | 81 | `value-03-` plus 120 `u` | 129 |

### What the run prints

Before connecting, the verb prints the broker, TLS mode, authentication mode, topic prefix, repeat count, interval, payload, and the full plan. For each scenario it prints the topic, payload bytes, QoS and retain settings, ContentType bytes, correlation length, small correlation hex, escaped text, response topic, format, expiry, and user property count or ordered names.

During the run it prints one line per publish with the PUBACK reason. `NoMatchingSubscribers` is reported as a warning but counts as success because the broker acknowledged the QoS 1 publish. `--repeat` multiplies the number of messages by the number of selected scenarios.

Exit codes are:

- `0`: every publish received a successful PUBACK and the client disconnected.
- `1`: invalid arguments, certificate load failure, connect failure, publish rejection, PUBACK timeout, or disconnect failure.
- `130`: cancelled with Ctrl+C.

Connect, publish acknowledgement, and disconnect waits are bounded. On failure the run stops instead of publishing more messages.

### UI acceptance checklist

This checklist is a verification aid, not an approval or release readiness statement.

1. Connect MqttProbe with MQTT v5 and subscribe to the exact topics or `benchmarks/v5/+`.
2. Run `v5-traffic` with all scenarios. Confirm every topic arrives once per repeat.
3. Confirm the JSON payload is visible for every scenario, including the scenarios whose visible details are mostly properties.
4. At 1280 by 800, publish `user-properties-12` and `user-properties-40`. Confirm all properties fit through scrolling or wrapping, the total count is visible, and the payload stays visible.
5. Narrow the window and publish `long-content-type`. The value has no spaces, so confirm the UI wraps the long unbroken token without horizontal scrolling, the full value remains reachable, and the payload stays visible.
6. Publish `whitespace-correlation`. Confirm leading spaces, trailing spaces, repeated spaces, tabs, and the CR LF break remain distinguishable and are not collapsed into ordinary single spaces.
7. Publish `empty-properties` and `absent-properties` side by side. Confirm absent correlation shows no value, while empty correlation is shown as present with zero length. Confirm the empty ContentType does not appear as a present property because the encoder did not send it.
8. Publish `binary-correlation`. Confirm the bytes are shown as binary or hex, not as invalid text with replacement characters.
9. Publish `duplicate-user-properties`. Confirm the repeated names appear in publish order: `trace` three times, then `region` twice, then `sample` once.
10. Publish `all-six`. Confirm all six property categories are visible: ContentType, CorrelationData, ResponseTopic, PayloadFormatIndicator, MessageExpiryInterval, and UserProperties.
11. Subscribe with a second client after a run. Confirm no retained message appears for any v5 traffic topic.

## Payload publisher

The `publish` verb sends payload formats at a configurable rate. It uses a compile-linked generated Sparkplug B protobuf source file from MqttProbe.Core and does not otherwise depend on MqttProbe.UI. Samples are shaped to match MqttProbe's detector priority order so each format is correctly identified by the receiving pipeline.

### Interactive mode

Running `publish` without any flags, or with only some flags, launches an interactive prompt. Each setting is prompted one by one with its default shown in brackets. Press Enter to accept.

When stdin is redirected, missing values use defaults instead of prompting.

Prompt order:

1. Host (default: `MQTT_BENCHMARK_HOST` or `localhost`)
2. Port (default: `MQTT_BENCHMARK_PORT` or `1883`)
3. Formats: numbered list, `0` or `all` for all, or comma-separated names and numbers
4. Messages per format (default: `100`)
5. Rate in messages per second, 0 means unlimited (default: `0`)
6. Concurrency (default: `1`)
7. Summary and "Press Enter to start"

### Supported payload formats

Samples are crafted to win MqttProbe's first-match detector. Priority order is shown in the table.

| Format | Topic | Sample shape |
|---|---|---|
| `Empty` | `benchmarks/payloads/empty` | Zero-length payload |
| `Sparkplug` | `spBv1.0/bench/DDATA/publisher` | Sparkplug B protobuf with 4 metrics |
| `MessagePack` | `benchmarks/payloads/msgpack` | MessagePack map with 4 fields |
| `Binary` | `benchmarks/payloads/binary` | Invalid UTF-8 bytes that are not structured MessagePack |
| `Json` | `benchmarks/payloads/json` | Compact JSON object |
| `Xml` | `benchmarks/payloads/xml` | XML starting with `<` |
| `Hex` | `benchmarks/payloads/hex` | Even-length lowercase hex without spaces |
| `Base64` | `benchmarks/payloads/base64` | Valid base64 with padding |
| `PlainText` | `benchmarks/payloads/plaintext` | Valid UTF-8 that is not JSON, XML, hex, or base64 |
| `Csv` | `benchmarks/payloads/csv` | Multi-line CSV, requires the sample plugin |
| `ChirpStack` | `application/1/device/0102030405060708/event/up` | Real ChirpStack `integration.UplinkEvent`, requires the schema package |

The `Csv` format requires the sample payload format plugin. See [samples/CustomDemoPlugin/README.md](../../samples/CustomDemoPlugin/README.md) for build and install instructions.

`ChirpStack` publishes the same payload as `samples/protobuf/chirpstack/sample-uplink.b64` on a topic that matches the schema manifest's `application/+/device/+/event/up` pattern. Without that schema package installed the bytes are still delivered, but no protobuf schema matches the topic, so they are detected as binary rather than decoded. Install it from **Settings > Plugins** first. See [samples/protobuf/chirpstack/README.md](../../samples/protobuf/chirpstack/README.md).

### Publish usage

```powershell
# Interactive mode, prompts for each setting
dotnet run --project benchmarks\MqttProbe.Benchmarks -c Release -- publish

# Partial flags, prompts only for missing settings
dotnet run --project benchmarks\MqttProbe.Benchmarks -c Release -- publish --host 192.168.1.100

# Full flags, no prompts
dotnet run --project benchmarks\MqttProbe.Benchmarks -c Release -- publish --host 192.168.1.100 --port 1883 --count 500 --rate 200 --concurrency 4 --format Sparkplug,Json

# Publish only Sparkplug payloads
dotnet run --project benchmarks\MqttProbe.Benchmarks -c Release -- publish --format Sparkplug

# Publish several formats
dotnet run --project benchmarks\MqttProbe.Benchmarks -c Release -- publish --format Json,Base64,Hex

# High throughput with concurrency
dotnet run --project benchmarks\MqttProbe.Benchmarks -c Release -- publish --concurrency 8 --count 10000 --format Sparkplug
```

### Publish options

| Flag | Default | Description |
|---|---|---|
| `--host` | `localhost` | MQTT broker hostname, or set `MQTT_BENCHMARK_HOST` |
| `--port` | `1883` | MQTT broker port, or set `MQTT_BENCHMARK_PORT` |
| `--count` | `100` | Number of messages to publish per format |
| `--rate` | `0` (unlimited) | Messages per second, 0 means no throttle |
| `--concurrency` | `1` | Maximum in-flight publishes, sequential when 1 |
| `--format` | all formats | Comma-separated payload formats |

Available format names:

`Empty`, `Sparkplug`, `MessagePack`, `Binary`, `Json`, `Xml`, `Hex`, `Base64`, `PlainText`, `Csv`, `ChirpStack`

When `--concurrency 1`, publishes are sequential with accurate rate limiting.

When `--concurrency N` and N is greater than 1, up to N publishes are in flight through `SemaphoreSlim`. Rate limiting is approximate at higher concurrency because the delay is applied before acquiring the semaphore slot.

### Subscribing in MqttProbe

Some brokers disallow the multi-level `#` wildcard. Prefer single-level `+` or exact topics.

Recommended filters for the payload publisher:

| Subscription | Covers |
|---|---|
| `benchmarks/payloads/+` | Empty, MessagePack, Binary, Json, Xml, Hex, Base64, PlainText, Csv |
| `spBv1.0/bench/DDATA/publisher` | Sparkplug |
| `application/+/device/+/event/up` | ChirpStack |

Add the filters in MqttProbe's Subscriptions tab before you run the publisher.

If `+` is also blocked, subscribe to the exact topics in the format table, one filter per format you need.

Avoid `#` unless you know the broker allows it. A root `#` subscription is also noisier than this tool needs.

### Observing publish results in MqttProbe

1. Connect MqttProbe to the same broker.
2. Add the subscriptions above before publishing.
3. Open the **Browser** tab and expand `benchmarks/payloads/...` and the Sparkplug topic.
4. Select each topic and confirm the detected format matches the sample.
5. Click the metrics chip in the app shell bar to see live messages per second. The flyout includes messages processed and dropped, processing time, payload size, counts by detected format, and optional app health details.
