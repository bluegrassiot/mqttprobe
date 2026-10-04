using Microsoft.AspNetCore.Components;
using MqttProbe.UI.Tests.TestHelpers;
using MudBlazor;

namespace MqttProbe.UI.Tests.Components.Browser;

[TestFixture]
public class ExcludeEditorTests : BunitTestContext
{
    [SetUp]
    public void Setup()
    {
        EnsureMudProviders();
    }

    private static Func<string, Task<bool>> NoAdd() => _ => Task.FromResult(true);

    private EventCallback<IReadOnlyList<string>> NoRemove() =>
        EventCallback.Factory.Create<IReadOnlyList<string>>(this, _ => { });

    private Func<string, Task<bool>> AcceptedAdd() => _ => Task.FromResult(true);

    private static Func<string, Task<bool>> RejectedAdd() => _ => Task.FromResult(false);

    private Func<string, Task<bool>> CapturingAdd(Action<string> capture) =>
        v =>
        {
            capture(v);
            return Task.FromResult(true);
        };

    [Test]
    public void Renders_Items_TopicOnly()
    {
        var items = new List<string> { "$SYS/#", "sensors/+/temp" };

        var cut = Render<ExcludeEditor>(p => p
            .Add(x => x.Items, items)
            .Add(x => x.OnAdd, NoAdd())
            .Add(x => x.OnRemove, NoRemove()));

        cut.Markup.Should().Contain("$SYS/#");
        cut.Markup.Should().Contain("sensors/+/temp");
    }

    [Test]
    public void EmptyText_RendersWhenItemsEmpty()
    {
        var cut = Render<ExcludeEditor>(p => p
            .Add(x => x.Items, Array.Empty<string>())
            .Add(x => x.OnAdd, NoAdd())
            .Add(x => x.OnRemove, NoRemove()));

        cut.Markup.Should().Contain("Excluded topics");
    }

    [Test]
    public void Add_InvokesOnAdd_WithTopic()
    {
        string? captured = null;

        var cut = Render<ExcludeEditor>(p => p
            .Add(x => x.Items, new List<string> { "$SYS/#" })
            .Add(x => x.OnAdd, CapturingAdd(v => captured = v))
            .Add(x => x.OnRemove, NoRemove()));

        // With items, the panel starts expanded and body renders
        cut.FindComponent<MudExpansionPanel>().Instance.Expanded.Should().BeTrue();

        var topicInput = cut.FindAll("input")
            .First(e => !e.HasAttribute("readonly") && e.GetAttribute("type") != "checkbox");
        topicInput.Input(new ChangeEventArgs { Value = "$SYS/#" });
        cut.Find("button[title='Add exclude topic']").Click();

        captured.Should().NotBeNull();
        captured!.Should().Be("$SYS/#");
    }

    [Test]
    public async Task Remove_InvokesOnRemove_WithSelectedTopics()
    {
        IReadOnlyList<string>? removed = null;
        var items = new List<string> { "$SYS/#" };

        var cut = Render<ExcludeEditor>(p => p
            .Add(x => x.Items, items)
            .Add(x => x.OnAdd, NoAdd())
            .Add(x => x.OnRemove, EventCallback.Factory.Create<IReadOnlyList<string>>(this,
                v => removed = v)));

        // Items present so panel should be expanded; but with KeepContentAlive=false
        // we need to confirm expansion.
        cut.FindComponent<MudExpansionPanel>().Instance.Expanded.Should().BeTrue();

        cut.Find("input[type='checkbox']").Change(true);
        cut.Find("button[title='Remove']").Click();

        removed.Should().ContainSingle().Which.Should().Be("$SYS/#");
        await Task.CompletedTask;
    }

    [Test]
    public void Disabled_DisablesAddControls()
    {
        var cut = Render<ExcludeEditor>(p => p
            .Add(x => x.Items, new List<string> { "$SYS/#" })
            .Add(x => x.Disabled, true)
            .Add(x => x.OnAdd, NoAdd())
            .Add(x => x.OnRemove, NoRemove()));

        // With items, the panel starts expanded and body renders
        cut.FindComponent<MudExpansionPanel>().Instance.Expanded.Should().BeTrue();

        cut.Find("button[title='Add exclude topic']").HasAttribute("disabled").Should().BeTrue();
    }

