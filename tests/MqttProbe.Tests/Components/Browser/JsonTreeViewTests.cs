using System.Text.Json;
using Microsoft.AspNetCore.Components;
using MqttProbe.Models.Chart;
using MqttProbe.Shared.Tests.TestHelpers;

namespace MqttProbe.Shared.Tests.Components.Browser;

[TestFixture]
public class JsonTreeViewTests : BunitTestContext
{
    [Test]
    public void NullJson_RendersEmpty()
    {
        var cut = Render<JsonTreeView>(p => p.Add(x => x.Json, (string?)null));
        cut.FindAll(".json-tree-root").Should().BeEmpty();
    }

    [Test]
    public void InvalidJson_RendersEmpty()
    {
        var cut = Render<JsonTreeView>(p => p.Add(x => x.Json, "{not valid json"));
        cut.FindAll(".json-tree-root").Should().BeEmpty();
    }

    [Test]
    public void InvalidJson_RendersFallbackMessage()
    {
        var cut = Render<JsonTreeView>(p => p.Add(x => x.Json, "{not valid json"));
        cut.Find(".json-tree-error").TextContent.Should().Contain("not valid JSON");
    }

    [Test]
    public void ValidJson_RendersTreeNodesAndNoFallback()
    {
        var cut = Render<JsonTreeView>(p => p.Add(x => x.Json, """{"name":"Alice"}"""));
        cut.FindAll(".json-tree-root").Should().NotBeEmpty();
        cut.FindAll(".json-tree-error").Should().BeEmpty();
    }

    [Test]
    public void SimpleObject_RendersKeysAndValues()
    {
        var cut = Render<JsonTreeView>(p => p.Add(x => x.Json, """{"name":"Alice","age":30}"""));
        cut.Markup.Should().Contain("name");
        cut.Markup.Should().Contain("age");
    }

    [Test]
    public void StringValue_HasJsonStringClass()
    {
        var cut = Render<JsonTreeView>(p => p.Add(x => x.Json, """{"name":"Alice"}"""));
        cut.Find(".json-string").TextContent.Should().Contain("Alice");
    }

    [Test]
    public void NumberValue_HasJsonNumberClass()
    {
        var cut = Render<JsonTreeView>(p => p.Add(x => x.Json, """{"count":42}"""));
        cut.Find(".json-number").TextContent.Should().Be("42");
    }

    [Test]
    public void TrueValue_HasJsonTrueClass()
    {
        var cut = Render<JsonTreeView>(p => p.Add(x => x.Json, """{"flag":true}"""));
        cut.Find(".json-true").Should().NotBeNull();
    }

    [Test]
    public void FalseValue_HasJsonFalseClass()
    {
        var cut = Render<JsonTreeView>(p => p.Add(x => x.Json, """{"flag":false}"""));
        cut.Find(".json-false").Should().NotBeNull();
    }

    [Test]
    public void NullValue_ShowsNullTextWithClass()
    {
        var cut = Render<JsonTreeView>(p => p.Add(x => x.Json, """{"val":null}"""));
        cut.Find(".json-null").TextContent.Should().Be("null");
    }

    [Test]
    public void Array_RendersAllItems()
    {
        var cut = Render<JsonTreeView>(p => p.Add(x => x.Json, "[1,2,3]"));
        cut.FindAll(".json-number").Should().HaveCount(3);
    }

    [Test]
    public void EmptyObject_RendersWithoutToggle()
    {
        var cut = Render<JsonTreeView>(p => p.Add(x => x.Json, "{}"));
        cut.FindAll(".json-toggle").Should().BeEmpty();
    }

    [Test]
    public void DepthTwo_AutoExpandsDepthZeroAndOne_CollapsesDepthTwo()
    {
        // {"a":{"b":{"c":"deep"}}}
        // depth 0: root object → expanded (shows key "a" as .json-key)
        // depth 1: {"b":...}   → expanded (shows key "b" as .json-key)
        // depth 2: {"c":"deep"} → collapsed (shows as .json-preview, not a .json-key row)
        var cut = Render<JsonTreeView>(p => p.Add(x => x.Json, """{"a":{"b":{"c":"deep"}}}"""));
        var keys = cut.FindAll(".json-key").Select(e => e.TextContent).ToList();
        keys.Should().Contain("\"a\"");
        keys.Should().Contain("\"b\"");
        keys.Should().NotContain("\"c\""); // "c" is in the preview, not a real key row
        cut.FindAll(".json-preview").Should().HaveCount(1); // one collapsed node
    }

