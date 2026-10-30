using Microsoft.Extensions.Logging;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Services.Configuration;
using MqttProbe.Core.Services.Mqtt;

namespace MqttProbe.Core.Tests.Services.Mqtt;

[TestFixture]
public sealed class TopicExcludeBranchTests
{
    private const string Group = "spBv1.0/plant1";

    private IConnectionSettings _settings = null!;
    private SessionState _session = null!;
    private Connection _connection = null!;
    private TopicExcludeService _service = null!;

    [SetUp]
    public void Setup()
    {
        _settings = Substitute.For<IConnectionSettings>();
        _connection = new Connection { Id = Guid.NewGuid(), Name = "Test" };
        _session = new SessionState { SelectedConnection = _connection };
        _settings.Connections.Returns([]);
        _service = new TopicExcludeService(Substitute.For<ILogger<TopicExcludeService>>(), _settings, _session);
    }

    [TearDown]
    public void TearDown() => _service.Dispose();

    [Test]
    public async Task AddBranches_RawBranchCoversItselfAndDescendantsButNotSiblings()
    {
        var result = await _service.AddBranches([Raw("sensors/temp")], _service.CaptureConnectionToken());

        result.Results.Single().Outcome.Should().Be(TopicExcludeAddOutcome.Added);
        _service.IsExcluded("sensors/temp").Should().BeTrue();
        _service.IsExcluded("sensors/temp/room1").Should().BeTrue();
        _service.IsExcluded("sensors/temperature").Should().BeFalse();
        _service.IsExcluded("sensors/temp2").Should().BeFalse();
        _service.TopicExcludes.Should().Equal("sensors/temp/#");
    }

    [Test]
    public async Task AddBranches_EmptyRawPathPersistsSlashHashAndOnlyCoversLeadingEmptyLevels()
    {
        var result = await _service.AddBranches([Raw("")], _service.CaptureConnectionToken());

        result.Results.Single().Outcome.Should().Be(TopicExcludeAddOutcome.Added);
        _service.TopicExcludes.Should().Equal("/#");
        _service.IsExcluded("/").Should().BeTrue();
        _service.IsExcluded("/sensors").Should().BeTrue();
        _service.IsExcluded("sensors").Should().BeFalse();
        _service.IsExcluded("$SYS/broker/uptime").Should().BeFalse();
    }

    [Test]
    public async Task AddBranches_RawPathKeepsLiteralSpellingAndRejectsWildcardCharacters()
    {
        await _service.AddBranches([Raw("a//b")], _service.CaptureConnectionToken());

        _service.TopicExcludes.Should().Equal("a//b/#");
        _service.IsExcluded("a//b").Should().BeTrue();
        _service.IsExcluded("a/b").Should().BeFalse();

        var invalid = await _service.AddBranches(
            [Raw("a/+/b"), Raw("a/#"), new TopicBranchTarget(TopicBranchKind.Raw)],
            _service.CaptureConnectionToken());

        invalid.Results.Should().OnlyContain(item => item.Outcome == TopicExcludeAddOutcome.Invalid);
        _service.TopicExcludes.Should().Equal("a//b/#");
    }

    [Test]
    public async Task AddBranches_NodeScopeBlocksNodeAndFutureDeviceTopics()
    {
        await _service.AddBranches([Node("node1")], _service.CaptureConnectionToken());

        _service.IsExcluded("spBv1.0/plant1/NBIRTH/node1").Should().BeTrue();
        _service.IsExcluded("spBv1.0/plant1/NDATA/node1").Should().BeTrue();
        _service.IsExcluded("spBv1.0/plant1/DBIRTH/node1/dev1").Should().BeTrue();
        _service.IsExcluded("spBv1.0/plant1/DDATA/node1/dev1").Should().BeTrue();
        _service.IsExcluded("spBv1.0/plant1/NFUTURE/node1").Should().BeTrue("future node message types stay covered");
        _service.IsExcluded("spBv1.0/plant1/DFUTURE/node1/dev9").Should().BeTrue("future device types stay covered");
        _service.IsExcluded("spBv1.0/plant1/NDATA/node2").Should().BeFalse();
        _service.IsExcluded("spBv1.0/plant2/NDATA/node1").Should().BeFalse();
        _service.IsExcluded("spBv1.0/plant1/STATE/host").Should().BeFalse();
        _service.TopicExcludes.Should().BeEmpty();
        _service.SparkplugExcludes.Should().ContainSingle()
            .Which.Should().Be(new SparkplugTopicExclude(
                new SparkplugSelectionScope(SparkplugSelectionKind.Node, Group, "node1")));
    }

