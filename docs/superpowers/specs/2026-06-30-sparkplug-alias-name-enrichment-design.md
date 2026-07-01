# Sparkplug Alias Name Enrichment

## Summary

When a Sparkplug B message contains metrics identified only by numeric alias (no `name` field), the payload browser and chart subsystems degrade: the JSON tree shows raw indexed paths and the "Add to Chart" action produces unfriendly series names like `metrics[0].doubleValue`. This feature enriches the decoded view of alias-only metrics by resolving their human-readable names from the topology service's alias map, without mutating the original protobuf JSON text.

## Goals

1. Resolve metric names for alias-only metrics at decode time, producing a per-message alias-to-name map.
2. Display a visually muted annotation (e.g. `→ Flow Rate (resolved)`) next to the alias value in the JSON tree, distinct from real JSON properties.
3. Use the resolved clean name for chart field paths and series names (e.g. `metrics.Flow Rate` instead of `metrics[0].doubleValue`).
4. Centralize resolution in `PayloadDecoder` so both `JsonTreeNode` and `JsonFieldExtractor` consume the same per-message side map.
5. Add a global `EnrichSparkplugAliasNames` setting (default `true`) to toggle enrichment on/off.

## Non-goals

- Mutating the raw protobuf JSON text to inject `name` fields into the decoded payload string.
- Persisting alias maps across sessions or connections.
- Resolving aliases for non-Sparkplug message formats.
- Adding alias-based filtering or sorting in the topology view.

## User-facing behavior

When enrichment is enabled (default):

- **Payload browser**: A metric object with `"alias": 42` but no `"name"` property shows the original JSON unchanged, plus a muted annotation rendered outside the JSON structure: `→ Flow Rate (resolved)`. This annotation is italic, uses a muted color, and is not a new JSON property in the tree.
- **Chart field picker**: The field path for an alias-only metric appears as `metrics.Flow Rate` (clean name) instead of `metrics[0].doubleValue`. The "Add to Chart" button uses the same clean name as the series label.
- **Chart data series**: The series name in the chart configuration uses the clean resolved name (e.g. `Flow Rate`), not the annotated `(resolved)` label.

When enrichment is disabled:

- Behavior is identical to today: alias-only metrics show indexed paths, no annotations appear, and chart series fall back to index-based names.

## Architecture / implementation design

### Setting: `EnrichSparkplugAliasNames`

**File:** `src/MqttProbe.Shared/Models/Configuration/Configuration.cs`

Add to `UiPreferences`:

```csharp
public class UiPreferences
{
    public bool FontAccessible { get; set; } = true;
    public string Theme { get; set; } = "dark";
    public string FontFamily { get; set; } = "OpenDyslexic";
    public bool AutoResubscribe { get; set; } = true;
    public bool EnrichSparkplugAliasNames { get; set; } = true;
    public List<string> DismissedHints { get; set; } = [];
}
```

**File:** `src/MqttProbe.Shared/Services/SettingsStore.cs`

Add to `ISettingsStore`:

```csharp
Task SetEnrichSparkplugAliasNamesAsync(bool enrich);
```

Implementation follows the existing `SetAutoResubscribeAsync` pattern: set the property, call `SaveAsync()`, invoke `UiPreferencesChanged`.

**File:** `src/MqttProbe.Shared/Components/Pages/Settings.razor`

Add a `MudSwitch` in the Subscriptions section (or a new "Sparkplug" section):

```razor
<MudSwitch T="bool" Value="@SettingsStore.Config.Ui.EnrichSparkplugAliasNames"
           ValueChanged="@OnEnrichAliasNamesChanged"
           Color="Color.Primary"
           Label="Enrich Sparkplug alias names" />
<MudText Typo="Typo.caption" Color="Color.Secondary" Class="mt-1">
    When enabled, alias-only Sparkplug metrics are annotated with resolved names from the topology.
</MudText>
```

### PayloadDecoder extension

**File:** `src/MqttProbe.Shared/Services/Sparkplug/PayloadDecoder.cs`

The static `PayloadDecoder` becomes an injectable instance service to access `ISparkplugTopologyService`.

