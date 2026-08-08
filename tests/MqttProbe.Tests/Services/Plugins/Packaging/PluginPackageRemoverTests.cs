using Microsoft.Extensions.Logging;
using MqttProbe.Models.Plugins;
using MqttProbe.Services.Plugins.Packaging;
using MqttProbe.Services.Plugins.Protobuf;

namespace MqttProbe.Shared.Tests.Services.Plugins.Packaging;

[TestFixture]
public class PluginPackageRemoverTests
{
    private string _pluginFolder = string.Empty;
    private readonly List<string> _extraFolders = [];

    [SetUp]
    public void SetUp()
    {
        _pluginFolder = Path.Combine(Path.GetTempPath(), "mqttprobe-remover-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_pluginFolder);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_pluginFolder))
        {
            Directory.Delete(_pluginFolder, recursive: true);
        }

        foreach (var folder in _extraFolders)
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }

        _extraFolders.Clear();
    }

    [Test]
    public async Task IdOnly_Removes_A_Schema_Package_Immediately()
    {
        var session = new PluginInstallSession();
        var remover = CreateRemover(session);
        var installPath = CreateSchema("demo");

        var outcome = await remover.RemoveAsync("demo", CancellationToken.None);

        outcome.Succeeded.Should().BeTrue(outcome.Error);
        outcome.InstallPath.Should().Be(installPath);
        outcome.RequiresRestart.Should().BeFalse();
        Directory.Exists(installPath).Should().BeFalse();
        session.TryGet("demo", out var requiresRestart).Should().BeTrue();
        requiresRestart.Should().BeFalse();
    }

    [Test]
    public async Task IdOnly_Marks_An_Assembly_For_Pending_Removal()
    {
        var session = new PluginInstallSession();
        var remover = CreateRemover(session);
        var installPath = CreateAssembly("demoplugin");

        var outcome = await remover.RemoveAsync("demoplugin", CancellationToken.None);

        outcome.Succeeded.Should().BeTrue(outcome.Error);
        outcome.InstallPath.Should().Be(installPath);
        outcome.RequiresRestart.Should().BeTrue();
        Directory.Exists(installPath).Should().BeTrue();
        PluginPendingOperations.HasPendingOperation(_pluginFolder, "demoplugin").Should().BeTrue();
        session.TryGet("demoplugin", out var requiresRestart).Should().BeTrue();
        requiresRestart.Should().BeTrue();
    }

    [Test]
    public async Task IdOnly_Uses_Only_The_First_Writable_Folder_And_Does_Not_Reprobe_It()
    {
        var firstFolder = CreateSiblingFolder("first");
        var config = new PluginConfig();
        config.PluginFolders.Add(firstFolder);
        config.PluginFolders.Add(_pluginFolder);
        var probedFolders = new List<string>();
        var remover = new PluginPackageRemover(
            config,
            new PluginInstallSession(),
            null!,
            folder =>
            {
                probedFolders.Add(folder);
                return true;
            });
        var firstInstallPath = CreateAssembly("demoplugin", firstFolder);
        var laterInstallPath = CreateAssembly("demoplugin");

        var outcome = await remover.RemoveAsync("demoplugin", CancellationToken.None);

        outcome.Succeeded.Should().BeTrue(outcome.Error);
        probedFolders.Should().ContainSingle().Which.Should().Be(firstFolder);
        PluginPendingOperations.HasPendingOperation(firstFolder, "demoplugin").Should().BeTrue();
        PluginPendingOperations.HasPendingOperation(_pluginFolder, "demoplugin").Should().BeFalse();
        Directory.Exists(firstInstallPath).Should().BeTrue();
        Directory.Exists(laterInstallPath).Should().BeTrue();
    }

    [Test]
    public async Task IdOnly_Returns_A_Missing_Package_Failure()
    {
        var outcome = await CreateRemover().RemoveAsync("missing", CancellationToken.None);

        outcome.Succeeded.Should().BeFalse();
        outcome.Error.Should().Contain("missing");
    }

    [Test]
    public async Task IdOnly_Returns_A_No_Writable_Folder_Failure()
    {
        var config = new PluginConfig();
        config.PluginFolders.Add(_pluginFolder);
        var remover = new PluginPackageRemover(config, new PluginInstallSession(), null!, _ => false);

        var outcome = await remover.RemoveAsync("demo", CancellationToken.None);

        outcome.Succeeded.Should().BeFalse();
        outcome.Error.Should().Be("No writable plugin folder is configured on this host.");
    }

