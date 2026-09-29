#!/usr/bin/env bash
# Smoke test for the Authentik OIDC local lab with integrated mqttprobe.
# Validates compose config, starts the stack, checks Authentik/Caddy health,
# OIDC discovery, mqttprobe health, OIDC challenge redirect, provider config,
# then tears everything down.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

EXPECTED_ISSUER="https://authentik.localhost:9443/application/o/mqttprobe/"
CA_CERT=""
PROJECT_NAME="ak-smoke-$$"

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

# Validate .env exists and required values are set
if [ ! -f .env ]; then
  echo "FAIL: .env file not found. Copy .env.example to .env and fill in real values." >&2
  exit 1
fi
for key in AUTHENTIK_SECRET_KEY PG_PASS AUTHENTIK_BOOTSTRAP_PASSWORD MQTT_PROBE_CLIENT_SECRET; do
  value=$(grep -E "^${key}=" .env | head -1 | cut -d= -f2-)
  if [ -z "$value" ]; then
    echo "FAIL: $key is missing or empty in .env" >&2
    exit 1
  fi
  if echo "$value" | grep -q 'CHANGE_ME'; then
    echo "FAIL: $key still contains CHANGE_ME placeholder in .env" >&2
    exit 1
  fi
done

echo "--- docker compose config ---"
docker compose -p "$PROJECT_NAME" config --quiet

echo "--- starting stack (build) ---"
docker compose -p "$PROJECT_NAME" up -d --build

echo "--- waiting for Authentik server health ---"
for i in $(seq 1 180); do
  container_id=$(docker compose -p "$PROJECT_NAME" ps -q server 2>/dev/null || true)
  if [ -n "$container_id" ]; then
    health_status=$(docker inspect --format '{{.State.Health.Status}}' "$container_id" 2>/dev/null || true)
    if [ "$health_status" = "healthy" ]; then
      echo "Authentik server healthy after ${i}s"
      break
    fi
  fi
  if [ "$i" -eq 180 ]; then
    echo "FAIL: Authentik server did not become healthy within 180s"
    docker compose -p "$PROJECT_NAME" logs server
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
    exit 1
  fi
  sleep 1
done