    [Test]
    public void Toggle_ExpandsCollapsedNode()
    {
        var json = """{"a":{"b":{"c":"deep"}}}""";
        var cut = Render<JsonTreeView>(p => p.Add(x => x.Json, json));

        // depth-2 node is collapsed — one preview element, "c" not yet a .json-key
        cut.FindAll(".json-preview").Should().HaveCount(1);
        cut.FindAll(".json-key").Select(e => e.TextContent).Should().NotContain("\"c\"");

        cut.FindAll(".json-toggle").Last().Click(); // expand the deepest collapsed node

        // now fully expanded — no previews, "c" is a real key row
        cut.FindAll(".json-preview").Should().BeEmpty();
        cut.FindAll(".json-key").Select(e => e.TextContent).Should().Contain("\"c\"");
    }

    [Test]
    public void Toggle_CollapsesExpandedNode()
    {
        var cut = Render<JsonTreeView>(p => p.Add(x => x.Json, """{"name":"Alice"}"""));

        // expanded: Alice appears in a .json-string span
        cut.Find(".json-string").Should().NotBeNull();
        cut.FindAll(".json-preview").Should().BeEmpty();

        cut.Find(".json-toggle").Click();

        // collapsed: no .json-string span; a .json-preview appears instead
        cut.FindAll(".json-string").Should().BeEmpty();
        cut.FindAll(".json-preview").Should().HaveCount(1);
    }

    [Test]
    public void ChangingJson_ResetsExpansionState()
    {
        var cut = Render<JsonTreeView>(p => p.Add(x => x.Json, """{"name":"Alice"}"""));

        // collapse the root — preview appears
        cut.Find(".json-toggle").Click();
        cut.FindAll(".json-preview").Should().HaveCount(1);

        // change JSON — tree resets, auto-expands; no preview on a shallow object
        cut.Render(p => p.Add(x => x.Json, """{"name":"Bob"}"""));
        cut.Markup.Should().Contain("Bob");
        cut.FindAll(".json-preview").Should().BeEmpty();
    }

    [Test]
    public void CollapsedNode_ShowsPreviewInMarkup()
    {
        // depth-2 object {"name":"Alice","age":30} is collapsed — preview must appear in markup
        var cut = Render<JsonTreeView>(p => p.Add(x => x.Json, """{"a":{"b":{"name":"Alice","age":30}}}"""));
        cut.Markup.Should().Contain("name");
        cut.Markup.Should().Contain("Alice");
    }

    // --- GetCollapsedPreview unit tests (pure C#, no rendering required) ---

    [Test]
    public void GetCollapsedPreview_ObjectWithMoreThanMaxItems_ShowsEllipsis()
    {
        using var doc = JsonDocument.Parse("""{"a":1,"b":"hi","c":true,"d":null}""");
        var preview = JsonTreeNode.GetCollapsedPreview(doc.RootElement);
        preview.Should().Contain("\"a\": 1");
        preview.Should().Contain("\"b\": \"hi\"");
        preview.Should().Contain("\"c\": true");
        preview.Should().Contain("…");
        preview.Should().NotContain("\"d\"");
    }

    [Test]
    public void GetCollapsedPreview_ObjectWithFewProps_NoEllipsis()
    {
        using var doc = JsonDocument.Parse("""{"x":1,"y":2}""");
        var preview = JsonTreeNode.GetCollapsedPreview(doc.RootElement);
        preview.Should().Contain("\"x\": 1");
        preview.Should().Contain("\"y\": 2");
        preview.Should().NotContain("…");
    }

    [Test]
    public void GetCollapsedPreview_ArrayWithMoreThanMaxItems_ShowsEllipsis()
    {
        using var doc = JsonDocument.Parse("[10,20,30,40,50]");
        var preview = JsonTreeNode.GetCollapsedPreview(doc.RootElement);
        preview.Should().Contain("10");
        preview.Should().Contain("20");
        preview.Should().Contain("30");
        preview.Should().Contain("…");
        preview.Should().NotContain("40");
    }

