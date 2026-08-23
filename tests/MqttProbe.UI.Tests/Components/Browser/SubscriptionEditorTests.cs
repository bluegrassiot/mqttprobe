using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MQTTnet.Protocol;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.UI.Tests.TestHelpers;
using MudBlazor;

namespace MqttProbe.UI.Tests.Components.Browser;

[TestFixture]
public class SubscriptionEditorTests : BunitTestContext
{
    [SetUp]
    public void Setup()
    {
        EnsureMudProviders();
    }

    private EventCallback<(string Topic, MqttQualityOfServiceLevel Qos)> NoAdd() =>
        EventCallback.Factory.Create<(string Topic, MqttQualityOfServiceLevel Qos)>(this, _ => { });

    private EventCallback<IReadOnlyList<string>> NoRemove() =>
        EventCallback.Factory.Create<IReadOnlyList<string>>(this, _ => { });

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
            .Add(x => x.OnRemove, NoRemove()));

        cut.Markup.Should().Contain("a/#");
        cut.Markup.Should().Contain("0 · At most once");
    }

    [Test]
    public async Task Add_InvokesOnAdd_WithTopicAndDefaultQos()
    {
        (string Topic, MqttQualityOfServiceLevel Qos)? captured = null;

        var cut = Render<SubscriptionEditor>(p => p
            .Add(x => x.Items, Array.Empty<SubscribedTopic>())
            .Add(x => x.OnAdd, EventCallback.Factory.Create<(string Topic, MqttQualityOfServiceLevel Qos)>(this,
                v => captured = v))
            .Add(x => x.OnRemove, NoRemove()));

        var topicInput = cut.FindAll("input")
            .First(e => !e.HasAttribute("readonly") && e.GetAttribute("type") != "checkbox");
        topicInput.Input(new ChangeEventArgs { Value = "sensors/#" });
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
                v => removed = v)));

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
            .Add(x => x.OnRemove, NoRemove()));

        cut.Find("button[title='Add subscription']").HasAttribute("disabled").Should().BeTrue();
    }

    [Test]
    public void Preset_FillsTopicDraft_DoesNotCallOnAdd()
    {
        var addCalled = false;

        var cut = Render<SubscriptionEditor>(p => p
            .Add(x => x.Items, Array.Empty<SubscribedTopic>())
            .Add(x => x.OnAdd, EventCallback.Factory.Create<(string Topic, MqttQualityOfServiceLevel Qos)>(this,
                _ => addCalled = true))
            .Add(x => x.OnRemove, NoRemove()));

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
            .Add(x => x.OnRemove, NoRemove()));

        cut.Markup.Should().Contain("No active subscriptions");
        cut.Markup.Should().Contain("Add a topic above to start receiving messages");
    }

    [Test]
    public void Input_ThenImmediateClick_AddsTopic()
    {
        (string Topic, MqttQualityOfServiceLevel Qos)? captured = null;

        var cut = Render<SubscriptionEditor>(p => p
            .Add(x => x.Items, Array.Empty<SubscribedTopic>())
            .Add(x => x.OnAdd, EventCallback.Factory.Create<(string Topic, MqttQualityOfServiceLevel Qos)>(this,
                v => captured = v))
            .Add(x => x.OnRemove, NoRemove()));

        var topicInput = cut.FindAll("input")
            .First(e => !e.HasAttribute("readonly") && e.GetAttribute("type") != "checkbox");
        topicInput.Input(new ChangeEventArgs { Value = "sensors/#" });
        cut.Find("button[title='Add subscription']").Click();

        captured.Should().NotBeNull();
        captured!.Value.Topic.Should().Be("sensors/#");
    }

    [Test]
    public async Task AddForTests_InvokesOnAdd_WithTrimmedTopicAndCurrentQos()
    {
        (string Topic, MqttQualityOfServiceLevel Qos)? captured = null;

        var cut = Render<SubscriptionEditor>(p => p
            .Add(x => x.Items, Array.Empty<SubscribedTopic>())
            .Add(x => x.OnAdd, EventCallback.Factory.Create<(string Topic, MqttQualityOfServiceLevel Qos)>(this,
                v => captured = v))
            .Add(x => x.OnRemove, NoRemove()));

        cut.Instance.TopicDraft = "  factory/#  ";
        cut.Instance.QosDraft = MqttQualityOfServiceLevel.ExactlyOnce;

        await cut.InvokeAsync(() => cut.Instance.AddForTests());

        captured.Should().NotBeNull();
        captured!.Value.Topic.Should().Be("factory/#");
        captured.Value.Qos.Should().Be(MqttQualityOfServiceLevel.ExactlyOnce);
    }

    [Test]
    public void MudExpansionPanel_Renders()
    {
        var cut = Render<SubscriptionEditor>(p => p
            .Add(x => x.Items, Array.Empty<SubscribedTopic>())
            .Add(x => x.OnAdd, NoAdd())
            .Add(x => x.OnRemove, NoRemove()));

        cut.FindComponent<MudExpansionPanel>().Should().NotBeNull();
    }

    [Test]
    public void ExpandedByDefault()
    {
        var cut = Render<SubscriptionEditor>(p => p
            .Add(x => x.Items, Array.Empty<SubscribedTopic>())
            .Add(x => x.OnAdd, NoAdd())
            .Add(x => x.OnRemove, NoRemove()));

        cut.FindComponent<MudExpansionPanel>().Instance.Expanded.Should().BeTrue();
    }

    [Test]
    public void CountChip_RendersWhenItemsPresent()
    {
        var items = new List<SubscribedTopic>
        {
            new() { Topic = "a/#" },
            new() { Topic = "b/#" },
            new() { Topic = "c/#" }
        };

        var cut = Render<SubscriptionEditor>(p => p
            .Add(x => x.Items, items)
            .Add(x => x.OnAdd, NoAdd())
            .Add(x => x.OnRemove, NoRemove()));

        var chip = cut.Find(".count-chip");
        chip.TextContent.Should().Be("3");
    }

    [Test]
    public void CountChip_ShowsZeroWhenNoItems()
    {
        var cut = Render<SubscriptionEditor>(p => p
            .Add(x => x.Items, Array.Empty<SubscribedTopic>())
            .Add(x => x.OnAdd, NoAdd())
            .Add(x => x.OnRemove, NoRemove()));

        var chip = cut.Find(".count-chip");
        chip.TextContent.Should().Be("0");
    }

    [Test]
    public void CountChip_HiddenWhenShowCountIsFalse()
    {
        var items = new List<SubscribedTopic>
        {
            new() { Topic = "a/#" },
            new() { Topic = "b/#" }
        };

        var cut = Render<SubscriptionEditor>(p => p
            .Add(x => x.Items, items)
            .Add(x => x.ShowCount, false)
            .Add(x => x.OnAdd, NoAdd())
            .Add(x => x.OnRemove, NoRemove()));

        cut.FindAll(".count-chip").Should().BeEmpty();
    }

    [Test]
    public void WhenExpanded_BodyContentIsRendered()
    {
        var cut = Render<SubscriptionEditor>(p => p
            .Add(x => x.Items, Array.Empty<SubscribedTopic>())
            .Add(x => x.OnAdd, NoAdd())
            .Add(x => x.OnRemove, NoRemove()));

        // SubscriptionEditor starts expanded — the body should contain the empty-state text
        cut.FindComponent<MudExpansionPanel>().Instance.Expanded.Should().BeTrue();
        cut.Markup.Should().Contain("No subscriptions");
    }

    [Test]
    public void ScrollContainer_Presence_WhenExpandedWithItems()
    {
        var items = new List<SubscribedTopic>
        {
            new() { Topic = "a/#" },
            new() { Topic = "b/#" },
            new() { Topic = "c/#" },
            new() { Topic = "d/#" }
        };

        var cut = Render<SubscriptionEditor>(p => p
            .Add(x => x.Items, items)
            .Add(x => x.OnAdd, NoAdd())
            .Add(x => x.OnRemove, NoRemove()));

        cut.Find(".subscription-editor-table-wrap").Should().NotBeNull();
        cut.Find(".mud-table-container").Should().NotBeNull();
    }

    [Test]
    public void Root_HasRegionRole_AndTabIndex()
    {
        var cut = Render<SubscriptionEditor>(p => p
            .Add(x => x.Items, Array.Empty<SubscribedTopic>())
            .Add(x => x.OnAdd, NoAdd())
            .Add(x => x.OnRemove, NoRemove()));

        var root = cut.Find(".subscription-editor");
        root.GetAttribute("role").Should().Be("region");
        root.GetAttribute("aria-label").Should().Be("Subscriptions list");
        root.GetAttribute("tabindex").Should().Be("0");
    }

    [Test]
    public void TableWrap_IsScrollRegion_WhenExpandedWithItems()
    {
        var items = new List<SubscribedTopic>
        {
            new() { Topic = "a/#" },
            new() { Topic = "b/#" },
            new() { Topic = "c/#" },
            new() { Topic = "d/#" }
        };

        var cut = Render<SubscriptionEditor>(p => p
            .Add(x => x.Items, items)
            .Add(x => x.OnAdd, NoAdd())
            .Add(x => x.OnRemove, NoRemove()));

        var wrap = cut.Find(".subscription-editor-table-wrap");
        wrap.Should().NotBeNull();
    }

    [Test]
    public void ExpandCollapse_CanBeToggled()
    {
        var cut = Render<SubscriptionEditor>(p => p
            .Add(x => x.Items, new List<SubscribedTopic> { new() { Topic = "a/#" } })
            .Add(x => x.OnAdd, NoAdd())
            .Add(x => x.OnRemove, NoRemove()));

        // Starts expanded
        cut.FindComponent<MudExpansionPanel>().Instance.Expanded.Should().BeTrue();

        // Click header to collapse
        cut.FindComponent<MudExpansionPanel>().Find(".mud-expand-panel-header").Click();
        cut.Render();

        cut.FindComponent<MudExpansionPanel>().Instance.Expanded.Should().BeFalse();
    }

    [Test]
    public void Selector_DisplaysFormattedQosLabels()
    {
        var cut = Render<SubscriptionEditor>(p => p
            .Add(x => x.Items, Array.Empty<SubscribedTopic>())
            .Add(x => x.OnAdd, NoAdd())
            .Add(x => x.OnRemove, NoRemove()));

        // MudSelect in closed state shows the selected value (AtLeastOnce by default).
        cut.Markup.Should().Contain("1 · At least once");
    }

    [Test]
    public void Table_DisplaysFormattedQosLabel()
    {
        var items = new List<SubscribedTopic>
        {
            new() { Topic = "a/#", QualityOfServiceLevel = MqttQualityOfServiceLevel.ExactlyOnce }
        };

        var cut = Render<SubscriptionEditor>(p => p
            .Add(x => x.Items, items)
            .Add(x => x.OnAdd, NoAdd())
            .Add(x => x.OnRemove, NoRemove()));

        cut.Markup.Should().Contain("2 · Exactly once");
    }
}
