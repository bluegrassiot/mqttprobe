using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using MqttProbe.UI.Components.Pages;
using MqttProbe.UI.Tests.TestHelpers;
using MqttProbe.Web;
using MqttProbe.Web.Authentication;
using MqttProbe.Web.Components;

namespace MqttProbe.UI.Tests.Components;

[TestFixture]
public class RoutesTests : BunitTestContext
{
    [SetUp]
    public void RegisterForceLoginNotifier()
    {
        // ForceLoginNotifier (kept for session expiry) is the only service the
        // bare unauthorized tree needs; any missing shell service would mean the
        // app shell leaked back into these states.
        Services.AddSingleton<ILoginNavigationNotifier>(new LoginNavigationNotifier());
    }

    [Test]
    public void NotAuthorized_WhenAnonymous_RendersSignedOutView_BareWithoutShell_WithoutAutoNavigation()
    {
        var navigation = Services.GetRequiredService<NavigationManager>();
        var uriBefore = navigation.Uri;

        var cut = Render<Routes>();

        cut.Markup.Should().Contain("You're signed out");

        var signIn = cut.Find("a[href='/Login']");
        signIn.TextContent.Should().Be("Sign in");
        signIn.GetAttribute("data-enhance-nav").Should().Be("false");

        AssertBareWithoutShell(cut);
        cut.FindAll("a").Should().HaveCount(1);

        navigation.Uri.Should().Be(uriBefore);
    }

    [Test]
    public void NotAuthorized_WhenSignedInWithoutPermission_RendersAccessDeniedView_BareWithoutShell_WithoutAutoNavigation()
    {
        AuthorizationContext.SetAuthorized("operator");
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/change-password");
        var uriBefore = navigation.Uri;

        var cut = Render<Routes>();

        cut.Markup.Should().Contain("Access denied");
        cut.Markup.Should().NotContain("You're signed out");

        var home = cut.Find("a[href='/']");
        home.TextContent.Should().Be("Back to home");

        AssertBareWithoutShell(cut);
        cut.FindAll("a").Should().HaveCount(1);

        navigation.Uri.Should().Be(uriBefore);
    }

    [Test]
    public void Authorized_WhenPageNeedsNoAuthentication_RendersPage_NotTheUnauthorizedView()
    {
        var cut = Render<AuthRouteView>(p => p
            .Add(c => c.RouteData, new RouteData(typeof(ForceLoginNotifier), new Dictionary<string, object?>()))
            .Add(c => c.NotAuthorized, (RenderFragment<AuthenticationState>)(_ =>
                builder => builder.AddContent(0, "unauthorized-marker"))));

        cut.Markup.Should().NotContain("unauthorized-marker");
    }

    [Test]
    public void HeldPolicy_WhenRouteChangesToRestrictedPage_DoesNotInitializeOrRenderRestrictedPage()
    {
        var held = new HeldAuthorizationService();
        Services.AddSingleton<IAuthorizationService>(held);

        var gate = RenderGate(typeof(AuthorizedProbePage));
        held.CompleteAll(AuthorizationResult.Success());
        gate.WaitForAssertion(() => gate.Markup.Should().Contain("probe-page-marker"));

        // Moving to a restricted route must clear the prior grant synchronously,
        // even while the new policy evaluation is still in flight.
        gate.Render(p => p.Add(c => c.RouteData,
            new RouteData(typeof(ChangePassword), new Dictionary<string, object?>())));

        gate.Markup.Should().NotContain("probe-page-marker");
        gate.FindComponents<ChangePassword>().Should().BeEmpty();

        // A forced render during the in-flight evaluation stays fail-closed.
        gate.Render();
        gate.Markup.Should().NotContain("probe-page-marker");
        gate.FindComponents<ChangePassword>().Should().BeEmpty();

        held.CompleteAll(AuthorizationResult.Failed());
        gate.WaitForAssertion(() => gate.Markup.Should().Contain("unauthorized-anonymous"));
        gate.FindComponents<ChangePassword>().Should().BeEmpty();
    }

    [Test]
    public void StaleCompletion_AfterAuthStateChange_DoesNotOverwriteNewerState()
    {
        var held = new HeldAuthorizationService();
        Services.AddSingleton<IAuthorizationService>(held);
        AuthorizationContext.SetAuthorized("admin");

        var gate = RenderGate(typeof(AuthorizedProbePage));
        var olderCalls = held.CallCount;

        // The auth state changes while the first evaluations are still in flight.
        AuthorizationContext.SetNotAuthorized();
        held.CallCount.Should().BeGreaterThan(olderCalls);

        held.CompleteFrom(olderCalls, AuthorizationResult.Failed());
        gate.WaitForAssertion(() => gate.Markup.Should().Contain("unauthorized-anonymous"));

        // The older evaluations complete later with a grant; every one must be
        // dropped rather than overwriting the newer denial.
        var rendersBefore = gate.RenderCount;
        for (var i = 0; i < olderCalls; i++)
            held.Complete(i, AuthorizationResult.Success());

        gate.WaitForAssertion(() => gate.RenderCount.Should().BeGreaterThanOrEqualTo(rendersBefore + olderCalls));
        gate.Markup.Should().Contain("unauthorized-anonymous");
        gate.Markup.Should().NotContain("probe-page-marker");
        gate.Markup.Should().NotContain("unauthorized-authenticated");
    }

