# ChirpStack protobuf sample

Decodes ChirpStack v4 uplink events with mqttprobe's built-in `protobuf` format.

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

## Files

- `protobuf-schemas.json` — the manifest: which `.proto` files to load, and which MQTT
  topics map to which message type.
- `integration/`, `gw/`, `common/` — unmodified ChirpStack v4 schema files, in their
  original directory layout so the `import` statements resolve as written.
- `sample-uplink.b64` — a base64-encoded sample uplink payload for replay/testing. It
  covers the `up` topic only; the other mappings are exercised against live traffic.

`integration.proto` also imports `google/protobuf/timestamp.proto` and `struct.proto`.
You do **not** need to supply those — mqttprobe's protobuf parser resolves the Google
well-known types itself.

## What gets decoded

The manifest maps **every** topic ChirpStack uses — all seven device events plus the
downlink command topic:

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

The command topic is included because applications *publish* downlinks there — those
messages cross the broker, so mqttprobe sees them too.

## The manifest

```json
{
  "schemas": [
    {
      "files": [ "integration/integration.proto" ],
      "topicPattern": "application/+/device/+/event/up",
      "messageType": "integration.UplinkEvent"
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

Both templates are configurable in ChirpStack; if you have changed them, adjust
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

Use `-s` (`--stdin-file`), not `-l` (`--stdin-line`): the payload is binary and contains
`0x0a` bytes, which `-l` would treat as message separators — publishing several truncated
messages instead of one intact `UplinkEvent`.

## Attribution

The `.proto` files are from [ChirpStack](https://github.com/chirpstack/chirpstack),
Copyright (c) 2022 Orne Brocaar, MIT licensed. They are redistributed here unmodified.