    [Test]
    public async Task AddBranches_DeviceScopeBlocksFutureMessageTypesForThatDeviceOnly()
    {
        await _service.AddBranches([Device("node1", "dev1")], _service.CaptureConnectionToken());

        _service.IsExcluded("spBv1.0/plant1/DBIRTH/node1/dev1").Should().BeTrue();
        _service.IsExcluded("spBv1.0/plant1/DDATA/node1/dev1").Should().BeTrue();
        _service.IsExcluded("spBv1.0/plant1/DCMD/node1/dev1").Should().BeTrue();
        _service.IsExcluded("spBv1.0/plant1/DFUTURE/node1/dev1").Should().BeTrue(
            "future device message types stay covered");
        _service.IsExcluded("spBv1.0/plant1/DDATA/node1/dev2").Should().BeFalse();
        _service.IsExcluded("spBv1.0/plant1/NDATA/node1").Should().BeFalse();
    }

    [Test]
    public async Task AddBranches_MessageTypeScopeBlocksOnlyThatVerb()
    {
        await _service.AddBranches(
            [Node("node1", "NDATA"), Device("node1", "dev1", "DDATA")],
            _service.CaptureConnectionToken());

        _service.IsExcluded("spBv1.0/plant1/NDATA/node1").Should().BeTrue();
        _service.IsExcluded("spBv1.0/plant1/NBIRTH/node1").Should().BeFalse();
        _service.IsExcluded("spBv1.0/plant1/DDATA/node1/dev1").Should().BeTrue();
        _service.IsExcluded("spBv1.0/plant1/DBIRTH/node1/dev1").Should().BeFalse();
        _service.IsExcluded("spBv1.0/plant1/DDATA/node1/dev2").Should().BeFalse();
    }

    [Test]
    public async Task AddBranches_RejectsInvalidSparkplugScopes()
    {
        var result = await _service.AddBranches(
        [
            new TopicBranchTarget(TopicBranchKind.Sparkplug),
            new TopicBranchTarget(TopicBranchKind.Sparkplug,
                Scope: new SparkplugSelectionScope(SparkplugSelectionKind.Node, "other/plant1", "node1")),
            new TopicBranchTarget(TopicBranchKind.Sparkplug,
                Scope: new SparkplugSelectionScope(SparkplugSelectionKind.Node, Group, "node1", "dev1")),
            new TopicBranchTarget(TopicBranchKind.Sparkplug,
                Scope: new SparkplugSelectionScope(SparkplugSelectionKind.Device, Group, "node1")),
            new TopicBranchTarget(TopicBranchKind.Sparkplug,
                Scope: new SparkplugSelectionScope(SparkplugSelectionKind.Node, Group, "node1"),
                MessageType: "DDATA"),
            new TopicBranchTarget(TopicBranchKind.Sparkplug,
                Scope: new SparkplugSelectionScope(SparkplugSelectionKind.Node, Group, "node1"),
                MessageType: "NDATA/x")
        ], _service.CaptureConnectionToken());

        result.Results.Should().OnlyContain(item => item.Outcome == TopicExcludeAddOutcome.Invalid);
        _service.SparkplugExcludes.Should().BeEmpty();
    }

