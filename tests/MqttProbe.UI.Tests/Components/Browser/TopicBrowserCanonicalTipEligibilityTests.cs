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
public sealed class TopicBrowserCanonicalTipEligibilityTests : BunitTestContext
{
    private const string CanonicalNodeTip = "spBv1.0/plant1/NDATA/node1";
    private const string AliasNodeTip = "spBv1.0/plant1/NDATA//node1";
    private const string AliasDeviceTip = "spBv1.0/plant1/DDATA/node1//dev1";

    private MessageStoreManager _manager = null!;
    private IMqttManagedClient _client = null!;
    private Func<MqttApplicationMessageReceivedEventArgs, Task>? _handler;
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

        _manager = new MessageStoreManager(_client, NullLogger<MessageStoreManager>.Instance, performance,
            Substitute.For<IUxMetricsService>(), pipeline, sparkplug,
            topologyService: null, commandService: null,
            topicExcludeService: Substitute.For<ITopicExcludeService>());
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

    private async Task<IRenderedComponent<TopicBrowser>> RenderGrouped()
    {
        _preferences.GroupSparkplugTopicsByNodeAndDevice = true;
        var cut = Render<TopicBrowser>();
        EnsureMudProviders();
        await cut.InvokeAsync(() => cut.FindAll(".mud-toolbar button")[0].Click());
        return cut;
    }

    private static List<string> Labels(IRenderedComponent<TopicBrowser> cut) =>
        cut.FindAll(".topic-tree-row").Select(RowLabel).ToList();

    // A row shows its label and its own counts, so comparisons match on the label span alone.
    private static string RowLabel(AngleSharp.Dom.IElement row) =>
        (row.QuerySelector(".flex-grow-1")?.TextContent ?? row.TextContent).Trim();

    private static TopicNodeSnapshot FindNode(TopicNodeSnapshot node, string fullPath)
    {
        if (node.FullTopic == fullPath)
            return node;

        foreach (var child in node.Children)
        {
            var found = Descendant(child, fullPath);
            if (found is not null)
                return found;
        }

        throw new InvalidOperationException(fullPath + " is not in the snapshot");
    }

    private static TopicNodeSnapshot? Descendant(TopicNodeSnapshot node, string fullPath)
    {
        foreach (var child in node.Children)
        {
            if (child.FullTopic == fullPath)
                return child;

            var found = Descendant(child, fullPath);
            if (found is not null)
                return found;
        }

        return null;
    }

    [Test]
    public async Task AliasPublishedTopicIsStoredOnTheSupportedPathButNeverGrouped()
    {
        await PublishAsync(AliasNodeTip, "alias-node-value");

        var snapshot = _manager.GetTopicTreeSnapshot();
        snapshot.Roots.Should().ContainSingle();
        var normalizedTip = FindNode(snapshot.Roots.Single(), "spBv1.0/plant1/NDATA/node1");
        normalizedTip.HasObservedDirectTopic.Should().BeTrue("a message was accepted at this node");
        normalizedTip.HasObservedCanonicalTopic.Should().BeFalse(
            "the published spelling was an alias, not the node's own path");

        var cut = await RenderGrouped();

        Labels(cut).Should().Contain(text => text.Contains("node1", StringComparison.Ordinal));
        cut.FindAll(".topic-tree-row--group").Should().BeEmpty("no grouping row is invented for an alias tip");
        cut.FindAll(".topic-grouping-flag").Should().NotBeEmpty();
    }

    [Test]
    public async Task AliasPublishedDeviceTopicIsNeverGrouped()
    {
        await PublishAsync(AliasDeviceTip, "alias-device-value");

        var snapshot = _manager.GetTopicTreeSnapshot();
        FindNode(snapshot.Roots.Single(), "spBv1.0/plant1/DDATA/node1/dev1")
            .HasObservedCanonicalTopic.Should().BeFalse();

        var cut = await RenderGrouped();

        cut.FindAll(".topic-tree-row--group").Should().BeEmpty();
        Labels(cut).Should().Contain(text => text.Contains("dev1", StringComparison.Ordinal));
    }

    [Test]
    public async Task RepublishingTheCanonicalTipGroupsItAndKeepsTheAliasHistory()
    {
        await PublishAsync(AliasNodeTip, "alias-node-value");
        await PublishAsync(CanonicalNodeTip, "canonical-node-value");

        var snapshot = _manager.GetTopicTreeSnapshot();
        var tip = FindNode(snapshot.Roots.Single(), CanonicalNodeTip);
        tip.HasObservedDirectTopic.Should().BeTrue();
        tip.HasObservedCanonicalTopic.Should().BeTrue("the second publication used the node's own path");

        var cut = await RenderGrouped();

        Labels(cut).Should().Contain("NDATA");
        cut.FindAll(".topic-tree-row--group").Should().NotBeEmpty();
        cut.FindAll(".topic-tree-row .flex-grow-1")
            .Count(element => element.TextContent.Trim() == CanonicalNodeTip)
            .Should().Be(1, "one row for the tip, whichever spelling the messages arrived on");

        var tipRow = cut.FindAll(".topic-tree-row")
            .Single(row => row.QuerySelector(".flex-grow-1")?.TextContent.Trim() == CanonicalNodeTip);
        tipRow.TextContent.Should().Contain("M 2");
    }

    [Test]
    public async Task CanonicalTipSelectionSurvivesTheAliasMessagesBesideIt()
    {
        await PublishAsync(AliasNodeTip, "alias-node-value");
        await PublishAsync(CanonicalNodeTip, "canonical-node-value");

        var cut = await RenderGrouped();
        var tipRow = cut.FindAll(".topic-tree-row")
            .Single(row => row.QuerySelector(".flex-grow-1")?.TextContent.Trim() == CanonicalNodeTip);
        await cut.InvokeAsync(() => tipRow.Click());

        cut.WaitForAssertion(() => cut.FindAll(".topic-tree-row--selected").Should().ContainSingle());
        _manager.GetSelectedTopicState().Key.Should().Be(CanonicalNodeTip,
            "the selection is the normalized path, not the alias spelling");
        var selected = cut.Find(".topic-tree-row--selected");
        RowLabel(selected).Should().Be(CanonicalNodeTip,
            "the marked row is the tip, not the alias spelling");
    }
}
