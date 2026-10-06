using Microsoft.Extensions.Logging;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Services.Configuration;
using MqttProbe.Core.Services.Mqtt;

namespace MqttProbe.Core.Tests.Services.Mqtt;

[TestFixture]
public class TopicExcludeServiceTests
{
    private IConnectionSettings _settings = null!;
    private SessionState _session = null!;
    private TopicExcludeService _service = null!;
    private CapturingLogger<TopicExcludeService> _logger = null!;

    [SetUp]
    public void Setup()
    {
        _settings = Substitute.For<IConnectionSettings>();
        _session = new SessionState
        {
            SelectedConnection = new Connection { Id = Guid.NewGuid(), Name = "Test" }
        };
        _settings.Connections.Returns([]);
        _logger = new CapturingLogger<TopicExcludeService>();
        _service = new TopicExcludeService(_logger, _settings, _session);
    }

    [TearDown]
    public void TearDown() => _service.Dispose();

    [Test]
    public async Task Add_PersistsImmediatelyAndMatchesMqttWildcards()
    {
        await _service.Add("sensors/#");

        _service.TopicExcludes.Should().ContainSingle().Which.Should().Be("sensors/#");
        _service.IsExcluded("sensors/temperature").Should().BeTrue();
        await _settings.Received(1).AddConnectionAsync(
            Arg.Is<Connection>(connection => connection.TopicExcludes.Contains("sensors/#")));
    }

    [Test]
    public async Task Add_HashAlone_IsAllowed()
    {
        await _service.Add("#");

        _service.IsExcluded("anything").Should().BeTrue();
        _service.IsExcluded("$SYS/broker/uptime").Should().BeTrue();
    }

    [Test]
    public async Task Add_Duplicate_IsRejected()
    {
        await _service.Add("duplicate");
        await _service.Add("duplicate");

        _service.TopicExcludes.Should().ContainSingle();
        var result = _service.ValidateAdd("duplicate");
        result.IsValid.Should().BeFalse();
        result.Feedback!.Message.Should().Contain("Already");
    }

    [Test]
    public async Task Add_InvalidFilter_DoesNotMutateState()
    {
        await _service.Add("sensors/#/invalid");

        _service.TopicExcludes.Should().BeEmpty();
        _service.ValidateAdd("sensors/#/invalid").IsValid.Should().BeFalse();
    }

    [Test]
    public async Task Add_PersistenceFailure_DoesNotMutateState()
    {
        _settings.AddConnectionAsync(Arg.Any<Connection>())
            .Returns(Task.FromException(new IOException("disk full")));

        var result = await _service.Add("failed/topic");

        result.IsValid.Should().BeFalse();
        result.Feedback!.Message.Should().Contain("Failed");
        _service.TopicExcludes.Should().BeEmpty();
        _session.SelectedConnection.TopicExcludes.Should().BeEmpty();
    }

    [Test]
    public async Task Add_AtLimit_IsRejected()
    {
        for (var i = 0; i < 500; i++)
            await _service.Add($"topic/{i}");

        await _service.Add("topic/501");

        _service.TopicExcludes.Should().HaveCount(500);
        var result = _service.ValidateAdd("topic/501");
        result.IsValid.Should().BeFalse();
        result.Feedback!.Message.Should().Contain("limit");
    }

    [Test]
    public void SelectedConnectionChanged_LoadsThatConnectionsExcludes()
    {
        var next = new Connection
        {
            Id = Guid.NewGuid(),
            TopicExcludes = ["new/#"]
        };

        _session.SelectedConnection = next;

        _service.TopicExcludes.Should().Equal("new/#");
        _service.IsExcluded("new/topic").Should().BeTrue();
        _service.IsExcluded("old/topic").Should().BeFalse();
    }

    [Test]
    public void SelectedConnectionChanged_CapsLoadedExcludesAt500()
    {
        _session.SelectedConnection = new Connection
        {
            Id = Guid.NewGuid(),
            TopicExcludes = Enumerable.Range(0, 501).Select(i => $"topic/{i}").ToList()
        };

        _service.TopicExcludes.Should().HaveCount(500);
    }

    [Test]
    public async Task Remove_PersistsRemainingExcludes()
    {
        await _service.Add("keep");
        await _service.Add("remove");
        _settings.ClearReceivedCalls();

        await _service.Remove(["remove"]);

        _service.TopicExcludes.Should().Equal("keep");
        await _settings.Received(1).AddConnectionAsync(
            Arg.Is<Connection>(connection => connection.TopicExcludes.SequenceEqual(new[] { "keep" })));
    }