    [Test]
    public void GetCollapsedPreview_NestedObject_ShowsBracePlaceholder()
    {
        using var doc = JsonDocument.Parse("""{"inner":{"x":1}}""");
        var preview = JsonTreeNode.GetCollapsedPreview(doc.RootElement);
        preview.Should().Contain("{…}");
    }

    [Test]
    public void GetCollapsedPreview_NestedArray_ShowsBracketPlaceholder()
    {
        using var doc = JsonDocument.Parse("""{"items":[1,2,3]}""");
        var preview = JsonTreeNode.GetCollapsedPreview(doc.RootElement);
        preview.Should().Contain("[…]");
    }

    [Test]
    public void GetCollapsedPreview_LongString_Truncates()
    {
        using var doc = JsonDocument.Parse("""{"key":"this string is definitely longer than fifteen characters"}""");
        var preview = JsonTreeNode.GetCollapsedPreview(doc.RootElement);
        preview.Should().Contain("…");
        preview.Should().NotContain("this string is definitely longer than fifteen characters");
    }

    [Test]
    public void GetCollapsedPreview_NullValue_ShowsNull()
    {
        using var doc = JsonDocument.Parse("""{"flag":null}""");
        var preview = JsonTreeNode.GetCollapsedPreview(doc.RootElement);
        preview.Should().Contain("null");
    }

    // --- Chart button tests ---

    [Test]
    public void NumberValue_ShowsChartButton_WhenOnAddToChartIsSet()
    {
        var cut = Render<JsonTreeView>(p => p
            .Add(x => x.Json, """{"temp":23.4}""")
            .Add(x => x.OnAddToChart, EventCallback.Factory.Create<ChartFieldSelection>(this, _ => { })));
        cut.Find(".json-chart-btn").Should().NotBeNull();
    }

    [Test]
    public void NumberValue_NoChartButton_WhenOnAddToChartIsNotSet()
    {
        var cut = Render<JsonTreeView>(p => p.Add(x => x.Json, """{"temp":23.4}"""));
        cut.FindAll(".json-chart-btn").Should().BeEmpty();
    }

    [Test]
    public void BoolValue_ShowsChartButton_WhenOnAddToChartIsSet()
    {
        var cut = Render<JsonTreeView>(p => p
            .Add(x => x.Json, """{"active":true}""")
            .Add(x => x.OnAddToChart, EventCallback.Factory.Create<ChartFieldSelection>(this, _ => { })));
        cut.Find(".json-chart-btn").Should().NotBeNull();
    }

    [Test]
    public async Task ChartButton_InvokesCallbackWithTopLevelPath()
    {
        ChartFieldSelection? captured = null;
        var cut = Render<JsonTreeView>(p => p
            .Add(x => x.Json, """{"temp":23.4}""")
            .Add(x => x.OnAddToChart, EventCallback.Factory.Create<ChartFieldSelection>(this, p => captured = p)));

        await cut.Find(".json-chart-btn").ClickAsync(new());

        captured.Should().Be(new ChartFieldSelection("temp"));
    }

    [Test]
    public void FalseValue_ShowsChartButton_WhenOnAddToChartIsSet()
    {
        var cut = Render<JsonTreeView>(p => p
            .Add(x => x.Json, """{"active":false}""")
            .Add(x => x.OnAddToChart, EventCallback.Factory.Create<ChartFieldSelection>(this, _ => { })));
        cut.Find(".json-chart-btn").Should().NotBeNull();
    }

    [Test]
    public void ChartButton_PreservesTitleAndAccessibleLabel()
    {
        var cut = Render<JsonTreeView>(p => p
            .Add(x => x.Json, """{"temp":1}""")
            .Add(x => x.OnAddToChart, EventCallback.Factory.Create<ChartFieldSelection>(this, _ => { })));

        var button = cut.Find(".json-chart-btn");
        button.GetAttribute("title").Should().Be("Add to chart");
        button.QuerySelector(".json-chart-btn__label")!.TextContent.Should().Contain("ADD TO CHART");
    }

    [Test]
    public async Task ChartButton_InvokesCallbackWithNestedPath()
    {
        ChartFieldSelection? captured = null;
        var cut = Render<JsonTreeView>(p => p
            .Add(x => x.Json, """{"device":{"temp":23.4}}""")
            .Add(x => x.OnAddToChart, EventCallback.Factory.Create<ChartFieldSelection>(this, p => captured = p)));

        // "device" is at depth 1 → auto-expanded; "temp" is a number leaf → button visible
        await cut.Find(".json-chart-btn").ClickAsync(new());

        captured.Should().Be(new ChartFieldSelection("device.temp"));
    }

