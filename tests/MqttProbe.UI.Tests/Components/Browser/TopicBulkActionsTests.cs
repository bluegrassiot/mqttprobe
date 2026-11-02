using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using MqttProbe.Core;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Services.Mqtt;
using MqttProbe.Core.Services.Security;
using MqttProbe.UI.Tests.TestHelpers;
using MudBlazor;

namespace MqttProbe.UI.Tests.Components.Browser;

[TestFixture]
public sealed class TopicBulkActionsTests : BunitTestContext
{
    private const string Alpha = "sensors/alpha";
    private const string Beta = "sensors/beta";
    private const string Gamma = "sensors/gamma";
    private const string Other = "sensors/other";

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
        Services.AddSingleton(_dialogs);
        Services.AddSingleton(_snackbar);
        Services.AddSingleton(_excludes);
        Services.AddSingleton(_manager);
        Services.AddSingleton(Substitute.For<ISubscriptionManager>());
        _excludes.TopicExcludes.Returns(Array.Empty<string>());
    }

    [Test]
    public void TargetList_ShowsEveryFullPath()
    {
        var paths = ImmutableArray.Create("a/very/long/topic/that/needs/wrapping", Beta);

        var cut = Render<TopicTargetList>(parameters => parameters
            .Add(component => component.Topics, paths));

        cut.FindAll(".topic-target-list__item").Should().HaveCount(2);
        cut.Markup.Should().Contain("a/very/long/topic/that/needs/wrapping");
        cut.Markup.Should().Contain(Beta);
    }

    [Test]
    public async Task Exclude_OpensConfirmationWithEveryExactFullPath()
    {
        AuthorizeAsOperator();
        _fixture.AddTopics((Alpha, 1), (Beta, 1));
        var selection = new TopicBulkSelection(_fixture.Adapter);
        selection.SelectAll([Raw(Alpha), Raw(Beta)]);
        DialogParameters? captured = null;
        var dialogRef = Substitute.For<IDialogReference>();
        dialogRef.Result.Returns(Task.FromResult<DialogResult?>(DialogResult.Cancel()));
        _dialogs.ShowAsync<ExcludeTopicsDialog>(
                Arg.Any<string>(), Arg.Do<DialogParameters>(parameters => captured = parameters),
                Arg.Any<DialogOptions>())
            .Returns(dialogRef);
        var cut = Render<TopicBulkToolbar>(parameters =>
            parameters.AddCascadingValue(selection));

        cut.Find(".topic-bulk-toolbar__exclude").Click();
        await cut.InvokeAsync(() => Task.CompletedTask);

        captured.Should().NotBeNull();
        captured["Topics"].Should().BeEquivalentTo(new[] { Raw(Alpha), Raw(Beta) });
        await _excludes.DidNotReceive().AddBranches(
            Arg.Any<IReadOnlyList<TopicBranchTarget>>(), Arg.Any<TopicExcludeConnectionToken>());
    }

    [Test]
    public async Task Exclude_ReportsPerPathOutcomes_AndLeavesOnlyTheUnaddedPathChecked()
    {
        AuthorizeAsOperator();
        _fixture.AddTopics((Alpha, 1), (Beta, 1), (Gamma, 1));
        var selection = new TopicBulkSelection(_fixture.Adapter);
        selection.SelectAll([Raw(Alpha), Raw(Beta), Raw(Gamma)]);
        _excludes.AddBranches(Arg.Any<IReadOnlyList<TopicBranchTarget>>(), Arg.Any<TopicExcludeConnectionToken>())
            .Returns(new TopicBranchExcludeBatchResult([
                new TopicBranchExcludeResult(Raw(Alpha), TopicExcludeAddOutcome.Added),
                new TopicBranchExcludeResult(Raw(Beta), TopicExcludeAddOutcome.AlreadyExcluded),
                new TopicBranchExcludeResult(Raw(Gamma), TopicExcludeAddOutcome.LimitReached)
            ]));
        ReturnDialog(DialogResult.Ok(ImmutableArray.Create(Raw(Alpha), Raw(Beta), Raw(Gamma))));
        var cut = Render<TopicBulkToolbar>(parameters =>
            parameters.AddCascadingValue(selection));

        cut.Find(".topic-bulk-toolbar__exclude").Click();
        await cut.InvokeAsync(() => Task.CompletedTask);

        await _excludes.Received(1).AddBranches(
            Arg.Is<IReadOnlyList<TopicBranchTarget>>(targets =>
                targets.SequenceEqual(new[] { Raw(Alpha), Raw(Beta), Raw(Gamma) })),
            Arg.Any<TopicExcludeConnectionToken>());
        cut.WaitForAssertion(() =>
        {
            var report = cut.Find(".topic-bulk-outcome").TextContent;
            report.Should().Contain($"Excluded (1): \"{Alpha}\"");
            report.Should().Contain($"Already excluded (1): \"{Beta}\"");
            report.Should().Contain($"Not added (1): \"{Gamma}\": limit reached");
        });
        selection.Branches.Select(branch => branch.Path).Should().Equal(new[] { Gamma }, "only a path that was not added stays selected");
    }

    [Test]
    public async Task ClearMessages_UsesTheConfirmedBranches_AndPrunesEmptyRows()
    {
        _fixture.AddTopics((Alpha, 2), (Beta, 1), (Other, 1));
        var selection = new TopicBulkSelection(_fixture.Adapter);
        selection.SelectAll([Raw(Alpha), Raw(Beta)]);
        var pinned = selection.Branches;
        ReturnDialog(DialogResult.Ok(pinned));
        _manager.ClearMessagesForBranchesAsync(
                Arg.Any<IReadOnlyList<TopicBranchTarget>>(), Arg.Any<TopicExcludeConnectionToken>())
            .Returns(call => Task.FromResult(new TopicBranchClearResult(true,
                _fixture.Adapter.Store.ClearMessagesForBranches(
                    call.Arg<IReadOnlyList<TopicBranchTarget>>()))));
        var cut = Render<TopicBulkToolbar>(parameters =>
            parameters.AddCascadingValue(selection));

        cut.Find(".topic-bulk-toolbar__clear-messages").Click();
        await cut.InvokeAsync(() => Task.CompletedTask);

        await _manager.Received(1).ClearMessagesForBranchesAsync(
            Arg.Is<IReadOnlyList<TopicBranchTarget>>(targets => targets.SequenceEqual(pinned)),
            Arg.Any<TopicExcludeConnectionToken>());
        cut.WaitForAssertion(() =>
        {
            var report = cut.Find(".topic-bulk-outcome").TextContent;
            report.Should().Contain("3 stored messages cleared from 2 topics");
            report.Should().NotContain("Topics left with no messages are removed from the tree.");
        });

        var state = _fixture.Adapter.Store.GetSelectedTopicState();
        state.Branches.Should().Equal(pinned);
        state.MessageCount.Should().Be(0);
        var roots = _fixture.Adapter.Store.GetTopicTreeSnapshot().Roots;
        TopicTreeProjection.RepresentsRealPath(roots, groupSparkplugTopics: false, Alpha).Should().BeFalse();
        TopicTreeProjection.RepresentsRealPath(roots, groupSparkplugTopics: false, Beta).Should().BeFalse();

        _fixture.Adapter.Store.SelectTopic(Other);
        _fixture.Adapter.Store.GetSelectedTopicState().MessageCount.Should().Be(1,
            "messages outside the selection are untouched");
    }

    [Test]
    public void TheExcludeEditor_ShowsAPathTheBulkActionPersisted()
    {
        _excludes.TopicExcludes.Returns(new[] { Alpha });

        var cut = Render<Subscriptions>();

        cut.Markup.Should().Contain(Alpha);
    }

    [Test]
    public async Task ClearMessages_Cancelled_DoesNotClearOrChangeTheSelection()
    {
        _fixture.AddTopics((Alpha, 2));
        var selection = new TopicBulkSelection(_fixture.Adapter);
        selection.SelectAll([Raw(Alpha)]);
        ReturnDialog(DialogResult.Cancel());
        var cut = Render<TopicBulkToolbar>(parameters =>
            parameters.AddCascadingValue(selection));

        cut.Find(".topic-bulk-toolbar__clear-messages").Click();
        await cut.InvokeAsync(() => Task.CompletedTask);

        await _manager.DidNotReceive().ClearMessagesForBranchesAsync(
            Arg.Any<IReadOnlyList<TopicBranchTarget>>(), Arg.Any<TopicExcludeConnectionToken>());
        selection.Branches.Select(branch => branch.Path).Should().Equal(new[] { Alpha });
        _fixture.Adapter.Store.GetSelectedTopicState().MessageCount.Should().Be(2);
    }

    [Test]
    public async Task ClearMessages_ClearsTheTargetsTheDialogConfirmed_NotALaterSelection()
    {
        _fixture.AddTopics((Alpha, 2), (Beta, 1));
        var selection = new TopicBulkSelection(_fixture.Adapter);
        selection.SelectAll([Raw(Alpha)]);
        var confirmed = selection.Branches;
        var resultSource = new TaskCompletionSource<DialogResult?>();
        var dialogRef = Substitute.For<IDialogReference>();
        dialogRef.Result.Returns(resultSource.Task);
        _dialogs.ShowAsync<ClearSelectedMessagesDialog>(
                Arg.Any<string>(), Arg.Any<DialogParameters>(), Arg.Any<DialogOptions>())
            .Returns(dialogRef);
        _manager.ClearMessagesForBranchesAsync(
                Arg.Any<IReadOnlyList<TopicBranchTarget>>(), Arg.Any<TopicExcludeConnectionToken>())
            .Returns(call => Task.FromResult(new TopicBranchClearResult(true,
                _fixture.Adapter.Store.ClearMessagesForBranches(
                    call.Arg<IReadOnlyList<TopicBranchTarget>>()))));
        var cut = Render<TopicBulkToolbar>(parameters =>
            parameters.AddCascadingValue(selection));

        cut.Find(".topic-bulk-toolbar__clear-messages").Click();
        selection.SelectAll([Raw(Alpha), Raw(Beta)]);
        resultSource.SetResult(DialogResult.Ok(confirmed));
        await cut.InvokeAsync(() => Task.CompletedTask);

        await _manager.Received(1).ClearMessagesForBranchesAsync(
            Arg.Is<IReadOnlyList<TopicBranchTarget>>(targets => targets.SequenceEqual(confirmed)),
            Arg.Any<TopicExcludeConnectionToken>());
        var remainingState = _fixture.Adapter.Store.GetSelectedTopicState();
        remainingState.MessageCount.Should().Be(1,
            "only the confirmed branch was cleared, so the branch added afterwards keeps its message");
        _fixture.Adapter.Store.GetSelectedMessages(remainingState.Token, 10)!.Messages
            .Should().ContainSingle().Which.Topic.Should().Be(Beta);
    }

    [Test]
    public async Task ClearMessages_WhenTheStoreRejectsTheToken_ReportsNothingWasCleared()
    {
        _fixture.AddTopics((Alpha, 2));
        var selection = new TopicBulkSelection(_fixture.Adapter);
        selection.SelectAll([Raw(Alpha)]);
        ReturnDialog(DialogResult.Ok(selection.Branches));
        _manager.ClearMessagesForBranchesAsync(
                Arg.Any<IReadOnlyList<TopicBranchTarget>>(), Arg.Any<TopicExcludeConnectionToken>())
            .Returns(Task.FromResult(new TopicBranchClearResult(Applied: false, ClearedMessages: 0)));
        var cut = Render<TopicBulkToolbar>(parameters =>
            parameters.AddCascadingValue(selection));

        cut.Find(".topic-bulk-toolbar__clear-messages").Click();
        await cut.InvokeAsync(() => Task.CompletedTask);

        cut.WaitForAssertion(() =>
        {
            var report = cut.Find(".topic-bulk-outcome").TextContent;
            report.Should().Contain(
                "The connection changed before the clear was confirmed. Nothing was cleared.");
        });
        selection.Branches.Select(branch => branch.Path).Should().Equal(new[] { Alpha }, "a rejected clear keeps the selection");
        _fixture.Adapter.Store.GetSelectedTopicState().MessageCount.Should().Be(2);
    }

    [Test]
    public void Toolbar_ThemeClassFollowsTheCascadingMode()
    {
        var selection = new TopicBulkSelection(_fixture.Adapter);
        selection.SelectAll([Raw(Alpha)]);

        var light = Render<TopicBulkToolbar>(parameters => parameters
            .AddCascadingValue("IsDarkMode", false)
            .AddCascadingValue(selection));
        light.Find(".topic-bulk-toolbar").ClassName.Should().Contain("topic-bulk-toolbar--light");

        var dark = Render<TopicBulkToolbar>(parameters => parameters
            .AddCascadingValue("IsDarkMode", true)
            .AddCascadingValue(selection));
        dark.Find(".topic-bulk-toolbar").ClassName.Should().Contain("topic-bulk-toolbar--dark");
    }

    [Test]
    public void ToolbarActions_AreRealButtons_WithPlainLabelsAndHelpers()
    {
        _fixture.AddTopics((Alpha, 1), (Beta, 1));
        var selection = new TopicBulkSelection(_fixture.Adapter);
        selection.SelectAll([Raw(Alpha), Raw(Beta)]);
        var cut = Render<TopicBulkToolbar>(parameters =>
            parameters.AddCascadingValue(selection));

        var actions = cut.Find(".topic-bulk-toolbar__actions");
        actions.QuerySelectorAll(".mud-button-text").Should().BeEmpty(
            "no toolbar action is a low-contrast text link");

        var clear = actions.QuerySelector(".topic-bulk-toolbar__clear-messages")!;
        clear.ClassName.Should().Contain("mud-button-outlined")
            .And.Contain("mud-button-outlined-error");
        clear.TextContent.Trim().Should().Be("Clear messages");
        clear.GetAttribute("title").Should()
            .Be("Clears stored messages for the selected topics. New messages still arrive.");

        actions.QuerySelectorAll(".topic-bulk-toolbar__clear-selection").Should().BeEmpty(
            "the global checkbox clears the selection");
        actions.QuerySelectorAll(".topic-bulk-toolbar__done").Should().BeEmpty(
            "there is no selection mode to leave");
    }

    [Test]
    public void ClearingTheSelection_DoesNotDeleteMessages()
    {
        _fixture.AddTopics((Alpha, 2));
        var selection = new TopicBulkSelection(_fixture.Adapter);
        selection.SelectAll([Raw(Alpha)]);
        var cut = Render<TopicBulkToolbar>(parameters =>
            parameters.AddCascadingValue(selection));

        selection.Clear();

        cut.WaitForAssertion(() => cut.Markup.Should().NotContain("topics selected"));
        _fixture.Adapter.Store.GetSelectedTopicState().Key.Should().BeNull();
        _fixture.Adapter.Store.GetSelectedTopicState().MessageCount.Should().Be(0);
        _fixture.Adapter.Store.GetTopicTreeSnapshot().Roots.Should().NotBeEmpty(
            "the topics and their stored messages are kept");
        _manager.DidNotReceive().ClearMessagesForBranchesAsync(
            Arg.Any<IReadOnlyList<TopicBranchTarget>>(), Arg.Any<TopicExcludeConnectionToken>());
    }

    [Test]
    public void Toolbar_StaysHiddenUntilSomethingIsSelected()
    {
        var selection = new TopicBulkSelection(_fixture.Adapter);
        var cut = Render<TopicBulkToolbar>(parameters =>
            parameters.AddCascadingValue(selection));

        cut.Markup.Should().NotContain("topics selected");

        selection.SelectAll([Raw(Alpha)]);

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("1 topic selected"));
    }

    [Test]
    public void Toolbar_NamesTheWholeTreeWhenEveryRootIsSelected()
    {
        _fixture.AddTopics((Alpha, 1), (Beta, 1));
        var selection = new TopicBulkSelection(_fixture.Adapter) { CoversWholeTree = true };
        selection.SelectAll([Raw(Alpha), Raw(Beta)]);
        var cut = Render<TopicBulkToolbar>(parameters =>
            parameters.AddCascadingValue(selection));

        cut.Find(".topic-bulk-toolbar__count").TextContent.Should().Be("All topics selected",
            "a count of root branches would understate the checked rows");
    }

    [Test]
    public async Task Exclude_ConfirmTargetsTheRawSpelling_NotANormalizedName()
    {
        AuthorizeAsOperator();
        var selection = new TopicBulkSelection(_fixture.Adapter);
        selection.SelectAll([Raw("accept//alias")]);
        _excludes.AddBranches(Arg.Any<IReadOnlyList<TopicBranchTarget>>(), Arg.Any<TopicExcludeConnectionToken>())
            .Returns(new TopicBranchExcludeBatchResult([
                new TopicBranchExcludeResult(Raw("accept//alias"), TopicExcludeAddOutcome.Added)
            ]));
        ReturnDialog(DialogResult.Ok(ImmutableArray.Create(Raw("accept//alias"))));
        var cut = Render<TopicBulkToolbar>(parameters =>
            parameters.AddCascadingValue(selection));

        cut.Find(".topic-bulk-toolbar__exclude").Click();
        await cut.InvokeAsync(() => Task.CompletedTask);

        await _excludes.Received(1).AddBranches(
            Arg.Is<IReadOnlyList<TopicBranchTarget>>(targets =>
                targets.SequenceEqual(new[] { Raw("accept//alias") })),
            Arg.Any<TopicExcludeConnectionToken>());
    }

    [Test]
    public async Task Exclude_AppliedWithPurgeFailure_IsExcludedWithACleanupWarning_NotNotAdded()
    {
        AuthorizeAsOperator();
        _fixture.AddTopics((Alpha, 1), (Beta, 1), (Gamma, 1));
        var selection = new TopicBulkSelection(_fixture.Adapter);
        selection.SelectAll([Raw(Alpha), Raw(Beta), Raw(Gamma)]);
        _excludes.AddBranches(Arg.Any<IReadOnlyList<TopicBranchTarget>>(), Arg.Any<TopicExcludeConnectionToken>())
            .Returns(new TopicBranchExcludeBatchResult([
                new TopicBranchExcludeResult(Raw(Alpha), TopicExcludeAddOutcome.Added),
                new TopicBranchExcludeResult(Raw(Beta), TopicExcludeAddOutcome.AppliedWithPurgeFailure,
                    new UserNotification(UserNotificationSeverity.Warning, "Stored history could not be cleared.")),
                new TopicBranchExcludeResult(Raw(Gamma), TopicExcludeAddOutcome.Failed,
                    new UserNotification(UserNotificationSeverity.Error, "Connection changed."))
            ]));
        ReturnDialog(DialogResult.Ok(ImmutableArray.Create(Raw(Alpha), Raw(Beta), Raw(Gamma))));
        var cut = Render<TopicBulkToolbar>(parameters =>
            parameters.AddCascadingValue(selection));

        cut.Find(".topic-bulk-toolbar__exclude").Click();
        await cut.InvokeAsync(() => Task.CompletedTask);

        cut.WaitForAssertion(() =>
        {
            var report = cut.Find(".topic-bulk-outcome").TextContent;
            report.Should().Contain($"Excluded (1): \"{Alpha}\"");
            report.Should().Contain(
                $"Excluded, history cleanup incomplete (1): \"{Beta}\": Stored history could not be cleared.");
            report.Should().Contain($"Not added (1): \"{Gamma}\": Connection changed.");
            report.Should().NotContain(
                $"Not added (1): \"{Beta}\"", "an applied exclusion is never reported as not added");
        });
        selection.Branches.Select(branch => branch.Path).Should().Equal(new[] { Gamma }, "AppliedTopics reconciles the applied paths");
        _snackbar.Received(1).Add(
            Arg.Is<string>(message => message.Contains("Excluded 2 of 3 topics")),
            Severity.Warning, Arg.Any<Action<SnackbarOptions>?>(), Arg.Any<string?>());
    }

    [Test]
    public async Task Exclude_SavedButNotApplied_IsReportedApartFromNotAdded_AndStaysChecked()
    {
        AuthorizeAsOperator();
        _fixture.AddTopics((Alpha, 1), (Beta, 1));
        var selection = new TopicBulkSelection(_fixture.Adapter);
        selection.SelectAll([Raw(Alpha), Raw(Beta)]);
        _excludes.AddBranches(Arg.Any<IReadOnlyList<TopicBranchTarget>>(), Arg.Any<TopicExcludeConnectionToken>())
            .Returns(new TopicBranchExcludeBatchResult([
                new TopicBranchExcludeResult(Raw(Alpha), TopicExcludeAddOutcome.Added),
                new TopicBranchExcludeResult(Raw(Beta), TopicExcludeAddOutcome.SavedButNotApplied,
                    new UserNotification(UserNotificationSeverity.Warning, "Saved on the previous connection."))
            ]));
        ReturnDialog(DialogResult.Ok(ImmutableArray.Create(Raw(Alpha), Raw(Beta))));
        var cut = Render<TopicBulkToolbar>(parameters =>
            parameters.AddCascadingValue(selection));

        cut.Find(".topic-bulk-toolbar__exclude").Click();
        await cut.InvokeAsync(() => Task.CompletedTask);

        cut.WaitForAssertion(() =>
        {
            var report = cut.Find(".topic-bulk-outcome").TextContent;
            report.Should().Contain($"Excluded (1): \"{Alpha}\"");
            report.Should().Contain(
                $"Saved, not applied to this connection (1): \"{Beta}\": Saved on the previous connection.");
            report.Should().NotContain("Not added", "an exclusion that was saved is not a failed one");
        });
        selection.Branches.Select(branch => branch.Path).Should().Equal(new[] { Beta },
            "the path stays checked because its exclusion did not reach this connection");
        _snackbar.Received(1).Add(
            Arg.Is<string>(message => message.Contains("Excluded 1 of 2 topics")
                && message.Contains("1 topic saved but not applied to this connection")),
            Severity.Warning, Arg.Any<Action<SnackbarOptions>?>(), Arg.Any<string?>());
    }

    [Test]
    public async Task Exclude_PurgeFailureOnly_HasNoNotAddedLine()
    {
        AuthorizeAsOperator();
        _fixture.AddTopics((Alpha, 1));
        var selection = new TopicBulkSelection(_fixture.Adapter);
        selection.SelectAll([Raw(Alpha)]);
        _excludes.AddBranches(Arg.Any<IReadOnlyList<TopicBranchTarget>>(), Arg.Any<TopicExcludeConnectionToken>())
            .Returns(new TopicBranchExcludeBatchResult([
                new TopicBranchExcludeResult(Raw(Alpha), TopicExcludeAddOutcome.AppliedWithPurgeFailure,
                    new UserNotification(UserNotificationSeverity.Warning, "History stayed in the buffer."))
            ]));
        ReturnDialog(DialogResult.Ok(ImmutableArray.Create(Raw(Alpha))));
        var cut = Render<TopicBulkToolbar>(parameters =>
            parameters.AddCascadingValue(selection));

        cut.Find(".topic-bulk-toolbar__exclude").Click();
        await cut.InvokeAsync(() => Task.CompletedTask);

        cut.WaitForAssertion(() =>
        {
            var report = cut.Find(".topic-bulk-outcome").TextContent;
            report.Should().Contain("history cleanup incomplete (1)");
            report.Should().Contain("History stayed in the buffer.");
            report.Should().NotContain("Not added");
        });
        selection.Branches.Select(branch => branch.Path).Should().BeEmpty();
        _snackbar.Received(1).Add(
            Arg.Is<string>(message => message.Contains("Stored history cleanup did not finish")),
            Severity.Warning, Arg.Any<Action<SnackbarOptions>?>(), Arg.Any<string?>());
    }

    private static TopicBranchTarget Raw(string path) => new(TopicBranchKind.Raw, Path: path);

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

    private void AuthorizeAsOperator() =>
        AuthorizationContext.SetAuthorized("testuser").SetRoles(AppRoles.Operator);
}
