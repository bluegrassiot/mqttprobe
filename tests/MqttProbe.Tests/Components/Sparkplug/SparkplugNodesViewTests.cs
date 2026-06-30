using Microsoft.Extensions.DependencyInjection;
using MqttProbe.Models.Sparkplug;
using MqttProbe.Services.Sparkplug;
using MqttProbe.Shared.Tests.TestHelpers;
using MudBlazor;

namespace MqttProbe.Shared.Tests.Components.Sparkplug;

[TestFixture]
public class SparkplugNodesViewTests : BunitTestContext
{
    private const string RemoveOfflineNodes = "Prune offline";
    private ISparkplugTopologyService _mockTopology = null!;
    private IDialogService _mockDialogService = null!;
    private ISnackbar _mockSnackbar = null!;

    [SetUp]
    public void SetupMocks()
    {
        _mockTopology = Substitute.For<ISparkplugTopologyService>();
        _mockTopology.Groups.Returns(new Dictionary<string, SpbGroup>());
        Services.AddSingleton(_mockTopology);
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
        var group = new SpbGroup { };
        var offline = new SpbNode { NodeId = "edge-01", GroupId = "factory", Status = SpbNodeStatus.Offline };
        group.Nodes["edge-01"] = offline;
        _mockTopology.Groups.Returns(new Dictionary<string, SpbGroup> { ["factory"] = group });

        var cut = Render<SparkplugNodesView>();

        cut.Markup.Should().Contain("app-tabpanel-header__row");
        cut.Markup.Should().Contain(">Nodes<");
        cut.Markup.Should().Contain(RemoveOfflineNodes);
        cut.Markup.Should().Contain("app-tabpanel-header__filters");
    }

    [Test]
    public void Renders_NodeList_WhenNodesExist()
    {
        var group = new SpbGroup { };
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
        var group = new SpbGroup { };
        var node = new SpbNode { NodeId = "edge-01", GroupId = "factory", Status = SpbNodeStatus.Online };
        group.Nodes["edge-01"] = node;

        var cut = Render<SparkplugNodesView>();
        _mockTopology.Groups.Returns(new Dictionary<string, SpbGroup> { ["factory"] = group });

        _mockTopology.TopologyChanged += Raise.Event<Action>();

        cut.Markup.Should().Contain("edge-01");
    }

    [Test]
    public void NodeRemoval_PreservesSelectedNodeState()
    {
        var group = new SpbGroup { };
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

        cut.FindAll(".spb-node-row").Should().ContainSingle();
        cut.Markup.Should().Contain("edge-A");
        cut.Markup.Should().NotContain("edge-B");
        cut.FindAll(".spb-node-row--selected").Should().ContainSingle("the selected node survives a sibling removal");
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
        var group = new SpbGroup { };
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
        var group = new SpbGroup { };
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
        var group = new SpbGroup { };
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
        var group = new SpbGroup { };
        var online = new SpbNode { NodeId = "edge-A", GroupId = "factory", Status = SpbNodeStatus.Online };
        group.Nodes["edge-A"] = online;
        _mockTopology.Groups.Returns(new Dictionary<string, SpbGroup> { ["factory"] = group });

        var cut = Render<SparkplugNodesView>();

        cut.Markup.Should().Contain(RemoveOfflineNodes);
        var btn = cut.FindAll("button").First(b => b.TextContent.Contains(RemoveOfflineNodes));
        btn.HasAttribute("disabled").Should().BeTrue();
    }

