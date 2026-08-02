using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MQTTnet.Protocol;
using MqttProbe.Models.Mqtt;
using MqttProbe.Shared.Tests.TestHelpers;
using MudBlazor;

namespace MqttProbe.Shared.Tests.Components.Browser;

[TestFixture]
public class SubscriptionEditorTests : BunitTestContext
{
    private IDialogService _mockDialog = null!;

    [SetUp]
    public void Setup()
    {
        _mockDialog = Substitute.For<IDialogService>();
        Services.AddSingleton(_mockDialog);

        EnsureMudProviders();
    }

    private EventCallback<(string Topic, MqttQualityOfServiceLevel Qos)> NoAdd() =>
        EventCallback.Factory.Create<(string Topic, MqttQualityOfServiceLevel Qos)>(this, _ => { });

    private EventCallback<IReadOnlyList<string>> NoRemove() =>
        EventCallback.Factory.Create<IReadOnlyList<string>>(this, _ => { });

    private EventCallback NoClearAll() =>
        EventCallback.Factory.Create(this, () => { });

    [Test]
    public void Renders_Items_TopicAndQos()
    {
        var items = new List<SubscribedTopic>
        {
            new() { Topic = "a/#", QualityOfServiceLevel = MqttQualityOfServiceLevel.AtMostOnce }
        };

        var cut = Render<SubscriptionEditor>(p => p
            .Add(x => x.Items, items)
            .Add(x => x.OnAdd, NoAdd())
            .Add(x => x.OnRemove, NoRemove())
            .Add(x => x.OnClearAll, NoClearAll()));

        cut.Markup.Should().Contain("a/#");
        cut.Markup.Should().Contain("AtMostOnce");
    }

    [Test]
    public async Task Add_InvokesOnAdd_WithTopicAndDefaultQos()
    {
        (string Topic, MqttQualityOfServiceLevel Qos)? captured = null;

        var cut = Render<SubscriptionEditor>(p => p
            .Add(x => x.Items, Array.Empty<SubscribedTopic>())
            .Add(x => x.OnAdd, EventCallback.Factory.Create<(string Topic, MqttQualityOfServiceLevel Qos)>(this,
                v => captured = v))
            .Add(x => x.OnRemove, NoRemove())
            .Add(x => x.OnClearAll, NoClearAll()));

        var topicInput = cut.FindAll("input")
            .First(e => !e.HasAttribute("readonly") && e.GetAttribute("type") != "checkbox");
        topicInput.Change("sensors/#");
        cut.Find("button[title='Add subscription']").Click();

        captured.Should().NotBeNull();
        captured!.Value.Topic.Should().Be("sensors/#");
        captured.Value.Qos.Should().Be(MqttQualityOfServiceLevel.AtLeastOnce);
        await Task.CompletedTask;
    }

    [Test]
    public async Task Remove_InvokesOnRemove_WithSelectedTopics()
    {
        IReadOnlyList<string>? removed = null;
        var items = new List<SubscribedTopic> { new() { Topic = "x/#" } };

        var cut = Render<SubscriptionEditor>(p => p
            .Add(x => x.Items, items)
            .Add(x => x.OnAdd, NoAdd())
            .Add(x => x.OnRemove, EventCallback.Factory.Create<IReadOnlyList<string>>(this,
                v => removed = v))
            .Add(x => x.OnClearAll, NoClearAll()));

        cut.Find("input[type='checkbox']").Change(true);
        cut.Find("button[title='Remove']").Click();

        removed.Should().ContainSingle().Which.Should().Be("x/#");
        await Task.CompletedTask;
    }

    [Test]
    public void Disabled_DisablesAddControls()
    {
        var cut = Render<SubscriptionEditor>(p => p
            .Add(x => x.Items, Array.Empty<SubscribedTopic>())
            .Add(x => x.Disabled, true)
            .Add(x => x.OnAdd, NoAdd())
            .Add(x => x.OnRemove, NoRemove())
            .Add(x => x.OnClearAll, NoClearAll()));

        cut.Find("button[title='Add subscription']").HasAttribute("disabled").Should().BeTrue();
    }

    [Test]
    public async Task ClearAll_WhenConfirmed_InvokesOnClearAll()
    {
        _mockDialog.ShowMessageBoxAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DialogOptions>())
            .Returns(Task.FromResult<bool?>(true));

        var cleared = false;
        var items = new List<SubscribedTopic> { new() { Topic = "a" }, new() { Topic = "b" } };

