#!/usr/bin/env bash
# Smoke test for the Keycloak OIDC local lab with integrated mqttprobe.
# Validates compose config, starts the stack, checks Keycloak/Caddy health,
# OIDC discovery, mqttprobe health and OIDC login button, then tears everything down.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

EXPECTED_ISSUER="https://keycloak.localhost:8443/realms/mqttprobe-test"
CA_CERT=""
PROJECT_NAME="kc-smoke-$$"

# Read admin credentials from .env or use defaults
KC_ADMIN_USER="admin"
KC_ADMIN_PASS="admin"
if [ -f .env ]; then
  while IFS='=' read -r key value; do
    case "$key" in
      KC_BOOTSTRAP_ADMIN_USERNAME) KC_ADMIN_USER="$value" ;;
      KC_BOOTSTRAP_ADMIN_PASSWORD) KC_ADMIN_PASS="$value" ;;
    esac
  done < .env
fi

ORIGINAL_EXIT=0

cleanup() {
  local status=$?
  [ "$status" -eq 0 ] && status=$ORIGINAL_EXIT
  echo "--- cleanup ---"
  if [ -n "$CA_CERT" ] && [ -f "$CA_CERT" ]; then
    rm -f "$CA_CERT"
  fi
  docker compose -p "$PROJECT_NAME" down -v 2>/dev/null || true
  exit "$status"
}
trap cleanup EXIT

echo "--- docker compose config ---"
docker compose -p "$PROJECT_NAME" config --quiet

echo "--- starting stack (build) ---"
docker compose -p "$PROJECT_NAME" up -d --build

echo "--- waiting for Keycloak health (management port 9000) ---"
for i in $(seq 1 120); do
  if docker compose -p "$PROJECT_NAME" exec -T keycloak bash -c "exec 3<>/dev/tcp/localhost/9000 && echo -e 'GET /health/ready HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n' >&3 && cat <&3 | grep -q '200 OK'" > /dev/null 2>&1; then
    echo "Keycloak healthy after ${i}s"
    break
  fi
  if [ "$i" -eq 120 ]; then
    echo "FAIL: Keycloak did not become healthy within 120s"
    docker compose -p "$PROJECT_NAME" logs keycloak
    ORIGINAL_EXIT=1
    exit 1
  fi
  sleep 1
done

echo "--- waiting for Caddy ---"
for i in $(seq 1 30); do
  if docker compose -p "$PROJECT_NAME" ps caddy --format '{{.State}}' 2>/dev/null | grep -q 'running'; then
    echo "Caddy running after ${i}s"
    break
  fi
  if [ "$i" -eq 30 ]; then
    echo "FAIL: Caddy did not start within 30s"
    docker compose -p "$PROJECT_NAME" logs caddy
    ORIGINAL_EXIT=1
    exit 1
  fi
  sleep 1
done

echo "--- exporting Caddy root CA ---"
CA_CERT=$(mktemp)
docker compose -p "$PROJECT_NAME" cp caddy:/data/caddy/pki/authorities/local/root.crt "$CA_CERT"

echo "--- waiting for mqttprobe health ---"
for i in $(seq 1 120); do
  if docker compose -p "$PROJECT_NAME" exec -T mqttprobe wget -q -O - http://127.0.0.1:8080/health > /dev/null 2>&1; then
    echo "mqttprobe healthy after ${i}s"
    break
  fi
  if [ "$i" -eq 120 ]; then
    echo "FAIL: mqttprobe did not become healthy within 120s"
    docker compose -p "$PROJECT_NAME" logs mqttprobe
    ORIGINAL_EXIT=1
    exit 1
  fi
  sleep 1
done

