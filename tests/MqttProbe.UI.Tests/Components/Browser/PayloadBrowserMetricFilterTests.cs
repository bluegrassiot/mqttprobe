using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using MqttProbe.Core.Models.Configuration;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Services.Configuration;
using MqttProbe.Core.Services.Metrics;
using MqttProbe.Core.Services.Mqtt;
using MqttProbe.Core.Services.Plugins.BuiltIn;
using MqttProbe.Core.Services.Plugins.Pipeline;
using MqttProbe.Core.Services.Plugins.Registry;
using MqttProbe.UI.Tests.TestHelpers;
using MudBlazor;

namespace MqttProbe.UI.Tests.Components.Browser;

[TestFixture]
public sealed class PayloadBrowserMetricFilterTests : BunitTestContext
{
    private const string Label = "spBv1.0/g/n1 Motor/Temp";

    private static readonly SparkplugSelectionScope _nodeScope =
        new(SparkplugSelectionKind.Node, "spBv1.0/g", "n1");

    private IMessageStoreManager _mockMsgStore = null!;
    private SelectedTopicState _selection = null!;
    private TaskCompletionSource<(SelectedTopicToken Token, int Limit)>? _nextQueryRequest;

    [SetUp]
    public void Setup()
    {
        _mockMsgStore = Substitute.For<IMessageStoreManager>();
        var token = new SelectedTopicToken(Guid.NewGuid(), 1, 1);
        _selection = new SelectedTopicState(token, "metric:key", 0, Label)
        {
            Mode = TopicSelectionMode.SparkplugMetric,
            Metric = new MqttSparkplugMetricTarget(_nodeScope, "Motor/Temp")
        };
        _mockMsgStore.GetSelectedTopicState().Returns(_ => _selection);
        _mockMsgStore.When(store => store.SelectTopics(null))
            .Do(_ => _selection = _selection with
            {
                Token = new SelectedTopicToken(_selection.Token.StoreId, 2, 2),
                Key = null,
                Label = null,
                Mode = TopicSelectionMode.SingleTopic,
                Metric = null
            });

        Services.AddSingleton(_mockMsgStore);
        var performance = Substitute.For<IPerformanceSettings>();
        performance.Performance.Returns(new AppConfiguration().Performance);
        Services.AddPerformanceSettings(performance);
        Services.AddSingleton(Substitute.For<IUxMetricsService>());
        Services.AddUiSettings(SettingsSubstituteRegistration.CreateUiSettings());
        Services.AddSingleton(Substitute.For<IDialogService>());
        Services.AddSingleton(Substitute.For<ISnackbar>());

        var builder = new PluginRegistryBuilder();
        BuiltInPluginRegistration.RegisterBuiltIns(builder);
        var pipeline = new PayloadPipeline(builder.Build(),
            Substitute.For<Microsoft.Extensions.Logging.ILogger<PayloadPipeline>>());
        Services.AddSingleton(pipeline);
        Services.AddSingleton<IFormatDisplayNames>(new FormatDisplayNames(pipeline));
    }

    private void ReturnMessages(params MqttMessage[] messages)
    {
        _mockMsgStore.GetSelectedMessagesAsync(Arg.Any<SelectedTopicToken>(), Arg.Any<int>())
            .Returns(call =>
            {
                var token = call.ArgAt<SelectedTopicToken>(0);
                Interlocked.Exchange(ref _nextQueryRequest, null)?.TrySetResult((token, call.ArgAt<int>(1)));
                return Task.FromResult<SelectedMessagesSnapshot?>(
                    new SelectedMessagesSnapshot(_selection with { Token = token }, call.ArgAt<int>(1),
                        ImmutableArray.CreateRange(messages)));
            });
    }

    private void ReturnStaleSnapshot()
    {
        _mockMsgStore.GetSelectedMessagesAsync(Arg.Any<SelectedTopicToken>(), Arg.Any<int>())
            .Returns(call =>
            {
                var token = call.ArgAt<SelectedTopicToken>(0);
                Interlocked.Exchange(ref _nextQueryRequest, null)?.TrySetResult((token, call.ArgAt<int>(1)));
                var stale = _selection with
                {
                    Token = new SelectedTopicToken(token.StoreId, token.Generation + 1, token.ContentVersion),
                    MessageCount = 1
                };
                return Task.FromResult<SelectedMessagesSnapshot?>(new SelectedMessagesSnapshot(stale,
                    call.ArgAt<int>(1), ImmutableArray.Create(new MqttMessage
                    {
                        Topic = "spBv1.0/g/NDATA/n1",
                        Payload = "18.4"
                    })));
            });
    }

    [Test]
    public void MetricFilterBar_NamesTheSelectionAboveTheMessageList_EvenWithNoMatches()
    {
        ReturnMessages();
        EnsureMudProviders();

        var cut = Render<PayloadBrowser>();

        cut.WaitForAssertion(() => cut.Find(".pb-metric-filter__label").TextContent.Should().Be(Label));
        cut.Find(".payload-topic-title").TextContent.Trim().Should().Be(Label,
            "the list heading keeps the metric label when no message matches");
        cut.Markup.Should().Contain("No stored messages for the selected topics");

        var barIndex = cut.Markup.IndexOf("pb-metric-filter", StringComparison.Ordinal);
        var listIndex = cut.Markup.IndexOf("payload-message-list", StringComparison.Ordinal);
        barIndex.Should().BeGreaterThan(0).And.BeLessThan(listIndex);
    }

    [Test]
    public void ClearMetricFilter_RemovesTheFilterAndAsksCoreToClear()
    {
        ReturnMessages();
        EnsureMudProviders();
        var cut = Render<PayloadBrowser>();
        cut.WaitForAssertion(() => cut.FindAll(".pb-metric-filter").Should().ContainSingle());

        cut.Find(".pb-metric-filter__clear").Click();

        _mockMsgStore.Received(1).SelectTopics(null);
        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".pb-metric-filter").Should().BeEmpty();
            cut.Markup.Should().Contain("Select a topic to see messages");
        });
    }

    [Test]
    public void StaleAsyncSnapshot_DoesNotReplaceTheMetricHeading()
    {
        ReturnStaleSnapshot();
        EnsureMudProviders();
        var request = new TaskCompletionSource<(SelectedTopicToken Token, int Limit)>();
        _nextQueryRequest = request;
        var cut = Render<PayloadBrowser>();

        cut.WaitForAssertion(() => cut.Find(".pb-metric-filter__label").Should().NotBeNull());
        request.Task.IsCompleted.Should().BeTrue("the browser queried the store");
        cut.Find(".payload-topic-title").TextContent.Trim().Should().Be(Label);
        cut.FindAll(".payload-row").Should().BeEmpty(
            "a snapshot from a stale selection token is rejected");
    }
}
