using Microsoft.Extensions.DependencyInjection;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Services.Mqtt;
using MqttProbe.Core.Services.Security;
using MqttProbe.UI.Tests.TestHelpers;
using MudBlazor;

namespace MqttProbe.UI.Tests.Components.Browser;

[TestFixture]
public sealed class TopicSingleSelectionActionsTests : BunitTestContext
{
    private const string Alpha = "sensors/temp";
    private const string Child = "sensors/temp/room1";
    private const string Other = "sensors/humidity";

    private TopicBrowserTestFixture _fixture = null!;
    private IDialogService _dialogs = null!;
    private ISnackbar _snackbar = null!;
    private ITopicExcludeService _excludes = null!;
    private IMessageStoreManager _manager = null!;

    [SetUp]
    public void Setup()
    {
        _fixture = new TopicBrowserTestFixture();
        Services.AddSingleton<ITopicSelectionStore>(_fixture.Adapter);
        Services.AddUiSettings(SettingsSubstituteRegistration.CreateUiSettings());
        _dialogs = Substitute.For<IDialogService>();
        _snackbar = Substitute.For<ISnackbar>();
        _excludes = Substitute.For<ITopicExcludeService>();
        _manager = Substitute.For<IMessageStoreManager>();
        _manager.GetSelectedTopicState().Returns(_ => _fixture.Adapter.Store.GetSelectedTopicState());
        Services.AddSingleton(_dialogs);
        Services.AddSingleton(_snackbar);
        Services.AddSingleton(_excludes);
        Services.AddSingleton(_manager);
        Services.AddSingleton(Substitute.For<ISubscriptionManager>());
        _excludes.TopicExcludes.Returns(Array.Empty<string>());
    }

    [Test]
    public async Task ClearMessages_SingleTopic_ClearsItsScopeAndKeepsItSelected()
    {
        AddTopics();
        var selection = SingleTopicSubscription();
        ReturnDialog(DialogResult.Ok(selection.Branches));
        _manager.ClearMessagesForBranchesAsync(
                Arg.Any<IReadOnlyList<TopicBranchTarget>>(), Arg.Any<TopicExcludeConnectionToken>())
            .Returns(call => Task.FromResult(new TopicBranchClearResult(true,
                _fixture.Adapter.Store.ClearMessagesForBranches(
                    call.Arg<IReadOnlyList<TopicBranchTarget>>()))));
        var cut = RenderToolbar(selection);

        cut.Find(".topic-bulk-toolbar__count").TextContent.Should()
            .Be("Selected topic and its subtopics: \"sensors/temp\"");
        cut.Find(".topic-bulk-toolbar__mobile-count").TextContent.Should()
            .Be("Topic and subtopics: \"sensors/temp\"");
        var clear = cut.Find(".topic-bulk-toolbar__clear-messages");
        clear.GetAttribute("title").Should()
            .Be("Clears stored messages for the selected topic and its subtopics. New messages still arrive.");

        clear.Click();
        await cut.InvokeAsync(() => Task.CompletedTask);

        await _manager.Received(1).ClearMessagesForBranchesAsync(
            Arg.Is<IReadOnlyList<TopicBranchTarget>>(targets => targets.SequenceEqual(selection.Branches)),
            Arg.Any<TopicExcludeConnectionToken>());

        cut.WaitForAssertion(() => cut.Find(".topic-bulk-outcome").TextContent
            .Should().Contain("3 stored messages cleared from 1 topic"));
        var state = _fixture.Adapter.Store.GetSelectedTopicState();
        state.Mode.Should().Be(TopicSelectionMode.Branches,
            "the cleared topic is kept as a branch so later arrivals stay selected");
        state.Branches.Should().ContainSingle().Which.Path.Should().Be(Alpha);

        _fixture.Adapter.Add(Alpha, Message("fresh-parent", 5));
        _fixture.Adapter.Add(Child, Message("fresh-child", 6));
        state = _fixture.Adapter.Store.GetSelectedTopicState();
        var messages = _fixture.Adapter.Store.GetSelectedMessages(state.Token, 10)!.Messages;
        messages.Select(message => message.Payload).Should().Contain("fresh-parent");
        messages.Select(message => message.Payload).Should().Contain("fresh-child");
        cut.FindAll(".topic-bulk-toolbar").Should().ContainSingle();
    }