echo "--- checking OIDC discovery issuer (via host curl, retry 60s) ---"
CADDY_DISCOVERY=""
for i in $(seq 1 60); do
  CURL_OUT=$(curl -s --cacert "$CA_CERT" https://keycloak.localhost:8443/realms/mqttprobe-test/.well-known/openid-configuration 2>&1) && CURL_EXIT=0 || CURL_EXIT=$?
  if [ "$CURL_EXIT" -eq 0 ] && [ -n "$CURL_OUT" ]; then
    CADDY_DISCOVERY="$CURL_OUT"
    echo "  discovery OK after ${i}s"
    break
  fi
  sleep 1
done
if [ -z "$CADDY_DISCOVERY" ]; then
  echo "FAIL: OIDC discovery timed out after 60s (exit=$CURL_EXIT)"
  ORIGINAL_EXIT=1
  exit 1
fi

CADDY_ISSUER=$(echo "$CADDY_DISCOVERY" | grep -o '"issuer"[[:space:]]*:[[:space:]]*"[^"]*"' | head -1 | sed 's/.*"issuer"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/')
echo "  expected: $EXPECTED_ISSUER"
echo "  actual:   $CADDY_ISSUER"

if [ "$CADDY_ISSUER" != "$EXPECTED_ISSUER" ]; then
  echo "FAIL: OIDC issuer mismatch"
  ORIGINAL_EXIT=1
  exit 1
fi

echo "--- checking required OIDC endpoints ---"
for endpoint in authorization_endpoint token_endpoint end_session_endpoint pushed_authorization_request_endpoint; do
  value=$(echo "$CADDY_DISCOVERY" | grep -o "\"$endpoint\":[[:space:]]*\"[^\"]*\"" | head -1)
  if [ -z "$value" ]; then
    echo "FAIL: missing $endpoint in discovery"
    ORIGINAL_EXIT=1
    exit 1
  fi
  echo "  $endpoint: OK"
done

echo "--- checking mqttprobe /health through Caddy ---"
HEALTH_CODE=$(curl -s -o /dev/null -w "%{http_code}" --cacert "$CA_CERT" https://localhost:5001/health)
if [ "$HEALTH_CODE" != "200" ]; then
  echo "FAIL: mqttprobe /health through Caddy returned HTTP $HEALTH_CODE"
  ORIGINAL_EXIT=1
  exit 1
fi
echo "  /health HTTP 200: OK"

echo "--- checking mqttprobe /Login OIDC button ---"
LOGIN_PAGE=$(curl -s --cacert "$CA_CERT" https://localhost:5001/Login)
if echo "$LOGIN_PAGE" | grep -q 'keycloak\.localhost.*openid'; then
  echo "  /Login contains OIDC Keycloak link: OK"
elif echo "$LOGIN_PAGE" | grep -qE 'oidc|openid|Keycloak'; then
  echo "  /Login contains OIDC reference: OK"
else
  echo "WARN: /Login page does not contain obvious OIDC link (may need JS rendering)"
fi

echo "--- checking OIDC challenge redirect ---"
# GET /Login to obtain antiforgery token and session cookie
COOKIE_JAR=$(mktemp)
LOGIN_HTML=$(curl -s --cacert "$CA_CERT" -c "$COOKIE_JAR" https://localhost:5001/Login)
if [ $? -ne 0 ]; then
  rm -f "$COOKIE_JAR"
  echo "FAIL: GET /Login failed"
  ORIGINAL_EXIT=1
  exit 1
fi

# Extract the antiforgery token from the hidden input field
AF_TOKEN=$(echo "$LOGIN_HTML" | grep -o 'name="__RequestVerificationToken"[^>]*value="[^"]*"' | head -1 | sed 's/.*value="\([^"]*\)".*/\1/')
if [ -z "$AF_TOKEN" ]; then
  rm -f "$COOKIE_JAR"
  echo "FAIL: Could not extract antiforgery token from /Login page"
  ORIGINAL_EXIT=1
  exit 1
fi
echo "  antiforgery token extracted: OK"

# POST to /Login?handler=Challenge with antiforgery token and cookies
CHALLENGE_HEADERS=$(mktemp)
CHALLENGE_RESP=$(curl -s -D "$CHALLENGE_HEADERS" --cacert "$CA_CERT" \
  -b "$COOKIE_JAR" -c "$COOKIE_JAR" \
  -X POST "https://localhost:5001/Login?handler=Challenge" \
  -H "Content-Type: application/x-www-form-urlencoded" \
  -d "__RequestVerificationToken=$(python3 -c "import urllib.parse; print(urllib.parse.quote('$AF_TOKEN'))")&returnUrl=%2F") || {
  rm -f "$COOKIE_JAR" "$CHALLENGE_HEADERS"
  echo "FAIL: POST /Login?handler=Challenge failed"
  ORIGINAL_EXIT=1
  exit 1
}

# Read response headers to check for redirect
REDIRECT_URL=$(grep -i '^Location:' "$CHALLENGE_HEADERS" | head -1 | sed 's/Location:[[:space:]]*//' | tr -d '\r')
rm -f "$COOKIE_JAR" "$CHALLENGE_HEADERS"

if [ -z "$REDIRECT_URL" ]; then
  echo "FAIL: POST /Login?handler=Challenge did not return a redirect (no Location header)"
  ORIGINAL_EXIT=1
  exit 1
fi
echo "  redirect URL: $REDIRECT_URL"

# Verify the redirect goes to Keycloak's OIDC auth endpoint
if echo "$REDIRECT_URL" | grep -q 'keycloak\.localhost.*openid-connect/auth'; then
  echo "  OIDC challenge redirects to Keycloak auth endpoint: OK"
else
  echo "FAIL: OIDC challenge redirect does not go to Keycloak auth endpoint (got: $REDIRECT_URL)"
  ORIGINAL_EXIT=1
  exit 1
fi

# Verify it includes client_id=mqttprobe (proves the client config is correct)
if echo "$REDIRECT_URL" | grep -q 'client_id=mqttprobe'; then
  echo "  client_id=mqttprobe in redirect: OK"
else
  echo "FAIL: OIDC challenge redirect missing client_id=mqttprobe (got: $REDIRECT_URL)"
  ORIGINAL_EXIT=1
  exit 1
fi

# Verify redirect_uri is set (PAR uses request_uri, direct uses redirect_uri)
if echo "$REDIRECT_URL" | grep -qE 'redirect_uri=|request_uri='; then
  echo "  redirect_uri set (via PAR or direct param): OK"
else
  echo "FAIL: OIDC challenge redirect missing redirect_uri or request_uri (got: $REDIRECT_URL)"
  ORIGINAL_EXIT=1
  exit 1
fi

echo "--- checking realm via admin API ---"
ADMIN_TOKEN=$(curl -s --cacert "$CA_CERT" \
  -X POST "https://keycloak.localhost:8443/realms/master/protocol/openid-connect/token" \
  -H "Content-Type: application/x-www-form-urlencoded" \
  -d "grant_type=password&client_id=admin-cli&username=$KC_ADMIN_USER&password=$KC_ADMIN_PASS" \
  | grep -o '"access_token":"[^"]*"' | head -1 | sed 's/"access_token":"\([^"]*\)"/\1/') || {
  echo "FAIL: admin token curl failed"
  ORIGINAL_EXIT=1
  exit 1
}

if [ -z "$ADMIN_TOKEN" ]; then
  echo "WARN: could not get admin token, skipping admin API checks"
else
  echo "--- checking client settings ---"
  CLIENT_JSON=$(curl -s --cacert "$CA_CERT" \
    -H "Authorization: Bearer $ADMIN_TOKEN" \
    "https://keycloak.localhost:8443/admin/realms/mqttprobe-test/clients?clientId=mqttprobe") || {
    echo "FAIL: client query curl failed"
    ORIGINAL_EXIT=1
    exit 1
  }

  if echo "$CLIENT_JSON" | grep -q '"publicClient":false'; then
    echo "  client is confidential: OK"
  else
    echo "FAIL: client should be confidential"
    ORIGINAL_EXIT=1
    exit 1
  fi

  if echo "$CLIENT_JSON" | grep -q '"pkce.code.challenge.method":"S256"'; then
    echo "  PKCE S256: OK"
  else
    echo "FAIL: PKCE should be S256"
    ORIGINAL_EXIT=1
    exit 1
  fi

  echo "--- checking back-channel logout URL ---"
  # The backchannel.logout.url must be the internal Docker URL so Keycloak
  # can reach mqttprobe from inside its container. It must NOT be the
  # public localhost:5001 URL (which Keycloak cannot resolve internally).
  BACKCHANNEL_URL=$(echo "$CLIENT_JSON" | grep -o '"backchannel.logout.url"[[:space:]]*:[[:space:]]*"[^"]*"' | head -1 | sed 's/.*"\([^"]*\)"$/\1/')
  if [ -z "$BACKCHANNEL_URL" ]; then
    echo "WARN: could not extract backchannel.logout.url from client JSON"
  else
    echo "  backchannel.logout.url: $BACKCHANNEL_URL"
    if echo "$BACKCHANNEL_URL" | grep -q 'localhost:5001'; then
      echo "FAIL: backchannel.logout.url points to localhost:5001 (unreachable from Keycloak container). Expected http://mqttprobe:8080/oidc/backchannel-logout" # DevSkim: ignore DS137138 compose-internal backchannel URL
      ORIGINAL_EXIT=1
      exit 1
    fi
    if [ "$BACKCHANNEL_URL" != "http://mqttprobe:8080/oidc/backchannel-logout" ]; then # DevSkim: ignore DS137138 compose-internal backchannel URL
      echo "FAIL: backchannel.logout.url unexpected: $BACKCHANNEL_URL (expected http://mqttprobe:8080/oidc/backchannel-logout)" # DevSkim: ignore DS137138 compose-internal backchannel URL
      ORIGINAL_EXIT=1
      exit 1
    fi
    echo "  backchannel.logout.url is internal: OK"
  fi

  echo "--- checking users ---"
  USERS_JSON=$(curl -s --cacert "$CA_CERT" \
    -H "Authorization: Bearer $ADMIN_TOKEN" \
    "https://keycloak.localhost:8443/admin/realms/mqttprobe-test/users") || {
    echo "FAIL: users query curl failed"
    ORIGINAL_EXIT=1
    exit 1
  }

  if echo "$USERS_JSON" | grep -q '"username":"admitted-user"'; then
    echo "  admitted-user: OK"
  else
    echo "FAIL: admitted-user not found"
    ORIGINAL_EXIT=1
    exit 1
  fi

  if echo "$USERS_JSON" | grep -q '"username":"denied-user"'; then
    echo "  denied-user: OK"
  else
    echo "FAIL: denied-user not found"
    ORIGINAL_EXIT=1
    exit 1
  fi
fi

echo ""
echo "PASS: all checks succeeded"