using AngleSharp.Dom;
using Microsoft.Extensions.DependencyInjection;
using MqttProbe.Core.Models.Configuration;
using MqttProbe.Core.Models.Sparkplug;
using MqttProbe.Core.Services.Configuration;
using MqttProbe.Core.Services.Sparkplug;
using MqttProbe.UI.Tests.TestHelpers;
using MudBlazor;
using MudBlazor.Extensions;

namespace MqttProbe.UI.Tests.Components.Sparkplug;

[TestFixture]
public sealed class SparkplugNodesViewMetricModeTests : BunitTestContext
{
    private ISparkplugTopologyService _topology = null!;

    [SetUp]
    public void SetupMocks()
    {
        _topology = Substitute.For<ISparkplugTopologyService>();
        _topology.Groups.Returns(new Dictionary<string, SpbGroup>());
        Services.AddSingleton(_topology);
        Services.AddSingleton(Substitute.For<ISparkplugCommandService>());

        var sparkplugSettings = Substitute.For<ISparkplugSettings>();
        sparkplugSettings.Sparkplug.Returns(new SparkplugSettings());
        Services.AddSparkplugSettings(sparkplugSettings);
        Services.AddUiSettings(SettingsSubstituteRegistration.CreateUiSettings());

        Services.AddSingleton(Substitute.For<IDialogService>());
        Services.AddSingleton(Substitute.For<ISnackbar>());
        EnsureMudProviders();
    }

    [Test]
    public void NodesView_DefaultsToHierarchy()
    {
        var cut = RenderNode(NodeWithMetrics());

        cut.FindComponent<MudToggleGroup<SparkplugMetricViewMode>>()
            .Instance.GetState(value => value.Value).Should().Be(SparkplugMetricViewMode.Hierarchy);
    }

    [Test]
    public void NodesView_HierarchyGroupsNodeMetricsAndHidesCollapsedChildren()
    {
        var cut = RenderNode(NodeWithMetrics());
        ClickNode(cut);

        var folder = cut.FindAll("tr.spb-metric-row--folder").Single();
        folder.QuerySelector(".spb-td-name")!.GetAttribute("title").Should().Be("A");
        cut.Markup.Should().NotContain("A/T1");
    }

    [Test]
    public void NodesView_HierarchyGroupsExpandedDeviceMetrics()
    {
        var node = NodeWithMetrics();
        var device = new SpbDevice { DeviceId = "sensor-A", NodeId = node.NodeId, GroupId = node.GroupId };
        device.Metrics = [Metric("B/T1"), Metric("B/T2")];
        node.Devices[device.DeviceId] = device;
        var cut = RenderNode(node);

        ClickNode(cut);
        cut.FindAll(".spb-device-row").First(row => row.TextContent.Contains("sensor-A")).Click();

        cut.FindAll("tr.spb-metric-row--folder").Single(row =>
                row.QuerySelector(".spb-td-name")!.GetAttribute("title") == "B")
            .QuerySelector(".spb-td-name")!.GetAttribute("title").Should().Be("B");
        cut.Markup.Should().NotContain("B/T1");
    }

    [Test]
    public async Task NodesView_FlatModeShowsFullNamesWithoutFolders()
    {
        var cut = RenderNode(NodeWithMetrics());
        ClickNode(cut);

        var toggle = cut.FindComponent<MudToggleGroup<SparkplugMetricViewMode>>();
        await cut.InvokeAsync(() => toggle.Instance.ValueChanged.InvokeAsync(SparkplugMetricViewMode.Flat));
        cut.Render();

        cut.FindAll("tr.spb-metric-row--folder").Should().BeEmpty();
        cut.FindAll("tr.spb-metric-row").Select(row => row.QuerySelector(".spb-metric-label")!.TextContent.Trim())
            .Should().Equal("A/T1", "A/T2");
    }

    [Test]
    public async Task NodesView_HierarchyFlatHierarchy_UpdatesNodeAndDeviceVisibleLabels()
    {
        var node = new SpbNode { NodeId = "edge-01", GroupId = "factory", Status = SpbNodeStatus.Online };
        node.Metrics = [Metric("Node/Deep/Seven")];
        var device = new SpbDevice { DeviceId = "sensor-A", NodeId = node.NodeId, GroupId = node.GroupId };
        device.Metrics = [Metric("Device/Deep/Two")];
        node.Devices[device.DeviceId] = device;
        var cut = RenderNode(node);

        ClickNode(cut);
        cut.FindAll(".spb-device-row").First(row => row.TextContent.Contains("sensor-A")).Click();

        VisibleLabel(cut, "Node/Deep/Seven").Should().Be("Seven");
        VisibleLabel(cut, "Device/Deep/Two").Should().Be("Two");

        var toggle = cut.FindComponent<MudToggleGroup<SparkplugMetricViewMode>>();
        await cut.InvokeAsync(() => toggle.Instance.ValueChanged.InvokeAsync(SparkplugMetricViewMode.Flat));
        cut.Render();

        VisibleLabel(cut, "Node/Deep/Seven").Should().Be("Node/Deep/Seven");
        VisibleLabel(cut, "Device/Deep/Two").Should().Be("Device/Deep/Two");

        await cut.InvokeAsync(() => toggle.Instance.ValueChanged.InvokeAsync(SparkplugMetricViewMode.Hierarchy));
        cut.Render();

        VisibleLabel(cut, "Node/Deep/Seven").Should().Be("Seven");
        VisibleLabel(cut, "Device/Deep/Two").Should().Be("Two");
    }

    private static string VisibleLabel(IRenderedComponent<SparkplugNodesView> cut, string fullPath) =>
        cut.FindAll("tr.spb-metric-row")
            .Single(row => row.QuerySelector(".spb-td-name")!.GetAttribute("title") == fullPath)
            .QuerySelector(".spb-metric-label")!.TextContent.Trim();

    private IRenderedComponent<SparkplugNodesView> RenderNode(SpbNode node)
    {
        var group = new SpbGroup();
        group.Nodes[node.NodeId] = node;
        _topology.Groups.Returns(new Dictionary<string, SpbGroup> { [node.GroupId] = group });
        return Render<SparkplugNodesView>();
    }

    private static void ClickNode(IRenderedComponent<SparkplugNodesView> cut) =>
        cut.FindAll(".spb-node-row").First(row => row.TextContent.Contains("edge-01")).Click();

    private static SpbNode NodeWithMetrics()
    {
        var node = new SpbNode { NodeId = "edge-01", GroupId = "factory", Status = SpbNodeStatus.Online };
        node.Metrics = [Metric("A/T1"), Metric("A/T2")];
        return node;
    }

    private static SpbMetricSnapshot Metric(string name) =>
        new(name, "double", "1", DateTime.UtcNow);
}