    [Test]
    public async Task SparkplugDatatype_DoesNotShowChartButton()
    {
        var cut = Render<JsonTreeView>(p => p
            .Add(x => x.Json, """{"metrics":[{"name":"Metric-1","datatype":10,"doubleValue":66.36}]}""")
            .Add(x => x.OnAddToChart, EventCallback.Factory.Create<ChartFieldSelection>(this, _ => { })));
        await cut.InvokeAsync(() => cut.Instance.ExpandAll());

        cut.FindAll(".json-chart-btn").Should().ContainSingle();
    }

    [Test]
    public async Task SparkplugValue_InvokesCallbackWithMetricNameAsSeriesName()
    {
        ChartFieldSelection? captured = null;
        var cut = Render<JsonTreeView>(p => p
            .Add(x => x.Json, """{"metrics":[{"name":"Metric-1","datatype":10,"doubleValue":66.36}]}""")
            .Add(x => x.OnAddToChart, EventCallback.Factory.Create<ChartFieldSelection>(this, p => captured = p)));
        await cut.InvokeAsync(() => cut.Instance.ExpandAll());

        await cut.Find(".json-chart-btn").ClickAsync(new());

        captured.Should().Be(new ChartFieldSelection("metrics.Metric-1", "Metric-1"));
    }

    [Test]
    public async Task SparkplugValueWithoutName_InvokesCallbackWithRawPathAndNoSeriesName()
    {
        ChartFieldSelection? captured = null;
        var cut = Render<JsonTreeView>(p => p
            .Add(x => x.Json, """{"metrics":[{"datatype":10,"doubleValue":66.36}]}""")
            .Add(x => x.OnAddToChart, EventCallback.Factory.Create<ChartFieldSelection>(this, p => captured = p)));
        await cut.InvokeAsync(() => cut.Instance.ExpandAll());

        await cut.Find(".json-chart-btn").ClickAsync(new());

        captured.Should().Be(new ChartFieldSelection("metrics[0].doubleValue"));
    }

    [Test]
    public void GenericAlias_ShowsChartButton_WhenOnAddToChartIsSet()
    {
        ChartFieldSelection? captured = null;
        var cut = Render<JsonTreeView>(p => p
            .Add(x => x.Json, """{"alias":42}""")
            .Add(x => x.OnAddToChart, EventCallback.Factory.Create<ChartFieldSelection>(this, sel => captured = sel)));
        cut.Find(".json-chart-btn").Should().NotBeNull();

        cut.Find(".json-chart-btn").Click();

        captured.Should().Be(new ChartFieldSelection("alias"));
    }

    [Test]
    public async Task NestedGenericAlias_ShowsChartButton_WhenOnAddToChartIsSet()
    {
        ChartFieldSelection? captured = null;
        var cut = Render<JsonTreeView>(p => p
            .Add(x => x.Json, """{"device":{"alias":42}}""")
            .Add(x => x.OnAddToChart, EventCallback.Factory.Create<ChartFieldSelection>(this, sel => captured = sel)));
        cut.Find(".json-chart-btn").Should().NotBeNull();

        await cut.Find(".json-chart-btn").ClickAsync(new());

        captured.Should().Be(new ChartFieldSelection("device.alias"));
    }

    [Test]
    public async Task GenericAliasWithValueSibling_ShowsChartButton_WhenOnAddToChartIsSet()
    {
        ChartFieldSelection? captured = null;
        var cut = Render<JsonTreeView>(p => p
            .Add(x => x.Json, """{"device":{"alias":42,"value":10}}""")
            .Add(x => x.OnAddToChart, EventCallback.Factory.Create<ChartFieldSelection>(this, sel => captured = sel)));
        await cut.InvokeAsync(() => cut.Instance.ExpandAll());

        var buttons = cut.FindAll(".json-chart-btn");
        buttons.Should().HaveCount(2);

        await buttons[0].ClickAsync(new());

        captured.Should().Be(new ChartFieldSelection("device.alias"));
    }

