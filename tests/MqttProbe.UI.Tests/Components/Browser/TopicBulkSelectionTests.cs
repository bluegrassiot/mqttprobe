using AngleSharp.Dom;
using Microsoft.AspNetCore.Components.Web;
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
    public async Task EveryDisplayedLevel_HasACheckbox()
    {
        _fixture.AddTopics(("sensors/temp", 1), ("sensors/temp/room1", 2), ("sensors/humidity", 2));
        var cut = await RenderExpandedAsync();

        cut.WaitForAssertion(() => cut.FindAll(".topic-select-checkbox").Should().HaveCount(4));
        Row(cut, "sensors").QuerySelector(".topic-select-checkbox").Should().NotBeNull();
        Row(cut, "sensors/temp").QuerySelector(".topic-select-checkbox").Should().NotBeNull();
        Row(cut, "sensors/temp/room1").QuerySelector(".topic-select-checkbox").Should().NotBeNull();
        Row(cut, "sensors/humidity").QuerySelector(".topic-select-checkbox").Should().NotBeNull();
    }

    [Test]
    public async Task TheCheckboxSitsBesideTheNodeIcon_WithoutAPlaceholderSlot()
    {
        _fixture.AddTopics(("sensors/temp", 1), ("sensors/temp/room1", 2));
        var cut = await RenderExpandedAsync();

        cut.WaitForAssertion(() => cut.FindAll(".topic-select-checkbox").Should().HaveCount(3));

        var grouping = Row(cut, "sensors");
        grouping.QuerySelector(".topic-select-checkbox").Should().NotBeNull();
        grouping.QuerySelector(".topic-select-slot").Should().BeNull("no blank gutter is reserved");
        AssertColumnBesideIcon(grouping, cell => cell.QuerySelector(".topic-chevron") is not null);

        AssertColumnBesideIcon(Row(cut, "sensors/temp"), cell => cell.QuerySelector(".topic-chevron") is not null);
        AssertColumnBesideIcon(
            Row(cut, "sensors/temp/room1"), cell => cell.ClassList.Contains("topic-tree-chevron-spacer"));
    }

    [Test]
    public async Task SelectingTwoTopics_FiltersTheirUnion_NewestFirst_WithoutDuplicates()
    {
        _fixture.Adapter.Add("sensors/temp", Message("temp-old", 10));
        _fixture.Adapter.Add("sensors/humidity", Message("humid", 20));
        _fixture.Adapter.Add("sensors/temp", Message("temp-new", 30));
        _fixture.Adapter.Add("unrelated/topic", Message("unrelated", 40));
        var cut = await RenderExpandedAsync();

        await CheckAsync(cut, "sensors/temp");
        await CheckAsync(cut, "sensors/humidity");

        var state = _fixture.Adapter.Store.GetSelectedTopicState();
        state.Key.Should().StartWith("branches:");
        state.Label.Should().Be("2 branches selected");
        state.MessageCount.Should().Be(3);
        var messages = _fixture.Adapter.Store.GetSelectedMessages(state.Token, 10)!;
        messages.Messages.Select(message => message.Payload)
            .Should().Equal("temp-new", "humid", "temp-old");
    }

    [Test]
    public async Task RowClick_ChecksTheConcreteTopic()
    {
        _fixture.AddTopics(("sensors/temp", 1), ("sensors/humidity", 2));
        var cut = await RenderExpandedAsync();

        await Row(cut, "sensors/temp").ClickAsync();

        cut.WaitForAssertion(() =>
        {
            var state = _fixture.Adapter.Store.GetSelectedTopicState();
            state.Key.Should().StartWith("branches:");
            state.Branches.Select(branch => branch.Path).Should().Equal("sensors/temp");
        });
    }

    [TestCase("Enter")]
    [TestCase(" ")]
    public async Task KeyboardActivation_ChecksTheRowInsteadOfSelectingTheTopic(string key)
    {
        _fixture.AddTopics(("sensors/temp", 1), ("sensors/humidity", 2));
        var cut = await RenderExpandedAsync();

        await Row(cut, "sensors/temp").KeyDownAsync(new KeyboardEventArgs { Key = key });

        cut.WaitForAssertion(() =>
        {
            var state = _fixture.Adapter.Store.GetSelectedTopicState();
            state.Key.Should().StartWith("branches:");
            state.Branches.Select(branch => branch.Path).Should().Equal("sensors/temp");
        });
    }

    [Test]
    public async Task ASelectedTopicThatVanishes_IsDropped_AndTheRemainingFilterStaysHonest()
    {
        _fixture.AddTopics(("sensors/temp", 1), ("sensors/humidity", 2));
        var cut = await RenderExpandedAsync();
        await CheckAsync(cut, "sensors/temp");
        await CheckAsync(cut, "sensors/humidity");

        _fixture.Adapter.Store.RemoveMatchingTopic("sensors/humidity");
        await cut.InvokeAsync(() => cut.Instance.RefreshAsync());

        cut.WaitForAssertion(() =>
        {
            var state = _fixture.Adapter.Store.GetSelectedTopicState();
            state.Key.Should().StartWith("branches:");
            state.Branches.Select(branch => branch.Path).Should().Equal("sensors/temp");
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
        var cut = await RenderExpandedAsync();
        await CheckAsync(cut, "spBv1.0/plant1/NBIRTH/node1");
        await CheckAsync(cut, "spBv1.0/plant1/NDATA/node1");

        _preferences.GroupSparkplugTopicsByNodeAndDevice = false;
        _uiSettings.UiPreferencesChanged += Raise.Event<Action>();
        await cut.InvokeAsync(() => cut.Instance.RefreshAsync());
        cut.WaitForAssertion(() => cut.Markup.Should().NotContain("Grouped by node and device"));
        await ExpandAllAsync(cut);
        cut.WaitForAssertion(() =>
        {
            _fixture.Adapter.Store.GetSelectedTopicState().Key.Should().StartWith("branches:");
            cut.FindAll(".topic-select-checkbox input[checked]").Should().HaveCount(2);
        });

        _preferences.GroupSparkplugTopicsByNodeAndDevice = true;
        _uiSettings.UiPreferencesChanged += Raise.Event<Action>();
        await cut.InvokeAsync(() => cut.Instance.RefreshAsync());
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Grouped by node and device"));
        await ExpandAllAsync(cut);
        cut.WaitForAssertion(() =>
        {
            _fixture.Adapter.Store.GetSelectedTopicState().Branches.Select(branch => branch.Path)
                .Should().BeEquivalentTo("spBv1.0/plant1/NBIRTH/node1", "spBv1.0/plant1/NDATA/node1");
            cut.FindAll(".topic-select-checkbox input[checked]").Should().HaveCount(4,
                "the two tips and the two message-type rows that name exactly them");
            cut.FindAll(".topic-tree-row")
                .First(row => row.QuerySelector(".topic-name")?.TextContent.Trim() == "node1")
                .QuerySelector(".topic-select-checkbox .mud-checkbox-null")
                .Should().NotBeNull("the node row also names verbs that are not selected");
        });
    }

    [Test]
    public async Task TwoMountedBrowsers_ShareTheSameCheckedSet()
    {
        _fixture.AddTopics(("sensors/temp", 1), ("sensors/humidity", 2));
        var selection = new TopicBulkSelection(_fixture.Adapter);
        var first = Render<TopicBrowser>(parameters => parameters.AddCascadingValue(selection));
        var second = Render<TopicBrowser>(parameters => parameters.AddCascadingValue(selection));
        await ExpandAllAsync(first);
        await ExpandAllAsync(second);

        second.WaitForAssertion(() => second.FindAll(".topic-select-checkbox").Should().HaveCount(3));
        await CheckAsync(first, "sensors/temp");

        second.WaitForAssertion(() =>
            Row(second, "sensors/temp").QuerySelector(".topic-select-checkbox input[checked]")
                .Should().NotBeNull());
    }

    [Test]
    public void TheSummaryShowsTheCheckedCount_AndClearingItHidesTheStrip()
    {
        _fixture.AddTopics(("sensors/temp", 1), ("sensors/humidity", 2));
        var selection = new TopicBulkSelection(_fixture.Adapter);
        selection.SelectAll([Raw("sensors/temp"), Raw("sensors/humidity")]);
        var cut = Render<TopicBulkToolbar>(parameters => parameters.AddCascadingValue(selection));

        cut.Markup.Should().Contain("2 topics selected");

        selection.Clear();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().NotContain("topics selected");
            _fixture.Adapter.Store.GetSelectedTopicState().Key.Should().BeNull();
        });
    }

    [Test]
    public async Task RawSlashLevelsAreDistinctBranches()
    {
        _fixture.Adapter.Add("a/b", RawMessage("canonical", "a/b"));
        _fixture.Adapter.Add("a//b", RawMessage("alias", "a//b"));
        var cut = await RenderExpandedAsync();

        cut.FindAll(".topic-tree-row[data-topic-path='a/b']").Should().ContainSingle();
        cut.FindAll(".topic-tree-row[data-topic-path='a//b']").Should().ContainSingle();
        cut.WaitForAssertion(() => cut.FindAll(".topic-select-checkbox").Should().HaveCount(4));

        await CheckAsync(cut, "a//b");

        var state = _fixture.Adapter.Store.GetSelectedTopicState();
        state.Branches.Should().ContainSingle().Which.Path.Should().Be("a//b");
    }

    [Test]
    public async Task TheEmptyFirstLevel_SelectsOnlyTopicsThatStartWithASlash()
    {
        _fixture.Adapter.Add("a", RawMessage("plain", "a"));
        _fixture.Adapter.Add("/b", RawMessage("leading", "/b"));
        _fixture.Adapter.Add("/c", RawMessage("leading-2", "/c"));
        var cut = await RenderExpandedAsync();

        cut.WaitForAssertion(() => cut.FindAll(".topic-select-checkbox").Should().HaveCount(4));

        Row(cut, "").QuerySelector(".topic-name--empty").Should().NotBeNull();
        Row(cut, "").QuerySelector(".topic-name--empty")!.TextContent.Should().Be("(empty level)");
        Row(cut, "").GetAttribute("title").Should().Contain("Empty first MQTT level");
        await CheckAsync(cut, "");

        var state = _fixture.Adapter.Store.GetSelectedTopicState();
        state.Branches.Should().ContainSingle().Which.Path.Should().BeEmpty();
        state.MessageCount.Should().Be(2, "the empty first level is not every topic");
        Row(cut, "").QuerySelector(".topic-select-checkbox input[checked]").Should().NotBeNull();
        Row(cut, "a").QuerySelector(".topic-select-checkbox input[checked]").Should().BeNull();
    }

    [Test]
    public async Task ACoveredDescendant_CannotBeUncheckedOnItsOwn()
    {
        _fixture.AddTopics(("a/b", 1), ("a/c", 1), ("a", 1));
        var cut = await RenderExpandedAsync();

        await CheckAsync(cut, "a");
        var inherited = Row(cut, "a/b").QuerySelector(".topic-select-checkbox input");
        inherited.Should().NotBeNull();
        inherited!.HasAttribute("disabled").Should().BeTrue(
            "removing the branch that covers this row would drop its siblings too");
        Row(cut, "a/b").QuerySelector(".topic-select-checkbox__name")!.TextContent
            .Should().Be("Selected through \"a\"");

        await Row(cut, "a/b").ClickAsync();
        _fixture.Adapter.Store.GetSelectedTopicState().Branches.Should().ContainSingle()
            .Which.Path.Should().Be("a", "the row cannot drop the branch that covers it");

        await Row(cut, "a").ClickAsync();
        cut.WaitForAssertion(() => cut.FindAll(".topic-select-checkbox input[checked]").Should().BeEmpty());
    }

    [Test]
    public async Task AMixedAncestor_StaysEditable_AndWidensTheSelectionToItself()
    {
        _fixture.AddTopics(("a/b", 1), ("a/c", 1));
        var cut = await RenderExpandedAsync();

        await CheckAsync(cut, "a/b");
        Row(cut, "a").QuerySelector(".topic-select-checkbox input")!.HasAttribute("disabled")
            .Should().BeFalse("a partly covered ancestor is still its own choice");

        await Row(cut, "a").ClickAsync();

        _fixture.Adapter.Store.GetSelectedTopicState().Branches.Should().ContainSingle()
            .Which.Path.Should().Be("a", "the wider branch already covers the narrower one");
    }

    [Test]
    public async Task ClickingARow_ChecksItsBranchAndKeepsIt()
    {
        _fixture.AddTopics(("sensors/temp", 1), ("sensors/humidity", 1));
        var cut = await RenderExpandedAsync();

        await Row(cut, "sensors/temp").ClickAsync();

        var state = _fixture.Adapter.Store.GetSelectedTopicState();
        state.Key.Should().StartWith("branches:");
        state.Branches.Should().ContainSingle().Which.Path.Should().Be("sensors/temp");
        cut.WaitForAssertion(() =>
        {
            Row(cut, "sensors/temp").ClassName.Should().Contain("topic-tree-row--selected");
            cut.FindAll(".topic-tree-row--selected").Should().ContainSingle();
        });
    }

    [Test]
    public void TwoScopesThatDifferOnlyByFieldBoundary_StayDistinct()
    {
        var first = new TopicBranchTarget(TopicBranchKind.Sparkplug,
            Scope: new SparkplugSelectionScope(SparkplugSelectionKind.Node, "a|b", "c"));
        var second = new TopicBranchTarget(TopicBranchKind.Sparkplug,
            Scope: new SparkplugSelectionScope(SparkplugSelectionKind.Node, "a", "b|c"));
        var selection = new TopicBulkSelection(_fixture.Adapter);

        selection.SelectAll([first, second]);

        selection.Count.Should().Be(2);
        selection.Branches.Should().BeEquivalentTo(new[] { first, second });
    }

    [Test]
    public async Task GlobalCheckbox_IsUncheckedWithNoSelection_AndChecksEveryRoot()
    {
        _fixture.AddTopics(("sensors/temp", 1), ("sensors/humidity", 1), ("unrelated/topic", 1));
        var (cut, selection) = await RenderWithSelectionAsync();

        GlobalCheckbox(cut).HasAttribute("checked").Should().BeFalse();
        selection.CoversWholeTree.Should().BeFalse();

        await GlobalClickAsync(cut);

        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".topic-select-checkbox input[checked]").Should().HaveCount(5);
            selection.Branches.Select(branch => branch.Path)
                .Should().BeEquivalentTo("sensors", "unrelated");
            selection.Summary.Should().Be("All topics selected");
        });

        await GlobalClickAsync(cut);

        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".topic-select-checkbox input[checked]").Should().BeEmpty();
            _fixture.Adapter.Store.GetSelectedTopicState().Key.Should().BeNull();
            selection.CoversWholeTree.Should().BeFalse();
        });
    }

    [Test]
    public async Task GlobalCheckbox_IsMixedWhenOnlySomeRootsAreChecked_AndAClickChecksAll()
    {
        _fixture.AddTopics(("sensors/temp", 1), ("sensors/humidity", 1));
        var (cut, selection) = await RenderWithSelectionAsync();

        await CheckAsync(cut, "sensors/temp");

        cut.WaitForAssertion(() => cut.Find(".topic-select-all .mud-checkbox-null").Should().NotBeNull(
            "a partial selection is mixed, not unchecked"));
        selection.Summary.Should().Be("1 topic selected");

        await GlobalClickAsync(cut);

        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".topic-select-checkbox input[checked]").Should().HaveCount(3);
            selection.Summary.Should().Be("All topics selected");
        });
    }

    [Test]
    public async Task GlobalCheckbox_SpansTheWholeTreeBeyondTheFilter()
    {
        _fixture.AddTopics(("sensors/temp", 1), ("sensors/humidity", 1), ("unrelated/topic", 1));
        var (cut, selection) = await RenderWithSelectionAsync();

        await cut.Find(".topic-filter-field input").InputAsync("humidity");
        cut.WaitForAssertion(() =>
            cut.FindAll(".topic-tree-row").Should().HaveCount(2, "the match and its ancestor"));

        await GlobalClickAsync(cut);

        cut.WaitForAssertion(() =>
            selection.Branches.Select(branch => branch.Path)
                .Should().BeEquivalentTo("sensors", "unrelated"));

        await cut.Find(".topic-filter-field input").InputAsync(string.Empty);
        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".topic-tree-row").Should().HaveCount(5);
            cut.FindAll(".topic-select-checkbox input[checked]").Should().HaveCount(5,
                "the roots checked through the global checkbox cover every row");
        });
    }

    [Test]
    public async Task GlobalCheckbox_TurnsMixedWhenANewRootArrives()
    {
        _fixture.AddTopics(("sensors/temp", 1));
        var (cut, selection) = await RenderWithSelectionAsync();
        await GlobalClickAsync(cut);
        cut.WaitForAssertion(() => selection.Summary.Should().Be("All topics selected"));

        _fixture.AddTopics(("other", 1));
        await cut.InvokeAsync(() => cut.Instance.RefreshAsync());

        cut.WaitForAssertion(() =>
        {
            cut.Find(".topic-select-all .mud-checkbox-null").Should().NotBeNull(
                "the new root is not selected, so the tree is only partly selected");
            selection.Branches.Should().ContainSingle().Which.Path.Should().Be("sensors");
        });

        await GlobalClickAsync(cut);
        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".topic-select-checkbox input[checked]").Should().HaveCount(3);
            selection.Summary.Should().Be("All topics selected");
        });
    }

    [Test]
    public void GlobalCheckbox_IsDisabledWithoutTopics()
    {
        var cut = Render<TopicBrowser>();
        EnsureMudProviders();

        GlobalCheckbox(cut).HasAttribute("disabled").Should().BeTrue(
            "there is nothing to select");
    }

    private async Task<IRenderedComponent<TopicBrowser>> RenderExpandedAsync()
    {
        var cut = Render<TopicBrowser>();
        EnsureMudProviders();
        await ExpandAllAsync(cut);
        return cut;
    }

    private async Task<(IRenderedComponent<TopicBrowser> Cut, TopicBulkSelection Selection)> RenderWithSelectionAsync()
    {
        var selection = new TopicBulkSelection(_fixture.Adapter);
        var cut = Render<TopicBrowser>(parameters => parameters.AddCascadingValue(selection));
        EnsureMudProviders();
        await ExpandAllAsync(cut);
        return (cut, selection);
    }

    private static IElement GlobalCheckbox(IRenderedComponent<TopicBrowser> cut) =>
        cut.Find(".topic-select-all input");

    private static async Task GlobalClickAsync(IRenderedComponent<TopicBrowser> cut)
    {
        var input = cut.Find(".topic-select-all input");
        await input.ChangeAsync(!input.HasAttribute("checked"));
    }

    private static async Task ExpandAllAsync(IRenderedComponent<TopicBrowser> cut) =>
        await cut.FindAll(".mud-toolbar button")[0].ClickAsync();

    private static IElement Row(IRenderedComponent<TopicBrowser> cut, string path) =>
        cut.FindAll(".topic-tree-row")
            .First(row => row.GetAttribute("data-topic-path") == path);

    private static void AssertColumnBesideIcon(IElement row, Func<IElement, bool> precedesColumn)
    {
        var children = row.Children.ToList();
        var checkbox = children.FindIndex(child => child.ClassList.Contains("topic-select-checkbox-wrap"));
        var icon = children.FindIndex(child => child.ClassList.Contains("topic-node-icon"));

        checkbox.Should().BeGreaterThan(0);
        checkbox.Should().Be(icon - 1, "the checkbox sits immediately before the node icon");
        precedesColumn(children[checkbox - 1]).Should().BeTrue("the column follows the chevron cell");
    }

    private static TopicBranchTarget Raw(string path) => new(TopicBranchKind.Raw, Path: path);

    private static async Task CheckAsync(IRenderedComponent<TopicBrowser> cut, string label)
    {
        var rows = cut.FindAll(".topic-tree-row")
            .Where(row => row.GetAttribute("data-topic-path") == label)
            .ToList();
        var checkbox = rows
            .Select(row => row.QuerySelector(".topic-select-checkbox-wrap"))
            .FirstOrDefault(wrapper => wrapper is not null);
        if (checkbox is null)
            throw new InvalidOperationException(
                $"no checkbox for {label}: " + string.Join(" | ", rows.Select(row => row.OuterHtml)));
        await checkbox.ClickAsync();
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
