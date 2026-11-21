using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Services.Mqtt;

namespace MqttProbe.Core.Tests.Services.Mqtt;

public sealed partial class TopicExcludeBranchTests
{
    [Test]
    public async Task AddBranches_RawBranchSavedDuringAConnectionSwitch_ReportsSavedButNotApplied()
    {
        var next = new Connection { Id = Guid.NewGuid(), Name = "Next" };
        var purged = new List<string>();
        var excluded = new List<string>();
        _service.PurgeExcludedTopic += purged.Add;
        _service.TopicExcluded += excluded.Add;

        var result = await AddBranchesWhileSwitchingDuringPersistence(next, () => [Raw("sensors/temp")]);

        var item = result.Results.Single();
        item.Outcome.Should().Be(TopicExcludeAddOutcome.SavedButNotApplied);
        item.Feedback!.Severity.Should().Be(UserNotificationSeverity.Warning);
        item.Feedback!.Message.Should().Be(
            "Connection changed while saving; the exclusion was saved but is not applied to the active connection");
        result.AllSucceeded.Should().BeFalse();
        result.AddedTargets.Should().BeEmpty();
        result.AppliedTargets.Should().BeEmpty();

        _connection.TopicExcludes.Should().Equal("sensors/temp/#");
        next.TopicExcludes.Should().BeEmpty();
        next.SparkplugTopicExcludes.Should().BeEmpty();
        _service.TopicExcludes.Should().BeEmpty();
        _service.SparkplugExcludes.Should().BeEmpty();
        _service.IsExcluded("sensors/temp/room1").Should().BeFalse();
        purged.Should().BeEmpty();
        excluded.Should().BeEmpty();
        await _settings.Received(1).AddConnectionAsync(
            Arg.Is<Connection>(connection => connection.Id == _connection.Id));
        await _settings.DidNotReceive().AddConnectionAsync(
            Arg.Is<Connection>(connection => connection.Id == next.Id));
    }

    [Test]
    public async Task AddBranches_SparkplugBranchSavedDuringAConnectionSwitch_ReportsSavedButNotApplied()
    {
        var next = new Connection { Id = Guid.NewGuid(), Name = "Next" };
        var purged = new List<SparkplugTopicExclude>();
        var excluded = new List<SparkplugTopicExclude>();
        _service.PurgeExcludedSparkplug += purged.Add;
        _service.SparkplugExcluded += excluded.Add;

        var result = await AddBranchesWhileSwitchingDuringPersistence(next, () => [Node("node1")]);

        var item = result.Results.Single();
        item.Outcome.Should().Be(TopicExcludeAddOutcome.SavedButNotApplied);
        item.Feedback!.Severity.Should().Be(UserNotificationSeverity.Warning);
        item.Feedback!.Message.Should().Be(
            "Connection changed while saving; the exclusion was saved but is not applied to the active connection");
        result.AllSucceeded.Should().BeFalse();
        result.AddedTargets.Should().BeEmpty();
        result.AppliedTargets.Should().BeEmpty();

        _connection.SparkplugTopicExcludes.Should().ContainSingle().Which.Should().Be(
            new SparkplugTopicExclude(
                new SparkplugSelectionScope(SparkplugSelectionKind.Node, Group, "node1")));
        _connection.TopicExcludes.Should().BeEmpty();
        next.TopicExcludes.Should().BeEmpty();
        next.SparkplugTopicExcludes.Should().BeEmpty();
        _service.TopicExcludes.Should().BeEmpty();
        _service.SparkplugExcludes.Should().BeEmpty();
        _service.IsExcluded("spBv1.0/plant1/NDATA/node1").Should().BeFalse();
        purged.Should().BeEmpty();
        excluded.Should().BeEmpty();
        await _settings.Received(1).AddConnectionAsync(
            Arg.Is<Connection>(connection => connection.Id == _connection.Id));
        await _settings.DidNotReceive().AddConnectionAsync(
            Arg.Is<Connection>(connection => connection.Id == next.Id));
    }

    [Test]
    public async Task AddBranches_ConnectionSwitchesDuringTheFirstPersistence_ReportsSavedThenConnectionChanged()
    {
        var next = new Connection { Id = Guid.NewGuid(), Name = "Next" };

        var result = await AddBranchesWhileSwitchingDuringPersistence(
            next, () => [Raw("sensors/temp"), Node("node1")]);

        result.Results.Select(item => item.Outcome).Should().Equal(
            TopicExcludeAddOutcome.SavedButNotApplied,
            TopicExcludeAddOutcome.ConnectionChanged);
        result.Results[1].Feedback!.Message.Should().Be(
            "Active connection changed; topic exclusion was not applied");
        result.AllSucceeded.Should().BeFalse();
        result.AddedTargets.Should().BeEmpty();
        result.AppliedTargets.Should().BeEmpty();

        _connection.TopicExcludes.Should().Equal("sensors/temp/#");
        _connection.SparkplugTopicExcludes.Should().BeEmpty();
        next.TopicExcludes.Should().BeEmpty();
        next.SparkplugTopicExcludes.Should().BeEmpty();
        _service.TopicExcludes.Should().BeEmpty();
        _service.SparkplugExcludes.Should().BeEmpty();
        await _settings.Received(1).AddConnectionAsync(
            Arg.Is<Connection>(connection => connection.Id == _connection.Id));
        await _settings.DidNotReceive().AddConnectionAsync(
            Arg.Is<Connection>(connection => connection.Id == next.Id));
    }

    private async Task<TopicBranchExcludeBatchResult> AddBranchesWhileSwitchingDuringPersistence(
        Connection next, Func<IReadOnlyList<TopicBranchTarget>> targets)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _settings.AddConnectionAsync(Arg.Any<Connection>()).Returns(async _ =>
        {
            entered.TrySetResult();
            await release.Task;
        });

        var addTask = _service.AddBranches(targets(), _service.CaptureConnectionToken());
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            _session.SelectedConnection = next;
        }
        finally
        {
            release.SetResult();
        }

        return await addTask.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
