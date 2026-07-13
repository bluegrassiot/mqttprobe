namespace MqttProbe.Services.Platform;

public interface IUpdateService
{
    /// <summary>False on non-Windows heads and for portable (non-installed) Windows runs.</summary>
    public bool IsSupported { get; }

    /// <summary>Returns the available version string, or null when up to date, unsupported, or on any error.</summary>
    public Task<string?> CheckForUpdateAsync(CancellationToken cancellationToken = default);

    /// <summary>Downloads the update found by the last successful check, applies it, and restarts. No-op if none.</summary>
    public Task DownloadAndApplyAsync(CancellationToken cancellationToken = default);
}
