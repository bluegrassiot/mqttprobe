using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using MqttProbe.Core.Models.Configuration;
using MqttProbe.Core.Models.Sparkplug;
using MqttProbe.Core.Services.Configuration;
using MqttProbe.Core.Services.Plugins.BuiltIn;
using MqttProbe.Core.Services.Sparkplug;
using MqttProbe.PluginContracts;
using MqttProbe.UI.Tests.TestHelpers;
using MudBlazor;
using Org.Eclipse.Tahu.Protobuf;

namespace MqttProbe.UI.Tests.Components.Sparkplug;

[TestFixture]
public class SparkplugNodesViewTests : BunitTestContext
{
    private const string RemoveOfflineNodesSelector = "button[aria-label='Remove all offline nodes']";
    private ISparkplugTopologyService _mockTopology = null!;
    private ISparkplugCommandService _mockCommandService = null!;
    private ISparkplugSettings _mockSparkplugSettings = null!;
    private AppConfiguration _cfg = null!;
    private IDialogService _mockDialogService = null!;
    private ISnackbar _mockSnackbar = null!;

    [SetUp]
    public void SetupMocks()
    {
        _mockTopology = Substitute.For<ISparkplugTopologyService>();
        _mockTopology.Groups.Returns(new Dictionary<string, SpbGroup>());
        Services.AddSingleton(_mockTopology);
        _mockCommandService = Substitute.For<ISparkplugCommandService>();
        Services.AddSingleton(_mockCommandService);
        _cfg = new AppConfiguration();
        _mockSparkplugSettings = Substitute.For<ISparkplugSettings>();
        _mockSparkplugSettings.Sparkplug.Returns(_cfg.Sparkplug ?? new SparkplugSettings());
        Services.AddSparkplugSettings(_mockSparkplugSettings);
        _mockDialogService = Substitute.For<IDialogService>();
        Services.AddSingleton(_mockDialogService);
        _mockSnackbar = Substitute.For<ISnackbar>();
        Services.AddSingleton(_mockSnackbar);
        EnsureMudProviders();
    }

    [Test]
    public void Renders_EmptyState_WhenNoNodesObserved()
    {
        var cut = Render<SparkplugNodesView>();

        cut.Markup.Should().Contain("No Sparkplug B nodes observed");
    }

    [Test]
    public void Renders_Header_With_Title_And_RemoveOffline_In_HeaderActions()
    {
        var group = new SpbGroup();
        var offline = new SpbNode { NodeId = "edge-01", GroupId = "factory", Status = SpbNodeStatus.Offline };
        group.Nodes["edge-01"] = offline;
        _mockTopology.Groups.Returns(new Dictionary<string, SpbGroup> { ["factory"] = group });

        var cut = Render<SparkplugNodesView>();

        cut.Markup.Should().Contain("app-tabpanel-header__row");
        cut.Markup.Should().Contain(">Nodes<");
        cut.FindAll(RemoveOfflineNodesSelector).Should().NotBeEmpty();
        cut.Markup.Should().Contain("app-tabpanel-header__filters");
    }

    [Test]
    public void Renders_NodeList_WhenNodesExist()
    {
        var group = new SpbGroup();
        var node = new SpbNode { NodeId = "edge-01", GroupId = "factory", Status = SpbNodeStatus.Online };
        group.Nodes["edge-01"] = node;
        _mockTopology.Groups.Returns(new Dictionary<string, SpbGroup> { ["factory"] = group });

        var cut = Render<SparkplugNodesView>();

        cut.Markup.Should().Contain("edge-01");
        cut.Markup.Should().Contain("FACTORY");
    }

