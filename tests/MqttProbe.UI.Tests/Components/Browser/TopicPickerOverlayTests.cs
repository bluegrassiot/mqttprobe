using Microsoft.Extensions.DependencyInjection;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Services.Mqtt;
using MqttProbe.UI.Tests.TestHelpers;

namespace MqttProbe.UI.Tests.Components.Browser;

[TestFixture]
public class TopicPickerOverlayTests : BunitTestContext
{
    private IMessageStoreManager _mockMsgStore = null!;
    private TopicBrowserTestFixture _fixture = null!;

    [SetUp]
    public void SetupMocks()
    {
        _mockMsgStore = Substitute.For<IMessageStoreManager>();
        _mockMsgStore.RootTopicCount.Returns(0);
        Services.AddSingleton(_mockMsgStore);
        _fixture = new TopicBrowserTestFixture();

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

    [Test]
    public void Panel_IsAModalDialogLabelledByItsTitle()
    {
        var cut = Render<TopicPickerOverlay>(p => p.Add(x => x.IsOpen, true));

        var panel = cut.Find(".topic-picker-overlay__panel");
        panel.GetAttribute("role").Should().Be("dialog");
        panel.GetAttribute("aria-modal").Should().Be("true");
        var labelledBy = panel.GetAttribute("aria-labelledby");
        labelledBy.Should().NotBeNullOrWhiteSpace();
        cut.Find($"#{labelledBy}").TextContent.Trim().Should().Be("Topics");
    }

    [Test]
    public void Panel_IsInsideAFocusTrap()
    {
        var cut = Render<TopicPickerOverlay>(p => p.Add(x => x.IsOpen, true));

        var trap = cut.FindAll(".mud-focus-trap").Should().ContainSingle().Subject;
        trap.QuerySelector(".topic-picker-overlay__panel").Should().NotBeNull();
    }

    [Test]
    public async Task Escape_InvokesOnClose()
    {
        var closed = false;
        var cut = Render<TopicPickerOverlay>(
            p => p.Add(x => x.IsOpen, true)
                  .Add(x => x.OnClose, () => { closed = true; return Task.CompletedTask; }));

        await cut.InvokeAsync(() => cut.Find(".topic-picker-overlay").KeyDown("Escape"));

        closed.Should().BeTrue();
    }

    [Test]
    public void Opening_SavesFocusBeforeTheTrapMovesIt()
    {
        Render<TopicPickerOverlay>(p => p.Add(x => x.IsOpen, true));

        var identifiers = JSInterop.Invocations.Select(invocation => invocation.Identifier).ToList();
        var saveIndex = identifiers.IndexOf("mudElementRef.saveFocus");
        saveIndex.Should().BeGreaterThanOrEqualTo(0, "the trap must save the opener");
        identifiers.Take(saveIndex).Should().NotContain(
            identifier => identifier.Contains("focus", StringComparison.OrdinalIgnoreCase),
            "no component may move focus before the trap records the opener");
        identifiers.IndexOf("mudElementRef.focusFirst").Should().BeGreaterThan(saveIndex,
            "the trap must place its initial focus after saving the opener");
    }

    [Test]
    public void Closing_RestoresFocusToWhateverOpenedIt()
    {
        var cut = Render<TopicPickerOverlay>(p => p.Add(x => x.IsOpen, true));
        JSInterop.Invocations.Should().Contain(invocation => invocation.Identifier == "mudElementRef.saveFocus");

        cut.Render(p => p.Add(x => x.IsOpen, false));

        JSInterop.Invocations.Should().Contain(invocation => invocation.Identifier == "mudElementRef.restoreFocus");
    }

    [Test]
    public void Footer_ShowsNoSelectionAndDisablesShowMessages_WithACloseExit()
    {
        var cut = RenderPicker();

        cut.Find(".topic-picker-overlay__selection").TextContent.Trim().Should().Be("No topics selected");
        var done = cut.Find(".topic-picker-overlay__done");
        done.TextContent.Trim().Should().Be("Show messages");
        done.HasAttribute("disabled").Should().BeTrue();
        cut.Find("button[title='Close']").Should().NotBeNull(
            "the primary action is disabled at zero, so the existing close control stays the exit");
    }

    [Test]
    public void SelectionChanges_UpdateTheFooterAndShowMessagesAvailability()
    {
        var selection = new TopicBulkSelection(_fixture.Adapter);
        var cut = RenderPicker(selection);

        selection.SelectAll([Raw("sensors/alpha"), Raw("sensors/beta")]);

        cut.WaitForAssertion(() =>
        {
            cut.Find(".topic-picker-overlay__selection").TextContent.Trim().Should().Be("2 topics selected");
            cut.Find(".topic-picker-overlay__done").HasAttribute("disabled").Should().BeFalse();
        });

        selection.CoversWholeTree = true;

        cut.WaitForAssertion(() => cut.Find(".topic-picker-overlay__selection").TextContent.Trim()
            .Should().Be("All topics selected"));

        selection.Clear();

        cut.WaitForAssertion(() =>
        {
            cut.Find(".topic-picker-overlay__selection").TextContent.Trim().Should().Be("No topics selected");
            cut.Find(".topic-picker-overlay__done").HasAttribute("disabled").Should().BeTrue();
        });
        cut.Find("button[title='Close']").Should().NotBeNull(
            "deselecting every topic must never trap the user in the picker");
    }

    [Test]
    public async Task ShowMessages_ClosesThePickerAndKeepsTheLiveSelection()
    {
        var selection = new TopicBulkSelection(_fixture.Adapter);
        selection.SelectAll([Raw("sensors/alpha")]);
        var closed = false;
        var cut = RenderPicker(selection, () => { closed = true; return Task.CompletedTask; });

        await cut.InvokeAsync(() => cut.Find(".topic-picker-overlay__done").Click());

        closed.Should().BeTrue();
        selection.Branches.Select(branch => branch.Path).Should().Equal("sensors/alpha");
        _fixture.SelectedKey.Should().NotBeNull("closing the picker keeps the selection the store already holds");
    }

    [Test]
    public async Task MetricSelection_EnablesShowMessagesAndClosesWithoutChangingThePredicate()
    {
        var scope = new SparkplugSelectionScope(SparkplugSelectionKind.Node, "spBv1.0/plant1", "node1");
        var state = new SelectedTopicState(
            new SelectedTopicToken(Guid.NewGuid(), 1, 2), "metric:key", 2, "spBv1.0/plant1/node1 Motor/Temp")
        {
            Mode = TopicSelectionMode.SparkplugMetric,
            Metric = new MqttSparkplugMetricTarget(scope, "Motor/Temp")
        };
        _mockMsgStore.GetSelectedTopicState().Returns(state);
        var closed = false;
        var cut = RenderPicker(null, () => { closed = true; return Task.CompletedTask; });

        cut.Find(".topic-picker-overlay__selection").TextContent.Trim()
            .Should().Be("spBv1.0/plant1/node1 Motor/Temp");
        var done = cut.Find(".topic-picker-overlay__done");
        done.HasAttribute("disabled").Should().BeFalse("Core's metric predicate is a valid browsing target");

        await cut.InvokeAsync(() => done.Click());

        closed.Should().BeTrue();
        _mockMsgStore.GetSelectedTopicState().Mode.Should().Be(TopicSelectionMode.SparkplugMetric);
    }

    [Test]
    public async Task MultiMetricSelection_ShowsTheCoreLabelAndCloseLeavesItUntouched()
    {
        var scope = new SparkplugSelectionScope(SparkplugSelectionKind.Node, "spBv1.0/plant1", "node1");
        var metrics = new[]
        {
            new MqttSparkplugMetricTarget(scope, "Motor/Temp"),
            new MqttSparkplugMetricTarget(scope, "Temp"),
            new MqttSparkplugMetricTarget(scope, "Speed")
        };
        var state = new SelectedTopicState(
            new SelectedTopicToken(Guid.NewGuid(), 1, 2), "metrics:key", 2, "3 metrics")
        {
            Mode = TopicSelectionMode.SparkplugMetric,
            Metrics = [.. metrics]
        };
        _mockMsgStore.GetSelectedTopicState().Returns(state);
        var closed = false;
        var cut = RenderPicker(null, () => { closed = true; return Task.CompletedTask; });

        cut.Find(".topic-picker-overlay__selection").TextContent.Trim().Should().Be("3 metrics");
        cut.Find(".topic-picker-overlay__done").HasAttribute("disabled").Should()
            .BeFalse("the Core metric set is a valid browsing target");

        await cut.InvokeAsync(() => cut.Find(".topic-picker-overlay__done").Click());

        closed.Should().BeTrue();
        _mockMsgStore.DidNotReceive().SelectTopics(Arg.Any<TopicSelection?>());
        var after = _mockMsgStore.GetSelectedTopicState();
        after.Token.Should().Be(state.Token, "opening and closing keeps the same selection generation");
        after.Metrics.Should().BeEquivalentTo(metrics);
    }

    [Test]
    public void Footer_IsSiblingOfTheScrollingBody()
    {
        var cut = RenderPicker();
        var panel = cut.Find(".topic-picker-overlay__panel");

        var footer = panel.QuerySelector(".topic-picker-overlay__footer");
        footer.Should().NotBeNull();
        panel.QuerySelector(".topic-picker-overlay__body .topic-picker-overlay__footer").Should().BeNull(
            "the action stays outside the tree's scroll owner");
        footer!.QuerySelector(".topic-picker-overlay__done").Should().NotBeNull();
        footer.QuerySelector(".topic-picker-overlay__selection").Should().NotBeNull();
    }

    private IRenderedComponent<TopicPickerOverlay> RenderPicker(
        TopicBulkSelection? selection = null, Func<Task>? onClose = null) =>
        Render<TopicPickerOverlay>(parameters =>
        {
            parameters.Add(x => x.IsOpen, true);
            if (selection is not null)
                parameters.AddCascadingValue(selection);
            if (onClose is not null)
                parameters.Add(x => x.OnClose, onClose);
        });

    private static TopicBranchTarget Raw(string path) => new(TopicBranchKind.Raw, Path: path);
}
