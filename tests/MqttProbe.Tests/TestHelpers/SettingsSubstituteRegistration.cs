using Microsoft.Extensions.DependencyInjection;
using MqttProbe.Services.Configuration;

namespace MqttProbe.Shared.Tests.TestHelpers;

// bUnit fixtures register one ISettingsStore substitute; components inject narrow facets.
// AddSingleton(store) alone type-infers to ISettingsStore only, so the facets must be
// registered explicitly against the same instance.
public static class SettingsSubstituteRegistration
{
    public static IServiceCollection AddSettingsSubstitute(
        this IServiceCollection services, ISettingsStore store)
    {
        services.AddSingleton(store);
        services.AddSingleton<IConnectionSettings>(store);
        services.AddSingleton<IChartSettings>(store);
        services.AddSingleton<IEmulatorSettings>(store);
        services.AddSingleton<IUiSettings>(store);
        services.AddSingleton<IPerformanceSettings>(store);
        services.AddSingleton<IAuthSettings>(store);
        return services;
    }
}
