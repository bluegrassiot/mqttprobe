using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MqttProbe.Components.Emulation;
using MqttProbe.Models.Emulation;
using MqttProbe.Models.Mqtt;
using MqttProbe.Services.Emulation;
using MqttProbe.Services.Mqtt;
using MqttProbe.Shared.Tests.TestHelpers;
using MudBlazor;

namespace MqttProbe.Shared.Tests.Components.Emulation;

[TestFixture]
public class EmulationPanelTests : BunitTestContext
{
    private IEmulationService _mockService = null!;
    private List<EmulatorNodeConfig> _nodes = null!;
    private IDialogService _mockDialogService = null!;
    private ISnackbar _mockSnackbar = null!;

    [SetUp]
    public void SetupMocks()
    {
        _nodes = [];
        _mockService = Substitute.For<IEmulationService>();
        _mockService.Nodes.Returns(_ => _nodes);
        _mockService.PublishIntervalMs.Returns(500);
        _mockService.IsRunning.Returns(false);
        Services.AddSingleton(_mockService);
        _mockDialogService = Substitute.For<IDialogService>();
        Services.AddSingleton(_mockDialogService);
        _mockSnackbar = Substitute.For<ISnackbar>();
        Services.AddSingleton(_mockSnackbar);

        var mockSessionState = Substitute.For<ISessionState>();
        mockSessionState.SelectedConnection.Returns(new Connection());
        Services.AddSingleton(mockSessionState);

        EnsureMudProviders();
    }

    private static EmulatorNodeConfig Node(
        string nodeId,
        string groupId = "Plant1",
        int devices = 1,
        int metricsPerDevice = 1,
        EmulatorNodeType type = EmulatorNodeType.SparkplugB,
        GenericPayloadFormat format = GenericPayloadFormat.Json)
    {
        var node = new EmulatorNodeConfig { NodeId = nodeId, GroupId = groupId, Type = type, PayloadFormat = format };
        for (var d = 0; d < devices; d++)
        {
            var device = new EmulatorDeviceConfig { DeviceId = $"Device-{d + 1}" };
            for (var m = 0; m < metricsPerDevice; m++)
                device.Metrics.Add(new EmulatorMetricConfig { Name = $"Metric-{m + 1}" });
            node.Devices.Add(device);
        }

        return node;
    }

    [Test]
    public void EmptyState_NoNodes_RendersCallToAction()
    {
        var cut = Render<EmulationPanel>();

        cut.Markup.Should().Contain("No emulator nodes");
        cut.Find("button[title='Add node']").Should().NotBeNull();
    }

    [Test]
    public async Task AddNodeButton_Click_AddsDefaultSparkplugNode()
    {
        EmulatorNodeConfig? added = null;
        await _mockService.AddNodeAsync(Arg.Do<EmulatorNodeConfig>(n => added = n));
        var cut = Render<EmulationPanel>();

        cut.Find("button[title='Add node']").Click();
        await cut.InvokeAsync(() => Task.CompletedTask);

        added.Should().NotBeNull();
        added!.Type.Should().Be(EmulatorNodeType.SparkplugB);
        added.GroupId.Should().Be("Plant1");
        added.NodeId.Should().Be("Node-1");
        added.Devices.Should().ContainSingle()
            .Which.DeviceId.Should().Be("Device-1");
        added.Devices[0].Metrics.Should().ContainSingle()
            .Which.Waveform.Should().Be(WaveformKind.Sine);
    }

    [Test]
    public async Task AddNodeButton_Click_PicksFirstFreeNodeId()
    {
        _nodes.Add(Node("Node-1"));
        _nodes.Add(Node("Node-2"));
        EmulatorNodeConfig? added = null;
        await _mockService.AddNodeAsync(Arg.Do<EmulatorNodeConfig>(n => added = n));
        var cut = Render<EmulationPanel>();

        cut.Find("button[title='Add node']").Click();
        await cut.InvokeAsync(() => Task.CompletedTask);

        added!.NodeId.Should().Be("Node-3");
    }

