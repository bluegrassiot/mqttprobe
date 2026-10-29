using Microsoft.Extensions.DependencyInjection;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Services.Mqtt;
using MqttProbe.UI.Tests.TestHelpers;

namespace MqttProbe.UI.Tests.Components.Browser;

[TestFixture]
public class MobileTopicBarTests : BunitTestContext
{
    private IMessageStoreManager _mockMsgStore = null!;

    private static SelectedTopicState Selection(
        string? topic,
        int messageCount = 0,
        string? label = null) =>
        new(new SelectedTopicToken(Guid.NewGuid(), 1, 0), topic, messageCount, label);

    [SetUp]
    public void SetupMocks()
    {
        _mockMsgStore = Substitute.For<IMessageStoreManager>();
        _mockMsgStore.GetSelectedTopicState().Returns(Selection(null));
        Services.AddSingleton(_mockMsgStore);
        EnsureMudProviders();
    }

    [Test]
    public void Renders_ChooseTopic_WhenNoTopicSelected()
    {
        _mockMsgStore.GetSelectedTopicState().Returns(Selection(null));

        var cut = Render<MobileTopicBar>();

        cut.Markup.Should().Contain("Choose a topic");
    }

    [Test]
    public void Renders_SelectedTopicName_WhenTopicSelected()
    {
        _mockMsgStore.GetSelectedTopicState().Returns(Selection("sensor/temp", 5));

        var cut = Render<MobileTopicBar>();

        cut.Markup.Should().Contain("sensor/temp");
    }

    [Test]
    public void Renders_TheGroupingLabel_WhenAGroupedRowIsSelected()
    {
        _mockMsgStore.GetSelectedTopicState()
            .Returns(Selection("\0spb:node:spBv1.0/plant1/node1", 7, "node1"));

        var cut = Render<MobileTopicBar>();

        cut.Markup.Should().Contain("node1");
        cut.Markup.Should().NotContain("spb:");
        cut.Markup.Should().Contain("7");
    }

    [Test]
    public void Renders_MessageCount_WhenTopicHasMessages()
    {
        _mockMsgStore.GetSelectedTopicState().Returns(Selection("sensor/temp", 42));

        var cut = Render<MobileTopicBar>();

        cut.Markup.Should().Contain("42");
    }

    [Test]
    public void TopicsButton_WhenNoTopics_IsDisabled()
    {
        _mockMsgStore.GetSelectedTopicState().Returns(Selection(null));

        var cut = Render<MobileTopicBar>(p => p.Add(x => x.HasTopics, false));

        var btn = cut.FindAll("button").First(b => b.TextContent.Contains("Topics"));
        btn.HasAttribute("disabled").Should().BeTrue();
    }

    [Test]
    public void TopicsButton_WhenHasTopics_IsEnabled()
    {
        _mockMsgStore.GetSelectedTopicState().Returns(Selection(null));

        var cut = Render<MobileTopicBar>(p => p.Add(x => x.HasTopics, true));

        var btn = cut.FindAll("button").First(b => b.TextContent.Contains("Topics"));
        btn.HasAttribute("disabled").Should().BeFalse();
    }

    [Test]
    public async Task ClickingTopicsButton_InvokesOnOpenPicker()
    {
        var opened = false;
        _mockMsgStore.GetSelectedTopicState().Returns(Selection(null));

        var cut = Render<MobileTopicBar>(
            p => p.Add(x => x.HasTopics, true)
                  .Add(x => x.OnOpenPicker, () => { opened = true; return Task.CompletedTask; }));

        await cut.InvokeAsync(() =>
            cut.FindAll("button").First(b => b.TextContent.Contains("Topics")).Click());

        opened.Should().BeTrue();
    }

    [Test]
    public async Task ClickingInfoArea_WhenHasTopics_InvokesOnOpenPicker()
    {
        var opened = false;
        _mockMsgStore.GetSelectedTopicState().Returns(Selection("sensor/temp", 1));

        var cut = Render<MobileTopicBar>(
            p => p.Add(x => x.HasTopics, true)
                  .Add(x => x.OnOpenPicker, () => { opened = true; return Task.CompletedTask; }));

        await cut.InvokeAsync(() =>
            cut.Find(".mobile-topic-bar__info").Click());

        opened.Should().BeTrue();
    }

    [Test]
    public async Task ClickingInfoArea_WhenNoTopics_DoesNotInvokeOnOpenPicker()
    {
        var opened = false;
        _mockMsgStore.GetSelectedTopicState().Returns(Selection(null));

        var cut = Render<MobileTopicBar>(
            p => p.Add(x => x.HasTopics, false)
                  .Add(x => x.OnOpenPicker, () => { opened = true; return Task.CompletedTask; }));

        await cut.InvokeAsync(() =>
            cut.Find(".mobile-topic-bar__info").Click());

        opened.Should().BeFalse();
    }

    [Test]
    public async Task BulkSelection_ShowsTheCount_AndSelectStartsSelecting()
    {
        var started = false;
        var selection = new TopicBulkSelection(TopicTreeSelectionTestAdapter.Create());
        selection.Enter(["sensors/temp", "sensors/humidity"]);

        var cut = Render<MobileTopicBar>(p => p
            .AddCascadingValue(selection)
            .Add(x => x.HasTopics, true)
            .Add(x => x.OnSelectTopics, () => { started = true; return Task.CompletedTask; }));

        cut.Markup.Should().Contain("2 topics selected");

        await cut.InvokeAsync(() =>
            cut.Find(".mobile-topic-bar__select").Click());

        started.Should().BeTrue();
    }

    [Test]
    public async Task DisposeAsync_DoesNotThrow()
    {
        var cut = Render<MobileTopicBar>();

        var act = async () => await cut.Instance.DisposeAsync();

        await act.Should().NotThrowAsync();
    }
}
