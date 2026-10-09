using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Playwright;

namespace MqttProbe.TestInfrastructure.Fixtures;

public sealed partial class LocalBrowserSmokeFixture : IAsyncDisposable
{
    private const string MosquittoImage =
        "eclipse-mosquitto@sha256:94f5a3d7deafa59fa3440d227ddad558f59d293c612138de841eec61bfa4d353";
    private static readonly TimeSpan _startupTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan _defaultCleanupTimeout = TimeSpan.FromSeconds(10);
    private const int ActionTimeoutMs = 15_000;

    private readonly string _temporaryRoot;
    private readonly Func<Task<IPlaywright>> _createPlaywright;
    private readonly Func<CancellationToken, Task> _verifySetupListener;
    private readonly Func<Task<string?>> _cleanupWeb;
    private readonly TimeSpan _cleanupTimeout;
    private readonly TimeProvider _timeProvider;
    private IContainer? _broker;
    private Task? _brokerDisposalTask;
    private Process? _webProcess;
    private Task? _setupTask;
    private Task<bool>? _setupEvidenceTask;
    private Task? _browserCleanupTask;
    private Task? _browserCloseTask;
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IPage? _page;
    private string _setupStage = "listener ownership verification";
    private string _startupStage = "broker startup";

    internal LocalBrowserSmokeFixture(
        string temporaryRoot,
        IContainer? broker,
        Func<Task<IPlaywright>> createPlaywright,
        Func<CancellationToken, Task> verifySetupListener,
        TimeSpan cleanupTimeout,
        TimeProvider timeProvider,
        Func<Task<string?>>? cleanupWeb = null)
    {
        _temporaryRoot = temporaryRoot;
        _broker = broker;
        _createPlaywright = createPlaywright;
        _verifySetupListener = verifySetupListener;
        _cleanupTimeout = cleanupTimeout;
        _timeProvider = timeProvider;
        _cleanupWeb = cleanupWeb ?? CleanupWebAsync;
    }

    public string BaseUrl { get; private set; } = string.Empty;
    public string BrokerHost { get; private set; } = string.Empty;
    public int BrokerPort { get; private set; }
    public string Username { get; private set; } = string.Empty;
    public string Password { get; private set; } = string.Empty;

    public static async Task<LocalBrowserSmokeFixture> StartAsync(string webAssemblyPath)
    {
        var temporaryRoot = CreateTemporaryRoot();
        LocalBrowserSmokeFixture fixture = null!;
        fixture = new LocalBrowserSmokeFixture(
            temporaryRoot,
            broker: null,
            () => Playwright.CreateAsync(),
            cancellationToken => fixture.VerifyListenerOwnershipAsync(new Uri(fixture.BaseUrl).Port, cancellationToken),
            _defaultCleanupTimeout,
            TimeProvider.System);
        using var startupTimeout = new CancellationTokenSource(_startupTimeout);
        try
        {
            fixture._startupStage = "broker startup";
            await fixture.StartBrokerAsync(startupTimeout.Token);
            startupTimeout.Token.ThrowIfCancellationRequested();
            fixture._startupStage = "Web startup";
            await fixture.StartWebAsync(webAssemblyPath, startupTimeout.Token);
            startupTimeout.Token.ThrowIfCancellationRequested();
            fixture._startupStage = "visible account setup";
            await fixture.ProvisionLocalAccountAsync(startupTimeout.Token);
            startupTimeout.Token.ThrowIfCancellationRequested();
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
                    startupFailure, fixture.GetStartupFailureStageSummary(), cleanupFailure);
            }