echo "--- checking OIDC discovery issuer (via host curl, retry 120s) ---"
CADDY_DISCOVERY=""
for i in $(seq 1 120); do
  CURL_OUT=$(curl -s --cacert "$CA_CERT" https://authentik.localhost:9443/application/o/mqttprobe/.well-known/openid-configuration 2>&1) && CURL_EXIT=0 || CURL_EXIT=$?
  if [ "$CURL_EXIT" -eq 0 ] && [ -n "$CURL_OUT" ]; then
    # Validate JSON is parseable
    if echo "$CURL_OUT" | python3 -m json.tool >/dev/null 2>&1; then
      CADDY_DISCOVERY="$CURL_OUT"
      echo "  discovery OK after ${i}s"
      break
    fi
  fi
  sleep 1
done
if [ -z "$CADDY_DISCOVERY" ]; then
  echo "FAIL: OIDC discovery timed out after 120s (exit=$CURL_EXIT)" >&2
  exit 1
fi

CADDY_ISSUER=$(echo "$CADDY_DISCOVERY" | grep -o '"issuer"[[:space:]]*:[[:space:]]*"[^"]*"' | head -1 | sed 's/.*"issuer"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/')
echo "  expected: $EXPECTED_ISSUER"
echo "  actual:   $CADDY_ISSUER"

if [ "$CADDY_ISSUER" != "$EXPECTED_ISSUER" ]; then
  echo "FAIL: OIDC issuer mismatch"
  exit 1
fi

echo "--- checking required OIDC endpoints ---"
for endpoint in authorization_endpoint token_endpoint end_session_endpoint; do
  value=$(echo "$CADDY_DISCOVERY" | grep -o "\"$endpoint\":[[:space:]]*\"[^\"]*\"" | head -1)
  if [ -z "$value" ]; then
    echo "FAIL: missing $endpoint in discovery"
    exit 1
  fi
  echo "  $endpoint: OK"
done

echo "--- checking mqttprobe /health through Caddy ---"
HEALTH_CODE=$(curl -s -o /dev/null -w "%{http_code}" --cacert "$CA_CERT" https://localhost:5001/health)
if [ "$HEALTH_CODE" != "200" ]; then
  echo "FAIL: mqttprobe /health through Caddy returned HTTP $HEALTH_CODE"
  exit 1
fi
echo "  /health HTTP 200: OK"

echo "--- checking mqttprobe /Login OIDC button ---"
LOGIN_PAGE=$(curl -s --cacert "$CA_CERT" https://localhost:5001/Login)
if echo "$LOGIN_PAGE" | grep -q 'authentik\.localhost.*openid'; then
  echo "  /Login contains OIDC Authentik link: OK"
elif echo "$LOGIN_PAGE" | grep -qE 'oidc|openid|Authentik'; then
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
  exit 1
fi

# Extract the antiforgery token from the hidden input field
AF_TOKEN=$(echo "$LOGIN_HTML" | grep -o 'name="__RequestVerificationToken"[^>]*value="[^"]*"' | head -1 | sed 's/.*value="\([^"]*\)".*/\1/')
if [ -z "$AF_TOKEN" ]; then
  rm -f "$COOKIE_JAR"
  echo "FAIL: Could not extract antiforgery token from /Login page"
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
  exit 1
}

# Read response headers to check for redirect
REDIRECT_URL=$(grep -i '^Location:' "$CHALLENGE_HEADERS" | head -1 | sed 's/Location:[[:space:]]*//' | tr -d '\r')
rm -f "$COOKIE_JAR" "$CHALLENGE_HEADERS"

if [ -z "$REDIRECT_URL" ]; then
  echo "FAIL: POST /Login?handler=Challenge did not return a redirect (no Location header)"
  exit 1
fi
echo "  redirect URL: $REDIRECT_URL"

# Verify the redirect goes to Authentik's OIDC auth endpoint
if echo "$REDIRECT_URL" | grep -q 'authentik\.localhost.*authorize'; then
  echo "  OIDC challenge redirects to Authentik: OK"
else
  echo "FAIL: OIDC challenge redirect does not go to Authentik (got: $REDIRECT_URL)"
  exit 1
fi

# Verify it includes client_id=mqttprobe
if echo "$REDIRECT_URL" | grep -q 'client_id=mqttprobe'; then
  echo "  client_id=mqttprobe in redirect: OK"
else
  echo "FAIL: OIDC challenge redirect missing client_id=mqttprobe (got: $REDIRECT_URL)"
  exit 1
fi

# Verify redirect_uri is set (PAR uses request_uri, direct uses redirect_uri)
if echo "$REDIRECT_URL" | grep -qE 'redirect_uri=|request_uri='; then
  echo "  redirect_uri set (via PAR or direct param): OK"
else
  echo "FAIL: OIDC challenge redirect missing redirect_uri or request_uri (got: $REDIRECT_URL)"
  exit 1
fi

echo "--- checking blueprint objects via ak shell ---"

# Check provider exists and has correct config
PROVIDER_CHECK=$(docker compose -p "$PROJECT_NAME" exec -T server ak shell -c "
import sys
try:
    from authentik.providers.oauth2.models import OAuth2Provider
    p = OAuth2Provider.objects.filter(name='mqttprobe').first()
    if p:
        print(f'provider:client_id={p.client_id}')
        print(f'provider:logout_uri={p.logout_uri}')
        print(f'provider:logout_method={p.logout_method}')
        flow = p.invalidation_flow
        print(f'provider:invalidation_flow_slug={flow.slug if flow else \"NONE\"}')
    else:
        print('provider:NOT_FOUND')
except Exception as e:
    print(f'ERROR: {e}', file=sys.stderr)
    sys.exit(1)
" 2>&1)
if [ $? -ne 0 ]; then
  echo "FAIL: ak shell error checking provider: $PROVIDER_CHECK" >&2
  exit 1
fi
if echo "$PROVIDER_CHECK" | grep -q 'provider:NOT_FOUND'; then
  echo "FAIL: mqttprobe provider not found" >&2
  exit 1
fi
if echo "$PROVIDER_CHECK" | grep -q 'ERROR:'; then
  echo "FAIL: provider check error: $PROVIDER_CHECK" >&2
  exit 1
fi
echo "  provider: OK"

# Verify backchannel logout is configured correctly
if echo "$PROVIDER_CHECK" | grep -q 'provider:logout_uri=http://mqttprobe:8080/signout-oidc'; then # DevSkim: ignore DS137138 compose-internal backchannel URL
  echo "  backchannel logout_uri (internal): OK"
else
  echo "FAIL: provider logout_uri is not http://mqttprobe:8080/signout-oidc" >&2 # DevSkim: ignore DS137138 compose-internal backchannel URL
  exit 1
fi
if echo "$PROVIDER_CHECK" | grep -q 'provider:logout_method=backchannel'; then
  echo "  logout_method=backchannel: OK"
else
  echo "FAIL: provider logout_method is not backchannel" >&2
  exit 1
fi

# Verify invalidation flow is the custom mqttprobe-invalidation-flow
if echo "$PROVIDER_CHECK" | grep -q 'provider:invalidation_flow_slug=mqttprobe-invalidation-flow'; then
  echo "  invalidation_flow=mqttprobe-invalidation-flow: OK"
else
  echo "FAIL: provider invalidation_flow is not mqttprobe-invalidation-flow" >&2
  exit 1
fi

# Check custom invalidation flow has correct stage binding
FLOW_CHECK=$(docker compose -p "$PROJECT_NAME" exec -T server ak shell -c "
import sys
try:
    from authentik.flows.models import Flow, FlowStageBinding
    f = Flow.objects.filter(slug='mqttprobe-invalidation-flow').first()
    if f:
        print(f'flow:slug={f.slug}')
        print(f'flow:designation={f.designation}')
        bindings = FlowStageBinding.objects.filter(target=f).order_by('order')
        for b in bindings:
            print(f'binding:order={b.order}:stage={b.stage.__class__.__name__}:{b.stage.name}')
        if not bindings.exists():
            print('binding:NONE')
    else:
        print('flow:NOT_FOUND')
except Exception as e:
    print(f'ERROR: {e}', file=sys.stderr)
    sys.exit(1)
" 2>&1)
if [ $? -ne 0 ]; then
  echo "FAIL: ak shell error checking flow: $FLOW_CHECK" >&2
  exit 1
fi
if echo "$FLOW_CHECK" | grep -q 'flow:NOT_FOUND'; then
  echo "FAIL: mqttprobe-invalidation-flow not found" >&2
  exit 1
fi
if echo "$FLOW_CHECK" | grep -q 'ERROR:'; then
  echo "FAIL: flow check error: $FLOW_CHECK" >&2
  exit 1
fi
echo "  custom invalidation flow: OK"

# Verify flow designation is invalidation
if echo "$FLOW_CHECK" | grep -q 'flow:designation=invalidation'; then
  echo "  flow designation=invalidation: OK"
else
  echo "FAIL: mqttprobe-invalidation-flow designation is not invalidation" >&2
  exit 1
fi

# Verify the flow has a UserLogoutStage binding at order 0
if echo "$FLOW_CHECK" | grep -q 'binding:order=0:stage=UserLogoutStage:default-invalidation-logout'; then
  echo "  flow binding: order=0 UserLogoutStage default-invalidation-logout: OK"
else
  echo "FAIL: mqttprobe-invalidation-flow does not have UserLogoutStage at order 0" >&2
  exit 1
fi

echo "--- checking end_session_endpoint runtime accessibility ---"
# Extract end_session_endpoint from discovery JSON
END_SESSION_URL=$(echo "$CADDY_DISCOVERY" | grep -o '"end_session_endpoint"[[:space:]]*:[[:space:]]*"[^"]*"' | head -1 | sed 's/.*"end_session_endpoint"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/')
if [ -z "$END_SESSION_URL" ]; then
  echo "FAIL: end_session_endpoint not found in discovery document" >&2
  exit 1
fi
echo "  end_session_endpoint: $END_SESSION_URL"

# Hit the end_session_endpoint with client_id (no credentials needed).
# A live endpoint returns 200 (invalidation flow page) or 302 (redirect to auth
# flow when no session exists). 404 or 500 indicates a broken configuration.
END_SESSION_CODE=$(curl -s -o /dev/null -w "%{http_code}" --cacert "$CA_CERT" "${END_SESSION_URL}?client_id=mqttprobe")
if [ "$END_SESSION_CODE" = "404" ] || [ "$END_SESSION_CODE" = "500" ]; then
  echo "FAIL: end_session_endpoint returned HTTP $END_SESSION_CODE (broken or misconfigured)" >&2
  exit 1
fi
echo "  end_session_endpoint HTTP $END_SESSION_CODE (live): OK"

# Check scope mapping exists and has correct scope_name
SCOPE_CHECK=$(docker compose -p "$PROJECT_NAME" exec -T server ak shell -c "
import sys
try:
    from authentik.providers.oauth2.models import ScopeMapping
    sm = ScopeMapping.objects.filter(managed='mqttprobe.io/providers/oauth2/scope-mqttprobe-access').first()
    if sm:
        print(f'scope_mapping:name={sm.name}')
        print(f'scope_mapping:scope_name={sm.scope_name}')
        print(f'scope_mapping:expression={repr(sm.expression)}')
    else:
        print('scope_mapping:NOT_FOUND')
except Exception as e:
    print(f'ERROR: {e}', file=sys.stderr)
    sys.exit(1)
" 2>&1)
if [ $? -ne 0 ]; then
  echo "FAIL: ak shell error checking scope mapping: $SCOPE_CHECK" >&2
  exit 1
fi
if echo "$SCOPE_CHECK" | grep -q 'scope_mapping:NOT_FOUND'; then
  echo "FAIL: mqttprobe scope mapping not found" >&2
  exit 1
fi
if echo "$SCOPE_CHECK" | grep -q 'ERROR:'; then
  echo "FAIL: scope mapping check error: $SCOPE_CHECK" >&2
  exit 1
fi
echo "  scope_mapping: OK"

# Verify scope_name is 'profile' (not a custom name that would never be requested)
if echo "$SCOPE_CHECK" | grep -q 'scope_mapping:scope_name=profile'; then
  echo "  scope_name=profile: OK"
else
  echo "FAIL: scope mapping scope_name is not 'profile' (claim would never be emitted)" >&2
  exit 1
fi

# Verify ak_is_group_member uses 'name=' not 'group_name=' (Django ORM field)
if echo "$SCOPE_CHECK" | grep -q 'scope_mapping:expression=.*group_name='; then
  echo "FAIL: scope mapping uses group_name= (invalid Django field); must use name=" >&2
  exit 1
fi
if echo "$SCOPE_CHECK" | grep -q 'scope_mapping:expression=.*name='; then
  echo "  ak_is_group_member(name=...): OK"
fi

# Check group exists
GROUP_CHECK=$(docker compose -p "$PROJECT_NAME" exec -T server ak shell -c "
import sys
try:
    from authentik.core.models import Group
    g = Group.objects.filter(name='mqttprobe Users').first()
    if g:
        print(f'group:name={g.name}')
    else:
        print('group:NOT_FOUND')
except Exception as e:
    print(f'ERROR: {e}', file=sys.stderr)
    sys.exit(1)
" 2>&1)
if [ $? -ne 0 ]; then
  echo "FAIL: ak shell error checking group: $GROUP_CHECK" >&2
  exit 1
fi
if echo "$GROUP_CHECK" | grep -q 'group:NOT_FOUND'; then
  echo "FAIL: mqttprobe Users group not found" >&2
  exit 1
fi
if echo "$GROUP_CHECK" | grep -q 'ERROR:'; then
  echo "FAIL: group check error: $GROUP_CHECK" >&2
  exit 1
fi
echo "  group: OK"

# Check application exists
APP_CHECK=$(docker compose -p "$PROJECT_NAME" exec -T server ak shell -c "
import sys
try:
    from authentik.core.models import Application
    a = Application.objects.filter(slug='mqttprobe').first()
    if a:
        print(f'app:name={a.name}')
    else:
        print('app:NOT_FOUND')
except Exception as e:
    print(f'ERROR: {e}', file=sys.stderr)
    sys.exit(1)
" 2>&1)
if [ $? -ne 0 ]; then
  echo "FAIL: ak shell error checking application: $APP_CHECK" >&2
  exit 1
fi
if echo "$APP_CHECK" | grep -q 'app:NOT_FOUND'; then
  echo "FAIL: mqttprobe application not found" >&2
  exit 1
fi
if echo "$APP_CHECK" | grep -q 'ERROR:'; then
  echo "FAIL: application check error: $APP_CHECK" >&2
  exit 1
fi
echo "  application: OK"

echo ""
echo "PASS: all checks succeeded"
echo ""
echo "NOTE: Users are not created by the blueprint. Create admitted-user and denied-user"
echo "      in the Authentik admin UI as described in README.md."
