# ChirpStack protobuf sample

Decodes ChirpStack v4 gateway and integration protobuf payloads with mqttprobe's built-in `protobuf` format.

The `.proto` files here are **ChirpStack's own, copied verbatim** — nothing was
trimmed, inlined, or rewritten. That is the point of this sample: you drop your
schema in as-is and point a manifest at it.

## Install

Copy `samples/protobuf` into your plugin folder and restart mqttprobe — the same
gesture as dropping in a plugin DLL. The sample directory mirrors the installed layout,
so nothing needs renaming or rearranging:

    samples/protobuf/chirpstack/   ->   Plugins/protobuf/chirpstack/

Installed, it looks like this:

    Plugins/
      protobuf/
        chirpstack/
          protobuf-schemas.json
          integration/integration.proto
          gw/gw.proto
          common/common.proto

### Adding a second, unrelated schema set

Give each set its own subdirectory with its own manifest — the same way `PluginLoader`
accepts a DLL either directly in the plugin folder or in its own subdirectory:

    Plugins/
      protobuf/
        chirpstack/
          protobuf-schemas.json
          integration/…  gw/…  common/…
        acme/
          protobuf-schemas.json
          acme/…

Each subdirectory is a separate import root, parsed independently, so two vendors can
both ship a `common/common.proto` without shadowing each other. Sets are independent:
delete a folder to remove it. Don't merge unrelated schemas into a single manifest —
they would then share an import root and could collide.

There is nothing to change in application configuration. The plugin folder differs per
host:

| Host | Plugin folder |
|------|---------------|
| Web (`dotnet run`) | `src/MqttProbe.Web/Plugins` |
| Docker | the mounted `/app/Plugins` volume |
| Photino Desktop (Windows) | `%USERPROFILE%\.config\mqttprobe\plugins` |
| MAUI Windows | `%LOCALAPPDATA%\Bluegrass IoT\com.bluegrassiot.mqttprobe\Data\plugins` |

## Installing from the UI

Build a package and install it from **Settings → Plugins → Install plugin**:

```bash
python scripts/packaging/pack-plugin.py samples/protobuf/chirpstack
```

Schema packages activate as soon as you press **Apply now** — no restart needed. Messages already
in the store keep the decoding they were captured with; new messages use the new schemas.

## Files

- `mqttprobe-plugin.json` — the package manifest read by **Install plugin**; identifies this as a
  `protobuf-schemas` package so it can be built with `pack-plugin.py` and installed from the UI.
- `protobuf-schemas.json` — the schema manifest: which `.proto` files to load, and which MQTT
  topics map to which message type.
- `integration/`, `gw/`, `common/` — unmodified ChirpStack v4 schema files, in their
  original directory layout so the `import` statements resolve as written.
- `sample-uplink.b64`: a base64-encoded application uplink payload for replay/testing. It
  covers the application `up` topic only. Gateway payloads are not included; the gateway
  mappings are exercised against live traffic.

`integration.proto` also imports `google/protobuf/timestamp.proto` and `struct.proto`.
You do **not** need to supply those — mqttprobe's protobuf parser resolves the Google
well-known types itself.

## What gets decoded

The manifest maps the ChirpStack v4 gateway topics, all seven device events, and the downlink command topic:

| Topic | Message |
|-------|---------|
| `application/+/device/+/event/up` | `integration.UplinkEvent` |
| `application/+/device/+/event/join` | `integration.JoinEvent` |
| `application/+/device/+/event/ack` | `integration.AckEvent` |
| `application/+/device/+/event/txack` | `integration.TxAckEvent` |
| `application/+/device/+/event/log` | `integration.LogEvent` |
| `application/+/device/+/event/status` | `integration.StatusEvent` |
| `application/+/device/+/event/location` | `integration.LocationEvent` |
| `application/+/device/+/command/down` | `integration.DownlinkCommand` |
| `+/gateway/+/event/up` | `gw.UplinkFrame` |
| `+/gateway/+/event/stats` | `gw.GatewayStats` |
| `+/gateway/+/event/ack` | `gw.DownlinkTxAck` |
| `+/gateway/+/command/down` | `gw.DownlinkFrame` |
| `+/gateway/+/state/conn` | `gw.ConnState` |

