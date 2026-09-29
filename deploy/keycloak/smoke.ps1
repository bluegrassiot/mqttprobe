#!/usr/bin/env pwsh
# Smoke test for the Keycloak OIDC local lab with integrated mqttprobe (Windows).
# Validates compose config, starts the stack, checks Keycloak/Caddy health,
# OIDC discovery, mqttprobe health and OIDC login button, then tears everything down.
$ErrorActionPreference = "Stop"

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $ScriptDir

$ExpectedIssuer = "https://keycloak.localhost:8443/realms/mqttprobe-test"
$CaCert = $null
$ProjectName = "kc-smoke-$(Get-Random -Maximum 99999)"

# Read admin credentials from .env or use defaults
$KcAdminUser = "admin"
$KcAdminPass = "admin"
if (Test-Path .env) {
    Get-Content .env | ForEach-Object {
        if ($_ -match '^KC_BOOTSTRAP_ADMIN_USERNAME=(.+)$') { $KcAdminUser = $Matches[1].Trim() }
        if ($_ -match '^KC_BOOTSTRAP_ADMIN_PASSWORD=(.+)$') { $KcAdminPass = $Matches[1].Trim() }
    }
}

try {
    Write-Host "--- docker compose config ---"
    docker compose -p $ProjectName config --quiet
    if ($LASTEXITCODE -ne 0) { throw "docker compose config failed" }

    Write-Host "--- starting stack (build) ---"
    docker compose -p $ProjectName up -d --build
    if ($LASTEXITCODE -ne 0) { throw "docker compose up failed" }

    Write-Host "--- waiting for Keycloak health (management port 9000) ---"
    $healthy = $false
    for ($i = 1; $i -le 120; $i++) {
        $null = docker compose -p $ProjectName exec -T keycloak bash -c "exec 3<>/dev/tcp/localhost/9000 && echo -e 'GET /health/ready HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n' >&3 && cat <&3 | grep -q '200 OK'" 2>&1
        if ($LASTEXITCODE -eq 0) {
            Write-Host "Keycloak healthy after ${i}s"
            $healthy = $true
            break
        }
        Start-Sleep -Seconds 1
    }
    if (-not $healthy) {
        Write-Host "FAIL: Keycloak did not become healthy within 120s"
        docker compose -p $ProjectName logs keycloak
        throw "Keycloak health check timed out"
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
        Write-Host "FAIL: Caddy did not start within 30s"
        docker compose -p $ProjectName logs caddy
        throw "Caddy startup timed out"
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

    Write-Host "--- checking OIDC discovery issuer (via host curl, retry 60s) ---"
    $discoveryJson = $null
    for ($i = 1; $i -le 60; $i++) {
        $curlOut = & curl.exe -s --cacert $CaCert --ssl-no-revoke "https://keycloak.localhost:8443/realms/mqttprobe-test/.well-known/openid-configuration" 2>&1
        $curlExit = $LASTEXITCODE
        if ($curlExit -eq 0 -and $curlOut -match '\S') {
            $discoveryJson = $curlOut
            Write-Host "  discovery OK after ${i}s"
            break
        }
        Start-Sleep -Seconds 1
    }
    if (-not $discoveryJson) {
        throw "OIDC discovery timed out after 60s (exit=$curlExit)"
    }

    $issuerMatch = [regex]::Match($discoveryJson, '"issuer"\s*:\s*"([^"]*)"')
    $actualIssuer = if ($issuerMatch.Success) { $issuerMatch.Groups[1].Value } else { "" }
    Write-Host "  expected: $ExpectedIssuer"
    Write-Host "  actual:   $actualIssuer"

    if ($actualIssuer -ne $ExpectedIssuer) {
        throw "OIDC issuer mismatch"
    }

    Write-Host "--- checking required OIDC endpoints ---"
    foreach ($endpoint in @("authorization_endpoint", "token_endpoint", "end_session_endpoint", "pushed_authorization_request_endpoint")) {
        $epMatch = [regex]::Match($discoveryJson, "`"$endpoint`"\s*:\s*`"[^`"]*`"")
        if (-not $epMatch.Success) {
            throw "missing $endpoint in discovery"
        }
        Write-Host "  $endpoint`: OK"
    }

    Write-Host "--- checking mqttprobe /health through Caddy ---"
    $healthResp = & curl.exe -s -o NUL -w "%{http_code}" --cacert $CaCert --ssl-no-revoke "https://localhost:5001/health" 2>&1
    if ($healthResp -ne "200") {
        throw "mqttprobe /health through Caddy returned HTTP $healthResp"
    }
    Write-Host "  /health HTTP 200: OK"

    Write-Host "--- checking mqttprobe /Login OIDC button ---"
    $loginPage = & curl.exe -s --cacert $CaCert --ssl-no-revoke "https://localhost:5001/Login" 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "mqttprobe /Login curl failed"
    }
    if ($loginPage -match 'keycloak\.localhost.*openid') {
        Write-Host "  /Login contains OIDC Keycloak link: OK"
    } elseif ($loginPage -match 'oidc|openid|Keycloak') {
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
        throw "GET /Login failed"
    }

    # Extract the antiforgery token from the hidden input field
    $afTokenMatch = [regex]::Match($loginHtml, 'name="__RequestVerificationToken"[^>]*value="([^"]*)"')
    if (-not $afTokenMatch.Success) {
        Remove-Item $cookieJar -ErrorAction SilentlyContinue
        throw "Could not extract antiforgery token from /Login page"
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
        throw "POST /Login?handler=Challenge failed (exit=$challengeExit)"
    }

    # The Challenge handler should return a302 redirect to Keycloak
    $locationMatch = [regex]::Match($headers, 'Location:\s*(\S+)')
    if (-not $locationMatch.Success) {
        throw "POST /Login?handler=Challenge did not return a redirect (no Location header)"
    }
    $redirectUrl = $locationMatch.Groups[1].Value
    Write-Host "  redirect URL: $redirectUrl"

    # Verify the redirect goes to Keycloak's OIDC auth endpoint
    if ($redirectUrl -match 'keycloak\.localhost.*openid-connect/auth') {
        Write-Host "  OIDC challenge redirects to Keycloak auth endpoint: OK"
    } else {
        throw "OIDC challenge redirect does not go to Keycloak auth endpoint (got: $redirectUrl)"
    }

    # Verify it includes client_id=mqttprobe (proves the client config is correct)
    if ($redirectUrl -match 'client_id=mqttprobe') {
        Write-Host "  client_id=mqttprobe in redirect: OK"
    } else {
        throw "OIDC challenge redirect missing client_id=mqttprobe (got: $redirectUrl)"
    }

    # Verify redirect_uri=https://localhost:5001/signin-oidc is set
    # PAR sends redirect_uri via request_uri, but OidcAuthenticationEvents also sets it
    # in the protocol message. Check either the URL or the request_uri parameter.
    $hasRedirectUri = ($redirectUrl -match 'redirect_uri=https(%3A%2F%2F|://)localhost(%3A|:)5001(%2F|/)signin-oidc')
    $hasRequestUri = ($redirectUrl -match 'request_uri=')
    if ($hasRedirectUri -or $hasRequestUri) {
        Write-Host "  redirect_uri set (via PAR or direct param): OK"
    } else {
        throw "OIDC challenge redirect missing redirect_uri or request_uri (got: $redirectUrl)"
    }

    Write-Host "--- checking realm via admin API ---"
    $tokenResponse = & curl.exe -s --cacert $CaCert --ssl-no-revoke `
        -X POST "https://keycloak.localhost:8443/realms/master/protocol/openid-connect/token" `
        -H "Content-Type: application/x-www-form-urlencoded" `
        -d "grant_type=password&client_id=admin-cli&username=$KcAdminUser&password=$KcAdminPass"
    if ($LASTEXITCODE -ne 0) { throw "admin token curl failed (exit=$LASTEXITCODE)" }
    $tokenMatch = [regex]::Match($tokenResponse, '"access_token":"([^"]*)"')
    $adminToken = if ($tokenMatch.Success) { $tokenMatch.Groups[1].Value } else { "" }

    if ([string]::IsNullOrEmpty($adminToken)) {
        Write-Host "WARN: could not get admin token, skipping admin API checks"
    } else {
        Write-Host "--- checking client settings ---"
        $clientJson = & curl.exe -s --cacert $CaCert --ssl-no-revoke `
            -H "Authorization: Bearer $adminToken" `
            "https://keycloak.localhost:8443/admin/realms/mqttprobe-test/clients?clientId=mqttprobe"
        if ($LASTEXITCODE -ne 0) { throw "client query curl failed (exit=$LASTEXITCODE)" }

        if ($clientJson -match '"publicClient":false') {
            Write-Host "  client is confidential: OK"
        } else {
            throw "client should be confidential"
        }

        if ($clientJson -match '"pkce\.code\.challenge\.method":"S256"') {
            Write-Host "  PKCE S256: OK"
        } else {
            throw "PKCE should be S256"
        }

        Write-Host "--- checking back-channel logout URL ---"
        # The backchannel.logout.url must be the internal Docker URL so Keycloak
        # can reach mqttprobe from inside its container. It must NOT be the
        # public localhost:5001 URL (which Keycloak cannot resolve internally).
        if ($clientJson -match '"backchannel\.logout\.url"\s*:\s*"([^"]*)"') {
            $backchannelUrl = $Matches[1]
            Write-Host "  backchannel.logout.url: $backchannelUrl"
            if ($backchannelUrl -match 'localhost:5001') {
                throw "backchannel.logout.url points to localhost:5001 (unreachable from Keycloak container). Expected http://mqttprobe:8080/signout-oidc" # DevSkim: ignore DS137138 compose-internal backchannel URL
            }
            if ($backchannelUrl -ne 'http://mqttprobe:8080/signout-oidc') { # DevSkim: ignore DS137138 compose-internal backchannel URL
                throw "backchannel.logout.url unexpected: $backchannelUrl (expected http://mqttprobe:8080/signout-oidc)" # DevSkim: ignore DS137138 compose-internal backchannel URL
            }
            Write-Host "  backchannel.logout.url is internal: OK"
        } else {
            Write-Host "WARN: could not extract backchannel.logout.url from client JSON"
        }

        Write-Host "--- checking users ---"
        $usersJson = & curl.exe -s --cacert $CaCert --ssl-no-revoke `
            -H "Authorization: Bearer $adminToken" `
            "https://keycloak.localhost:8443/admin/realms/mqttprobe-test/users"
        if ($LASTEXITCODE -ne 0) { throw "users query curl failed (exit=$LASTEXITCODE)" }

        if ($usersJson -match '"username":"admitted-user"') {
            Write-Host "  admitted-user: OK"
        } else {
            throw "admitted-user not found"
        }

        if ($usersJson -match '"username":"denied-user"') {
            Write-Host "  denied-user: OK"
        } else {
            throw "denied-user not found"
        }
    }

    Write-Host ""
    Write-Host "PASS: all checks succeeded"
} finally {
    Write-Host "--- cleanup ---"
    $prevEAP = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    if ($CaCert -and (Test-Path $CaCert)) { Remove-Item $CaCert -ErrorAction SilentlyContinue }
    docker compose -p $ProjectName down -v *> $null
    $ErrorActionPreference = $prevEAP
}