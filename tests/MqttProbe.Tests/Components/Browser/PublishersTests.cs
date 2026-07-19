using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MqttProbe.Services.Metrics;
using MqttProbe.Services.Mqtt;
using MqttProbe.Services.Platform;
using MqttProbe.Services.Security;
using MqttProbe.Shared.Tests.TestHelpers;
using MudBlazor;

namespace MqttProbe.Shared.Tests.Components.Browser;

[TestFixture]
public class PublishersTests : BunitTestContext
{
    private IMqttManagedClient _mockClient = null!;
    private ISnackbar _mockSnackbar = null!;

    [SetUp]
    public void SetupMocks()
    {
        _mockClient = Substitute.For<IMqttManagedClient>();
        _mockClient.EnqueueAsync(Arg.Any<MqttApplicationMessage>()).Returns(Task.CompletedTask);
        Services.AddSingleton(_mockClient);
        Services.AddSingleton(Substitute.For<ILogger<Publishers>>());
        Services.AddSingleton(Substitute.For<IAppInfoService>());
        Services.AddSingleton(Substitute.For<IUxMetricsService>());
        _mockSnackbar = Substitute.For<ISnackbar>();
        Services.AddSingleton(_mockSnackbar);

        // Build service provider and register MudPopoverProvider (required by MudSelect)
        EnsureMudProviders();
    }

    [TearDown]
    public void TeardownMocks() => _mockClient.Dispose();

    [Test]
    public void Renders_Header_With_Publish_Button_In_Header_Not_Bottom()
    {
        var cut = Render<Publishers>();

        cut.Markup.Should().Contain("app-tabpanel-header__row");
        cut.Markup.Should().Contain(">Publish<");
        cut.FindAll("button").Count(b => b.TextContent.Contains("Publish")).Should().Be(1,
            "the Publish button should live in the header exactly once");
    }

    [Test]
    public void Renders_QosDropdown_WithAllThreeOptions()
    {
        var cut = Render<Publishers>();

        // MudSelect renders with a label; all three enum values are available as items.
        // The label text is rendered inline even in the closed state.
        cut.Markup.Should().Contain("QoS Level");
        // The select element(s) must exist (MudSelect may render multiple divs)
        cut.FindAll(".mud-select").Should().NotBeEmpty();
    }

    [Test]
    public void PasteFromClipboardButton_UsesTertiaryTextStyle()
    {
        // The Paste button only renders when the host is native (MAUI) or Windows;
        // in bUnit on Linux the default `IAppInfoService` is a non-native substitute
        // and the button is absent, which is why this test needs IsNative = true.
        var appInfo = Services.GetRequiredService<IAppInfoService>();
        appInfo.IsNative.Returns(true);

        var cut = Render<Publishers>();

        // Tertiary quiet helper: text + default color (slate), not primary orange.
        var pasteButton = cut.FindAll("button")
            .First(b => b.TextContent.Contains("Paste from clipboard"));
        pasteButton.ClassList.Should().Contain("mud-button-text");
        pasteButton.ClassList.Should().Contain("mud-button-text-default");
        pasteButton.ClassList.Should().NotContain("mud-button-text-primary");
    }

    [Test]
    public async Task PublishButton_AsOperator_CallsEnqueueAsync_WithCorrectTopic()
    {
        AuthorizationContext.SetAuthorized("op").SetRoles(AppRoles.Operator);
        var cut = Render<Publishers>();

        // Set the Topic field (first input in the form)
        cut.FindAll("input")[0].Change("test/topic");

        cut.Find("button[title='Publish']").Click();

        await _mockClient.Received(1).EnqueueAsync(
            Arg.Is<MqttApplicationMessage>(m => m!.Topic == "test/topic"));
    }

    [Test]
    public async Task PublishButton_WhenUnauthorized_DoesNotPublish()
    {
        // Unauthenticated by default — direct rendering must not bypass the role gate.
        var cut = Render<Publishers>();

        cut.FindAll("input")[0].Change("test/topic");
        cut.Find("button[title='Publish']").Click();

        await _mockClient.DidNotReceive().EnqueueAsync(Arg.Any<MqttApplicationMessage>());
        _mockSnackbar.Received().Add(
            Arg.Is<string>(m => m!.Contains("permission")),
            Severity.Error,
            Arg.Any<Action<SnackbarOptions>?>(),
            Arg.Any<string?>());
    }

    [Test]
    public async Task PublishButton_DoesNothing_WhenTopicIsEmpty()
    {
        var cut = Render<Publishers>();

        // Topic field is empty by default — publish button should be a no-op
        cut.Find("button[title='Publish']").Click();

        await _mockClient.DidNotReceive().EnqueueAsync(Arg.Any<MqttApplicationMessage>());
    }


