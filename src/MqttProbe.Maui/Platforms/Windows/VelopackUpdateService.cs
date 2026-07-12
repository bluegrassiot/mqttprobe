using Microsoft.Extensions.Logging;
using MqttProbe.Services.Platform;
using Velopack;
using Velopack.Sources;

namespace MqttProbe.WinUI;

public sealed class VelopackUpdateService : IUpdateService
{
    private const string RepoUrl = "https://github.com/bluegrassiot/mqttprobe";

    private readonly ILogger<VelopackUpdateService> _logger;
    private readonly UpdateManager _manager;
    private UpdateInfo? _pendingUpdate;

    public VelopackUpdateService(ILogger<VelopackUpdateService> logger)
    {
        _logger = logger;
        _manager = new UpdateManager(new GithubSource(RepoUrl, accessToken: null, prerelease: false));
    }

    // False for portable-zip runs: Velopack only reports installed for apps it laid down.
    public bool IsSupported => _manager.IsInstalled;

    public async Task<string?> CheckForUpdateAsync(CancellationToken cancellationToken = default)
    {
        if (!IsSupported)
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
        if (_pendingUpdate is null)
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
