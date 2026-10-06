# Authentik OIDC Local Lab

Local Authentik instance with integrated mqttprobe for developing and testing OIDC authentication. The blueprint provisions an OAuth2 provider, application, custom scope mapping, and group that mirror the integration test fixture.

## Quick start

```bash
cd deploy/authentik

# Copy env and fill in real values
cp .env.example .env
# Edit .env: replace all CHANGE_ME values with real secrets

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
| Authentik | http://server:9000 | https://authentik.localhost:9443 | OIDC identity provider |
| mqttprobe | http://mqttprobe:8080 | https://localhost:5001 | Application under test |
| Caddy | - | - | TLS edge reverse proxy |
| PostgreSQL | - | - | Authentik database |

mqttprobe is configured automatically via environment variables to use Authentik as its OIDC authority. No manual `appsettings.json` edits are needed.

Its `/app/config` directory (DataProtection keys under `dp-keys/`, `secrets.dat`, certificates, and settings) lives in the named `mqttprobe-config` volume. That volume survives `docker compose up -d --build --force-recreate mqttprobe`, so antiforgery tokens and stored secrets stay decryptable across image rebuilds. `docker compose down -v` deletes it along with the database and Caddy certs.

## Readiness

Authentik takes 30-90 seconds to complete migrations on first start. Wait until the server healthcheck passes:

```bash
docker compose ps   # server should show "healthy"
```

The Authentik admin UI is at `https://authentik.localhost:9443`. Log in as `akadmin` with the `AUTHENTIK_BOOTSTRAP_PASSWORD` from your `.env`.

## Trust the Caddy local CA

Caddy issues self-signed certificates for `authentik.localhost` and `localhost`. Trust its root CA to remove browser warnings.

### Export the root CA certificate

```bash
docker compose cp caddy:/data/caddy/pki/authorities/local/root.crt ./caddy-root-ca.crt
```

The smoke scripts export this automatically to a temp file and clean it up on exit.

### Windows

```powershell
certutil -addstore -f "Root" caddy-root-ca.crt
```

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

Runs compose config validation, starts the stack with `--build`, verifies Authentik and mqttprobe health, checks OIDC discovery and endpoints, verifies mqttprobe `/health` and `/Login` through Caddy, submits the OIDC Challenge form to verify the redirect to Authentik, checks provider configuration including backchannel logout, and asserts blueprint objects exist via `ak shell`. Tears down with volumes on exit.

```bash
bash smoke.sh
```

On Windows PowerShell:

```powershell
.\smoke.ps1
```

## Manual test users

The blueprint does not create users. Create them manually in the Authentik admin UI (`https://authentik.localhost:9443/if/admin/`).

### akadmin

The built-in admin user is created automatically on first start. Use it to log in to the admin UI and create the other users.

1. Browse to `https://authentik.localhost:9443/if/admin/`.
2. Log in as `akadmin` with the `AUTHENTIK_BOOTSTRAP_PASSWORD` from your `.env`.

### admitted-user

1. In the admin UI, go to **Directory > Users** and click **Create**.
2. Set **Username** to `admitted-user`, set a password, and click **Create**.
3. Go to **Directory > Groups**, open **mqttprobe Users**, and add `admitted-user` to it.

Both steps are required: the user must exist AND be a member of **mqttprobe Users**. The custom scope mapping emits `mqttprobe_access=admin` only for members of that group. If you skip the group membership step, the user can authenticate but mqttprobe will deny access because the claim is missing.

After adding a user to the group, log out of mqttprobe and log in again (or use a different browser/incognito window) to acquire a fresh token that includes the claim.

### denied-user

1. In the admin UI, go to **Directory > Users** and click **Create**.
2. Set **Username** to `denied-user`, set a password, and click **Create**.
3. Do **not** add this user to the **mqttprobe Users** group.

This user can authenticate through Authentik (no IdP-level block). The custom scope mapping emits no `mqttprobe_access` claim because the user is outside the group. mqttprobe itself denies access after successful authentication.

## Verify the flow

1. Open `https://localhost:5001` in a browser (trust the Caddy CA first).
2. Click login. You should be redirected to `https://authentik.localhost:9443`.
3. Log in as `admitted-user`. You should be redirected back and see the mqttprobe dashboard.
4. Log out. You should be redirected back to the Authentik logout page, then to `https://localhost:5001/signout-callback-oidc`.
5. Log in as `denied-user`. Authentication succeeds (Authentik does not block this user), but mqttprobe denies access because the `mqttprobe_access` claim is absent.

## Logout behavior

The provider is configured with back-channel logout (`logout_uri: http://mqttprobe:8080/oidc/backchannel-logout`). This means Authentik sends a server-to-server POST to mqttprobe's internal HTTP endpoint when a session ends. The browser redirect and post-logout callback URLs remain the public `https://localhost:5001/...` addresses, and the OIDC middleware's own `/signout-oidc` path is a separate, browser-facing route.

The provider uses a custom invalidation flow (`mqttprobe-invalidation-flow`) that includes a `UserLogoutStage`. When a user signs out of mqttprobe, the RP-initiated logout hits this flow, which destroys the shared Authentik SSO session cookie and manages provider-scoped token invalidation. Because the SSO session is terminated, other applications sharing the same Authentik instance will also lose their sessions on the next request.

## Blueprint behavior

The blueprint applies on every Authentik worker cycle (approximately every 60 minutes) and on file changes. Manual edits to blueprint-managed objects (provider, scope mapping, group, application) may be overwritten. The blueprint does not create users; they must be added manually.

## Cleanup

```bash
# Stop containers and remove volumes (wipes database, Caddy certs, and mqttprobe config)
docker compose down -v

# Remove the exported CA cert
rm -f caddy-root-ca.crt
```

## Warnings

- **Local only.** This lab uses `tls internal`, test secrets, and a self-signed CA. Do not expose it to a network or reuse these credentials in production.
- **Secrets are gitignored.** The `.env` file is listed in `.gitignore`. Never commit it.
- **Caddy CA is ephemeral.** The root CA lives in the `caddy-data` volume. Wiping volumes (`docker compose down -v`) regenerates it, and you must re-trust it.
- **Blueprint applies on every cycle.** Authentik reapplies blueprints every 60 minutes and on file change. Manual edits to blueprint-managed objects may be overwritten.
- **Loopback only.** Caddy ports are bound to `127.0.0.1`. Other machines cannot reach this lab.
- **Config volume is separate.** mqttprobe's DataProtection keys and secrets live in the `mqttprobe-config` volume. Removing only that volume forces new key material (existing encrypted secrets become undecryptable) without touching Authentik users or the database.
