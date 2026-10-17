using AngleSharp.Dom;
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
public sealed class TopicBrowserGroupedSelectionTests : BunitTestContext
{
    private MessageStoreManager _manager = null!;
    private IMqttManagedClient _client = null!;
    private Func<MqttApplicationMessageReceivedEventArgs, Task>? _handler;
    private readonly UiPreferences _preferences = new();
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
            Substitute.For<IUxMetricsService>(), pipeline, sparkplug, topicExcludeService: _excludes);
        await _manager.Start();

        _preferences.GroupSparkplugTopicsByNodeAndDevice = true;
        Services.AddSingleton<IMessageStoreManager>(_manager);
        Services.AddUiSettings(SettingsSubstituteRegistration.CreateUiSettings(_preferences));
    }

    [TearDown]
    public void DisposeStore()
    {
        _manager.Dispose();
        _client.Dispose();
    }

    [Test]
    public async Task SelectingANodeRow_ShowsItsWholeSubtreeUnderAReadableName()
    {
        await PublishAsync("spBv1.0/plant1/NDATA/node1", "node-data");
        await PublishAsync("spBv1.0/plant1/DDATA/node1/dev1", "dev1-data");
        var cut = await RenderExpandedAsync();

        await cut.InvokeAsync(() => RowWith(cut, "node1").Click());

        var state = _manager.GetSelectedTopicState();
        state.Label.Should().Be("spBv1.0/plant1/node1", "the bar shows a name, not the projection id");
        state.Key.Should().StartWith("\0spb:node:");
        state.MessageCount.Should().Be(2);

        var messages = await _manager.GetSelectedMessagesAsync(state.Token, 10);
        messages!.Messages.Select(message => message.Payload)
            .Should().BeEquivalentTo(["node-data", "dev1-data"]);
    }

    [Test]
    public async Task SelectingADeviceRow_ExcludesTheNodesOwnTraffic()
    {
        await PublishAsync("spBv1.0/plant1/NBIRTH/node1", "birth");
        await PublishAsync("spBv1.0/plant1/DDATA/node1/dev1", "dev1-data");
        var cut = await RenderExpandedAsync();

        await cut.InvokeAsync(() => RowWith(cut, "dev1").Click());

        var state = _manager.GetSelectedTopicState();
        state.MessageCount.Should().Be(1);
        (await _manager.GetSelectedMessagesAsync(state.Token, 10))!.Messages
            .Should().ContainSingle().Which.Payload.Should().Be("dev1-data");
    }

    [Test]
    public async Task ACollapsedNodeSelection_AdoptsAVerbThatArrivesLater()
    {
        await PublishAsync("spBv1.0/plant1/NDATA/node1", "node-data");
        var cut = await RenderExpandedAsync();

        await cut.InvokeAsync(() => RowWith(cut, "node1").Click());
        var selected = _manager.GetSelectedTopicState();
        selected.MessageCount.Should().Be(1);

        var chevron = RowWith(cut, "node1").QuerySelector("button");
        chevron.Should().NotBeNull();
        await cut.InvokeAsync(() => chevron!.Click());
        cut.WaitForAssertion(() => cut.Markup.Should().NotContain("spBv1.0/plant1/NDATA/node1"));

        await PublishAsync("spBv1.0/plant1/NDEATH/node1", "death");

        var after = _manager.GetSelectedTopicState();
        after.Token.Generation.Should().Be(selected.Token.Generation, "the row did not change");
        after.MessageCount.Should().Be(2, "a verb under the selected node still belongs");
        cut.WaitForAssertion(() =>
        {
            var highlighted = cut.FindAll(".topic-tree-row--selected");
            highlighted.Should().ContainSingle();
            highlighted[0].TextContent.Should().Contain("node1");
        }, TimeSpan.FromSeconds(10));
    }

    [Test]
    public async Task UnrelatedTrafficAndReclickingTheSameRowKeepTheSelection()
    {
        await PublishAsync("spBv1.0/plant1/NDATA/node1", "node-data");
        var cut = await RenderExpandedAsync();

        await cut.InvokeAsync(() => RowWith(cut, "node1").Click());
        var selected = _manager.GetSelectedTopicState();

        await PublishAsync("spBv1.0/plant1/NDATA/node2", "unrelated");

        var afterUnrelated = _manager.GetSelectedTopicState();
        afterUnrelated.Token.Generation.Should().Be(selected.Token.Generation);
        afterUnrelated.MessageCount.Should().Be(1, "node2 is a sibling, not under node1");

        await cut.InvokeAsync(() => RowWith(cut, "node1").Click());

        _manager.GetSelectedTopicState().Token.Generation.Should().Be(selected.Token.Generation,
            "clicking the already-selected row must not restart the message list");
    }

    [Test]
    public async Task AStaleMessageTypeRow_CannotReplaceOrPublishAnotherSelection()
    {
        await PublishAsync("spBv1.0/plant1/NBIRTH/node1", "birth");
        await PublishAsync("spBv1.0/plant1/NBIRTH/node1/extra", "residue");
        await PublishAsync("sensors/temp", "sensors");
        SelectedTopicState? published = null;
        var cut = await RenderExpandedAsync(state =>
        {
            published = state;
            return Task.CompletedTask;
        });

        _manager.SelectTopic("sensors/temp");
        var live = _manager.GetSelectedTopicState();

        await cut.InvokeAsync(() =>
        {
            _excludes.PurgeExcludedTopic += Raise.Event<Action<string>>("spBv1.0/plant1/NBIRTH/node1");
            RowWith(cut, "NBIRTH").Click();
        });

        var after = _manager.GetSelectedTopicState();
        after.Key.Should().Be("sensors/temp", "the stale row's canonical marker is gone");
        after.Token.Generation.Should().Be(live.Token.Generation, "a refused request cannot replace the selection");
        published.Should().BeNull("a refused row must not be published");
    }

    private async Task PublishAsync(string topic, string payload)
    {
        var message = new MqttApplicationMessageBuilder().WithTopic(topic).WithPayload(payload).Build();
        var packet = new MQTTnet.Packets.MqttPublishPacket { Topic = topic };
        await _handler!(new MqttApplicationMessageReceivedEventArgs("test-client", message, packet, null));
    }

    private async Task<IRenderedComponent<TopicBrowser>> RenderExpandedAsync(
        Func<SelectedTopicState, Task>? onSelected = null)
    {
        var cut = Render<TopicBrowser>(parameters => parameters
            .Add(component => component.OnSelectedChanged, state => onSelected?.Invoke(state) ?? Task.CompletedTask));
        EnsureMudProviders();
        await cut.InvokeAsync(() => cut.FindAll(".mud-toolbar button")[0].Click());
        return cut;
    }

    private static IElement RowWith(IRenderedComponent<TopicBrowser> cut, string text) =>
        cut.FindAll(".topic-tree-row").First(row => row.TextContent.Contains(text, StringComparison.Ordinal));
}
