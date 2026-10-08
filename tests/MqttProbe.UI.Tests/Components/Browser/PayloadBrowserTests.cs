using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MqttProbe.Core.Models.Chart;
using MqttProbe.Core.Models.Configuration;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Services.Configuration;
using MqttProbe.Core.Services.Metrics;
using MqttProbe.Core.Services.Mqtt;
using MqttProbe.Core.Services.Platform;
using MqttProbe.Core.Services.Plugins.BuiltIn;
using MqttProbe.Core.Services.Plugins.Pipeline;
using MqttProbe.Core.Services.Plugins.Registry;
using MqttProbe.UI.Tests.TestHelpers;
using MudBlazor;

namespace MqttProbe.UI.Tests.Components.Browser;

[TestFixture]
public class PayloadBrowserTests : BunitTestContext
{
    private IMessageStoreManager _mockMsgStore = null!;
    private IPerformanceSettings _mockPerformance = null!;
    private IUxMetricsService _mockMetrics = null!;
    private Guid _storeId;
    private SelectedTopicState _selection = null!;
    private TaskCompletionSource<(SelectedTopicToken Token, int Limit)>? _nextQueryRequest;

    [SetUp]
    public void SetupMocks()
    {
        _mockMsgStore = Substitute.For<IMessageStoreManager>();
        _storeId = Guid.NewGuid();
        _selection = MakeSelection("sensor", generation: 1, contentVersion: 1);
        _mockMsgStore.GetSelectedTopicState().Returns(_ => _selection);
        ReturnMessages([]);
        Services.AddSingleton(_mockMsgStore);

        _mockPerformance = Substitute.For<IPerformanceSettings>();
        var cfg = new AppConfiguration();
        _mockPerformance.Performance.Returns(cfg.Performance);
        Services.AddPerformanceSettings(_mockPerformance);

        _mockMetrics = Substitute.For<IUxMetricsService>();
        Services.AddSingleton(_mockMetrics);

        Services.AddSingleton(Substitute.For<IDialogService>());
        Services.AddSingleton(Substitute.For<ISnackbar>());

        // Register IFormatDisplayNames using a real pipeline with built-in detectors
        var builder = new PluginRegistryBuilder();
        BuiltInPluginRegistration.RegisterBuiltIns(builder);
        var registry = builder.Build();
        var pipeline = new PayloadPipeline(registry, Substitute.For<Microsoft.Extensions.Logging.ILogger<PayloadPipeline>>());
        Services.AddSingleton(pipeline);
        Services.AddSingleton<IFormatDisplayNames>(new FormatDisplayNames(pipeline));
    }

    private SelectedTopicState MakeSelection(string? fullTopic, long generation, long contentVersion, int messageCount = 0) =>
        new(new SelectedTopicToken(_storeId, generation, contentVersion), fullTopic, messageCount);

    private void ReturnMessages(IReadOnlyList<MqttMessage> messages)
    {
        _mockMsgStore.GetSelectedMessagesAsync(Arg.Any<SelectedTopicToken>(), Arg.Any<int>())
            .Returns(call =>
            {
                var token = call.ArgAt<SelectedTopicToken>(0);
                Interlocked.Exchange(ref _nextQueryRequest, null)?.TrySetResult((token, call.ArgAt<int>(1)));
                return Task.FromResult<SelectedMessagesSnapshot?>(
                    new SelectedMessagesSnapshot(_selection with { Token = token }, call.ArgAt<int>(1),
                        System.Collections.Immutable.ImmutableArray.CreateRange(messages)));
            });
    }

    private static SelectedMessagesSnapshot Snapshot(SelectedTopicState state, int limit, string payload) =>
        new(state, limit,
            System.Collections.Immutable.ImmutableArray.Create(new MqttMessage { Topic = state.FullTopic, Payload = payload }));

    private void ReturnResponses(params Task<SelectedMessagesSnapshot?>[] responses)
    {
        var responseIndex = -1;
        _mockMsgStore.GetSelectedMessagesAsync(Arg.Any<SelectedTopicToken>(), Arg.Any<int>())
            .Returns(call =>
            {
                var request = Interlocked.Exchange(ref _nextQueryRequest, null);
                request?.TrySetResult((call.ArgAt<SelectedTopicToken>(0), call.ArgAt<int>(1)));
                return responses[Math.Min(Interlocked.Increment(ref responseIndex), responses.Length - 1)];
            });
    }

    private void ObserveNextQuery(TaskCompletionSource<(SelectedTopicToken Token, int Limit)> request) =>
        _nextQueryRequest = request;


    [Test]
    public void MessageList_AtOrAboveCap_ShowsTruncationWarning()
    {
        var messages = Enumerable.Range(0, 600)
            .Select(i => new MqttMessage { Topic = "sensor/temp", Payload = i.ToString() })
            .ToList();
        ReturnMessages(messages);
        EnsureMudProviders();

        var cut = Render<PayloadBrowser>();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("500 msgs"));

