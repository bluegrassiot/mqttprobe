using AngleSharp.Dom;
using Microsoft.Extensions.DependencyInjection;
using MQTTnet.Protocol;
using MqttProbe.Core.Models.Configuration;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Services.Configuration;
using MqttProbe.Core.Services.Mqtt;
using MqttProbe.UI.Tests.TestHelpers;
using MudBlazor;

namespace MqttProbe.UI.Tests.Components.Browser;

[TestFixture]
public sealed class TopicBulkSelectionTests : BunitTestContext
{
    private TopicBrowserTestFixture _fixture = null!;
    private IUiSettings _uiSettings = null!;
    private readonly UiPreferences _preferences = new();

    [SetUp]
    public void Setup()
    {
        _fixture = new TopicBrowserTestFixture();
        Services.AddSingleton<ITopicSelectionStore>(_fixture.Adapter);
        _uiSettings = SettingsSubstituteRegistration.CreateUiSettings(_preferences);
        Services.AddUiSettings(_uiSettings);
        Services.AddSingleton(Substitute.For<IDialogService>());
        Services.AddSingleton(Substitute.For<ISnackbar>());
        Services.AddSingleton(Substitute.For<ITopicExcludeService>());
        Services.AddSingleton(Substitute.For<IMessageStoreManager>());
    }

    [Test]
    public void Checkboxes_AppearOnlyOnConcreteTopicRows_InSelectionMode()
    {
        _fixture.AddTopics(("sensors/temp", 1), ("sensors/temp/room1", 2), ("sensors/humidity", 2));
        var cut = RenderExpanded();

        cut.FindAll(".topic-select-checkbox").Should().BeEmpty("the mode is entered deliberately");

        cut.Find(".topic-select-toggle").Click();

        cut.WaitForAssertion(() => cut.FindAll(".topic-select-checkbox").Should().HaveCount(3));
        Row(cut, "sensors").QuerySelector(".topic-select-checkbox")
            .Should().BeNull("a branch row is not a concrete topic");
        Row(cut, "sensors/temp").QuerySelector(".topic-select-checkbox").Should().NotBeNull();
        Row(cut, "sensors/temp/room1").QuerySelector(".topic-select-checkbox").Should().NotBeNull();
        Row(cut, "sensors/humidity").QuerySelector(".topic-select-checkbox").Should().NotBeNull();
    }

    [Test]
    public void SelectingTwoTopics_FiltersTheirUnion_NewestFirst_WithoutDuplicates()
    {
        _fixture.Adapter.Add("sensors/temp", Message("temp-old", 10));
        _fixture.Adapter.Add("sensors/temp/room1", Message("room-1", 20));
        _fixture.Adapter.Add("sensors/temp", Message("temp-new", 30));
        _fixture.Adapter.Add("unrelated/topic", Message("unrelated", 40));
        var cut = RenderExpanded();

        cut.Find(".topic-select-toggle").Click();
        cut.WaitForAssertion(() => cut.FindAll(".topic-select-checkbox").Should().HaveCount(3));

        Check(cut, "sensors/temp");
        Check(cut, "sensors/temp/room1");

        var state = _fixture.Adapter.Store.GetSelectedTopicState();
        state.Key.Should().StartWith("multi:");
        state.Label.Should().Be("2 topics selected");
        state.MessageCount.Should().Be(3);
        var messages = _fixture.Adapter.Store.GetSelectedMessages(state.Token, 10)!;
        messages.Messages.Select(message => message.Payload)
            .Should().Equal("temp-new", "room-1", "temp-old");
    }

    [Test]
    public void RowClick_InSelectionMode_ChecksTheConcreteTopic()
    {
        _fixture.AddTopics(("sensors/temp", 1), ("sensors/humidity", 2));
        var cut = RenderExpanded();
        cut.Find(".topic-select-toggle").Click();

        Row(cut, "sensors/temp").Click();

        cut.WaitForAssertion(() =>
        {
            var state = _fixture.Adapter.Store.GetSelectedTopicState();
            state.Key.Should().StartWith("multi:");
            state.Paths.Should().Equal("sensors/temp");
        });
    }