    [Test]
    public async Task IdOnly_Returns_A_Failure_When_The_Pending_Area_Cannot_Be_Written()
    {
        CreateAssembly("demoplugin");
        File.WriteAllText(Path.Combine(_pluginFolder, PluginPackagePaths.PendingFolderName), string.Empty);
        var logger = new CapturingLogger();

        var outcome = await CreateRemover(logger: logger).RemoveAsync("demoplugin", CancellationToken.None);

        outcome.Succeeded.Should().BeFalse();
        outcome.Error.Should().Be("Could not remove plugin 'demoplugin'. It will be retried on next start.");
        outcome.Error.Should().NotContain(_pluginFolder);
        logger.Warnings.Should().ContainSingle().Which.Message.Should()
            .Be("Could not mark plugin demoplugin for removal; it will be retried on next start.");
    }

    [Test]
    public async Task IdOnly_Prefers_The_Schema_Package_When_Both_Kinds_Exist()
    {
        var session = new PluginInstallSession();
        var remover = CreateRemover(session);
        var schemaPath = CreateSchema("demo");
        var assemblyPath = CreateAssembly("demo");

        var outcome = await remover.RemoveAsync("demo", CancellationToken.None);

        outcome.Succeeded.Should().BeTrue(outcome.Error);
        outcome.InstallPath.Should().Be(schemaPath);
        Directory.Exists(schemaPath).Should().BeFalse();
        Directory.Exists(assemblyPath).Should().BeTrue();
        outcome.RequiresRestart.Should().BeFalse();
    }

    [Test]
    public async Task ExplicitPath_Removes_A_Schema_From_A_Later_Configured_Folder()
    {
        var firstFolder = CreateSiblingFolder("first");
        var config = new PluginConfig();
        config.PluginFolders.Add(firstFolder);
        config.PluginFolders.Add(_pluginFolder);
        var session = new PluginInstallSession();
        var remover = new PluginPackageRemover(config, session, null!, _ => true);
        var installPath = CreateSchema("demo");

        var outcome = await remover.RemoveAsync(
            "demo", Path.Combine(_pluginFolder, ProtobufSchemaFolderLoader.FolderName, ".", "demo"), false,
            CancellationToken.None);

        outcome.Succeeded.Should().BeTrue(outcome.Error);
        outcome.InstallPath.Should().Be(Path.GetFullPath(installPath));
        Directory.Exists(installPath).Should().BeFalse();
    }

    [Test]
    public async Task IdOnly_Ignores_A_PreCanceled_Token()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var installPath = CreateSchema("demo");

        var outcome = await CreateRemover().RemoveAsync("demo", cts.Token);