    [Test]
    public async Task AddBranches_ReportsDuplicatesOncePerOriginalTarget()
    {
        var duplicate = Node("node1");
        var result = await _service.AddBranches(
            [Raw("sensors/temp"), Raw("sensors/temp"), duplicate, duplicate],
            _service.CaptureConnectionToken());

        result.Results.Select(item => item.Outcome).Should().Equal(
            TopicExcludeAddOutcome.Added,
            TopicExcludeAddOutcome.AlreadyExcluded,
            TopicExcludeAddOutcome.Added,
            TopicExcludeAddOutcome.AlreadyExcluded);
        result.Results[1].Target.Should().Be(Raw("sensors/temp"));
        result.Results[3].Target.Should().Be(duplicate);
        result.AllSucceeded.Should().BeTrue("already excluded is the requested end state");
        _service.TopicExcludes.Should().Equal("sensors/temp/#");
        _service.SparkplugExcludes.Should().ContainSingle();
    }

    [Test]
    public async Task AddBranches_SharesTheFiveHundredEntryCapAcrossRawAndSparkplugRules()
    {
        UseConnection(new Connection
        {
            Id = Guid.NewGuid(),
            TopicExcludes = [.. Enumerable.Range(0, 499).Select(index => $"topic/{index}")]
        });

        var result = await _service.AddBranches(
            [Node("node1"), Node("node2")], _service.CaptureConnectionToken());

        result.Results.Select(item => item.Outcome).Should().Equal(
            TopicExcludeAddOutcome.Added, TopicExcludeAddOutcome.LimitReached);
        _service.TopicExcludes.Should().HaveCount(499);
        _service.SparkplugExcludes.Should().ContainSingle();
    }

    [Test]
    public async Task AddBranches_PersistsTypedRulesAndReloadsThemWithoutFlattening()
    {
        var result = await _service.AddBranches(
            [Raw("sensors/temp"), Node("node1"), Device("node1", "dev1", "DDATA")],
            _service.CaptureConnectionToken());

        result.AddedTargets.Should().HaveCount(3);
        _connection.TopicExcludes.Should().Equal("sensors/temp/#");
        _connection.SparkplugTopicExcludes.Should().Equal(
            new SparkplugTopicExclude(new SparkplugSelectionScope(SparkplugSelectionKind.Node, Group, "node1")),
            new SparkplugTopicExclude(
                new SparkplugSelectionScope(SparkplugSelectionKind.Device, Group, "node1", "dev1"), "DDATA"));

        using var restored = new TopicExcludeService(
            Substitute.For<ILogger<TopicExcludeService>>(), _settings, _session);
        restored.TopicExcludes.Should().Equal("sensors/temp/#");
        restored.SparkplugExcludes.Should().Equal(_connection.SparkplugTopicExcludes);
        restored.IsExcluded("spBv1.0/plant1/DDATA/node1/dev1").Should().BeTrue();
        restored.IsExcluded("sensors/temp/room1").Should().BeTrue();
    }

    [Test]
    public async Task RemoveBranches_RemovesTypedRulesAndKeepsRawFilters()
    {
        await _service.AddBranches(
            [Raw("sensors/temp"), Node("node1")], _service.CaptureConnectionToken());

        var result = await _service.RemoveBranches([Node("node1")]);

        result.IsValid.Should().BeTrue();
        _service.SparkplugExcludes.Should().BeEmpty();
        _service.TopicExcludes.Should().Equal("sensors/temp/#");
        _connection.SparkplugTopicExcludes.Should().BeEmpty();
        _service.IsExcluded("spBv1.0/plant1/NDATA/node1").Should().BeFalse();
        _service.IsExcluded("sensors/temp").Should().BeTrue();
    }

    [Test]
    public async Task AddBranches_ReportsPartialPersistenceFailurePerTarget()
    {
        _settings.AddConnectionAsync(Arg.Any<Connection>()).Returns(call =>
            call.Arg<Connection>().SparkplugTopicExcludes.Any(rule => rule.Scope.NodeId == "node2")
                ? Task.FromException(new IOException("disk full"))
                : Task.CompletedTask);

        var result = await _service.AddBranches(
            [Node("node1"), Node("node2"), Node("node3")], _service.CaptureConnectionToken());

        result.Results.Select(item => item.Outcome).Should().Equal(
            TopicExcludeAddOutcome.Added,
            TopicExcludeAddOutcome.Failed,
            TopicExcludeAddOutcome.Added);
        result.Results[1].Feedback!.Message.Should().Contain("Failed");
        result.AddedTargets.Should().Equal(Node("node1"), Node("node3"));
        _service.SparkplugExcludes.Select(rule => rule.Scope.NodeId).Should().Equal("node1", "node3");
    }