            throw;
        }
    }

    private string GetStartupFailureStageSummary() => _startupStage switch
    {
        "visible account setup" => _setupStage,
        "Web startup" => "Web startup",
        _ => "broker startup",
    };

    public async ValueTask DisposeAsync()
    {
        Exception? cleanupFailure = null;
        string? listenerValidationError = null;
        try
        {
            if (_setupTask is not null || _browserCleanupTask is not null
                || _browser is not null || _playwright is not null)
                await WaitForBrowserCleanupAsync();
        }
        catch (Exception ex)
        {
            cleanupFailure ??= ex;
        }

        try
        {
            listenerValidationError = await _cleanupWeb();
        }
        catch (Exception ex)
        {
            cleanupFailure ??= ex;
        }

        if (_broker is not null)
        {
            if (_brokerDisposalTask is { IsCompleted: true } previousTask
                && (previousTask.IsFaulted || previousTask.IsCanceled))
            {
                ObserveFault(previousTask);
                _brokerDisposalTask = null;
            }
            var disposalTask = _brokerDisposalTask ??= _broker.DisposeAsync().AsTask();
            ObserveFault(disposalTask);
            try
            {
                await disposalTask.WaitAsync(_cleanupTimeout, _timeProvider);
                _broker = null;
                _brokerDisposalTask = null;
            }
            catch (Exception ex)
            {
                if (disposalTask.IsFaulted)
                    _ = disposalTask.Exception;
                cleanupFailure ??= ex;
            }
        }
        if (AllOwnedResourcesReleased())
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

        if (!AllOwnedResourcesReleased())
            cleanupFailure ??= new InvalidOperationException("Fixture cleanup still owns resources.");

        if (cleanupFailure is not null || listenerValidationError is not null)
        {
            var retry = _webProcess is null ? string.Empty : " The Web process remains active; retry DisposeAsync after it exits.";
            var validation = listenerValidationError is null ? string.Empty : $" {listenerValidationError}";
            throw new InvalidOperationException(
                $"Local smoke fixture cleanup failed ({cleanupFailure?.GetType().Name ?? "listener validation"}).{validation}{retry}");
        }
    }

    private async Task<string?> CleanupWebAsync()
    {
        if (_webProcess is not { } webProcess)
            return null;

        Exception? cleanupFailure = null;
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
            throw new InvalidOperationException(
                $"Local smoke Web cleanup failed ({cleanupFailure?.GetType().Name ?? "TimeoutException"}).");

        string? warning = null;
        try
        {
            var owners = await GetListenerProcessIdsAsync(new Uri(BaseUrl).Port, CancellationToken.None);
            if (owners.Count > 0)
                warning = "The Web listener remained in use after the owned process exited.";
        }
        catch (Exception ex)
        {
            warning = $"Web listener cleanup verification failed ({ex.GetType().Name}).";
        }

        webProcess.Dispose();
        _webProcess = null;
        return warning;
    }

    private async Task StartBrokerAsync(CancellationToken cancellationToken)
    {
        var config = """
            listener 1883
            protocol mqtt
            allow_anonymous true
            persistence false
            """;

        _broker ??= new ContainerBuilder(MosquittoImage)
            .WithPortBinding(1883, true)
            .WithResourceMapping(Encoding.UTF8.GetBytes(config), "/mosquitto/config/mosquitto.conf")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("mosquitto.*running"))
            .Build();

        cancellationToken.ThrowIfCancellationRequested();
        await _broker.StartAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        BrokerHost = _broker.Hostname;
        BrokerPort = _broker.GetMappedPublicPort(1883);
    }

    private async Task StartWebAsync(string webAssemblyPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(webAssemblyPath))
            throw new InvalidOperationException("Local smoke Web assembly was not found.");

        Directory.CreateDirectory(Path.Combine(_temporaryRoot, "config"));
        cancellationToken.ThrowIfCancellationRequested();
        await File.WriteAllTextAsync(Path.Combine(_temporaryRoot, "appsettings.json"),
            "{\"Authentication\":{\"Mode\":\"Local\"},\"AllowedHosts\":\"*\"}", cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
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

        cancellationToken.ThrowIfCancellationRequested();
        _webProcess = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Local smoke Web process could not be started.");
        cancellationToken.ThrowIfCancellationRequested();
        _ = _webProcess.StandardOutput.ReadToEndAsync();
        _ = _webProcess.StandardError.ReadToEndAsync();

        await VerifyListenerOwnershipAsync(port, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(2) };
        while (!cancellationToken.IsCancellationRequested)
        {
            if (_webProcess.HasExited)
                throw new InvalidOperationException("Local smoke Web process exited before becoming ready.");

            try
            {
                using var response = await client.GetAsync($"{BaseUrl}/Setup", cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    var body = await response.Content.ReadAsStringAsync(cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (body.Contains("Create your login credentials", StringComparison.Ordinal))
                    {
                        await VerifyListenerOwnershipAsync(port, cancellationToken);
                        cancellationToken.ThrowIfCancellationRequested();
                        return;
                    }
                }
            }
            catch (HttpRequestException)
            {
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }

            await Task.Delay(250, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
        }

        cancellationToken.ThrowIfCancellationRequested();
        throw new InvalidOperationException("Local smoke Web readiness timed out.");
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
        string startupStageSummary,
        Exception cleanupFailure)
        : base($"Local smoke fixture startup failed during {startupStageSummary} "
            + $"({startupFailure.GetType().Name}) "
            + $"and cleanup failed ({cleanupFailure.GetType().Name}).")
    {
        Fixture = fixture;
    }

    public LocalBrowserSmokeFixture Fixture { get; }
}