    [Test]
    public async Task SparkplugAlias_DoesNotShowChartButton()
    {
        var aliasNames = new Dictionary<ulong, string> { [42] = "Flow Rate" };
        var cut = Render<JsonTreeView>(p => p
            .Add(x => x.Json, """{"metrics":[{"alias":42,"doubleValue":3.14}]}""")
            .Add(x => x.AliasNames, aliasNames)
            .Add(x => x.OnAddToChart, EventCallback.Factory.Create<ChartFieldSelection>(this, _ => { })));
        await cut.InvokeAsync(() => cut.Instance.ExpandAll());

        cut.FindAll(".json-chart-btn").Should().ContainSingle();
    }

    [Test]
    public async Task SparkplugAliasValue_InvokesCallbackWithResolvedMetricNameAsSeriesName()
    {
        ChartFieldSelection? captured = null;
        var aliasNames = new Dictionary<ulong, string> { [42] = "Flow Rate" };
        var cut = Render<JsonTreeView>(p => p
            .Add(x => x.Json, """{"metrics":[{"alias":42,"doubleValue":3.14}]}""")
            .Add(x => x.AliasNames, aliasNames)
            .Add(x => x.OnAddToChart, EventCallback.Factory.Create<ChartFieldSelection>(this, p => captured = p)));
        await cut.InvokeAsync(() => cut.Instance.ExpandAll());

        await cut.Find(".json-chart-btn").ClickAsync(new());

        captured.Should().Be(new ChartFieldSelection("metrics.Flow Rate", "Flow Rate"));
    }

    [Test]
    public void HasExpandableContent_True_WhenJsonHasNonEmptyContainer()
    {
        var cut = Render<JsonTreeView>(p => p.Add(x => x.Json, """{"a":1}"""));
        cut.Instance.HasExpandableContent.Should().BeTrue();
    }

    [Test]
    public void HasExpandableContent_False_WhenJsonIsNull()
    {
        var cut = Render<JsonTreeView>(p => p.Add(x => x.Json, (string?)null));
        cut.Instance.HasExpandableContent.Should().BeFalse();
    }

    [Test]
    public void HasExpandableContent_False_WhenJsonIsInvalid()
    {
        var cut = Render<JsonTreeView>(p => p.Add(x => x.Json, "{not valid json"));
        cut.Instance.HasExpandableContent.Should().BeFalse();
    }

    [TestCase("\"hello\"")]
    [TestCase("42")]
    [TestCase("true")]
    [TestCase("false")]
    [TestCase("null")]
    public void HasExpandableContent_False_WhenRootIsPrimitive(string json)
    {
        var cut = Render<JsonTreeView>(p => p.Add(x => x.Json, json));
        cut.Instance.HasExpandableContent.Should().BeFalse();
    }

    [TestCase("{}")]
    [TestCase("[]")]
    public void HasExpandableContent_False_WhenRootIsEmptyContainer(string json)
    {
        var cut = Render<JsonTreeView>(p => p.Add(x => x.Json, json));
        cut.Instance.HasExpandableContent.Should().BeFalse();
    }

    [Test]
    public void IsExpandableJson_True_WhenJsonHasNonEmptyContainer()
    {
        JsonTreeView.IsExpandableJson("""{"a":1}""").Should().BeTrue();
        JsonTreeView.IsExpandableJson("[1,2,3]").Should().BeTrue();
    }

    [TestCase((string?)null)]
    [TestCase("")]
    [TestCase("   ")]
    [TestCase("{not valid json")]
    public void IsExpandableJson_False_WhenJsonIsNullEmptyOrInvalid(string? json)
    {
        JsonTreeView.IsExpandableJson(json).Should().BeFalse();
    }

    [TestCase("\"hello\"")]
    [TestCase("42")]
    [TestCase("true")]
    [TestCase("false")]
    [TestCase("null")]
    [TestCase("{}")]
    [TestCase("[]")]
    public void IsExpandableJson_False_WhenRootIsPrimitiveOrEmptyContainer(string json)
    {
        JsonTreeView.IsExpandableJson(json).Should().BeFalse();
    }

    [Test]
    public void ExpandAll_ExpandsAllNodes()
    {
        // Default AutoExpandDepth=2 collapses {"c":"deep"} into a preview;
        // after Expand-all, every node — including depth-3 — must be expanded.
        var cut = Render<JsonTreeView>(p => p.Add(x => x.Json, """{"a":{"b":{"c":"deep"}}}"""));
        cut.FindAll(".json-preview").Should().HaveCount(1);

        cut.InvokeAsync(() => cut.Instance.ExpandAll());

        cut.FindAll(".json-preview").Should().BeEmpty();
        cut.FindAll(".json-key").Select(e => e.TextContent).Should().Contain("\"c\"");
    }

