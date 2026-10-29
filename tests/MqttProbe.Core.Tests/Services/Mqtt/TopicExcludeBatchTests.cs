using Microsoft.Extensions.Logging;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Services.Configuration;
using MqttProbe.Core.Services.Mqtt;

namespace MqttProbe.Core.Tests.Services.Mqtt;

[TestFixture]
public sealed class TopicExcludeBatchTests
{
    private IConnectionSettings _settings = null!;
    private SessionState _session = null!;
    private TopicExcludeService _service = null!;

    [SetUp]
    public void Setup()
    {
        _settings = Substitute.For<IConnectionSettings>();
        _session = new SessionState
        {
            SelectedConnection = new Connection { Id = Guid.NewGuid(), Name = "Test" }
        };
        _settings.Connections.Returns([]);
        _service = new TopicExcludeService(Substitute.For<ILogger<TopicExcludeService>>(), _settings, _session);
    }

    [TearDown]
    public void TearDown() => _service.Dispose();

    [Test]
    public async Task AddMany_AppendsExactPathsAndPreservesExistingEntries()
    {
        await _service.Add("existing/#");

        var result = await _service.AddMany(["sensors/temp", "sensors/humidity"]);

        result.Results.Select(item => item.Outcome).Should().Equal(
            TopicExcludeAddOutcome.Added, TopicExcludeAddOutcome.Added);
        result.AddedTopics.Should().Equal("sensors/temp", "sensors/humidity");
        _service.TopicExcludes.Should().Equal("existing/#", "sensors/temp", "sensors/humidity");
        _session.SelectedConnection.TopicExcludes.Should().Equal("existing/#", "sensors/temp", "sensors/humidity");
        _service.IsExcluded("sensors/temp").Should().BeTrue("future traffic on the path is gated out");
        _service.IsExcluded("sensors/other").Should().BeFalse();
    }

    [Test]
    public async Task AddMany_ReportsAlreadyExcludedOncePerPath()
    {
        await _service.Add("duplicate");

        var result = await _service.AddMany(["duplicate", "duplicate", "new/topic"]);

        result.Results.Select(item => item.Outcome).Should().Equal(
            TopicExcludeAddOutcome.AlreadyExcluded, TopicExcludeAddOutcome.AlreadyExcluded, TopicExcludeAddOutcome.Added);
        result.AllSucceeded.Should().BeTrue("already excluded is the requested end state");
        _service.TopicExcludes.Should().Equal("duplicate", "new/topic");
    }

    [Test]
    public async Task AddMany_StopsAtTheCapAndReportsTheRemainingPathsAsLimited()
    {
        for (var i = 0; i < 499; i++)
            await _service.Add($"topic/{i}");

        var result = await _service.AddMany(["topic/last", "topic/over", "topic/further"]);

        result.Results.Select(item => item.Outcome).Should().Equal(
            TopicExcludeAddOutcome.Added, TopicExcludeAddOutcome.LimitReached, TopicExcludeAddOutcome.LimitReached);
        result.AllSucceeded.Should().BeFalse();
        _service.TopicExcludes.Should().HaveCount(500);
        _service.TopicExcludes.Should().Contain("topic/last");
        _service.TopicExcludes.Should().NotContain("topic/over");
    }

    [Test]
    public async Task AddMany_KeepsAlreadyExcludedPathsReportableWhenTheListIsFull()
    {
        for (var i = 0; i < 500; i++)
            await _service.Add($"topic/{i}");

        var result = await _service.AddMany(["topic/0", "topic/new"]);

        result.Results.Select(item => item.Outcome).Should().Equal(
            TopicExcludeAddOutcome.AlreadyExcluded, TopicExcludeAddOutcome.LimitReached);
    }

    [Test]
    public async Task AddMany_ReportsAPartialPersistenceFailurePerPath()
    {
        _settings.AddConnectionAsync(Arg.Any<Connection>()).Returns(call =>
            call.Arg<Connection>().TopicExcludes.Contains("failing/topic")
                ? Task.FromException(new IOException("disk full"))
                : Task.CompletedTask);

        var result = await _service.AddMany(["first/topic", "failing/topic", "last/topic"]);

        result.Results.Select(item => item.Outcome).Should().Equal(
            TopicExcludeAddOutcome.Added, TopicExcludeAddOutcome.Failed, TopicExcludeAddOutcome.Added);
        result.Results[1].Feedback!.Message.Should().Contain("Failed");
        result.AddedTopics.Should().Equal("first/topic", "last/topic");
        _service.TopicExcludes.Should().Equal("first/topic", "last/topic");
    }

