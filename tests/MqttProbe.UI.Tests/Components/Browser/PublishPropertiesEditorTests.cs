using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Services.Mqtt;
using MqttProbe.UI.Tests.TestHelpers;
using MqttProbe.UI.Utilities;
using MudBlazor;
using MudBlazor.Extensions;
using MudBlazor.Services;

namespace MqttProbe.UI.Tests.Components.Browser;

[TestFixture]
public class PublishPropertiesEditorTests : BunitTestContext
{
    private ISnackbar _mockSnackbar = null!;

    [SetUp]
    public void Setup()
    {
        _mockSnackbar = Substitute.For<ISnackbar>();
        Services.AddSingleton(_mockSnackbar);
        EnsureMudProviders();
    }

    private IRenderedComponent<PublishPropertiesEditor> RenderEditor(PublishPropertiesDraft draft) =>
        Render<PublishPropertiesEditor>(p => p.Add(x => x.Draft, draft));

    [Test]
    public void Renders_Region_And_Count_Chip()
    {
        var draft = new PublishPropertiesDraft { ContentType = "text/plain" };
        draft.UserProperties.Add(new MqttMessageUserProperty("a", "1"));

        var cut = RenderEditor(draft);

        cut.Find("[role='region'][aria-label='MQTT 5 properties']").Should().NotBeNull();
        cut.Find(".count-chip").TextContent.Trim().Should().Be("2");
        cut.Instance.Expanded.Should().BeTrue("a draft with values opens the panel");
    }

    [Test]
    public async Task Add_AppendsPropertyAndClearsDraftFields()
    {
        var draft = new PublishPropertiesDraft();
        var cut = RenderEditor(draft);
        cut.Instance.NameDraft = "site";
        cut.Instance.ValueDraft = " lab ";

        await cut.InvokeAsync(() => cut.Instance.AddForTests());

        draft.UserProperties.Should().Equal(new MqttMessageUserProperty("site", " lab "));
        cut.Instance.NameDraft.Should().BeEmpty();
        cut.Instance.ValueDraft.Should().BeEmpty();
    }

    [Test]
    public async Task Add_AllowsDuplicateNamesAndKeepsOrder()
    {
        var draft = new PublishPropertiesDraft();
        var cut = RenderEditor(draft);

        foreach (var value in new[] { "1", "2", "3" })
        {
            cut.Instance.NameDraft = "trace";
            cut.Instance.ValueDraft = value;
            await cut.InvokeAsync(() => cut.Instance.AddForTests());
        }

        draft.UserProperties.Select(p => p.Value).Should().Equal("1", "2", "3");
        draft.UserProperties.Should().AllSatisfy(p => p.Name.Should().Be("trace"));
    }

    [Test]
    public async Task Add_AllowsEmptyValue()
    {
        var draft = new PublishPropertiesDraft();
        var cut = RenderEditor(draft);
        cut.Instance.NameDraft = "flag";

        await cut.InvokeAsync(() => cut.Instance.AddForTests());

        draft.UserProperties.Should().Equal(new MqttMessageUserProperty("flag", ""));
    }

    [TestCase("")]
    [TestCase("   ")]
    public async Task Add_RejectsBlankName(string name)
    {
        var draft = new PublishPropertiesDraft();
        var cut = RenderEditor(draft);
        cut.Instance.NameDraft = name;
        cut.Instance.ValueDraft = "v";

        await cut.InvokeAsync(() => cut.Instance.AddForTests());

        draft.UserProperties.Should().BeEmpty();
        _mockSnackbar.Received(1).Add(Arg.Is<string>(m => m!.Contains("blank")), Severity.Warning,
            Arg.Any<Action<SnackbarOptions>?>(), Arg.Any<string?>());
    }