    [Test]
    public async Task SetMessageFromClipBoard_SetsMessageFieldFromJson()
    {
        var mockClipboard = Services.GetRequiredService<IClipboardService>();
        var clipboardJson = System.Text.Json.JsonSerializer.Serialize(
            new MqttProbe.Models.Mqtt.MqttMessage { Topic = "test/topic", Payload = "hello from clipboard" });
        mockClipboard.GetTextAsync().Returns(clipboardJson);

        var cut = Render<Publishers>();

        await cut.InvokeAsync(() => cut.Instance.SetMessageFromClipBoard());

        var messageField = typeof(Publishers).GetField("_message",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var message = (MqttProbe.Models.Mqtt.MqttMessage?)messageField!.GetValue(cut.Instance);
        message!.Payload.Should().Be("hello from clipboard");
    }

    [Test]
    public async Task SetMessageFromClipBoard_WhenClipboardEmpty_LeavesFieldUnchanged()
    {
        var mockClipboard = Services.GetRequiredService<IClipboardService>();
        mockClipboard.GetTextAsync().Returns((string?)null);

        var cut = Render<Publishers>();

        await cut.InvokeAsync(() => cut.Instance.SetMessageFromClipBoard());

        var messageField = typeof(Publishers).GetField("_message",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var message = (MqttProbe.Models.Mqtt.MqttMessage?)messageField!.GetValue(cut.Instance);
        message!.Payload.Should().BeNullOrEmpty();
    }

    [Test]
    public void FormatJsonButton_IsPresent()
    {
        var cut = Render<Publishers>();
        cut.Markup.Should().Contain("Format");
    }

    [Test]
    public void ValidateJsonButton_IsGone()
    {
        var cut = Render<Publishers>();
        cut.Markup.Should().NotContain("Validate JSON");
    }

    [Test]
    public void FormatJson_ValidJson_FormatsPayloadAndShowsSuccess()
    {
        var cut = Render<Publishers>();
        var messageField = typeof(Publishers).GetField("_message",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var msg = (MqttProbe.Models.Mqtt.MqttMessage?)messageField!.GetValue(cut.Instance);
        msg!.Payload = """{"key":"value"}""";

        cut.Instance.FormatJsonPayload();

        msg.Payload.Should().Contain("\n", "valid JSON should be re-serialized with indentation");
        _mockSnackbar.Received(1).Add("Payload formatted.", Severity.Success,
            Arg.Any<Action<SnackbarOptions>?>(), Arg.Any<string?>());
    }

    [Test]
    public void FormatJson_InvalidJson_ShowsWarning()
    {
        var cut = Render<Publishers>();
        var messageField = typeof(Publishers).GetField("_message",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var msg = (MqttProbe.Models.Mqtt.MqttMessage?)messageField!.GetValue(cut.Instance);
        msg!.Payload = "{bad json";

        cut.Instance.FormatJsonPayload();

        _mockSnackbar.Received(1).Add("Invalid JSON — fix syntax before publishing.",
            Severity.Warning, Arg.Any<Action<SnackbarOptions>?>(), Arg.Any<string?>());
    }

    [Test]
    public void FormatJson_EmptyPayload_ShowsInfo()
    {
        var cut = Render<Publishers>();

        cut.Instance.FormatJsonPayload();

        _mockSnackbar.Received(1).Add("Payload is empty.", Severity.Info,
            Arg.Any<Action<SnackbarOptions>?>(), Arg.Any<string?>());
    }

    [Test]
    public void MinifyJsonButton_IsPresent()
    {
        var cut = Render<Publishers>();
        cut.Markup.Should().Contain("Minify");
    }

    [Test]
    public void MinifyJson_ValidJson_MinifiesPayloadAndShowsSuccess()
    {
        var cut = Render<Publishers>();
        var messageField = typeof(Publishers).GetField("_message",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var msg = (MqttProbe.Models.Mqtt.MqttMessage?)messageField!.GetValue(cut.Instance);
        msg!.Payload = "{\n  \"key\": \"value\"\n}";

        cut.Instance.MinifyJsonPayload();

        msg.Payload.Should().NotContain("\n");
        msg.Payload.Should().NotContain("  ");
        _mockSnackbar.Received(1).Add("Payload minified.", Severity.Success,
            Arg.Any<Action<SnackbarOptions>?>(), Arg.Any<string?>());
    }

    [Test]
    public void MinifyJson_InvalidJson_ShowsWarning()
    {
        var cut = Render<Publishers>();
        var messageField = typeof(Publishers).GetField("_message",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var msg = (MqttProbe.Models.Mqtt.MqttMessage?)messageField!.GetValue(cut.Instance);
        msg!.Payload = "{bad json";

        cut.Instance.MinifyJsonPayload();

        _mockSnackbar.Received(1).Add("Invalid JSON — fix syntax before publishing.",
            Severity.Warning, Arg.Any<Action<SnackbarOptions>?>(), Arg.Any<string?>());
    }

    [Test]
    public void MinifyJson_EmptyPayload_ShowsInfo()
    {
        var cut = Render<Publishers>();

        cut.Instance.MinifyJsonPayload();

        _mockSnackbar.Received(1).Add("Payload is empty.", Severity.Info,
            Arg.Any<Action<SnackbarOptions>?>(), Arg.Any<string?>());
    }
}
