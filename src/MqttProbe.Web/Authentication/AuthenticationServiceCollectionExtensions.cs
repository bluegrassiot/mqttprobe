using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.Components.Server.Circuits;
using MqttProbe.Core.Services.Emulation;
using MqttProbe.Core.Services.Mqtt;

namespace MqttProbe.Web.Authentication;

public static class AuthenticationServiceCollectionExtensions
{
    // Registers all OIDC authentication/session services.
    // Must be called before AddMqttProbeCore/PerCircuit so the scoped gate
    // is the same instance injected into MQTT/emulation dependencies.
    public static IServiceCollection AddOidcAuthentication(
        this IServiceCollection services,
        TimeSpan sessionLifetime,
        TimeSpan? cleanupTimeout = null)
    {
        services.AddSingleton(sp =>
        {
            var timeProvider = sp.GetRequiredService<TimeProvider>();
            return new AppSessionCoordinator(timeProvider, sessionLifetime);
        });

        services.AddScoped<RevocableSessionActivityGate>();

        services.AddScoped<ISessionActivityGate>(sp =>
            sp.GetRequiredService<RevocableSessionActivityGate>());

        services.AddScoped<CircuitLease>(sp =>
        {
            var gate = sp.GetRequiredService<RevocableSessionActivityGate>();
            var teardownHandler = sp.GetRequiredService<ICircuitTeardownHandler>();
            return new CircuitLease(gate, teardownHandler);
        });

        services.AddScoped<ICircuitTeardownHandler>(sp =>
        {
            var mqttClient = sp.GetService<IMqttManagedClient>();
            var emulationService = sp.GetService<IEmulationService>();
            var authInvalidator = sp.GetRequiredService<IAuthenticationStateInvalidator>();
            var loginNotifier = sp.GetRequiredService<ILoginNavigationNotifier>();
            var logger = sp.GetService<Microsoft.Extensions.Logging.ILogger<ScopedCircuitTeardownHandler>>()
                ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ScopedCircuitTeardownHandler>.Instance;
            return new ScopedCircuitTeardownHandler(
                mqttClient, emulationService, authInvalidator, loginNotifier, logger, cleanupTimeout);
        });

        services.AddScoped<IAuthenticationStateInvalidator, BlazorAuthenticationStateInvalidator>();

        services.AddScoped<ILoginNavigationNotifier, LoginNavigationNotifier>();

        services.AddScoped<CircuitHandler>(sp =>
        {
            var coordinator = sp.GetRequiredService<AppSessionCoordinator>();
            var authStateProvider = sp.GetRequiredService<AuthenticationStateProvider>();
            var lease = sp.GetRequiredService<CircuitLease>();
            var authInvalidator = sp.GetRequiredService<IAuthenticationStateInvalidator>();
            var loginNotifier = sp.GetRequiredService<ILoginNavigationNotifier>();
            return new AppSessionCircuitHandler(
                coordinator, authStateProvider, lease, authInvalidator, loginNotifier);
        });

        return services;
    }
}

public sealed class BlazorAuthenticationStateInvalidator : IAuthenticationStateInvalidator
{
    private readonly IHostEnvironmentAuthenticationStateProvider _authStateProvider;

    public BlazorAuthenticationStateInvalidator(AuthenticationStateProvider authStateProvider)
    {
        if (authStateProvider is not IHostEnvironmentAuthenticationStateProvider hostProvider)
        {
            throw new InvalidOperationException(
                $"The registered {nameof(AuthenticationStateProvider)} ({authStateProvider.GetType().FullName}) " +
                $"does not implement {nameof(IHostEnvironmentAuthenticationStateProvider)}. " +
                "Blazor's built-in ServerAuthenticationStateProvider satisfies both interfaces; " +
                "a custom provider must also implement the host interface to support authentication state invalidation.");
        }

        _authStateProvider = hostProvider;
    }

    public void SetAnonymous()
    {
        _authStateProvider.SetAuthenticationState(Task.FromResult(
            new AuthenticationState(new System.Security.Claims.ClaimsPrincipal())));
    }
}