    [Test]
    public void Preset_FillsTopicDraft_DoesNotCallOnAdd()
    {
        var addCalled = false;

        var cut = Render<ExcludeEditor>(p => p
            .Add(x => x.Items, new List<string> { "$SYS/#" })
            .Add(x => x.OnAdd, CapturingAdd(_ => addCalled = true))
            .Add(x => x.OnRemove, NoRemove()));

        var chip = cut.FindAll(".mud-chip, button")
            .First(e => e.TextContent.Contains("$SYS/#"));
        chip.Click();

        addCalled.Should().BeFalse();
        cut.Instance.TopicDraft.Should().Be("$SYS/#");
    }

    [Test]
    public void Input_ThenImmediateClick_AddsTopic()
    {
        string? captured = null;

        var cut = Render<ExcludeEditor>(p => p
            .Add(x => x.Items, new List<string> { "$SYS/#" })
            .Add(x => x.OnAdd, CapturingAdd(v => captured = v))
            .Add(x => x.OnRemove, NoRemove()));

        var topicInput = cut.FindAll("input")
            .First(e => !e.HasAttribute("readonly") && e.GetAttribute("type") != "checkbox");
        topicInput.Input(new ChangeEventArgs { Value = "$SYS/#" });
        cut.Find("button[title='Add exclude topic']").Click();

        captured.Should().NotBeNull();
        captured!.Should().Be("$SYS/#");
    }

    [Test]
    public async Task AddForTests_InvokesOnAdd_WithTrimmedTopic()
    {
        string? captured = null;

        var cut = Render<ExcludeEditor>(p => p
            .Add(x => x.Items, Array.Empty<string>())
            .Add(x => x.OnAdd, CapturingAdd(v => captured = v))
            .Add(x => x.OnRemove, NoRemove()));

        cut.Instance.TopicDraft = "  $SYS/#  ";

        await cut.InvokeAsync(() => cut.Instance.AddForTests());

        captured.Should().NotBeNull();
        captured!.Should().Be("$SYS/#");
    }

    [Test]
    public void NoQosColumn_WhenItemsPresent()
    {
        var items = new List<string> { "$SYS/#" };

        var cut = Render<ExcludeEditor>(p => p
            .Add(x => x.Items, items)
            .Add(x => x.OnAdd, NoAdd())
            .Add(x => x.OnRemove, NoRemove()));

        cut.Markup.Should().NotContain("QoS");
        cut.Markup.Should().NotContain("QualityOfService");
    }

    [Test]
    public void RemoveButton_WhenNothingSelected_IsDisabled()
    {
        var items = new List<string> { "$SYS/#" };

        var cut = Render<ExcludeEditor>(p => p
            .Add(x => x.Items, items)
            .Add(x => x.OnAdd, NoAdd())
            .Add(x => x.OnRemove, NoRemove()));

        cut.Find("button[title='Remove']").HasAttribute("disabled").Should().BeTrue();
    }

    [Test]
    public void MudExpansionPanel_Renders()
    {
        var cut = Render<ExcludeEditor>(p => p
            .Add(x => x.Items, Array.Empty<string>())
            .Add(x => x.OnAdd, NoAdd())
            .Add(x => x.OnRemove, NoRemove()));

        cut.FindComponent<MudExpansionPanel>().Should().NotBeNull();
    }

    [Test]
    public void EmptyItems_RendersCollapsedByDefault()
    {
        var cut = Render<ExcludeEditor>(p => p
            .Add(x => x.Items, Array.Empty<string>())
            .Add(x => x.OnAdd, NoAdd())
            .Add(x => x.OnRemove, NoRemove()));

        cut.FindComponent<MudExpansionPanel>().Instance.Expanded.Should().BeFalse();
    }