The application command topic is included because applications *publish* downlinks there. The
gateway topics cover what ChirpStack publishes to and receives from the gateway itself.

Gateway topics carry a region level in front, for example
`us915_1/gateway/008000000001fdbd/event/up`, so the `+` in the first position matches the
region name. If your broker publishes gateway topics without that region level
(`gateway/<gateway_id>/event/up`), add a second mapping for each message with the shorter
pattern. One pattern cannot match both shapes.

`gw.UplinkFrame.phy_payload` stays as the encrypted LoRaWAN PHYPayload bytes, shown base64
encoded. Gateway decoding exposes the radio metadata around those bytes. Decrypted application
data appears on the integration event topics, not here.

`state/conn` decodes to `gw.ConnState` with the gateway id and a `state` value of `ONLINE` or
`OFFLINE`.

## The manifest

```json
{
  "schemas": [
    {
      "files": [ "integration/integration.proto" ],
      "topicPattern": "application/+/device/+/event/up",
      "messageType": "integration.UplinkEvent"
    },
    {
      "files": [ "gw/gw.proto" ],
      "topicPattern": "+/gateway/+/event/up",
      "messageType": "gw.UplinkFrame"
    }
  ]
}
```

- `files` — paths relative to this folder. Only entry points are listed; imports are
  followed automatically. Repeating the same file across entries is fine — it is loaded
  once.
- `topicPattern` — an MQTT topic filter, `+` and `#` wildcards supported.
- `messageType` — the fully-qualified protobuf message name.

The first matching topic pattern wins, so put more specific patterns before broader ones.

## How ChirpStack publishes protobuf

ChirpStack's MQTT integration can be configured with a **protobuf** marshaler. It then
publishes device events to:

    application/{application_id}/device/{dev_eui}/event/{event_type}

and subscribes for downlinks on:

    application/{application_id}/device/{dev_eui}/command/{command}

Gateway traffic uses:

    {region}/gateway/{gateway_id}/event/{event_type}
    {region}/gateway/{gateway_id}/command/down
    {region}/gateway/{gateway_id}/state/conn

The gateway event types are `up`, `stats`, and `ack`. Connection state uses `state/conn`.
These templates are configurable in ChirpStack; if you have changed them, adjust
`topicPattern` to match.

### Tracking upstream instead

Nothing here is special to these copies. To follow upstream, clone
[chirpstack/chirpstack](https://github.com/chirpstack/chirpstack) and copy
`api/proto/{integration,gw,common}` in beside a manifest — the same `files`,
`topicPattern`, and `messageType` values apply unchanged.

## Replay the sample

    base64 -d sample-uplink.b64 | mosquitto_pub -t 'application/1/device/0102030405060708/event/up' -s

(or publish the decoded bytes with any MQTT client). mqttprobe will show the decoded
`UplinkEvent` fields (`f_cnt`, `device_info.dev_eui`, `data`, …) in the payload tree.

Gateway frames can be replayed the same way on `us915_1/gateway/<gateway_id>/event/up` (or
your region prefix). The decoded `phy_payload` remains the encrypted LoRaWAN bytes; mqttprobe
shows those bytes and the surrounding radio metadata, not decrypted sensor values.

Use `-s` (`--stdin-file`), not `-l` (`--stdin-line`): the payload is binary and contains
`0x0a` bytes, which `-l` would treat as message separators — publishing several truncated
messages instead of one intact `UplinkEvent`.

## Attribution

The `.proto` files are from [ChirpStack](https://github.com/chirpstack/chirpstack),
Copyright (c) 2022 Orne Brocaar, MIT licensed. They are redistributed here unmodified.