    [Test]
    public void CollapseAll_CollapsesAllNodesIncludingRoot()
    {
        // Collapsing the root leaves zero .json-key rows; the root itself
        // becomes a single .json-preview (the literal "everything" reading).
        var cut = Render<JsonTreeView>(p => p.Add(x => x.Json, """{"a":{"b":{"c":"deep"}}}"""));

        cut.InvokeAsync(() => cut.Instance.CollapseAll());

        cut.FindAll(".json-key").Should().BeEmpty();
        cut.FindAll(".json-preview").Should().HaveCount(1);
    }

    [Test]
    public void ChangingJson_ResetsToDefaultDepth_AfterExpandAll()
    {
        // After Expand-all, all nodes are open. A new payload must reset
        // to AutoExpandDepth=2 so depth-2 collapses back into a preview.
        var cut = Render<JsonTreeView>(p => p.Add(x => x.Json, """{"a":{"b":{"c":"deep"}}}"""));
        cut.InvokeAsync(() => cut.Instance.ExpandAll());
        cut.FindAll(".json-preview").Should().BeEmpty();

        cut.Render(p => p.Add(x => x.Json, """{"x":{"y":{"z":"deep"}}}"""));

        cut.FindAll(".json-preview").Should().HaveCount(1);
        cut.FindAll(".json-key").Select(e => e.TextContent).Should().NotContain("\"z\"");
    }

    // --- Alias annotation tests ---

    [Test]
    public void AliasAnnotation_RendersForAliasOnlyMetric()
    {
        var aliasNames = new Dictionary<ulong, string> { [42] = "Flow Rate" };
        var cut = Render<JsonTreeView>(p => p
            .Add(x => x.Json, """{"metrics":[{"alias":42,"doubleValue":3.14}]}""")
            .Add(x => x.AliasNames, aliasNames));
        cut.InvokeAsync(() => cut.Instance.ExpandAll());

        cut.Markup.Should().Contain("→ Flow Rate (resolved)");
    }

    [Test]
    public void AliasAnnotation_NotRenderedWhenNamePresent()
    {
        var aliasNames = new Dictionary<ulong, string> { [5] = "Pressure" };
        var cut = Render<JsonTreeView>(p => p
            .Add(x => x.Json, """{"metrics":[{"name":"Pressure","alias":5,"doubleValue":1013.0}]}""")
            .Add(x => x.AliasNames, aliasNames));
        cut.InvokeAsync(() => cut.Instance.ExpandAll());

        cut.Markup.Should().NotContain("(resolved)");
    }

    [Test]
    public void AliasAnnotation_NotRenderedWhenAliasNamesNull()
    {
        var cut = Render<JsonTreeView>(p => p
            .Add(x => x.Json, """{"metrics":[{"alias":42,"doubleValue":3.14}]}""")
            .Add(x => x.AliasNames, (IReadOnlyDictionary<ulong, string>?)null));
        cut.InvokeAsync(() => cut.Instance.ExpandAll());

        cut.Markup.Should().NotContain("(resolved)");
    }

    [Test]
    public void AliasAnnotation_NotRenderedWhenAliasNotInMap()
    {
        var aliasNames = new Dictionary<ulong, string> { [7] = "Temperature" };
        var cut = Render<JsonTreeView>(p => p
            .Add(x => x.Json, """{"metrics":[{"alias":42,"doubleValue":3.14}]}""")
            .Add(x => x.AliasNames, aliasNames));
        cut.InvokeAsync(() => cut.Instance.ExpandAll());

        cut.Markup.Should().NotContain("(resolved)");
    }

    [Test]
    public void AliasAnnotation_NotRenderedWhenAliasIsZero()
    {
        var aliasNames = new Dictionary<ulong, string> { [0] = "Something" };
        var cut = Render<JsonTreeView>(p => p
            .Add(x => x.Json, """{"metrics":[{"alias":0,"doubleValue":1.0}]}""")
            .Add(x => x.AliasNames, aliasNames));
        cut.InvokeAsync(() => cut.Instance.ExpandAll());

        cut.Markup.Should().NotContain("(resolved)");
    }
}