    [Test]
    public async Task Add_StopsAtLimit()
    {
        var draft = new PublishPropertiesDraft();
        for (var i = 0; i < PublishPropertiesDraft.MaxUserProperties; i++)
            draft.UserProperties.Add(new MqttMessageUserProperty($"k{i}", "v"));
        var cut = RenderEditor(draft);
        cut.Instance.NameDraft = "one-too-many";

        await cut.InvokeAsync(() => cut.Instance.AddForTests());

        draft.UserProperties.Should().HaveCount(PublishPropertiesDraft.MaxUserProperties);
        _mockSnackbar.Received(1).Add(Arg.Is<string>(m => m!.Contains("limit")), Severity.Warning,
            Arg.Any<Action<SnackbarOptions>?>(), Arg.Any<string?>());
        cut.Find("button[title='Add user property']").HasAttribute("disabled").Should().BeTrue();
    }

    [Test]
    public async Task EnterInNameField_Adds()
    {
        var draft = new PublishPropertiesDraft();
        var cut = RenderEditor(draft);
        cut.Instance.NameDraft = "k";
        cut.Instance.ValueDraft = "v";

        var nameInput = cut.FindComponents<MudTextField<string>>().First(f => f.Instance.Label == "Name");
        await cut.InvokeAsync(() => nameInput.Instance.OnKeyDown.InvokeAsync(new KeyboardEventArgs { Key = "Enter" }));

        draft.UserProperties.Should().Equal(new MqttMessageUserProperty("k", "v"));
    }

    [Test]
    public async Task RemoveSelected_RemovesOnlySelectedRowsEvenWhenDuplicated()
    {
        var draft = new PublishPropertiesDraft();
        draft.UserProperties.Add(new MqttMessageUserProperty("trace", "1"));
        draft.UserProperties.Add(new MqttMessageUserProperty("trace", "1"));
        draft.UserProperties.Add(new MqttMessageUserProperty("site", "lab"));
        var cut = RenderEditor(draft);

        cut.Instance.SelectForTests(1);
        await cut.InvokeAsync(() => cut.Instance.RemoveSelectedForTests());

        draft.UserProperties.Should().Equal(new MqttMessageUserProperty("trace", "1"), new MqttMessageUserProperty("site", "lab"));
    }

    [Test]
    public async Task RemoveSelected_WithNothingSelected_ShowsInfo()
    {
        var draft = new PublishPropertiesDraft();
        draft.UserProperties.Add(new MqttMessageUserProperty("k", "v"));
        var cut = RenderEditor(draft);

        await cut.InvokeAsync(() => cut.Instance.RemoveSelectedForTests());

        draft.UserProperties.Should().HaveCount(1);
        _mockSnackbar.Received(1).Add(Arg.Any<string>(), Severity.Info, Arg.Any<Action<SnackbarOptions>?>(), Arg.Any<string?>());
    }

    [Test]
    public void Clear_ResetsDraft()
    {
        var draft = new PublishPropertiesDraft { ContentType = "x" };
        draft.UserProperties.Add(new MqttMessageUserProperty("k", "v"));
        var cut = RenderEditor(draft);

        cut.Find("button[title='Clear properties']").Click();

        draft.Any.Should().BeFalse();
    }

    [Test]
    public void Rows_RenderNameAndValueVerbatim()
    {
        var draft = new PublishPropertiesDraft();
        draft.UserProperties.Add(new MqttMessageUserProperty("k", "two  spaces"));
        var cut = RenderEditor(draft);

        var cells = cut.FindAll("td.publish-props-cell").Select(td => td.TextContent).ToList();
        cells.Should().Contain("k").And.Contain("two  spaces");
    }

    [Test]
    public void InvalidResponseTopic_ShowsFieldError()
    {
        var cut = RenderEditor(new PublishPropertiesDraft { ResponseTopic = "a/#" });

        cut.FindAll(".mud-input-error").Should().NotBeEmpty();
        cut.Markup.Should().Contain("without + or # wildcards");
    }

