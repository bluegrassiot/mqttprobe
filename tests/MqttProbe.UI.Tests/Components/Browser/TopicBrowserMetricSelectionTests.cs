using AngleSharp.Dom;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using MQTTnet.Protocol;
using MqttProbe.Core.Models.Configuration;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Models.Sparkplug;
using MqttProbe.Core.Services.Configuration;
using MqttProbe.Core.Services.Sparkplug;
using MqttProbe.UI.Tests.TestHelpers;
using MudBlazor;

namespace MqttProbe.UI.Tests.Components.Browser;

[TestFixture]
public sealed class TopicBrowserMetricSelectionTests : BunitTestContext
{
    private const string NodeTopic = "spBv1.0/plant1/NDATA/node1";
    private const string DeviceTopic = "spBv1.0/plant1/DDATA/node1/dev1";
    private const string MotorTemp = "Motor/Temp";

    private static readonly SparkplugSelectionScope _nodeScope =
        new(SparkplugSelectionKind.Node, "spBv1.0/plant1", "node1");

    private TopicBrowserTestFixture _fixture = null!;
    private ISparkplugTopologyService _topology = null!;
    private SpbNode _node = null!;
    private readonly UiPreferences _preferences = new();
    private IUiSettings _uiSettings = null!;

    [SetUp]
    public void Setup()
    {
        _fixture = new TopicBrowserTestFixture();
        Services.AddSingleton<ITopicSelectionStore>(_fixture.Adapter);

        _preferences.GroupSparkplugTopicsByNodeAndDevice = true;
        _preferences.TopicTreeMultiSelect = false;
        _uiSettings = SettingsSubstituteRegistration.CreateUiSettings(_preferences);
        Services.AddUiSettings(_uiSettings);
        Services.AddSingleton(Substitute.For<IDialogService>());
        Services.AddSingleton(Substitute.For<ISnackbar>());

        _node = new SpbNode { NodeId = "node1", GroupId = "plant1", Status = SpbNodeStatus.Online };
        _node.Metrics = [Metric(MotorTemp, "21.5"), Metric("Temp", "3")];
        var device = new SpbDevice { DeviceId = "dev1", NodeId = "node1", GroupId = "plant1" };
        device.Metrics = [Metric("Speed/Value", "7")];
        _node.Devices["dev1"] = device;

        var group = new SpbGroup { GroupId = "plant1" };
        group.Nodes["node1"] = _node;
        _topology = Substitute.For<ISparkplugTopologyService>();
        _topology.Groups.Returns(new Dictionary<string, SpbGroup>(StringComparer.Ordinal)
        {
            ["plant1"] = group
        });

        AddNodeMessage("alias-only", MotorTemp);
        AddNodeMessage("temperature", "Temp");
        _fixture.Adapter.Add(DeviceTopic, new MqttMessage("speed", DeviceTopic, false,
            MqttQualityOfServiceLevel.AtMostOnce)
        {
            Topic = DeviceTopic,
            SparkplugMetricNames = ["Speed/Value"]
        });
    }

    private static SpbMetricSnapshot Metric(string name, string value) =>
        new(name, "double", value, DateTime.UnixEpoch);

    private void AddNodeMessage(string payload, params string[] names) =>
        _fixture.Adapter.Add(NodeTopic, new MqttMessage(payload, NodeTopic, false,
            MqttQualityOfServiceLevel.AtMostOnce)
        {
            Topic = NodeTopic,
            SparkplugMetricNames = [.. names]
        });

    private IRenderedComponent<TopicBrowser> RenderExpanded()
    {
        var cut = Render<TopicBrowser>(parameters => parameters
            .Add(component => component.TopologyService, _topology));
        EnsureMudProviders();
        cut.Find("[aria-label='Expand all']").Click();
        return cut;
    }

    private static IElement MetricRow(IRenderedComponent<TopicBrowser> cut, string name)
    {
        var rows = cut.FindAll($"[data-metric-name='{name}']");
        return rows.Single();
    }

    private static IElement FolderRow(IRenderedComponent<TopicBrowser> cut, string label) =>
        cut.FindAll("[data-metric-folder='true']")
            .Single(row => row.QuerySelector(".topic-name")?.TextContent.Trim() == label);