    [TestCase("Enter")]
    [TestCase(" ")]
    public void KeyboardActivation_InSelectionMode_ChecksTheRowInsteadOfSelectingTheTopic(string key)
    {
        _fixture.AddTopics(("sensors/temp", 1), ("sensors/humidity", 2));
        var cut = RenderExpanded();
        cut.Find(".topic-select-toggle").Click();

        Row(cut, "sensors/temp").KeyDown(key);

        cut.WaitForAssertion(() =>
        {
            var state = _fixture.Adapter.Store.GetSelectedTopicState();
            state.Key.Should().StartWith("multi:");
            state.Paths.Should().Equal("sensors/temp");
        });
    }

    [Test]
    public void LeavingSelectionMode_ClearsTheBulkFilter_AndRestoresOrdinaryRows()
    {
        _fixture.AddTopics(("sensors/temp", 1), ("sensors/humidity", 2));
        var cut = RenderExpanded();
        cut.Find(".topic-select-toggle").Click();
        Check(cut, "sensors/temp");
        Check(cut, "sensors/humidity");
        _fixture.Adapter.Store.GetSelectedTopicState().Key.Should().StartWith("multi:");

        cut.Find(".topic-select-toggle").Click();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".topic-select-checkbox").Should().BeEmpty();
            _fixture.Adapter.Store.GetSelectedTopicState().Key.Should().BeNull();
        });
    }

    [Test]
    public async Task ASelectedTopicThatVanishes_IsDropped_AndTheRemainingFilterStaysHonest()
    {
        _fixture.AddTopics(("sensors/temp", 1), ("sensors/humidity", 2));
        var cut = RenderExpanded();
        cut.Find(".topic-select-toggle").Click();
        Check(cut, "sensors/temp");
        Check(cut, "sensors/humidity");

        _fixture.Adapter.Store.RemoveMatchingTopic("sensors/humidity");
        await cut.InvokeAsync(() => cut.Instance.RefreshAsync());

        cut.WaitForAssertion(() =>
        {
            var state = _fixture.Adapter.Store.GetSelectedTopicState();
            state.Key.Should().StartWith("multi:");
            state.Paths.Should().Equal("sensors/temp");
            cut.FindAll(".topic-select-checkbox input[checked]").Should().ContainSingle();
        });

        _fixture.Adapter.Store.RemoveMatchingTopic("sensors/temp");
        await cut.InvokeAsync(() => cut.Instance.RefreshAsync());

        cut.WaitForAssertion(() =>
        {
            _fixture.Adapter.Store.GetSelectedTopicState().Key.Should().BeNull(
                "no stale path may keep filtering");
            cut.FindAll(".topic-select-checkbox input[checked]").Should().BeEmpty();
        });
    }

    [Test]
    public async Task RegroupingTheTree_KeepsBothSelectedSparkplugTipsChecked()
    {
        _preferences.GroupSparkplugTopicsByNodeAndDevice = true;
        _fixture.Adapter.Add("spBv1.0/plant1/NBIRTH/node1", Message("birth", 10));
        _fixture.Adapter.Add("spBv1.0/plant1/NDATA/node1", Message("data", 20));
        var cut = RenderExpanded();
        cut.Find(".topic-select-toggle").Click();
        Check(cut, "spBv1.0/plant1/NBIRTH/node1");
        Check(cut, "spBv1.0/plant1/NDATA/node1");

        _preferences.GroupSparkplugTopicsByNodeAndDevice = false;
        _uiSettings.UiPreferencesChanged += Raise.Event<Action>();
        await cut.InvokeAsync(() => cut.Instance.RefreshAsync());
        cut.WaitForAssertion(() => cut.Markup.Should().NotContain("Grouped by node and device"));
        ExpandAll(cut);
        cut.WaitForAssertion(() =>
        {
            _fixture.Adapter.Store.GetSelectedTopicState().Key.Should().StartWith("multi:");
            cut.FindAll(".topic-select-checkbox input[checked]").Should().HaveCount(2);
        });

        _preferences.GroupSparkplugTopicsByNodeAndDevice = true;
        _uiSettings.UiPreferencesChanged += Raise.Event<Action>();
        await cut.InvokeAsync(() => cut.Instance.RefreshAsync());
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Grouped by node and device"));
        ExpandAll(cut);
        cut.WaitForAssertion(() =>
            cut.FindAll(".topic-select-checkbox input[checked]").Should().HaveCount(2));
    }

    [Test]
    public void TwoMountedBrowsers_ShareTheSameCheckedSet()
    {
        _fixture.AddTopics(("sensors/temp", 1), ("sensors/humidity", 2));
        var selection = new TopicBulkSelection(_fixture.Adapter);
        var first = Render<TopicBrowser>(parameters => parameters.AddCascadingValue(selection));
        var second = Render<TopicBrowser>(parameters => parameters.AddCascadingValue(selection));
        ExpandAll(first);
        ExpandAll(second);

        first.Find(".topic-select-toggle").Click();
        second.WaitForAssertion(() => second.FindAll(".topic-select-checkbox").Should().HaveCount(2));
        Check(first, "sensors/temp");

        second.WaitForAssertion(() =>
            Row(second, "sensors/temp").QuerySelector(".topic-select-checkbox input[checked]")
                .Should().NotBeNull());
    }

    [Test]
    public void TheSummaryShowsTheCheckedCount_AndClearSelectionEmptiesIt()
    {
        _fixture.AddTopics(("sensors/temp", 1), ("sensors/humidity", 2));
        var selection = new TopicBulkSelection(_fixture.Adapter);
        selection.Enter(["sensors/temp", "sensors/humidity"]);
        var cut = Render<TopicBulkToolbar>(parameters => parameters.AddCascadingValue(selection));

        cut.Markup.Should().Contain("2 topics selected");

        cut.Find(".topic-bulk-toolbar__clear-selection").Click();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("0 topics selected");
            _fixture.Adapter.Store.GetSelectedTopicState().Key.Should().BeNull();
        });
    }

    [Test]
    public void AliasOnlyTopic_ChecksTheRawSpelling_NotTheNormalizedName()
    {
        _fixture.Adapter.Add("a//b", RawMessage("alias", "a//b"));
        var cut = RenderExpanded();
        cut.Find(".topic-select-toggle").Click();
        cut.WaitForAssertion(() => cut.FindAll(".topic-select-checkbox").Should().HaveCount(1));

        Check(cut, "a/b");

        var state = _fixture.Adapter.Store.GetSelectedTopicState();
        state.Paths.Should().Equal("a//b");
        state.Label.Should().Be("a//b");
        state.MessageCount.Should().Be(1);
        _fixture.Adapter.Store.GetSelectedMessages(state.Token, 10)!.Messages
            .Should().ContainSingle().Which.Payload.Should().Be("alias");
    }

    [Test]
    public void EachSpellingOfOneNode_IsIndependentlySelectable()
    {
        _fixture.Adapter.Add("a/b", RawMessage("canonical", "a/b"));
        _fixture.Adapter.Add("a//b", RawMessage("alias", "a//b"));
        var cut = RenderExpanded();
        cut.Find(".topic-select-toggle").Click();
        cut.WaitForAssertion(() =>
            cut.FindAll(".topic-select-checkbox").Should().HaveCount(2));
        cut.FindAll(".topic-tree-row[data-topic-spelling]").Should().HaveCount(2);
        cut.FindAll(".topic-tree-row:not([data-topic-spelling]) .topic-select-checkbox")
            .Should().BeEmpty("only exact-spelling rows are bulk targets");

        Check(cut, "a//b");

        var state = _fixture.Adapter.Store.GetSelectedTopicState();
        state.Paths.Should().Equal("a//b");
        state.MessageCount.Should().Be(1);
        _fixture.Adapter.Store.GetSelectedMessages(state.Token, 10)!.Messages
            .Should().ContainSingle().Which.Payload.Should().Be("alias");
        cut.FindAll(".topic-tree-row[data-topic-path=\"a/b\"] input[type=checkbox][checked]")
            .Should().BeEmpty("the canonical spelling is a separate choice");

        Check(cut, "a/b");
        _fixture.Adapter.Store.GetSelectedTopicState().Paths.Should().Equal("a//b", "a/b");
    }

    [Test]
    public void ClearingOneSpelling_LeavesTheOtherSpellingIntact()
    {
        _fixture.Adapter.Add("a/b", RawMessage("canonical", "a/b"));
        _fixture.Adapter.Add("a//b", RawMessage("alias", "a//b"));
        var cut = RenderExpanded();
        cut.Find(".topic-select-toggle").Click();
        Check(cut, "a//b");

        _fixture.Adapter.Store.ClearMessagesForTopics(["a//b"]).Should().Be(1);

        var state = _fixture.Adapter.Store.GetSelectedTopicState();
        state.Paths.Should().Equal("a//b");
        state.MessageCount.Should().Be(0);
        _fixture.Adapter.Store.SelectTopics(TopicSelection.ForExactTopics(["a/b"]));
        var remaining = _fixture.Adapter.Store.GetSelectedTopicState();
        _fixture.Adapter.Store.GetSelectedMessages(remaining.Token, 10)!.Messages
            .Should().ContainSingle().Which.Payload.Should().Be("canonical");
        cut.InvokeAsync(() => cut.Instance.RefreshAsync());
        cut.WaitForAssertion(() =>
            cut.FindAll(".topic-tree-row[data-topic-path=\"a//b\"]").Should().ContainSingle(
                "the cleared spelling's row remains visible"));
    }

    [Test]
    public async Task AliasSelection_SurvivesARefresh_AndClearsWhenTheNodeGoes()
    {
        _fixture.Adapter.Add("a//b", RawMessage("alias", "a//b"));
        var cut = RenderExpanded();
        cut.Find(".topic-select-toggle").Click();
        Check(cut, "a/b");

        await cut.InvokeAsync(() => cut.Instance.RefreshAsync());
        _fixture.Adapter.Store.GetSelectedTopicState().Paths.Should().Equal("a//b");

        _fixture.Adapter.Store.RemoveMatchingTopic("a//b");
        await cut.InvokeAsync(() => cut.Instance.RefreshAsync());

        cut.WaitForAssertion(() =>
            _fixture.Adapter.Store.GetSelectedTopicState().Key.Should().BeNull());
    }

    private IRenderedComponent<TopicBrowser> RenderExpanded()
    {
        var cut = Render<TopicBrowser>();
        EnsureMudProviders();
        ExpandAll(cut);
        return cut;
    }

    private static void ExpandAll(IRenderedComponent<TopicBrowser> cut) =>
        cut.FindAll(".mud-toolbar button")[0].Click();

    private static IElement Row(IRenderedComponent<TopicBrowser> cut, string path) =>
        cut.FindAll(".topic-tree-row")
            .First(row => row.GetAttribute("data-topic-path") == path);

    private static void Check(IRenderedComponent<TopicBrowser> cut, string label)
    {
        var rows = cut.FindAll(".topic-tree-row")
            .Where(row => row.GetAttribute("data-topic-path") == label)
            .ToList();
        var checkbox = rows
            .Select(row => row.QuerySelector(".topic-select-checkbox input"))
            .FirstOrDefault(input => input is not null);
        if (checkbox is null)
            throw new InvalidOperationException(
                $"no checkbox for {label}: " + string.Join(" | ", rows.Select(row => row.OuterHtml)));
        checkbox.Change(true);
    }

    private static MqttMessage Message(string payload, int second) =>
        new(payload, "unused", false, MqttQualityOfServiceLevel.AtMostOnce)
        {
            Topic = null,
            DateTimeReceived = DateTime.UnixEpoch.AddSeconds(second)
        };

    private static MqttMessage RawMessage(string payload, string rawTopic) =>
        new(payload, rawTopic, false, MqttQualityOfServiceLevel.AtMostOnce)
        {
            Topic = rawTopic,
            DateTimeReceived = DateTime.UnixEpoch.AddSeconds(1)
        };
}
