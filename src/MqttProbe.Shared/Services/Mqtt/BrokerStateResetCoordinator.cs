using Microsoft.Extensions.Logging;
using MqttProbe.Models.Mqtt;
using MqttProbe.Services.Chart;
using MqttProbe.Services.Emulation;
using MqttProbe.Services.Sparkplug;

namespace MqttProbe.Services.Mqtt;

public interface IBrokerStateResetCoordinator
{
    public Task ResetIfBrokerChangedAsync(Connection target);
}

public class BrokerStateResetCoordinator : IBrokerStateResetCoordinator
{
    private readonly IMessageStoreManager _messageStore;
    private readonly ISparkplugTopologyService _topology;
    private readonly ISubscriptionManager _subscriptions;
    private readonly IChartDataService _chartData;
    private readonly IEmulationService _emulation;
    private readonly ILogger<BrokerStateResetCoordinator> _logger;

    private BrokerIdentity? _lastActiveIdentity;
    private readonly object _identityLock = new();

    public BrokerStateResetCoordinator(
        IMessageStoreManager messageStore,
        ISparkplugTopologyService topology,
        ISubscriptionManager subscriptions,
        IChartDataService chartData,
        IEmulationService emulation,
        ILogger<BrokerStateResetCoordinator> logger)
    {
        _messageStore = messageStore;
        _topology = topology;
        _subscriptions = subscriptions;
        _chartData = chartData;
        _emulation = emulation;
        _logger = logger;
    }

    public async Task ResetIfBrokerChangedAsync(Connection target)
    {
        var targetIdentity = BrokerIdentity.FromConnection(target);

        BrokerIdentity? previous;
        lock (_identityLock)
        {
            previous = _lastActiveIdentity;
            _lastActiveIdentity = targetIdentity;
        }

        if (previous is null)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug("First connect to {Host}:{Port}; storing identity without reset",
                    target.Host, target.Port);
            return;
        }

        if (previous == targetIdentity)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug("Broker identity unchanged ({Host}:{Port}); skipping reset",
                    target.Host, target.Port);
            return;
        }

        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation(
                "Broker changed (previous={Previous}, target={Target}:{Port}); resetting runtime state",
                previous.Host, target.Host, target.Port);

        await ResetAllServicesAsync(target.Id);
    }

    private async Task ResetAllServicesAsync(Guid connectionId)
    {
        await SafeResetAsync("EmulationService.ResetForConnectionAsync",
            () => _emulation.ResetForConnectionAsync(connectionId));

        await SafeResetAsync("MessageStoreManager.ClearAllMessages",
            () => _messageStore.ClearAllMessages());

        SafeReset("SparkplugTopologyService.ClearAll",
            () => _topology.ClearAll());

        SafeReset("SubscriptionManager.ClearActiveSubscriptions",
            () => _subscriptions.ClearActiveSubscriptions());

        SafeReset("ChartDataService.ClearBuffers",
            () => _chartData.ClearBuffers());
    }

    private async Task SafeResetAsync(string description, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reset {Service}", description);
        }
    }

    private void SafeReset(string description, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reset {Service}", description);
        }
    }
}