    [Test]
    public async Task ClearMessages_GroupedScope_UsesTheBranchClear()
    {
        AddTopics();
        var selection = GroupedScopeSelection();
        ReturnDialog(DialogResult.Ok(selection.Branches));
        _manager.ClearMessagesForBranchesAsync(
                Arg.Any<IReadOnlyList<TopicBranchTarget>>(), Arg.Any<TopicExcludeConnectionToken>())
            .Returns(Task.FromResult(new TopicBranchClearResult(true, 0)));
        var cut = RenderToolbar(selection);

        cut.Find(".topic-bulk-toolbar__count").TextContent.Should()
            .Be("Selected node: spBv1.0/plant1/node1");
        cut.Find(".topic-bulk-toolbar__clear-messages").GetAttribute("title").Should()
            .Be("Clears stored messages for the selected group of topics. New messages still arrive.");

        cut.Find(".topic-bulk-toolbar__clear-messages").Click();
        await cut.InvokeAsync(() => Task.CompletedTask);

        await _manager.Received(1).ClearMessagesForBranchesAsync(
            Arg.Is<IReadOnlyList<TopicBranchTarget>>(targets => targets.SequenceEqual(selection.Branches)),
            Arg.Any<TopicExcludeConnectionToken>());
    }

    [Test]
    public async Task ClearMessages_DoesNotRestoreATopicDeselectedWhileTheDialogIsOpen()
    {
        AddTopics();
        var selection = SingleTopicSubscription();
        var (shown, result) = DeferClearDialog();
        StubClearThroughStore();
        var cut = RenderToolbar(selection);

        cut.Find(".topic-bulk-toolbar__clear-messages").Click();
        await shown.WaitAsync(TimeSpan.FromSeconds(5));
        _fixture.Adapter.Store.SelectTopics(null);
        await cut.InvokeAsync(() => result.SetResult(DialogResult.Ok(selection.Branches)));

        cut.WaitForAssertion(() => cut.Find(".topic-bulk-outcome").TextContent
            .Should().Contain("cleared from 1 topic"));
        _fixture.Adapter.Store.GetSelectedTopicState().Key.Should().BeNull(
            "a topic deselected before the clear must stay deselected");
    }

    [Test]
    public async Task ClearMessages_PreservesTheDescendantBrowsedWhileTheDialogIsOpen()
    {
        AddTopics();
        var selection = SingleTopicSubscription();
        var (shown, result) = DeferClearDialog();
        StubClearThroughStore();
        var cut = RenderToolbar(selection);

        cut.Find(".topic-bulk-toolbar__clear-messages").Click();
        await shown.WaitAsync(TimeSpan.FromSeconds(5));
        _fixture.Adapter.Store.SelectTopics(TopicSelection.ForTopic(Child));
        await cut.InvokeAsync(() => result.SetResult(DialogResult.Ok(selection.Branches)));

        cut.WaitForAssertion(() => cut.Find(".topic-bulk-outcome").TextContent
            .Should().Contain("cleared from 1 topic"));
        var state = _fixture.Adapter.Store.GetSelectedTopicState();
        state.Mode.Should().Be(TopicSelectionMode.Branches);
        state.Branches.Should().ContainSingle().Which.Path.Should().Be(Child,
            "the browse scope selected before the clear is kept instead of the cleared parent");

        _fixture.Adapter.Add(Child, Message("fresh-child", 7));
        _fixture.Adapter.Add(Alpha, Message("fresh-parent", 8));
        state = _fixture.Adapter.Store.GetSelectedTopicState();
        var messages = _fixture.Adapter.Store.GetSelectedMessages(state.Token, 10)!.Messages;
        messages.Select(message => message.Payload).Should().Contain("fresh-child");
        messages.Select(message => message.Payload).Should().NotContain("fresh-parent");
        cut.FindAll(".topic-bulk-toolbar").Should().ContainSingle();
    }