    [Test]
    public void InvalidHexCorrelation_ShowsFieldError()
    {
        var cut = RenderEditor(new PublishPropertiesDraft
        {
            CorrelationPresent = true,
            CorrelationText = "xyz",
            CorrelationEncoding = CorrelationDataEncoding.Hex
        });

        cut.FindAll(".mud-input-error").Should().NotBeEmpty();
        cut.Markup.Should().Contain("even number of hex digits");
    }

    [Test]
    public void CorrelationPresence_IsOffByDefault_AndDisablesTheField()
    {
        var cut = RenderEditor(new PublishPropertiesDraft());

        cut.FindComponent<MudSwitch<bool>>().Instance.GetState(x => x.Value).Should().BeFalse();
        CorrelationTextField(cut).Instance.Disabled.Should().BeTrue();
        cut.Markup.Should().Contain("No correlation data is sent");
    }

    [Test]
    public async Task CorrelationPresence_On_EnablesTheFieldAndCountsTheProperty()
    {
        var draft = new PublishPropertiesDraft();
        var cut = RenderEditor(draft);

        await cut.InvokeAsync(() => cut.FindComponent<MudSwitch<bool>>().Instance.ValueChanged.InvokeAsync(true));
        cut.Render();

        draft.CorrelationPresent.Should().BeTrue();
        CorrelationTextField(cut).Instance.Disabled.Should().BeFalse();
        cut.Find(".count-chip").TextContent.Trim().Should().Be("1");
    }

    [Test]
    public async Task CorrelationPresence_Off_KeepsTextButStopsCountingIt()
    {
        var draft = new PublishPropertiesDraft { CorrelationPresent = true, CorrelationText = "keep" };
        var cut = RenderEditor(draft);

        await cut.InvokeAsync(() => cut.FindComponent<MudSwitch<bool>>().Instance.ValueChanged.InvokeAsync(false));
        cut.Render();

        draft.CorrelationText.Should().Be("keep");
        draft.SetCount.Should().Be(0);
        CorrelationTextField(cut).Instance.Disabled.Should().BeTrue();
    }

    [Test]
    public void CorrelationPresence_PresentWithEmptyValue_ShowsZeroLengthHint()
    {
        var cut = RenderEditor(new PublishPropertiesDraft { CorrelationPresent = true });

        cut.Markup.Should().Contain("zero-length");
        cut.FindAll(".mud-input-error").Should().BeEmpty();
    }

    private static IRenderedComponent<MudTextField<string>> CorrelationTextField(IRenderedComponent<PublishPropertiesEditor> cut) =>
        cut.FindComponents<MudTextField<string>>().First(f => f.Instance.Label == "Correlation data");

    [Test]
    public void Clear_WorksWithImportedProperties()
    {
        var draft = new PublishPropertiesDraft { ContentType = "x" };
        draft.UserProperties.Add(new MqttMessageUserProperty("k", "v"));
        var cut = RenderEditor(draft);

        cut.Find("button[title='Clear properties']").Click();

        draft.Any.Should().BeFalse();
    }

    [Test]
    public void Clear_IsDisabledWhenNothingIsSet()
    {
        var cut = RenderEditor(new PublishPropertiesDraft());

        cut.Find("button[title='Clear properties']").HasAttribute("disabled").Should().BeTrue();
    }

    [Test]
    public void OverCapDraft_ShowsErrorAlertAndErrorChip()
    {
        var draft = new PublishPropertiesDraft();
        for (var i = 0; i <= PublishPropertiesDraft.MaxUserProperties; i++)
            draft.UserProperties.Add(new MqttMessageUserProperty($"k{i}", "v"));

        var cut = RenderEditor(draft);

        cut.Markup.Should().Contain("At most 50 user properties can be sent. Remove 1.");
        cut.Find(".count-chip").ClassList.Should().Contain("mud-chip-color-error");
        cut.Find("button[title='Add user property']").HasAttribute("disabled").Should().BeTrue();
    }


