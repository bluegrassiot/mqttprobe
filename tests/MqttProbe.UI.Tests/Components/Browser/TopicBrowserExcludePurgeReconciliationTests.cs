using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MQTTnet;
using MqttProbe.Core.Models.Configuration;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Services.Configuration;
using MqttProbe.Core.Services.Metrics;
using MqttProbe.Core.Services.Mqtt;
using MqttProbe.Core.Services.Plugins.BuiltIn;
using MqttProbe.Core.Services.Plugins.Pipeline;
using MqttProbe.Core.Services.Plugins.Registry;
using MqttProbe.UI.Tests.TestHelpers;

namespace MqttProbe.UI.Tests.Components.Browser;

[TestFixture]
public sealed class TopicBrowserExcludePurgeReconciliationTests : BunitTestContext
{
    private const string Aggregate = "spBv1.0/plant1/NDATA";
    private const string Residue = "spBv1.0/plant1/NDATA/ghosttip/leaf";

    private MessageStoreManager _manager = null!;
    private IMqttManagedClient _client = null!;
    private Func<MqttApplicationMessageReceivedEventArgs, Task>? _handler;
    private ITopicExcludeService _excludes = null!;
    private IUiSettings _uiSettings = null!;
    private readonly UiPreferences _preferences = new();

    [SetUp]
    public async Task SetupStore()
    {
        _client = Substitute.For<IMqttManagedClient>();
        _client.When(x => x.ApplicationMessageReceivedAsync +=
                Arg.Any<Func<MqttApplicationMessageReceivedEventArgs, Task>>())
            .Do(call => _handler = call.Arg<Func<MqttApplicationMessageReceivedEventArgs, Task>>());

        var performance = Substitute.For<IPerformanceSettings>();
        performance.Performance.Returns(new PerformanceSettings { MaxStoredMessages = 10_000, MaxTopicNodes = 10_000 });
        var sparkplug = Substitute.For<ISparkplugSettings>();
        sparkplug.Sparkplug.Returns(new SparkplugSettings());

        var builder = new PluginRegistryBuilder();
        BuiltInPluginRegistration.RegisterBuiltIns(builder);
        var pipeline = new PayloadPipeline(builder.Build(), NullLogger<PayloadPipeline>.Instance);

        _excludes = Substitute.For<ITopicExcludeService>();
        _manager = new MessageStoreManager(_client, NullLogger<MessageStoreManager>.Instance, performance,
            Substitute.For<IUxMetricsService>(), pipeline, sparkplug,
            topologyService: null, commandService: null, topicExcludeService: _excludes);
        await _manager.Start();

        _uiSettings = SettingsSubstituteRegistration.CreateUiSettings(_preferences);
        Services.AddSingleton<IMessageStoreManager>(_manager);
        Services.AddUiSettings(_uiSettings);
    }

    [TearDown]
    public void DisposeStore()
    {
        _manager.Dispose();
        _client.Dispose();
    }

    private async Task PublishAsync(string topic, string payload)
    {
        var message = new MqttApplicationMessageBuilder().WithTopic(topic).WithPayload(payload).Build();
        var packet = new MQTTnet.Packets.MqttPublishPacket { Topic = topic };
        await _handler!(new MqttApplicationMessageReceivedEventArgs("test-client", message, packet, null));
    }

    [Test]
    public async Task PurgingTheResidue_KeepsTheCheckedVerbBranch()
    {
        await PublishAsync("spBv1.0/plant1/NDATA/node1", "tip-value");
        await PublishAsync(Residue, "residue-value");
        _preferences.GroupSparkplugTopicsByNodeAndDevice = true;

        _manager.SelectTopics(TopicSelection.ForBranches([
            new TopicBranchTarget(TopicBranchKind.Raw, Path: Aggregate)
        ]));

        var cut = Render<TopicBrowser>();
        EnsureMudProviders();
        await cut.InvokeAsync(() => cut.FindAll(".mud-toolbar button")[0].Click());
        cut.WaitForAssertion(() => SelectedPaths(cut)
            .Should().Contain("spBv1.0/plant1/NDATA/node1", "the tip under the checked verb is grouped"));

        // The exclude purge Core performs for a matching topic filter.
        _excludes.PurgeExcludedTopic += Raise.Event<Action<string>>("spBv1.0/plant1/NDATA/ghosttip/#");

        await cut.InvokeAsync(() => cut.Instance.RefreshAsync());
        _manager.GetSelectedTopicState().Branches.Select(branch => branch.Path)
            .Should().Equal(new[] { Aggregate },
                "the valid tip under the same verb still represents the branch");
        SelectedPaths(cut).Should().Contain("spBv1.0/plant1/NDATA/node1");
        _manager.GetTopicTreeSnapshot().Roots.Should().NotBeEmpty();
        cut.Markup.Should().Contain("spBv1.0/plant1/NDATA/node1",
            "the valid tip under the same verb survives the purge and stays grouped");
    }

    [Test]
    public async Task ARowThatReturnsBeforeTheReconcile_KeepsTheBranchChecked()
    {
        await PublishAsync("spBv1.0/plant1/NDATA/node1", "tip-value");
        await PublishAsync(Residue, "residue-value");
        _preferences.GroupSparkplugTopicsByNodeAndDevice = true;
        _manager.SelectTopics(TopicSelection.ForBranches([
            new TopicBranchTarget(TopicBranchKind.Raw, Path: Aggregate)
        ]));
        var before = _manager.GetSelectedTopicState();

        // Everything under the branch is purged, then the tip comes back before the component
        // reconciles: the rows exist again, so the branch must stay checked.
        _excludes.PurgeExcludedTopic += Raise.Event<Action<string>>("spBv1.0/plant1/NDATA/#");
        await PublishAsync("spBv1.0/plant1/NDATA/node1", "tip-again");

        var cut = Render<TopicBrowser>();
        EnsureMudProviders();

        await cut.InvokeAsync(() => cut.FindAll(".mud-toolbar button")[0].Click());
        cut.WaitForAssertion(() =>
        {
            _manager.GetSelectedTopicState().Branches.Select(branch => branch.Path).Should().Equal(Aggregate);
            SelectedPaths(cut).Should().Contain("spBv1.0/plant1/NDATA/node1");
        });
        _manager.GetSelectedTopicState().Token.Generation.Should().Be(before.Token.Generation,
            "reconciling against the returned rows must not move the selection on");
    }

    private static IReadOnlyList<string> SelectedPaths(IRenderedComponent<TopicBrowser> cut) =>
        [.. cut.FindAll(".topic-tree-row--selected")
            .Select(row => row.GetAttribute("data-topic-path") ?? string.Empty)];
}
