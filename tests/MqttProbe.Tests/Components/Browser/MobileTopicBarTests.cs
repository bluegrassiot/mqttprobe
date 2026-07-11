using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using MqttProbe.Models.Mqtt;
using MqttProbe.Services.Mqtt;
using MqttProbe.Shared.Tests.TestHelpers;
using MudBlazor;

namespace MqttProbe.Shared.Tests.Components.Browser;

[TestFixture]
public class MobileTopicBarTests : BunitTestContext
{
    private IMessageStoreManager _mockMsgStore = null!;

    [SetUp]
    public void SetupMocks()
    {
        _mockMsgStore = Substitute.For<IMessageStoreManager>();
        _mockMsgStore.MessageStores.Returns(new ConcurrentDictionary<string, MessageStore>());
        Services.AddSingleton(_mockMsgStore);
        EnsureMudProviders();
    }

    [Test]
    public void Renders_ChooseTopic_WhenNoTopicSelected()
    {
        _mockMsgStore.SelectedMessageStore.Returns((MessageStore?)null);

        var cut = Render<MobileTopicBar>();

        cut.Markup.Should().Contain("Choose a topic");
    }

    [Test]
    public void Renders_SelectedTopicName_WhenTopicSelected()
    {
        var store = new MessageStore { Topic = "sensor/temp", FullTopic = "sensor/temp", MessageCount = 5 };
        _mockMsgStore.SelectedMessageStore.Returns(store);

        var cut = Render<MobileTopicBar>();

        cut.Markup.Should().Contain("sensor/temp");
    }

    [Test]
    public void Renders_MessageCount_WhenTopicHasMessages()
    {
        var store = new MessageStore { Topic = "sensor/temp", FullTopic = "sensor/temp", MessageCount = 42 };
        _mockMsgStore.SelectedMessageStore.Returns(store);

        var cut = Render<MobileTopicBar>();

        cut.Markup.Should().Contain("42");
    }

    [Test]
    public void TopicsButton_WhenNoTopics_IsDisabled()
    {
        _mockMsgStore.SelectedMessageStore.Returns((MessageStore?)null);

        var cut = Render<MobileTopicBar>(p => p.Add(x => x.HasTopics, false));

        var btn = cut.FindAll("button").First(b => b.TextContent.Contains("Topics"));
        btn.HasAttribute("disabled").Should().BeTrue();
    }

    [Test]
    public void TopicsButton_WhenHasTopics_IsEnabled()
    {
        _mockMsgStore.SelectedMessageStore.Returns((MessageStore?)null);

        var cut = Render<MobileTopicBar>(p => p.Add(x => x.HasTopics, true));

        var btn = cut.FindAll("button").First(b => b.TextContent.Contains("Topics"));
        btn.HasAttribute("disabled").Should().BeFalse();
    }

    [Test]
    public async Task ClickingTopicsButton_InvokesOnOpenPicker()
    {
        var opened = false;
        _mockMsgStore.SelectedMessageStore.Returns((MessageStore?)null);

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
        var store = new MessageStore { Topic = "sensor/temp", FullTopic = "sensor/temp", MessageCount = 1 };
        _mockMsgStore.SelectedMessageStore.Returns(store);

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
        _mockMsgStore.SelectedMessageStore.Returns((MessageStore?)null);

        var cut = Render<MobileTopicBar>(
            p => p.Add(x => x.HasTopics, false)
                  .Add(x => x.OnOpenPicker, () => { opened = true; return Task.CompletedTask; }));

        await cut.InvokeAsync(() =>
            cut.Find(".mobile-topic-bar__info").Click());

        opened.Should().BeFalse();
    }

    [Test]
    public async Task DisposeAsync_DoesNotThrow()
    {
        var cut = Render<MobileTopicBar>();

        var act = async () => await cut.Instance.DisposeAsync();

        await act.Should().NotThrowAsync();
    }
}