        var cut = Render<SubscriptionEditor>(p => p
            .Add(x => x.Items, items)
            .Add(x => x.OnAdd, NoAdd())
            .Add(x => x.OnRemove, NoRemove())
            .Add(x => x.OnClearAll, EventCallback.Factory.Create(this, () => cleared = true)));

        cut.Find("button[title='Clear all']").Click();
        await cut.InvokeAsync(() => { });

        cleared.Should().BeTrue();
    }

    [Test]
    public async Task ClearAll_WhenCancelled_DoesNotInvokeOnClearAll()
    {
        _mockDialog.ShowMessageBoxAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DialogOptions>())
            .Returns(Task.FromResult<bool?>(false));

        var cleared = false;
        var items = new List<SubscribedTopic> { new() { Topic = "a" } };

        var cut = Render<SubscriptionEditor>(p => p
            .Add(x => x.Items, items)
            .Add(x => x.OnAdd, NoAdd())
            .Add(x => x.OnRemove, NoRemove())
            .Add(x => x.OnClearAll, EventCallback.Factory.Create(this, () => cleared = true)));

        cut.Find("button[title='Clear all']").Click();
        await cut.InvokeAsync(() => { });

        cleared.Should().BeFalse();
        await Task.CompletedTask;
    }

    [Test]
    public void Preset_FillsTopicDraft_DoesNotCallOnAdd()
    {
        var addCalled = false;

        var cut = Render<SubscriptionEditor>(p => p
            .Add(x => x.Items, Array.Empty<SubscribedTopic>())
            .Add(x => x.OnAdd, EventCallback.Factory.Create<(string Topic, MqttQualityOfServiceLevel Qos)>(this,
                _ => addCalled = true))
            .Add(x => x.OnRemove, NoRemove())
            .Add(x => x.OnClearAll, NoClearAll()));

        var chip = cut.FindAll(".mud-chip, button")
            .First(e => e.TextContent.Contains("spBv1.0/#"));
        chip.Click();

        addCalled.Should().BeFalse();
        cut.Instance.TopicDraft.Should().Be("spBv1.0/#");
    }

    [Test]
    public void EmptyText_AndEmptyHint_Render_WhenItemsEmpty()
    {
        var cut = Render<SubscriptionEditor>(p => p
            .Add(x => x.Items, Array.Empty<SubscribedTopic>())
            .Add(x => x.EmptyText, "No active subscriptions")
            .Add(x => x.EmptyHint, "Add a topic above to start receiving messages")
            .Add(x => x.OnAdd, NoAdd())
            .Add(x => x.OnRemove, NoRemove())
            .Add(x => x.OnClearAll, NoClearAll()));

        cut.Markup.Should().Contain("No active subscriptions");
        cut.Markup.Should().Contain("Add a topic above to start receiving messages");
    }

    [Test]
    public async Task Disabled_BlocksClearAll_WithoutPromptingDialog()
    {
        var items = new List<SubscribedTopic> { new() { Topic = "a" } };

        var cut = Render<SubscriptionEditor>(p => p
            .Add(x => x.Items, items)
            .Add(x => x.Disabled, true)
            .Add(x => x.OnAdd, NoAdd())
            .Add(x => x.OnRemove, NoRemove())
            .Add(x => x.OnClearAll, NoClearAll()));

        cut.Find("button[title='Clear all']").HasAttribute("disabled").Should().BeTrue();

        await cut.InvokeAsync(() => { });

        await _mockDialog.DidNotReceive().ShowMessageBoxAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DialogOptions>());
    }

    [Test]
    public async Task AddForTests_InvokesOnAdd_WithTrimmedTopicAndCurrentQos()
    {
        (string Topic, MqttQualityOfServiceLevel Qos)? captured = null;

        var cut = Render<SubscriptionEditor>(p => p
            .Add(x => x.Items, Array.Empty<SubscribedTopic>())
            .Add(x => x.OnAdd, EventCallback.Factory.Create<(string Topic, MqttQualityOfServiceLevel Qos)>(this,
                v => captured = v))
            .Add(x => x.OnRemove, NoRemove())
            .Add(x => x.OnClearAll, NoClearAll()));

        cut.Instance.TopicDraft = "  factory/#  ";
        cut.Instance.QosDraft = MqttQualityOfServiceLevel.ExactlyOnce;

        await cut.InvokeAsync(() => cut.Instance.AddForTests());

        captured.Should().NotBeNull();
        captured!.Value.Topic.Should().Be("factory/#");
        captured.Value.Qos.Should().Be(MqttQualityOfServiceLevel.ExactlyOnce);
    }
}
