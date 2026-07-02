using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MqttProbe.Models.Chart;
using MqttProbe.Models.Configuration;
using MqttProbe.Models.Mqtt;
using MqttProbe.Services.Configuration;
using MqttProbe.Services.Mqtt;
using MqttProbe.Services.Platform;
using MqttProbe.Shared.Tests.TestHelpers;
using MudBlazor;

namespace MqttProbe.Shared.Tests.Components.Browser;

[TestFixture]
public class PayloadBrowserTests : BunitTestContext
{
    private IMessageStoreManager _mockMsgStore = null!;
    private ISettingsStore _mockSettingsStore = null!;

    [SetUp]
    public void SetupMocks()
    {
        _mockMsgStore = Substitute.For<IMessageStoreManager>();
        _mockMsgStore.GetMessagesForSelectedTopic()
            .Returns(Task.FromResult<IEnumerable<MqttMessage>>(Array.Empty<MqttMessage>()));
        _mockMsgStore.GetSelectedTopicVersion().Returns(1L);
        _mockMsgStore.GetRecentMessagesAsync(Arg.Any<string>(), Arg.Any<int>())
            .Returns(Task.FromResult<IReadOnlyList<MqttMessage>>(Array.Empty<MqttMessage>()));
        var mockStore = new MessageStore { Topic = "sensor", FullTopic = "sensor" };
        _mockMsgStore.SelectedMessageStore.Returns(mockStore);
        Services.AddSingleton(_mockMsgStore);

        _mockSettingsStore = Substitute.For<ISettingsStore>();
        _mockSettingsStore.Config.Returns(new AppConfiguration());
        Services.AddSingleton(_mockSettingsStore);

        Services.AddSingleton(Substitute.For<IDialogService>());
        Services.AddSingleton(Substitute.For<ISnackbar>());
    }


    [Test]
    public void MessageList_AtOrAboveCap_ShowsTruncationWarning()
    {
        var messages = Enumerable.Range(0, 600)
            .Select(i => new MqttMessage { Topic = "sensor/temp", Payload = i.ToString() })
            .ToList();
        _mockMsgStore.GetRecentMessagesAsync(Arg.Any<string>(), Arg.Any<int>())
            .Returns(Task.FromResult<IReadOnlyList<MqttMessage>>(messages));
        EnsureMudProviders();

        var cut = Render<PayloadBrowser>();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Showing latest 500"));

        // Verify GetRecentMessagesAsync was called with the default display cap (500).
        _mockMsgStore.Received(1).GetRecentMessagesAsync(Arg.Any<string>(), 500);
    }

