# Browser smoke tests (manual, opt-in)

Two Playwright for .NET tests, one per local OIDC lab (Authentik or Keycloak). Each test:

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
5. adds an exclude filter through the Subscriptions Excluded topics panel,
6. returns to Browser, verifies matching topics are absent from the payload table while
   unaffected topics remain visible,
7. publishes blocked markers then an unaffected sentinel, waits for the sentinel, then
   asserts blocked messages stay absent across repeated DOM checks (~3 s),
8. removes the exclude filter, publishes a fresh matching message, and verifies it resumes
   while historical baseline and blocked payloads stay purged,
9. disconnects through the real UI button.

Each case uses a unique single-segment root (`exclude{Guid}`), explicit topic arrays for
matches and controls, and a per-case wildcard subscribe filter. The MQTT client is bounded
by a scenario cancellation token and disposed even if connect fails.

Not listed in `MqttProbe.slnx`, not wired into CI. **Always pass a `--filter`.**

### Prerequisites

- A separate local app instance on `https://localhost:5081` (temporary dev config). Adjust
  `MQTTPROBE_TEST_BASE_URL` if your instance runs elsewhere.
- A real MQTT broker reachable at `localhost:1883` (the existing local Mosquitto service).
  Adjust `MQTTPROBE_TEST_MQTT_HOST` / `MQTTPROBE_TEST_MQTT_PORT` if it differs.
- Chromium installed once per machine (same as the auth tests above).
- Local login credentials in the environment.

### Run (Windows PowerShell 5.1)

```powershell
$env:MQTTPROBE_TEST_USERNAME = 'your-local-user'
$env:MQTTPROBE_TEST_PASSWORD = '<password>'

dotnet test tests/MqttProbe.BrowserSmokeTests --filter FullyQualifiedName~ExclusionBrowserSmokeTests

# Unset the credentials afterwards
Remove-Item Env:MQTTPROBE_TEST_USERNAME, Env:MQTTPROBE_TEST_PASSWORD
```

Optional environment variables:

| Variable | Default | Description |
|---|---|---|
| `MQTTPROBE_TEST_BASE_URL` | `https://localhost:5081` | App origin (must be https loopback) |
| `MQTTPROBE_TEST_MQTT_HOST` | `localhost` | MQTT broker host |
| `MQTTPROBE_TEST_MQTT_PORT` | `1883` | MQTT broker port |

## Live MQTT flow smoke tests

Five tests in `LiveFlows/` exercise connect, subscribe, publish, topic tree
expand/collapse, long topic overflow, status chip transitions, and mobile viewport
behavior against a real MQTT broker and local app instance.

Not listed in `MqttProbe.slnx`, not wired into CI. **Always pass a `--filter`.**

### Prerequisites

- A local app instance on `https://localhost:5001` (or set `MQTTPROBE_TEST_BASE_URL`).
- A real MQTT broker at `localhost:1883` (or set `MQTTPROBE_TEST_MQTT_HOST` /
  `MQTTPROBE_TEST_MQTT_PORT`).
- Chromium installed once per machine.
- Local login credentials in the environment.

### Run (Windows PowerShell 5.1)

```powershell
$env:MQTTPROBE_TEST_USERNAME = 'your-local-user'
$env:MQTTPROBE_TEST_PASSWORD = '<password>'

# All five live flow tests
dotnet test tests/MqttProbe.BrowserSmokeTests --filter FullyQualifiedName~MqttLiveFlowsSmokeTests

# Individual tests
dotnet test tests/MqttProbe.BrowserSmokeTests --filter Name~ConnectSubscribePublishVisibleDisconnect
dotnet test tests/MqttProbe.BrowserSmokeTests --filter Name~LongTopicStringOverflowAndScroll
dotnet test tests/MqttProbe.BrowserSmokeTests --filter Name~StatusChipTransitionsDisconnectReconnect
dotnet test tests/MqttProbe.BrowserSmokeTests --filter Name~TopicTreeExpandCollapseMultipleTopics
dotnet test tests/MqttProbe.BrowserSmokeTests --filter Name~MobileViewportWithLiveBroker

Remove-Item Env:MQTTPROBE_TEST_USERNAME, Env:MQTTPROBE_TEST_PASSWORD
```

### Environment variables

| Variable | Default | Description |
|---|---|---|
| `MQTTPROBE_TEST_BASE_URL` | `https://localhost:5001` | App origin (must be https loopback) |
| `MQTTPROBE_TEST_MQTT_HOST` | `localhost` | MQTT broker host |
| `MQTTPROBE_TEST_MQTT_PORT` | `1883` | MQTT broker port |

## Behaviour

The browser runs headed so you can watch the flow; waits are bounded with a 180s overall
budget. A missing variable, an unexpected origin, an undismissable dialog, a missing
logout-endpoint response, or a stage timeout fails the test instead of skipping. Failures
report the failing stage plus the exception type; extra facts (origins, paths) are built by
the test itself. Credentials are read from the environment and never logged, and the tests
save no HAR, trace, screenshots, or storage state.