    [Test]
    public void WithItems_RendersExpandedByDefault()
    {
        var items = new List<string> { "$SYS/#" };

        var cut = Render<ExcludeEditor>(p => p
            .Add(x => x.Items, items)
            .Add(x => x.OnAdd, NoAdd())
            .Add(x => x.OnRemove, NoRemove()));

        cut.FindComponent<MudExpansionPanel>().Instance.Expanded.Should().BeTrue();
    }

    [Test]
    public void NoEyeIcon_InMarkup()
    {
        var cut = Render<ExcludeEditor>(p => p
            .Add(x => x.Items, Array.Empty<string>())
            .Add(x => x.OnAdd, NoAdd())
            .Add(x => x.OnRemove, NoRemove()));

        cut.Markup.Should().NotContain("EyeOff");
    }

    [Test]
    public void NoClearAllButton_InMarkup()
    {
        var items = new List<string> { "$SYS/#" };

        var cut = Render<ExcludeEditor>(p => p
            .Add(x => x.Items, items)
            .Add(x => x.OnAdd, NoAdd())
            .Add(x => x.OnRemove, NoRemove()));

        cut.Markup.Should().NotContain("Clear all");
    }

    [Test]
    public void CountChip_RendersWhenItemsPresent()
    {
        var items = new List<string> { "$SYS/#", "sensors/+/temp" };

        var cut = Render<ExcludeEditor>(p => p
            .Add(x => x.Items, items)
            .Add(x => x.OnAdd, NoAdd())
            .Add(x => x.OnRemove, NoRemove()));

        var chip = cut.Find(".count-chip");
        chip.TextContent.Should().Be("2");
    }

    [Test]
    public void CountChip_ShowsZeroWhenNoItems()
    {
        var cut = Render<ExcludeEditor>(p => p
            .Add(x => x.Items, Array.Empty<string>())
            .Add(x => x.OnAdd, NoAdd())
            .Add(x => x.OnRemove, NoRemove()));

        var chip = cut.Find(".count-chip");
        chip.TextContent.Should().Be("0");
    }

    [Test]
    public void ExpandCollapse_InitialStateDependsOnItems()
    {
        // With items: expanded
        var cutWithItems = Render<ExcludeEditor>(p => p
            .Add(x => x.Items, new List<string> { "$SYS/#" })
            .Add(x => x.OnAdd, NoAdd())
            .Add(x => x.OnRemove, NoRemove()));

        cutWithItems.FindComponent<MudExpansionPanel>().Instance.Expanded.Should().BeTrue();

        // Without items: collapsed
        var cutEmpty = Render<ExcludeEditor>(p => p
            .Add(x => x.Items, Array.Empty<string>())
            .Add(x => x.OnAdd, NoAdd())
            .Add(x => x.OnRemove, NoRemove()));

        cutEmpty.FindComponent<MudExpansionPanel>().Instance.Expanded.Should().BeFalse();
    }

    [Test]
    public void Collapsed_NoBodyContent()
    {
        // When collapsed, the editor root should indicate collapsed state
        var cut = Render<ExcludeEditor>(p => p
            .Add(x => x.Items, Array.Empty<string>())
            .Add(x => x.OnAdd, NoAdd())
            .Add(x => x.OnRemove, NoRemove()));

        cut.FindComponent<MudExpansionPanel>().Instance.Expanded.Should().BeFalse();
    }

    [Test]
    public void ScrollContainer_Presence_WhenExpandedWithItems()
    {
        var items = new List<string> { "$SYS/#", "sensors/+/temp", "a/b/c", "x/y/z" };

        var cut = Render<ExcludeEditor>(p => p
            .Add(x => x.Items, items)
            .Add(x => x.OnAdd, NoAdd())
            .Add(x => x.OnRemove, NoRemove()));

        cut.Find(".exclude-editor-table-wrap").Should().NotBeNull();
        // The table container inside should be the scroll viewport
        cut.Find(".mud-table-container").Should().NotBeNull();
    }

