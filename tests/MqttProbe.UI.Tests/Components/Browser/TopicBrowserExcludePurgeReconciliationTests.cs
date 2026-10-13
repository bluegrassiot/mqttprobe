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
    public async Task PurgingTheResidue_ClearsASelectionOnThePrefixTheTreeWasShowing()
    {
        await PublishAsync("spBv1.0/plant1/NDATA/node1", "tip-value");
        await PublishAsync(Residue, "residue-value");
        _preferences.GroupSparkplugTopicsByNodeAndDevice = true;

        _manager.SelectTopic(Aggregate);
        _manager.GetSelectedTopicState().FullTopic.Should().Be(Aggregate,
            "the residual prefix has a row while the malformed tip exists");

        var cut = Render<TopicBrowser>();
        EnsureMudProviders();
        await cut.InvokeAsync(() => cut.FindAll(".mud-toolbar button")[0].Click());
        cut.WaitForAssertion(() => cut.FindAll(".topic-tree-row--selected").Should().ContainSingle());

        // The exclude purge Core performs for a matching topic filter.
        _excludes.PurgeExcludedTopic += Raise.Event<Action<string>>("spBv1.0/plant1/NDATA/ghosttip/#");

        // The refresh is timer-driven (one tick per second), so bound both waits above that period.
        var refreshBound = TimeSpan.FromSeconds(10);
        cut.WaitForAssertion(() => _manager.GetSelectedTopicState().FullTopic.Should().BeNull(), refreshBound);
        cut.WaitForAssertion(() => cut.FindAll(".topic-tree-row--selected").Should().BeEmpty(), refreshBound);
        _manager.GetTopicTreeSnapshot().Roots.Should().NotBeEmpty();
        cut.Markup.Should().Contain("spBv1.0/plant1/NDATA/node1",
            "the valid tip under the same verb survives the purge and stays grouped");
    }

    [Test]
    public async Task AResidueThatReturnsBeforeTheClear_LeavesTheSelectionAndItsTokenUntouched()
    {
        await PublishAsync("spBv1.0/plant1/NDATA/node1", "tip-value");
        await PublishAsync(Residue, "residue-value");
        _preferences.GroupSparkplugTopicsByNodeAndDevice = true;
        _manager.SelectTopic(Aggregate);
        var before = _manager.GetSelectedTopicState();
        before.FullTopic.Should().Be(Aggregate,
            "the residual prefix has a row while the malformed tip exists");

        // The exclude purge the component must react to, and then the malformed tip back in the
        // store before the component's conditional clear arrives: a newer tree, the same token.
        _excludes.PurgeExcludedTopic += Raise.Event<Action<string>>("spBv1.0/plant1/NDATA/ghosttip/#");
        var purgedVerdict = _manager.GetTopicTreeSnapshot();
        await PublishAsync(Residue, "residue-again");
        _manager.GetVersion().Should().BeGreaterThan(purgedVerdict.Version,
            "the store moved on since the verdict was taken");

        var forwarding = new StaleVerdictManager(_manager, purgedVerdict);
        Services.AddSingleton<IMessageStoreManager>(forwarding);
        var cut = Render<TopicBrowser>();
        EnsureMudProviders();

        cut.WaitForAssertion(() => forwarding.ClearAttempts.Should().Be(1,
            "the component asks once, then the restored residue is always represented"));
        forwarding.Cleared.Should().Be(0, "the tree version no longer matched the verdict");
        var after = _manager.GetSelectedTopicState();
        after.FullTopic.Should().Be(Aggregate, "the rejected clear left the selection alone");
        after.Token.StoreId.Should().Be(before.Token.StoreId);
        after.Token.Generation.Should().Be(before.Token.Generation,
            "a rejected clear must not move the selection on");

        await cut.InvokeAsync(() => cut.FindAll(".mud-toolbar button")[0].Click());
        cut.WaitForAssertion(
            () => cut.FindAll(".topic-tree-row--selected").Should().ContainSingle(),
            TimeSpan.FromSeconds(10));
    }

    // Serves the purged verdict the component's own read took, then forwards the conditional
    // clear to the real manager, whose tree has since moved on with the returned residue.
    private sealed class StaleVerdictManager(IMessageStoreManager inner, TopicTreeSnapshot verdict)
        : IMessageStoreManager
    {
        private bool _verdictPending = true;

        public int ClearAttempts { get; private set; }

        public int Cleared { get; private set; }

        public TopicTreeSnapshot GetTopicTreeSnapshot()
        {
            if (!_verdictPending)
                return inner.GetTopicTreeSnapshot();

            _verdictPending = false;
            return verdict;
        }

        public bool TryClearSelection(long treeVersion, SelectedTopicState expectedSelection)
        {
            ClearAttempts++;
            var cleared = inner.TryClearSelection(treeVersion, expectedSelection);
            if (cleared)
                Cleared++;

            return cleared;
        }

        public int RootTopicCount => inner.RootTopicCount;

        public SelectedTopicState GetSelectedTopicState() => inner.GetSelectedTopicState();

        public void SelectTopic(string? fullTopic) => inner.SelectTopic(fullTopic);

        public Task<SelectedMessagesSnapshot?> GetSelectedMessagesAsync(
            SelectedTopicToken expectedSelection, int limit) =>
            inner.GetSelectedMessagesAsync(expectedSelection, limit);

        public bool IsListening => inner.IsListening;

        public int MaxStoredMessages => inner.MaxStoredMessages;

        public int MaxTopicNodes => inner.MaxTopicNodes;

        public int TotalStoredMessages => inner.TotalStoredMessages;

        public int TopicNodeCount => inner.TopicNodeCount;

        public long DroppedMessageCount => inner.DroppedMessageCount;

        public long ExcludedMessageCount => inner.ExcludedMessageCount;

        public long GetVersion() => inner.GetVersion();

        public Task ClearAllMessages() => inner.ClearAllMessages();

        public Task Stop() => inner.Stop();

        public Task Start() => inner.Start();

        public event Func<MqttMessage, Task>? MessageReceived
        {
            add => inner.MessageReceived += value;
            remove => inner.MessageReceived -= value;
        }

        // The fixture owns the real manager's lifetime; this wrapper adds no resources of its own.
        public void Dispose() => GC.SuppressFinalize(this);
    }
}
