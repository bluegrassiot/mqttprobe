using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MqttProbe.Core.Models.Emulation;
using MqttProbe.Core.Services.Emulation;
using MqttProbe.UI.Components.Emulation;
using MqttProbe.UI.Tests.TestHelpers;
using MudBlazor;

namespace MqttProbe.UI.Tests.Components.Emulation;

[TestFixture]
public class EmulatorNodeCardTests : BunitTestContext
{
    private IEmulationService _mockService = null!;
    private IDialogService _mockDialogService = null!;
    private EmulatorNodeConfig _node = null!;

    [SetUp]
    public void SetupMocks()
    {
        _node = new EmulatorNodeConfig
        {
            NodeId = "Press-01",
            GroupId = "Plant1",
            Devices =
            [
                new EmulatorDeviceConfig
                {
                    DeviceId = "Sensor-1",
                    Metrics =
                    [
                        new EmulatorMetricConfig { Name = "Flow Rate" },
                        new EmulatorMetricConfig { Name = "Pressure" }
                    ]
                }
            ]
        };
        _mockService = Substitute.For<IEmulationService>();
        _mockService.Nodes.Returns(_ => new List<EmulatorNodeConfig> { _node });
        _mockService.PublishIntervalMs.Returns(500);
        _mockService.IsRunning.Returns(false);
        _mockDialogService = Substitute.For<IDialogService>();
        Services.AddSingleton(_mockService);
        Services.AddSingleton(_mockDialogService);
        EnsureMudProviders();
    }

    private IRenderedComponent<EmulatorNodeCard> RenderCard(bool isExpanded = false, Action? onToggle = null) =>
        Render<EmulatorNodeCard>(ps =>
        {
            ps.Add(p => p.Node, _node)
                .Add(p => p.IsExpanded, isExpanded);
            if (onToggle is not null)
                ps.Add(p => p.OnToggleExpand, EventCallback.Factory.Create(this, onToggle));
        });

    [Test]
    public void CollapsedRow_ExposesNativeToggleWithExpandedStateAndControls()
    {
        var cut = RenderCard();

        var toggle = cut.Find("button.emu-node-row__toggle");
        toggle.GetAttribute("type").Should().Be("button");
        toggle.GetAttribute("aria-expanded").Should().Be("false");
        toggle.GetAttribute("tabindex").Should().BeNull();
        toggle.HasAttribute("disabled").Should().BeFalse();

        var controlledId = toggle.GetAttribute("aria-controls");
        controlledId.Should().NotBeNullOrWhiteSpace();
        cut.FindAll(".emu-node-row__toggle .emu-node-row__actions").Should().BeEmpty(
            "duplicate and delete must stay outside the toggle");

        var actions = cut.Find(".emu-node-row__actions");
        actions.QuerySelector("button[title='Duplicate node']").Should().NotBeNull();
        actions.QuerySelector("button[title='Delete node']").Should().NotBeNull();
    }

    [Test]
    public void ToggleButton_Click_InvokesOnToggleExpandOnce()
    {
        var toggles = 0;
        var cut = RenderCard(onToggle: () => toggles++);

        cut.Find("button.emu-node-row__toggle").Click();

        toggles.Should().Be(1);
    }

    [Test]
    public async Task NodeActions_Click_DoesNotToggleTheCard()
    {
        var toggles = 0;
        var cut = RenderCard(onToggle: () => toggles++);

        cut.Find("button[title='Delete node']").Click();
        await cut.InvokeAsync(() => Task.CompletedTask);

        await _mockService.Received(1).RemoveNodeAsync(_node.Id);
        toggles.Should().Be(0, "delete must not expand or collapse the node");
    }

    [Test]
    public void CollapsedRow_ShowsSecondaryLineWithTypeGroupCountsAndRate()
    {
        var cut = RenderCard();

        cut.Markup.Should().Contain("SparkplugB");
        cut.Markup.Should().Contain("Plant1");
        cut.Markup.Should().Contain("1 device");
        cut.Markup.Should().Contain("2 metrics");
        cut.Markup.Should().Contain("~4 msg/s");
    }

    [Test]
    public void CollapsedRow_ShowsIdleStatusDotAndLabel()
    {
        var cut = RenderCard();

        cut.Find(".emu-status-dot").Should().NotBeNull();
        cut.Markup.Should().Contain("IDLE");
    }

    [Test]
    public void Collapsed_DoesNotRenderEditor()
    {
        var cut = RenderCard();

        cut.FindComponents<EmulatorNodeEditor>().Should().BeEmpty();
    }

    [Test]
    public void Expanded_RendersNodeEditor()
    {
        var cut = RenderCard(isExpanded: true);

        cut.FindComponents<EmulatorNodeEditor>().Should().ContainSingle();
        cut.Find(".emu-node-editor").Id.Should().Be(
            cut.Find("button.emu-node-row__toggle").GetAttribute("aria-controls"));
        cut.FindAll(".emu-node-row__toggle .emu-node-editor").Should().BeEmpty(
            "editing fields must stay outside the disclosure button");
    }

    [Test]
    public async Task DeleteButton_Click_CallsRemoveNodeAsync()
    {
        var cut = RenderCard();

        cut.Find("button[title='Delete node']").Click();
        await cut.InvokeAsync(() => Task.CompletedTask);

        await _mockService.Received(1).RemoveNodeAsync(_node.Id);
    }

