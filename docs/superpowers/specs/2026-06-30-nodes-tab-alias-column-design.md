# Nodes Tab Alias Column

## Summary

Add an `Alias` column to the Sparkplug B Nodes tab metric tables so operators can see the numeric alias assigned to each metric. The alias is already tracked internally by `SparkplugTopologyService` for name resolution but is discarded when creating `SpbMetricSnapshot` records. This change surfaces that data in the UI.

## Goals

1. Display the Sparkplug B metric alias (a `ulong` assigned by the node during NBIRTH/DBIRTH) in the Nodes tab metric tables.
2. Show the alias number when present; show an em dash (`—`) when the metric has no alias or the alias is zero.
3. Place the Alias column between the existing Type and Value columns in both the node-level and device-level metric tables.
4. Preserve alias information through the snapshot creation pipeline so it is available for display and future use.

## Non-goals

- Editing or assigning aliases in the UI.
- Sorting or filtering by alias (not requested; can be added later).
- Changing alias resolution logic in `SparkplugTopologyService` (the existing `AliasMap` lookup remains unchanged).
- Persisting aliases across sessions or exporting them.

## User-facing behavior

The Nodes tab detail panel shows two metric tables: one for the selected node and one for each expanded device. Both tables currently display four columns: Name, Type, Value, Updated.

After this change, both tables display five columns: Name, Type, **Alias**, Value, Updated.

| Alias present | Display |
|---|---|
| `metric.Alias > 0` | The alias number (e.g. `1`, `2`, `42`) |
| `metric.Alias` absent or `0` | `—` (em dash) |

The Alias column is right-aligned to align numeric values visually. No column header sort is added in this iteration.

## Architecture / implementation design

### 1. Extend `SpbMetricSnapshot`

**File:** `src/MqttProbe.Shared/Models/Sparkplug/SpbTopology.cs`

Add an `Alias` property to the record:

```csharp
public sealed record SpbMetricSnapshot(
    string Name,
    string DataType,
    string Value,
    DateTime LastUpdated,
    ulong? Alias = null);
```

Using `ulong?` (nullable) keeps the record backward-compatible: existing call sites that construct snapshots without an alias continue to compile, with `Alias` defaulting to `null`.

### 2. Preserve alias in `CreateMetricSnapshot`

**File:** `src/MqttProbe.Shared/Services/Sparkplug/SparkplugTopologyService.cs`

Update `CreateMetricSnapshot` to accept and forward the alias:

```csharp
private static SpbMetricSnapshot CreateMetricSnapshot(Payload.Types.Metric metric, string name)
{
    var (value, dataType) = ExtractMetricValue(metric);
    var alias = metric.Alias != 0 ? metric.Alias : (ulong?)null;
    return new SpbMetricSnapshot(name, dataType, value, DateTime.UtcNow, alias);
}
```

No changes to callers (`HandleNBirth`, `HandleNData`, `HandleDBirth`, `HandleDData`) — they already pass the full `metric` object. The `AliasMap` population logic is unaffected.

### 3. Add Alias column to `SparkplugNodesView.razor`

**File:** `src/MqttProbe.Shared/Components/Sparkplug/SparkplugNodesView.razor`

In both the node metrics table (`HeaderContent` + `RowTemplate`, around lines 223–243) and the device metrics table (around lines 285–299), insert an `Alias` column between Type and Value:

**HeaderContent** — add after the Type `<MudTh>`:

```razor
<MudTh Class="spb-th-right">Alias</MudTh>
```

**RowTemplate** — add after the Type `<MudTd>`:

```razor
<MudTd Class="spb-td-alias spb-th-right">
    @(context.Alias.HasValue ? context.Alias.Value.ToString() : "—")
</MudTd>
```

The `spb-th-right` class already exists for right-aligning the Value column; reusing it keeps alignment consistent. A `spb-td-alias` class is added for potential future styling (e.g. monospace font); initially it carries no custom rules.

### 4. No CSS changes required

The existing `spb-th-right` class handles right-alignment. The new `spb-td-alias` class is a hook for future styling only. No new CSS rules are needed for this iteration.

## Data flow

```
MQTT payload (protobuf)
  └─ Payload.Types.Metric  (has .Alias as ulong, 0 = absent)
       │
       ├─ HandleNBirth / HandleDBirth
       │    ├─ AliasMap[metric.Alias] = metric.Name   (for resolution)
       │    └─ CreateMetricSnapshot(metric, name)      (NEW: preserves alias)
       │
       └─ HandleNData / HandleDData
            ├─ AliasMap lookup for name resolution     (unchanged)
            └─ CreateMetricSnapshot(metric, name)      (NEW: preserves alias)
                 │
                 └─ SpbMetricSnapshot { Alias: ulong? }
                      │
                      └─ SparkplugNodesView.razor
                           └─ Renders alias or "—"
```

## Testing plan

### Topology service tests

**File:** `tests/MqttProbe.Tests/Services/Sparkplug/SparkplugTopologyServiceTests.cs`

1. **NBirth preserves alias** — Send an NBIRTH payload with metrics that have aliases. Assert `node.Metrics[i].Alias` equals the expected `ulong?` value.
2. **NBirth metric without alias gets null** — Send an NBIRTH with a metric where `Alias = 0`. Assert `Alias` is `null`.
3. **NData preserves alias** — After NBIRTH, send NDATA with a metric carrying an alias. Assert the updated snapshot's `Alias` is set.
4. **DBirth preserves alias** — Same as NBirth but for device metrics.
5. **DData preserves alias** — Same as NData but for device metrics.

### Component tests

**File:** `tests/MqttProbe.Tests/Components/Sparkplug/SparkplugNodesViewTests.cs`

1. **Alias column header rendered** — Select a node with metrics. Assert the markup contains `>Alias<` in the table header.
2. **Alias value displayed** — Create a `SpbMetricSnapshot` with `Alias = 42`. Render the node detail. Assert markup contains `42`.
3. **Em dash for null alias** — Create a `SpbMetricSnapshot` with `Alias = null`. Render. Assert markup contains the em dash `—`.
4. **Device metric table also shows alias** — Expand a device with metrics. Assert the device metric table header contains `>Alias<` and rows show alias values.

### Coverage target

New code must meet the 75% coverage minimum. The changes are small (one record property, one method tweak, one Razor template insertion) and the tests above cover all branches.

## Open decisions

None.
