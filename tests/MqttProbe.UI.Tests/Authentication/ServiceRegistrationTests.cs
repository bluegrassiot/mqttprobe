using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using MqttProbe.Core.Services.Emulation;
using MqttProbe.Core.Services.Mqtt;
using MqttProbe.Web.Authentication;

namespace MqttProbe.UI.Tests.Authentication;

[TestFixture]
public class ServiceRegistrationTests
{
    private static readonly DateTimeOffset _epoch = new(2026, 8, 29, 8, 0, 0, TimeSpan.Zero);

    [Test]
    public void ScopedServices_CircuitLease_MqttManagedClient_EmulationService_ReceiveSameGate()
    {
        var services = new ServiceCollection();

        services.AddSingleton<TimeProvider>(new FakeTimeProvider(_epoch));
        services.AddScoped<IMqttManagedClient>(sp => Substitute.For<IMqttManagedClient>());
        services.AddScoped<IEmulationService>(sp => Substitute.For<IEmulationService>());
        services.AddScoped<AuthenticationStateProvider, TestBlazorAuthProvider>();

        services.AddOidcAuthentication(TimeSpan.FromHours(8));

        var serviceProvider = services.BuildServiceProvider();

        using var scope1 = serviceProvider.CreateScope();
        using var scope2 = serviceProvider.CreateScope();

        var gate1 = scope1.ServiceProvider.GetRequiredService<RevocableSessionActivityGate>();
        var lease1 = scope1.ServiceProvider.GetRequiredService<CircuitLease>();

        lease1.Gate.Should().BeSameAs(gate1);

        var gate2 = scope2.ServiceProvider.GetRequiredService<RevocableSessionActivityGate>();
        var lease2 = scope2.ServiceProvider.GetRequiredService<CircuitLease>();

        gate2.Should().NotBeSameAs(gate1);
        lease2.Gate.Should().BeSameAs(gate2);
    }

    [Test]
    public void SingletonCoordinator_IsSameAcrossScopes()
    {
        var services = new ServiceCollection();

        services.AddSingleton<TimeProvider>(new FakeTimeProvider(_epoch));
        services.AddScoped<IMqttManagedClient>(sp => Substitute.For<IMqttManagedClient>());
        services.AddScoped<IEmulationService>(sp => Substitute.For<IEmulationService>());
        services.AddScoped<AuthenticationStateProvider, TestBlazorAuthProvider>();

        services.AddOidcAuthentication(TimeSpan.FromHours(8));

        var serviceProvider = services.BuildServiceProvider();

        using var scope1 = serviceProvider.CreateScope();
        using var scope2 = serviceProvider.CreateScope();

        var coordinator1 = scope1.ServiceProvider.GetRequiredService<AppSessionCoordinator>();
        var coordinator2 = scope2.ServiceProvider.GetRequiredService<AppSessionCoordinator>();

        coordinator1.Should().BeSameAs(coordinator2);
    }

    [Test]
    public async Task AddOidcAuthentication_CoordinatorLogsThroughDiRegisteredLogger()
    {
        ILogger<AppSessionCoordinator> coordinatorLogger = Substitute.For<ILogger<AppSessionCoordinator>>();

        var services = new ServiceCollection();

        services.AddSingleton<TimeProvider>(new FakeTimeProvider(_epoch));
        services.AddSingleton(coordinatorLogger);
        services.AddScoped<IMqttManagedClient>(sp => Substitute.For<IMqttManagedClient>());
        services.AddScoped<IEmulationService>(sp => Substitute.For<IEmulationService>());
        services.AddScoped<AuthenticationStateProvider, TestBlazorAuthProvider>();

        services.AddOidcAuthentication(TimeSpan.FromHours(8));

        using var provider = services.BuildServiceProvider();

        var coordinator = provider.GetRequiredService<AppSessionCoordinator>();
        var identity = ExternalIdentity.Create("https://idp.example.com", "user-123", "John Doe");
        var record = coordinator.CreateSession(identity);

        // NullLogger swallows this, so a missing DI logger fails the assertion.
        coordinator.SessionRevoked += _ => throw new InvalidOperationException("subscriber failed");

        await coordinator.RevokeSessionAsync(record.SessionId);

        var warnings = coordinatorLogger.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(ILogger.Log)
                && Equals(call.GetArguments()[0], LogLevel.Warning))
            .Select(call => call.GetArguments()[2]?.ToString() ?? "");

