# Browser smoke tests (manual, opt-in)

Opt-in Playwright for .NET acceptance tests. Provider tests cover sign-in and logout in the
Authentik and Keycloak labs. The local authentication test covers local sign-in and logout.
Separate exclusion and live-flow tests cover MQTT filtering, subscriptions, and the in-app
emulators using local authentication.

Each provider test:

1. signs in through the provider,
2. checks the live app shell (top bar plus the auto-opened connection dialog),
3. closes the dialog with its visible **Cancel** button,
4. clicks the real top-bar Logout and requires an actual browser document response from the
   provider's logout endpoint (exact origin and path, 2xx/3xx) before the app lands on `/Login`,
5. signs in again in the same context, demanding both username and password so silent SSO
   fails,
6. logs out a final time with a fresh per-click response waiter.

Shared plumbing lives in `BrowserSmokeTestBase.cs`; provider specifics live in `Authentik/`
and `Keycloak/`.

Not listed in `MqttProbe.slnx`, not wired into CI. **Always pass a `--filter`.** Both labs
serve the app on `https://localhost:5001`, so they collide: only one lab can run at a time,
and running the project without a filter executes both tests against whichever lab is up.

## Before you run

- One lab is up and healthy (`deploy/authentik` or `deploy/keycloak`): `docker compose ps`
  shows healthy and `https://localhost:5001/Login` shows that provider's sign-in button.
- Chromium once per machine: build the project, then run
  `powershell -ExecutionPolicy Bypass -File tests\MqttProbe.BrowserSmokeTests\bin\Debug\net10.0\playwright.ps1 install chromium`
  (skip it if Chromium is already installed by Playwright).

## Run (Windows PowerShell 5.1)

Both tests read credentials from the environment only, with no fallback values. The two
`dotnet test` commands below are **alternatives**: run only the one matching the lab that is
currently running, one at a time, never both:

```powershell
$env:MQTTPROBE_TEST_USERNAME = 'admitted-user'
$env:MQTTPROBE_TEST_PASSWORD = '<password>'

# Alternative A, only when the Authentik lab is running
dotnet test tests/MqttProbe.BrowserSmokeTests --filter FullyQualifiedName~AuthentikLabSmokeTests

# Alternative B, only when the Keycloak lab is running
dotnet test tests/MqttProbe.BrowserSmokeTests --filter FullyQualifiedName~KeycloakLabSmokeTests

# Unset the credentials afterwards
Remove-Item Env:MQTTPROBE_TEST_USERNAME, Env:MQTTPROBE_TEST_PASSWORD
```

Optional `MQTTPROBE_TEST_BASE_URL` must be an https loopback origin (default
`https://localhost:5001`). The Keycloak test requires exactly `https://localhost:5001` because
that is the client's redirect allowlist entry, and it fails early when the `/Login` button is
not the Keycloak one.

## Local authentication smoke test

`Local/LocalAuthenticationSmokeTests.cs` tests the local (non-OIDC) login flow: invalid
password error, valid sign-in and app shell, logout with redirect to `/Login`, re-login, and
a second logout. Same credentials as the OIDC tests. `MQTTPROBE_TEST_BASE_URL` defaults to
`https://localhost:5081` (must be an https loopback origin).

```powershell
$env:MQTTPROBE_TEST_USERNAME = 'your-local-user'
$env:MQTTPROBE_TEST_PASSWORD = '<password>'

dotnet test tests/MqttProbe.BrowserSmokeTests --filter FullyQualifiedName~LocalAuthenticationSmokeTests

Remove-Item Env:MQTTPROBE_TEST_USERNAME, Env:MQTTPROBE_TEST_PASSWORD
```

## Exclusion smoke tests

Two parameterized cases in `Exclusions/ExclusionBrowserSmokeTests.cs` exercise the
topic-exclusion feature through the real UI: one exact filter, one wildcard. Each test:

1. signs in once through the local Login page (`#username` / `#password`),
2. configures a unique new MQTT connection through the dialog (Name, Client ID, Host,
   Port, TLS off), clicks Connect, and waits for the connected chip and Pause button,
3. subscribes to `<root>/#` through the Subscriptions tab and publishes baseline messages
   via a plain-TCP MQTTnet client,
4. verifies each baseline topic+payload pair in the Browser payload table (`.pb-cell-topic`
   / `.pb-cell-payload`),
5. adds and removes an exclude filter through the Subscriptions Excluded topics panel and
   checks the count and list immediately, without leaving the tab,
6. returns to Browser, verifies matching topics are absent from the payload table while
   unaffected topics remain visible,
7. publishes blocked markers then an unaffected sentinel, waits for the sentinel, then
   asserts blocked messages stay absent across repeated DOM checks (~3 s),
8. removes the exclude filter, publishes a fresh matching message, and verifies it resumes
   while historical baseline and blocked payloads stay purged,
