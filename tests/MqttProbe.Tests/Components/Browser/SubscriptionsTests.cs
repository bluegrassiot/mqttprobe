using Microsoft.Extensions.DependencyInjection;
using MqttProbe.Services.Mqtt;
using MqttProbe.Services.Security;
using MqttProbe.Shared.Tests.TestHelpers;
using MudBlazor;

namespace MqttProbe.Shared.Tests.Components.Browser;

[TestFixture]
public class SubscriptionsTests : BunitTestContext
{
    private ISubscriptionManager _mockSubMgr = null!;
    private ISnackbar _mockSnackbar = null!;

    [SetUp]
    public void SetupMocks()
    {
        _mockSubMgr = Substitute.For<ISubscriptionManager>();
        _mockSubMgr.Topics.Returns(new HashSet<string>());
        Services.AddSingleton(_mockSubMgr);

        _mockSnackbar = Substitute.For<ISnackbar>();
        Services.AddSingleton(_mockSnackbar);

        // Build service provider and register MudPopoverProvider (required by MudTable's rows-per-page select)
        EnsureMudProviders();
    }

    private void AuthorizeAsOperator()
    {
        AuthorizationContext.SetAuthorized("testuser").SetRoles(AppRoles.Operator);
    }
    [Test]
    public void Renders_ActiveSubscriptions_FromManager()
    {
        _mockSubMgr.Topics.Returns(new HashSet<string> { "spBv1.0/+/DDATA/#", "my/topic" });

        var cut = Render<Subscriptions>();

        var markup = cut.Markup;
        markup.Should().Contain("spBv1.0/+/DDATA/#");
        markup.Should().Contain("my/topic");
    }

    [Test]
    public async Task AddButton_CallsSubscribeAsync_WithEnteredTopic()
    {
        AuthorizeAsOperator();
        _mockSubMgr.Add(Arg.Any<string>()).Returns(Task.CompletedTask);
        var cut = Render<Subscriptions>();

        // The topic MudTextField is the LAST text input (search field comes first in the toolbar).
        // Find all text-type inputs and change the last one (the topic field).
        var topicInput = cut.FindAll("input").Last(e => e.GetAttribute("type") != "checkbox");
        topicInput.Change("sensors/#");
        cut.Find("button[title='Subscribe']").Click();

        await _mockSubMgr.Received(1).Add("sensors/#");
    }

    [Test]
    public async Task DeleteSelected_CallsUnsubscribeAsync()
    {
        AuthorizeAsOperator();
        _mockSubMgr.Topics.Returns(new HashSet<string> { "a/topic" });
        _mockSubMgr.Remove(Arg.Any<List<string>>()).Returns(Task.CompletedTask);
        var cut = Render<Subscriptions>();

        // Select the checkbox and click Remove
        cut.Find("input[type='checkbox']").Change(true);
        cut.Find("button[title='Remove']").Click();

        await _mockSubMgr.Received(1).Remove(Arg.Any<List<string>>());
    }

    [Test]
    public async Task AddButton_AwaitsDelayedAdd_DisablingButtonUntilComplete_ThenClearsTopic()
    {
        AuthorizeAsOperator();
        var pending = new TaskCompletionSource();
        _mockSubMgr.Add("sensors/#").Returns(pending.Task);
        var cut = Render<Subscriptions>();

        var topicInput = cut.FindAll("input").Last(e => e.GetAttribute("type") != "checkbox");
        topicInput.Change("sensors/#");
        cut.Find("button[title='Subscribe']").Click();

        cut.Find("button[title='Subscribe']").HasAttribute("disabled")
            .Should().BeTrue("the add button stays disabled while the Add task is in flight");

        pending.SetResult();

        cut.WaitForAssertion(() =>
        {
            cut.Find("button[title='Subscribe']").HasAttribute("disabled").Should().BeFalse();
            cut.FindAll("input").Last(e => e.GetAttribute("type") != "checkbox")
                .GetAttribute("value").Should().BeNullOrEmpty();
        });

        await _mockSubMgr.Received(1).Add("sensors/#");
    }

    [Test]
    public void AddButton_WhenAddFails_SurfacesErrorSnackbar()
    {
        AuthorizeAsOperator();
        _mockSubMgr.Add("bad/#")
            .Returns(Task.FromException(new InvalidOperationException("broker rejected")));
        var cut = Render<Subscriptions>();

        var topicInput = cut.FindAll("input").Last(e => e.GetAttribute("type") != "checkbox");
        topicInput.Change("bad/#");
        cut.Find("button[title='Subscribe']").Click();

        cut.WaitForAssertion(() =>
            _mockSnackbar.Received().Add(
                Arg.Is<string>(m => m!.Contains("Failed to subscribe")),
                Severity.Error,
                Arg.Any<Action<SnackbarOptions>?>(),
                Arg.Any<string>()));
    }

    [Test]
    public void RowTemplate_UsesMudTd_NotMudTh()
    {
        _mockSubMgr.Topics.Returns(new HashSet<string> { "some/topic" });

        var cut = Render<Subscriptions>();

        // Regression guard: row data cells must be <td>, not <th>
        cut.FindAll("td").Should().NotBeEmpty();
        cut.FindAll("th[data-label]").Should().BeEmpty();
    }
}