        // Verify the selected snapshot query uses the default display cap (500).
        _mockMsgStore.Received(1).GetSelectedMessagesAsync(Arg.Any<SelectedTopicToken>(), 500);
    }

    [Test]
    public void MessageList_BelowCap_ShowsNoTruncationWarning()
    {
        var messages = Enumerable.Range(0, 10)
            .Select(i => new MqttMessage { Topic = "sensor/temp", Payload = i.ToString() })
            .ToList();
        ReturnMessages(messages);
        EnsureMudProviders();

        var cut = Render<PayloadBrowser>();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("sensor/temp"));
        cut.Markup.Should().NotContain("msgs");
    }

    [Test]
    public void MessageList_WithConfiguredMaxDisplayMessages_ShowsCustomTruncationWarning()
    {
        var customCfg = new AppConfiguration
        {
            Performance = new PerformanceSettings { MaxDisplayMessages = 3 }
        };
        _mockPerformance.Performance.Returns(customCfg.Performance);
        var messages = Enumerable.Range(0, 3)
            .Select(i => new MqttMessage { Topic = "sensor/temp", Payload = i.ToString() })
            .ToList();
        ReturnMessages(messages);
        EnsureMudProviders();

        var cut = Render<PayloadBrowser>();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("3 msgs"));
        cut.FindAll(".payload-row").Should().HaveCount(3);

        // Verify the selected snapshot query uses the custom display cap (3).
        _mockMsgStore.Received(1).GetSelectedMessagesAsync(Arg.Any<SelectedTopicToken>(), 3);
    }

    [Test]
    public void DoesNotQuery_WhenVersionUnchanged()
    {
        ReturnMessages(Enumerable.Range(1, 5)
            .Select(i => new MqttMessage { Topic = "sensor/temp", Payload = i.ToString() })
            .ToList());
        EnsureMudProviders();

        var cut = Render<PayloadBrowser>();

        // First tick queries the current selection/content token.
        cut.WaitForAssertion(() =>
            _mockMsgStore.Received(1).GetSelectedMessagesAsync(Arg.Any<SelectedTopicToken>(), Arg.Any<int>()));

        // Wait past a second timer tick (500ms interval) to prove no second query fires.
        Thread.Sleep(600);

        // Still exactly 1 call while the selection/content token is unchanged.
        _mockMsgStore.Received(1).GetSelectedMessagesAsync(Arg.Any<SelectedTopicToken>(), Arg.Any<int>());
    }

    [Test]
    public async Task QueriesAgain_WhenVersionChanges()
    {
        ReturnMessages(Enumerable.Range(1, 5)
            .Select(i => new MqttMessage { Topic = "sensor/temp", Payload = i.ToString() })
            .ToList());
        EnsureMudProviders();

        var initialQuery = new TaskCompletionSource<(SelectedTopicToken Token, int Limit)>(TaskCreationOptions.RunContinuationsAsynchronously);
        ObserveNextQuery(initialQuery);
        var cut = Render<PayloadBrowser>();
        (await initialQuery.Task.WaitAsync(TimeSpan.FromSeconds(5)))
            .Should().Be((_selection.Token, 500));
        cut.WaitForAssertion(() => _mockMetrics.Received().SetDisplayedMessageCount(5), TimeSpan.FromSeconds(5));

        var updatedSelection = MakeSelection("sensor", generation: 1, contentVersion: 2);
        var updatedQuery = new TaskCompletionSource<(SelectedTopicToken Token, int Limit)>(TaskCreationOptions.RunContinuationsAsynchronously);
        ObserveNextQuery(updatedQuery);
        _selection = updatedSelection;
        (await updatedQuery.Task.WaitAsync(TimeSpan.FromSeconds(5)))
            .Should().Be((updatedSelection.Token, 500));

        // Verify limit matches default MaxDisplayMessages (500).
        _ = _mockMsgStore.Received(2).GetSelectedMessagesAsync(Arg.Any<SelectedTopicToken>(), 500);
    }

    [Test]
    public void NoSelectedTopic_DoesNotQueryAndShowsEmpty()
    {
        _selection = MakeSelection(null, generation: 2, contentVersion: 0);
        EnsureMudProviders();

        var cut = Render<PayloadBrowser>();

        cut.WaitForAssertion(() =>
            _mockMsgStore.DidNotReceive().GetSelectedMessagesAsync(Arg.Any<SelectedTopicToken>(), Arg.Any<int>()));
        cut.FindAll(".payload-row").Should().BeEmpty();
    }

    [Test]
    public void FilterFunc_WithTopicSearchTerm_MatchesByTopicCaseInsensitive()
    {
        var cut = Render<PayloadBrowser>();
        cut.Instance.SearchStringTerm = "SENSOR";

        cut.Instance.FilterFunc(new MqttMessage { Topic = "sensor/temp", Payload = "42" })
            .Should().BeTrue();
        cut.Instance.FilterFunc(new MqttMessage { Topic = "pressure/raw", Payload = "100" })
            .Should().BeFalse();
    }

    [Test]
    public void FilterFunc_WithPayloadSearchTerm_MatchesByPayloadCaseInsensitive()
    {
        var cut = Render<PayloadBrowser>();
        cut.Instance.SearchStringTerm = "hello";

        cut.Instance.FilterFunc(new MqttMessage { Topic = "any/topic", Payload = "Hello World" })
            .Should().BeTrue();
        cut.Instance.FilterFunc(new MqttMessage { Topic = "any/topic", Payload = "goodbye" })
            .Should().BeFalse();
    }

    [Test]
    public void FilterFunc_WithNullOrWhitespaceSearch_AlwaysReturnsTrue()
    {
        var cut = Render<PayloadBrowser>();
        var msg = new MqttMessage { Topic = "sensor/temp", Payload = "42" };

        cut.Instance.SearchStringTerm = null;
        cut.Instance.FilterFunc(msg).Should().BeTrue();

        cut.Instance.SearchStringTerm = "   ";
        cut.Instance.FilterFunc(msg).Should().BeTrue();

        cut.Instance.SearchStringTerm = string.Empty;
        cut.Instance.FilterFunc(msg).Should().BeTrue();
    }


    [Test]
    public void IsJson_WithValidJsonObjectAndArray_ReturnsTrue()
    {
        Render<PayloadBrowser>();

        PayloadBrowser.IsJson("""{"key": "value", "num": 42}""").Should().BeTrue();
        PayloadBrowser.IsJson("[1, 2, 3]").Should().BeTrue();
        PayloadBrowser.IsJson("\"a string\"").Should().BeFalse();
    }

    [Test]
    public void IsJson_WithPlainTextAndMalformed_ReturnsFalse()
    {
        Render<PayloadBrowser>();

        PayloadBrowser.IsJson("not json at all").Should().BeFalse();
        PayloadBrowser.IsJson("{unclosed").Should().BeFalse();
        PayloadBrowser.IsJson("").Should().BeFalse();
    }


    [Test]
    public async Task MessageChanged_WithMessage_ShowsDetailView()
    {
        var cut = Render<PayloadBrowser>();
        var msg = new MqttMessage { Topic = "test/topic", Payload = "plain text payload" };

        await cut.InvokeAsync(() => cut.Instance.MessageChanged(msg));

        cut.Markup.Should().Contain("Message Details");
        cut.Markup.Should().Contain("test/topic");
    }

    [Test]
    public async Task JsonMessage_ShowsExpandAndCollapseButtonsInSectionHeader()
    {
        var cut = Render<PayloadBrowser>();
        var msg = new MqttMessage { Topic = "test/topic", Payload = """{"a":{"b":{"c":"deep"}}}""" };

        await cut.InvokeAsync(() => cut.Instance.MessageChanged(msg));

        cut.Find(".json-expand-all-btn").Should().NotBeNull();
        cut.Find(".json-collapse-all-btn").Should().NotBeNull();
    }

    [Test]
    public async Task DetailUtilityButtons_UsePrimaryActionColor()
    {
        var cut = Render<PayloadBrowser>();
        var msg = new MqttMessage { Topic = "test/topic", Payload = """{"a":{"b":{"c":"deep"}}}""" };

        await cut.InvokeAsync(() => cut.Instance.MessageChanged(msg));

        cut.Find("button[title='Copy full message']").ClassList.Should().Contain("mud-primary-text");
        cut.Find("button[title='Copy topic']").ClassList.Should().Contain("mud-primary-text");
        cut.Find("button[title='Expand all']").ClassList.Should().Contain("mud-primary-text");
        cut.Find("button[title='Collapse all']").ClassList.Should().Contain("mud-primary-text");
        cut.Find("button[title='Copy payload']").ClassList.Should().Contain("mud-primary-text");
    }

    [Test]
    public async Task JsonMessage_ExpandCollapseButtons_AreNotRenderedAsButtonGroup()
    {
        var cut = Render<PayloadBrowser>();
        var msg = new MqttMessage { Topic = "test/topic", Payload = """{"a":{"b":{"c":"deep"}}}""" };

        await cut.InvokeAsync(() => cut.Instance.MessageChanged(msg));

        cut.FindAll(".mud-button-group-root").Should().BeEmpty();
    }

    [Test]
    public async Task JsonMessage_ExpandCollapseButtons_UseMaximizeAndMinimizeIcons()
    {
        var cut = Render<PayloadBrowser>();
        var msg = new MqttMessage { Topic = "test/topic", Payload = """{"a":{"b":{"c":"deep"}}}""" };

        await cut.InvokeAsync(() => cut.Instance.MessageChanged(msg));

        cut.Find("button[title='Expand all']").InnerHtml.Should().Contain("21 3 21 9");
        cut.Find("button[title='Collapse all']").InnerHtml.Should().Contain("4 14 10 14 10 20");
    }

    [Test]
    public async Task NonJsonMessage_DoesNotShowExpandOrCollapseButtons()
    {
        var cut = Render<PayloadBrowser>();
        var msg = new MqttMessage { Topic = "test/topic", Payload = "plain text payload" };

        await cut.InvokeAsync(() => cut.Instance.MessageChanged(msg));

        cut.FindAll(".json-expand-all-btn").Should().BeEmpty();
        cut.FindAll(".json-collapse-all-btn").Should().BeEmpty();
    }

    [Test]
    public async Task PrimitiveJsonMessage_DoesNotShowExpandOrCollapseButtons()
    {
        var cut = Render<PayloadBrowser>();
        var msg = new MqttMessage { Topic = "test/topic", Payload = "42" };

        await cut.InvokeAsync(() => cut.Instance.MessageChanged(msg));

        cut.FindAll(".json-expand-all-btn").Should().BeEmpty();
        cut.FindAll(".json-collapse-all-btn").Should().BeEmpty();
    }

    [Test]
    public async Task EmptyObjectJsonMessage_DoesNotShowExpandOrCollapseButtons()
    {
        var cut = Render<PayloadBrowser>();
        var msg = new MqttMessage { Topic = "test/topic", Payload = "{}" };

        await cut.InvokeAsync(() => cut.Instance.MessageChanged(msg));

        cut.FindAll(".json-expand-all-btn").Should().BeEmpty();
        cut.FindAll(".json-collapse-all-btn").Should().BeEmpty();
    }

    [Test]
    public async Task ExpandAllButton_ExpandsAllNodesInTree()
    {
        var cut = Render<PayloadBrowser>();
        var msg = new MqttMessage { Topic = "test/topic", Payload = """{"a":{"b":{"c":"deep"}}}""" };

        await cut.InvokeAsync(() => cut.Instance.MessageChanged(msg));

        cut.WaitForElement(".json-expand-all-btn");

        // MudTable virtualization re-renders asynchronously, which can invalidate a found
        // element's event-handler id before the click dispatches on slow (CI) machines.
        // WaitForAssertion retries the find+click until it lands on a settled render tree.
        cut.WaitForAssertion(() =>
        {
            cut.Find(".json-expand-all-btn").Click();
            cut.FindAll(".json-preview").Should().BeEmpty();
        });
        cut.FindAll(".json-key").Select(e => e.TextContent).Should().Contain("\"c\"");
    }

    [Test]
    public async Task CollapseAllButton_CollapsesAllNodesInTree()
    {
        var cut = Render<PayloadBrowser>();
        var msg = new MqttMessage { Topic = "test/topic", Payload = """{"a":{"b":{"c":"deep"}}}""" };

        await cut.InvokeAsync(() => cut.Instance.MessageChanged(msg));

        cut.WaitForElement(".json-collapse-all-btn");

        await cut.InvokeAsync(() => cut.Find(".json-collapse-all-btn").Click());

        cut.FindAll(".json-key").Should().BeEmpty();
        cut.FindAll(".json-preview").Should().HaveCount(1);
    }



    [Test]
    public async Task DelayedSelectionSwitch_IgnoresPreviousTopicResult()
    {
        var requestedSelection = _selection;
        var staleResponse = new TaskCompletionSource<SelectedMessagesSnapshot?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var currentResponse = new TaskCompletionSource<SelectedMessagesSnapshot?>(TaskCreationOptions.RunContinuationsAsynchronously);
        ReturnResponses(staleResponse.Task, currentResponse.Task);
        EnsureMudProviders();

        var initialQuery = new TaskCompletionSource<(SelectedTopicToken Token, int Limit)>(TaskCreationOptions.RunContinuationsAsynchronously);
        ObserveNextQuery(initialQuery);
        var cut = Render<PayloadBrowser>();
        (await initialQuery.Task.WaitAsync(TimeSpan.FromSeconds(5)))
            .Should().Be((requestedSelection.Token, 500));

        var currentSelection = MakeSelection("other", generation: 2, contentVersion: 1);
        var currentQuery = new TaskCompletionSource<(SelectedTopicToken Token, int Limit)>(TaskCreationOptions.RunContinuationsAsynchronously);
        ObserveNextQuery(currentQuery);
        _selection = currentSelection;
        staleResponse.SetResult(Snapshot(requestedSelection, 500, "stale"));
        (await currentQuery.Task.WaitAsync(TimeSpan.FromSeconds(5)))
            .Should().Be((currentSelection.Token, 500));

        await cut.InvokeAsync(() => { });
        cut.Markup.Should().NotContain("stale");

        currentResponse.SetResult(Snapshot(currentSelection, 500, "current"));
        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".payload-row").Should().ContainSingle();
            cut.Markup.Should().Contain("current");
            cut.Markup.Should().NotContain("stale");
        }, TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task DelayedClearThenReselect_IgnoresOldResultAndLoadsCurrentSelection()
    {
        var requestedSelection = _selection;
        var staleResponse = new TaskCompletionSource<SelectedMessagesSnapshot?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var currentResponse = new TaskCompletionSource<SelectedMessagesSnapshot?>(TaskCreationOptions.RunContinuationsAsynchronously);
        ReturnResponses(staleResponse.Task, currentResponse.Task);
        EnsureMudProviders();

        var initialQuery = new TaskCompletionSource<(SelectedTopicToken Token, int Limit)>(TaskCreationOptions.RunContinuationsAsynchronously);
        ObserveNextQuery(initialQuery);
        var cut = Render<PayloadBrowser>();
        (await initialQuery.Task.WaitAsync(TimeSpan.FromSeconds(5)))
            .Should().Be((requestedSelection.Token, 500));

        var clearedSelection = MakeSelection(null, generation: 2, contentVersion: 0);
        var currentSelection = MakeSelection("sensor", generation: 3, contentVersion: 0);
        var currentQuery = new TaskCompletionSource<(SelectedTopicToken Token, int Limit)>(TaskCreationOptions.RunContinuationsAsynchronously);
        ObserveNextQuery(currentQuery);
        _selection = clearedSelection;
        _selection = currentSelection;
        staleResponse.SetResult(Snapshot(requestedSelection, 500, "stale"));

        (await currentQuery.Task.WaitAsync(TimeSpan.FromSeconds(5)))
            .Should().Be((currentSelection.Token, 500));
        await cut.InvokeAsync(() => { });
        cut.Markup.Should().NotContain("stale");
        currentResponse.SetResult(Snapshot(currentSelection, 500, "current-after-clear"));

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("current-after-clear");
            cut.Markup.Should().NotContain("stale");
        }, TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task DelayedResult_AfterPurgeAndSamePathRecreation_LoadsOnlyTheNewGeneration()
    {
        var requestedSelection = _selection;
        var staleResponse = new TaskCompletionSource<SelectedMessagesSnapshot?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var currentResponse = new TaskCompletionSource<SelectedMessagesSnapshot?>(TaskCreationOptions.RunContinuationsAsynchronously);
        ReturnResponses(staleResponse.Task, currentResponse.Task);
        EnsureMudProviders();

        var initialQuery = new TaskCompletionSource<(SelectedTopicToken Token, int Limit)>(TaskCreationOptions.RunContinuationsAsynchronously);
        ObserveNextQuery(initialQuery);
        var cut = Render<PayloadBrowser>();
        (await initialQuery.Task.WaitAsync(TimeSpan.FromSeconds(5)))
            .Should().Be((requestedSelection.Token, 500));

        var currentSelection = MakeSelection("sensor", generation: 3, contentVersion: 0);
        var currentQuery = new TaskCompletionSource<(SelectedTopicToken Token, int Limit)>(TaskCreationOptions.RunContinuationsAsynchronously);
        ObserveNextQuery(currentQuery);
        _selection = MakeSelection("other", generation: 2, contentVersion: 1);
        _selection = currentSelection;
        staleResponse.SetResult(Snapshot(requestedSelection, 500, "stale"));

        (await currentQuery.Task.WaitAsync(TimeSpan.FromSeconds(5)))
            .Should().Be((currentSelection.Token, 500));
        await cut.InvokeAsync(() => { });
        cut.Markup.Should().NotContain("stale");
        currentResponse.SetResult(Snapshot(currentSelection, 500, "current-recreated"));

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("current-recreated");
            cut.Markup.Should().NotContain("stale");
        }, TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task DelayedResult_WhenDisplayLimitChanges_QueriesAndAppliesTheNewLimit()
    {
        var requestedSelection = _selection;
        var staleResponse = new TaskCompletionSource<SelectedMessagesSnapshot?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var currentResponse = new TaskCompletionSource<SelectedMessagesSnapshot?>(TaskCreationOptions.RunContinuationsAsynchronously);
        ReturnResponses(staleResponse.Task, currentResponse.Task);
        EnsureMudProviders();

        var initialQuery = new TaskCompletionSource<(SelectedTopicToken Token, int Limit)>(TaskCreationOptions.RunContinuationsAsynchronously);
        ObserveNextQuery(initialQuery);
        var cut = Render<PayloadBrowser>();
        (await initialQuery.Task.WaitAsync(TimeSpan.FromSeconds(5)))
            .Should().Be((requestedSelection.Token, 500));

        _mockPerformance.Performance.Returns(new PerformanceSettings { MaxDisplayMessages = 3 });
        var updatedLimitQuery = new TaskCompletionSource<(SelectedTopicToken Token, int Limit)>(TaskCreationOptions.RunContinuationsAsynchronously);
        ObserveNextQuery(updatedLimitQuery);
        staleResponse.SetResult(Snapshot(requestedSelection, 500, "stale"));

        (await updatedLimitQuery.Task.WaitAsync(TimeSpan.FromSeconds(5)))
            .Should().Be((requestedSelection.Token, 3));
        await cut.InvokeAsync(() => { });
        cut.Markup.Should().NotContain("stale");
        currentResponse.SetResult(Snapshot(requestedSelection, 3, "current-limited"));

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("current-limited");
            cut.Markup.Should().NotContain("stale");
        }, TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task DelayedResult_AfterDispose_DrainsWithoutApplying()
    {
        var requestedSelection = _selection;
        var response = new TaskCompletionSource<SelectedMessagesSnapshot?>();
        ReturnResponses(response.Task);
        EnsureMudProviders();

        var initialQuery = new TaskCompletionSource<(SelectedTopicToken Token, int Limit)>(TaskCreationOptions.RunContinuationsAsynchronously);
        ObserveNextQuery(initialQuery);
        var cut = Render<PayloadBrowser>();
        (await initialQuery.Task.WaitAsync(TimeSpan.FromSeconds(5)))
            .Should().Be((requestedSelection.Token, 500));
        await cut.Instance.DisposeAsync();
        response.SetResult(Snapshot(requestedSelection, 500, "stale"));
        await cut.InvokeAsync(() => Task.CompletedTask);

        _mockMetrics.DidNotReceive().SetDisplayedMessageCount(1);
    }

    [Test]
    public async Task ContentAdvancingDuringFetch_AppliesInterimThenRefreshesNewestVersion()
    {
        var requestedSelection = _selection;
        var interimResponse = new TaskCompletionSource<SelectedMessagesSnapshot?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var latestResponse = new TaskCompletionSource<SelectedMessagesSnapshot?>(TaskCreationOptions.RunContinuationsAsynchronously);
        ReturnResponses(interimResponse.Task, latestResponse.Task);
        EnsureMudProviders();

        var initialQuery = new TaskCompletionSource<(SelectedTopicToken Token, int Limit)>(TaskCreationOptions.RunContinuationsAsynchronously);
        ObserveNextQuery(initialQuery);
        var cut = Render<PayloadBrowser>();
        (await initialQuery.Task.WaitAsync(TimeSpan.FromSeconds(5)))
            .Should().Be((requestedSelection.Token, 500));
        var latestSelection = MakeSelection("sensor", generation: 1, contentVersion: 2);
        _selection = latestSelection;
        var latestQuery = new TaskCompletionSource<(SelectedTopicToken Token, int Limit)>(TaskCreationOptions.RunContinuationsAsynchronously);
        ObserveNextQuery(latestQuery);
        interimResponse.SetResult(Snapshot(requestedSelection, 500, "interim"));

        (await latestQuery.Task.WaitAsync(TimeSpan.FromSeconds(5)))
            .Should().Be((latestSelection.Token, 500));
        await cut.InvokeAsync(() => Task.CompletedTask);
        cut.Markup.Should().Contain("interim");
        latestResponse.SetResult(Snapshot(latestSelection, 500, "latest"));

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("latest");
            cut.Markup.Should().NotContain("interim");
        }, TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task ContentRefreshPreservesDetailWithinGeneration_AndNewGenerationAtSamePathClearsIt()
    {
        var initialSelection = _selection;
        var initialResponse = new TaskCompletionSource<SelectedMessagesSnapshot?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sameGenerationResponse = new TaskCompletionSource<SelectedMessagesSnapshot?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var recreatedResponse = new TaskCompletionSource<SelectedMessagesSnapshot?>(TaskCreationOptions.RunContinuationsAsynchronously);
        ReturnResponses(initialResponse.Task, sameGenerationResponse.Task, recreatedResponse.Task);
        EnsureMudProviders();

        var initialQuery = new TaskCompletionSource<(SelectedTopicToken Token, int Limit)>(TaskCreationOptions.RunContinuationsAsynchronously);
        ObserveNextQuery(initialQuery);
        var cut = Render<PayloadBrowser>();
        (await initialQuery.Task.WaitAsync(TimeSpan.FromSeconds(5)))
            .Should().Be((initialSelection.Token, 500));
        initialResponse.SetResult(Snapshot(initialSelection, 500, "initial-row"));
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("initial-row"), TimeSpan.FromSeconds(5));

        var detailMessage = new MqttMessage { Topic = "sensor/detail", Payload = "user-detail" };
        await cut.InvokeAsync(() => cut.Instance.MessageChanged(detailMessage));
        cut.Markup.Should().Contain("Message Details");
        cut.Markup.Should().Contain("user-detail");

        var sameGeneration = MakeSelection("sensor", generation: 1, contentVersion: 2);
        var sameGenerationQuery = new TaskCompletionSource<(SelectedTopicToken Token, int Limit)>(TaskCreationOptions.RunContinuationsAsynchronously);
        ObserveNextQuery(sameGenerationQuery);
        _selection = sameGeneration;
        (await sameGenerationQuery.Task.WaitAsync(TimeSpan.FromSeconds(5)))
            .Should().Be((sameGeneration.Token, 500));
        sameGenerationResponse.SetResult(new SelectedMessagesSnapshot(sameGeneration, 500,
            System.Collections.Immutable.ImmutableArray.Create(
                new MqttMessage { Topic = "sensor", Payload = "same-generation-row" },
                new MqttMessage { Topic = "sensor", Payload = "second-same-generation-row" })));
        cut.WaitForAssertion(() => _mockMetrics.Received().SetDisplayedMessageCount(2), TimeSpan.FromSeconds(5));
        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("Message Details");
            cut.Markup.Should().Contain("user-detail");
        }, TimeSpan.FromSeconds(5));

        await cut.InvokeAsync(() => cut.Find("button[title='Back to message list']").Click());
        cut.Markup.Should().Contain("same-generation-row");
        await cut.InvokeAsync(() => cut.Instance.MessageChanged(detailMessage));

        var recreated = MakeSelection("sensor", generation: 2, contentVersion: 0);
        var recreatedQuery = new TaskCompletionSource<(SelectedTopicToken Token, int Limit)>(TaskCreationOptions.RunContinuationsAsynchronously);
        ObserveNextQuery(recreatedQuery);
        _selection = recreated;
        (await recreatedQuery.Task.WaitAsync(TimeSpan.FromSeconds(5)))
            .Should().Be((recreated.Token, 500));
        recreatedResponse.SetResult(Snapshot(recreated, 500, "recreated-row"));
        cut.WaitForAssertion(() => _mockMetrics.Received().SetDisplayedMessageCount(1), TimeSpan.FromSeconds(5));
        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("recreated-row");
            cut.Markup.Should().NotContain("Message Details");
            cut.Markup.Should().NotContain("user-detail");
        }, TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task DisposeAsync_SetsDisposedFlagAndDoesNotThrow()
    {
        var cut = Render<PayloadBrowser>();

        await cut.Instance.DisposeAsync();

        cut.Instance.Disposed.Should().BeTrue();
    }


    [Test]
    public void GetPayloadType_NullOrWhitespace_ReturnsEmpty()
    {
        Render<PayloadBrowser>();

        PayloadBrowser.GetPayloadType(null).Should().Be(PayloadBrowser.PayloadType.Empty);
        PayloadBrowser.GetPayloadType("").Should().Be(PayloadBrowser.PayloadType.Empty);
        PayloadBrowser.GetPayloadType("   ").Should().Be(PayloadBrowser.PayloadType.Empty);
    }

    [Test]
    public void GetPayloadType_ValidJson_ReturnsJson()
    {
        Render<PayloadBrowser>();

        PayloadBrowser.GetPayloadType("""{"key":"value"}""").Should().Be(PayloadBrowser.PayloadType.Json);
        PayloadBrowser.GetPayloadType("[1,2,3]").Should().Be(PayloadBrowser.PayloadType.Json);
    }

    [Test]
    public void GetPayloadType_XmlPayload_ReturnsXml()
    {
        Render<PayloadBrowser>();

        PayloadBrowser.GetPayloadType("<root><child/></root>").Should().Be(PayloadBrowser.PayloadType.Xml);
    }

    [Test]
    public void GetPayloadType_BooleanPayload_ReturnsBoolean()
    {
        Render<PayloadBrowser>();

        PayloadBrowser.GetPayloadType("true").Should().Be(PayloadBrowser.PayloadType.Boolean);
        PayloadBrowser.GetPayloadType("False").Should().Be(PayloadBrowser.PayloadType.Boolean);
        PayloadBrowser.GetPayloadType("TRUE").Should().Be(PayloadBrowser.PayloadType.Boolean);
    }

    [Test]
    public void GetPayloadType_NumericPayload_ReturnsNumber()
    {
        Render<PayloadBrowser>();

        PayloadBrowser.GetPayloadType("42").Should().Be(PayloadBrowser.PayloadType.Number);
        PayloadBrowser.GetPayloadType("3.14").Should().Be(PayloadBrowser.PayloadType.Number);
        PayloadBrowser.GetPayloadType("-5.0").Should().Be(PayloadBrowser.PayloadType.Number);
        PayloadBrowser.GetPayloadType("1e6").Should().Be(PayloadBrowser.PayloadType.Number);
    }

    [Test]
    public void GetPayloadType_PlainString_ReturnsText()
    {
        Render<PayloadBrowser>();

        PayloadBrowser.GetPayloadType("hello world").Should().Be(PayloadBrowser.PayloadType.Text);
        PayloadBrowser.GetPayloadType("temperature:42").Should().Be(PayloadBrowser.PayloadType.Text);
        PayloadBrowser.GetPayloadType("OK").Should().Be(PayloadBrowser.PayloadType.Text);
    }


    [TestCase("empty", "Empty")]
    [TestCase("sparkplug-b", "Sparkplug B")]
    [TestCase("messagepack", "MessagePack")]
    [TestCase("binary", "Binary")]
    [TestCase("json", "JSON")]
    [TestCase("xml", "XML")]
    [TestCase("hex", "Hex text")]
    [TestCase("base64", "Base64 text")]
    [TestCase("plaintext", "Plain text")]
    public void FormatDisplayNames_KnownFormatIds_ReturnExpectedName(string formatId, string expected)
    {
        Render<PayloadBrowser>();
        var formatDisplayNames = Services.GetRequiredService<IFormatDisplayNames>();

        formatDisplayNames.GetDisplayName(formatId).Should().Be(expected);
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void FormatDisplayNames_NullOrWhitespace_ReturnsNull(string? formatId)
    {
        Render<PayloadBrowser>();
        var formatDisplayNames = Services.GetRequiredService<IFormatDisplayNames>();

        formatDisplayNames.GetDisplayName(formatId).Should().BeNull();
    }

    [Test]
    public void FormatDisplayNames_UnknownFormatId_ReturnsRawId()
    {
        Render<PayloadBrowser>();
        var formatDisplayNames = Services.GetRequiredService<IFormatDisplayNames>();

        // Unknown non-null ids fall through so plugin formats still render informatively.
        formatDisplayNames.GetDisplayName("custom-plugin-format").Should().Be("custom-plugin-format");
    }


    [Test]
    public async Task SelectedMessage_WithFormatId_RendersFormatInMetaTable()
    {
        var cut = Render<PayloadBrowser>();
        var msg = new MqttMessage
        {
            Topic = "test/topic",
            Payload = """{"a":1}""",
            FormatId = "base64"
        };

        await cut.InvokeAsync(() => cut.Instance.MessageChanged(msg));

        var meta = cut.Find(".payload-detail-meta");
        meta.TextContent.Should().Contain("Format");
        meta.TextContent.Should().Contain("Base64 text");
    }

    [Test]
    public async Task SelectedMessage_WithNoFormatId_OmitsFormatRow()
    {
        var cut = Render<PayloadBrowser>();
        var msg = new MqttMessage
        {
            Topic = "test/topic",
            Payload = """{"a":1}"""
        };

        await cut.InvokeAsync(() => cut.Instance.MessageChanged(msg));

        // Detail view is visible, but no Format label appears since FormatId is null.
        cut.Markup.Should().Contain("Message Details");
        var labels = cut.FindAll(".payload-detail-meta td.pb-meta-label")
            .Select(td => td.TextContent.Trim());
        labels.Should().NotContain("Format");
    }

    [Test]
    public async Task SelectedMessage_WithJsonFormatId_StillShowsJsonContentChip()
    {
        var cut = Render<PayloadBrowser>();
        var msg = new MqttMessage
        {
            Topic = "test/topic",
            Payload = """{"a":1}""",
            FormatId = "json"
        };

        await cut.InvokeAsync(() => cut.Instance.MessageChanged(msg));

        // Format row reflects detection; payload content heuristic still labels the body.
        cut.Find(".payload-detail-meta").TextContent.Should().Contain("JSON");
        cut.Find(".payload-detail-body").TextContent.Should().Contain("JSON");
    }


    [Test]
    public void TryFormatXml_ValidXml_ReturnsFormattedString()
    {
        var result = PayloadBrowser.TryFormatXml("<root><child/></root>");
        result.Should().NotBeNull();
        result.Should().Contain("root");
    }

    [Test]
    public void TryFormatXml_InvalidXml_ReturnsNull()
    {
        var result = PayloadBrowser.TryFormatXml("<unclosed");
        result.Should().BeNull();
    }


    [TestCase("temperature", ExpectedResult = "temperature")]
    [TestCase("metrics.value", ExpectedResult = "value")]
    [TestCase("a.b.c", ExpectedResult = "c")]
    public string ChartShortLabel_ReturnsLastSegment(string path)
        => PayloadBrowser.ChartShortLabel(path);

    [Test]
    public void ChartLabel_WithSuggestedSeriesName_ReturnsSeriesName()
    {
        PayloadBrowser.ChartLabel(new ChartFieldSelection("metrics[0].doubleValue", "Metric-1"))
            .Should().Be("Metric-1");
    }

    [Test]
    public void ChartLabel_WithoutSuggestedSeriesName_ReturnsLastPathSegment()
    {
        PayloadBrowser.ChartLabel(new ChartFieldSelection("metrics[0].doubleValue"))
            .Should().Be("doubleValue");
    }


    [Test]
    public async Task AddToChart_OpensQuickAddToChartDialog()
    {
        var mockDialogService = Services.GetRequiredService<IDialogService>();
        var mockDialogRef = Substitute.For<IDialogReference>();
        mockDialogRef.Result.Returns(Task.FromResult<DialogResult?>(DialogResult.Cancel()));
        mockDialogService
            .ShowAsync<QuickAddToChartDialog>(Arg.Any<string>(), Arg.Any<DialogParameters>(), Arg.Any<DialogOptions>())
            .Returns(Task.FromResult(mockDialogRef));

        var cut = Render<PayloadBrowser>();

        await cut.InvokeAsync(() => cut.Instance.AddToChart("$.temp", "temp", "sensors/temp"));

        await mockDialogService.Received(1)
            .ShowAsync<QuickAddToChartDialog>(Arg.Any<string>(), Arg.Any<DialogParameters>(), Arg.Any<DialogOptions>());
    }

    [Test]
    public async Task AddToChart_WhenDialogReturnsMessage_ShowsSnackbar()
    {
        var mockDialogService = Services.GetRequiredService<IDialogService>();
        var mockSnackbar = Services.GetRequiredService<ISnackbar>();
        var mockDialogRef = Substitute.For<IDialogReference>();
        mockDialogRef.Result.Returns(Task.FromResult<DialogResult?>(DialogResult.Ok("Added to chart")));
        mockDialogService
            .ShowAsync<QuickAddToChartDialog>(Arg.Any<string>(), Arg.Any<DialogParameters>(), Arg.Any<DialogOptions>())
            .Returns(Task.FromResult(mockDialogRef));

        var cut = Render<PayloadBrowser>();

        await cut.InvokeAsync(() => cut.Instance.AddToChart("$.temp", "temp", "sensors/temp"));

        mockSnackbar.Received(1).Add("Added to chart", Severity.Success,
            Arg.Any<Action<SnackbarOptions>?>(), Arg.Any<string?>());
    }

    [Test]
    public async Task CopyFullMessage_WhenMessageSelected_WritesSerializedJsonToClipboard()
    {
        var mockClipboard = Services.GetRequiredService<IClipboardService>();
        var message = new MqttMessage { Topic = "sensors/temp", Payload = "42" };
        var cut = Render<PayloadBrowser>();
        await cut.InvokeAsync(() => cut.Instance.MessageChanged(message));

        cut.WaitForElement("button[title='Copy full message']");
        await cut.InvokeAsync(() => cut.Find("button[title='Copy full message']").Click());

        await mockClipboard.Received(1).WriteTextAsync(JsonSerializer.Serialize(message));
    }

    [Test]
    public async Task CopyTopic_WhenMessageSelected_WritesTopicToClipboard()
    {
        var mockClipboard = Services.GetRequiredService<IClipboardService>();
        var message = new MqttMessage { Topic = "sensors/temp", Payload = "42" };
        var cut = Render<PayloadBrowser>();
        await cut.InvokeAsync(() => cut.Instance.MessageChanged(message));

        cut.WaitForElement("button[title='Copy topic']");
        await cut.InvokeAsync(() => cut.Find("button[title='Copy topic']").Click());

        await mockClipboard.Received(1).WriteTextAsync("sensors/temp");
    }

    [Test]
    public async Task CopyPayload_WhenMessageSelected_WritesPayloadToClipboard()
    {
        var mockClipboard = Services.GetRequiredService<IClipboardService>();
        var message = new MqttMessage { Topic = "sensors/temp", Payload = "42" };
        var cut = Render<PayloadBrowser>();
        await cut.InvokeAsync(() => cut.Instance.MessageChanged(message));

        cut.WaitForElement("button[title='Copy payload']");
        await cut.InvokeAsync(() => cut.Find("button[title='Copy payload']").Click());

        await mockClipboard.Received(1).WriteTextAsync("42");
    }

    [Test]
    public async Task MessageChanged_SelectionSurvivesInitialTimerTick()
    {
        var messages = new List<MqttMessage>
        {
            new() { Topic = "sensor/temp", Payload = "1" }
        };
        ReturnMessages(messages);
        EnsureMudProviders();

        var cut = Render<PayloadBrowser>();

        // Wait for the first timer tick to load messages.
        cut.WaitForAssertion(() =>
            _mockMetrics.Received().SetDisplayedMessageCount(1));

        var message = new MqttMessage { Topic = "sensor/temp", Payload = "42" };
        await cut.InvokeAsync(() => cut.Instance.MessageChanged(message));

        var refreshedSelection = MakeSelection("sensor", generation: 1, contentVersion: 2);
        _selection = refreshedSelection;
        cut.WaitForAssertion(() =>
            _mockMsgStore.Received(1).GetSelectedMessagesAsync(refreshedSelection.Token, 500),
            TimeSpan.FromSeconds(5));

        cut.Find("button[title='Copy topic']").Should().NotBeNull();
        cut.Find("button[title='Copy payload']").Should().NotBeNull();
    }

    [Test]
    public void Refresh_AfterTopicSelected_ReportsCountToMetrics()
    {
        var messages = Enumerable.Range(0, 5)
            .Select(i => new MqttMessage { Topic = "sensor/temp", Payload = i.ToString() })
            .ToList();
        ReturnMessages(messages);
        EnsureMudProviders();

        var cut = Render<PayloadBrowser>();

        cut.WaitForAssertion(() =>
            _mockMetrics.Received().SetDisplayedMessageCount(5));
    }

    [Test]
    public void NoSelectedTopic_ResetsDisplayedCountToZero()
    {
        _selection = MakeSelection(null, generation: 2, contentVersion: 0);
        EnsureMudProviders();

        var cut = Render<PayloadBrowser>();

        cut.WaitForAssertion(() =>
            _mockMetrics.Received().SetDisplayedMessageCount(0));
    }

    [Test]
    public async Task Dispose_ResetsDisplayedCountToZero()
    {
        EnsureMudProviders();
        var cut = Render<PayloadBrowser>();

        await cut.Instance.DisposeAsync();

        _mockMetrics.Received().SetDisplayedMessageCount(0);
    }

    [Test]
    public async Task DetailView_QosRow_DisplaysFormattedLabel()
    {
        var cut = Render<PayloadBrowser>();
        var msg = new MqttMessage
        {
            Topic = "test/topic",
            Payload = """{"a":1}""",
            QualityOfServiceLevel = MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce
        };

        await cut.InvokeAsync(() => cut.Instance.MessageChanged(msg));

        var meta = cut.Find(".payload-detail-meta");
        meta.TextContent.Should().Contain("1 · At least once");
    }

    [Test]
    public async Task DetailView_QosRow_DoesNotShowRawEnumName()
    {
        var cut = Render<PayloadBrowser>();
        var msg = new MqttMessage
        {
            Topic = "test/topic",
            Payload = """{"a":1}""",
            QualityOfServiceLevel = MQTTnet.Protocol.MqttQualityOfServiceLevel.AtMostOnce
        };

        await cut.InvokeAsync(() => cut.Instance.MessageChanged(msg));

        var meta = cut.Find(".payload-detail-meta");
        meta.TextContent.Should().Contain("0 · At most once");
        meta.TextContent.Should().NotContain("AtMostOnce");
    }
}