    [Test]
    public void HeaderChips_ReflectNodeAndDeviceCounts()
    {
        _nodes.Add(Node("Press-01", devices: 1));
        _nodes.Add(Node("Press-02", devices: 2));

        var cut = Render<EmulationPanel>();

        cut.Markup.Should().Contain("2 nodes");
        cut.Markup.Should().Contain("3 devices");
    }

    [Test]
    public void FilterField_NarrowsNodeListByNodeId()
    {
        _nodes.Add(Node("Press-01"));
        _nodes.Add(Node("Pump-01"));
        var cut = Render<EmulationPanel>();
        cut.FindAll(".emu-node-row").Should().HaveCount(2);

        cut.Find(".emu-filter-text input").Input("Pump");

        cut.FindAll(".emu-node-row").Should().HaveCount(1);
        cut.Markup.Should().Contain("Pump-01");
        cut.Markup.Should().NotContain("Press-01");
    }

    [Test]
    public void HighThroughput_AtOrAbove200PerSecond_RendersWarningAlert()
    {
        _nodes.Add(Node("Bulk-1", metricsPerDevice: 100, type: EmulatorNodeType.Generic, format: GenericPayloadFormat.PlainText));

        var cut = Render<EmulationPanel>();

        cut.Markup.Should().Contain("High throughput");
    }

    [Test]
    public void HighThroughput_BelowThreshold_NoWarningAlert()
    {
        _nodes.Add(Node("Press-01"));

        var cut = Render<EmulationPanel>();

        cut.Markup.Should().NotContain("High throughput");
    }

    [Test]
    public void RunningState_DisablesMutationsAndShowsRunningChip()
    {
        _nodes.Add(Node("Press-01"));
        _mockService.IsRunning.Returns(true);

        var cut = Render<EmulationPanel>();

        cut.Markup.Should().Contain("Running");
        cut.Find("button[title='Add node']").HasAttribute("disabled").Should().BeTrue();
        cut.Find("button[title='Remove all nodes']").HasAttribute("disabled").Should().BeTrue();
        cut.FindComponents<MudNumericField<int>>()
            .Single(f => f.Instance.Label == "Publish interval (ms)")
            .Instance.Disabled.Should().BeTrue();
    }

    [Test]
    public void StartButton_WithZeroNodes_IsDisabled()
    {
        var cut = Render<EmulationPanel>();

        cut.Find("button[title='Start emulation']").HasAttribute("disabled").Should().BeTrue();
    }

    [Test]
    public async Task StartButton_Click_CallsStartAsync()
    {
        _nodes.Add(Node("Press-01"));
        var cut = Render<EmulationPanel>();

        cut.Find("button[title='Start emulation']").Click();
        await cut.InvokeAsync(() => Task.CompletedTask);

        await _mockService.Received(1).StartAsync();
    }

    [Test]
    public async Task StopButton_WhenRunning_CallsStopAsync()
    {
        _nodes.Add(Node("Press-01"));
        _mockService.IsRunning.Returns(true);
        var cut = Render<EmulationPanel>();

        cut.Find("button[title='Stop emulation']").Click();
        await cut.InvokeAsync(() => Task.CompletedTask);

        await _mockService.Received(1).StopAsync();
    }

    [Test]
    public async Task ClearAllButton_Click_Confirmed_CallsRemoveAllNodesAsync()
    {
        _nodes.Add(Node("Press-01"));
        _mockDialogService.ShowMessageBoxAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DialogOptions>())
            .Returns(Task.FromResult<bool?>(true));
        var cut = Render<EmulationPanel>();

        cut.Find("button[title='Remove all nodes']").Click();
        await cut.InvokeAsync(() => Task.CompletedTask);

