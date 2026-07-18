# MqttProbe Payload Publisher

Standalone MQTT payload publisher built on **MQTTnet 5.2**. No dependency on MqttProbe.Shared
(except a compile-linked Sparkplug B protobuf source file). Publishes MQTT messages in configurable
payload formats to a real broker. Samples are shaped to match MqttProbe's detector priority order
so each format is correctly identified by the receiving pipeline.

## Quick start

```powershell
# Start a broker
docker run --rm -d -p 1883:1883 eclipse-mosquitto:2 `
  sh -c "printf 'allow_anonymous true\nlistener 1883\n' > /tmp/m.conf && mosquitto -c /tmp/m.conf"

# Launch interactive mode — prompts for each setting with defaults:
dotnet run --project benchmarks\MqttProbe.Benchmarks -c Release -- publish

# Or supply flags directly for scripting:
dotnet run --project benchmarks\MqttProbe.Benchmarks -c Release -- publish --count 500 --rate 200
```

### Interactive mode

Running `publish` without any flags (or with only some flags) launches an interactive prompt.
Each setting is prompted one-by-one with its default shown in brackets; press Enter to accept.

When stdin is redirected (e.g. piped input), missing values silently use defaults instead of prompting.

**Prompt order:**
1. Host (default: `MQTT_BENCHMARK_HOST` env or `localhost`)
2. Port (default: `MQTT_BENCHMARK_PORT` env or `1883`)
3. Formats — numbered list; enter `0` or `all` for all, or comma-separated names/numbers
4. Messages per format (default: `100`)
5. Rate in msg/s, 0 = unlimited (default: `0`)
6. Concurrency (default: `1`)
7. Summary + "Press Enter to start"

## Supported payload formats

Samples are crafted to win MqttProbe's first-match detector (priority order shown).

| Format | Topic | Sample shape |
|---|---|---|
| `Empty` | `benchmarks/payloads/empty` | Zero-length payload |
| `Sparkplug` | `spBv1.0/bench/DDATA/publisher` | Sparkplug B protobuf with 4 metrics |
| `MessagePack` | `benchmarks/payloads/msgpack` | MessagePack map with 4 fields |
| `Binary` | `benchmarks/payloads/binary` | Invalid UTF-8 bytes (not structured msgpack) |
| `Json` | `benchmarks/payloads/json` | Compact JSON object `{...}` |
| `Xml` | `benchmarks/payloads/xml` | XML starting with `<` |
| `Hex` | `benchmarks/payloads/hex` | Even-length lowercase hex, no spaces |
| `Base64` | `benchmarks/payloads/base64` | Valid base64 with proper padding |
| `PlainText` | `benchmarks/payloads/plaintext` | Valid UTF-8, not JSON/XML/hex/base64 |
| `Csv` | `benchmarks/payloads/csv` | Multi-line CSV (requires `samples/CustomDemoPlugin` loaded) |

The `Csv` format requires the sample payload format plugin. See [samples/CustomDemoPlugin/README.md](../../samples/CustomDemoPlugin/README.md) for build and install instructions.

## Usage

```powershell
# Interactive mode — prompts for each setting:
dotnet run --project benchmarks\MqttProbe.Benchmarks -c Release -- publish

# Partial flags — prompts only for missing settings:
dotnet run --project benchmarks\MqttProbe.Benchmarks -c Release -- publish --host 192.168.1.100

# Full flags — no prompts, runs immediately:
dotnet run --project benchmarks\MqttProbe.Benchmarks -c Release -- publish --host 192.168.1.100 --port 1883 --count 500 --rate 200 --concurrency 4 --format Sparkplug,Json

# Publish only Sparkplug payloads:
dotnet run --project benchmarks\MqttProbe.Benchmarks -c Release -- publish --format Sparkplug

# Publish multiple specific formats (comma-separated):
dotnet run --project benchmarks\MqttProbe.Benchmarks -c Release -- publish --format Json,Base64,Hex

# High-throughput with concurrency:
dotnet run --project benchmarks\MqttProbe.Benchmarks -c Release -- publish --concurrency 8 --count 10000 --format Sparkplug
```

## Options

| Flag | Default | Description |
|---|---|---|
| `--host` | `localhost` | MQTT broker hostname (or set `MQTT_BENCHMARK_HOST`) |
| `--port` | `1883` | MQTT broker port (or set `MQTT_BENCHMARK_PORT`) |
| `--count` | `100` | Number of messages to publish per format |
| `--rate` | `0` (unlimited) | Messages per second (0 = no throttle) |
| `--concurrency` | `1` | Max in-flight publishes (sequential when 1) |
| `--format` | all formats | Comma-separated list of payload formats to publish |

**Available format names:**

`Empty`, `Sparkplug`, `MessagePack`, `Binary`, `Json`, `Xml`, `Hex`, `Base64`, `PlainText`, `Csv`

## Concurrency

When `--concurrency 1` (default), publishes are sequential with accurate rate limiting.

When `--concurrency N` (N > 1), up to N publishes are in-flight concurrently via `SemaphoreSlim`.
Rate limiting is approximate at higher concurrency — the delay is applied before acquiring
the semaphore slot, so actual throughput may exceed the rate slightly.

## Subscribing in MqttProbe

Some brokers disallow the multi-level `#` wildcard. Prefer single-level `+` or exact topics.

**Recommended (covers every sample this tool publishes):**

| Subscription | Covers |
|---|---|
| `benchmarks/payloads/+` | Empty, MessagePack, Binary, Json, Xml, Hex, Base64, PlainText, Csv |
| `spBv1.0/bench/DDATA/publisher` | Sparkplug |

Add both filters in MqttProbe's Subscriptions tab before you run the publisher.

If `+` is also blocked, subscribe to the exact topics from the format table above (one filter per format you care about).

Avoid `#` unless you know the broker allows it. A root `#` subscription is also noisier than you need for this tool.

## Observing results in MqttProbe

1. Connect MqttProbe to the same broker.
2. Add the subscriptions above (before you publish).
3. Open the **Browser** tab and expand `benchmarks/payloads/...` and the Sparkplug topic.
4. Select each topic and confirm the detected format matches the sample (Json, Xml, Hex, and so on).
5. Click the **metrics chip** in the app shell bar (shows live msg/s). The flyout includes:
   - Messages processed / dropped
   - Processing time (avg / max)
   - Payload size (avg / max)
   - Counts **by detected format**
   - Optional app health (CPU, heap, threads, GC) and other diagnostics

Use the format breakdown in that flyout to confirm each sample type is being classified while you publish.