    [Test]
    public void TopologyChanged_TriggersRerender()
    {
        var group = new SpbGroup();
        var node = new SpbNode { NodeId = "edge-01", GroupId = "factory", Status = SpbNodeStatus.Online };
        group.Nodes["edge-01"] = node;

        var cut = Render<SparkplugNodesView>();
        _mockTopology.Groups.Returns(new Dictionary<string, SpbGroup> { ["factory"] = group });

        _mockTopology.TopologyChanged += Raise.Event<Action>();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("edge-01"), TimeSpan.FromSeconds(5));
    }

    [Test]
    public void NodeRemoval_PreservesSelectedNodeState()
    {
        var group = new SpbGroup();
        var nodeA = new SpbNode { NodeId = "edge-A", GroupId = "factory", Status = SpbNodeStatus.Online };
        var nodeB = new SpbNode { NodeId = "edge-B", GroupId = "factory", Status = SpbNodeStatus.Online };
        group.Nodes["edge-A"] = nodeA;
        group.Nodes["edge-B"] = nodeB;
        _mockTopology.Groups.Returns(new Dictionary<string, SpbGroup> { ["factory"] = group });

        var cut = Render<SparkplugNodesView>();
        cut.FindAll(".spb-node-row").First(r => r.TextContent.Contains("edge-A")).Click();
        cut.FindAll(".spb-node-row--selected").Should().ContainSingle();

        group.Nodes.TryRemove("edge-B", out _);
        _mockTopology.TopologyChanged += Raise.Event<Action>();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".spb-node-row").Should().ContainSingle();
            cut.Markup.Should().Contain("edge-A");
            cut.Markup.Should().NotContain("edge-B");
            cut.FindAll(".spb-node-row--selected").Should().ContainSingle("the selected node survives a sibling removal");
        }, TimeSpan.FromSeconds(5));
    }

    [Test]
    public void DetailOpenModifier_TogglesWithNodeSelection()
    {
        var group = new SpbGroup();
        var node = new SpbNode { NodeId = "edge-01", GroupId = "factory", Status = SpbNodeStatus.Online };
        group.Nodes["edge-01"] = node;
        _mockTopology.Groups.Returns(new Dictionary<string, SpbGroup> { ["factory"] = group });

        var cut = Render<SparkplugNodesView>();

        cut.FindAll(".spb-split--detail-open").Should().BeEmpty("no node is selected yet");

        cut.FindAll(".spb-node-row").First(r => r.TextContent.Contains("edge-01")).Click();
        cut.FindAll(".spb-split--detail-open").Should().ContainSingle("selecting a node opens the detail takeover");

        cut.FindAll("button").First(b => b.TextContent.Contains("Back")).Click();
        cut.FindAll(".spb-split--detail-open").Should().BeEmpty("Back clears the selection and returns to the list");
    }

    [Test]
    public async Task DisposeAsync_DoesNotThrow()
    {
        var cut = Render<SparkplugNodesView>();

        var act = async () => await cut.Instance.DisposeAsync();

        await act.Should().NotThrowAsync();
    }

    [Test]
    public void TrashButton_OnlyRenderedForOfflineNodes()
    {
        var group = new SpbGroup();
        var online = new SpbNode { NodeId = "edge-A", GroupId = "factory", Status = SpbNodeStatus.Online };
        var offline = new SpbNode { NodeId = "edge-B", GroupId = "factory", Status = SpbNodeStatus.Offline };
        group.Nodes["edge-A"] = online;
        group.Nodes["edge-B"] = offline;
        _mockTopology.Groups.Returns(new Dictionary<string, SpbGroup> { ["factory"] = group });

        var cut = Render<SparkplugNodesView>();

        cut.FindAll("[title='Remove node from view']").Count.Should().Be(1);
    }

    [Test]
    public async Task TrashButton_Click_CallsRemoveNodeAndShowsInfoSnackbar()
    {
        var group = new SpbGroup();
        var offline = new SpbNode { NodeId = "edge-B", GroupId = "factory", Status = SpbNodeStatus.Offline };
        group.Nodes["edge-B"] = offline;
        _mockTopology.Groups.Returns(new Dictionary<string, SpbGroup> { ["factory"] = group });
        _mockTopology.RemoveNode("factory", "edge-B").Returns(true);

        var cut = Render<SparkplugNodesView>();

        await cut.InvokeAsync(() => cut.Find("[title='Remove node from view']").Click());

        _mockTopology.Received(1).RemoveNode("factory", "edge-B");
        _mockSnackbar.Received(1).Add("Removed node factory/edge-B.", Severity.Info, Arg.Any<Action<SnackbarOptions>?>(), Arg.Any<string?>());
    }

    [Test]
    public async Task TrashButton_Click_WhenServiceReturnsFalse_ShowsWarningSnackbar()
    {
        var group = new SpbGroup();
        var offline = new SpbNode { NodeId = "edge-B", GroupId = "factory", Status = SpbNodeStatus.Offline };
        group.Nodes["edge-B"] = offline;
        _mockTopology.Groups.Returns(new Dictionary<string, SpbGroup> { ["factory"] = group });
        _mockTopology.RemoveNode("factory", "edge-B").Returns(false);

        var cut = Render<SparkplugNodesView>();

        await cut.InvokeAsync(() => cut.Find("[title='Remove node from view']").Click());

        _mockSnackbar.Received(1).Add("Node not found.", Severity.Warning, Arg.Any<Action<SnackbarOptions>?>(), Arg.Any<string?>());
    }

    [Test]
    public void RemoveOfflineButton_DisabledWhenNoOfflineNodes()
    {
        var group = new SpbGroup();
        var online = new SpbNode { NodeId = "edge-A", GroupId = "factory", Status = SpbNodeStatus.Online };
        group.Nodes["edge-A"] = online;
        _mockTopology.Groups.Returns(new Dictionary<string, SpbGroup> { ["factory"] = group });

        var cut = Render<SparkplugNodesView>();

        cut.FindAll(RemoveOfflineNodesSelector).Should().NotBeEmpty();
        var btn = cut.Find(RemoveOfflineNodesSelector);
        btn.HasAttribute("disabled").Should().BeTrue();
    }

    [Test]
    public void RemoveOfflineButton_EnabledWhenOfflineNodesExist()
    {
        var group = new SpbGroup();
        var offline = new SpbNode { NodeId = "edge-B", GroupId = "factory", Status = SpbNodeStatus.Offline };
        group.Nodes["edge-B"] = offline;
        _mockTopology.Groups.Returns(new Dictionary<string, SpbGroup> { ["factory"] = group });

        var cut = Render<SparkplugNodesView>();

        var btn = cut.Find(RemoveOfflineNodesSelector);
        btn.HasAttribute("disabled").Should().BeFalse();
    }

    [Test]
    public async Task RemoveOfflineButton_Click_Confirm_CallsServiceAndShowsSuccessSnackbar()
    {
        _mockDialogService.ShowMessageBoxAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DialogOptions>())
            .Returns(Task.FromResult<bool?>(true));
        _mockTopology.RemoveOfflineNodes().Returns(3);

        var group = new SpbGroup();
        var offline = new SpbNode { NodeId = "edge-B", GroupId = "factory", Status = SpbNodeStatus.Offline };
        group.Nodes["edge-B"] = offline;
        _mockTopology.Groups.Returns(new Dictionary<string, SpbGroup> { ["factory"] = group });

        var cut = Render<SparkplugNodesView>();
        var btn = cut.Find(RemoveOfflineNodesSelector);

        await cut.InvokeAsync(() => btn.Click());

        await _mockDialogService.Received(1).ShowMessageBoxAsync(
            "Remove offline nodes", Arg.Is<string>(s => s!.Contains("1 offline node")),
            "Remove", Arg.Any<string?>(), "Cancel", Arg.Any<DialogOptions>());
        _mockTopology.Received(1).RemoveOfflineNodes();
        _mockSnackbar.Received(1).Add("Removed 3 offline nodes.", Severity.Success, Arg.Any<Action<SnackbarOptions>?>(), Arg.Any<string?>());
    }

    [Test]
    public async Task RemoveOfflineButton_Click_Cancel_DoesNotCallService()
    {
        _mockDialogService.ShowMessageBoxAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DialogOptions>())
            .Returns(Task.FromResult<bool?>(false));

        var group = new SpbGroup();
        var offline = new SpbNode { NodeId = "edge-B", GroupId = "factory", Status = SpbNodeStatus.Offline };
        group.Nodes["edge-B"] = offline;
        _mockTopology.Groups.Returns(new Dictionary<string, SpbGroup> { ["factory"] = group });

        var cut = Render<SparkplugNodesView>();
        var btn = cut.Find(RemoveOfflineNodesSelector);

        await cut.InvokeAsync(() => btn.Click());

        await _mockDialogService.Received(1).ShowMessageBoxAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DialogOptions>());
        _mockTopology.DidNotReceive().RemoveOfflineNodes();
        _mockSnackbar.DidNotReceive().Add(Arg.Any<string>(), Arg.Any<Severity>(), Arg.Any<Action<SnackbarOptions>?>(), Arg.Any<string?>());
    }

    [Test]
    public void OnTopologyChanged_WhenSelectedNodeIsRemoved_DetailPaneClears()
    {
        var group = new SpbGroup();
        var selected = new SpbNode { NodeId = "edge-A", GroupId = "factory", Status = SpbNodeStatus.Online };
        group.Nodes["edge-A"] = selected;
        _mockTopology.Groups.Returns(new Dictionary<string, SpbGroup> { ["factory"] = group });

        var cut = Render<SparkplugNodesView>();
        cut.FindAll(".spb-node-row").First(r => r.TextContent.Contains("edge-A")).Click();
        cut.FindAll(".spb-detail-panel").Should().ContainSingle();

        group.Nodes.TryRemove("edge-A", out _);
        _mockTopology.TopologyChanged += Raise.Event<Action>();

        cut.WaitForAssertion(() => cut.FindAll(".spb-detail-panel").Should().BeEmpty(), TimeSpan.FromSeconds(5));
    }

    [Test]
    public void OnTopologyChanged_WhenGroupIsPruned_GroupFilterResetsToAll()
    {
        var groupA = new SpbGroup();
        groupA.Nodes["edge-A"] = new SpbNode { NodeId = "edge-A", GroupId = "alpha", Status = SpbNodeStatus.Online };
        var groupB = new SpbGroup();
        groupB.Nodes["edge-B"] = new SpbNode { NodeId = "edge-B", GroupId = "beta", Status = SpbNodeStatus.Online };
        _mockTopology.Groups.Returns(new Dictionary<string, SpbGroup>
        {
            ["alpha"] = groupA,
            ["beta"] = groupB,
        });

        var cut = Render<SparkplugNodesView>();
        cut.Instance.SetGroupFilterForTest("alpha");
        cut.Instance.GetGroupFilterForTest().Should().Be("alpha");

        groupA.Nodes.TryRemove("edge-A", out _);
        _mockTopology.Groups.Returns(new Dictionary<string, SpbGroup> { ["beta"] = groupB });
        _mockTopology.TopologyChanged += Raise.Event<Action>();

        cut.WaitForAssertion(() => cut.Instance.GetGroupFilterForTest().Should().BeNull(), TimeSpan.FromSeconds(5));
    }

    [Test]
    public void NodeMetricTable_RendersAliasColumnHeader()
    {
        var group = new SpbGroup();
        var node = new SpbNode { NodeId = "edge-01", GroupId = "factory", Status = SpbNodeStatus.Online };
        node.Metrics = [new SpbMetricSnapshot("Temp", "double", "20.0000", DateTime.UtcNow, 42UL)];
        group.Nodes["edge-01"] = node;
        _mockTopology.Groups.Returns(new Dictionary<string, SpbGroup> { ["factory"] = group });

        var cut = Render<SparkplugNodesView>();
        cut.FindAll(".spb-node-row").First(r => r.TextContent.Contains("edge-01")).Click();

        cut.Markup.Should().Contain(">Alias<");
    }

    [Test]
    public void NodeMetricTable_DisplaysAliasValueWhenPresent()
    {
        var group = new SpbGroup();
        var node = new SpbNode { NodeId = "edge-01", GroupId = "factory", Status = SpbNodeStatus.Online };
        node.Metrics = [new SpbMetricSnapshot("Temp", "double", "20.0000", DateTime.UtcNow, 42UL)];
        group.Nodes["edge-01"] = node;
        _mockTopology.Groups.Returns(new Dictionary<string, SpbGroup> { ["factory"] = group });

        var cut = Render<SparkplugNodesView>();
        cut.FindAll(".spb-node-row").First(r => r.TextContent.Contains("edge-01")).Click();

        cut.Markup.Should().Contain("42");
    }

    [Test]
    public void NodeMetricTable_DisplaysEmDashWhenAliasNull()
    {
        var group = new SpbGroup();
        var node = new SpbNode { NodeId = "edge-01", GroupId = "factory", Status = SpbNodeStatus.Online };
        node.Metrics = [new SpbMetricSnapshot("Temp", "double", "20.0000", DateTime.UtcNow)];
        group.Nodes["edge-01"] = node;
        _mockTopology.Groups.Returns(new Dictionary<string, SpbGroup> { ["factory"] = group });

        var cut = Render<SparkplugNodesView>();
        cut.FindAll(".spb-node-row").First(r => r.TextContent.Contains("edge-01")).Click();

        cut.Markup.Should().Contain("\u2014");
    }

    [Test]
    public async Task NodeMetric_AddToChart_UsesNodeDataTopicAndPreservesMetricName()
    {
        var dialogRef = Substitute.For<IDialogReference>();
        dialogRef.Result.Returns(Task.FromResult<DialogResult?>(DialogResult.Ok("Chart created")));
        _mockDialogService.ShowAsync<QuickAddToChartDialog>(Arg.Any<string>(), Arg.Any<DialogParameters>(), Arg.Any<DialogOptions>())
            .Returns(dialogRef);
        var node = new SpbNode { NodeId = "edge one", GroupId = "Plant A", Status = SpbNodeStatus.Online };
        node.Metrics = [new SpbMetricSnapshot("Flow Rate", "double", "3.14", DateTime.UtcNow)];
        var group = new SpbGroup();
        group.Nodes[node.NodeId] = node;
        _mockTopology.Groups.Returns(new Dictionary<string, SpbGroup> { [group.GroupId] = group });

        var cut = Render<SparkplugNodesView>();
        cut.FindAll(".spb-node-row").Single().Click();
        await cut.Find(".json-chart-btn").ClickAsync(new());

        var parameters = _mockDialogService.ReceivedCalls()
            .Single(call => call.GetMethodInfo().Name == nameof(IDialogService.ShowAsync))
            .GetArguments()[1].Should().BeOfType<DialogParameters<QuickAddToChartDialog>>().Subject;
        parameters.Get<string>("Topic").Should().Be("spBv1.0/Plant A/NDATA/edge one");
        parameters.Get<string>("JsonPath").Should().Be("metrics.Flow Rate");
        parameters.Get<string>("SeriesName").Should().Be("Flow Rate");
        _mockSnackbar.Received(1).Add("Chart created", Severity.Success, Arg.Any<Action<SnackbarOptions>?>(), Arg.Any<string?>());
    }

    [Test]
    public async Task DeviceMetric_AddToChart_UsesDeviceDataTopic()
    {
        var dialogRef = Substitute.For<IDialogReference>();
        dialogRef.Result.Returns(Task.FromResult<DialogResult?>(DialogResult.Cancel()));
        _mockDialogService.ShowAsync<QuickAddToChartDialog>(Arg.Any<string>(), Arg.Any<DialogParameters>(), Arg.Any<DialogOptions>())
            .Returns(dialogRef);
        var node = new SpbNode { NodeId = "edge", GroupId = "plant", Status = SpbNodeStatus.Online };
        var device = new SpbDevice { DeviceId = "sensor", NodeId = node.NodeId, GroupId = node.GroupId, Status = SpbNodeStatus.Online };
        device.Metrics = [new SpbMetricSnapshot("Temperature", "float", "21", DateTime.UtcNow)];
        node.Devices[device.DeviceId] = device;
        var group = new SpbGroup();
        group.Nodes[node.NodeId] = node;
        _mockTopology.Groups.Returns(new Dictionary<string, SpbGroup> { [group.GroupId] = group });

        var cut = Render<SparkplugNodesView>();
        cut.FindAll(".spb-node-row").Single().Click();
        cut.FindAll(".spb-device-row").Single().Click();
        await cut.Find(".json-chart-btn").ClickAsync(new());

        var call = _mockDialogService.ReceivedCalls().Single(c => c.GetMethodInfo().Name == nameof(IDialogService.ShowAsync));
        var parameters = call.GetArguments()[1].Should().BeOfType<DialogParameters<QuickAddToChartDialog>>().Subject;
        parameters.Get<string>("Topic").Should().Be("spBv1.0/plant/DDATA/edge/sensor");
        parameters.Get<string>("JsonPath").Should().Be("metrics.Temperature");
        parameters.Get<string>("SeriesName").Should().Be("Temperature");
        _mockSnackbar.DidNotReceive().Add(Arg.Any<string>(), Arg.Any<Severity>(), Arg.Any<Action<SnackbarOptions>?>(), Arg.Any<string?>());
    }

    [Test]
    public void NonNumericSparkplugMetric_DoesNotShowChartButton()
    {
        var node = new SpbNode { NodeId = "edge", GroupId = "plant", Status = SpbNodeStatus.Online };
        node.Metrics = [new SpbMetricSnapshot("Mode", "string", "auto", DateTime.UtcNow)];
        var group = new SpbGroup();
        group.Nodes[node.NodeId] = node;
        _mockTopology.Groups.Returns(new Dictionary<string, SpbGroup> { [group.GroupId] = group });

        var cut = Render<SparkplugNodesView>();
        cut.FindAll(".spb-node-row").Single().Click();

        cut.FindAll(".json-chart-btn").Should().BeEmpty();
    }

    [Test]
    public async Task ChartableSparkplugValues_OpenQuickAddWithNamedPathsAndSeries()
    {
        var dialogRef = Substitute.For<IDialogReference>();
        dialogRef.Result.Returns(Task.FromResult<DialogResult?>(DialogResult.Cancel()));
        _mockDialogService.ShowAsync<QuickAddToChartDialog>(Arg.Any<string>(), Arg.Any<DialogParameters>(), Arg.Any<DialogOptions>())
            .Returns(dialogRef);
        var node = new SpbNode { NodeId = "edge", GroupId = "plant", Status = SpbNodeStatus.Online };
        node.Metrics =
        [
            new SpbMetricSnapshot("Boolean Flag", "bool", "true", DateTime.UtcNow),
            new SpbMetricSnapshot("Boolean Off", "bool", "false", DateTime.UtcNow),
            new SpbMetricSnapshot("Boolean Text", "string", "true", DateTime.UtcNow),
            new SpbMetricSnapshot("Set Point", "string", "22.5", DateTime.UtcNow),
            new SpbMetricSnapshot("Alias Metric", "unknown", "7", DateTime.UtcNow, 42UL),
            new SpbMetricSnapshot("Missing Value", "unknown", "\u2014", DateTime.UtcNow),
            new SpbMetricSnapshot("Null Value", "double", null!, DateTime.UtcNow)
        ];
        var group = new SpbGroup();
        group.Nodes[node.NodeId] = node;
        _mockTopology.Groups.Returns(new Dictionary<string, SpbGroup> { [group.GroupId] = group });

        var cut = Render<SparkplugNodesView>();
        cut.FindAll(".spb-node-row").Single().Click();
        var rows = cut.FindAll(".spb-metric-row");
        rows.Should().HaveCount(7);
        rows.Single(row => row.TextContent.Contains("Boolean Flag")).QuerySelector(".json-chart-btn").Should().NotBeNull();
        rows.Single(row => row.TextContent.Contains("Boolean Off")).QuerySelector(".json-chart-btn").Should().NotBeNull();
        rows.Single(row => row.TextContent.Contains("Boolean Text")).QuerySelector(".json-chart-btn").Should().BeNull();
        rows.Single(row => row.TextContent.Contains("Set Point")).QuerySelector(".json-chart-btn").Should().NotBeNull();
        rows.Single(row => row.TextContent.Contains("Alias Metric")).QuerySelector(".json-chart-btn").Should().NotBeNull();
        rows.Single(row => row.TextContent.Contains("Missing Value")).QuerySelector(".json-chart-btn").Should().BeNull();
        rows.Single(row => row.TextContent.Contains("Null Value")).QuerySelector(".json-chart-btn").Should().BeNull();

        foreach (var name in new[] { "Boolean Flag", "Boolean Off", "Set Point", "Alias Metric" })
        {
            var button = cut.FindAll(".spb-metric-row").Single(row => row.TextContent.Contains(name))
                .QuerySelector(".json-chart-btn")!;
            await cut.InvokeAsync(() => button.Click());
        }

        var calls = _mockDialogService.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(IDialogService.ShowAsync))
            .ToList();
        calls.Should().HaveCount(4);
        foreach (var name in new[] { "Boolean Flag", "Boolean Off", "Set Point", "Alias Metric" })
        {
            var parameters = calls.Select(call => call.GetArguments()[1])
                .OfType<DialogParameters<QuickAddToChartDialog>>()
                .Single(parameters => parameters.Get<string>("SeriesName") == name);
            parameters.Get<string>("JsonPath").Should().Be($"metrics.{name}");
        }
    }

    [Test]
    public async Task AliasOnlyNativeBooleanWithoutDatatype_OffersChartActionButStringTrueDoesNot()
    {
        var dialogRef = Substitute.For<IDialogReference>();
        dialogRef.Result.Returns(Task.FromResult<DialogResult?>(DialogResult.Cancel()));
        _mockDialogService.ShowAsync<QuickAddToChartDialog>(Arg.Any<string>(), Arg.Any<DialogParameters>(), Arg.Any<DialogOptions>())
            .Returns(dialogRef);

        var topology = new SparkplugTopologyService();
        var extractor = new SparkplugTopologyExtractor();
        async Task Apply(string topic, Payload payload)
        {
            var bytes = payload.ToByteArray();
            var envelope = DecodedPayloadEnvelope.CreateSuccess("sparkplug-b", topic, bytes, payload.ToString(), typedPayload: payload);
            await topology.ApplyTopologyEventsAsync(extractor.Extract(envelope));
        }

        var birth = new Payload { Timestamp = 1, Seq = 1 };
        birth.Metrics.Add(new Payload.Types.Metric { Name = "Boolean Metric", Alias = 7, Datatype = 11, BooleanValue = false });
        birth.Metrics.Add(new Payload.Types.Metric { Name = "Text Metric", Alias = 8, Datatype = 12, StringValue = "false" });
        await Apply("spBv1.0/plant/NBIRTH/edge", birth);

        var data = new Payload { Timestamp = 2, Seq = 2 };
        data.Metrics.Add(new Payload.Types.Metric { Alias = 7, BooleanValue = true });
        data.Metrics.Add(new Payload.Types.Metric { Alias = 8, StringValue = "true" });
        await Apply("spBv1.0/plant/NDATA/edge", data);

        var node = topology.Groups["plant"].Nodes["edge"];
        _mockTopology.Groups.Returns(topology.Groups);
        node.Metrics.Single(metric => metric.Name == "Boolean Metric").DataType.Should().Be("bool");
        node.Metrics.Single(metric => metric.Name == "Boolean Metric").Value.Should().Be("true");
        node.Metrics.Single(metric => metric.Name == "Text Metric").DataType.Should().Be("unknown");

        var cut = Render<SparkplugNodesView>();
        cut.FindAll(".spb-node-row").Single().Click();
        var rows = cut.FindAll(".spb-metric-row");
        rows.Single(row => row.TextContent.Contains("Boolean Metric")).QuerySelector(".json-chart-btn").Should().NotBeNull();
        rows.Single(row => row.TextContent.Contains("Text Metric")).QuerySelector(".json-chart-btn").Should().BeNull();

        await cut.InvokeAsync(() => rows.Single(row => row.TextContent.Contains("Boolean Metric"))
            .QuerySelector(".json-chart-btn")!.Click());
        var parameters = _mockDialogService.ReceivedCalls()
            .Single(call => call.GetMethodInfo().Name == nameof(IDialogService.ShowAsync))
            .GetArguments()[1].Should().BeOfType<DialogParameters<QuickAddToChartDialog>>().Subject;
        parameters.Get<string>("JsonPath").Should().Be("metrics.Boolean Metric");
        parameters.Get<string>("SeriesName").Should().Be("Boolean Metric");
    }

    [Test]
    public void DeviceMetricTable_RendersAliasColumnAndValue()
    {
        var group = new SpbGroup();
        var node = new SpbNode { NodeId = "edge-01", GroupId = "factory", Status = SpbNodeStatus.Online };
        var device = new SpbDevice { DeviceId = "sensor-A", NodeId = "edge-01", GroupId = "factory", Status = SpbNodeStatus.Online };
        device.Metrics = [new SpbMetricSnapshot("Voltage", "double", "220.0000", DateTime.UtcNow, 7UL)];
        node.Devices["sensor-A"] = device;
        group.Nodes["edge-01"] = node;
        _mockTopology.Groups.Returns(new Dictionary<string, SpbGroup> { ["factory"] = group });

        var cut = Render<SparkplugNodesView>();
        cut.FindAll(".spb-node-row").First(r => r.TextContent.Contains("edge-01")).Click();
        cut.FindAll(".spb-device-row").First(r => r.TextContent.Contains("sensor-A")).Click();

        cut.Markup.Should().Contain(">Alias<");
        cut.Markup.Should().Contain("7");
    }

    [Test]
    public void Detail_ActionsRow_AlwaysShowsRebirthAndRebootButtons()
    {
        var group = new SpbGroup();
        var node = new SpbNode { NodeId = "edge-01", GroupId = "factory", Status = SpbNodeStatus.Online };
        group.Nodes["edge-01"] = node;
        _mockTopology.Groups.Returns(new Dictionary<string, SpbGroup> { ["factory"] = group });

        var cut = Render<SparkplugNodesView>();
        cut.FindAll(".spb-node-row").First(r => r.TextContent.Contains("edge-01")).Click();

        cut.FindAll("button").Should().Contain(b => b.TextContent.Contains("Request rebirth"));
        cut.FindAll("button").Should().Contain(b => b.TextContent.Contains("Reboot node"));
    }

    [Test]
    public void Detail_RebootButton_DisabledWhenAllowNodeRebootIsFalse()
    {
        _cfg.Sparkplug = new SparkplugSettings { AllowNodeReboot = false };
        _mockSparkplugSettings.Sparkplug.Returns(_cfg.Sparkplug);

        var group = new SpbGroup();
        var node = new SpbNode { NodeId = "edge-01", GroupId = "factory", Status = SpbNodeStatus.Online };
        group.Nodes["edge-01"] = node;
        _mockTopology.Groups.Returns(new Dictionary<string, SpbGroup> { ["factory"] = group });

        var cut = Render<SparkplugNodesView>();
        cut.FindAll(".spb-node-row").First(r => r.TextContent.Contains("edge-01")).Click();

        var rebootBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Reboot node"));
        rebootBtn.HasAttribute("disabled").Should().BeTrue();
    }

    [Test]
    public void Detail_RebootButton_EnabledWhenAllowNodeRebootIsTrue()
    {
        _cfg.Sparkplug = new SparkplugSettings { AllowNodeReboot = true };
        _mockSparkplugSettings.Sparkplug.Returns(_cfg.Sparkplug);

        var group = new SpbGroup();
        var node = new SpbNode { NodeId = "edge-01", GroupId = "factory", Status = SpbNodeStatus.Online };
        group.Nodes["edge-01"] = node;
        _mockTopology.Groups.Returns(new Dictionary<string, SpbGroup> { ["factory"] = group });

        var cut = Render<SparkplugNodesView>();
        cut.FindAll(".spb-node-row").First(r => r.TextContent.Contains("edge-01")).Click();

        var rebootBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Reboot node"));
        rebootBtn.HasAttribute("disabled").Should().BeFalse();
    }

    [Test]
    public async Task RebirthButton_Click_ShowsConfirmMessageBox()
    {
        _mockDialogService.ShowMessageBoxAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DialogOptions>())
            .Returns(Task.FromResult<bool?>(false));

        var group = new SpbGroup();
        var node = new SpbNode { NodeId = "edge-01", GroupId = "factory", Status = SpbNodeStatus.Online };
        group.Nodes["edge-01"] = node;
        _mockTopology.Groups.Returns(new Dictionary<string, SpbGroup> { ["factory"] = group });

        var cut = Render<SparkplugNodesView>();
        cut.FindAll(".spb-node-row").First(r => r.TextContent.Contains("edge-01")).Click();
        var rebirthBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Request rebirth"));

        await cut.InvokeAsync(() => rebirthBtn.Click());

        await _mockDialogService.Received(1).ShowMessageBoxAsync(
            "Request node rebirth", Arg.Is<string>(s => s!.Contains("factory/edge-01")),
            "Request rebirth", Arg.Any<string?>(), "Cancel", Arg.Any<DialogOptions>());
    }

    [Test]
    public async Task RebirthButton_Click_Confirm_CallsCommandService()
    {
        _mockDialogService.ShowMessageBoxAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DialogOptions>())
            .Returns(Task.FromResult<bool?>(true));
        _mockCommandService.RequestNodeRebirthAsync("factory", "edge-01")
            .Returns(SparkplugCommandResult.Published);

        var group = new SpbGroup();
        var node = new SpbNode { NodeId = "edge-01", GroupId = "factory", Status = SpbNodeStatus.Online };
        group.Nodes["edge-01"] = node;
        _mockTopology.Groups.Returns(new Dictionary<string, SpbGroup> { ["factory"] = group });

        var cut = Render<SparkplugNodesView>();
        cut.FindAll(".spb-node-row").First(r => r.TextContent.Contains("edge-01")).Click();
        var rebirthBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Request rebirth"));

        await cut.InvokeAsync(() => rebirthBtn.Click());

        await _mockCommandService.Received(1).RequestNodeRebirthAsync("factory", "edge-01");
        _mockSnackbar.Received(1).Add("Rebirth requested for factory/edge-01.", Severity.Info, Arg.Any<Action<SnackbarOptions>?>(), Arg.Any<string?>());
    }

    [Test]
    public async Task RebirthButton_Click_Cancel_DoesNotCallCommandService()
    {
        _mockDialogService.ShowMessageBoxAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DialogOptions>())
            .Returns(Task.FromResult<bool?>(false));

        var group = new SpbGroup();
        var node = new SpbNode { NodeId = "edge-01", GroupId = "factory", Status = SpbNodeStatus.Online };
        group.Nodes["edge-01"] = node;
        _mockTopology.Groups.Returns(new Dictionary<string, SpbGroup> { ["factory"] = group });

        var cut = Render<SparkplugNodesView>();
        cut.FindAll(".spb-node-row").First(r => r.TextContent.Contains("edge-01")).Click();
        var rebirthBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Request rebirth"));

        await cut.InvokeAsync(() => rebirthBtn.Click());

        await _mockCommandService.DidNotReceive().RequestNodeRebirthAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    [Test]
    public async Task DeviceRebirth_Confirm_CallsCommandService()
    {
        _mockDialogService.ShowMessageBoxAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DialogOptions>())
            .Returns(Task.FromResult<bool?>(true));
        _mockCommandService.RequestDeviceRebirthAsync("factory", "edge-01", "sensor-A")
            .Returns(SparkplugCommandResult.Published);

        var group = new SpbGroup();
        var node = new SpbNode { NodeId = "edge-01", GroupId = "factory", Status = SpbNodeStatus.Online };
        var device = new SpbDevice { DeviceId = "sensor-A", NodeId = "edge-01", GroupId = "factory", Status = SpbNodeStatus.Online };
        node.Devices["sensor-A"] = device;
        group.Nodes["edge-01"] = node;
        _mockTopology.Groups.Returns(new Dictionary<string, SpbGroup> { ["factory"] = group });

        var cut = Render<SparkplugNodesView>();
        cut.FindAll(".spb-node-row").First(r => r.TextContent.Contains("edge-01")).Click();
        cut.FindAll(".spb-device-row").First(r => r.TextContent.Contains("sensor-A")).Click();
        var deviceRebirthBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Rebirth"));

        await cut.InvokeAsync(() => deviceRebirthBtn.Click());

        await _mockCommandService.Received(1).RequestDeviceRebirthAsync("factory", "edge-01", "sensor-A");
        _mockSnackbar.Received(1).Add(
            "Rebirth requested for factory/edge-01/sensor-A.",
            Severity.Info, Arg.Any<Action<SnackbarOptions>?>(), Arg.Any<string?>());
    }

    [Test]
    public async Task DeviceRebirth_Cancel_DoesNotCallCommandService()
    {
        _mockDialogService.ShowMessageBoxAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DialogOptions>())
            .Returns(Task.FromResult<bool?>(false));

        var group = new SpbGroup();
        var node = new SpbNode { NodeId = "edge-01", GroupId = "factory", Status = SpbNodeStatus.Online };
        var device = new SpbDevice { DeviceId = "sensor-A", NodeId = "edge-01", GroupId = "factory", Status = SpbNodeStatus.Online };
        node.Devices["sensor-A"] = device;
        group.Nodes["edge-01"] = node;
        _mockTopology.Groups.Returns(new Dictionary<string, SpbGroup> { ["factory"] = group });

        var cut = Render<SparkplugNodesView>();
        cut.FindAll(".spb-node-row").First(r => r.TextContent.Contains("edge-01")).Click();
        cut.FindAll(".spb-device-row").First(r => r.TextContent.Contains("sensor-A")).Click();
        var deviceRebirthBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Rebirth"));

        await cut.InvokeAsync(() => deviceRebirthBtn.Click());

        await _mockCommandService.DidNotReceive().RequestDeviceRebirthAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Test]
    public async Task Reboot_ConfirmWithOkTrue_CallsCommandService()
    {
        var dialogRef = Substitute.For<IDialogReference>();
        dialogRef.Result.Returns(Task.FromResult<DialogResult?>(DialogResult.Ok(true)));
        _mockDialogService.ShowAsync<NodeRebootConfirmDialog>(
                Arg.Any<string>(), Arg.Any<DialogParameters>(), Arg.Any<DialogOptions>())
            .Returns(dialogRef);
        _mockCommandService.RequestNodeRebootAsync("factory", "edge-01")
            .Returns(SparkplugCommandResult.Published);

        _cfg.Sparkplug = new SparkplugSettings { AllowNodeReboot = true };
        _mockSparkplugSettings.Sparkplug.Returns(_cfg.Sparkplug);
        var group = new SpbGroup();
        var node = new SpbNode { NodeId = "edge-01", GroupId = "factory", Status = SpbNodeStatus.Online };
        group.Nodes["edge-01"] = node;
        _mockTopology.Groups.Returns(new Dictionary<string, SpbGroup> { ["factory"] = group });

        var cut = Render<SparkplugNodesView>();
        cut.FindAll(".spb-node-row").First(r => r.TextContent.Contains("edge-01")).Click();
        var rebootBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Reboot node"));

        await cut.InvokeAsync(() => rebootBtn.Click());

        await _mockCommandService.Received(1).RequestNodeRebootAsync("factory", "edge-01");
        _mockSnackbar.Received(1).Add(
            "Reboot requested for factory/edge-01.",
            Severity.Info, Arg.Any<Action<SnackbarOptions>?>(), Arg.Any<string?>());
    }

    [Test]
    public async Task Reboot_CancelledDialog_DoesNotCallCommandService()
    {
        var dialogRef = Substitute.For<IDialogReference>();
        dialogRef.Result.Returns(Task.FromResult<DialogResult?>(DialogResult.Cancel()));
        _mockDialogService.ShowAsync<NodeRebootConfirmDialog>(
                Arg.Any<string>(), Arg.Any<DialogParameters>(), Arg.Any<DialogOptions>())
            .Returns(dialogRef);

        _cfg.Sparkplug = new SparkplugSettings { AllowNodeReboot = true };
        _mockSparkplugSettings.Sparkplug.Returns(_cfg.Sparkplug);
        var group = new SpbGroup();
        var node = new SpbNode { NodeId = "edge-01", GroupId = "factory", Status = SpbNodeStatus.Online };
        group.Nodes["edge-01"] = node;
        _mockTopology.Groups.Returns(new Dictionary<string, SpbGroup> { ["factory"] = group });

        var cut = Render<SparkplugNodesView>();
        cut.FindAll(".spb-node-row").First(r => r.TextContent.Contains("edge-01")).Click();
        var rebootBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Reboot node"));

        await cut.InvokeAsync(() => rebootBtn.Click());

        await _mockCommandService.DidNotReceive().RequestNodeRebootAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    [Test]
    public async Task Reboot_NullDialogResult_DoesNotCallCommandService()
    {
        var dialogRef = Substitute.For<IDialogReference>();
        dialogRef.Result.Returns(Task.FromResult<DialogResult?>(null));
        _mockDialogService.ShowAsync<NodeRebootConfirmDialog>(
                Arg.Any<string>(), Arg.Any<DialogParameters>(), Arg.Any<DialogOptions>())
            .Returns(dialogRef);

        _cfg.Sparkplug = new SparkplugSettings { AllowNodeReboot = true };
        _mockSparkplugSettings.Sparkplug.Returns(_cfg.Sparkplug);
        var group = new SpbGroup();
        var node = new SpbNode { NodeId = "edge-01", GroupId = "factory", Status = SpbNodeStatus.Online };
        group.Nodes["edge-01"] = node;
        _mockTopology.Groups.Returns(new Dictionary<string, SpbGroup> { ["factory"] = group });

        var cut = Render<SparkplugNodesView>();
        cut.FindAll(".spb-node-row").First(r => r.TextContent.Contains("edge-01")).Click();
        var rebootBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Reboot node"));

        await cut.InvokeAsync(() => rebootBtn.Click());

        await _mockCommandService.DidNotReceive().RequestNodeRebootAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    [Test]
    public async Task Reboot_OkNonTrueData_DoesNotCallCommandService()
    {
        var dialogRef = Substitute.For<IDialogReference>();
        dialogRef.Result.Returns(Task.FromResult<DialogResult?>(DialogResult.Ok(false)));
        _mockDialogService.ShowAsync<NodeRebootConfirmDialog>(
                Arg.Any<string>(), Arg.Any<DialogParameters>(), Arg.Any<DialogOptions>())
            .Returns(dialogRef);

        _cfg.Sparkplug = new SparkplugSettings { AllowNodeReboot = true };
        _mockSparkplugSettings.Sparkplug.Returns(_cfg.Sparkplug);
        var group = new SpbGroup();
        var node = new SpbNode { NodeId = "edge-01", GroupId = "factory", Status = SpbNodeStatus.Online };
        group.Nodes["edge-01"] = node;
        _mockTopology.Groups.Returns(new Dictionary<string, SpbGroup> { ["factory"] = group });

        var cut = Render<SparkplugNodesView>();
        cut.FindAll(".spb-node-row").First(r => r.TextContent.Contains("edge-01")).Click();
        var rebootBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Reboot node"));

        await cut.InvokeAsync(() => rebootBtn.Click());

        await _mockCommandService.DidNotReceive().RequestNodeRebootAsync(Arg.Any<string>(), Arg.Any<string>());
    }
}

[TestFixture]
public class SparkplugNodesViewHelperTests
{
    [TestCase(SpbNodeStatus.Online, "var(--mud-palette-success)")]
    [TestCase(SpbNodeStatus.Offline, "var(--mud-palette-error)")]
    [TestCase(SpbNodeStatus.Unknown, "var(--mud-palette-text-secondary)")]
    public void StatusColor_ReturnsExpectedColor(SpbNodeStatus status, string expected)
    {
        SparkplugNodeDisplay.StatusColor(status).Should().Be(expected);
    }

    [TestCase(SpbNodeStatus.Online, "ONLINE")]
    [TestCase(SpbNodeStatus.Offline, "OFFLINE")]
    [TestCase(SpbNodeStatus.Unknown, "UNKNOWN")]
    public void StatusLabel_ReturnsExpectedLabel(SpbNodeStatus status, string expected)
    {
        SparkplugNodeDisplay.StatusLabel(status).Should().Be(expected);
    }

    [TestCase("double", true)]
    [TestCase("int8", true)]
    [TestCase("int32", true)]
    [TestCase("int64", true)]
    [TestCase("uint8", true)]
    [TestCase("uint32", true)]
    [TestCase("float", true)]
    [TestCase("string", false)]
    [TestCase("bool", false)]
    [TestCase("bytes", false)]
    public void IsNumericType_ReturnsExpectedResult(string dataType, bool expected)
    {
        SparkplugNodeDisplay.IsNumericType(dataType).Should().Be(expected);
    }
}
