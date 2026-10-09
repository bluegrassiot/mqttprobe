using Microsoft.Extensions.Logging;
using MqttProbe.Core.Services.Platform;
using Velopack;
using Velopack.Sources;

namespace MqttProbe.Desktop.Services;

public sealed class DesktopVelopackUpdateService : IUpdateService
{
    // Velopack's GithubSource requires the repo URL as a literal; there is no
    // per-user configuration surface for the update feed, so S1075 is noise here.
#pragma warning disable S1075 // URIs should not be hardcoded
    private const string RepoUrl = "https://github.com/bluegrassiot/mqttprobe";
#pragma warning restore S1075

    private readonly ILogger<DesktopVelopackUpdateService> _logger;
    private readonly UpdateManager? _manager;
    private UpdateInfo? _pendingUpdate;

    public DesktopVelopackUpdateService(ILogger<DesktopVelopackUpdateService> logger)
    {
        _logger = logger;
        try
        {
            _manager = new UpdateManager(new GithubSource(RepoUrl, accessToken: null, prerelease: false));
        }
        catch (InvalidOperationException ex)
        {
            // VelopackLocator not set (not in an installed app context).
            _logger.LogDebug(ex, "Velopack locator not initialized; running outside installed app.");
            _manager = null;
        }
    }

    // False for zip/dev runs and for Windows builds of this head: Velopack
    // only reports installed for apps it laid down (the Linux AppImage).
    public bool IsSupported => _manager?.IsInstalled ?? false;

    public async Task<string?> CheckForUpdateAsync(CancellationToken cancellationToken = default)
    {
        if (!IsSupported || _manager is null)
            return null;

        try
        {
            _pendingUpdate = await _manager.CheckForUpdatesAsync();
            return _pendingUpdate?.TargetFullRelease.Version.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Update check failed; continuing without update.");
            return null;
        }
    }

    public async Task DownloadAndApplyAsync(CancellationToken cancellationToken = default)
    {
        var pendingUpdate = _pendingUpdate;
        var manager = _manager;
        if (pendingUpdate is null || manager is null)
            return;

        await RunDownloadAndApplyAsync(async () =>
        {
            await manager.DownloadUpdatesAsync(pendingUpdate, cancelToken: cancellationToken);
            manager.ApplyUpdatesAndRestart(pendingUpdate.TargetFullRelease);
        });
    }

    internal async Task RunDownloadAndApplyAsync(Func<Task> applyUpdate)
    {
        try
        {
            await applyUpdate();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Update download/apply failed.");
            throw;
        }
    }
}
