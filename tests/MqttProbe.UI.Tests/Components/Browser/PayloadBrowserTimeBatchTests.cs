using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using MqttProbe.Core.Models.Configuration;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Services.Configuration;
using MqttProbe.Core.Services.Metrics;
using MqttProbe.Core.Services.Mqtt;
using MqttProbe.Core.Services.Plugins.BuiltIn;
using MqttProbe.Core.Services.Plugins.Pipeline;
using MqttProbe.Core.Services.Plugins.Registry;
using MqttProbe.UI.Services.Formatting;
using MqttProbe.UI.Tests.TestHelpers;
using MudBlazor;

namespace MqttProbe.UI.Tests.Components.Browser;

[TestFixture]
public class PayloadBrowserTimeBatchTests : BunitTestContext
{
    private static readonly Guid _storeId = Guid.NewGuid();
    private static readonly DateTime _baseUtc = new(2026, 10, 29, 22, 41, 7, 123, DateTimeKind.Utc);

    private IMessageStoreManager _store = null!;
    private RecordingUiSettings _settings = null!;
    private SelectedTopicState _selection = null!;
    private List<MqttMessage> _messages = [];
    private ScriptedRuntime _runtime = null!;
    private int _queries;

    [SetUp]
    public void Setup()
    {
        _store = Substitute.For<IMessageStoreManager>();
        _selection = new SelectedTopicState(new SelectedTopicToken(_storeId, 1, 1), "sensor/temp", 0);
        _store.GetSelectedTopicState().Returns(_ => _selection);
        _store.GetSelectedMessagesAsync(Arg.Any<SelectedTopicToken>(), Arg.Any<int>())
            .Returns(call =>
            {
                _queries++;
                return Task.FromResult<SelectedMessagesSnapshot?>(new SelectedMessagesSnapshot(
                    _selection with { Token = call.ArgAt<SelectedTopicToken>(0) },
                    call.ArgAt<int>(1),
                    ImmutableArray.CreateRange(_messages)));
            });
        Services.AddSingleton(_store);

        _settings = new RecordingUiSettings();
        _settings.Ui.MessageTime = new MessageTimePreferences
        {
            Zone = MessageTimePreferences.ZoneLocal,
            ClockFormat = MessageTimePreferences.ClockFormat12Hour
        };
        Services.AddUiSettings(_settings);

        var performance = Substitute.For<IPerformanceSettings>();
        performance.Performance.Returns(new AppConfiguration().Performance);
        Services.AddPerformanceSettings(performance);

        Services.AddSingleton(Substitute.For<IUxMetricsService>());
        Services.AddSingleton(Substitute.For<IDialogService>());
        Services.AddSingleton(Substitute.For<ISnackbar>());

        var builder = new PluginRegistryBuilder();
        BuiltInPluginRegistration.RegisterBuiltIns(builder);
        var pipeline = new PayloadPipeline(builder.Build(), Substitute.For<Microsoft.Extensions.Logging.ILogger<PayloadPipeline>>());
        Services.AddSingleton<IFormatDisplayNames>(new FormatDisplayNames(pipeline));

        _runtime = new ScriptedRuntime();
        Services.AddSingleton<IDeviceTimeFormatter>(new DeviceTimeFormatterInterop(_runtime));
    }

    private static MqttMessage Message(int offsetMilliseconds) =>
        new()
        {
            Topic = "sensor/temp",
            Payload = "{}",
            DateTimeReceived = _baseUtc.AddMilliseconds(offsetMilliseconds)
        };

    private IRenderedComponent<PayloadBrowser> RenderBrowser()
    {
        Render<MudPopoverProvider>();
        return Render<PayloadBrowser>();
    }

    private void Publish(int count)
    {
        for (var index = 0; index < count; index++)
            _messages.Add(Message(index));
        _selection = _selection with { Token = new SelectedTopicToken(_storeId, 1, _selection.Token.ContentVersion + 1) };
    }

    private static IReadOnlyList<string> RowTimes(IRenderedComponent<PayloadBrowser> browser) =>
        [.. browser.FindAll("[data-time-value]").Select(element => element.TextContent.Trim())];