    [Test]
    public async Task ClearMessages_PreservesTheEmptyRootBrowseForSlashLeadingTopics()
    {
        _fixture.AddTopics(("/parent", 1), ("/parent/child", 1));
        var selection = EmptyRootSubscription();
        selection.Branches.Should().ContainSingle().Which.Path.Should().Be(string.Empty);
        ReturnDialog(DialogResult.Ok(selection.Branches));
        StubClearThroughStore();
        var cut = RenderToolbar(selection);

        cut.Find(".topic-bulk-toolbar__clear-messages").Click();
        await cut.InvokeAsync(() => Task.CompletedTask);

        cut.WaitForAssertion(() => cut.Find(".topic-bulk-outcome").TextContent
            .Should().Contain("cleared from 1 topic"));
        var state = _fixture.Adapter.Store.GetSelectedTopicState();
        state.Mode.Should().Be(TopicSelectionMode.Branches);
        state.Branches.Should().ContainSingle().Which.Path.Should().Be(string.Empty,
            "an empty key is the structural root for slash-leading topics");

        _fixture.Adapter.Add("/parent", Message("fresh-parent", 9));
        _fixture.Adapter.Add("/parent/child", Message("fresh-child", 10));
        _fixture.Adapter.Add("plain-sibling", Message("fresh-sibling", 11));
        state = _fixture.Adapter.Store.GetSelectedTopicState();
        var messages = _fixture.Adapter.Store.GetSelectedMessages(state.Token, 10)!.Messages;
        messages.Select(message => message.Payload).Should().Contain("fresh-parent");
        messages.Select(message => message.Payload).Should().Contain("fresh-child");
        messages.Select(message => message.Payload).Should().NotContain("fresh-sibling",
            "a non-slash sibling is outside the structural root");
        cut.FindAll(".topic-bulk-toolbar").Should().ContainSingle();
    }

    [Test]
    public async Task ClearMessages_SkipsRestorationWhenTheConnectionChangesBeforeRestore()
    {
        AddTopics();
        var selection = SingleTopicSubscription();
        var (shown, result) = DeferClearDialog();
        StubClearThroughStore();
        var original = new TopicExcludeConnectionToken(1, Guid.NewGuid(), 1);
        var switched = new TopicExcludeConnectionToken(2, Guid.NewGuid(), 1);
        var current = original;
        _excludes.CaptureConnectionToken().Returns(_ => current);
        var cut = RenderToolbar(selection);

        cut.Find(".topic-bulk-toolbar__clear-messages").Click();
        await shown.WaitAsync(TimeSpan.FromSeconds(5));
        current = switched;
        await cut.InvokeAsync(() => result.SetResult(DialogResult.Ok(selection.Branches)));

        cut.WaitForAssertion(() => cut.Find(".topic-bulk-outcome").TextContent
            .Should().Contain("cleared from 1 topic"));
        _fixture.Adapter.Store.GetSelectedTopicState().Key.Should().BeNull(
            "the new connection keeps its own selection");
    }

    [Test]
    public async Task Exclude_SingleTopic_UsesTheBranchExclusionAndClearsTheSelection()
    {
        AuthorizeAsOperator();
        AddTopics();
        var selection = SingleTopicSubscription();
        var branch = selection.Branches.Single();
        ReturnDialog(DialogResult.Ok(selection.Branches));
        _excludes.AddBranches(Arg.Any<IReadOnlyList<TopicBranchTarget>>(), Arg.Any<TopicExcludeConnectionToken>())
            .Returns(new TopicBranchExcludeBatchResult([
                new TopicBranchExcludeResult(branch, TopicExcludeAddOutcome.Added)
            ]));
        var cut = RenderToolbar(selection);

        var exclude = cut.Find(".topic-bulk-toolbar__exclude");
        exclude.TextContent.Trim().Should().Be("Exclude topic");
        exclude.GetAttribute("title").Should()
            .Be("Adds the selected topic and its subtopics to this connection's excluded topics.");

        exclude.Click();
        await cut.InvokeAsync(() => Task.CompletedTask);

        await _excludes.Received(1).AddBranches(
            Arg.Is<IReadOnlyList<TopicBranchTarget>>(targets => targets.SequenceEqual(new[] { branch })),
            Arg.Any<TopicExcludeConnectionToken>());
        await _excludes.DidNotReceive().AddMany(Arg.Any<IReadOnlyList<string>>());
        _snackbar.Received(1).Add(
            "Excluded 1 topic.", Severity.Success, Arg.Any<Action<SnackbarOptions>?>(), Arg.Any<string?>());
        _fixture.Adapter.Store.GetSelectedTopicState().Key.Should().BeNull();
        cut.WaitForAssertion(() => cut.FindAll(".topic-bulk-toolbar").Should().BeEmpty());
    }

