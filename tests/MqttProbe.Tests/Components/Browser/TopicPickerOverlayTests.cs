using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using MqttProbe.Models.Mqtt;
using MqttProbe.Services.Mqtt;
using MqttProbe.Shared.Tests.TestHelpers;

namespace MqttProbe.Shared.Tests.Components.Browser;

[TestFixture]
public class TopicPickerOverlayTests : BunitTestContext
{
    private IMessageStoreManager _mockMsgStore = null!;

    [SetUp]
    public void SetupMocks()
    {
        _mockMsgStore = Substitute.For<IMessageStoreManager>();
        _mockMsgStore.MessageStores.Returns(new ConcurrentDictionary<string, MessageStore>());
        Services.AddSingleton(_mockMsgStore);

        ComponentFactories.AddStub<TopicBrowser>();
        EnsureMudProviders();
    }

    [Test]
    public void WhenIsOpen_False_RendersNothing()
    {
        var cut = Render<TopicPickerOverlay>(p => p.Add(x => x.IsOpen, false));

        cut.Markup.Trim().Should().BeEmpty();
    }

    [Test]
    public void WhenIsOpen_True_RendersOverlay()
    {
        var cut = Render<TopicPickerOverlay>(p => p.Add(x => x.IsOpen, true));

        cut.Markup.Should().Contain("topic-picker-overlay");
        cut.Markup.Should().Contain("Topics");
    }

    [Test]
    public void WhenIsOpen_True_RendersCloseButton()
    {
        var cut = Render<TopicPickerOverlay>(p => p.Add(x => x.IsOpen, true));

        cut.Find("button[title='Close']").Should().NotBeNull();
    }

    [Test]
    public void WhenIsOpen_True_TitleUsesCssClass_NotInlineStyle()
    {
        var cut = Render<TopicPickerOverlay>(p => p.Add(x => x.IsOpen, true));

        var title = cut.Find(".topic-picker-overlay__title");
        title.GetAttribute("style").Should().NotContain("font-weight");
        title.ClassList.Should().Contain("topic-picker-overlay__title");
    }

    [Test]
    public async Task ClickingCloseButton_InvokesOnClose()
    {
        var closed = false;
        var cut = Render<TopicPickerOverlay>(
            p => p.Add(x => x.IsOpen, true)
                  .Add(x => x.OnClose, () => { closed = true; return Task.CompletedTask; }));

        await cut.InvokeAsync(() => cut.Find("button[title='Close']").Click());

        closed.Should().BeTrue();
    }

    [Test]
    public async Task ClickingBackdrop_InvokesOnClose()
    {
        var closed = false;
        var cut = Render<TopicPickerOverlay>(
            p => p.Add(x => x.IsOpen, true)
                  .Add(x => x.OnClose, () => { closed = true; return Task.CompletedTask; }));

        await cut.InvokeAsync(() => cut.Find(".topic-picker-overlay").Click());

        closed.Should().BeTrue();
    }

    [Test]
    public void PanelIsNestedInsideOverlay_ForStopPropagation()
    {
        var cut = Render<TopicPickerOverlay>(p => p.Add(x => x.IsOpen, true));

        // The overlay has @onclick="CloseWithoutSelection" and the panel inside
        // has @onclick:stopPropagation to prevent clicks on the panel from
        // bubbling to the overlay. Verify the nesting structure.
        var overlay = cut.Find(".topic-picker-overlay");
        var panel = overlay.QuerySelector(".topic-picker-overlay__panel");
        panel.Should().NotBeNull();
        // The panel must be a descendant of the overlay (clicks inside it
        // are stopped by @onclick:stopPropagation on MudPaper).
        overlay.Children.Should().Contain(panel!);
    }
}
