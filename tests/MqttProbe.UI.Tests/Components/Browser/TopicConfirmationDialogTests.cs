using Microsoft.Extensions.DependencyInjection;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.UI.Tests.TestHelpers;
using MudBlazor;

namespace MqttProbe.UI.Tests.Components.Browser;

[TestFixture]
public sealed class TopicConfirmationDialogTests : BunitTestContext
{
    private IRenderedComponent<MudDialogProvider> _provider = null!;

    [SetUp]
    public void Setup()
    {
        EnsureMudProviders();
        _provider = Render<MudDialogProvider>();
    }

    [Test]
    public async Task ClearMessages_ShowsOnlyTheOneSentenceConfirmation()
    {
        await ShowAsync<ClearSelectedMessagesDialog>();

        _provider.Find(".topic-clear-dialog__lead").TextContent.Trim()
            .Should().Be("Clear messages from the selected topics?");
        _provider.Markup.Should().NotContain("Stored messages for these topics");
        _provider.Markup.Should().NotContain("new messages keep arriving");
        _provider.Markup.Should().NotContain("Topics left with no messages");
    }

    [Test]
    public async Task Exclude_ShowsOnlyTheOneSentenceConfirmation()
    {
        await ShowAsync<ExcludeTopicsDialog>();

        _provider.Find(".topic-target-dialog__lead").TextContent.Trim()
            .Should().Be("Exclude the selected topics?");
        _provider.FindAll(".topic-target-dialog__note").Should().BeEmpty();
        _provider.Markup.Should().NotContain("will be added");
        _provider.Markup.Should().NotContain("everything below them");
        _provider.Markup.Should().NotContain("new messages below them");
        _provider.Markup.Should().NotContain("logical scope");
    }

    private async Task ShowAsync<TDialog>() where TDialog : Microsoft.AspNetCore.Components.IComponent
    {
        var parameters = new DialogParameters
        {
            ["Topics"] = new List<TopicBranchTarget>
            {
                new(TopicBranchKind.Raw, Path: "sensors/alpha"),
                new(TopicBranchKind.Raw, Path: "sensors/beta")
            }
        };
        var dialogService = Services.GetRequiredService<IDialogService>();

        await _provider.InvokeAsync(async () =>
            await dialogService.ShowAsync<TDialog>("Confirmation", parameters));
    }
}
