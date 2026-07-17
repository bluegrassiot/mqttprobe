namespace MqttProbe.Services.Platform;

/// <summary>Registered on heads without self-update (Web, Desktop, Android, portable runs).</summary>
public sealed class NoOpUpdateService : IUpdateService
{
    public bool IsSupported => false;
    public Task<string?> CheckForUpdateAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
    public Task DownloadAndApplyAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