    [Test]
    public void ClickingAMetricLeaf_SelectsTheExactOwnerAndMetric()
    {
        var cut = RenderExpanded();

        MetricRow(cut, MotorTemp).Click();

        var request = _fixture.Adapter.Selections.Single()!;
        request.Mode.Should().Be(TopicSelectionMode.SparkplugMetric);
        request.Metric.Should().Be(new MqttSparkplugMetricTarget(_nodeScope, MotorTemp));
        request.Topics.Should().BeEmpty();
        request.Branches.Should().BeEmpty();

        var state = _fixture.Adapter.Store.GetSelectedTopicState();
        state.Label.Should().Be("spBv1.0/plant1/node1 Motor/Temp");
        state.MessageCount.Should().Be(1, "the alias-only update carries the metric name");
        state.Paths.Should().BeEmpty();
        state.Branches.Should().BeEmpty();
        MetricRow(cut, MotorTemp).ClassList.Should().Contain("topic-tree-row--selected");
    }

    [Test]
    public void ClickingAMetricFolder_OnlyExpandsIt()
    {
        _node.Metrics = [Metric("Pure/Leaf", "1")];
        var cut = RenderExpanded();

        FolderRow(cut, "Pure").Click();

        _fixture.Adapter.Selections.Should().BeEmpty("a pure folder is not a selectable metric");
        cut.WaitForAssertion(() => cut.FindAll("[data-metric-name='Pure/Leaf']").Should().BeEmpty());
    }

    [Test]
    public void AValueThatAlsoHasChildren_SelectsOnClickAndExpandsOnChevron()
    {
        _node.Metrics = [Metric("A", "1"), Metric("A/B", "2")];
        var cut = RenderExpanded();

        var a = MetricRow(cut, "A");
        a.QuerySelector(".topic-chevron").Should().NotBeNull("A also owns the nested metric A/B");
        a.Click();
        _fixture.Adapter.Store.GetSelectedTopicState().Metric!.MetricName.Should().Be("A");

        MetricRow(cut, "A").QuerySelector(".topic-chevron")!.Click();

        _fixture.Adapter.Store.GetSelectedTopicState().Metric!.MetricName.Should().Be("A",
            "the chevron toggles expansion without changing the selected metric");
        cut.WaitForAssertion(() => cut.FindAll("[data-metric-name='A/B']").Should().BeEmpty());
        MetricRow(cut, "A").QuerySelector(".topic-chevron")!.Click();
        cut.WaitForAssertion(() => MetricRow(cut, "A/B").Should().NotBeNull());
        MetricRow(cut, "A/B").Click();
        _fixture.Adapter.Store.GetSelectedTopicState().Metric!.MetricName.Should().Be("A/B",
            "both A and A/B keep their own exact selection");
    }

    [Test]
    public void KeyboardActivation_ExpandsAPureMetricFolderWithoutSelecting()
    {
        _node.Metrics = [Metric("Pure/Leaf", "1")];
        var cut = RenderExpanded();

        FolderRow(cut, "Pure").KeyDownAsync(new KeyboardEventArgs { Key = "Enter" });

        _fixture.Adapter.Selections.Should().BeEmpty();
        cut.WaitForAssertion(() => cut.FindAll("[data-metric-name='Pure/Leaf']").Should().BeEmpty());
    }

    [TestCase("Enter")]
    [TestCase(" ")]
    public void KeyboardActivation_SelectsTheMetric(string key)
    {
        var cut = RenderExpanded();

        MetricRow(cut, MotorTemp).KeyDownAsync(new KeyboardEventArgs { Key = key });

        cut.WaitForAssertion(() =>
            _fixture.Adapter.Store.GetSelectedTopicState().Metric.Should()
                .Be(new MqttSparkplugMetricTarget(_nodeScope, MotorTemp)));
    }

    [Test]
    public void CollapseAndSearch_KeepTheSelectedMetric()
    {
        var cut = RenderExpanded();
        MetricRow(cut, MotorTemp).Click();

        cut.Find("[aria-label='Collapse all']").Click();
        _fixture.Adapter.Store.GetSelectedTopicState().Metric.Should()
            .Be(new MqttSparkplugMetricTarget(_nodeScope, MotorTemp));

        cut.Find(".topic-filter-field input").Input("no-such-row");
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("No topics match"));

