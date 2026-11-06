using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MqttProbe.Core.Models.Emulation;
using MqttProbe.Core.Services.Emulation;
using MqttProbe.UI.Components.Emulation;
using MqttProbe.UI.Tests.TestHelpers;

namespace MqttProbe.UI.Tests.Components.Emulation;

[TestFixture]
public class EmulatorDeviceEditorTests : BunitTestContext
{
    private IEmulationService _mockService = null!;
    private EmulatorNodeConfig _node = null!;
    private EmulatorDeviceConfig _device = null!;

    [SetUp]
    public void SetupMocks()
    {
        _device = new EmulatorDeviceConfig
        {
            DeviceId = "Sensor-1",
            Metrics = [new EmulatorMetricConfig { Name = "Flow Rate" }]
        };
        _node = new EmulatorNodeConfig
        {
            NodeId = "Press-01",
            GroupId = "Plant1",
            Devices = [_device]
        };
        _mockService = Substitute.For<IEmulationService>();
        _mockService.Nodes.Returns(new List<EmulatorNodeConfig> { _node });
        _mockService.IsRunning.Returns(false);
        Services.AddSingleton(_mockService);
        EnsureMudProviders();
    }

    [Test]
    public void CollapsedDevice_ExposesNativeToggleWithExpandedStateAndControls()
    {
        var cut = RenderDevice();

        var toggle = cut.Find("button.emu-device-row__toggle");
        toggle.GetAttribute("type").Should().Be("button");
        toggle.GetAttribute("aria-expanded").Should().Be("false");
        toggle.GetAttribute("tabindex").Should().BeNull();
        toggle.HasAttribute("disabled").Should().BeFalse();
        toggle.GetAttribute("aria-controls").Should().NotBeNullOrWhiteSpace();
        toggle.QuerySelector("svg").Should().NotBeNull("the existing chevron stays inside the disclosure button");

        cut.FindAll(".emu-device-row__toggle .emu-device-row__actions").Should().BeEmpty(
            "duplicate and delete must stay outside the toggle");

        var actions = cut.Find(".emu-device-row__actions");
        actions.QuerySelector("button[title='Duplicate device']").Should().NotBeNull();
        actions.QuerySelector("button[title='Delete device']").Should().NotBeNull();
    }

    [Test]
    public void ExpandedDevice_ExposesExpandedStateAndControlsItsDetail()
    {
        var cut = RenderDevice(expanded: true);

        var toggle = cut.Find("button.emu-device-row__toggle");
        toggle.GetAttribute("aria-expanded").Should().Be("true");
        cut.Find(".emu-device-row").ClassList.Should().Contain("emu-device-row--expanded");
        cut.Find(".emu-device-detail").Id.Should().Be(toggle.GetAttribute("aria-controls"));
        cut.FindAll(".emu-device-row__toggle .emu-device-detail").Should().BeEmpty(
            "editing fields must stay outside the disclosure button");
    }

    [Test]
    public void ToggleButton_Click_InvokesOnToggleExpandOnce()
    {
        var toggles = 0;
        var cut = RenderDevice(onToggle: () => toggles++);

        cut.Find("button.emu-device-row__toggle").Click();

        toggles.Should().Be(1);
    }

    [Test]
    public void DeviceActions_Click_DoesNotToggleTheRow()
    {
        var toggles = 0;
        var cut = RenderDevice(onToggle: () => toggles++);

        cut.Find("button[title='Delete device']").Click();

        _node.Devices.Should().BeEmpty();
        toggles.Should().Be(0, "delete must not expand or collapse the device");
    }

    private IRenderedComponent<EmulatorDeviceEditor> RenderDevice(
        bool expanded = false,
        Action? onToggle = null,
        Action? onChanged = null) =>
        Render<EmulatorDeviceEditor>(parameters =>
        {
            parameters.Add(p => p.Node, _node)
                .Add(p => p.Device, _device)
                .Add(p => p.IsExpanded, expanded);
            if (onToggle is not null)
                parameters.Add(p => p.OnToggleExpand, EventCallback.Factory.Create(this, onToggle));
            if (onChanged is not null)
                parameters.Add(p => p.OnChanged, EventCallback.Factory.Create(this, onChanged));
        });
}