    [Test]
    public void Root_HasRegionRole_AndTabIndex()
    {
        var cut = Render<ExcludeEditor>(p => p
            .Add(x => x.Items, Array.Empty<string>())
            .Add(x => x.OnAdd, NoAdd())
            .Add(x => x.OnRemove, NoRemove()));

        var root = cut.Find(".exclude-editor");
        root.GetAttribute("role").Should().Be("region");
        root.GetAttribute("aria-label").Should().Be("Excluded topics list");
        root.GetAttribute("tabindex").Should().Be("0");
    }

    [Test]
    public void TableWrap_IsScrollRegion_WhenExpandedWithItems()
    {
        var items = new List<string> { "$SYS/#", "sensors/+/temp", "a/b/c", "x/y/z" };

        var cut = Render<ExcludeEditor>(p => p
            .Add(x => x.Items, items)
            .Add(x => x.OnAdd, NoAdd())
            .Add(x => x.OnRemove, NoRemove()));

        cut.Find(".exclude-editor-table-wrap").Should().NotBeNull();
    }

    [Test]
    public void WildcardGuidance_RendersInPresets()
    {
        var cut = Render<ExcludeEditor>(p => p
            .Add(x => x.Items, Array.Empty<string>())
            .Add(x => x.OnAdd, NoAdd())
            .Add(x => x.OnRemove, NoRemove()));

        cut.Markup.Should().Contain("Wildcards:");
        cut.Markup.Should().Contain("matches one level");
        cut.Markup.Should().Contain("matches remaining levels");
    }

    [Test]
    public void NoTableWhenCollapsed_Empty()
    {
        var cut = Render<ExcludeEditor>(p => p
            .Add(x => x.Items, Array.Empty<string>())
            .Add(x => x.OnAdd, NoAdd())
            .Add(x => x.OnRemove, NoRemove()));

        cut.FindAll(".exclude-editor-table-wrap").Should().BeEmpty();
    }

    [Test]
    public async Task Add_WhenRejected_KeepsTopicDraft()
    {
        var cut = Render<ExcludeEditor>(p => p
            .Add(x => x.Items, new List<string> { "$SYS/#" })
            .Add(x => x.OnAdd, RejectedAdd())
            .Add(x => x.OnRemove, NoRemove()));

        cut.Instance.TopicDraft = "$SYS/#";

        await cut.InvokeAsync(() => cut.Instance.AddForTests());

        cut.Instance.TopicDraft.Should().Be("$SYS/#");
    }

    [Test]
    public async Task Add_WhenAccepted_ClearsTopicDraft()
    {
        var cut = Render<ExcludeEditor>(p => p
            .Add(x => x.Items, new List<string> { "$SYS/#" })
            .Add(x => x.OnAdd, AcceptedAdd())
            .Add(x => x.OnRemove, NoRemove()));

        cut.Instance.TopicDraft = "sensors/+/temp";

        await cut.InvokeAsync(() => cut.Instance.AddForTests());

        cut.Instance.TopicDraft.Should().BeEmpty();
    }

    [Test]
    public void RemoveButton_DisablesWhenSelectionNoLongerInItems()
    {
        var removed = false;
        var cut = Render<ExcludeEditor>(p => p
            .Add(x => x.Items, new List<string> { "stale/topic" })
            .Add(x => x.OnAdd, NoAdd())
            .Add(x => x.OnRemove, EventCallback.Factory.Create<IReadOnlyList<string>>(this,
                _ => removed = true)));

        // Row 0's checkbox; index 0 is the header select-all.
        cut.FindAll("input[type='checkbox']")[1].Change(true);
        cut.Find("button[title='Remove']").HasAttribute("disabled").Should().BeFalse();

        // Switching connection replaces the whole list, so the selection goes stale.
        cut.Render(p => p.Add(x => x.Items, new List<string> { "other/topic" }));

        cut.Find("button[title='Remove']").HasAttribute("disabled").Should().BeTrue();

        cut.InvokeAsync(() => cut.Instance.RemoveSelectedForTests()).Wait();
        removed.Should().BeFalse();
    }
}