        outcome.Succeeded.Should().BeTrue(outcome.Error);
        Directory.Exists(installPath).Should().BeFalse();
    }

    [Test]
    public async Task ExplicitPath_Ignores_A_PreCanceled_Token()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var installPath = CreateSchema("demo");

        var outcome = await CreateRemover().RemoveAsync("demo", installPath, false, cts.Token);

        outcome.Succeeded.Should().BeTrue(outcome.Error);
        Directory.Exists(installPath).Should().BeFalse();
    }

    [Test]
    public async Task ExplicitPath_Deletes_A_NeverLoaded_Assembly_And_Forgets_The_Session()
    {
        var session = new PluginInstallSession();
        session.Record("demoplugin", requiresRestart: true);
        var remover = CreateRemover(session);
        var installPath = CreateAssembly("demoplugin");

        var outcome = await remover.RemoveAsync("demoplugin", installPath, false, CancellationToken.None);

        outcome.Succeeded.Should().BeTrue(outcome.Error);
        outcome.RequiresRestart.Should().BeFalse();
        Directory.Exists(installPath).Should().BeFalse();
        PluginPendingOperations.HasPendingOperation(_pluginFolder, "demoplugin").Should().BeFalse();
        session.HasPending.Should().BeFalse();
    }

    [Test]
    public async Task ExplicitPath_Defers_A_Loaded_Assembly_In_Its_Owning_Folder()
    {
        var firstFolder = CreateSiblingFolder("first");
        var config = new PluginConfig();
        config.PluginFolders.Add(firstFolder);
        config.PluginFolders.Add(_pluginFolder);
        var session = new PluginInstallSession();
        var remover = new PluginPackageRemover(config, session, null!, _ => true);
        var installPath = CreateAssembly("demoplugin");

        var outcome = await remover.RemoveAsync("demoplugin", installPath, true, CancellationToken.None);

        outcome.Succeeded.Should().BeTrue(outcome.Error);
        outcome.RequiresRestart.Should().BeTrue();
        PluginPendingOperations.HasPendingOperation(_pluginFolder, "demoplugin").Should().BeTrue();
        PluginPendingOperations.HasPendingOperation(firstFolder, "demoplugin").Should().BeFalse();
    }

    [Test]
    public async Task ExplicitPath_Uses_Canonical_Configured_Path_With_CaseDifference_And_DotSegment()
    {
        var firstFolder = CreateSiblingFolder("first");
        var configuredFolder = Path.Combine(_pluginFolder, ".");
        var config = new PluginConfig();
        config.PluginFolders.Add(firstFolder);
        config.PluginFolders.Add(configuredFolder);
        var remover = new PluginPackageRemover(config, new PluginInstallSession(), null!, _ => true);
        var installPath = CreateAssembly("demoplugin");
        var canonicalInstallPath = Path.GetFullPath(installPath);

        var outcome = await remover.RemoveAsync(
            "demoplugin", installPath.ToUpperInvariant(), false, CancellationToken.None);

        outcome.Succeeded.Should().BeTrue(outcome.Error);
        outcome.InstallPath.Should().Be(canonicalInstallPath);
        Directory.Exists(canonicalInstallPath).Should().BeFalse();
        PluginPendingOperations.HasPendingOperation(_pluginFolder, "demoplugin").Should().BeFalse();
        PluginPendingOperations.HasPendingOperation(firstFolder, "demoplugin").Should().BeFalse();
    }

    [Test]
    public async Task ExplicitPath_Fails_When_The_Owning_Assembly_Folder_Is_Not_Writable()
    {
        var remover = CreateRemover(writable: false);
        var installPath = CreateAssembly("demoplugin");

        var outcome = await remover.RemoveAsync("demoplugin", installPath, false, CancellationToken.None);

        outcome.Succeeded.Should().BeFalse();
        outcome.Error.Should().Contain("not writable");
        outcome.Error.Should().NotContain("not installed");
    }

    [Test]
    public async Task ExplicitPath_Returns_A_Failure_When_The_Removal_Marker_Cannot_Be_Written()
    {
        CreateAssembly("demoplugin");
        File.WriteAllText(Path.Combine(_pluginFolder, PluginPackagePaths.PendingFolderName), string.Empty);
        var logger = new CapturingLogger();

        var outcome = await CreateRemover(logger: logger).RemoveAsync(
            "demoplugin", Path.Combine(_pluginFolder, "demoplugin"), true, CancellationToken.None);

        outcome.Succeeded.Should().BeFalse();
        outcome.Error.Should().Be("Could not remove plugin 'demoplugin'. It will be retried on next start.");
        logger.Warnings.Should().ContainSingle().Which.Message.Should()
            .Be("Could not mark plugin demoplugin for removal; it will be retried on next start.");
    }

    private PluginPackageRemover CreateRemover(
        PluginInstallSession? session = null, bool writable = true, ILogger? logger = null)
    {
        var config = new PluginConfig();
        config.PluginFolders.Add(_pluginFolder);

        return new PluginPackageRemover(config, session ?? new PluginInstallSession(), logger ?? null!, _ => writable);
    }

    private string CreateSchema(string id)
    {
        var path = PluginPackagePaths.ResolveInstallPath(
            _pluginFolder, PluginPackageKinds.ProtobufSchemas, id);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "schema.proto"), "syntax = \"proto3\";");
        return path;
    }

    private string CreateAssembly(string id, string? pluginFolder = null)
    {
        var path = PluginPackagePaths.ResolveInstallPath(
            pluginFolder ?? _pluginFolder, PluginPackageKinds.Assembly, id);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, id + ".dll"), "MZ");
        return path;
    }

    private string CreateSiblingFolder(string name)
    {
        var path = Path.Combine(Path.GetTempPath(), "mqttprobe-remover-" + name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        _extraFolders.Add(path);
        return path;
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<LogEntry> Entries { get; } = [];

        public IEnumerable<LogEntry> Warnings => Entries.Where(entry => entry.Level == LogLevel.Warning);

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NoopScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add(new LogEntry(logLevel, formatter(state, exception)));
        }
    }

    private sealed record LogEntry(LogLevel Level, string Message);

    private sealed class NoopScope : IDisposable
    {
        public static NoopScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