    [Test]
    public async Task DuplicateButton_Click_OpensDialogAndDuplicatesWithChosenCount()
    {
        var dialogRef = Substitute.For<IDialogReference>();
        dialogRef.Result.Returns(Task.FromResult<DialogResult?>(DialogResult.Ok(2)));
        _mockDialogService.ShowAsync<DuplicateNodeDialog>(
                Arg.Any<string>(), Arg.Any<DialogParameters>(), Arg.Any<DialogOptions>())
            .Returns(dialogRef);
        var cut = RenderCard();

        cut.Find("button[title='Duplicate node']").Click();
        await cut.InvokeAsync(() => Task.CompletedTask);

        await _mockDialogService.Received(1)
            .ShowAsync<DuplicateNodeDialog>(Arg.Any<string>(), Arg.Any<DialogParameters>(), Arg.Any<DialogOptions>());
        await _mockService.Received(1).DuplicateNodeAsync(_node.Id, 2);
    }

    [Test]
    public async Task DuplicateDialog_Cancelled_DoesNotDuplicate()
    {
        var dialogRef = Substitute.For<IDialogReference>();
        dialogRef.Result.Returns(Task.FromResult<DialogResult?>(DialogResult.Cancel()));
        _mockDialogService.ShowAsync<DuplicateNodeDialog>(
                Arg.Any<string>(), Arg.Any<DialogParameters>(), Arg.Any<DialogOptions>())
            .Returns(dialogRef);
        var cut = RenderCard();

        cut.Find("button[title='Duplicate node']").Click();
        await cut.InvokeAsync(() => Task.CompletedTask);

        await _mockService.DidNotReceive().DuplicateNodeAsync(Arg.Any<Guid>(), Arg.Any<int>());
    }

    [Test]
    public void WhileRunning_DuplicateAndDeleteAreDisabled()
    {
        _mockService.IsRunning.Returns(true);

        var cut = RenderCard();

        cut.Find("button[title='Duplicate node']").HasAttribute("disabled").Should().BeTrue();
        cut.Find("button[title='Delete node']").HasAttribute("disabled").Should().BeTrue();
    }

    [Test]
    public void WhileRunningConnected_ShowsConnectedStatusDotAndLabel()
    {
        _mockService.IsRunning.Returns(true);
        _mockService.GetStatus(_node.Id).Returns(NodeRuntimeStatus.Connected);

        var cut = RenderCard();

        cut.Markup.Should().Contain("CONNECTED");
        cut.Find(".emu-status-dot").ClassList.Should().Contain("emu-node-status--connected");
        cut.Find(".emu-status-label").ClassList.Should().Contain("emu-node-status--connected");
    }

    [Test]
    public void WhileRunningFailed_ShowsErrorStatusDotAndLabel()
    {
        _mockService.IsRunning.Returns(true);
        _mockService.GetStatus(_node.Id).Returns(NodeRuntimeStatus.Error);

        var cut = RenderCard();

        cut.Markup.Should().Contain("ERROR");
        cut.Find(".emu-status-dot").ClassList.Should().Contain("emu-node-status--error");
        cut.Find(".emu-status-label").ClassList.Should().Contain("emu-node-status--error");
    }
}

[TestFixture]
public class DuplicateNodeDialogTests : BunitTestContext
{
    private EmulatorNodeConfig _source = null!;
    private IRenderedComponent<MudDialogProvider> _provider = null!;

    [SetUp]
    public void Setup()
    {
        _source = new EmulatorNodeConfig { NodeId = "Press-07", GroupId = "Plant1" };
        EnsureMudProviders();
        _provider = Render<MudDialogProvider>();
    }

    private async Task<IDialogReference> ShowDialogAsync()
    {
        var dialogService = Services.GetRequiredService<IDialogService>();
        IDialogReference? reference = null;
        await _provider.InvokeAsync(async () =>
        {
            var parameters = new DialogParameters<DuplicateNodeDialog>
            {
                { x => x.Source, _source },
                { x => x.ExistingNodes, new List<EmulatorNodeConfig> { _source } }
            };
            reference = await dialogService.ShowAsync<DuplicateNodeDialog>("Duplicate", parameters);
        });
        return reference!;
    }

    [Test]
    public async Task PreviewNames_DefaultSingleCopy_ShowsNextName()
    {
        await ShowDialogAsync();

        _provider.Markup.Should().Contain("Press-08");
    }

    [Test]
    public async Task PreviewNames_UpdateLiveWithCopyCount()
    {
        await ShowDialogAsync();

        var countField = _provider.FindComponent<MudNumericField<int>>();
        await _provider.InvokeAsync(() => countField.Instance.ValueChanged.InvokeAsync(3));

        _provider.Markup.Should().Contain("Press-08");
        _provider.Markup.Should().Contain("Press-09");
        _provider.Markup.Should().Contain("Press-10");
    }

    [Test]
    public async Task CreateButton_ClosesDialogWithCopyCount()
    {
        var reference = await ShowDialogAsync();
        var countField = _provider.FindComponent<MudNumericField<int>>();
        await _provider.InvokeAsync(() => countField.Instance.ValueChanged.InvokeAsync(2));

        _provider.Find("button[title='Create copies']").Click();

        var result = await reference.Result;
        result.Should().NotBeNull();
        result!.Canceled.Should().BeFalse();
        result.Data.Should().Be(2);
    }

    [Test]
    public async Task CancelButton_CancelsDialog()
    {
        var reference = await ShowDialogAsync();

        _provider.Find("button[title='Cancel duplicate']").Click();

        var result = await reference.Result;
        result.Should().NotBeNull();
        result!.Canceled.Should().BeTrue();
    }
}
