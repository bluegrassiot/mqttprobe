using Microsoft.Extensions.Logging;
using MqttProbe.Models.Plugins;
using MqttProbe.Services.Platform;

namespace MqttProbe.Services.Plugins.Packaging;

public sealed class PluginPackageInstaller
{
    private readonly PluginConfig _config;
    private readonly PluginPackagePublisher _publisher;
    private readonly PluginPackageRemover _remover;
    private readonly ILogger<PluginPackageInstaller> _logger;

    public PluginPackageInstaller(
        PluginConfig config,
        IAppInfoService appInfo,
        PluginInstallSession session,
        PluginArchiveLimits limits,
        ILoggerFactory loggerFactory)
    {
        _config = config;
        _logger = loggerFactory.CreateLogger<PluginPackageInstaller>();

        var reader = new PluginPackageArchiveReader(config, appInfo, limits, loggerFactory);
        _publisher = new PluginPackagePublisher(reader, session, _logger);
        _remover = new PluginPackageRemover(config, session, _logger, IsWritable);
    }

    public string? WritablePluginFolder => _config.PluginFolders.FirstOrDefault(IsWritable);

    internal Func<string, bool>? WritableProbeOverride { get; set; }

    public async Task<PluginInstallOutcome> InstallAsync(Stream package, CancellationToken ct)
    {
        try
        {
            var pluginFolder = WritablePluginFolder;

            if (pluginFolder is null)
            {
                return PluginInstallOutcome.Fail("No writable plugin folder is configured on this host.");
            }

            return await _publisher.InstallAsync(package, pluginFolder, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidDataException)
        {
            return PluginInstallOutcome.Fail("The uploaded file is not a readable zip archive.");
        }
        catch (PluginPackageExpansionLimitExceededException ex)
        {
            return PluginInstallOutcome.Fail(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Plugin install failed.");
            return PluginInstallOutcome.Fail("Install failed. See the server log for details.");
        }
    }

    public Task<PluginInstallOutcome> RemoveAsync(string id, CancellationToken ct) =>
        _remover.RemoveAsync(id, ct);

    public Task<PluginInstallOutcome> RemoveAsync(
        string id, string installPath, bool isLoaded, CancellationToken ct) =>
        _remover.RemoveAsync(id, installPath, isLoaded, ct);

    internal void Swap(string stagedPath, string installPath) =>
        _publisher.Swap(stagedPath, installPath);

    private bool IsWritable(string folder) =>
        (WritableProbeOverride ?? DefaultIsWritable)(folder);

    private static bool DefaultIsWritable(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);

            var probe = Path.Combine(folder, ".write-probe-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }
}
