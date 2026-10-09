using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Playwright;

namespace MqttProbe.TestInfrastructure.Fixtures;

public sealed class LocalBrowserSmokeFixture : IAsyncDisposable
{
    private const string MosquittoImage =
        "eclipse-mosquitto@sha256:94f5a3d7deafa59fa3440d227ddad558f59d293c612138de841eec61bfa4d353";
    private const int StartupTimeoutSeconds = 60;
    private const int ActionTimeoutMs = 15_000;

    private readonly string _temporaryRoot;
    private IContainer? _broker;
    private Process? _webProcess;

    private LocalBrowserSmokeFixture(string temporaryRoot) => _temporaryRoot = temporaryRoot;

    public string BaseUrl { get; private set; } = string.Empty;
    public string BrokerHost { get; private set; } = string.Empty;
    public int BrokerPort { get; private set; }
    public string Username { get; private set; } = string.Empty;
    public string Password { get; private set; } = string.Empty;

    public static async Task<LocalBrowserSmokeFixture> StartAsync(string webAssemblyPath)
    {
        var temporaryRoot = CreateTemporaryRoot();
        var fixture = new LocalBrowserSmokeFixture(temporaryRoot);
        try
        {
            await fixture.StartBrokerAsync();
            await fixture.StartWebAsync(webAssemblyPath);
            await fixture.ProvisionLocalAccountAsync();
            return fixture;
        }
        catch (Exception startupFailure)
        {
            try
            {
                await fixture.DisposeAsync();
            }
            catch (Exception cleanupFailure)
            {
                throw new LocalBrowserSmokeFixtureStartException(fixture,
                    startupFailure, cleanupFailure);
            }

            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        Exception? cleanupFailure = null;
        string? listenerValidationError = null;
        if (_webProcess is { } webProcess)
        {
            for (var attempt = 0; attempt < 2 && !webProcess.HasExited; attempt++)
            {
                try
                {
                    webProcess.Kill(entireProcessTree: false);
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    await webProcess.WaitForExitAsync(timeout.Token);
                }
                catch (Exception ex)
                {
                    if (attempt == 1)
                        cleanupFailure = ex;
                }
            }

            if (!webProcess.HasExited)
                cleanupFailure ??= new TimeoutException();
            else
            {
                try
                {
                    var owners = await GetListenerProcessIdsAsync(new Uri(BaseUrl).Port, CancellationToken.None);
                    if (owners.Count > 0)
                        listenerValidationError = "The Web listener remained in use after the owned process exited.";
                }
                catch (Exception ex)
                {
                    listenerValidationError = $"Web listener cleanup verification failed ({ex.GetType().Name}).";
                }

                webProcess.Dispose();
                _webProcess = null;
            }
        }

        try
        {
            if (_broker is not null)
            {
                await _broker.DisposeAsync();
                _broker = null;
            }
        }
        catch (Exception ex)
        {
            cleanupFailure ??= ex;
        }
        if (_webProcess is null)
        {
            try
            {
                if (Directory.Exists(_temporaryRoot))
                    Directory.Delete(_temporaryRoot, recursive: true);
            }
            catch (Exception ex)
            {
                cleanupFailure ??= ex;
            }
        }

        if (cleanupFailure is not null || listenerValidationError is not null)
        {
            var retry = _webProcess is null ? string.Empty : " The Web process remains active; retry DisposeAsync after it exits.";
            var validation = listenerValidationError is null ? string.Empty : $" {listenerValidationError}";
            throw new InvalidOperationException(
                $"Local smoke fixture cleanup failed ({cleanupFailure?.GetType().Name ?? "listener validation"}).{validation}{retry}");
        }
    }

    private async Task StartBrokerAsync()
    {
        var config = """
            listener 1883
            protocol mqtt
            allow_anonymous true
            persistence false
            """;

        _broker = new ContainerBuilder(MosquittoImage)
            .WithPortBinding(1883, true)
            .WithResourceMapping(Encoding.UTF8.GetBytes(config), "/mosquitto/config/mosquitto.conf")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("mosquitto.*running"))
            .Build();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(StartupTimeoutSeconds));
        await _broker.StartAsync(timeout.Token);
        BrokerHost = _broker.Hostname;
        BrokerPort = _broker.GetMappedPublicPort(1883);
    }

    private async Task StartWebAsync(string webAssemblyPath)
    {
        if (!File.Exists(webAssemblyPath))
            throw new InvalidOperationException("Local smoke Web assembly was not found.");

        Directory.CreateDirectory(Path.Combine(_temporaryRoot, "config"));
        await File.WriteAllTextAsync(Path.Combine(_temporaryRoot, "appsettings.json"),
            "{\"Authentication\":{\"Mode\":\"Local\"},\"AllowedHosts\":\"*\"}");

        var port = ReserveLoopbackPort();
        BaseUrl = $"https://localhost:{port}";

        var startInfo = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = _temporaryRoot,
        };
        startInfo.ArgumentList.Add(webAssemblyPath);
        startInfo.ArgumentList.Add("--urls");
        startInfo.ArgumentList.Add(BaseUrl);
        startInfo.ArgumentList.Add("--contentRoot");
        startInfo.ArgumentList.Add(_temporaryRoot);
        startInfo.ArgumentList.Add("--environment");
        startInfo.ArgumentList.Add("Development");
        ClearInheritedEndpointOverrides(startInfo.Environment);
        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        startInfo.Environment["DOTNET_ENVIRONMENT"] = "Development";
        startInfo.Environment["Authentication__Mode"] = "Local";

        _webProcess = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Local smoke Web process could not be started.");
        _ = _webProcess.StandardOutput.ReadToEndAsync();
        _ = _webProcess.StandardError.ReadToEndAsync();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(StartupTimeoutSeconds));
        await VerifyListenerOwnershipAsync(port, timeout.Token);

        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(2) };
        while (!timeout.IsCancellationRequested)
        {
            if (_webProcess.HasExited)
                throw new InvalidOperationException("Local smoke Web process exited before becoming ready.");

            try
            {
                using var response = await client.GetAsync($"{BaseUrl}/Setup", timeout.Token);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    var body = await response.Content.ReadAsStringAsync(timeout.Token);
                    if (body.Contains("Create your login credentials", StringComparison.Ordinal))
                    {
                        await VerifyListenerOwnershipAsync(port, timeout.Token);
                        return;
                    }
                }
            }
            catch (HttpRequestException)
            {
            }
            catch (TaskCanceledException) when (!timeout.IsCancellationRequested)
            {
            }

            await Task.Delay(250, timeout.Token);
        }

        throw new InvalidOperationException("Local smoke Web readiness timed out.");
    }

    private async Task ProvisionLocalAccountAsync()
    {
        Username = $"smoke-{Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant()}";
        Password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));

        IPlaywright? playwright = null;
        IBrowser? browser = null;
        IPage? page = null;
        var stage = "listener ownership verification";
        InvalidOperationException? setupFailure = null;
        Exception? closeFailure = null;
        try
        {
            using (var ownershipTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                await VerifyListenerOwnershipAsync(new Uri(BaseUrl).Port, ownershipTimeout.Token);

            stage = "Playwright initialization";
            playwright = await Playwright.CreateAsync();
            stage = "browser launch";
            browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = false,
            });
            stage = "browser context creation";
            var context = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                IgnoreHTTPSErrors = true,
                ViewportSize = new ViewportSize { Width = 1280, Height = 900 },
            });
            stage = "page creation";
            page = await context.NewPageAsync();
            page.SetDefaultTimeout(ActionTimeoutMs);
            page.SetDefaultNavigationTimeout(40_000);

            stage = "Setup navigation";
            await page.GotoAsync($"{BaseUrl}/Setup", new PageGotoOptions
            {
                Timeout = 40_000,
                WaitUntil = WaitUntilState.DOMContentLoaded,
            });
            stage = "username input";
            await page.Locator("#username").FillAsync(Username);
            stage = "password input";
            await page.Locator("#password").FillAsync(Password);
            stage = "password confirmation input";
            await page.Locator("#confirmPassword").FillAsync(Password);
            stage = "Setup submit";
            await page.Locator("form button[type='submit']").ClickAsync();
            stage = "connection dialog wait";
            await page.Locator(".connection-dialog-content").WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = 40_000,
            });
        }
        catch (Exception ex)
        {
            var evidenceWritten = await TryWriteSetupFailureSnapshotAsync(page, stage);
            var evidenceStatus = evidenceWritten ? string.Empty : " Failure evidence unavailable.";
            setupFailure = new InvalidOperationException(
                $"Visible local account setup failed during {stage} ({ex.GetType().Name}).{evidenceStatus}");
        }
        finally
        {
            if (browser is not null)
            {
                try
                {
                    await browser.CloseAsync().WaitAsync(TimeSpan.FromSeconds(10));
                }
                catch (Exception ex)
                {
                    closeFailure = ex;
                }
            }

            try
            {
                playwright?.Dispose();
            }
            catch (Exception ex)
            {
                closeFailure ??= ex;
            }
        }

        if (setupFailure is not null)
        {
            var closeStatus = closeFailure is null
                ? string.Empty
                : $" Browser close also failed ({closeFailure.GetType().Name}).";
            throw new InvalidOperationException(setupFailure.Message + closeStatus, setupFailure);
        }

        if (closeFailure is TimeoutException)
            throw new InvalidOperationException("Visible local account setup browser did not close in time.");
        if (closeFailure is not null)
            throw new InvalidOperationException(
                $"Visible local account setup browser close failed ({closeFailure.GetType().Name}).");
    }

    private async Task<bool> TryWriteSetupFailureSnapshotAsync(IPage? page, string stage)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            var route = "other";
            if (page is not null && Uri.TryCreate(page.Url, UriKind.Absolute, out var uri))
            {
                route = uri.AbsolutePath switch
                {
                    "/Setup" => "Setup",
                    "/Login" => "Login",
                    _ => "other",
                };
            }

            var snapshot = page is null
                ? new { Stage = stage, Route = route, PageAvailable = false, Controls = (object?)null }
                : new
                {
                    Stage = stage,
                    Route = route,
                    PageAvailable = true,
                    Controls = (object?)new
                    {
                        Username = await GetSelectorCountsAsync(page, "#username", timeout.Token),
                        Password = await GetSelectorCountsAsync(page, "#password", timeout.Token),
                        ConfirmPassword = await GetSelectorCountsAsync(page, "#confirmPassword", timeout.Token),
                        Submit = await GetSelectorCountsAsync(page, "form button[type='submit']", timeout.Token),
                        ValidationSummary = await GetSelectorCountsAsync(page, ".validation-summary-errors", timeout.Token),
                        ValidationMessage = await GetSelectorCountsAsync(page, ".validation-message", timeout.Token),
                        ConnectionDialog = await GetSelectorCountsAsync(page, ".connection-dialog-content", timeout.Token),
                    },
                };

            var evidenceRoot = Path.GetDirectoryName(_temporaryRoot)
                ?? throw new InvalidOperationException("Fixture temporary directory has no parent.");
            var path = Path.Combine(evidenceRoot, $"mqttprobe-browser-smoke-{Guid.NewGuid():N}.json");
            await File.WriteAllTextAsync(path, System.Text.Json.JsonSerializer.Serialize(snapshot), timeout.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<(int Count, int VisibleCount)> GetSelectorCountsAsync(
        IPage page, string selector, CancellationToken cancellationToken)
    {
        var locator = page.Locator(selector);
        var count = await locator.CountAsync().WaitAsync(cancellationToken);
        var visibleCount = 0;
        for (var index = 0; index < count; index++)
        {
            if (await locator.Nth(index).IsVisibleAsync().WaitAsync(cancellationToken))
                visibleCount++;
        }

        return (count, visibleCount);
    }

    private async Task VerifyListenerOwnershipAsync(int port, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Local smoke listener ownership is verified with Windows netstat.");

        var webProcess = _webProcess
            ?? throw new InvalidOperationException("Local smoke Web process is not running.");
        while (!cancellationToken.IsCancellationRequested)
        {
            if (webProcess.HasExited)
                throw new InvalidOperationException("Local smoke Web process exited before binding its port.");

            var owners = await GetListenerProcessIdsAsync(port, cancellationToken);
            if (owners.Any(owner => owner != webProcess.Id))
                throw new InvalidOperationException("Local smoke Web port is already owned by another process.");
            if (owners.Contains(webProcess.Id))
                return;

            await Task.Delay(150, cancellationToken);
        }

        throw new TimeoutException("Local smoke Web did not bind its port before readiness timed out.");
    }

    private static async Task<HashSet<int>> GetListenerProcessIdsAsync(
        int port, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("netstat.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("-ano");
        startInfo.ArgumentList.Add("-p");
        startInfo.ArgumentList.Add("tcp");

        using var netstat = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start listener ownership check.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var outputTask = netstat.StandardOutput.ReadToEndAsync(timeout.Token);
        _ = netstat.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await netstat.WaitForExitAsync(timeout.Token);
            var output = await outputTask;
            if (netstat.ExitCode != 0)
                throw new InvalidOperationException("Listener ownership check failed.");

            var owners = new HashSet<int>();
            foreach (var line in output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries))
            {
                var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length < 5
                    || !fields[0].Equals("TCP", StringComparison.OrdinalIgnoreCase)
                    || !fields[^2].Equals("LISTENING", StringComparison.OrdinalIgnoreCase)
                    || !TryGetEndpointPort(fields[1], out var localPort)
                    || localPort != port
                    || !int.TryParse(fields[^1], NumberStyles.None, CultureInfo.InvariantCulture, out var processId))
                {
                    continue;
                }

                owners.Add(processId);
            }

            return owners;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            if (!netstat.HasExited)
            {
                netstat.Kill(entireProcessTree: false);
                using var killTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await netstat.WaitForExitAsync(killTimeout.Token);
            }

            throw new TimeoutException("Listener ownership check exceeded its time limit.");
        }
    }

    private static bool TryGetEndpointPort(string endpoint, out int port)
    {
        port = 0;
        var separator = endpoint.LastIndexOf(':');
        return separator >= 0
            && int.TryParse(endpoint.AsSpan(separator + 1), NumberStyles.None, CultureInfo.InvariantCulture, out port);
    }

    private static void ClearInheritedEndpointOverrides(IDictionary<string, string?> environment)
    {
        var endpointKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ASPNETCORE_URLS", "ASPNETCORE_HTTP_PORTS", "ASPNETCORE_HTTPS_PORTS",
            "ASPNETCORE_CONTENTROOT", "DOTNET_URLS", "URLS", "HTTP_PORTS",
            "HTTPS_PORTS", "CONTENTROOT", "Kestrel__Endpoints", "DOTNET_Kestrel__Endpoints",
            "ASPNETCORE_Kestrel__Endpoints",
        };

        foreach (var name in environment.Keys.ToArray())
        {
            if (endpointKeys.Contains(name)
                || name.StartsWith("Kestrel__Endpoints__", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("DOTNET_Kestrel__Endpoints__", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("ASPNETCORE_Kestrel__Endpoints__", StringComparison.OrdinalIgnoreCase))
            {
                environment.Remove(name);
            }
        }
    }

    private static int ReserveLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static string CreateTemporaryRoot()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var tempBase = string.IsNullOrWhiteSpace(localAppData)
            ? Path.GetTempPath()
            : Path.Combine(localAppData, "Temp", "opencode");
        Directory.CreateDirectory(tempBase);
        var path = Path.Combine(tempBase, $"mqttprobe-browser-smoke-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}

public sealed class LocalBrowserSmokeFixtureStartException : Exception
{
    public LocalBrowserSmokeFixtureStartException(
        LocalBrowserSmokeFixture fixture,
        Exception startupFailure,
        Exception cleanupFailure)
        : base($"Local smoke fixture startup failed ({startupFailure.GetType().Name}) "
            + $"and cleanup failed ({cleanupFailure.GetType().Name}).")
    {
        Fixture = fixture;
    }

    public LocalBrowserSmokeFixture Fixture { get; }
}