    [Test]
    public void ABatchPerCapacity_StillFormatsEveryVisibleRow()
    {
        Publish(500);
        var browser = RenderBrowser();

        browser.WaitForAssertion(() =>
        {
            browser.FindAll("[data-time-pending]").Should().BeEmpty();
            RowTimes(browser).Should().NotBeEmpty().And.OnlyContain(time => time == "T");
        }, TimeSpan.FromSeconds(10));

        _runtime.Module.BatchSizes.Should().Equal(64, 64, 64, 64, 64, 64, 64, 52);
    }

    [Test]
    public void SnapshotArrivingDuringABatch_KeepsEarlierTimestampsAndFormatsTheNewest()
    {
        Publish(200);
        var browser = RenderBrowser();
        browser.WaitForAssertion(
            () => browser.FindAll("[data-time-pending]").Should().BeEmpty(),
            TimeSpan.FromSeconds(10));

        var gate = _runtime.Module.GateNextFormatCall();
        var queries = _queries;
        Publish(3);
        browser.WaitForAssertion(() => _queries.Should().BeGreaterThan(queries), TimeSpan.FromSeconds(10));

        gate.SetResult();

        browser.WaitForAssertion(() =>
        {
            browser.FindAll("[data-time-pending]").Should().BeEmpty(
                "a snapshot that lands mid-batch must not drop the timestamps already applied");
            RowTimes(browser).Should().OnlyContain(time => time == "T");
            _runtime.Module.BatchSizes.Should().Contain(11,
                "the 203-message snapshot must be formatted in full, ending in its 11-item batch");
        }, TimeSpan.FromSeconds(10));
    }

    [Test]
    public void ClickingARowDuringABatch_OpensTheMessageDetails()
    {
        Publish(200);
        var browser = RenderBrowser();
        browser.WaitForAssertion(
            () => browser.FindAll("[data-time-pending]").Should().BeEmpty(),
            TimeSpan.FromSeconds(10));

        var gate = _runtime.Module.GateNextFormatCall();
        var queries = _queries;
        Publish(3);
        browser.WaitForAssertion(() => _queries.Should().BeGreaterThan(queries), TimeSpan.FromSeconds(10));

        browser.FindAll(".payload-row")[0].Click();

        browser.WaitForAssertion(
            () => browser.FindAll(".pb-received-instant").Should().ContainSingle(),
            TimeSpan.FromSeconds(5));

        gate.SetResult();
    }

    private sealed class ScriptedRuntime : IJSRuntime
    {
        internal ScriptedModule Module { get; } = new();

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier, CancellationToken cancellationToken, object?[]? args) =>
            identifier == "import"
                ? new ValueTask<TValue>((TValue)(object)Module)
                : throw new InvalidOperationException($"Unexpected identifier {identifier}");
    }

    private sealed class ScriptedModule : IJSObjectReference
    {
        internal List<int> BatchSizes { get; } = [];
        internal int Calls => BatchSizes.Count;
        private TaskCompletionSource? _gate;

        internal TaskCompletionSource GateNextFormatCall()
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _gate = gate;
            return gate;
        }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            if (identifier == "installTimeBridge")
                return default;

            return InvokeFormatAsync<TValue>(args);
        }

        private async ValueTask<TValue> InvokeFormatAsync<TValue>(object?[]? args)
        {
            var batch = (IReadOnlyList<DeviceTimeFormatRequest>)args![0]!;
            BatchSizes.Add(batch.Count);
            var gate = _gate;
            _gate = null;
            if (gate is not null)
                await gate.Task;

            var result = new DeviceTimeFormatResult(
                "America/New_York",
                "Eastern Standard Time",
                [.. batch.Select(item => new DeviceTimeFormatItem(
                    item.Key, "T", "D", "F", "EST",
                    item.Detail ? "Eastern Standard Time" : "",
                    item.Detail ? "UTC-05:00" : ""))]);
            return (TValue)(object)result;
        }

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier, CancellationToken cancellationToken, object?[]? args) =>
            InvokeAsync<TValue>(identifier, args);

        public ValueTask DisposeAsync() => default;
    }
}
