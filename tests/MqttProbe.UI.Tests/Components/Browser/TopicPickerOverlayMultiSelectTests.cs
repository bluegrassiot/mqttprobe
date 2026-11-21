using AngleSharp.Dom;
using Microsoft.Extensions.DependencyInjection;
using MQTTnet.Protocol;
using MqttProbe.Core.Models.Configuration;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Models.Sparkplug;
using MqttProbe.Core.Services.Sparkplug;
using MqttProbe.UI.Tests.TestHelpers;

namespace MqttProbe.UI.Tests.Components.Browser;

[TestFixture]
public sealed class TopicPickerOverlayMultiSelectTests : BunitTestContext
{
    private TopicBrowserTestFixture _fixture = null!;
    private UiPreferences _preferences = null!;
    private TopicBulkSelection _selection = null!;

    [SetUp]
    public void Setup()
    {
        _preferences = new UiPreferences();
        _fixture = new TopicBrowserTestFixture();
        _fixture.AddTopics(("sensors/temp", 1), ("sensors/humidity", 2));
        Services.AddSingleton<ITopicSelectionStore>(_fixture.Adapter);
        Services.AddUiSettings(SettingsSubstituteRegistration.CreateUiSettings(_preferences));
        _selection = new TopicBulkSelection(_fixture.Adapter);
    }

    [Test]
    public void MultiSelectPreference_RendersMultiSelectableTreeWithCheckboxes()
    {
        var cut = RenderPicker();

        cut.Find(".topic-browser-panel").Should().NotBeNull();
        cut.Find(".topic-tree").GetAttribute("aria-multiselectable").Should().Be("true");
        cut.FindAll(".topic-select-checkbox").Should().NotBeEmpty();
    }

    [Test]
    public void MultiSelect_CheckingTopicsAccumulatesSharedSelectionAndUpdatesTheFooter()
    {
        var cut = RenderPicker();
        ExpandAll(cut);

        ToggleCheckbox(cut, "sensors/temp");
        ToggleCheckbox(cut, "sensors/humidity");

        cut.WaitForAssertion(() =>
        {
            _selection.Branches.Select(branch => branch.Path)
                .Should().BeEquivalentTo("sensors/temp", "sensors/humidity");
            _fixture.Adapter.Store.GetSelectedTopicState().Branches.Should().HaveCount(2);
            cut.Find(".topic-picker-overlay__selection").TextContent.Trim().Should().Be("2 topics selected");
            cut.Find(".topic-picker-overlay__done").HasAttribute("disabled").Should().BeFalse();
        });

        ToggleCheckbox(cut, "sensors/temp");

        cut.WaitForAssertion(() =>
        {
            _selection.Branches.Select(branch => branch.Path).Should().Equal("sensors/humidity");
            _fixture.Adapter.Store.GetSelectedTopicState().Branches.Should().ContainSingle();
            cut.Find(".topic-picker-overlay__selection").TextContent.Trim().Should().Be("1 topic selected");
            cut.Find(".topic-picker-overlay__done").HasAttribute("disabled").Should().BeFalse();
        });

        ToggleCheckbox(cut, "sensors/humidity");

        cut.WaitForAssertion(() =>
        {
            _selection.HasSelection.Should().BeFalse();
            cut.Find(".topic-picker-overlay__selection").TextContent.Trim().Should().Be("No topics selected");
            cut.Find(".topic-picker-overlay__done").HasAttribute("disabled").Should().BeTrue();
        });
    }

    [Test]
    public async Task SingleSelectPreference_RowTapAdoptsTheTopicAndEnablesTheFooterAction()
    {
        _preferences.TopicTreeMultiSelect = false;
        _selection.SetMultiSelect(false);
        var closed = false;
        var cut = RenderPicker(() => { closed = true; return Task.CompletedTask; });

        cut.Find(".topic-tree").GetAttribute("aria-multiselectable").Should().Be("false");
        cut.FindAll(".topic-select-checkbox").Should().BeEmpty();
        ExpandAll(cut);

        Row(cut, "sensors/temp").Click();

        cut.WaitForAssertion(() =>
        {
            var state = _fixture.Adapter.Store.GetSelectedTopicState();
            state.Mode.Should().Be(TopicSelectionMode.SingleTopic);
            state.Key.Should().Be("sensors/temp");
            _selection.Branches.Should().ContainSingle().Which.Path.Should().Be("sensors/temp");
            cut.Find(".topic-picker-overlay__selection").TextContent.Trim()
                .Should().Be("Selected topic and its subtopics: \"sensors/temp\"");
            cut.Find(".topic-picker-overlay__done").HasAttribute("disabled").Should().BeFalse();
        });

        await cut.InvokeAsync(() => cut.Find(".topic-picker-overlay__done").Click());

        closed.Should().BeTrue();
        _fixture.Adapter.Store.GetSelectedTopicState().Key.Should().Be("sensors/temp");
    }