    [Test]
    public async Task Add_WhenActiveConnectionChangesDuringPersistence_DoesNotMutateNewConnection()
    {
        var published = new List<string>();
        _service.TopicExcluded += published.Add;
        var oldConnection = _session.SelectedConnection;
        var newConnection = new Connection
        {
            Id = Guid.NewGuid(),
            TopicExcludes = ["new/#"]
        };
        _settings.AddConnectionAsync(Arg.Any<Connection>()).Returns(_ =>
        {
            _session.SelectedConnection = newConnection;
            return Task.CompletedTask;
        });

        var result = await _service.Add("old/#");

        result.IsValid.Should().BeFalse();
        _session.SelectedConnection.Should().BeSameAs(newConnection);
        _service.TopicExcludes.Should().Equal("new/#");
        oldConnection.TopicExcludes.Should().Contain("old/#");
        published.Should().NotContain("old/#");
        published.Should().Contain("new/#");
        await _settings.Received(1).AddConnectionAsync(
            Arg.Is<Connection>(connection => connection.Id == oldConnection.Id));
    }

    [Test]
    public async Task TryEnter_AdmitsAllowedTopic_AndRejectsTopicUnderAnActiveFilter()
    {
        var admitted = _service.TryEnter("sensors/temp");
        admitted.Should().NotBeNull();
        admitted.Dispose();

        await _service.Add("sensors/#");

        _service.TryEnter("sensors/temp").Should().BeNull();
        _service.TryEnter("sensors/pressure").Should().BeNull();

        var other = _service.TryEnter("other/topic");
        other.Should().NotBeNull();
        other.Dispose();
    }

    [Test]
    public async Task Add_WaitsForInFlightReader_BeforeRaisingTopicExcluded()
    {
        var published = new List<string>();
        _service.TopicExcluded += published.Add;
        var gate = _service.TryEnter("sensors/temp");
        gate.Should().NotBeNull();

        var addTask = _service.Add("sensors/#");
        var filterApplied = await SatisfiesAsync(
            () => _service.IsExcluded("sensors/temp"), TimeSpan.FromSeconds(2));
        var finishedWhileParked = await SatisfiesAsync(
            () => addTask.IsCompleted, TimeSpan.FromMilliseconds(250));
        gate.Dispose();

        var result = await addTask.WaitAsync(TimeSpan.FromSeconds(10));

        filterApplied.Should().BeTrue("the filter has to be live before the purge waits for readers");
        finishedWhileParked.Should().BeFalse(
            "Add must hold the purge until the in-flight reader commits, or the reader can put excluded data back");
        result.IsValid.Should().BeTrue();
        published.Should().Equal("sensors/#");
    }

    [Test]
    public async Task Add_WhenConnectionChangesWhileDraining_DoesNotRaiseTopicExcluded()
    {
        var published = new List<string>();
        _service.TopicExcluded += published.Add;
        var gate = _service.TryEnter("sensors/temp");
        gate.Should().NotBeNull();

        var addTask = _service.Add("sensors/#");
        var filterApplied = await SatisfiesAsync(
            () => _service.IsExcluded("sensors/temp"), TimeSpan.FromSeconds(2));

        _session.SelectedConnection = new Connection
        {
            Id = Guid.NewGuid(),
            TopicExcludes = ["new/#"]
        };
        gate.Dispose();

        var result = await addTask.WaitAsync(TimeSpan.FromSeconds(10));

        filterApplied.Should().BeTrue();
        result.IsValid.Should().BeFalse();
        result.Feedback!.Message.Should().Contain("connection");
        published.Should().NotContain("sensors/#");
        published.Should().Contain("new/#");
        _service.TopicExcludes.Should().Equal("new/#");
    }

    [Test]
    public async Task ClearActiveTopicExcludes_WhileAddWaitsForReader_StillCompletes()
    {
        var published = new List<string>();
        _service.TopicExcluded += published.Add;
        var gate = _service.TryEnter("sensors/temp");
        gate.Should().NotBeNull();

        var addTask = _service.Add("sensors/#");
        var filterApplied = await SatisfiesAsync(
            () => _service.IsExcluded("sensors/temp"), TimeSpan.FromSeconds(2));

        _service.ClearActiveTopicExcludes();
        gate.Dispose();

        var result = await addTask.WaitAsync(TimeSpan.FromSeconds(10));

        filterApplied.Should().BeTrue();
        result.IsValid.Should().BeFalse("the generation moved on, so the purge is superseded");
        published.Should().BeEmpty();
        _service.TopicExcludes.Should().BeEmpty();
        _service.IsExcluded("sensors/temp").Should().BeFalse();
    }

