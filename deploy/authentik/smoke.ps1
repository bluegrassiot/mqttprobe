#!/usr/bin/env pwsh
# Smoke test for the Authentik OIDC local lab with integrated mqttprobe (Windows).
# Validates compose config, starts the stack, checks Authentik/Caddy health,
# OIDC discovery, mqttprobe health, OIDC challenge redirect, provider config,
# then tears everything down.
$ErrorActionPreference = "Stop"

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $ScriptDir

$ExpectedIssuer = "https://authentik.localhost:9443/application/o/mqttprobe/"
$CaCert = $null
$ProjectName = "ak-smoke-$(Get-Random -Maximum 99999)"

# Read bootstrap password from .env
$AkAdminPass = $null
if (Test-Path .env) {
    Get-Content .env | ForEach-Object {
        if ($_ -match '^AUTHENTIK_BOOTSTRAP_PASSWORD=(.+)$') { $AkAdminPass = $Matches[1].Trim() }
    }
}

try {
    # Validate .env exists and required values are set
    if (-not (Test-Path .env)) {
        throw "FAIL: .env file not found. Copy .env.example to .env and fill in real values."
    }
    $envContent = Get-Content .env -Raw
    foreach ($key in @("AUTHENTIK_SECRET_KEY", "PG_PASS", "AUTHENTIK_BOOTSTRAP_PASSWORD", "MQTT_PROBE_CLIENT_SECRET")) {
        $match = [regex]::Match($envContent, "(?m)^$key=(.*)")
        if (-not $match.Success -or $match.Groups[1].Value.Trim().Length -eq 0) {
            throw "FAIL: $key is missing or empty in .env"
        }
        if ($match.Groups[1].Value -match 'CHANGE_ME') {
            throw "FAIL: $key still contains CHANGE_ME placeholder in .env"
        }
    }

    Write-Host "--- docker compose config ---"
    docker compose -p $ProjectName config --quiet
    if ($LASTEXITCODE -ne 0) { throw "docker compose config failed" }

    Write-Host "--- starting stack (build) ---"
    docker compose -p $ProjectName up -d --build
    if ($LASTEXITCODE -ne 0) { throw "docker compose up failed" }

    Write-Host "--- waiting for Authentik server health ---"
    $healthy = $false
    for ($i = 1; $i -le 180; $i++) {
        $containerId = docker compose -p $ProjectName ps -q server 2>&1
        if ($containerId -and $containerId.Trim().Length -gt 0) {
            $healthStatus = docker inspect --format '{{.State.Health.Status}}' $containerId.Trim() 2>&1
            if ($healthStatus -match 'healthy') {
                Write-Host "Authentik server healthy after ${i}s"
                $healthy = $true
                break
            }
        }
        Start-Sleep -Seconds 1
    }
    if (-not $healthy) {
        docker compose -p $ProjectName logs server
        throw "FAIL: Authentik server did not become healthy within 180s"
    }

    Write-Host "--- waiting for Caddy ---"
    $caddyRunning = $false
    for ($i = 1; $i -le 30; $i++) {
        $state = docker compose -p $ProjectName ps caddy --format '{{.State}}' 2>&1
        if ($state -match 'running') {
            Write-Host "Caddy running after ${i}s"
            $caddyRunning = $true
            break
        }
        Start-Sleep -Seconds 1
    }
    if (-not $caddyRunning) {
        docker compose -p $ProjectName logs caddy
        throw "FAIL: Caddy did not start within 30s"
    }

    Write-Host "--- exporting Caddy root CA ---"
    $CaCert = Join-Path $env:TEMP "caddy-root-ca-$(Get-Random).crt"
    docker compose -p $ProjectName cp caddy:/data/caddy/pki/authorities/local/root.crt $CaCert
    if ($LASTEXITCODE -ne 0) { throw "docker compose cp failed" }

    Write-Host "--- waiting for mqttprobe health ---"
    $mqttHealthy = $false
    for ($i = 1; $i -le 120; $i++) {
        $null = docker compose -p $ProjectName exec -T mqttprobe wget -q -O - http://127.0.0.1:8080/health 2>&1
        if ($LASTEXITCODE -eq 0) {
            Write-Host "mqttprobe healthy after ${i}s"
            $mqttHealthy = $true
            break
        }
        Start-Sleep -Seconds 1
    }
    if (-not $mqttHealthy) {
        Write-Host "FAIL: mqttprobe did not become healthy within 120s"
        docker compose -p $ProjectName logs mqttprobe
        throw "mqttprobe health check timed out"
    }

    Write-Host "--- checking OIDC discovery issuer (via host curl, retry 120s) ---"
    $discoveryJson = $null
    for ($i = 1; $i -le 120; $i++) {
        $curlOut = & curl.exe -s --cacert $CaCert --ssl-no-revoke "https://authentik.localhost:9443/application/o/mqttprobe/.well-known/openid-configuration" 2>&1
        $curlExit = $LASTEXITCODE
        if ($curlExit -eq 0 -and $curlOut -match '\S') {
            # Validate JSON is parseable
            try {
                $null = $curlOut | ConvertFrom-Json
                $discoveryJson = $curlOut
                Write-Host "  discovery OK after ${i}s"
                break
            } catch {
                # Not valid JSON yet, retry
            }
        }
        Start-Sleep -Seconds 1
    }
    if (-not $discoveryJson) {
        throw "OIDC discovery timed out after 120s (exit=$curlExit)"
    }

    $issuerMatch = [regex]::Match($discoveryJson, '"issuer"\s*:\s*"([^"]*)"')
    $actualIssuer = if ($issuerMatch.Success) { $issuerMatch.Groups[1].Value } else { "" }
    Write-Host "  expected: $ExpectedIssuer"
    Write-Host "  actual:   $actualIssuer"

    if ($actualIssuer -ne $ExpectedIssuer) {
        throw "OIDC issuer mismatch"
    }

    Write-Host "--- checking required OIDC endpoints ---"
    foreach ($endpoint in @("authorization_endpoint", "token_endpoint", "end_session_endpoint")) {
        $epMatch = [regex]::Match($discoveryJson, "`"$endpoint`"\s*:\s*`"[^`"]*`"")
        if (-not $epMatch.Success) {
            throw "FAIL: missing $endpoint in discovery"
        }
        Write-Host "  $endpoint`: OK"
    }

    Write-Host "--- checking mqttprobe /health through Caddy ---"
    $healthResp = & curl.exe -s -o NUL -w "%{http_code}" --cacert $CaCert --ssl-no-revoke "https://localhost:5001/health" 2>&1
    if ($healthResp -ne "200") {
        throw "FAIL: mqttprobe /health through Caddy returned HTTP $healthResp"
    }
    Write-Host "  /health HTTP 200: OK"

    Write-Host "--- checking mqttprobe /Login OIDC button ---"
    $loginPage = & curl.exe -s --cacert $CaCert --ssl-no-revoke "https://localhost:5001/Login" 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "FAIL: mqttprobe /Login curl failed"
    }
    if ($loginPage -match 'authentik\.localhost.*openid') {
        Write-Host "  /Login contains OIDC Authentik link: OK"
    } elseif ($loginPage -match 'oidc|openid|Authentik') {
        Write-Host "  /Login contains OIDC reference: OK"
    } else {
        Write-Host "WARN: /Login page does not contain obvious OIDC link (may need JS rendering)"
    }

    Write-Host "--- checking OIDC challenge redirect ---"
    # GET /Login to obtain antiforgery token and session cookie
    $cookieJar = Join-Path $env:TEMP "curl-cookies-$(Get-Random).txt"
    $loginHtml = & curl.exe -s --cacert $CaCert --ssl-no-revoke -c $cookieJar "https://localhost:5001/Login" 2>&1
    if ($LASTEXITCODE -ne 0) {
        Remove-Item $cookieJar -ErrorAction SilentlyContinue
        throw "FAIL: GET /Login failed"
    }

    # Extract the antiforgery token from the hidden input field
    $afTokenMatch = [regex]::Match($loginHtml, 'name="__RequestVerificationToken"[^>]*value="([^"]*)"')
    if (-not $afTokenMatch.Success) {
        Remove-Item $cookieJar -ErrorAction SilentlyContinue
        throw "FAIL: Could not extract antiforgery token from /Login page"
    }
    $afToken = $afTokenMatch.Groups[1].Value
    Write-Host "  antiforgery token extracted: OK"

    # POST to /Login?handler=Challenge with antiforgery token and cookies
    $challengeHeaders = Join-Path $env:TEMP "curl-headers-$(Get-Random).txt"
    $challengeResp = & curl.exe -s -D $challengeHeaders --cacert $CaCert --ssl-no-revoke `
        -b $cookieJar -c $cookieJar `
        -X POST "https://localhost:5001/Login?handler=Challenge" `
        -H "Content-Type: application/x-www-form-urlencoded" `
        -d "__RequestVerificationToken=$([uri]::EscapeDataString($afToken))&returnUrl=%2F" 2>&1
    $challengeExit = $LASTEXITCODE

    # Read response headers to check for redirect
    $headers = if (Test-Path $challengeHeaders) { Get-Content $challengeHeaders -Raw } else { "" }
    Remove-Item $cookieJar, $challengeHeaders -ErrorAction SilentlyContinue

    if ($challengeExit -ne 0) {
        throw "FAIL: POST /Login?handler=Challenge failed (exit=$challengeExit)"
    }

    # The Challenge handler should return a redirect to Authentik
    $locationMatch = [regex]::Match($headers, 'Location:\s*(\S+)')
    if (-not $locationMatch.Success) {
        throw "FAIL: POST /Login?handler=Challenge did not return a redirect (no Location header)"
    }
    $redirectUrl = $locationMatch.Groups[1].Value
    Write-Host "  redirect URL: $redirectUrl"

    # Verify the redirect goes to Authentik's OIDC auth endpoint
    if ($redirectUrl -match 'authentik\.localhost.*authorize') {
        Write-Host "  OIDC challenge redirects to Authentik: OK"
    } else {
        throw "FAIL: OIDC challenge redirect does not go to Authentik (got: $redirectUrl)"
    }

    # Verify it includes client_id=mqttprobe
    if ($redirectUrl -match 'client_id=mqttprobe') {
        Write-Host "  client_id=mqttprobe in redirect: OK"
    } else {
        throw "FAIL: OIDC challenge redirect missing client_id=mqttprobe (got: $redirectUrl)"
    }

    # Verify redirect_uri=https://localhost:5001/signin-oidc is set
    $hasRedirectUri = ($redirectUrl -match 'redirect_uri=https(%3A%2F%2F|://)localhost(%3A|:)5001(%2F|/)signin-oidc')
    $hasRequestUri = ($redirectUrl -match 'request_uri=')
    if ($hasRedirectUri -or $hasRequestUri) {
        Write-Host "  redirect_uri set (via PAR or direct param): OK"
    } else {
        throw "FAIL: OIDC challenge redirect missing redirect_uri or request_uri (got: $redirectUrl)"
    }

    Write-Host "--- checking blueprint objects via ak shell ---"

    # Check provider exists and has correct config
    $providerCheck = docker compose -p $ProjectName exec -T server ak shell -c "
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
" 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "FAIL: ak shell error checking provider: $providerCheck"
    }
    if ($providerCheck -match 'provider:NOT_FOUND') {
        throw "FAIL: mqttprobe provider not found"
    }
    if ($providerCheck -match 'ERROR:') {
        throw "FAIL: provider check error: $providerCheck"
    }
    Write-Host "  provider: OK"

    # Verify backchannel logout is configured correctly
    if ($providerCheck -match 'provider:logout_uri=http://mqttprobe:8080/signout-oidc') { # DevSkim: ignore DS137138 compose-internal backchannel URL
        Write-Host "  backchannel logout_uri (internal): OK"
    } else {
        throw "FAIL: provider logout_uri is not http://mqttprobe:8080/signout-oidc" # DevSkim: ignore DS137138 compose-internal backchannel URL
    }
    if ($providerCheck -match 'provider:logout_method=backchannel') {
        Write-Host "  logout_method=backchannel: OK"
    } else {
        throw "FAIL: provider logout_method is not backchannel"
    }

    # Verify invalidation flow is the custom mqttprobe-invalidation-flow
    if ($providerCheck -match 'provider:invalidation_flow_slug=mqttprobe-invalidation-flow') {
        Write-Host "  invalidation_flow=mqttprobe-invalidation-flow: OK"
    } else {
        throw "FAIL: provider invalidation_flow is not mqttprobe-invalidation-flow"
    }

    # Check custom invalidation flow has correct stage binding
    $flowCheck = docker compose -p $ProjectName exec -T server ak shell -c "
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
" 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "FAIL: ak shell error checking flow: $flowCheck"
    }
    if ($flowCheck -match 'flow:NOT_FOUND') {
        throw "FAIL: mqttprobe-invalidation-flow not found"
    }
    if ($flowCheck -match 'ERROR:') {
        throw "FAIL: flow check error: $flowCheck"
    }
    Write-Host "  custom invalidation flow: OK"

    # Verify flow designation is invalidation
    if ($flowCheck -match 'flow:designation=invalidation') {
        Write-Host "  flow designation=invalidation: OK"
    } else {
        throw "FAIL: mqttprobe-invalidation-flow designation is not invalidation"
    }

    # Verify the flow has a UserLogoutStage binding at order 0
    if ($flowCheck -match 'binding:order=0:stage=UserLogoutStage:default-invalidation-logout') {
        Write-Host "  flow binding: order=0 UserLogoutStage default-invalidation-logout: OK"
    } else {
        throw "FAIL: mqttprobe-invalidation-flow does not have UserLogoutStage at order 0"
    }

    Write-Host "--- checking end_session_endpoint runtime accessibility ---"
    # Extract end_session_endpoint from discovery JSON
    $endSessionMatch = [regex]::Match($discoveryJson, '"end_session_endpoint"\s*:\s*"([^"]*)"')
    if (-not $endSessionMatch.Success) {
        throw "FAIL: end_session_endpoint not found in discovery document"
    }
    $endSessionUrl = $endSessionMatch.Groups[1].Value
    Write-Host "  end_session_endpoint: $endSessionUrl"

    # Hit the end_session_endpoint with client_id (no credentials needed).
    # A live endpoint returns 200 (invalidation flow page) or 302 (redirect to auth
    # flow when no session exists). 404 or 500 indicates a broken configuration.
    $endSessionResp = & curl.exe -s -o NUL -w "%{http_code}" --cacert $CaCert --ssl-no-revoke "$endSessionUrl`?client_id=mqttprobe" 2>&1
    if ($endSessionResp -eq "404" -or $endSessionResp -eq "500") {
        throw "FAIL: end_session_endpoint returned HTTP $endSessionResp (broken or misconfigured)"
    }
    Write-Host "  end_session_endpoint HTTP $endSessionResp (live): OK"

    # Check scope mapping exists and has correct scope_name
    $scopeCheck = docker compose -p $ProjectName exec -T server ak shell -c "
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
" 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "FAIL: ak shell error checking scope mapping: $scopeCheck"
    }
    if ($scopeCheck -match 'scope_mapping:NOT_FOUND') {
        throw "FAIL: mqttprobe scope mapping not found"
    }
    if ($scopeCheck -match 'ERROR:') {
        throw "FAIL: scope mapping check error: $scopeCheck"
    }
    Write-Host "  scope_mapping: OK"

    # Verify scope_name is 'profile' (not a custom name that would never be requested)
    if ($scopeCheck -match 'scope_mapping:scope_name=profile') {
        Write-Host "  scope_name=profile: OK"
    } else {
        throw "FAIL: scope mapping scope_name is not 'profile' (claim would never be emitted)"
    }

    # Verify ak_is_group_member uses 'name=' not 'group_name=' (Django ORM field)
    if ($scopeCheck -match 'scope_mapping:expression=.*group_name=') {
        throw "FAIL: scope mapping uses group_name= (invalid Django field); must use name="
    }
    if ($scopeCheck -match 'scope_mapping:expression=.*name=') {
        Write-Host "  ak_is_group_member(name=...): OK"
    }

    # Check group exists
    $groupCheck = docker compose -p $ProjectName exec -T server ak shell -c "
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
" 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "FAIL: ak shell error checking group: $groupCheck"
    }
    if ($groupCheck -match 'group:NOT_FOUND') {
        throw "FAIL: mqttprobe Users group not found"
    }
    if ($groupCheck -match 'ERROR:') {
        throw "FAIL: group check error: $groupCheck"
    }
    Write-Host "  group: OK"

    # Check application exists
    $appCheck = docker compose -p $ProjectName exec -T server ak shell -c "
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
" 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "FAIL: ak shell error checking application: $appCheck"
    }
    if ($appCheck -match 'app:NOT_FOUND') {
        throw "FAIL: mqttprobe application not found"
    }
    if ($appCheck -match 'ERROR:') {
        throw "FAIL: application check error: $appCheck"
    }
    Write-Host "  application: OK"

    Write-Host ""
    Write-Host "PASS: all checks succeeded"
    Write-Host ""
    Write-Host "NOTE: Users are not created by the blueprint. Create admitted-user and denied-user"
    Write-Host "      in the Authentik admin UI as described in README.md."
} finally {
    Write-Host "--- cleanup ---"
    $prevEAP = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    if ($CaCert -and (Test-Path $CaCert)) { Remove-Item $CaCert -ErrorAction SilentlyContinue }
    docker compose -p $ProjectName down -v *> $null
    $ErrorActionPreference = $prevEAP
}