    private static IRenderedComponent<MudTextField<string>> ExpiryField(IRenderedComponent<PublishPropertiesEditor> cut) =>
        cut.FindComponents<MudTextField<string>>().First(f => f.Instance.Label == "Message expiry (seconds)");

    [Test]
    public void Expiry_Zero_StaysZeroAndShowsNoError()
    {
        var draft = new PublishPropertiesDraft();
        var cut = RenderEditor(draft);

        ExpiryField(cut).Find("input").Input("0");
        ExpiryField(cut).Find("input").Blur();

        draft.ExpiryText.Should().Be("0");
        draft.ExpirySeconds.Should().BeNull();
        ExpiryField(cut).FindAll(".mud-input-error").Should().BeEmpty();
        cut.Markup.Should().Contain("Empty or 0 sends no expiry");
    }

    [TestCase("abc")]
    [TestCase("-5")]
    public void Expiry_InvalidText_ShowsFieldError(string text)
    {
        var cut = RenderEditor(new PublishPropertiesDraft { ExpiryText = text });

        cut.FindAll(".mud-input-error").Should().NotBeEmpty();
        cut.Markup.Should().Contain(PublishPropertiesDraft.ExpiryErrorText);
    }

    [Test]
    public void ContentTypeWithNul_ShowsFieldError()
    {
        var cut = RenderEditor(new PublishPropertiesDraft { ContentType = "text/\0plain" });

        cut.FindAll(".mud-input-error").Should().NotBeEmpty();
        cut.Markup.Should().Contain("Content type cannot contain NUL");
    }

    [Test]
    public async Task Add_RejectsNulValueAndOverLengthName()
    {
        var draft = new PublishPropertiesDraft();
        var cut = RenderEditor(draft);

        cut.Instance.NameDraft = "k";
        cut.Instance.ValueDraft = "a\0b";
        await cut.InvokeAsync(() => cut.Instance.AddForTests());

        cut.Instance.NameDraft = new string('n', MqttStringRules.MaxUtf8Bytes + 1);
        cut.Instance.ValueDraft = "v";
        await cut.InvokeAsync(() => cut.Instance.AddForTests());

        draft.UserProperties.Should().BeEmpty();
        _mockSnackbar.Received(1).Add(Arg.Is<string>(m => m!.Contains("value cannot contain NUL")), Severity.Warning, Arg.Any<Action<SnackbarOptions>?>(), Arg.Any<string?>());
        _mockSnackbar.Received(1).Add(Arg.Is<string>(m => m!.Contains("name cannot contain NUL")), Severity.Warning, Arg.Any<Action<SnackbarOptions>?>(), Arg.Any<string?>());
    }

    [Test]
    public void OverLengthImportedValue_ShowsAlertAndErrorChip()
    {
        var draft = new PublishPropertiesDraft();
        draft.UserProperties.Add(new MqttMessageUserProperty("k", new string('v', MqttStringRules.MaxUtf8Bytes + 1)));

        var cut = RenderEditor(draft);

        cut.Markup.Should().Contain("User property 1 value cannot contain NUL or exceed 65,535 bytes of UTF-8. Remove it.");
        cut.Find(".count-chip").ClassList.Should().Contain("mud-chip-color-error");
    }

    [Test]
    public void Clear_IsEnabledWhenOnlyInvalidExpiryTextIsPresent()
    {
        var draft = new PublishPropertiesDraft { ExpiryText = "abc" };
        var cut = RenderEditor(draft);

        var clear = cut.Find("button[title='Clear properties']");
        clear.HasAttribute("disabled").Should().BeFalse("invalid text counts as something to clear");
        clear.Click();

        draft.ExpiryText.Should().BeNull();
        draft.ValidationError.Should().BeNull();
    }

