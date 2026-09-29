using MqttProbe.Core.Services.Emulation;
using MqttProbe.Core.Services.Mqtt;

namespace MqttProbe.Web.Authentication;

public sealed class ScopedCircuitTeardownHandler : ICircuitTeardownHandler
{
    private readonly IMqttManagedClient? _mqttClient;
    private readonly IEmulationService? _emulationService;
    private readonly IAuthenticationStateInvalidator _authStateInvalidator;
    private readonly ILoginNavigationNotifier _loginNotifier;
    private readonly ILogger<ScopedCircuitTeardownHandler> _logger;
    private readonly TimeSpan _cleanupTimeout;

    public ScopedCircuitTeardownHandler(
        IMqttManagedClient? mqttClient,
        IEmulationService? emulationService,
        IAuthenticationStateInvalidator authStateInvalidator,
        ILoginNavigationNotifier loginNotifier,
        ILogger<ScopedCircuitTeardownHandler> logger,
        TimeSpan? cleanupTimeout = null)
    {
        _mqttClient = mqttClient;
        _emulationService = emulationService;
        _authStateInvalidator = authStateInvalidator;
        _loginNotifier = loginNotifier;
        _logger = logger;
        _cleanupTimeout = cleanupTimeout ?? TimeSpan.FromSeconds(10);
    }

    public Task TeardownAsync(bool notifyForceLogin = true, CancellationToken cancellationToken = default)
        => TeardownAsync(() => notifyForceLogin, cancellationToken);

    public async Task TeardownAsync(
        Func<bool> shouldNotifyForceLogin,
        CancellationToken cancellationToken = default)
    {
        // Hung emulation cannot prevent MQTT stop.
        var emulationTask = StopEmulationAsync(cancellationToken);
        var mqttTask = StopMqttAsync(cancellationToken);

        await Task.WhenAll(emulationTask, mqttTask);

        _authStateInvalidator.SetAnonymous();

        // Re-read here, not when revocation started: an explicit logout that
        // lands during cleanup must still win over an expiry-triggered
        // force-login, or the forced /Login navigation races the identity
        // provider redirect the browser is about to follow.
        if (shouldNotifyForceLogin())
        {
            _loginNotifier.NotifyForceLogin();
        }
    }

    private async Task StopEmulationAsync(CancellationToken cancellationToken)
    {
        if (_emulationService is null) return;

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_cleanupTimeout);
            await _emulationService.StopAsync().WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Emulation stop cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to stop emulation");
        }
    }

    private async Task StopMqttAsync(CancellationToken cancellationToken)
    {
        if (_mqttClient is null) return;

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_cleanupTimeout);
            await _mqttClient.StopAsync(timeout.Token).WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "MQTT stop cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to stop MQTT client");
        }
    }
}

public interface IAuthenticationStateInvalidator
{
    public void SetAnonymous();
}