    [Test]
    public void UnknownRoute_RendersBareNotFound_WithoutShell_WithoutAutoNavigation()
    {
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/no-such-page");
        var uriBefore = navigation.Uri;

        var cut = Render<Routes>();

        cut.Markup.Should().Contain("Page not found");
        var home = cut.Find("a[href='/']");
        home.TextContent.Should().Be("Back to home");

        AssertBareWithoutShell(cut);
        cut.FindAll("a").Should().HaveCount(1);

        navigation.Uri.Should().Be(uriBefore);
    }

    private IRenderedComponent<AuthRouteView> RenderGate(Type pageType)
    {
        RenderFragment<AuthenticationState> notAuthorized = state => builder => builder.AddContent(0,
            state.User.Identity?.IsAuthenticated == true ? "unauthorized-authenticated" : "unauthorized-anonymous");

        return Render<CascadingAuthenticationState>(p => p.AddChildContent(builder =>
        {
            builder.OpenComponent<AuthRouteView>(0);
            builder.AddAttribute(1, nameof(AuthRouteView.RouteData),
                new RouteData(pageType, new Dictionary<string, object?>()));
            builder.AddAttribute(2, nameof(AuthRouteView.NotAuthorized), notAuthorized);
            builder.CloseComponent();
        })).FindComponent<AuthRouteView>();
    }

    private static void AssertBareWithoutShell(IRenderedComponent<Routes> cut)
    {
        var markup = cut.Markup;
        markup.Should().NotContain("mud-layout");
        markup.Should().NotContain("mud-appbar");
        markup.Should().NotContain("app-shell");
        markup.Should().NotContain("mud-dialog");
        markup.Should().NotContain("mud-popover-provider");
        markup.Should().NotContain("mud-snackbar");

        // The bare state is one semantic region and one link: no other
        // interactives may be reachable behind it.
        cut.Find("main").Should().NotBeNull();
        cut.FindAll("button").Should().BeEmpty();
    }

    [Authorize]
    public sealed class AuthorizedProbePage : ComponentBase
    {
        protected override void BuildRenderTree(RenderTreeBuilder builder) =>
            builder.AddContent(0, "probe-page-marker");
    }

    private sealed class HeldAuthorizationService : IAuthorizationService
    {
        private readonly List<TaskCompletionSource<AuthorizationResult>> _calls = new();

        public int CallCount => _calls.Count;

        public Task<AuthorizationResult> AuthorizeAsync(
            ClaimsPrincipal user, object? resource, IEnumerable<IAuthorizationRequirement> requirements) => Hold();

        public Task<AuthorizationResult> AuthorizeAsync(
            ClaimsPrincipal user, object? resource, string policyName) => Hold();

        public void Complete(int call, AuthorizationResult result) => _calls[call].SetResult(result);

        public void CompleteAll(AuthorizationResult result) => CompleteFrom(0, result);

        public void CompleteFrom(int start, AuthorizationResult result)
        {
            for (var i = start; i < _calls.Count; i++)
                if (!_calls[i].Task.IsCompleted)
                    _calls[i].SetResult(result);
        }

        private Task<AuthorizationResult> Hold()
        {
            var call = new TaskCompletionSource<AuthorizationResult>();
            _calls.Add(call);
            return call.Task;
        }
    }
}

// AddAuthorization cascades an authentication state into every rendered component,
// so the missing-auth-state path needs a raw BunitContext without it.
[TestFixture]
public class RoutesMissingAuthStateTests : BunitContext
{
    [Test]
    public void MissingAuthenticationState_NeverGrants_AndDoesNotCallAuthorizationService()
    {
        Services.AddAuthorizationCore();
        var held = new CountingAuthorizationService();
        Services.AddSingleton<IAuthorizationService>(held);

        var cut = Render<AuthRouteView>(p => p
            .Add(c => c.RouteData,
                new RouteData(typeof(RoutesTests.AuthorizedProbePage), new Dictionary<string, object?>()))
            .Add(c => c.NotAuthorized, (RenderFragment<AuthenticationState>)(_ =>
                builder => builder.AddContent(0, "unauthorized-anonymous"))));

        cut.Markup.Should().Contain("unauthorized-anonymous");
        cut.Markup.Should().NotContain("probe-page-marker");
        held.CallCount.Should().Be(0);
    }

    private sealed class CountingAuthorizationService : IAuthorizationService
    {
        public int CallCount { get; private set; }

        public Task<AuthorizationResult> AuthorizeAsync(
            ClaimsPrincipal user, object? resource, IEnumerable<IAuthorizationRequirement> requirements)
        {
            CallCount++;
            return Task.FromResult(AuthorizationResult.Success());
        }

        public Task<AuthorizationResult> AuthorizeAsync(
            ClaimsPrincipal user, object? resource, string policyName)
        {
            CallCount++;
            return Task.FromResult(AuthorizationResult.Success());
        }
    }
}