    [Test]
    public void CollapsedPanel_StaysCollapsedAcrossRerendersOfTheSameDraft()
    {
        var draft = new PublishPropertiesDraft { ContentType = "text/plain" };
        var cut = RenderEditor(draft);
        cut.Instance.Expanded.Should().BeTrue("a draft with values opens the panel");

        cut.FindComponent<MudExpansionPanel>().Find(".mud-expand-panel-header").Click();
        cut.Instance.Expanded.Should().BeFalse();

        cut.Render(p => p.Add(x => x.Draft, draft));

        cut.Instance.Expanded.Should().BeFalse("a user collapse is kept while editing the same draft");
    }

    [Test]
    public void ImportedDraftWithProperties_OpensThePanel()
    {
        var cut = RenderEditor(new PublishPropertiesDraft());
        cut.Instance.Expanded.Should().BeFalse();

        cut.Render(p => p.Add(x => x.Draft, new PublishPropertiesDraft { ContentType = "text/plain" }));

        cut.Instance.Expanded.Should().BeTrue("a newly imported draft with values opens the panel");
    }

    [Test]
    public void Root_HasLabelledRegion_WithoutDeadTabStop()
    {
        var cut = RenderEditor(new PublishPropertiesDraft());

        var root = cut.Find(".publish-props");
        root.GetAttribute("role").Should().Be("region");
        root.GetAttribute("aria-label").Should().Be("MQTT 5 properties");
        root.HasAttribute("tabindex").Should().BeFalse("the Mud panel header is the keyboard entry point");

        cut.Find(".mud-expand-panel-header").GetAttribute("tabindex").Should().Be("0");
    }

    [TestCase("Enter")]
    [TestCase(" ")]
    public async Task CollapsedHeader_NativeActivation_TogglesWithoutAddingDraft(string key)
    {
        var draft = new PublishPropertiesDraft();
        var cut = RenderEditor(draft);
        cut.Instance.NameDraft = "pending";
        cut.Instance.ValueDraft = "value";

        cut.Instance.Expanded.Should().BeFalse();

        await cut.InvokeAsync(() => cut.Find(".mud-expand-panel-header").KeyDown(key));

        cut.WaitForAssertion(() =>
            cut.FindComponent<MudExpansionPanel>().Instance.Expanded.Should().BeTrue());
        cut.Instance.Expanded.Should().BeTrue();
        draft.UserProperties.Should().BeEmpty();
        cut.Instance.NameDraft.Should().Be("pending");

        await cut.InvokeAsync(() => cut.Find(".mud-expand-panel-header").KeyDown(key));

        cut.WaitForAssertion(() =>
            cut.FindComponent<MudExpansionPanel>().Instance.Expanded.Should().BeFalse());
        cut.Instance.Expanded.Should().BeFalse();
        draft.UserProperties.Should().BeEmpty();
        cut.Instance.NameDraft.Should().Be("pending");
    }

    [Test]
    public void HeaderArrowKeys_AreWiredToTheMudHeaderTargetClass()
    {
        var cut = RenderEditor(new PublishPropertiesDraft());

        var rootId = cut.Find(".publish-props").Id;
        var invocation = JSInterop.Invocations
            .Single(i => i.Identifier == "mudKeyInterceptor.connect"
                && i.Arguments.Count > 1 && Equals(i.Arguments[1], rootId));

        var options = invocation.Arguments[2].Should().BeOfType<KeyInterceptorOptions>().Subject;
        options.TargetClass.Should().Be("mud-expand-panel-header");
        options.Keys.Select(k => k.Key).Should().BeEquivalentTo(new[] { "ArrowRight", "ArrowLeft" });
        options.Keys.Should().OnlyContain(k => k.SubscribeDown && k.IgnoreDownRepeats
            && k.PreventDown == "none" && k.StopDown == "none");
        options.Keys.Should().NotContain(k => k.Key == "Enter" || k.Key == " ");
    }
}