        _fixture.Adapter.Store.GetSelectedTopicState().Metric.Should()
            .Be(new MqttSparkplugMetricTarget(_nodeScope, MotorTemp));
        _fixture.Adapter.ConditionalClearCalls.Should().Be(0);
    }

    [Test]
    public void OfflineOwnerAndZeroHistory_KeepTheSelectedMetricValue()
    {
        var cut = RenderExpanded();
        MetricRow(cut, "Temp").Click();

        _node.Status = SpbNodeStatus.Offline;
        _node.Metrics = [Metric("Temp", "3")];
        _fixture.Adapter.Store.RemoveMatchingTopic(NodeTopic);
        AddNodeMessage("no metric name");

        _topology.TopologyChanged += Raise.Event<Action>();

        cut.WaitForAssertion(() =>
        {
            var state = _fixture.Adapter.Store.GetSelectedTopicState();
            state.Metric!.MetricName.Should().Be("Temp");
            state.MessageCount.Should().Be(0, "the owner row remains while no message names the metric");
        });
        MetricRow(cut, "Temp").QuerySelector(".topic-metric-value")!.TextContent.Should().Be("3",
            "the last value is kept while the node is offline");
    }

    [Test]
    public void TurningGroupingOff_ClearsTheMetricSelection()
    {
        var cut = RenderExpanded();
        MetricRow(cut, MotorTemp).Click();

        _preferences.GroupSparkplugTopicsByNodeAndDevice = false;
        _uiSettings.UiPreferencesChanged += Raise.Event<Action>();

        cut.WaitForAssertion(() => _fixture.Adapter.Store.GetSelectedTopicState().Key.Should().BeNull());
        cut.Markup.Should().NotContain("Grouped by node and device");
    }

    [Test]
    public void TurningMultiSelectOn_ClearsTheMetricSelection()
    {
        var cut = RenderExpanded();
        MetricRow(cut, MotorTemp).Click();

        _preferences.TopicTreeMultiSelect = true;
        _uiSettings.UiPreferencesChanged += Raise.Event<Action>();

        cut.WaitForAssertion(() =>
        {
            _fixture.Adapter.Store.GetSelectedTopicState().Key.Should().BeNull();
            cut.FindAll("[data-metric-name]").Should().BeEmpty();
        });
    }

    [Test]
    public void BirthReplacementWithoutTheMetric_ClearsIt()
    {
        var cut = RenderExpanded();
        MetricRow(cut, MotorTemp).Click();

        _node.Metrics = [Metric("Temp", "3")];
        _topology.TopologyChanged += Raise.Event<Action>();

        cut.WaitForAssertion(() => _fixture.Adapter.Store.GetSelectedTopicState().Key.Should().BeNull());
    }

    [Test]
    public void PurgedOwner_ClearsTheMetric()
    {
        var cut = RenderExpanded();
        MetricRow(cut, MotorTemp).Click();

        _fixture.Adapter.Store.RemoveMatchingTopic(NodeTopic);
        _fixture.Adapter.Store.RemoveMatchingTopic(DeviceTopic);
        _topology.Groups.Returns(new Dictionary<string, SpbGroup>(StringComparer.Ordinal));
        _topology.TopologyChanged += Raise.Event<Action>();

        cut.WaitForAssertion(() => _fixture.Adapter.Store.GetSelectedTopicState().Key.Should().BeNull());
    }

    [Test]
    public void TopologyOnlyChange_RefreshesMetricRowsWithoutATreeVersionChange()
    {
        _node.Metrics = [];
        _node.Devices["dev1"].Metrics = [];
        var cut = RenderExpanded();
        cut.FindAll("[data-metric-name]").Should().BeEmpty();
        var version = _fixture.Adapter.Store.Version;

        _node.Metrics = [Metric("New/Metric", "1")];
        _topology.TopologyChanged += Raise.Event<Action>();

        cut.WaitForAssertion(() => cut.FindAll("[data-metric-folder='true']").Should().NotBeEmpty());
        FolderRow(cut, "New").Click();
        cut.WaitForAssertion(() => cut.FindAll("[data-metric-name='New/Metric']").Should().NotBeEmpty());
        _fixture.Adapter.Store.Version.Should().Be(version);
    }

    [Test]
    public async Task DisposeAsync_UnsubscribesFromTopologyChanges()
    {
        var cut = RenderExpanded();

        await cut.Instance.DisposeAsync();

        _topology.Received(1).TopologyChanged -= Arg.Any<Action>();
    }

    [Test]
    public void ExistingRealTopicSelection_RemainsARealTopic()
    {
        var cut = RenderExpanded();

        cut.FindAll(".topic-tree-row")
            .Single(row => row.GetAttribute("data-topic-path") == NodeTopic)
            .Click();

        var state = _fixture.Adapter.Store.GetSelectedTopicState();
        state.Mode.Should().Be(TopicSelectionMode.SingleTopic);
        state.Key.Should().Be(NodeTopic);
        state.Metric.Should().BeNull();
    }
}