        await _mockService.Received(1).RemoveAllNodesAsync();
    }

    [Test]
    public async Task RemoveAllButton_Click_ShowsConfirmationDialog()
    {
        _nodes.Add(Node("Press-01"));
        _mockDialogService.ShowMessageBoxAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DialogOptions>())
            .Returns(Task.FromResult<bool?>(true));
        var cut = Render<EmulationPanel>();

        cut.Find("button[title='Remove all nodes']").Click();
        await cut.InvokeAsync(() => Task.CompletedTask);

        await _mockDialogService.Received(1).ShowMessageBoxAsync(
            "Remove All Nodes",
            Arg.Is<string>(s => s.Contains("This will remove all configured nodes")),
            "Remove all", Arg.Any<string?>(), "Cancel", Arg.Any<DialogOptions>());
    }

    [Test]
    public async Task RemoveAllButton_Confirmed_CallsRemoveAllNodesAsync()
    {
        _nodes.Add(Node("Press-01"));
        _mockDialogService.ShowMessageBoxAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DialogOptions>())
            .Returns(Task.FromResult<bool?>(true));
        var cut = Render<EmulationPanel>();

        cut.Find("button[title='Remove all nodes']").Click();
        await cut.InvokeAsync(() => Task.CompletedTask);

        await _mockService.Received(1).RemoveAllNodesAsync();
    }

    [Test]
    public async Task RemoveAllButton_Confirmed_ShowsSuccessSnackbar()
    {
        _nodes.Add(Node("Press-01"));
        _mockDialogService.ShowMessageBoxAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DialogOptions>())
            .Returns(Task.FromResult<bool?>(true));
        var cut = Render<EmulationPanel>();

        cut.Find("button[title='Remove all nodes']").Click();
        await cut.InvokeAsync(() => Task.CompletedTask);

        _mockSnackbar.Received(1).Add("All nodes removed.", Severity.Success,
            Arg.Any<Action<SnackbarOptions>?>(), Arg.Any<string?>());
    }

    [Test]
    public async Task RemoveAllButton_Cancelled_DoesNotCallRemoveAllNodesAsync()
    {
        _nodes.Add(Node("Press-01"));
        _mockDialogService.ShowMessageBoxAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DialogOptions>())
            .Returns(Task.FromResult<bool?>(false));
        var cut = Render<EmulationPanel>();

        cut.Find("button[title='Remove all nodes']").Click();
        await cut.InvokeAsync(() => Task.CompletedTask);

        await _mockService.DidNotReceive().RemoveAllNodesAsync();
    }

    [Test]
    public async Task IntervalField_Commit_CallsSetPublishInterval()
    {
        _nodes.Add(Node("Press-01"));
        var cut = Render<EmulationPanel>();

        var intervalField = cut.FindComponents<MudNumericField<int>>()
            .Single(f => f.Instance.Label == "Publish interval (ms)");
        await cut.InvokeAsync(() => intervalField.Instance.ValueChanged.InvokeAsync(250));

        await _mockService.Received(1).SetPublishIntervalAsync(250);
    }

    [Test]
    public void CollapsedNodes_RenderNoWaveformPreviews()
    {
        _nodes.Add(Node("Press-01", metricsPerDevice: 3));
        _nodes.Add(Node("Press-02", metricsPerDevice: 3));

        var cut = Render<EmulationPanel>();

        cut.FindAll(".emu-waveform").Should().BeEmpty();
    }

    [Test]
    public void ExpandedDevice_RendersExactlyItsOwnPreviews()
    {
        _nodes.Add(Node("Press-01", devices: 2, metricsPerDevice: 2));
        _nodes.Add(Node("Press-02", metricsPerDevice: 3));
        var cut = Render<EmulationPanel>();

        cut.FindAll(".emu-node-row__main")[0].Click();
        cut.FindAll(".emu-waveform").Should().BeEmpty();

        cut.FindAll(".emu-device-row")[0].Click();

        cut.FindAll(".emu-waveform").Should().HaveCount(2);
    }

    [Test]
    public void Renders_Header_With_All_Three_Actions()
    {
        var cut = Render<EmulationPanel>();

        cut.Markup.Should().Contain("app-tabpanel-header__row");
        cut.Markup.Should().Contain(">Emulation<");

        var addBtn = cut.Find("button[title='Add node']");
        var removeBtn = cut.Find("button[title='Remove all nodes']");
        var startBtn = cut.Find("button[title='Start emulation']");

        addBtn.TextContent.Should().Contain("Add").And.NotContain("Add node");
        removeBtn.TextContent.Should().Contain("Remove").And.NotContain("Remove all");
        startBtn.TextContent.Should().Contain("Start").And.NotContain("Start emulation");
    }
}