**Current signature:**
```csharp
public static class PayloadDecoder
{
    internal static DecodedPayload Decode(MqttApplicationMessageReceivedEventArgs e)
    public static string GetPayloadStr(MqttApplicationMessageReceivedEventArgs e)
}
```

**New design:**

```csharp
public interface IPayloadDecoder
{
    DecodedPayload Decode(MqttApplicationMessageReceivedEventArgs e);
    string GetPayloadStr(MqttApplicationMessageReceivedEventArgs e);
}

public class PayloadDecoder : IPayloadDecoder
{
    private readonly ISparkplugTopologyService _topology;
    private readonly ISettingsStore _settings;

    public PayloadDecoder(ISparkplugTopologyService topology, ISettingsStore settings)
    {
        _topology = topology;
        _settings = settings;
    }

    public DecodedPayload Decode(MqttApplicationMessageReceivedEventArgs e) { ... }
    public string GetPayloadStr(MqttApplicationMessageReceivedEventArgs e) => Decode(e).Payload;
}
```

The `Decode` method, when processing a Sparkplug-format message and enrichment is enabled:

1. Parses the raw protobuf `Payload` object (already done in `DecodeSparkplug`).
2. Calls `Payload.Parser.ParseFrom(segment.ToArray())` to get the typed payload.
3. Calls `SparkplugTopologyService.TryParseTopic` to extract group/node/device from the topic.
4. Looks up the appropriate `AliasMap` from the topology service:
   - For node messages (NBIRTH/NDATA/NCMD): `Groups[group].Nodes[node].AliasMap`
   - For device messages (DBIRTH/DDATA/DCMD): `Groups[group].Nodes[node].Devices[device].AliasMap`
5. Iterates `payload.Metrics`; for each metric with `Alias != 0` and `string.IsNullOrEmpty(metric.Name)`, adds `alias → aliasMap[alias]` to the result map (if the alias map has an entry).
6. Returns `DecodedPayload` with the new `AliasNames` field populated.

**Important:** The `Payload` string in `DecodedPayload` remains exactly `Payload.Parser.ParseFrom(...).ToString()` — the original protobuf JSON text, never mutated.

### DecodedPayload extension

**File:** `src/MqttProbe.Shared/Services/Sparkplug/PayloadDecoder.cs`

```csharp
internal sealed record DecodedPayload(
    string Payload,
    DetectedPayloadFormat Format,
    IReadOnlyDictionary<ulong, string>? AliasNames = null);
```

The `AliasNames` property is `null` when the message is not Sparkplug, enrichment is disabled, or no alias-only metrics were resolved. Callers that ignore this field (e.g. `MessageStoreManager` storing the raw payload string) are unaffected.

### JsonTreeNode rendering

**File:** `src/MqttProbe.Shared/Components/Browser/JsonTreeNode.razor`

Add a new parameter:

```razor
[Parameter] public IReadOnlyDictionary<ulong, string>? AliasNames { get; set; }
```

When rendering a metric object (inside the `JsonValueKind.Object` case), after the existing property loop, if the object has an `"alias"` property but no `"name"` property, and `AliasNames` is not null, and the alias value resolves in the map, render a muted annotation:

```razor
@if (AliasNames is not null
     && element.TryGetProperty("alias", out var aliasEl)
     && aliasEl.ValueKind == JsonValueKind.Number
     && aliasEl.TryGetUInt64(out var aliasVal)
     && AliasNames.TryGetValue(aliasVal, out var resolvedName)
     && !element.TryGetProperty("name", out _))
{
    <span class="json-alias-annotation"> → @resolvedName (resolved)</span>
}
```

The `json-alias-annotation` CSS class applies italic font and muted color (e.g. `color: var(--mud-palette-text-secondary); font-style: italic;`). This annotation is rendered as a sibling of the JSON object, not as a new property inside it.

**File:** `src/MqttProbe.Shared/Components/Browser/JsonTreeView.razor`

Accept and forward the `AliasNames` parameter:

```razor
[Parameter] public IReadOnlyDictionary<ulong, string>? AliasNames { get; set; }
```

Pass it through to `JsonTreeNode`:

```razor
<JsonTreeNode Element="_doc.RootElement" Depth="0" AutoExpandDepth="@_autoExpandDepth"
              JsonPath="" OnAddToChart="OnAddToChart" AliasNames="@AliasNames" />
```

