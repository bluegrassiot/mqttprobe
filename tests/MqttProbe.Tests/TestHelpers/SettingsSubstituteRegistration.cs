using Microsoft.Extensions.DependencyInjection;
using MqttProbe.Services.Configuration;

namespace MqttProbe.Shared.Tests.TestHelpers;

// bUnit fixtures register only the facets their component injects. Facets that share a
// production instance (PreferenceSettings backs all three preference interfaces) may be
// registered from separate substitutes here, but they must project from one AppConfiguration
// or assertions pass for the wrong reason.
public static class SettingsSubstituteRegistration
{
    public static IServiceCollection AddUiSettings(this IServiceCollection services, IUiSettings ui) =>
        services.AddSingleton(ui);

    public static IServiceCollection AddPerformanceSettings(
        this IServiceCollection services, IPerformanceSettings performance) =>
        services.AddSingleton(performance);

    public static IServiceCollection AddAuthSettings(
        this IServiceCollection services, IAuthSettings auth) => services.AddSingleton(auth);

    public static IServiceCollection AddChartSettings(
        this IServiceCollection services, IChartSettings charts) => services.AddSingleton(charts);

    public static IServiceCollection AddEmulatorSettings(
        this IServiceCollection services, IEmulatorSettings emulators) =>
        services.AddSingleton(emulators);

    public static IServiceCollection AddConnectionSettings(
        this IServiceCollection services, IConnectionSettings connections) =>
        services.AddSingleton(connections);
}
