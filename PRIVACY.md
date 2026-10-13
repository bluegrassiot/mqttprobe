# Privacy Policy

MQTTProbe is an MQTT diagnostic app that runs on your device and connects to brokers you
choose. There is no MQTTProbe account, no sign-in, and no server of ours in the middle.

This policy covers the native iOS app. It is written to match what the app actually does
today, so it will change if the app changes.

## Where your data goes

Your MQTT traffic goes to the broker you enter in a connection. That
broker is operated by you or by the provider you choose. Once a message leaves
your device, that operator sees it, and it becomes subject to their retention rules,
their jurisdiction, and their access controls. We have no visibility into it and no
ability to delete it.

### What the app puts on the wire

When you connect, the app performs an MQTT handshake and then sends and receives messages.
Concretely, the app transmits:

- **Connection parameters**, in the MQTT CONNECT packet: the client ID you set or that the
  app generated, the MQTT protocol version, the keep-alive interval, the clean start or
  clean session flag, and the session expiry interval under MQTT 5.
- **Your MQTT username, and your password if you set one.**
- **Each subscription you add**, as a topic filter in a SUBSCRIBE packet.
- **Each message you publish**, with its topic, payload, quality of service, and retained
  flag.

If you have imported a client certificate for mutual TLS, the app presents that
certificate during the TLS handshake.

### What stays on your side of the connection

Some of what you type is configuration for how the app connects, not message data sent to
anyone:

- **Host, port, and protocol.** These decide where the TCP or WebSocket connection is
  opened. The host name is resolved through the normal DNS lookup your device performs,
  which involves your configured DNS resolver, not us.
- **TLS settings.** Whether TLS is on, which TLS versions are offered, and whether server
  certificate checking is bypassed are settings on your device for the handshake. They are
  not sent as message content.

Traffic is encrypted only when you turn TLS on and connect to a port the broker serves over
TLS. On a plaintext port, your credentials and messages travel unencrypted across whatever
network is carrying them. The app shows a warning when you enable its
allow-untrusted-certificate option, because that option skips server identity checks.

### Excluded topic filters stay local

The topic filters you add as exclusions are applied on your device. The app uses them to
drop incoming messages and to clear topics it has already stored. They are not sent to the
broker, and the broker does not know you set them.

### No developer telemetry

The app contains no analytics, advertising, crash reporting, or usage tracking, and it
contains no code that sends your broker traffic, your messages, or your settings to us. We
run no service that receives data from the app.

Opening a link from inside the app, such as the privacy policy link in Settings, hands the
address to your browser, and that site applies its own privacy policy and its own rules.
We do not control those sites and we do not see what you do on them. Your device and iOS
itself also perform their own network activity, such as App Store traffic and system
services, which this policy does not cover.

## What stays on your device

Everything the app remembers lives inside its own sandbox on your device:

- **Connection settings.** Names, hosts, ports, protocol versions, TLS flags, client
  IDs, and your subscriptions are written to a JSON settings file inside the app's
  private app-data folder.
- **Broker passwords.** Passwords are stripped out of that settings file before it is
  written. They are kept in the iOS Keychain under a key derived from the connection
  name, so the plaintext password is not sitting in a file you could copy off the
  device.
- **Client certificates and their keys.** Imported certificate files are stored in the
  app's private folder with the strongest iOS file protection class, complete, which
  means they are unreadable while the device is locked. The keys that encrypt them are
  held separately in the Keychain and are marked as this device only, so they are not
  synced to iCloud Keychain or restored onto a different device. The certificate files
  are also marked to be left out of device backups.
- **Chart and emulator configuration.** Saved charts and saved node emulator setups are
  part of the settings file described above.
- **Protobuf schema packages you import.** When you import a plugin package, the app
  unpacks it into its app-data folder under "plugins/protobuf/<package id>". What stays
  there is the package content: the .proto definitions, the JSON file that maps MQTT
  topics to message types, and the package manifest holding its name, version, and author.
  No message traffic is written there. Schema changes do not require a restart. See
  "Deleting your data" below for removal and cleanup behavior.
