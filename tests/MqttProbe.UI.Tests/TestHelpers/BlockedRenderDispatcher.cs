namespace MqttProbe.UI.Tests.TestHelpers;

internal sealed class BlockedRenderDispatcher
{
    private readonly List<Func<Task>> _held = [];

    internal bool Hold { get; set; }

    internal bool ThrowOnDispatch { get; set; }

    internal int PostedCount { get; private set; }

    internal Task Dispatch(Func<Task> work)
    {
        PostedCount++;
        if (ThrowOnDispatch)
            throw new ObjectDisposedException(nameof(BlockedRenderDispatcher));

        if (!Hold)
            return work();

        _held.Add(work);
        return Task.CompletedTask;
    }

    internal async Task ReleaseAsync()
    {
        while (_held.Count > 0)
        {
            var work = _held[0];
            _held.RemoveAt(0);
            await work();
        }
    }
}