    [Test]
    public async Task Add_ImmediatePurgeThrowing_ReportsFailureAndKeepsFilterActive()
    {
        var published = new List<string>();
        _service.TopicExcluded += published.Add;
        _service.PurgeExcludedTopic += _ => throw new InvalidOperationException("purge boom");

        var result = await _service.Add("sensors/#").WaitAsync(TimeSpan.FromSeconds(10));

        result.IsValid.Should().BeFalse("the caller has to see that dependent data was not cleared");
        result.Feedback!.Message.Should().Contain("clearing");
        _service.TopicExcludes.Should().ContainSingle().Which.Should().Be("sensors/#");
        _service.IsExcluded("sensors/temperature").Should().BeTrue("the filter stays active after a purge failure");
        published.Should().Equal("sensors/#");
        _logger.Entries.Should().Contain(entry =>
            entry.Level == LogLevel.Error && entry.Exception is InvalidOperationException);
    }

    [Test]
    public async Task Add_DeferredPurgeThrowing_StillCompletesAndReportsFailure()
    {
        var published = new List<string>();
        _service.TopicExcluded += published.Add;
        _service.PurgeExcludedTopic += _ => throw new InvalidOperationException("purge boom");
        var gate = _service.TryEnter("sensors/temp");
        gate.Should().NotBeNull();

        var addTask = _service.Add("sensors/#");
        var filterApplied = await SatisfiesAsync(
            () => _service.IsExcluded("sensors/temp"), TimeSpan.FromSeconds(2));
        var finishedWhileParked = await SatisfiesAsync(
            () => addTask.IsCompleted, TimeSpan.FromMilliseconds(250));
        gate.Dispose();

        var result = await addTask.WaitAsync(TimeSpan.FromSeconds(10));

        filterApplied.Should().BeTrue();
        finishedWhileParked.Should().BeFalse();
        result.IsValid.Should().BeFalse("a throwing purge settles as failure instead of stranding Add");
        result.Feedback!.Message.Should().Contain("clearing");
        _service.TopicExcludes.Should().Equal("sensors/#");
        published.Should().Equal("sensors/#");
        _logger.Entries.Should().Contain(entry => entry.Level == LogLevel.Error);
    }

    [Test]
    public void Reload_PurgerThrowingOnFirstFilter_DoesNotAbortRemainingFilters()
    {
        var purged = new List<string>();
        var published = new List<string>();
        _service.PurgeExcludedTopic += filter =>
        {
            purged.Add(filter);
            throw new InvalidOperationException("purge boom");
        };
        _service.TopicExcluded += published.Add;

        _session.SelectedConnection = new Connection
        {
            Id = Guid.NewGuid(),
            TopicExcludes = ["a/#", "b/#", "c/#"]
        };

        purged.Should().BeEquivalentTo("a/#", "b/#", "c/#");
        published.Should().BeEquivalentTo("a/#", "b/#", "c/#");
        _service.TopicExcludes.Should().BeEquivalentTo("a/#", "b/#", "c/#");
        _logger.Entries.Count(entry => entry.Level == LogLevel.Error).Should().Be(3);
    }

    [Test]
    public async Task TopicExcluded_ObserverThrowing_OperationSucceedsAndOtherObserversRun()
    {
        var published = new List<string>();
        _service.TopicExcluded += _ => throw new InvalidOperationException("observer boom");
        _service.TopicExcluded += published.Add;

        var result = await _service.Add("sensors/#").WaitAsync(TimeSpan.FromSeconds(10));

        result.IsValid.Should().BeTrue("an observer failure is isolated from the operation");
        published.Should().Equal("sensors/#");
        _logger.Entries.Should().Contain(entry =>
            entry.Level == LogLevel.Error && entry.Message.Contains("observer", StringComparison.Ordinal));
    }

    [Test]
    public async Task TopicExcluded_SubscriberSynchronouslyAddsDuringImmediatePurge_CompletesWithoutDeadlock()
    {
        var published = new List<string>();
        var reentered = 0;
        _service.TopicExcluded += filter =>
        {
            published.Add(filter);
            if (Interlocked.Exchange(ref reentered, 1) != 0)
                return;

            _service.Add("reentrant/#").GetAwaiter().GetResult();
        };

        var result = await _service.Add("first/#").WaitAsync(TimeSpan.FromSeconds(10));

        result.IsValid.Should().BeTrue();
        published.Should().Equal("first/#", "reentrant/#");
        _service.TopicExcludes.Should().BeEquivalentTo("first/#", "reentrant/#");
    }

