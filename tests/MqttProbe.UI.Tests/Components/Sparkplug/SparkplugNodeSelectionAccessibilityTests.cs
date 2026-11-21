using AngleSharp.Dom;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using MqttProbe.Core.Models.Sparkplug;
using MqttProbe.UI.Tests.TestHelpers;

namespace MqttProbe.UI.Tests.Components.Sparkplug;

[TestFixture]
public class SparkplugNodeSelectionAccessibilityTests : BunitTestContext
{
    [SetUp]
    public void Setup() => EnsureMudProviders();

    [Test]
    public void NodeList_EveryHealthyAndOfflineNode_HasNativeSelectionButton()
    {
        var online = Node("edge-A");
        var secondOnline = Node("edge-B");
        var offline = Node("edge-C", SpbNodeStatus.Offline);

        var cut = RenderNodeList([online, secondOnline, offline]);

        var rows = cut.FindAll(".spb-node-row");
        rows.Should().HaveCount(3);
        foreach (var row in rows)
        {
            var select = row.QuerySelector("button.spb-node-select")
                .Should().BeAssignableTo<IElement>("every node row needs its own selection control")
                .Which;
            select.ParentElement.Should().BeSameAs(row);
            select.GetAttribute("type").Should().Be("button");
            select.GetAttribute("tabindex").Should().BeNull();
            select.HasAttribute("disabled").Should().BeFalse();
        }

        var selectTexts = cut.FindAll("button.spb-node-select").Select(button => button.TextContent).ToList();
        selectTexts.Should().Contain(text => text.Contains("edge-A") && text.Contains("ONLINE"));
        selectTexts.Should().Contain(text => text.Contains("edge-C") && text.Contains("OFFLINE"));
    }

    [Test]
    public void NodeList_SelectedNode_ExposesAriaCurrentOnItsSelectionButton()
    {
        var selected = Node("edge-A");
        var other = Node("edge-B");
        var cut = RenderNodeList([selected, other], selected);

        var select = cut.FindAll("button.spb-node-select")
            .Single(button => button.TextContent.Contains("edge-A"));
        select.GetAttribute("aria-current").Should().Be("true");

        cut.FindAll("button.spb-node-select")
            .Where(button => !button.TextContent.Contains("edge-A"))
            .Should().OnlyContain(button => button.GetAttribute("aria-current") == null);

        cut.FindAll(".spb-node-row").Single(row => row.TextContent.Contains("edge-A"))
            .ClassList.Should().Contain("spb-node-row--selected");
    }

    [Test]
    public void NodeList_DeleteIsSiblingOfSelectionAndOnlyOfferedForOffline()
    {
        var online = Node("edge-A");
        var offline = Node("edge-B", SpbNodeStatus.Offline);
        var cut = RenderNodeList([online, offline]);

        var healthyRow = cut.FindAll(".spb-node-row").Single(row => row.TextContent.Contains("edge-A"));
        healthyRow.QuerySelector(".spb-node-remove").Should().BeNull("only offline nodes can be removed");
        healthyRow.QuerySelector("button[title='Remove node from view']").Should().BeNull();

        var offlineRow = cut.FindAll(".spb-node-row").Single(row => row.TextContent.Contains("edge-B"));
        var select = offlineRow.QuerySelector("button.spb-node-select")
            .Should().BeAssignableTo<IElement>().Which;
        var remove = offlineRow.QuerySelector(".spb-node-remove")
            .Should().BeAssignableTo<IElement>().Which;

        remove.ParentElement.Should().BeSameAs(offlineRow, "delete must sit beside the selection button, not inside it");
        select.QuerySelector(".spb-node-remove").Should().BeNull();
        select.QuerySelector("button[title='Remove node from view']").Should().BeNull();

        var deleteButton = remove.QuerySelector("button[title='Remove node from view']")
            .Should().BeAssignableTo<IElement>().Which;
        deleteButton.GetAttribute("aria-label").Should().Be("Remove edge-B from view");
    }

    [Test]
    public void NodeList_ClickingSelectionButton_SelectsOnce()
    {
        var online = Node("edge-A");
        var selections = 0;
        SpbNode? selected = null;
        var cut = RenderNodeList(
            [online],
            onSelect: node =>
            {
                selections++;
                selected = node;
            });

        cut.Find("button.spb-node-select").Click();

        selections.Should().Be(1, "the native button click must not select twice");
        selected.Should().BeSameAs(online);
    }

    [Test]
    public void NodeList_ClickingDelete_RemovesWithoutSelecting()
    {
        var offline = Node("edge-B", SpbNodeStatus.Offline);
        var selections = 0;
        SpbNode? removed = null;
        var cut = RenderNodeList(
            [offline],
            onSelect: _ => selections++,
            onRemove: node => removed = node);

        cut.Find(".spb-node-remove button").Click();

        selections.Should().Be(0, "delete must not select the row it removes");
        removed.Should().BeSameAs(offline);
    }