    [Test]
    public void MessageList_BelowCap_ShowsNoTruncationWarning()
    {
        var messages = Enumerable.Range(0, 10)
            .Select(i => new MqttMessage { Topic = "sensor/temp", Payload = i.ToString() })
            .ToList();
        _mockMsgStore.GetRecentMessagesAsync(Arg.Any<string>(), Arg.Any<int>())
            .Returns(Task.FromResult<IReadOnlyList<MqttMessage>>(messages));
        EnsureMudProviders();

        var cut = Render<PayloadBrowser>();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("sensor/temp"));
        cut.Markup.Should().NotContain("Showing latest");
    }

    [Test]
    public void MessageList_WithConfiguredMaxDisplayMessages_ShowsCustomTruncationWarning()
    {
        _mockSettingsStore.Config.Returns(new AppConfiguration
        {
            Performance = new PerformanceSettings { MaxDisplayMessages = 3 }
        });
        var messages = Enumerable.Range(0, 3)
            .Select(i => new MqttMessage { Topic = "sensor/temp", Payload = i.ToString() })
            .ToList();
        _mockMsgStore.GetRecentMessagesAsync(Arg.Any<string>(), Arg.Any<int>())
            .Returns(Task.FromResult<IReadOnlyList<MqttMessage>>(messages));
        EnsureMudProviders();

        var cut = Render<PayloadBrowser>();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Showing latest 3"));
        cut.FindAll(".payload-row").Should().HaveCount(3);

        // Verify GetRecentMessagesAsync was called with the custom display cap (3).
        _mockMsgStore.Received(1).GetRecentMessagesAsync(Arg.Any<string>(), 3);
    }

    [Test]
    public void DoesNotQuery_WhenVersionUnchanged()
    {
        _mockMsgStore.GetSelectedTopicVersion().Returns(42L);
        _mockMsgStore.GetRecentMessagesAsync(Arg.Any<string>(), Arg.Any<int>())
            .Returns(Task.FromResult<IReadOnlyList<MqttMessage>>(
                new List<MqttMessage>
                {
                    new() { Topic = "sensor/temp", Payload = "1" },
                    new() { Topic = "sensor/temp", Payload = "2" },
                    new() { Topic = "sensor/temp", Payload = "3" },
                    new() { Topic = "sensor/temp", Payload = "4" },
                    new() { Topic = "sensor/temp", Payload = "5" }
                }));
        EnsureMudProviders();

        var cut = Render<PayloadBrowser>();

        // First tick: _lastVersion (0) != 42 → queries.
        cut.WaitForAssertion(() =>
            _mockMsgStore.Received(1).GetRecentMessagesAsync(Arg.Any<string>(), Arg.Any<int>()));

        // Wait past a second timer tick (500ms interval) to prove no second query fires.
        Thread.Sleep(600);

        // Still exactly 1 call — version unchanged, second tick is a no-op.
        _mockMsgStore.Received(1).GetRecentMessagesAsync(Arg.Any<string>(), Arg.Any<int>());
    }

    [Test]
    public void QueriesAgain_WhenVersionChanges()
    {
        _mockMsgStore.GetSelectedTopicVersion().Returns(42L, 43L);
        _mockMsgStore.GetRecentMessagesAsync(Arg.Any<string>(), Arg.Any<int>())
            .Returns(Task.FromResult<IReadOnlyList<MqttMessage>>(
                new List<MqttMessage>
                {
                    new() { Topic = "sensor/temp", Payload = "1" },
                    new() { Topic = "sensor/temp", Payload = "2" },
                    new() { Topic = "sensor/temp", Payload = "3" },
                    new() { Topic = "sensor/temp", Payload = "4" },
                    new() { Topic = "sensor/temp", Payload = "5" }
                }));
        EnsureMudProviders();

        var cut = Render<PayloadBrowser>();

        // First tick: version 42 → queries.
        // Second tick: version 43 → queries again.
        cut.WaitForAssertion(() =>
            _mockMsgStore.Received(2).GetRecentMessagesAsync(Arg.Any<string>(), Arg.Any<int>()));

        // Verify limit matches default MaxDisplayMessages (500).
        _mockMsgStore.Received(2).GetRecentMessagesAsync(Arg.Any<string>(), 500);
    }

    [Test]
    public void NoSelectedTopic_DoesNotQueryAndShowsEmpty()
    {
        _mockMsgStore.SelectedMessageStore.Returns((MessageStore?)null);
        EnsureMudProviders();

        var cut = Render<PayloadBrowser>();

        cut.WaitForAssertion(() =>
            _mockMsgStore.DidNotReceive().GetRecentMessagesAsync(Arg.Any<string>(), Arg.Any<int>()));
        cut.FindAll(".payload-row").Should().BeEmpty();
    }

    [Test]
    public void FilterFunc_WithTopicSearchTerm_MatchesByTopicCaseInsensitive()
    {
        var cut = Render<PayloadBrowser>();
        cut.Instance._searchStringTerm = "SENSOR";

        cut.Instance.FilterFunc(new MqttMessage { Topic = "sensor/temp", Payload = "42" })
            .Should().BeTrue();
        cut.Instance.FilterFunc(new MqttMessage { Topic = "pressure/raw", Payload = "100" })
            .Should().BeFalse();
    }

    [Test]
    public void FilterFunc_WithPayloadSearchTerm_MatchesByPayloadCaseInsensitive()
    {
        var cut = Render<PayloadBrowser>();
        cut.Instance._searchStringTerm = "hello";

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

        cut.Instance._searchStringTerm = null;
        cut.Instance.FilterFunc(msg).Should().BeTrue();

        cut.Instance._searchStringTerm = "   ";
        cut.Instance.FilterFunc(msg).Should().BeTrue();

        cut.Instance._searchStringTerm = string.Empty;
        cut.Instance.FilterFunc(msg).Should().BeTrue();
    }


    [Test]
    public void IsJson_WithValidJsonObjectAndArray_ReturnsTrue()
    {
        var cut = Render<PayloadBrowser>();

        PayloadBrowser.IsJson("""{"key": "value", "num": 42}""").Should().BeTrue();
        PayloadBrowser.IsJson("[1, 2, 3]").Should().BeTrue();
        PayloadBrowser.IsJson("\"a string\"").Should().BeFalse();
    }

    [Test]
    public void IsJson_WithPlainTextAndMalformed_ReturnsFalse()
    {
        var cut = Render<PayloadBrowser>();

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

        cut.FindAll(".json-preview").Should().HaveCount(1);

        await cut.InvokeAsync(() => cut.Find(".json-expand-all-btn").Click());

        cut.FindAll(".json-preview").Should().BeEmpty();
        cut.FindAll(".json-key").Select(e => e.TextContent).Should().Contain("\"c\"");
    }

    [Test]
    public async Task CollapseAllButton_CollapsesAllNodesInTree()
    {
        var cut = Render<PayloadBrowser>();
        var msg = new MqttMessage { Topic = "test/topic", Payload = """{"a":{"b":{"c":"deep"}}}""" };

        await cut.InvokeAsync(() => cut.Instance.MessageChanged(msg));

        await cut.InvokeAsync(() => cut.Find(".json-collapse-all-btn").Click());

        cut.FindAll(".json-key").Should().BeEmpty();
        cut.FindAll(".json-preview").Should().HaveCount(1);
    }



    [Test]
    public async Task DisposeAsync_SetsDisposedFlagAndDoesNotThrow()
    {
        var cut = Render<PayloadBrowser>();

        await cut.Instance.DisposeAsync();

        cut.Instance._disposed.Should().BeTrue();
    }


    [Test]
    public void GetPayloadType_NullOrWhitespace_ReturnsEmpty()
    {
        var cut = Render<PayloadBrowser>();

        PayloadBrowser.GetPayloadType(null).Should().Be(PayloadBrowser.PayloadType.Empty);
        PayloadBrowser.GetPayloadType("").Should().Be(PayloadBrowser.PayloadType.Empty);
        PayloadBrowser.GetPayloadType("   ").Should().Be(PayloadBrowser.PayloadType.Empty);
    }

    [Test]
    public void GetPayloadType_ValidJson_ReturnsJson()
    {
        var cut = Render<PayloadBrowser>();

        PayloadBrowser.GetPayloadType("""{"key":"value"}""").Should().Be(PayloadBrowser.PayloadType.Json);
        PayloadBrowser.GetPayloadType("[1,2,3]").Should().Be(PayloadBrowser.PayloadType.Json);
    }

    [Test]
    public void GetPayloadType_XmlPayload_ReturnsXml()
    {
        var cut = Render<PayloadBrowser>();

        PayloadBrowser.GetPayloadType("<root><child/></root>").Should().Be(PayloadBrowser.PayloadType.Xml);
    }

    [Test]
    public void GetPayloadType_BooleanPayload_ReturnsBoolean()
    {
        var cut = Render<PayloadBrowser>();

        PayloadBrowser.GetPayloadType("true").Should().Be(PayloadBrowser.PayloadType.Boolean);
        PayloadBrowser.GetPayloadType("False").Should().Be(PayloadBrowser.PayloadType.Boolean);
        PayloadBrowser.GetPayloadType("TRUE").Should().Be(PayloadBrowser.PayloadType.Boolean);
    }

    [Test]
    public void GetPayloadType_NumericPayload_ReturnsNumber()
    {
        var cut = Render<PayloadBrowser>();

        PayloadBrowser.GetPayloadType("42").Should().Be(PayloadBrowser.PayloadType.Number);
        PayloadBrowser.GetPayloadType("3.14").Should().Be(PayloadBrowser.PayloadType.Number);
        PayloadBrowser.GetPayloadType("-5.0").Should().Be(PayloadBrowser.PayloadType.Number);
        PayloadBrowser.GetPayloadType("1e6").Should().Be(PayloadBrowser.PayloadType.Number);
    }

    [Test]
    public void GetPayloadType_PlainString_ReturnsText()
    {
        var cut = Render<PayloadBrowser>();

        PayloadBrowser.GetPayloadType("hello world").Should().Be(PayloadBrowser.PayloadType.Text);
        PayloadBrowser.GetPayloadType("temperature:42").Should().Be(PayloadBrowser.PayloadType.Text);
        PayloadBrowser.GetPayloadType("OK").Should().Be(PayloadBrowser.PayloadType.Text);
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
            .Returns(Task.FromResult<IDialogReference>(mockDialogRef));

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
            .Returns(Task.FromResult<IDialogReference>(mockDialogRef));

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

        await cut.InvokeAsync(() => cut.Find("button[title='Copy payload']").Click());

        await mockClipboard.Received(1).WriteTextAsync("42");
    }
}