    [Test]
    public async Task TopicExcluded_SubscriberSynchronouslyAddsDuringDeferredPurge_CompletesWithoutDeadlock()
    {
        var published = new List<string>();
        var reentered = 0;
        _service.TopicExcluded += filter =>
        {
            published.Add(filter);
            if (Interlocked.Exchange(ref reentered, 1) != 0)
                return;

            _service.Add("reentrant/#").GetAwaiter().GetResult();
        };

        var gate = _service.TryEnter("sensors/temp");
        gate.Should().NotBeNull();
        var addTask = _service.Add("sensors/#");
        var filterApplied = await SatisfiesAsync(
            () => _service.IsExcluded("sensors/temp"), TimeSpan.FromSeconds(2));
        var finishedWhileParked = await SatisfiesAsync(
            () => addTask.IsCompleted, TimeSpan.FromMilliseconds(250));

        // Releasing on the pool keeps a deadlock visible as a timeout instead of a hung run.
        await Task.Run(() => gate.Dispose()).WaitAsync(TimeSpan.FromSeconds(10));
        var result = await addTask.WaitAsync(TimeSpan.FromSeconds(10));

        filterApplied.Should().BeTrue();
        finishedWhileParked.Should().BeFalse();
        result.IsValid.Should().BeTrue();
        published.Should().Equal("sensors/#", "reentrant/#");
        _service.TopicExcludes.Should().BeEquivalentTo("sensors/#", "reentrant/#");
        _service.IsExcluded("reentrant/topic").Should().BeTrue();
    }

    [Test]
    public async Task ConnectionSwitch_DuringPurgeNotification_PublishesBothGenerationsAndCompletes()
    {
        var published = new List<string>();
        var switched = false;
        _service.TopicExcluded += filter =>
        {
            published.Add(filter);
            if (switched)
                return;

            switched = true;
            _session.SelectedConnection = new Connection
            {
                Id = Guid.NewGuid(),
                TopicExcludes = ["new/#"]
            };
        };

        var result = await _service.Add("old/#").WaitAsync(TimeSpan.FromSeconds(10));

        result.IsValid.Should().BeTrue("its purge had already started publishing");
        published.Should().Equal("old/#", "new/#");
        _service.TopicExcludes.Should().Equal("new/#");
        _service.IsExcluded("new/topic").Should().BeTrue();
        _service.IsExcluded("old/topic").Should().BeFalse();
    }

    [Test]
    public async Task Dispose_WhileDeferredPurgePending_AddStillCompletes()
    {
        var gate = _service.TryEnter("sensors/temp");
        gate.Should().NotBeNull();

        var addTask = _service.Add("sensors/#");
        var filterApplied = await SatisfiesAsync(
            () => _service.IsExcluded("sensors/temp"), TimeSpan.FromSeconds(2));
        filterApplied.Should().BeTrue();

        _service.Dispose();

        var result = await addTask.WaitAsync(TimeSpan.FromSeconds(10));

        result.IsValid.Should().BeFalse("disposal supersedes the parked purge instead of stranding Add");
        gate.Dispose();
    }

    [Test]
    public async Task Add_WhenDisposedDuringPersistence_ReportsFailure()
    {
        var published = new List<string>();
        _service.TopicExcluded += published.Add;
        var persisting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _settings.AddConnectionAsync(Arg.Any<Connection>()).Returns(async _ =>
        {
            persisting.TrySetResult();
            await release.Task;
        });

        var addTask = _service.Add("sensors/#");
        (await SatisfiesAsync(() => persisting.Task.IsCompleted, TimeSpan.FromSeconds(5))).Should().BeTrue(
            "the service has to be inside the persistence await before disposal races it");

        _service.Dispose();
        release.TrySetResult();

        var result = await addTask.WaitAsync(TimeSpan.FromSeconds(10));

        result.IsValid.Should().BeFalse(
            "a disposed service cannot claim an exclusion it will never purge or back");
        result.Feedback!.Message.Should().Contain("closed");
        _service.TopicExcludes.Should().BeEmpty();
        published.Should().BeEmpty("purgers must not run on a service that was disposed mid-flight");
    }