    [Test]
    public void NodeList_HealthUpdate_KeepsSelectionAndASingleSelectionControl()
    {
        var node = Node("edge-A");
        var groups = Groups(node);
        var cut = Render<SparkplugNodeList>(parameters => parameters
            .Add(component => component.Groups, groups)
            .Add(component => component.SelectedNode, node));
        cut.Find("button.spb-node-select").GetAttribute("aria-current").Should().Be("true");

        node.Status = SpbNodeStatus.Offline;
        cut.Render(parameters => parameters
            .Add(component => component.Groups, groups)
            .Add(component => component.SelectedNode, node));

        cut.FindAll("button.spb-node-select").Should().ContainSingle("a health change must not add a second tab stop");
        cut.Find("button.spb-node-select").TextContent.Should().Contain("OFFLINE");
        cut.Find("button.spb-node-select").GetAttribute("aria-current").Should().Be("true");
        cut.Find(".spb-node-remove").Should().NotBeNull();
    }

    [Test]
    public void DeviceList_EveryDevice_HasNativeSelectionButton()
    {
        var online = Device("sensor-A");
        var offline = Device("sensor-B", SpbNodeStatus.Offline);
        var node = NodeWithDevices(online, offline);

        var cut = Render<SparkplugNodeDetail>(parameters => parameters
            .Add(component => component.Node, node));

        var rows = cut.FindAll(".spb-device-row");
        rows.Should().HaveCount(2);
        foreach (var row in rows)
        {
            var select = row.QuerySelector("button.spb-device-select")
                .Should().BeAssignableTo<IElement>("every device row needs its own selection control")
                .Which;
            select.ParentElement.Should().BeSameAs(row);
            select.GetAttribute("type").Should().Be("button");
            select.GetAttribute("tabindex").Should().BeNull();
            select.QuerySelector("button").Should().BeNull("the rebirth control must not be nested in the select button");
        }

        var selectTexts = cut.FindAll("button.spb-device-select").Select(button => button.TextContent).ToList();
        selectTexts.Should().Contain(text => text.Contains("sensor-A") && text.Contains("ONLINE"));
        selectTexts.Should().Contain(text => text.Contains("sensor-B") && text.Contains("OFFLINE"));
    }

    [Test]
    public void DeviceList_ExpandedDevice_ExposesExpandedStateOnItsSelectionButton()
    {
        var expanded = Device("sensor-A");
        var collapsed = Device("sensor-B");
        var node = NodeWithDevices(expanded, collapsed);

        var cut = Render<SparkplugNodeDetail>(parameters => parameters
            .Add(component => component.Node, node)
            .Add(component => component.ExpandedDevice, expanded));

        var expandedButton = cut.FindAll("button.spb-device-select")
            .Single(button => button.TextContent.Contains("sensor-A"));
        expandedButton.GetAttribute("aria-expanded").Should().Be("true");

        cut.FindAll("button.spb-device-select")
            .Where(button => !button.TextContent.Contains("sensor-A"))
            .Should().OnlyContain(button => button.GetAttribute("aria-expanded") == "false");

        cut.FindAll(".spb-device-row").Single(row => row.TextContent.Contains("sensor-A"))
            .ClassList.Should().Contain("spb-device-row--expanded");
    }

    [Test]
    public void DeviceList_ClickingSelection_TogglesWithoutRequestingRebirth()
    {
        var device = Device("sensor-A");
        var node = NodeWithDevices(device);
        var toggles = 0;
        SpbDevice? toggled = null;
        var rebirths = 0;

        var cut = Render<SparkplugNodeDetail>(parameters => parameters
            .Add(component => component.Node, node)
            .Add(component => component.OnToggleDevice, EventCallback.Factory.Create<SpbDevice>(this, value =>
            {
                toggles++;
                toggled = value;
            }))
            .Add(component => component.OnRequestDeviceRebirth, EventCallback.Factory.Create<SpbDevice>(this, _ => rebirths++)));

        cut.Find("button.spb-device-select").Click();

        toggles.Should().Be(1);
        toggled.Should().BeSameAs(device);
        rebirths.Should().Be(0, "opening a device must not request a rebirth");
    }

    [Test]
    public void DeviceList_RebirthStaysASeparateFocusableControl()
    {
        var device = Device("sensor-A");
        var node = NodeWithDevices(device);
        var toggles = 0;
        var rebirths = 0;
        SpbDevice? rebirthed = null;

        var cut = Render<SparkplugNodeDetail>(parameters => parameters
            .Add(component => component.Node, node)
            .Add(component => component.OnToggleDevice, EventCallback.Factory.Create<SpbDevice>(this, _ => toggles++))
            .Add(component => component.OnRequestDeviceRebirth, EventCallback.Factory.Create<SpbDevice>(this, value =>
            {
                rebirths++;
                rebirthed = value;
            })));

        var row = cut.Find(".spb-device-row");
        var select = row.QuerySelector("button.spb-device-select")
            .Should().BeAssignableTo<IElement>().Which;
        var rebirth = row.QuerySelector(".spb-device-rebirth")
            .Should().BeAssignableTo<IElement>().Which;

        var rebirthParent = rebirth.ParentElement.Should().BeAssignableTo<IElement>().Which;
        var selectParent = select.ParentElement.Should().BeAssignableTo<IElement>().Which;
        rebirthParent.QuerySelector("button.spb-device-select").Should().NotBeNull(
            "the rebirth control shares the row with the selection button");
        selectParent.QuerySelector(".spb-device-rebirth").Should().NotBeNull();
        select.QuerySelector(".spb-device-rebirth").Should().BeNull("the rebirth control must not nest inside selection");

        var rebirthButton = rebirth.QuerySelector("button").Should().BeAssignableTo<IElement>().Which;
        rebirthButton.TextContent.Should().Contain("Rebirth");
        rebirthButton.GetAttribute("tabindex").Should().BeNull();

        rebirthButton.Click();

        rebirths.Should().Be(1);
        rebirthed.Should().BeSameAs(device);
        toggles.Should().Be(0, "requesting a rebirth must not expand the device");
    }