        warnings.Should().Contain(message => message.Contains("SessionRevoked subscriber failed"));
    }

    [Test]
    public void ISessionActivityGate_MapsToSameScopedInstance()
    {
        var services = new ServiceCollection();

        services.AddSingleton<TimeProvider>(new FakeTimeProvider(_epoch));
        services.AddScoped<IMqttManagedClient>(sp => Substitute.For<IMqttManagedClient>());
        services.AddScoped<IEmulationService>(sp => Substitute.For<IEmulationService>());
        services.AddScoped<AuthenticationStateProvider, TestBlazorAuthProvider>();

        services.AddOidcAuthentication(TimeSpan.FromHours(8));

        var serviceProvider = services.BuildServiceProvider();

        using var scope = serviceProvider.CreateScope();

        var gate = scope.ServiceProvider.GetRequiredService<RevocableSessionActivityGate>();
        var sessionGate = scope.ServiceProvider.GetRequiredService<ISessionActivityGate>();

        sessionGate.Should().BeSameAs(gate);
    }

    [Test]
    public void CircuitHandler_ReceivesInjectedScopedLease()
    {
        var services = new ServiceCollection();

        services.AddSingleton<TimeProvider>(new FakeTimeProvider(_epoch));
        services.AddScoped<IMqttManagedClient>(sp => Substitute.For<IMqttManagedClient>());
        services.AddScoped<IEmulationService>(sp => Substitute.For<IEmulationService>());
        services.AddScoped<AuthenticationStateProvider, TestBlazorAuthProvider>();

        services.AddOidcAuthentication(TimeSpan.FromHours(8));

        var serviceProvider = services.BuildServiceProvider();

        using var scope = serviceProvider.CreateScope();

        var lease = scope.ServiceProvider.GetRequiredService<CircuitLease>();
        var handler = scope.ServiceProvider.GetRequiredService<CircuitHandler>();

        handler.Should().BeOfType<AppSessionCircuitHandler>();
    }

    [Test]
    public void AddOidcAuthentication_ResolvesInvalidator_WhenSingleAuthProviderRegistration()
    {
        var services = new ServiceCollection();

        services.AddSingleton<TimeProvider>(new FakeTimeProvider(_epoch));
        services.AddScoped<IMqttManagedClient>(sp => Substitute.For<IMqttManagedClient>());
        services.AddScoped<IEmulationService>(sp => Substitute.For<IEmulationService>());
        services.AddScoped<AuthenticationStateProvider, TestBlazorAuthProvider>();

        services.AddOidcAuthentication(TimeSpan.FromHours(8));

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var invalidator = scope.ServiceProvider.GetRequiredService<IAuthenticationStateInvalidator>();

        invalidator.Should().NotBeNull();
        invalidator.Should().BeOfType<BlazorAuthenticationStateInvalidator>();
    }

    [Test]
    public void AddOidcAuthentication_InvalidatorProvider_IsSameObjectAsAuthProvider()
    {
        var services = new ServiceCollection();

        services.AddSingleton<TimeProvider>(new FakeTimeProvider(_epoch));
        services.AddScoped<IMqttManagedClient>(sp => Substitute.For<IMqttManagedClient>());
        services.AddScoped<IEmulationService>(sp => Substitute.For<IEmulationService>());
        services.AddScoped<AuthenticationStateProvider, TestBlazorAuthProvider>();

        services.AddOidcAuthentication(TimeSpan.FromHours(8));

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var authProvider = scope.ServiceProvider.GetRequiredService<AuthenticationStateProvider>();
        var invalidator = scope.ServiceProvider.GetRequiredService<IAuthenticationStateInvalidator>();

        invalidator.Should().NotBeNull();
        authProvider.Should().BeAssignableTo<IHostEnvironmentAuthenticationStateProvider>();
    }

    [Test]
    public void BlazorAuthenticationStateInvalidator_ThrowsForIncompatibleProvider()
    {
        var act = () => new BlazorAuthenticationStateInvalidator(new IncompatibleAuthProvider());

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*does not implement IHostEnvironmentAuthenticationStateProvider*");
    }

    [Test]
    public void BlazorAuthenticationStateInvalidator_SetAnonymous_ForwardsAnonymousState()
    {
        var provider = new TestBlazorAuthProvider();
        var invalidator = new BlazorAuthenticationStateInvalidator(provider);

        invalidator.SetAnonymous();

        provider.LastSetState.Should().NotBeNull();
        var state = provider.LastSetState!.Result;
        state.User.Identities.Should().BeEmpty();
    }

    private sealed class TestBlazorAuthProvider : AuthenticationStateProvider, IHostEnvironmentAuthenticationStateProvider
    {
        private Task<AuthenticationState>? _lastSetState;

        public Task<AuthenticationState>? LastSetState => _lastSetState;

        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new System.Security.Claims.ClaimsPrincipal()));

        public void SetAuthenticationState(Task<AuthenticationState> authenticationStateTask)
        {
            _lastSetState = authenticationStateTask;
        }
    }

    private sealed class IncompatibleAuthProvider : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new System.Security.Claims.ClaimsPrincipal()));
    }
}