    [Test]
    public async Task AddMany_ReportsInvalidFiltersAndContinues()
    {
        var result = await _service.AddMany(["sensors/#/invalid", "sensors/valid"]);

        result.Results.Select(item => item.Outcome).Should().Equal(
            TopicExcludeAddOutcome.Invalid, TopicExcludeAddOutcome.Added);
        _service.TopicExcludes.Should().Equal("sensors/valid");
    }

    [Test]
    public async Task AddMany_PurgesEveryAcceptedPathExactlyOnce()
    {
        var purged = new List<string>();
        _service.PurgeExcludedTopic += purged.Add;

        await _service.AddMany(["one/topic", "two/topic"]);

        purged.Should().Equal("one/topic", "two/topic");
    }

    [Test]
    public async Task AddMany_FailsTheRemainderWhenTheConnectionChangesMidBatch()
    {
        var startedOn = _session.SelectedConnection;
        var next = new Connection { Id = Guid.NewGuid(), Name = "Next" };
        _settings.AddConnectionAsync(Arg.Any<Connection>()).Returns(call =>
        {
            if (call.Arg<Connection>().Id == startedOn.Id
                && call.Arg<Connection>().TopicExcludes.Contains("first/topic"))
                _session.SelectedConnection = next;

            return Task.CompletedTask;
        });

        var result = await _service.AddMany(["first/topic", "second/topic", "third/topic"]);

        result.Results.Select(item => item.Outcome).Should().Equal(
            TopicExcludeAddOutcome.Failed,
            TopicExcludeAddOutcome.Failed,
            TopicExcludeAddOutcome.Failed);
        result.Results.Should().OnlyContain(item => item.Feedback!.Message.Contains("connection"));
        next.TopicExcludes.Should().BeEmpty("the batch must not write to the connection it did not start on");
        _service.TopicExcludes.Should().BeEmpty();
        await _settings.DidNotReceive().AddConnectionAsync(Arg.Is<Connection>(connection => connection.Id == next.Id));
    }

    [Test]
    public async Task AddMany_FromAnEarlierConnectionListener_DoesNotWriteAcrossConnections()
    {
        var settings = Substitute.For<IConnectionSettings>();
        settings.Connections.Returns([]);
        var startedOn = new Connection { Id = Guid.NewGuid(), Name = "StartedOn" };
        var next = new Connection { Id = Guid.NewGuid(), Name = "Next" };
        var session = new SessionState { SelectedConnection = startedOn };
        Task<TopicExcludeBatchResult>? batch = null;
        TopicExcludeService? service = null;
        session.SelectedConnectionChanged += _ => batch = service!.AddMany(["first/topic", "second/topic"]);
        service = new TopicExcludeService(Substitute.For<ILogger<TopicExcludeService>>(), settings, session);

        try
        {
            session.SelectedConnection = next;
            var result = await batch!;

            result.Results.Select(item => item.Outcome).Should().Equal(
                TopicExcludeAddOutcome.Failed, TopicExcludeAddOutcome.Failed);
            result.Results.Should().OnlyContain(item => item.Feedback!.Message.Contains("connection"));
            next.TopicExcludes.Should().BeEmpty();
            await settings.DidNotReceive().AddConnectionAsync(
                Arg.Is<Connection>(connection => connection.Id == next.Id));
        }
        finally
        {
            service.Dispose();
        }
    }

    [Test]
    public async Task AddMany_WhenAPurgeThrows_ReportsAppliedWithPurgeFailureAndKeepsFilterActive()
    {
        var published = new List<string>();
        _service.PurgeExcludedTopic += filter =>
        {
            if (filter == "boom/topic")
                throw new InvalidOperationException("purge boom");
        };
        _service.TopicExcluded += published.Add;

        var result = await _service.AddMany(["ok/topic", "boom/topic"]);

        result.Results.Select(item => item.Outcome).Should().Equal(
            TopicExcludeAddOutcome.Added,
            TopicExcludeAddOutcome.AppliedWithPurgeFailure);
        result.AddedTopics.Should().Equal("ok/topic");
        result.AppliedTopics.Should().Equal("ok/topic", "boom/topic");
        result.AllSucceeded.Should().BeFalse("a failed buffered purge is a partial success");
        result.Results[1].Feedback!.Message.Should().Contain("future");
        result.Results[1].Feedback!.Message.Should().Contain("clearing");
        _service.TopicExcludes.Should().Equal("ok/topic", "boom/topic");
        _session.SelectedConnection.TopicExcludes.Should().Equal("ok/topic", "boom/topic");
        _service.IsExcluded("ok/topic").Should().BeTrue();
        _service.IsExcluded("boom/topic").Should().BeTrue("future messages stay blocked");
        published.Should().Equal("ok/topic", "boom/topic");
    }
}
