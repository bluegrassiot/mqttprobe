using System.Globalization;
using MqttProbe.TestInfrastructure.Fixtures;

namespace MqttProbe.BrowserSmokeTests;

[SetUpFixture]
[NonParallelizable]
public sealed class LocalSmokeFixtureLifecycle
{
    [OneTimeSetUp]
    public void BeforeTests()
    {
    }

    [OneTimeTearDown]
    public Task AfterTests() => LocalSmokeFixtureHost.DisposeAsync();
}

[NonParallelizable]
public abstract class LocalSmokeFixtureBase
{
    private static readonly string[] _environmentVariables =
    [
        "MQTTPROBE_TEST_BASE_URL",
        "MQTTPROBE_TEST_MQTT_HOST",
        "MQTTPROBE_TEST_MQTT_PORT",
        "MQTTPROBE_TEST_USERNAME",
        "MQTTPROBE_TEST_PASSWORD",
    ];

    private Dictionary<string, string?>? _originalEnvironment;

    [OneTimeSetUp]
    public async Task StartLocalSmokeServicesAsync()
    {
        _originalEnvironment = _environmentVariables.ToDictionary(
            name => name, Environment.GetEnvironmentVariable, StringComparer.Ordinal);

        var fixture = await LocalSmokeFixtureHost.GetOrStartAsync();
        Environment.SetEnvironmentVariable("MQTTPROBE_TEST_BASE_URL", fixture.BaseUrl);
        Environment.SetEnvironmentVariable("MQTTPROBE_TEST_MQTT_HOST", fixture.BrokerHost);
        Environment.SetEnvironmentVariable("MQTTPROBE_TEST_MQTT_PORT",
            fixture.BrokerPort.ToString(CultureInfo.InvariantCulture));
        Environment.SetEnvironmentVariable("MQTTPROBE_TEST_USERNAME", fixture.Username);
        Environment.SetEnvironmentVariable("MQTTPROBE_TEST_PASSWORD", fixture.Password);
    }

    [OneTimeTearDown]
    public void RestoreEnvironment()
    {
        if (_originalEnvironment is null)
            return;

        foreach (var (name, value) in _originalEnvironment)
            Environment.SetEnvironmentVariable(name, value);
        _originalEnvironment = null;
    }
}

internal static class LocalSmokeFixtureHost
{
    private static readonly object _gate = new();
    private static Task<LocalBrowserSmokeFixture>? _fixtureTask;
    private static LocalBrowserSmokeFixture? _failedBootstrapFixture;

    public static Task<LocalBrowserSmokeFixture> GetOrStartAsync()
    {
        lock (_gate)
            return _fixtureTask ??= StartAsync();
    }

    public static async Task DisposeAsync()
    {
        Task<LocalBrowserSmokeFixture>? fixtureTask;
        LocalBrowserSmokeFixture? failedBootstrapFixture;
        lock (_gate)
        {
            fixtureTask = _fixtureTask;
            failedBootstrapFixture = _failedBootstrapFixture;
        }

        if (failedBootstrapFixture is not null)
        {
            await failedBootstrapFixture.DisposeAsync();
            ClearOwnership(fixtureTask, failedBootstrapFixture);
            return;
        }

        if (fixtureTask is null)
            return;

        LocalBrowserSmokeFixture fixture;
        try
        {
            fixture = await fixtureTask;
        }
        catch
        {
            lock (_gate)
            {
                failedBootstrapFixture = _failedBootstrapFixture;
                if (failedBootstrapFixture is null && ReferenceEquals(_fixtureTask, fixtureTask))
                    _fixtureTask = null;
            }

            if (failedBootstrapFixture is not null)
            {
                await failedBootstrapFixture.DisposeAsync();
                ClearOwnership(fixtureTask, failedBootstrapFixture);
            }
            return;
        }

        await fixture.DisposeAsync();
        ClearOwnership(fixtureTask, fixture);
    }

    private static void ClearOwnership(
        Task<LocalBrowserSmokeFixture>? fixtureTask,
        LocalBrowserSmokeFixture fixture)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_fixtureTask, fixtureTask))
                _fixtureTask = null;
            if (ReferenceEquals(_failedBootstrapFixture, fixture))
                _failedBootstrapFixture = null;
        }
    }

    private static async Task<LocalBrowserSmokeFixture> StartAsync()
    {
        var root = FindRepositoryRoot();
        var output = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var configuration = output.Parent?.Name
            ?? throw new InvalidOperationException("Could not determine browser test build configuration.");
        var targetFramework = output.Name;
        var webAssemblyPath = Path.Combine(root, "src", "MqttProbe.Web", "bin", configuration,
            targetFramework, "MqttProbe.Web.dll");
        try
        {
            return await LocalBrowserSmokeFixture.StartAsync(webAssemblyPath);
        }
        catch (LocalBrowserSmokeFixtureStartException ex)
        {
            lock (_gate)
                _failedBootstrapFixture = ex.Fixture;
            throw;
        }
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "MqttProbe.slnx"))
                && Directory.Exists(Path.Combine(directory.FullName, "src", "MqttProbe.Web")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Could not locate the mqttprobe source checkout.");
    }
}