- **Messages, in memory only.** Received messages, their topics, and their payloads are
  held in memory while the app is open. The app does not write them to disk, and they are
  gone when the app closes. How many are kept is capped by a limit you can change in
  settings, and the app trims the oldest as new ones arrive.

## The clipboard, on purpose

The app can copy a topic or payload you select, and can read JSON from the clipboard to
populate a publish form. It does that only when you tap the control. Treat the clipboard as
part of the wider system rather than as private to this app: on current iOS versions an app
reading the clipboard is expected to ask your permission first, but content you copy out
of MQTTProbe sits in a system-wide pasteboard, and it may be offered to other apps,
including ones you later paste it into, and handled by clipboard-related system features.
That is worth keeping in mind before copying a payload that contains something sensitive.

## Files you import

If you import a client certificate, the app reads the file you select through the system
document picker. That picker grants access to the one file you choose. The app keeps a
copy inside its sandbox under the protections described above, and it deletes its copy
when you remove the certificate or delete the connection.

The app does not scan your photo library, contacts, calendars, health data, or files
outside the picker. It does not use the microphone, camera, or location.

The plugin package picker works the same way: it grants access to the one archive you
choose. On iOS the app accepts protobuf schema packages, which are data rather than
executable code, and refuses any package that carries a plugin DLL.

The app creates temporary copies of the archive and unpacked files in a staging folder
inside its sandbox. It tries to delete these copies when the import finishes. If cleanup
fails, they can remain until you delete the app. The original file you selected is only
read, never changed or moved.

Failed cleanup during a schema upgrade can also leave a backup of the previous version
inside the sandbox until you delete the app.

## Backups, updates, and other devices

Two caveats, stated plainly rather than promised away.

Backups are the operating system's decision, not ours. Files in the app's private app-data
folder are generally eligible for iCloud and Finder backups, which means your settings
file can be included in a device backup and can be restored onto a replacement device. The
protobuf schema packages you imported live in that same folder and are not excluded, so
they can travel with a backup and reappear on a replacement device alongside your settings.
The certificate files are deliberately excluded, so a restored backup will not carry
your client certificate across to a new device. If you restore a backup onto a different
device, expect to re-import certificates and to enter broker passwords again.

Updates come from the App Store, not from the app. The app contains no self-update
mechanism, and on iOS it does not download or run new code at runtime.

## Deleting your data

Deleting a connection removes its saved settings, removes the password the app stored for
it, and deletes any client certificate attached to it.

Removing an imported schema package attempts to delete its folder immediately, without a
restart. If deletion fails, the app reports that it could not remove the package. Deleting
the app removes its schema package folders and any leftover staging copies.

Deleting the app removes the files it wrote, including the settings file and the stored
certificate files. Keychain entries are a separate case and are worth being precise about.
iOS does not reliably remove an app's Keychain items when the app is deleted, so some
Keychain data, including broker passwords you entered, may persist after uninstalling and
could reappear if you install the app again on the same device. The app cannot delete
Keychain items after it has been removed, and we have no way to reach them. If you want
that data gone, delete the connections from within the app before removing it.

Data that already reached a broker is outside the app's reach. Ask that broker's operator
if you need it removed there.

## Children

The app is a technical tool and does not collect information about anyone, including
children. It is not directed at children.

## Contact

For a privacy question or a privacy complaint, open an issue at
<https://github.com/bluegrassiot/mqttprobe/issues>.

Please do not post broker credentials, certificates, message payloads, hostnames of private
brokers, or personal information in a public issue. A public issue is visible to everyone
and may be indexed and archived. Describe the problem without the sensitive values, and
remove or rotate anything you have already exposed.

For a security vulnerability, do not open a public issue. Follow the process in
[SECURITY.md](SECURITY.md).

## Changes to this policy

If the app's data handling changes, this file changes with it in the same change. The
version that ships is the one published in the project repository at
<https://github.com/bluegrassiot/mqttprobe/blob/main/PRIVACY.md>.