    [Test]
    public void RemoveOfflineButton_EnabledWhenOfflineNodesExist()
    {
        var group = new SpbGroup { };
        var offline = new SpbNode { NodeId = "edge-B", GroupId = "factory", Status = SpbNodeStatus.Offline };
        group.Nodes["edge-B"] = offline;
        _mockTopology.Groups.Returns(new Dictionary<string, SpbGroup> { ["factory"] = group });

        var cut = Render<SparkplugNodesView>();

        var btn = cut.FindAll("button").First(b => b.TextContent.Contains(RemoveOfflineNodes));
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

        var group = new SpbGroup { };
        var offline = new SpbNode { NodeId = "edge-B", GroupId = "factory", Status = SpbNodeStatus.Offline };
        group.Nodes["edge-B"] = offline;
        _mockTopology.Groups.Returns(new Dictionary<string, SpbGroup> { ["factory"] = group });

        var cut = Render<SparkplugNodesView>();
        var btn = cut.FindAll("button").First(b => b.TextContent.Contains(RemoveOfflineNodes));

        await cut.InvokeAsync(() => btn.Click());

        await _mockDialogService.Received(1).ShowMessageBoxAsync(
            "Remove offline nodes", Arg.Is<string>(s => s.Contains("1 offline node")),
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

        var group = new SpbGroup { };
        var offline = new SpbNode { NodeId = "edge-B", GroupId = "factory", Status = SpbNodeStatus.Offline };
        group.Nodes["edge-B"] = offline;
        _mockTopology.Groups.Returns(new Dictionary<string, SpbGroup> { ["factory"] = group });

        var cut = Render<SparkplugNodesView>();
        var btn = cut.FindAll("button").First(b => b.TextContent.Contains(RemoveOfflineNodes));

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
        var group = new SpbGroup { };
        var selected = new SpbNode { NodeId = "edge-A", GroupId = "factory", Status = SpbNodeStatus.Online };
        group.Nodes["edge-A"] = selected;
        _mockTopology.Groups.Returns(new Dictionary<string, SpbGroup> { ["factory"] = group });

        var cut = Render<SparkplugNodesView>();
        cut.FindAll(".spb-node-row").First(r => r.TextContent.Contains("edge-A")).Click();
        cut.FindAll(".spb-detail-panel").Should().ContainSingle();

        group.Nodes.TryRemove("edge-A", out _);
        _mockTopology.TopologyChanged += Raise.Event<Action>();

        cut.FindAll(".spb-detail-panel").Should().BeEmpty();
    }

    [Test]
    public void OnTopologyChanged_WhenGroupIsPruned_GroupFilterResetsToAll()
    {
        var groupA = new SpbGroup { };
        groupA.Nodes["edge-A"] = new SpbNode { NodeId = "edge-A", GroupId = "alpha", Status = SpbNodeStatus.Online };
        var groupB = new SpbGroup { };
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

        cut.Instance.GetGroupFilterForTest().Should().BeNull();
    }

    [Test]
    public void NodeMetricTable_RendersAliasColumnHeader()
    {
        var group = new SpbGroup { };
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
        var group = new SpbGroup { };
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
        var group = new SpbGroup { };
        var node = new SpbNode { NodeId = "edge-01", GroupId = "factory", Status = SpbNodeStatus.Online };
        node.Metrics = [new SpbMetricSnapshot("Temp", "double", "20.0000", DateTime.UtcNow)];
        group.Nodes["edge-01"] = node;
        _mockTopology.Groups.Returns(new Dictionary<string, SpbGroup> { ["factory"] = group });

        var cut = Render<SparkplugNodesView>();
        cut.FindAll(".spb-node-row").First(r => r.TextContent.Contains("edge-01")).Click();

        cut.Markup.Should().Contain("\u2014");
    }

    [Test]
    public void DeviceMetricTable_RendersAliasColumnAndValue()
    {
        var group = new SpbGroup { };
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
}

[TestFixture]
public class SparkplugNodesViewHelperTests
{
    [TestCase(SpbNodeStatus.Online, "var(--mud-palette-success)")]
    [TestCase(SpbNodeStatus.Offline, "var(--mud-palette-error)")]
    [TestCase(SpbNodeStatus.Unknown, "var(--mud-palette-text-secondary)")]
    public void StatusColor_ReturnsExpectedColor(SpbNodeStatus status, string expected)
    {
        SparkplugNodesView.StatusColor(status).Should().Be(expected);
    }

    [TestCase(SpbNodeStatus.Online, "ONLINE")]
    [TestCase(SpbNodeStatus.Offline, "OFFLINE")]
    [TestCase(SpbNodeStatus.Unknown, "UNKNOWN")]
    public void StatusLabel_ReturnsExpectedLabel(SpbNodeStatus status, string expected)
    {
        SparkplugNodesView.StatusLabel(status).Should().Be(expected);
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
        SparkplugNodesView.IsNumericType(dataType).Should().Be(expected);
    }
}
