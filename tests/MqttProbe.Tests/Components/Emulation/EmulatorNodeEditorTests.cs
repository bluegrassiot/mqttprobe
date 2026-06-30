using Microsoft.Extensions.DependencyInjection;
using MqttProbe.Components.Emulation;
using MqttProbe.Models.Emulation;
using MqttProbe.Services.Configuration;
using MqttProbe.Services.Emulation;
using MqttProbe.Shared.Tests.TestHelpers;
using MudBlazor;

namespace MqttProbe.Shared.Tests.Components.Emulation;

[TestFixture]
public class EmulatorNodeEditorTests : BunitTestContext
{
    private IEmulationService _mockService = null!;
    private List<EmulatorNodeConfig> _nodes = null!;
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
                    Metrics = [new EmulatorMetricConfig { Name = "Flow Rate" }]
                }
            ]
        };
        _nodes = [_node];
        _mockService = Substitute.For<IEmulationService>();
        _mockService.Nodes.Returns(_ => _nodes);
        _mockService.PublishIntervalMs.Returns(500);
        _mockService.IsRunning.Returns(false);
        Services.AddSingleton(_mockService);
        EnsureMudProviders();
    }

    private IRenderedComponent<EmulatorNodeEditor> RenderEditor() =>
        Render<EmulatorNodeEditor>(ps => ps.Add(p => p.Node, _node));

    [Test]
    public void SparkplugNode_HidesGenericOnlyFields()
    {
        var cut = RenderEditor();

        cut.Markup.Should().NotContain("Payload format");
        cut.Markup.Should().NotContain("Topic template");
    }

    [Test]
    public void GenericNode_ShowsPayloadFormatAndTopicTemplateFields()
    {
        _node.Type = EmulatorNodeType.Generic;

        var cut = RenderEditor();

        cut.Markup.Should().Contain("Payload format");
        cut.Markup.Should().Contain("Topic template");
    }

    [Test]
    public void GenericJsonNode_ShowsResolvedDeviceTopicPreviewInMono()
    {
        _node.Type = EmulatorNodeType.Generic;
        _node.PayloadFormat = GenericPayloadFormat.Json;

        var cut = RenderEditor();

        var preview = cut.Find(".emu-topic-preview .telemetry-mono");
        preview.TextContent.Should().Be("Plant1/Press-01/Sensor-1");
    }

    [Test]
    public void GenericPlainTextNode_ShowsResolvedMetricTopicPreview()
    {
        _node.Type = EmulatorNodeType.Generic;
        _node.PayloadFormat = GenericPayloadFormat.PlainText;

        var cut = RenderEditor();

        var preview = cut.Find(".emu-topic-preview .telemetry-mono");
        preview.TextContent.Should().Be("Plant1/Press-01/Sensor-1/Flow Rate");
    }

    [Test]
    public void GenericNode_TemplateMissingMetricToken_ShowsValidationError()
    {
        _node.Type = EmulatorNodeType.Generic;
        _node.PayloadFormat = GenericPayloadFormat.PlainText;
        _node.TopicTemplate = "{group}/{node}/{device}";

        var cut = RenderEditor();

        cut.Markup.Should().Contain("must contain {metric}");
    }

    [Test]
    public void NodeIdWithIllegalChars_ShowsValidationMessage()
    {
        _node.NodeId = "bad/id";

        var cut = RenderEditor();

        cut.Markup.Should().Contain("Must not contain / + #");
    }

    [Test]
    public void NodeIdStartingWithDollar_ShowsValidationMessage()
    {
        _node.NodeId = "$bad";

        var cut = RenderEditor();

        cut.Markup.Should().Contain("Must not start with $");
    }

    [Test]
    public void DuplicateNodeIdInSameGroup_ShowsValidationMessage()
    {
        _nodes.Add(new EmulatorNodeConfig { NodeId = "Press-01", GroupId = "Plant1" });

        var cut = RenderEditor();

        cut.Markup.Should().Contain("already used in this group");
    }

    [Test]
    public async Task AddDeviceButton_AppendsDeviceAndSaves()
    {
        var cut = RenderEditor();

        cut.Find("button[title='Add device']").Click();
        await cut.InvokeAsync(() => Task.CompletedTask);

        _node.Devices.Should().HaveCount(2);
        _node.Devices[1].DeviceId.Should().Be("Device-1");
        await _mockService.Received(1).UpdateNodeAsync(_node);
    }

    [Test]
    public void DevicesSection_RendersOneRowPerDevice()
    {
        _node.Devices.Add(new EmulatorDeviceConfig { DeviceId = "Sensor-2" });

        var cut = RenderEditor();

        cut.FindAll(".emu-device-row").Should().HaveCount(2);
        cut.Markup.Should().Contain("Sensor-1");
        cut.Markup.Should().Contain("Sensor-2");
    }

    [Test]
    public async Task DeviceDeleteButton_RemovesDeviceAndSaves()
    {
        var cut = RenderEditor();

        cut.Find("button[title='Delete device']").Click();
        await cut.InvokeAsync(() => Task.CompletedTask);

        _node.Devices.Should().BeEmpty();
        await _mockService.Received(1).UpdateNodeAsync(_node);
    }

    [Test]
    public async Task DeviceDuplicateButton_AppendsCopyWithIncrementedId()
    {
        var cut = RenderEditor();

        cut.Find("button[title='Duplicate device']").Click();
        await cut.InvokeAsync(() => Task.CompletedTask);

        _node.Devices.Should().HaveCount(2);
        _node.Devices[1].DeviceId.Should().Be("Sensor-2");
        _node.Devices[1].Id.Should().NotBe(_node.Devices[0].Id);
        _node.Devices[1].Metrics.Should().ContainSingle()
            .Which.Name.Should().Be("Flow Rate");
        await _mockService.Received(1).UpdateNodeAsync(_node);
    }

    [Test]
    public void GenericNodeWithoutDevices_ShowsPublishesNothingHint()
    {
        _node.Type = EmulatorNodeType.Generic;
        _node.Devices.Clear();

        var cut = RenderEditor();

        cut.Markup.Should().Contain("publishes nothing");
    }

    [Test]
    public void SparkplugNode_ShowsUseMetricAliasesCheckbox()
    {
        var cut = RenderEditor();

        cut.Markup.Should().Contain("Use Metric Aliases");
    }

    [Test]
    public void GenericNode_HidesUseMetricAliasesCheckbox()
    {
        _node.Type = EmulatorNodeType.Generic;

        var cut = RenderEditor();

        cut.Markup.Should().NotContain("Use Metric Aliases");
    }

    [Test]
    public async Task UseMetricAliasesCheckbox_Toggle_SavesState()
    {
        var cut = RenderEditor();

        var checkbox = cut.FindComponent<MudCheckBox<bool>>();
        await cut.InvokeAsync(() => checkbox.Instance.ValueChanged.InvokeAsync(true));
        await cut.InvokeAsync(() => Task.CompletedTask);

        _node.UseMetricAliases.Should().BeTrue();
        await _mockService.Received(1).UpdateNodeAsync(_node);
    }

    [Test]
    public async Task UseMetricAliases_PersistsAcrossSaveLoad()
    {
        var filePath = Path.Combine(Path.GetTempPath(), $"alias_persist_test_{Guid.NewGuid()}.json");
        try
        {
            var store = new SettingsStore(filePath);
            await store.LoadAsync();
            var connectionId = Guid.NewGuid();

            var config = new EmulatorNodeConfig
            {
                Type = EmulatorNodeType.SparkplugB,
                NodeId = "Node-1",
                UseMetricAliases = true
            };
            await store.AddEmulatorNodeAsync(connectionId, config);

            // Reload from disk
            var store2 = new SettingsStore(filePath);
            await store2.LoadAsync();
            var nodes = store2.GetEmulatorNodes(connectionId);

            nodes.Should().ContainSingle();
            nodes[0].UseMetricAliases.Should().BeTrue();
        }
        finally
        {
            if (File.Exists(filePath)) File.Delete(filePath);
        }
    }
}
