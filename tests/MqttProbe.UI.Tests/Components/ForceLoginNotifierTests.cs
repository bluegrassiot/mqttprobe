using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using MqttProbe.Core.Services.Emulation;
using MqttProbe.Core.Services.Mqtt;
using MqttProbe.Web.Authentication;
using MqttProbe.Web.Components;

namespace MqttProbe.UI.Tests.Components;

[TestFixture]
public class ForceLoginNotifierTests
{
    [Test]
    public void ForceLogin_RegisteredAsInterface_NavigatesToLogin()
    {
        using var ctx = new Bunit.BunitContext();

        var notifier = new LoginNavigationNotifier();
        ctx.Services.AddSingleton<ILoginNavigationNotifier>(notifier);

        var navManager = ctx.Services.GetRequiredService<NavigationManager>();

        ctx.Render<ForceLoginNotifier>();

        navManager.NavigateTo("/some-page");

        notifier.NotifyForceLogin();

        navManager.Uri.Should().Contain("/Login");
    }

    [Test]
    public async Task Dispose_UnsubscribesFromNotifier()
    {
        using var ctx = new Bunit.BunitContext();

        var notifier = new LoginNavigationNotifier();
        ctx.Services.AddSingleton<ILoginNavigationNotifier>(notifier);

        var navManager = ctx.Services.GetRequiredService<NavigationManager>();

        ctx.Render<ForceLoginNotifier>();

        navManager.NavigateTo("/some-page");
        var uriBefore = navManager.Uri;

        await ctx.DisposeComponentsAsync();

        notifier.NotifyForceLogin();
        await ctx.Renderer.Dispatcher.InvokeAsync(() => { });

        navManager.Uri.Should().Be(uriBefore);
    }

    [Test]
    public void ProductionDi_ILoginNavigationNotifierResolvesFromAddOidcAuthentication()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(new FakeTimeProvider(DateTimeOffset.UtcNow));
        services.AddScoped<IMqttManagedClient>(
            sp => Substitute.For<IMqttManagedClient>());
        services.AddScoped<IEmulationService>(
            sp => Substitute.For<IEmulationService>());
        services.AddScoped<AuthenticationStateProvider, TestBlazorAuthProvider>();

        services.AddOidcAuthentication(TimeSpan.FromHours(8));

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var notifier = scope.ServiceProvider.GetRequiredService<ILoginNavigationNotifier>();

        notifier.Should().NotBeNull();
        notifier.Should().BeOfType<LoginNavigationNotifier>();
    }

    [Test]
    public async Task ForceLogin_FromNonRendererContext_NavigatesViaInvokeAsync()
    {
        using var ctx = new Bunit.BunitContext();

        var notifier = new LoginNavigationNotifier();
        ctx.Services.AddSingleton<ILoginNavigationNotifier>(notifier);

        var navManager = ctx.Services.GetRequiredService<NavigationManager>();

        _ = ctx.Render<ForceLoginNotifier>();

        navManager.NavigateTo("/some-page");

        await Task.Run(notifier.NotifyForceLogin);
        await ctx.Renderer.Dispatcher.InvokeAsync(() => { });

        navManager.Uri.Should().Be("http://localhost/Login");
    }

    private sealed class TestBlazorAuthProvider : AuthenticationStateProvider, IHostEnvironmentAuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new System.Security.Claims.ClaimsPrincipal()));

        public void SetAuthenticationState(Task<AuthenticationState> authenticationStateTask)
        {
        }
    }
}