    [Test]
    public void DeviceList_HealthUpdate_KeepsExpansionAndASingleSelectionControl()
    {
        var device = Device("sensor-A");
        var node = NodeWithDevices(device);
        var cut = Render<SparkplugNodeDetail>(parameters => parameters
            .Add(component => component.Node, node)
            .Add(component => component.ExpandedDevice, device));

        device.Status = SpbNodeStatus.Offline;
        cut.Render(parameters => parameters
            .Add(component => component.Node, node)
            .Add(component => component.ExpandedDevice, device));

        cut.FindAll("button.spb-device-select").Should().ContainSingle();
        cut.Find("button.spb-device-select").GetAttribute("aria-expanded").Should().Be("true");
        cut.Find("button.spb-device-select").TextContent.Should().Contain("OFFLINE");
    }

    [Test]
    public void DeviceList_ArrowKeysExpandAndCollapseIdempotently()
    {
        var device = Device("sensor-A");
        var node = NodeWithDevices(device);
        var toggles = 0;
        var toggle = EventCallback.Factory.Create<SpbDevice>(this, (_) => toggles++);

        var cut = Render<SparkplugNodeDetail>(parameters => parameters
            .Add(component => component.Node, node)
            .Add(component => component.OnToggleDevice, toggle));

        cut.Find("button.spb-device-select").KeyDown("ArrowRight");
        toggles.Should().Be(1);

        cut.Render(parameters => parameters
            .Add(component => component.Node, node)
            .Add(component => component.ExpandedDevice, device)
            .Add(component => component.OnToggleDevice, toggle));

        cut.Find("button.spb-device-select").KeyDown("ArrowRight");
        toggles.Should().Be(1, "Right on an expanded device is a no-op");

        cut.Find("button.spb-device-select").KeyDown("ArrowLeft");
        toggles.Should().Be(2);

        cut.Render(parameters => parameters
            .Add(component => component.Node, node)
            .Add(component => component.ExpandedDevice, (SpbDevice?)null)
            .Add(component => component.OnToggleDevice, toggle));

        cut.Find("button.spb-device-select").KeyDown("ArrowLeft");
        toggles.Should().Be(2, "Left on a collapsed device is a no-op");

        var button = cut.Find("button.spb-device-select");
        button.KeyDown(new KeyboardEventArgs { Key = "ArrowRight", CtrlKey = true });
        button.KeyDown(new KeyboardEventArgs { Key = "ArrowLeft", AltKey = true });
        button.KeyDown(new KeyboardEventArgs { Key = "ArrowRight", ShiftKey = true });
        button.KeyDown(new KeyboardEventArgs { Key = "ArrowRight", IsComposing = true });
        button.KeyDown("Enter");
        button.KeyDown(" ");
        toggles.Should().Be(2, "modifiers, IME composition and native activation stay untouched");
    }

    private static SpbNode Node(string nodeId, SpbNodeStatus status = SpbNodeStatus.Online) =>
        new() { NodeId = nodeId, GroupId = "factory", Status = status };

    private static SpbDevice Device(string deviceId, SpbNodeStatus status = SpbNodeStatus.Online) =>
        new() { DeviceId = deviceId, NodeId = "edge-01", GroupId = "factory", Status = status };

    private static SpbNode NodeWithDevices(params SpbDevice[] devices)
    {
        var node = Node("edge-01");
        foreach (var device in devices)
            node.Devices[device.DeviceId] = device;
        return node;
    }

    private static List<IGrouping<string, SpbNode>> Groups(params SpbNode[] nodes) =>
        nodes.GroupBy(node => node.GroupId).ToList();

    private IRenderedComponent<SparkplugNodeList> RenderNodeList(
        SpbNode[] nodes,
        SpbNode? selected = null,
        Action<SpbNode>? onSelect = null,
        Action<SpbNode>? onRemove = null) =>
        Render<SparkplugNodeList>(parameters =>
        {
            parameters.Add(component => component.Groups, Groups(nodes));
            parameters.Add(component => component.SelectedNode, selected);
            if (onSelect is not null)
                parameters.Add(component => component.OnSelect, EventCallback.Factory.Create(this, onSelect));
            if (onRemove is not null)
                parameters.Add(component => component.OnRemove, EventCallback.Factory.Create(this, onRemove));
        });
}