9. opens the Connection dialog, visits **On Connect**, then adds and removes an exclusion
   there and checks its count and row immediately without navigating away or reloading,
10. disconnects through the real UI button.

Each case uses a unique single-segment root (`exclude{Guid}`), explicit topic arrays for
matches and controls, and a per-case wildcard subscribe filter. The MQTT client is bounded
by a scenario cancellation token and disposed even if connect fails.

Not listed in `MqttProbe.slnx`, not wired into CI. **Always pass a `--filter`.**

### Prerequisites

- Docker running and Chromium installed once per machine (same as the auth tests above).
- A valid local HTTPS development certificate (`dotnet dev-certs https --check`).

The fixture starts a separate anonymous Mosquitto container on a dynamically mapped port and
the Web app on a dynamically selected HTTPS loopback port. It uses a unique temporary content
root and config directory, then creates a throwaway local account through the visible `/Setup`
page. It does not use or modify an existing app config, broker, or listener on port 1883. The
fixture is shared with the local live-flow and emulator tests and is disposed after the suite.

### Run (Windows PowerShell 5.1)

```powershell
dotnet test tests/MqttProbe.BrowserSmokeTests --filter FullyQualifiedName~ExclusionBrowserSmokeTests
```

## Live MQTT flow smoke tests

Five existing tests in `LiveFlows/` exercise connect, subscribe, publish, topic tree
expand/collapse, long topic overflow, status chip transitions, and mobile viewport
behavior against a real MQTT broker and local app instance. Two emulator cases also run the
Generic MQTT and Sparkplug B emulators from the UI, verify live Generic JSON or decoded
Sparkplug metrics, stop each emulator through the UI, and check Sparkplug STATE text in the
Browser payload table. The STATE message is published by a separate MQTTnet client because
the Sparkplug emulator does not publish STATE.

Not listed in `MqttProbe.slnx`, not wired into CI. **Always pass a `--filter`.**

### Prerequisites

The local fixture starts the app and an isolated Mosquitto container, provisions temporary
local credentials through `/Setup`, and removes its temporary config on teardown. You need
Docker, Chromium, and a valid local HTTPS development certificate. It does not connect to
the existing broker on port 1883.

### Run (Windows PowerShell 5.1)

```powershell
# Five existing live flow tests and the two emulator cases
dotnet test tests/MqttProbe.BrowserSmokeTests --filter FullyQualifiedName~MqttLiveFlowsSmokeTests

# Individual tests
dotnet test tests/MqttProbe.BrowserSmokeTests --filter Name~ConnectSubscribePublishVisibleDisconnect
dotnet test tests/MqttProbe.BrowserSmokeTests --filter Name~LongTopicStringOverflowAndScroll
dotnet test tests/MqttProbe.BrowserSmokeTests --filter Name~StatusChipTransitionsDisconnectReconnect
dotnet test tests/MqttProbe.BrowserSmokeTests --filter Name~TopicTreeExpandCollapseMultipleTopics
dotnet test tests/MqttProbe.BrowserSmokeTests --filter Name~MobileViewportWithLiveBroker

# Emulator cases only
dotnet test tests/MqttProbe.BrowserSmokeTests --filter Name~GenericEmulatorPublishesVisibleData
dotnet test tests/MqttProbe.BrowserSmokeTests --filter Name~SparkplugEmulatorPublishesDecodedDataAndStateText
```

## Release acceptance run

For the coordinated release/v1.0.7 acceptance run, use the isolated fixture for the local
exclusion and emulator cases. It starts its own Web app and Mosquitto instance, so no prepared
app or broker listener is needed. Run each filter separately so failures identify the scenario:

```powershell
dotnet test tests/MqttProbe.BrowserSmokeTests --filter FullyQualifiedName~ExclusionBrowserSmokeTests
dotnet test tests/MqttProbe.BrowserSmokeTests --filter Name~GenericEmulatorPublishesVisibleData
dotnet test tests/MqttProbe.BrowserSmokeTests --filter Name~SparkplugEmulatorPublishesDecodedDataAndStateText
```

The fixture generates credentials in memory, supplies them to the filtered test process, and
restores the prior environment after each local fixture. It does not log the credentials. The
exclusion and setup browsers are headed; live-flow and emulator browsers are headless. Every
smoke case uses a fresh browser context, ignores the local HTTPS certificate, limits actions
to 15 seconds and navigation to 40 seconds, and has a 180-second scenario budget. These tests
are manual, not part of the solution or CI.

## Behaviour

The provider tests and exclusion tests run headed; live flow and emulator tests run
headless. Waits are bounded with a 180s overall budget. A missing variable, an unexpected origin, an undismissable dialog, a missing
logout-endpoint response, or a stage timeout fails the test instead of skipping. Failures
report the failing stage plus the exception type; extra facts (origins, paths) are built by
the test itself. Credentials are read from the environment and never logged, and the tests
save no HAR, trace, screenshots, or storage state.
