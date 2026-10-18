using Microsoft.Extensions.Logging;

namespace MqttProbe.Core.Services.Mqtt;

public sealed partial class MqttManagedClient
{
    private sealed class ConnectionLifetime(long epoch, long subscriptionRevision)
    {
        private int _activeResources;
        private TaskCompletionSource _drained = CompletedDrain();
        private readonly TaskCompletionSource _retirementCompleted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public long Epoch { get; } = epoch;

        public long SubscriptionRevision { get; set; } = subscriptionRevision;

        public CancellationTokenSource Cancellation { get; } = new();

        public bool Retired { get; private set; }

        public bool RetirementStarted { get; private set; }

        public Task DrainTask => _drained.Task;

        public Task RetirementTask => _retirementCompleted.Task;

        public bool TryAdmitResource()
        {
            if (Retired)
                return false;

            if (_activeResources++ == 0)
                _drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return true;
        }

        public void CompleteResource()
        {
            if (--_activeResources == 0)
                _drained.TrySetResult();
        }

        public bool ClaimRetirement()
        {
            Retired = true;
            if (RetirementStarted)
                return false;

            RetirementStarted = true;
            return true;
        }

        public void CompleteRetirement() => _retirementCompleted.TrySetResult();

        private static TaskCompletionSource CompletedDrain()
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            completion.SetResult();
            return completion;
        }
    }

    private void StartLifetimeRetirement(Generation generation, ConnectionLifetime lifetime) =>
        _ = RetireLifetimeAsync(generation, lifetime);

    private async Task RetireLifetimeAsync(Generation generation, ConnectionLifetime lifetime)
    {
        try
        {
            try
            {
                await lifetime.Cancellation.CancelAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Connection lifetime cancellation callback failed");
            }

            await lifetime.DrainTask.ConfigureAwait(false);
            lifetime.Cancellation.Dispose();
        }
        finally
        {
            lock (_sync)
                generation.Lifetimes.Remove(lifetime);
            lifetime.CompleteRetirement();
        }
    }

    private void CompleteLifetimeResource(ConnectionLifetime lifetime)
    {
        lock (_sync)
            lifetime.CompleteResource();
    }

    private List<SemaphoreSlim> GetSubscriptionMutationGates(IEnumerable<string> topics)
    {
        lock (_sync)
        {
            return topics.Distinct(StringComparer.Ordinal)
                .OrderBy(topic => topic, StringComparer.Ordinal)
                .Select(topic =>
                {
                    if (!_subscriptionMutationGates.TryGetValue(topic, out var gate))
                    {
                        gate = new SemaphoreSlim(1, 1);
                        _subscriptionMutationGates.Add(topic, gate);
                    }
                    return gate;
                })
                .ToList();
        }
    }

    private static void ReleaseSubscriptionMutationGates(List<SemaphoreSlim> gates)
    {
        for (var index = gates.Count - 1; index >= 0; index--)
            gates[index].Release();
    }
}