    [Test]
    public async Task Dispose_WhileAddPausedInPersistenceAndSecondAddQueued_BothAddsFinish()
    {
        var published = new List<string>();
        _service.TopicExcluded += published.Add;
        var persisting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _settings.AddConnectionAsync(Arg.Any<Connection>()).Returns(async _ =>
        {
            persisting.TrySetResult();
            await release.Task;
        });

        var firstAdd = _service.Add("first/#");
        (await SatisfiesAsync(() => persisting.Task.IsCompleted, TimeSpan.FromSeconds(5))).Should().BeTrue(
            "the first Add has to own the operation semaphore before the second one queues");

        var secondAdd = _service.Add("second/#");
        var finishedWhileOwnerPaused = await SatisfiesAsync(
            () => secondAdd.IsCompleted, TimeSpan.FromMilliseconds(250));

        _service.Dispose();
        release.TrySetResult();

        var first = await firstAdd.WaitAsync(TimeSpan.FromSeconds(10));
        var second = await secondAdd.WaitAsync(TimeSpan.FromSeconds(10));

        finishedWhileOwnerPaused.Should().BeFalse(
            "the second Add has to be waiting on the operation lock when disposal lands");
        first.IsValid.Should().BeFalse();
        first.Feedback!.Message.Should().Contain("closed");
        second.IsValid.Should().BeFalse(
            "a queued operation must enter once the owner releases and reject on the disposed flag");
        second.Feedback!.Message.Should().Contain("closed");
        published.Should().BeEmpty();
        _service.TopicExcludes.Should().BeEmpty();
    }

    [Test]
    public async Task Add_AfterDispose_ReportsFailureInsteadOfThrowing()
    {
        _service.Dispose();

        var result = await _service.Add("sensors/#").WaitAsync(TimeSpan.FromSeconds(10));

        result.IsValid.Should().BeFalse();
        result.Feedback!.Message.Should().Contain("closed");
        _service.TopicExcludes.Should().BeEmpty();
    }

    [Test]
    public async Task Remove_AfterDispose_ReportsFailureInsteadOfThrowing()
    {
        _service.Dispose();

        var result = await _service.Remove(["sensors/#"]).WaitAsync(TimeSpan.FromSeconds(10));

        result.IsValid.Should().BeFalse();
        result.Feedback!.Message.Should().Contain("closed");
    }

    [Test]
    public async Task Remove_WhenDisposedDuringPersistence_ReportsFailure()
    {
        var persisting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _settings.AddConnectionAsync(Arg.Any<Connection>()).Returns(async _ =>
        {
            persisting.TrySetResult();
            await release.Task;
        });
        _service.TopicExcludes.Should().BeEmpty();

        var removeTask = _service.Remove(["sensors/#"]);
        (await SatisfiesAsync(() => persisting.Task.IsCompleted, TimeSpan.FromSeconds(5))).Should().BeTrue();

        _service.Dispose();
        release.TrySetResult();

        var result = await removeTask.WaitAsync(TimeSpan.FromSeconds(10));

        result.IsValid.Should().BeFalse();
        result.Feedback!.Message.Should().Contain("closed");
    }

    [Test]
    public async Task Remove_OvertakingAParkedAdd_ReportsTheExclusionAsRemoved()
    {
        var published = new List<string>();
        _service.TopicExcluded += published.Add;
        var gate = _service.TryEnter("sensors/temp");
        gate.Should().NotBeNull();

        var addTask = _service.Add("sensors/#");
        var filterApplied = await SatisfiesAsync(
            () => _service.IsExcluded("sensors/temp"), TimeSpan.FromSeconds(2));
        filterApplied.Should().BeTrue();
        addTask.IsCompleted.Should().BeFalse("the purge is parked on the in-flight reader");

        var removeResult = await _service.Remove(["sensors/#"]).WaitAsync(TimeSpan.FromSeconds(10));
        var result = await addTask.WaitAsync(TimeSpan.FromSeconds(10));

        removeResult.IsValid.Should().BeTrue();
        result.IsValid.Should().BeFalse("the exclusion is gone, so Add must not claim it applied");
        result.Feedback!.Message.Should().Contain("removed",
            "the caller deserves the real reason instead of a connection-change message");
        published.Should().BeEmpty("a purge whose filter was removed has nothing to clear");
        _service.TopicExcludes.Should().BeEmpty();
        _service.IsExcluded("sensors/temp").Should().BeFalse();

        gate.Dispose();
    }

    private static async Task<bool> SatisfiesAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return true;

            await Task.Delay(10);
        }

        return condition();
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull =>
            null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception), exception));
    }
}