    [Test]
    public async Task MetricSelection_SurvivesInTheMultiSelectPicker()
    {
        _preferences.GroupSparkplugTopicsByNodeAndDevice = true;
        const string nodeTopic = "spBv1.0/plant1/NDATA/node1";
        _fixture.Adapter.Add(nodeTopic, new MqttMessage("21.5", nodeTopic, false,
            MqttQualityOfServiceLevel.AtMostOnce)
        {
            Topic = nodeTopic,
            SparkplugMetricNames = ["Motor/Temp"]
        });
        var topology = CreateMetricTopology();
        var scope = new SparkplugSelectionScope(SparkplugSelectionKind.Node, "spBv1.0/plant1", "node1");
        var metric = new MqttSparkplugMetricTarget(scope, "Motor/Temp");
        _fixture.SetSelection(TopicSelection.ForSparkplugMetrics([metric]));
        var before = _fixture.Adapter.Store.GetSelectedTopicState();
        var closed = false;
        var cut = RenderPicker(() => { closed = true; return Task.CompletedTask; }, topology);

        cut.Find(".topic-picker-overlay__selection").TextContent.Trim()
            .Should().Be("spBv1.0/plant1/node1 Motor/Temp");
        cut.Find(".topic-picker-overlay__done").HasAttribute("disabled").Should().BeFalse();

        await cut.InvokeAsync(() => cut.Find(".topic-picker-overlay__done").Click());

        closed.Should().BeTrue();
        var after = _fixture.Adapter.Store.GetSelectedTopicState();
        after.Mode.Should().Be(TopicSelectionMode.SparkplugMetric);
        after.Token.Should().Be(before.Token);
        after.Metrics.Should().BeEquivalentTo(new[] { metric });
    }

    private IRenderedComponent<TopicPickerOverlay> RenderPicker(
        Func<Task>? onClose = null, ISparkplugTopologyService? topology = null)
    {
        EnsureMudProviders();
        return Render<TopicPickerOverlay>(parameters =>
        {
            parameters.Add(x => x.IsOpen, true);
            parameters.AddCascadingValue(_selection);
            if (onClose is not null)
                parameters.Add(x => x.OnClose, onClose);
            if (topology is not null)
                parameters.Add(x => x.TopologyService, topology);
        });
    }

    private static ISparkplugTopologyService CreateMetricTopology()
    {
        var node = new SpbNode { NodeId = "node1", GroupId = "plant1", Status = SpbNodeStatus.Online };
        node.Metrics = [new SpbMetricSnapshot("Motor/Temp", "double", "21.5", DateTime.UnixEpoch)];
        var group = new SpbGroup { GroupId = "plant1" };
        group.Nodes["node1"] = node;

        var topology = Substitute.For<ISparkplugTopologyService>();
        topology.Groups.Returns(new Dictionary<string, SpbGroup>(StringComparer.Ordinal)
        {
            ["plant1"] = group
        });
        return topology;
    }

    private static void ExpandAll(IRenderedComponent<TopicPickerOverlay> cut) =>
        cut.Find("[aria-label='Expand all']").Click();

    private static IElement Row(IRenderedComponent<TopicPickerOverlay> cut, string path) =>
        cut.FindAll(".topic-tree-row")
            .Single(row => row.GetAttribute("data-topic-path") == path);

    private static void ToggleCheckbox(IRenderedComponent<TopicPickerOverlay> cut, string path) =>
        Row(cut, path).QuerySelector(".topic-select-checkbox-wrap")!.Click();
}
