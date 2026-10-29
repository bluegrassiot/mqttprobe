using Microsoft.Extensions.DependencyInjection;
using MqttProbe.Core.Services.Mqtt;
using MqttProbe.UI.Tests.TestHelpers;

namespace MqttProbe.UI.Tests.Components.Browser;

// A keyboard move commits the row it asks the page to focus, and the row that reports real focus
// becomes the one the tree remembers. When the user leaves before the committed row is rendered,
// the page reports the rendered row it kept tabbable, so the tree keeps exactly one tab stop.
[TestFixture]
public class TopicBrowserTabStopContinuityTests : BunitTestContext
{
    private const string ModulePath = "./_content/MqttProbe.UI/js/topic-tree-keyboard.js";

    private IMessageStoreManager _mockMsgStore = null!;
    private TopicBrowserTestFixture _fixture = null!;

    [SetUp]
    public void SetupMocks()
    {
        _mockMsgStore = Substitute.For<IMessageStoreManager>();
        _fixture = new TopicBrowserTestFixture(_mockMsgStore);
        Services.AddSingleton<ITopicSelectionStore>(_fixture.Adapter);
        Services.AddUiSettings(SettingsSubstituteRegistration.CreateUiSettings());
    }

    [Test]
    public async Task CancelledMove_LeavesOneTabStopOnTheRowThePageReports()
    {
        var cut = RenderExpandedTree();
        cut.Find(".topic-tree-row[data-topic-key='sensors']").Focus();

        await PressKeyAsync(cut, "End");
        TabStopKey(cut).Should().Be("sensors/temp/room1");

        await cut.InvokeAsync(() => cut.Instance.OnTabStopFallbackAsync("sensors"));

        TabStopKey(cut).Should().Be("sensors");

        await PressKeyAsync(cut, "ArrowDown");
        TabStopKey(cut).Should().Be("sensors/humidity");

        await PressKeyAsync(cut, "Enter");
        cut.WaitForAssertion(() => cut.FindAll(".topic-tree-row--selected").Should().ContainSingle()
            .Which.GetAttribute("data-topic-key").Should().Be("sensors/humidity"));
    }

    [Test]
    public async Task CancelledMove_DoesNotClaimTheRowItNeverReached()
    {
        var module = JSInterop.SetupModule(ModulePath);
        module.SetupVoid("retainFocus");
        var cut = RenderExpandedTree();
        cut.Find(".topic-tree-row[data-topic-key='sensors']").Focus();

        await PressKeyAsync(cut, "End");

        LastRetainKey(module).Should().Be("sensors",
            "a live update may only follow the row that really holds focus");
    }

    [Test]
    public async Task CompletedMove_CommitsTheRowThatReportsFocus()
    {
        var module = JSInterop.SetupModule(ModulePath);
        module.SetupVoid("retainFocus");
        var cut = RenderExpandedTree();
        cut.Find(".topic-tree-row[data-topic-key='sensors']").Focus();

        await PressKeyAsync(cut, "End");
        LastRetainKey(module).Should().Be("sensors");

        cut.Find(".topic-tree-row[data-topic-key='sensors/temp/room1']").Focus();

        TabStopKey(cut).Should().Be("sensors/temp/room1");
        LastRetainKey(module).Should().Be("sensors/temp/room1");
    }

    [Test]
    public async Task FallbackReport_DoesNotTakeFocusBack()
    {
        var module = JSInterop.SetupModule(ModulePath);
        module.SetupVoid("moveFocus");
        module.SetupVoid("restoreFocus");
        var cut = RenderExpandedTree();
        cut.Find(".topic-tree-row[data-topic-key='sensors']").Focus();
        await PressKeyAsync(cut, "End");
        var before = FocusRequestCount(module);

        await cut.InvokeAsync(() => cut.Instance.OnTabStopFallbackAsync("sensors"));

        FocusRequestCount(module).Should().Be(before);
        TabStopKey(cut).Should().Be("sensors");
    }

    [Test]
    public async Task FallbackReport_LeavesTheTabStopAlone_WhenTheRowIsNotInTheTree()
    {
        var cut = RenderExpandedTree();
        await PressKeyAsync(cut, "End");

        await cut.InvokeAsync(() => cut.Instance.OnTabStopFallbackAsync("no/such/topic"));

        TabStopKey(cut).Should().Be("sensors/temp/room1");
    }

    private IRenderedComponent<TopicBrowser> RenderExpandedTree()
    {
        _fixture.AddTopics(TopicBrowserTestTrees.BuildTopics());
        var cut = Render<TopicBrowser>();
        EnsureMudProviders();
        cut.FindAll(".mud-toolbar button")[0].Click();
        cut.WaitForAssertion(() => cut.FindAll(".topic-tree-row").Should().HaveCount(4));
        return cut;
    }

    private static Task PressKeyAsync(IRenderedComponent<TopicBrowser> cut, string key) =>
        cut.InvokeAsync(() => cut.Find(".topic-tree-row[tabindex='0']").KeyDown(key));

    private static string? LastRetainKey(BunitJSModuleInterop module) =>
        module.Invocations.Last(invocation => invocation.Identifier == "retainFocus").Arguments[1] as string;

    private static int FocusRequestCount(BunitJSModuleInterop module) =>
        module.Invocations.Count(invocation => invocation.Identifier is "moveFocus" or "restoreFocus");

    private static string? TabStopKey(IRenderedComponent<TopicBrowser> cut)
    {
        var tabStops = cut.FindAll(".topic-tree-row[tabindex='0']");
        tabStops.Should().ContainSingle("a tree keeps exactly one roving tab stop");
        return tabStops[0].GetAttribute("data-topic-key");
    }
}