    [Test]
    public async Task AddBranches_WhenAPurgeThrows_ReportsAppliedWithPurgeFailureAndKeepsTheRuleActive()
    {
        _service.PurgeExcludedSparkplug += rule =>
        {
            if (rule.Scope.NodeId == "node1")
                throw new InvalidOperationException("purge boom");
        };

        var result = await _service.AddBranches([Node("node1")], _service.CaptureConnectionToken());

        result.Results.Single().Outcome.Should().Be(TopicExcludeAddOutcome.AppliedWithPurgeFailure);
        result.AppliedTargets.Should().Equal(Node("node1"));
        _service.IsExcluded("spBv1.0/plant1/DDATA/node1/dev1").Should().BeTrue();
        _connection.SparkplugTopicExcludes.Should().ContainSingle();
    }

    [Test]
    public async Task AddBranches_RaisesTypedPurgeAndObserverEventsWithoutStringFilters()
    {
        var purged = new List<SparkplugTopicExclude>();
        var excluded = new List<SparkplugTopicExclude>();
        var stringEvents = new List<string>();
        _service.PurgeExcludedSparkplug += purged.Add;
        _service.SparkplugExcluded += excluded.Add;
        _service.PurgeExcludedTopic += stringEvents.Add;
        _service.TopicExcluded += stringEvents.Add;

        await _service.AddBranches(
            [Node("node1"), Device("node1", "dev1", "DDATA")], _service.CaptureConnectionToken());

        purged.Should().Equal(
            new SparkplugTopicExclude(new SparkplugSelectionScope(
                SparkplugSelectionKind.Node, Group, "node1")),
            new SparkplugTopicExclude(new SparkplugSelectionScope(
                SparkplugSelectionKind.Device, Group, "node1", "dev1"), "DDATA"));
        excluded.Should().Equal(purged);
        stringEvents.Should().BeEmpty("typed rules never masquerade as MQTT filters");
    }

    [Test]
    public async Task AddBranches_RejectsAConnectionSwitchBeforeExecution()
    {
        var token = _service.CaptureConnectionToken();
        _session.SelectedConnection = new Connection { Id = Guid.NewGuid(), Name = "Next" };

        var result = await _service.AddBranches([Raw("sensors/temp")], token);

        result.Results.Single().Outcome.Should().Be(TopicExcludeAddOutcome.ConnectionChanged);
        _service.TopicExcludes.Should().BeEmpty();
        await _settings.DidNotReceive().AddConnectionAsync(Arg.Any<Connection>());
    }

    [Test]
    public async Task AddBranches_RejectsARoundTripToTheOriginalConnection()
    {
        var token = _service.CaptureConnectionToken();
        _session.SelectedConnection = new Connection { Id = Guid.NewGuid(), Name = "Next" };
        _session.SelectedConnection = _connection;

        var result = await _service.AddBranches([Raw("sensors/temp")], token);

        result.Results.Single().Outcome.Should().Be(TopicExcludeAddOutcome.ConnectionChanged);
        _service.TopicExcludes.Should().BeEmpty();
        _connection.TopicExcludes.Should().BeEmpty();
        await _settings.DidNotReceive().AddConnectionAsync(Arg.Any<Connection>());
    }

    [Test]
    public async Task AddBranches_RejectsAConnectionRoundTripTheServiceNeverSaw()
    {
        var original = new Connection { Id = Guid.NewGuid(), Name = "A" };
        var inner = new SessionState { SelectedConnection = original };
        var session = Substitute.For<ISessionState>();
        session.SelectedConnection.Returns(_ => inner.SelectedConnection);
        session.CaptureSelectedConnection().Returns(_ => inner.CaptureSelectedConnection());
        using var service = new TopicExcludeService(
            Substitute.For<ILogger<TopicExcludeService>>(), _settings, session);
        var token = service.CaptureConnectionToken();

        inner.SelectedConnection = new Connection { Id = Guid.NewGuid(), Name = "B" };
        inner.SelectedConnection = original;

        var result = await service.AddBranches([Raw("sensors/temp")], token);

        result.Results.Single().Outcome.Should().Be(TopicExcludeAddOutcome.ConnectionChanged);
        service.TopicExcludes.Should().BeEmpty();
        await _settings.DidNotReceive().AddConnectionAsync(Arg.Any<Connection>());
    }

