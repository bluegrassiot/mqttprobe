using AngleSharp.Dom;
using Microsoft.Extensions.DependencyInjection;
using MqttProbe.Core.Models.Configuration;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Services.Configuration;
using MqttProbe.UI.Tests.TestHelpers;

namespace MqttProbe.UI.Tests.Components.Browser;

[TestFixture]
public sealed class TopicTreeMultiSelectTests : BunitTestContext
{
    private TopicBrowserTestFixture _fixture = null!;
    private IUiSettings _uiSettings = null!;
    private readonly UiPreferences _preferences = new();

    [SetUp]
    public void Setup()
    {
        _fixture = new TopicBrowserTestFixture();
        _fixture.AddTopics(("sensors/temp", 1), ("sensors/humidity", 2));
        Services.AddSingleton<ITopicSelectionStore>(_fixture.Adapter);
        _uiSettings = SettingsSubstituteRegistration.CreateUiSettings(_preferences);
        Services.AddUiSettings(_uiSettings);
    }

    [Test]
    public void MultiSelectIsOnByDefault_SoRowsCarryCheckboxes()
    {
        var cut = RenderExpanded();

        cut.FindAll(".topic-select-checkbox").Should().NotBeEmpty();
        cut.Find(".topic-select-all input").Should().NotBeNull();
        cut.Find(".topic-tree").GetAttribute("aria-multiselectable").Should().Be("true");
    }

    [Test]
    public void Disabled_HidesCheckboxesAndSelectAll_AndMarksTheTreeSingleSelectable()
    {
        _preferences.TopicTreeMultiSelect = false;

        var cut = RenderExpanded();

        cut.FindAll(".topic-select-checkbox").Should().BeEmpty();
        cut.FindAll(".topic-select-all").Should().BeEmpty();
        cut.Find(".topic-tree").GetAttribute("aria-multiselectable").Should().Be("false");
    }

    [Test]
    public void Disabled_RowClickSelectsThatTopicAlone()
    {
        _preferences.TopicTreeMultiSelect = false;
        var cut = RenderExpanded();

        Row(cut, "sensors/temp").Click();

        cut.WaitForAssertion(() =>
        {
            var state = _fixture.Adapter.Store.GetSelectedTopicState();
            state.Mode.Should().Be(TopicSelectionMode.SingleTopic);
            state.Key.Should().Be("sensors/temp");
        });
        Row(cut, "sensors/temp").ClassList.Should().Contain("topic-tree-row--selected");
    }

    [TestCase("Enter")]
    [TestCase(" ")]
    public void Disabled_KeyboardActivationSelectsThatTopicAlone(string key)
    {
        _preferences.TopicTreeMultiSelect = false;
        var cut = RenderExpanded();

        Row(cut, "sensors/temp").KeyDown(key);

        cut.WaitForAssertion(() =>
        {
            var state = _fixture.Adapter.Store.GetSelectedTopicState();
            state.Mode.Should().Be(TopicSelectionMode.SingleTopic);
            state.Key.Should().Be("sensors/temp");
        });
    }

    [Test]
    public void Disabled_ClickingASecondTopic_ReplacesTheSelectionInsteadOfAdding()
    {
        _preferences.TopicTreeMultiSelect = false;
        var cut = RenderExpanded();

        Row(cut, "sensors/temp").Click();
        Row(cut, "sensors/humidity").Click();

        cut.WaitForAssertion(() =>
        {
            var state = _fixture.Adapter.Store.GetSelectedTopicState();
            state.Mode.Should().Be(TopicSelectionMode.SingleTopic);
            state.Key.Should().Be("sensors/humidity");
        });
        Row(cut, "sensors/temp").ClassList.Should().NotContain("topic-tree-row--selected");
    }

    [Test]
    public void TurningMultiSelectOff_ClearsTheBulkSelectionAndHidesTheControls()
    {
        var cut = RenderExpanded();
        ToggleCheckbox(cut, "sensors/temp");
        ToggleCheckbox(cut, "sensors/humidity");
        cut.WaitForAssertion(() =>
            _fixture.Adapter.Store.GetSelectedTopicState().Branches.Should().HaveCount(2));

        _preferences.TopicTreeMultiSelect = false;
        _uiSettings.UiPreferencesChanged += Raise.Event<Action>();

        cut.WaitForAssertion(() =>
        {
            _fixture.Adapter.Store.GetSelectedTopicState().Key.Should().BeNull();
            cut.FindAll(".topic-select-checkbox").Should().BeEmpty();
            cut.FindAll(".topic-select-all").Should().BeEmpty();
        });
    }

    [Test]
    public void TurningMultiSelectBackOn_RestoresCheckboxesAndBranchSelection()
    {
        _preferences.TopicTreeMultiSelect = false;
        var cut = RenderExpanded();

        _preferences.TopicTreeMultiSelect = true;
        _uiSettings.UiPreferencesChanged += Raise.Event<Action>();

        cut.WaitForAssertion(() => cut.FindAll(".topic-select-checkbox").Should().NotBeEmpty());
        Row(cut, "sensors/temp").Click();
        cut.WaitForAssertion(() =>
            _fixture.Adapter.Store.GetSelectedTopicState().Mode.Should().Be(TopicSelectionMode.Branches));
    }

    private IRenderedComponent<TopicBrowser> RenderExpanded()
    {
        var cut = Render<TopicBrowser>();
        EnsureMudProviders();
        cut.FindAll(".mud-toolbar button")[0].Click();
        return cut;
    }

    private static IElement Row(IRenderedComponent<TopicBrowser> cut, string path) =>
        cut.FindAll(".topic-tree-row")
            .First(row => row.GetAttribute("data-topic-path") == path);

    private static void ToggleCheckbox(IRenderedComponent<TopicBrowser> cut, string path) =>
        Row(cut, path).QuerySelector(".topic-select-checkbox-wrap")!.Click();
}
