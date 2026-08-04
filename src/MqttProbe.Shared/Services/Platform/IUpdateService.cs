namespace MqttProbe.Services.Platform;

public interface IUpdateService
{
    // False when not running from a Velopack-installed app (portable zip, dev runs, unsupported heads).
    public bool IsSupported { get; }

    // Null when up to date, unsupported, or on any error.
    public Task<string?> CheckForUpdateAsync(CancellationToken cancellationToken = default);

    // Applies the update found by the last successful check and restarts. No-op if none.
    public Task DownloadAndApplyAsync(CancellationToken cancellationToken = default);
}
