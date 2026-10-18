using AngleSharp.Dom;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MQTTnet;
using MqttProbe.Core.Models.Configuration;
using MqttProbe.Core.Services.Configuration;
using MqttProbe.Core.Services.Metrics;
using MqttProbe.Core.Services.Mqtt;
using MqttProbe.Core.Services.Plugins.BuiltIn;
using MqttProbe.Core.Services.Plugins.Pipeline;
using MqttProbe.Core.Services.Plugins.Registry;
using MqttProbe.UI.Components.Layout;
using MqttProbe.UI.Tests.TestHelpers;

namespace MqttProbe.UI.Tests.Components.Browser;

// The tree no longer shows a separate message-presence marker. Reset and purge still have to
// update the rows, and ordinary topics stay green circles apart from retained, storage and
// connection cues.
[TestFixture]
public sealed class TopicBrowserNoDirectMarkerTests : BunitTestContext
{
    private static readonly TimeSpan _refreshBound = TimeSpan.FromSeconds(10);

    private MessageStoreManager _manager = null!;
    private IMqttManagedClient _client = null!;
    private Func<MqttApplicationMessageReceivedEventArgs, Task>? _handler;
    private ITopicExcludeService _excludes = null!;

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

        Services.AddSingleton<IMessageStoreManager>(_manager);
        Services.AddUiSettings(SettingsSubstituteRegistration.CreateUiSettings(new UiPreferences()));
    }

    [TearDown]
    public void DisposeStore()
    {
        _manager.Dispose();
        _client.Dispose();
    }

    private async Task PublishAsync(string topic, string payload, bool retained = false)
    {
        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithRetainFlag(retained)
            .Build();
        var packet = new MQTTnet.Packets.MqttPublishPacket { Topic = topic };
        await _handler!(new MqttApplicationMessageReceivedEventArgs("test-client", message, packet, null));
    }

    private IRenderedComponent<TopicBrowser> RenderExpanded()
    {
        var cut = Render<TopicBrowser>();
        EnsureMudProviders();
        cut.FindAll(".mud-toolbar button")[0].Click();
        return cut;
    }

    private static IElement RowWith(IRenderedComponent<TopicBrowser> cut, string label) =>
        cut.FindAll(".topic-tree-row")
            .Single(row => row.QuerySelector(".flex-grow-1")!.TextContent.Trim() == label);

    [Test]
    public async Task ResetClearsTheTreeAndNoDirectMarkerEverAppears()
    {
        await PublishAsync("sensors/temp", "20");
        await PublishAsync("sensors/humidity", "50");

        var cut = RenderExpanded();
        cut.FindAll(".topic-direct-badge").Should().BeEmpty(
            "the separate message-presence marker is removed");
        RowWith(cut, "temp").QuerySelector(".topic-node-icon.topic-role--leaf")
            .Should().NotBeNull("a populated topic is still a green leaf circle");

        await cut.InvokeAsync(_manager.ClearAllMessages);

        cut.WaitForAssertion(
            () => cut.FindAll(".topic-tree-row").Should().BeEmpty(),
            _refreshBound);
        cut.FindAll(".topic-direct-badge").Should().BeEmpty();
    }

    [Test]
    public async Task PurgingATopicsMessagesLeavesItsBranchChildAndNoMarker()
    {
        await PublishAsync("sensors/temp", "20");
        await PublishAsync("sensors/temp/room1", "21");

        var cut = RenderExpanded();
        RowWith(cut, "temp").QuerySelector(".topic-node-icon.topic-role--branch")
            .Should().NotBeNull("the populated branch is a blue folder");
        RowWith(cut, "temp").QuerySelector(".topic-direct-badge").Should().BeNull();

        // The exclude purge Core performs for a matching topic filter.
        _excludes.PurgeExcludedTopic += Raise.Event<Action<string>>("sensors/temp");

        cut.WaitForAssertion(
            () => RowWith(cut, "temp").QuerySelector(".topic-node-icon.topic-role--branch")
                .Should().NotBeNull("the branch survives the purge of its own messages"),
            _refreshBound);
        RowWith(cut, "room1").QuerySelector(".topic-node-icon.topic-role--leaf")
            .Should().NotBeNull("the topic below the purge is still an ordinary green circle");
        cut.FindAll(".topic-direct-badge").Should().BeEmpty();
        cut.Markup.Should().Contain("room1", "the branch survives the purge of its own message");
    }

    [Test]
    public async Task RetainedMessagesProduceNoMarkerAndKeepTheirOwnCues()
    {
        await PublishAsync("sensors/temp", "20", retained: true);

        var cut = RenderExpanded();

        RowWith(cut, "temp").QuerySelector(".topic-node-icon.topic-role--leaf").Should().NotBeNull();
        RowWith(cut, "temp").QuerySelector(".topic-direct-badge").Should().BeNull();
        cut.Markup.Should().NotContain(LucideIcons.Bookmark, "retained status is a separate cue");
        cut.Markup.Should().NotContain(LucideIcons.Database, "the storage metaphor is retired from the tree");
        cut.Markup.Should().NotContain("spb-status-dot", "connection health belongs to the Sparkplug views");
    }
}
