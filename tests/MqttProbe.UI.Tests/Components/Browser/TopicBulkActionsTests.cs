using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using MqttProbe.Core;
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
        selection.Enter([Alpha, Beta]);
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
        captured["Topics"].Should().BeEquivalentTo(new[] { Alpha, Beta });
        await _excludes.DidNotReceive().AddMany(Arg.Any<IReadOnlyList<string>>());
    }

    [Test]
    public async Task Exclude_ReportsPerPathOutcomes_AndLeavesOnlyTheUnaddedPathChecked()
    {
        AuthorizeAsOperator();
        _fixture.AddTopics((Alpha, 1), (Beta, 1), (Gamma, 1));
        var selection = new TopicBulkSelection(_fixture.Adapter);
        selection.Enter([Alpha, Beta, Gamma]);
        _excludes.AddMany(Arg.Any<IReadOnlyList<string>>()).Returns(new TopicExcludeBatchResult([
            new TopicExcludeAddResult(Alpha, TopicExcludeAddOutcome.Added),
            new TopicExcludeAddResult(Beta, TopicExcludeAddOutcome.AlreadyExcluded),
            new TopicExcludeAddResult(Gamma, TopicExcludeAddOutcome.LimitReached)
        ]));
        ReturnDialog(DialogResult.Ok(ImmutableArray.Create(Alpha, Beta, Gamma)));
        var cut = Render<TopicBulkToolbar>(parameters =>
            parameters.AddCascadingValue(selection));

        cut.Find(".topic-bulk-toolbar__exclude").Click();
        await cut.InvokeAsync(() => Task.CompletedTask);

        await _excludes.Received(1).AddMany(Arg.Is<IReadOnlyList<string>>(topics =>
            topics.SequenceEqual(new[] { Alpha, Beta, Gamma })));
        cut.WaitForAssertion(() =>
        {
            var report = cut.Find(".topic-bulk-outcome").TextContent;
            report.Should().Contain($"Excluded (1): {Alpha}");
            report.Should().Contain($"Already excluded (1): {Beta}");
            report.Should().Contain($"Not added (1): {Gamma}: limit reached");
        });
        selection.Paths.Should().Equal(new[] { Gamma }, "only a path that was not added stays selected");
    }

    [Test]
    public async Task ClearMessages_UsesTheConfirmedPaths_AndKeepsTheSelectionAtZero()
    {
        _fixture.AddTopics((Alpha, 2), (Beta, 1), (Other, 1));
        var selection = new TopicBulkSelection(_fixture.Adapter);
        selection.Enter([Alpha, Beta]);
        var pinned = selection.Paths;
        ReturnDialog(DialogResult.Ok(pinned));
        _manager.ClearMessagesForTopicsAsync(Arg.Any<IReadOnlyList<string>>())
            .Returns(call => Task.FromResult(
                _fixture.Adapter.Store.ClearMessagesForTopics(call.Arg<IReadOnlyList<string>>())));
        var cut = Render<TopicBulkToolbar>(parameters =>
            parameters.AddCascadingValue(selection));

        cut.Find(".topic-bulk-toolbar__clear-messages").Click();
        await cut.InvokeAsync(() => Task.CompletedTask);

        await _manager.Received(1).ClearMessagesForTopicsAsync(Arg.Is<IReadOnlyList<string>>(topics =>
            topics.SequenceEqual(pinned)));
        cut.WaitForAssertion(() =>
        {
            var report = cut.Find(".topic-bulk-outcome").TextContent;
            report.Should().Contain("3 stored messages cleared from 2 topics");
            report.Should().Contain("Selected topics stay selected");
        });

        var state = _fixture.Adapter.Store.GetSelectedTopicState();
        state.Paths.Should().Equal(pinned);
        state.MessageCount.Should().Be(0);
        var roots = _fixture.Adapter.Store.GetTopicTreeSnapshot().Roots;
        TopicTreeProjection.RepresentsRealPath(roots, groupSparkplugTopics: false, Alpha).Should().BeTrue();
        TopicTreeProjection.RepresentsRealPath(roots, groupSparkplugTopics: false, Beta).Should().BeTrue();

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
        selection.Enter([Alpha]);
        ReturnDialog(DialogResult.Cancel());
        var cut = Render<TopicBulkToolbar>(parameters =>
            parameters.AddCascadingValue(selection));

        cut.Find(".topic-bulk-toolbar__clear-messages").Click();
        await cut.InvokeAsync(() => Task.CompletedTask);

        await _manager.DidNotReceive().ClearMessagesForTopicsAsync(Arg.Any<IReadOnlyList<string>>());
        selection.Paths.Should().Equal(new[] { Alpha });
        _fixture.Adapter.Store.GetSelectedTopicState().MessageCount.Should().Be(2);
    }

    [Test]
    public void Toolbar_ThemeClassFollowsTheCascadingMode()
    {
        var selection = new TopicBulkSelection(_fixture.Adapter);
        selection.Enter([Alpha]);

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
        selection.Enter([Alpha, Beta]);
        var cut = Render<TopicBulkToolbar>(parameters =>
            parameters.AddCascadingValue(selection));

        var actions = cut.Find(".topic-bulk-toolbar__actions");
        actions.QuerySelectorAll(".mud-button-text").Should().BeEmpty(
            "no toolbar action is a low-contrast text link");

        var deselect = actions.QuerySelector(".topic-bulk-toolbar__clear-selection")!;
        deselect.ClassName.Should().Contain("mud-button-outlined");
        deselect.TextContent.Trim().Should().Be("Deselect all");
        deselect.GetAttribute("title").Should().Be("Uncheck all topics. Messages are kept.");

        var clear = actions.QuerySelector(".topic-bulk-toolbar__clear-messages")!;
        clear.ClassName.Should().Contain("mud-button-outlined")
            .And.Contain("mud-button-outlined-error");
        clear.TextContent.Trim().Should().Be("Clear messages");
        clear.GetAttribute("title").Should()
            .Be("Clears stored messages for the selected topics. New messages still arrive.");

        var done = actions.QuerySelector(".topic-bulk-toolbar__done")!;
        done.ClassName.Should().Contain("mud-button-filled");
        done.TextContent.Trim().Should().Be("Done");
        done.GetAttribute("title").Should().Be("Leave selection mode and clear the topic filter.");
    }

    [Test]
    public void DeselectAll_DoesNotDeleteMessages()
    {
        _fixture.AddTopics((Alpha, 2));
        var selection = new TopicBulkSelection(_fixture.Adapter);
        selection.Enter([Alpha]);
        var cut = Render<TopicBulkToolbar>(parameters =>
            parameters.AddCascadingValue(selection));

        cut.Find(".topic-bulk-toolbar__clear-selection").Click();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("0 topics selected"));
        selection.IsActive.Should().BeTrue("deselecting stays in the mode");
        _fixture.Adapter.Store.GetSelectedTopicState().Key.Should().BeNull();
        _fixture.Adapter.Store.GetSelectedTopicState().MessageCount.Should().Be(0);
        _fixture.Adapter.Store.GetTopicTreeSnapshot().Roots.Should().NotBeEmpty(
            "the topics and their stored messages are kept");
        _manager.DidNotReceive().ClearMessagesForTopicsAsync(Arg.Any<IReadOnlyList<string>>());
    }

    [Test]
    public void Toolbar_AppearsWhenTheSharedSelectionEntersModeAfterRender()
    {
        var selection = new TopicBulkSelection(_fixture.Adapter);
        var cut = Render<TopicBulkToolbar>(parameters =>
            parameters.AddCascadingValue(selection));

        cut.Markup.Should().NotContain("topics selected");

        selection.Enter([Alpha]);

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("1 topic selected"));
    }

    [Test]
    public async Task Done_LeavesSelectionMode_AndClearsTheUnionFilter()
    {
        _fixture.AddTopics((Alpha, 1), (Beta, 1));
        var selection = new TopicBulkSelection(_fixture.Adapter);
        selection.Enter([Alpha, Beta]);
        var cut = Render<TopicBulkToolbar>(parameters =>
            parameters.AddCascadingValue(selection));

        cut.Find(".topic-bulk-toolbar__done").Click();
        await cut.InvokeAsync(() => Task.CompletedTask);

        selection.IsActive.Should().BeFalse();
        selection.Paths.Should().BeEmpty();
        _fixture.Adapter.Store.GetSelectedTopicState().Key.Should().BeNull();
        cut.Markup.Should().NotContain("topics selected");
    }

    [Test]
    public async Task Exclude_ConfirmTargetsTheRawSpelling_NotANormalizedName()
    {
        AuthorizeAsOperator();
        var selection = new TopicBulkSelection(_fixture.Adapter);
        selection.Enter(["accept//alias"]);
        _excludes.AddMany(Arg.Any<IReadOnlyList<string>>()).Returns(new TopicExcludeBatchResult([
            new TopicExcludeAddResult("accept//alias", TopicExcludeAddOutcome.Added)
        ]));
        ReturnDialog(DialogResult.Ok(ImmutableArray.Create("accept//alias")));
        var cut = Render<TopicBulkToolbar>(parameters =>
            parameters.AddCascadingValue(selection));

        cut.Find(".topic-bulk-toolbar__exclude").Click();
        await cut.InvokeAsync(() => Task.CompletedTask);

        await _excludes.Received(1).AddMany(Arg.Is<IReadOnlyList<string>>(topics =>
            topics.SequenceEqual(new[] { "accept//alias" })));
    }

    [Test]
    public async Task Exclude_AppliedWithPurgeFailure_IsExcludedWithACleanupWarning_NotNotAdded()
    {
        AuthorizeAsOperator();
        _fixture.AddTopics((Alpha, 1), (Beta, 1), (Gamma, 1));
        var selection = new TopicBulkSelection(_fixture.Adapter);
        selection.Enter([Alpha, Beta, Gamma]);
        _excludes.AddMany(Arg.Any<IReadOnlyList<string>>()).Returns(new TopicExcludeBatchResult([
            new TopicExcludeAddResult(Alpha, TopicExcludeAddOutcome.Added),
            new TopicExcludeAddResult(Beta, TopicExcludeAddOutcome.AppliedWithPurgeFailure,
                new UserNotification(UserNotificationSeverity.Warning, "Stored history could not be cleared.")),
            new TopicExcludeAddResult(Gamma, TopicExcludeAddOutcome.Failed,
                new UserNotification(UserNotificationSeverity.Error, "Connection changed."))
        ]));
        ReturnDialog(DialogResult.Ok(ImmutableArray.Create(Alpha, Beta, Gamma)));
        var cut = Render<TopicBulkToolbar>(parameters =>
            parameters.AddCascadingValue(selection));

        cut.Find(".topic-bulk-toolbar__exclude").Click();
        await cut.InvokeAsync(() => Task.CompletedTask);

        cut.WaitForAssertion(() =>
        {
            var report = cut.Find(".topic-bulk-outcome").TextContent;
            report.Should().Contain($"Excluded (1): {Alpha}");
            report.Should().Contain(
                $"Excluded, history cleanup incomplete (1): {Beta}: Stored history could not be cleared.");
            report.Should().Contain($"Not added (1): {Gamma}: Connection changed.");
            report.Should().NotContain(
                $"Not added (1): {Beta}", "an applied exclusion is never reported as not added");
        });
        selection.Paths.Should().Equal(new[] { Gamma }, "AppliedTopics reconciles the applied paths");
        _snackbar.Received(1).Add(
            Arg.Is<string>(message => message.Contains("Excluded 2 of 3 topics")),
            Severity.Warning, Arg.Any<Action<SnackbarOptions>?>(), Arg.Any<string?>());
    }

    [Test]
    public async Task Exclude_PurgeFailureOnly_HasNoNotAddedLine()
    {
        AuthorizeAsOperator();
        _fixture.AddTopics((Alpha, 1));
        var selection = new TopicBulkSelection(_fixture.Adapter);
        selection.Enter([Alpha]);
        _excludes.AddMany(Arg.Any<IReadOnlyList<string>>()).Returns(new TopicExcludeBatchResult([
            new TopicExcludeAddResult(Alpha, TopicExcludeAddOutcome.AppliedWithPurgeFailure,
                new UserNotification(UserNotificationSeverity.Warning, "History stayed in the buffer."))
        ]));
        ReturnDialog(DialogResult.Ok(ImmutableArray.Create(Alpha)));
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
        selection.Paths.Should().BeEmpty();
        _snackbar.Received(1).Add(
            Arg.Is<string>(message => message.Contains("Stored history cleanup did not finish")),
            Severity.Warning, Arg.Any<Action<SnackbarOptions>?>(), Arg.Any<string?>());
    }

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

