using Microsoft.Extensions.DependencyInjection;
using MqttProbe.UI.Tests.TestHelpers;
using MudBlazor;

namespace MqttProbe.UI.Tests.Components.Sparkplug;

[TestFixture]
public class WriteMetricDialogTests : BunitTestContext
{
    private const string SendSelector = "button[title='Send the command']";
    private IRenderedComponent<MudDialogProvider> _dialogProvider = null!;

    [SetUp]
    public void Setup()
    {
        EnsureMudProviders();
        _dialogProvider = Render<MudDialogProvider>();
    }

    [Test]
    public async Task Opens_WithTargetDetailsAndPrefilledValue()
    {
        await OpenDialog("int32", currentValue: "41", initialValue: "41");

        var markup = _dialogProvider.Markup;
        markup.Should().Contain("Setpoint");
        markup.Should().Contain("spBv1.0/g/NCMD/n");
        markup.Should().Contain("int32");
        markup.Should().Contain("Current value: 41");
        _dialogProvider.Find("input").GetAttribute("value").Should().Be("41");
        SendIsDisabled().Should().BeFalse();
    }

    [Test]
    public async Task FloatingPoint_PrefillsTheUnroundedValue()
    {
        await OpenDialog("double", currentValue: "1.2346", initialValue: "1.2345678901234567");

        _dialogProvider.Find("input").GetAttribute("value").Should().Be("1.2345678901234567");
        _dialogProvider.Markup.Should().Contain("Current value: 1.2346");
    }

    [TestCase("int8", "128")]
    [TestCase("uint8", "-1")]
    [TestCase("int32", "abc")]
    [TestCase("double", "1,5")]
    [TestCase("datetime", "2026-11-10")]
    [TestCase("uuid", "nope")]
    public async Task InvalidInput_ShowsErrorAndDisablesSend(string dataType, string input)
    {
        var dialog = await OpenDialog(dataType, currentValue: "0", initialValue: "0");

        _dialogProvider.Find("input").Input(input);

        SendIsDisabled().Should().BeTrue();
        _dialogProvider.FindAll(".mud-input-error").Should().NotBeEmpty();
        dialog.Result.IsCompleted.Should().BeFalse();
    }

    [Test]
    public async Task ValidInput_SendReturnsTheEnteredText()
    {
        var dialog = await OpenDialog("uint64", currentValue: "0", initialValue: "0");

        _dialogProvider.Find("input").Input("18446744073709551615");
        _dialogProvider.Find(SendSelector).Click();

        var result = await dialog.Result;
        result!.Canceled.Should().BeFalse();
        result.Data.Should().Be("18446744073709551615");
    }

    [Test]
    public async Task StringValue_IsReturnedVerbatim()
    {
        const string json = " {\"name\":\"Zoë ☃\"}\n ";
        var dialog = await OpenDialog("string", currentValue: "old", initialValue: "old");

        _dialogProvider.Find("textarea").Input(json);
        _dialogProvider.Find(SendSelector).Click();

        (await dialog.Result)!.Data.Should().Be(json);
    }

    [Test]
    public async Task StringValue_FormatPrettyPrintsJsonAndKeepsUnicode()
    {
        var dialog = await OpenDialog("text", currentValue: "", initialValue: "{\"name\":\"Zoë ☃\",\"n\":[1,2]}");

        _dialogProvider.Find("button[title='Pretty-print the value when it is valid JSON']").Click();
        _dialogProvider.Find(SendSelector).Click();

        var sent = (string)(await dialog.Result)!.Data!;
        sent.Should().Contain("\n");
        sent.Should().Contain("\"name\": \"Zoë ☃\"");
    }

    [Test]
    public async Task StringValue_FormatLeavesNonJsonUntouched()
    {
        var dialog = await OpenDialog("string", currentValue: "", initialValue: "plain text");

        _dialogProvider.Find("button[title='Pretty-print the value when it is valid JSON']").Click();

        _dialogProvider.Markup.Should().Contain("Not valid JSON");
        _dialogProvider.Find(SendSelector).Click();
        (await dialog.Result)!.Data.Should().Be("plain text");
    }

    [Test]
    public async Task EmptyString_CanBeSent()
    {
        var dialog = await OpenDialog("string", currentValue: "old", initialValue: "");

        SendIsDisabled().Should().BeFalse();
        _dialogProvider.Find(SendSelector).Click();

        (await dialog.Result)!.Data.Should().Be("");
    }

    [Test]
    public async Task Bool_OffersAChoiceInsteadOfATextInput()
    {
        var dialog = await OpenDialog("bool", currentValue: "false", initialValue: "false");

        _dialogProvider.FindAll("input[type='text']").Should().BeEmpty();
        _dialogProvider.FindAll("textarea").Should().BeEmpty();
        var toggle = _dialogProvider.FindComponent<MudToggleGroup<string>>();
        await _dialogProvider.InvokeAsync(() => toggle.Instance.ValueChanged.InvokeAsync("true"));
        _dialogProvider.Find(SendSelector).Click();

        (await dialog.Result)!.Data.Should().Be("true");
    }

    [Test]
    public async Task Cancel_ClosesWithoutAValue()
    {
        var dialog = await OpenDialog("int32", currentValue: "1", initialValue: "1");

        _dialogProvider.Find("button[title='Cancel without sending']").Click();

        (await dialog.Result)!.Canceled.Should().BeTrue();
    }

    private bool SendIsDisabled() => _dialogProvider.Find(SendSelector).HasAttribute("disabled");

    private async Task<IDialogReference> OpenDialog(string dataType, string currentValue, string initialValue)
    {
        var dialogService = Services.GetRequiredService<IDialogService>();
        IDialogReference? dialogRef = null;
        var parameters = new DialogParameters<WriteMetricDialog>
        {
            { d => d.Topic, "spBv1.0/g/NCMD/n" },
            { d => d.MetricName, "Setpoint" },
            { d => d.DataType, dataType },
            { d => d.CurrentValue, currentValue },
            { d => d.InitialValue, initialValue }
        };

        await _dialogProvider.InvokeAsync(async () =>
        {
            dialogRef = await dialogService.ShowAsync<WriteMetricDialog>("Write metric", parameters,
                new DialogOptions { MaxWidth = MaxWidth.Small, FullWidth = true });
        });

        return dialogRef!;
    }
}
