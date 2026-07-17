using Microsoft.Extensions.Logging;
using MqttProbe.Services.Platform;
using Velopack;
using Velopack.Sources;

namespace MqttProbe.Desktop.Services;

public sealed class DesktopVelopackUpdateService : IUpdateService
{
    private const string RepoUrl = "https://github.com/bluegrassiot/mqttprobe";

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
        if (_pendingUpdate is null || _manager is null)
            return;

        try
        {
            await _manager.DownloadUpdatesAsync(_pendingUpdate, cancelToken: cancellationToken);
            _manager.ApplyUpdatesAndRestart(_pendingUpdate.TargetFullRelease);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Update download/apply failed.");
        }
    }
}