The caller of `JsonTreeView` (likely `PayloadBrowser`) must obtain the `AliasNames` from the `DecodedPayload` and pass it down. This requires `PayloadBrowser` to have access to the decoded payload's alias map, which means the `MqttMessage` or a parallel data structure must carry it.

**File:** `src/MqttProbe.Shared/Models/Mqtt/MqttMessage.cs`

Extend `MqttMessage` to carry the optional alias map:

```csharp
public sealed class MqttMessage
{
    // existing fields...
    public IReadOnlyDictionary<ulong, string>? AliasNames { get; init; }
}
```

`MessageStoreManager.MessageHandler` passes `decodedPayload.AliasNames` when constructing the `MqttMessage`.

### JsonFieldExtractor chart path naming

**File:** `src/MqttProbe.Shared/Services/Chart/JsonFieldExtractor.cs`

The `IJsonFieldExtractor.Extract` method signature gains an optional alias map parameter:

```csharp
public interface IJsonFieldExtractor
{
    IReadOnlyDictionary<string, ExtractedField> Extract(string jsonPayload);
    IReadOnlyDictionary<string, ExtractedField> Extract(string jsonPayload, IReadOnlyDictionary<ulong, string>? aliasNames);
}
```

The existing `Extract(string)` overload delegates to `Extract(string, null)` for backward compatibility.

Inside `WalkArray`, when `TryExtractNamedValueArray` fails (because metrics lack `name`), and `aliasNames` is not null, attempt alias-based resolution:

1. For each array element that has `"alias"` but no `"name"`, look up `aliasNames[alias]`.
2. If found, use the resolved name as the field key (e.g. `metrics.Flow Rate`).
3. If not found, fall back to the existing indexed path behavior.

The chart series name (used in `ChartFieldRegistry` and chart labels) uses the same clean resolved string — never the `(resolved)` annotation text.

**ChartDataService** (`src/MqttProbe.Shared/Services/Chart/ChartDataService.cs`):

`MessageHandler` must pass the alias map through. Currently it calls `PayloadDecoder.GetPayloadStr(e)` which returns a string. With the new `IPayloadDecoder`, it calls `Decode(e)` to get `DecodedPayload`, then passes `decoded.AliasNames` to `extractor.Extract(payload, aliasNames)`.

```csharp
private Task MessageHandler(MqttApplicationMessageReceivedEventArgs e)
{
    var decoded = _decoder.Decode(e);
    var payload = decoded.Payload;
    if (!TryExtractFields(payload, decoded.AliasNames, out var fields))
        return Task.CompletedTask;
    // ...
}
```

## Data flow

```
MQTT message arrives
  │
  ├─ SparkplugTopologyService.OnMessageReceived
  │    └─ Updates AliasMap per node/device (existing, unchanged)
  │
  ├─ MessageStoreManager.MessageHandler
  │    ├─ IPayloadDecoder.Decode(e)
  │    │    ├─ Parses protobuf → JSON string (unchanged)
  │    │    ├─ Parses topic → group/node/device
  │    │    ├─ If enrichment enabled & Sparkplug format:
  │    │    │    ├─ Looks up AliasMap from topology
  │    │    │    └─ Builds alias→name map for alias-only metrics
  │    │    └─ Returns DecodedPayload { Payload, Format, AliasNames }
  │    │
  │    ├─ Stores MqttMessage { Payload, AliasNames, ... }
  │    └─ Fires MessageReceived
  │
  ├─ PayloadBrowser (renders message list)
  │    └─ Passes AliasNames to JsonTreeView → JsonTreeNode
  │         └─ Renders muted "→ Name (resolved)" annotation for alias-only metrics
  │
  └─ ChartDataService.MessageHandler
       ├─ IPayloadDecoder.Decode(e)
       ├─ extractor.Extract(payload, aliasNames)
       │    └─ Uses resolved name for field path (e.g. "metrics.Flow Rate")
       └─ Stores data points with clean series names
```

## Error handling / validation