    [Test]
    public void TryExecuteForConnection_RejectsAStaleSelectionRevision()
    {
        var token = _service.CaptureConnectionToken();
        var stale = token with { SelectionRevision = token.SelectionRevision + 1 };

        var executed = _service.TryExecuteForConnection(stale, () => 42, out var result);

        executed.Should().BeFalse();
        result.Should().Be(0);
    }

    [Test]
    public async Task RemoveBranches_QueuedRemovalDoesNotWriteAcrossAConnectionSwitch()
    {
        UseConnection(new Connection { Id = Guid.NewGuid(), TopicExcludes = ["old/#"] });
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _settings.AddConnectionAsync(Arg.Any<Connection>()).Returns(async call =>
        {
            if (!call.Arg<Connection>().TopicExcludes.Contains("blocked/topic"))
                return;

            entered.TrySetResult();
            await release.Task;
        });

        var addTask = _service.Add("blocked/topic");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var removeTask = _service.RemoveBranches([Raw("old")]);
        var next = new Connection { Id = Guid.NewGuid(), Name = "Next" };
        _session.SelectedConnection = next;
        release.SetResult();

        var result = await removeTask.WaitAsync(TimeSpan.FromSeconds(10));
        result.IsValid.Should().BeFalse("the queued removal started on the old connection");
        next.TopicExcludes.Should().BeEmpty();
        await _settings.DidNotReceive().AddConnectionAsync(
            Arg.Is<Connection>(connection => connection.Id == next.Id));
        await addTask.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Test]
    public async Task RemoveBranches_FromASelectedConnectionChangedSubscriberBeforeTheServiceLoads_DoesNotCopyTheOldList()
    {
        var settings = Substitute.For<IConnectionSettings>();
        settings.Connections.Returns([]);
        var original = new Connection { Id = Guid.NewGuid(), TopicExcludes = ["old/#"] };
        var next = new Connection { Id = Guid.NewGuid(), Name = "Next" };
        var session = new SessionState { SelectedConnection = original };
        Task<TopicExcludeOperationResult>? removal = null;
        TopicExcludeService? service = null;
        session.SelectedConnectionChanged += _ => removal = service!.RemoveBranches([Raw("old")]);
        service = new TopicExcludeService(
            Substitute.For<ILogger<TopicExcludeService>>(), settings, session);

        try
        {
            session.SelectedConnection = next;
            var result = await removal!.WaitAsync(TimeSpan.FromSeconds(10));

            result.IsValid.Should().BeFalse("the service had not loaded the new connection yet");
            next.TopicExcludes.Should().BeEmpty();
            await settings.DidNotReceive().AddConnectionAsync(
                Arg.Is<Connection>(connection => connection.Id == next.Id));
        }
        finally
        {
            service.Dispose();
        }
    }

    private void UseConnection(Connection connection)
    {
        _connection = connection;
        _session.SelectedConnection = connection;
    }

    private static TopicBranchTarget Raw(string path) => new(TopicBranchKind.Raw, path);

    private static TopicBranchTarget Node(string nodeId, string? messageType = null) =>
        new(TopicBranchKind.Sparkplug,
            Scope: new SparkplugSelectionScope(SparkplugSelectionKind.Node, Group, nodeId),
            MessageType: messageType);

    private static TopicBranchTarget Device(string nodeId, string deviceId, string? messageType = null) =>
        new(TopicBranchKind.Sparkplug,
            Scope: new SparkplugSelectionScope(SparkplugSelectionKind.Device, Group, nodeId, deviceId),
            MessageType: messageType);
}