    [Test]
    public async Task Exclude_GroupedScope_UsesTheBranchExclusion()
    {
        AuthorizeAsOperator();
        AddTopics();
        var selection = GroupedScopeSelection();
        var branch = selection.Branches.Single();
        ReturnDialog(DialogResult.Ok(selection.Branches));
        _excludes.AddBranches(Arg.Any<IReadOnlyList<TopicBranchTarget>>(), Arg.Any<TopicExcludeConnectionToken>())
            .Returns(new TopicBranchExcludeBatchResult([
                new TopicBranchExcludeResult(branch, TopicExcludeAddOutcome.Added)
            ]));
        var cut = RenderToolbar(selection);

        cut.Find(".topic-bulk-toolbar__exclude").Click();
        await cut.InvokeAsync(() => Task.CompletedTask);

        await _excludes.Received(1).AddBranches(
            Arg.Is<IReadOnlyList<TopicBranchTarget>>(targets => targets.SequenceEqual(new[] { branch })),
            Arg.Any<TopicExcludeConnectionToken>());
        await _excludes.DidNotReceive().AddMany(Arg.Any<IReadOnlyList<string>>());
        _fixture.Adapter.Store.GetSelectedTopicState().Key.Should().BeNull();
        cut.WaitForAssertion(() => cut.FindAll(".topic-bulk-toolbar").Should().BeEmpty());
    }

    private void AddTopics()
    {
        _fixture.AddTopics((Alpha, 2), (Child, 1), (Other, 1));
    }

    private TopicBulkSelection SingleTopicSubscription()
    {
        _fixture.Adapter.Store.SelectTopics(TopicSelection.ForTopic(Alpha));
        return PassiveSelection();
    }

    private TopicBulkSelection EmptyRootSubscription()
    {
        _fixture.Adapter.Store.SelectTopics(TopicSelection.ForTopic(string.Empty));
        return PassiveSelection();
    }

    private TopicBulkSelection GroupedScopeSelection()
    {
        var branch = new TopicBranchTarget(TopicBranchKind.Sparkplug,
            Scope: new SparkplugSelectionScope(SparkplugSelectionKind.Node, "spBv1.0/plant1", "node1"));
        _fixture.Adapter.Store.SelectTopics(TopicSelection.ForBranches([branch]));
        return PassiveSelection();
    }

    private TopicBulkSelection PassiveSelection()
    {
        var selection = new TopicBulkSelection(_fixture.Adapter);
        selection.SetMultiSelect(false);
        selection.AdoptSingle(_fixture.Adapter.Store.GetSelectedTopicState());
        return selection;
    }

    private IRenderedComponent<TopicBulkToolbar> RenderToolbar(TopicBulkSelection selection) =>
        Render<TopicBulkToolbar>(parameters => parameters.AddCascadingValue(selection));

    private void ReturnDialog(DialogResult result)
    {
        var dialogRef = Substitute.For<IDialogReference>();
        dialogRef.Result.Returns(Task.FromResult<DialogResult?>(result));
        _dialogs.ShowAsync<ExcludeTopicsDialog>(
                Arg.Any<string>(), Arg.Any<DialogParameters>(), Arg.Any<DialogOptions>())
            .Returns(dialogRef);
        _dialogs.ShowAsync<ClearSelectedMessagesDialog>(
                Arg.Any<string>(), Arg.Any<DialogParameters>(), Arg.Any<DialogOptions>())
            .Returns(dialogRef);
    }

    private (Task Shown, TaskCompletionSource<DialogResult?> Result) DeferClearDialog()
    {
        var shown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = new TaskCompletionSource<DialogResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dialogRef = Substitute.For<IDialogReference>();
        dialogRef.Result.Returns(result.Task);
        _dialogs.ShowAsync<ClearSelectedMessagesDialog>(
                Arg.Any<string>(), Arg.Any<DialogParameters>(), Arg.Any<DialogOptions>())
            .Returns(_ =>
            {
                shown.TrySetResult();
                return dialogRef;
            });
        return (shown.Task, result);
    }

    private void StubClearThroughStore()
    {
        _manager.ClearMessagesForBranchesAsync(
                Arg.Any<IReadOnlyList<TopicBranchTarget>>(), Arg.Any<TopicExcludeConnectionToken>())
            .Returns(call => Task.FromResult(new TopicBranchClearResult(true,
                _fixture.Adapter.Store.ClearMessagesForBranches(
                    call.Arg<IReadOnlyList<TopicBranchTarget>>()))));
    }

    private static MqttMessage Message(string payload, int second) =>
        new(payload, "unused", false, MQTTnet.Protocol.MqttQualityOfServiceLevel.AtMostOnce)
        {
            Topic = null,
            DateTimeReceived = DateTime.UnixEpoch.AddSeconds(second)
        };

    private void AuthorizeAsOperator() =>
        AuthorizationContext.SetAuthorized("testuser").SetRoles(AppRoles.Operator);
}