| Scenario | Behavior |
|---|---|
| **Missing alias mapping** (NDATA/DDATA arrives before NBIRTH) | The alias is not in `AliasMap`. The decoder silently omits that entry from `AliasNames`. The metric renders with its raw alias/indexed path, as if enrichment were off for that metric. No error logged. |
| **Enrichment setting off** | `Decode` skips alias resolution entirely. `AliasNames` is `null`. All downstream behavior is identical to the pre-feature baseline. |
| **Race with birth timing** | `AliasMap` is read under the node/device `SyncRoot` lock. If a birth and data message arrive concurrently, the lock serializes access. The data message either sees the fully populated map (if birth completes first) or an empty/partial map (if data arrives first). In the partial case, alias-only metrics are simply not enriched — no crash, no stale data. |
| **Non-Sparkplug message** | `AliasNames` is `null`. No enrichment attempted. `JsonTreeNode` and `JsonFieldExtractor` behave identically to today. |
| **Protobuf parse failure** | Falls back to UTF-8 string decode (existing behavior). `AliasNames` is `null`. |
| **Topology service not yet initialized** | `Groups` is empty. Alias lookup returns no matches. `AliasNames` is empty or `null`. No error. |
| **Duplicate alias in payload** | Last-wins semantics for the alias map (same as `Dictionary` behavior). Consistent with how `SparkplugTopologyService` already handles duplicates. |

## Testing plan

### Unit tests

**File:** `tests/MqttProbe.Tests/Services/Sparkplug/PayloadDecoderTests.cs`

1. **Alias enrichment with valid mapping** — Decode a Sparkplug NBIRTH message with alias=42 mapped to "Flow Rate" in topology. Assert `decoded.AliasNames[42] == "Flow Rate"`.
2. **Alias enrichment skipped when setting off** — Set `EnrichSparkplugAliasNames = false`. Decode same message. Assert `decoded.AliasNames` is `null`.
3. **Alias-only metric resolved** — Decode NDATA with metric having alias=7 but no name. Topology has alias=7 → "Temperature". Assert `decoded.AliasNames[7] == "Temperature"`.
4. **Missing alias mapping omitted** — Decode NDATA with alias=99 not in topology. Assert `decoded.AliasNames` does not contain key 99 (or is empty).
5. **Non-Sparkplug message** — Decode a plain text message. Assert `decoded.AliasNames` is `null`.
6. **Named metric not in alias map** — Decode NBIRTH with metric having both name="Pressure" and alias=5. Assert `decoded.AliasNames` does not contain key 5 (only alias-only metrics are enriched).

**File:** `tests/MqttProbe.Tests/Services/Chart/JsonFieldExtractorTests.cs`

7. **Alias-based field path** — Call `Extract` with a Sparkplug JSON containing an alias-only metric and a provided alias map. Assert the field key is `metrics.Flow Rate` instead of `metrics[0].doubleValue`.
8. **Alias map null falls back to indexed** — Call `Extract` with `aliasNames: null`. Assert indexed path is used.
9. **Alias not in map falls back to indexed** — Call `Extract` with alias map missing the metric's alias. Assert indexed path is used.

**File:** `tests/MqttProbe.Tests/Components/Browser/JsonTreeViewTests.cs`

10. **Annotation rendered for alias-only metric** — Render `JsonTreeView` with a Sparkplug JSON containing `{"alias": 42, "doubleValue": 3.14}` and `AliasNames = {42: "Flow Rate"}`. Assert markup contains `→ Flow Rate (resolved)`.
11. **No annotation when name present** — Render with `{"name": "Pressure", "alias": 5, "doubleValue": 1013.0}`. Assert no `(resolved)` annotation.
12. **No annotation when AliasNames null** — Render with `AliasNames = null`. Assert no annotation.

**File:** `tests/MqttProbe.Tests/Components/Pages/SettingsTests.cs`

13. **Toggle renders and fires** — Render Settings page. Assert the "Enrich Sparkplug alias names" switch is present. Toggle it. Assert `SetEnrichSparkplugAliasNamesAsync` is called.

### Coverage target

All new code paths (alias resolution in `PayloadDecoder`, annotation rendering in `JsonTreeNode`, field path override in `JsonFieldExtractor`, setting toggle in `Settings`) must meet the 75% coverage minimum.

## Open decisions

None.
