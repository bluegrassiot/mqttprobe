# Keycloak OIDC Local Lab

Local Keycloak instance with integrated mqttprobe for developing and testing OIDC authentication. The realm, client, claim mapper, and test users mirror the integration test fixture in `tests/MqttProbe.TestInfrastructure/Fixtures/KeycloakFixture.cs`.

## Quick start

```bash
cd deploy/keycloak

# Copy env (adjust admin creds if desired)
cp .env.example .env

# Start (builds mqttprobe from repo root Dockerfile)
docker compose up --build -d

# Stop (preserves data)
docker compose down

# Stop and wipe all data
docker compose down -v
```

## What runs

| Service | Internal URL | External URL | Description |
|---|---|---|---|
| Keycloak | http://keycloak:8080 | https://keycloak.localhost:8443 | OIDC identity provider |
| mqttprobe | http://mqttprobe:8080 | https://localhost:5001 | Application under test |
| Caddy | - | - | TLS edge reverse proxy |

mqttprobe is configured automatically via environment variables to use Keycloak as its OIDC authority. No manual `appsettings.json` edits are needed.

## Trust the Caddy local CA

Caddy issues self-signed certificates for `keycloak.localhost` and `localhost`. Trust its root CA to remove browser warnings.

### Export the root CA certificate

```bash
docker compose cp caddy:/data/caddy/pki/authorities/local/root.crt ./caddy-root-ca.crt
```

The smoke scripts export this automatically to a temp file and clean it up on exit.

### Windows

```powershell
# Import into the Trusted Root Certification Authorities store
certutil -addstore -f "Root" caddy-root-ca.crt
```

The Windows smoke script passes `--ssl-no-revoke` to every `curl.exe` call. Caddy's private local CA has no online revocation endpoint, so Windows Schannel returns exit 60 ("the revocation status is unknown") even when the chain and hostname are valid. `--ssl-no-revoke` disables only the revocation lookup; chain and hostname verification still apply. The script never uses `-k`.

This flag is a curl-only workaround for the smoke test. Browsers and .NET's `HttpClient` do not have an equivalent opt-out: they either skip revocation checks by default or require the root CA to be trusted in the OS store (as shown above). If you see TLS errors from mqttprobe itself, import the Caddy root CA into the Windows trust store.

### macOS

```bash
sudo security add-trusted-cert -d -r trustRoot -k /Library/Keychains/System.keychain caddy-root-ca.crt
```

### Linux (Debian/Ubuntu)

```bash
sudo cp caddy-root-ca.crt /usr/local/share/ca-certificates/caddy-local.crt
sudo update-ca-certificates
```

### Linux (Fedora/RHEL)

```bash
sudo cp caddy-root-ca.crt /etc/pki/ca-trust/source/anchors/caddy-local.crt
sudo update-ca-trust
```

## Smoke test

Runs compose config validation, starts the stack with `--build`, exports the Caddy root CA, verifies Keycloak and mqttprobe health, checks OIDC discovery and endpoints, verifies mqttprobe `/health` and `/Login` through Caddy, checks admin API client and user presence, then tears down with volumes.

```bash
bash smoke.sh
```

On Windows PowerShell:

```powershell
.\smoke.ps1
```

## Test credentials

| Username | Password | `mqttprobe_access` claim | Expected result |
|---|---|---|---|
| `admitted-user` | `test-password` | `admin` | Login succeeds, access granted |
| `denied-user` | `test-password` | `viewer` | Login succeeds, access denied (claim value not in AcceptedValues) |

## Verify the flow

1. Open `https://localhost:5001` in a browser (trust the Caddy CA first).
2. Click login. You should be redirected to `https://keycloak.localhost:8443`.
3. Log in as `admitted-user`. You should be redirected back and see the mqttprobe dashboard.
4. Log out. You should be redirected back to the Keycloak logout page, then to `https://localhost:5001/signout-callback-oidc`.
5. Log in as `denied-user`. You should see an access-denied page because the claim value `viewer` is not in `AcceptedValues`.

## Keycloak admin console

Browse to `https://keycloak.localhost:8443/admin` and log in with the admin credentials from `.env` (default: `admin` / `admin`).

## Warnings

- **Local only.** This lab uses `start-dev`, `tls internal`, and test secrets. Do not expose it to a network or reuse these credentials in production.
- **Secrets are gitignored.** The `.env` file is listed in `.gitignore`. Never commit it.
- **Caddy CA is ephemeral.** The root CA lives in the `caddy-data` volume. Wiping volumes (`docker compose down -v`) regenerates it, and you must re-trust it.
- **Realm import runs once.** Keycloak imports `realm.json` on first start. To re-import after changing `realm.json`, wipe volumes and restart: `docker compose down -v && docker compose up --build -d`. Without `-v`, persisted realm data from the previous import is reused.
- **Back-channel logout uses internal URL.** The client's `backchannel.logout.url` is `http://mqttprobe:8080/oidc/backchannel-logout` (Docker-internal). Browser redirect and post-logout URLs remain `https://localhost:5001/...`, and the OIDC middleware's own `/signout-oidc` path is a separate, browser-facing route. If you see Keycloak back-channel logout errors against `localhost:5001` or against `/signout-oidc`, your realm was imported from an older `realm.json`; wipe volumes to re-import.
- **Loopback only.** Caddy ports are bound to `127.0.0.1`. Other machines cannot reach this lab.

## Cleanup

```bash
# Stop containers and remove volumes (wipes realm data, Caddy certs, and CA bundle)
docker compose down -v

# Remove the exported CA cert
rm -f caddy-root-ca.crt
```