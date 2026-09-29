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

## Behaviour

The browser runs headed so you can watch the flow; waits are bounded with a 180s overall
budget. A missing variable, an unexpected origin, an undismissable dialog, a missing
logout-endpoint response, or a stage timeout fails the test instead of skipping. Failures
report the failing stage plus the exception type; extra facts (origins, paths) are built by
the test itself. Credentials are read from the environment and never logged, and the tests
save no HAR, trace, screenshots, or storage state.
