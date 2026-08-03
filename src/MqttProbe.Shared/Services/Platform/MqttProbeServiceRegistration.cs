using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MqttProbe.Components.Layout;
using MqttProbe.Models.Plugins;
using MqttProbe.Services.Chart;
using MqttProbe.Services.Configuration;
using MqttProbe.Services.Emulation;
using MqttProbe.Services.Metrics;
using MqttProbe.Services.Mqtt;
using MqttProbe.Services.Plugins;
using MqttProbe.Services.Plugins.Loading;
using MqttProbe.Services.Plugins.Packaging;
using MqttProbe.Services.Plugins.Pipeline;
using MqttProbe.Services.Security;
using MqttProbe.Services.Sparkplug;
using MudBlazor;
using MudBlazor.Services;

namespace MqttProbe.Services.Platform;

public enum HostSessionModel
{
    PerCircuit,
    SingleSession
}

public static class MqttProbeServiceRegistration
{
    public static IServiceCollection AddMqttProbeMud(this IServiceCollection services) =>
        services.AddMudServices(config =>
        {
            config.SnackbarConfiguration.PositionClass = Defaults.Classes.Position.TopCenter;
            config.SnackbarConfiguration.RequireInteraction = false;
            config.SnackbarConfiguration.PreventDuplicates = true;
            config.SnackbarConfiguration.NewestOnTop = false;
            config.SnackbarConfiguration.ShowCloseIcon = true;
            config.SnackbarConfiguration.VisibleStateDuration = 3000;
            config.SnackbarConfiguration.HideTransitionDuration = 500;
            config.SnackbarConfiguration.ShowTransitionDuration = 500;
            config.SnackbarConfiguration.SnackbarVariant = Variant.Filled;
        });

    public static IServiceCollection AddMqttProbeCore(
        this IServiceCollection services, HostSessionModel sessionModel)
    {
        var session = sessionModel == HostSessionModel.PerCircuit
            ? ServiceLifetime.Scoped
            : ServiceLifetime.Singleton;

        services.Add(new ServiceDescriptor(typeof(IMqttManagedClient),
            sp => new MqttManagedClient(sp.GetService<ILogger<MqttManagedClient>>()), session));
        services.Add(new ServiceDescriptor(typeof(ISessionState), typeof(SessionState), session));
        services.Add(new ServiceDescriptor(typeof(IEmulationService),
            sp => new EmulationService(
                sp.GetRequiredService<IEmulatorSettings>(),
                sp.GetRequiredService<ISparkplugNodeFactory>(),
                sp.GetRequiredService<ISessionState>(),
                sp.GetRequiredService<IMqttManagedClient>(),
                sp.GetRequiredService<IUxMetricsService>(),
                sp.GetRequiredService<ICertificateAssetStore>(),
                sp.GetRequiredService<ICertificateSessionQuarantine>(),
                sp.GetRequiredService<PayloadPipeline>(),
                sp.GetRequiredService<ILogger<EmulationService>>(),
                sp.GetRequiredService<IAppHealthMetricsCollector>()), session));
        services.Add(new ServiceDescriptor(
            typeof(IMessageStoreManager), typeof(MessageStoreManager), session));
        services.Add(new ServiceDescriptor(typeof(IMqttOptionsBuilder),
            sp => new MqttOptionsBuilder(sp.GetRequiredService<ICertificateAssetStore>()), session));
        services.Add(new ServiceDescriptor(
            typeof(IConnectionSessionLifecycle), typeof(ConnectionSessionLifecycle), session));
        services.Add(new ServiceDescriptor(
            typeof(IUxMetricsService), typeof(UxMetricsService), session));

        // Scoped in every host, including the single-session ones. Preserved as-is rather
        // than folded into the session lifetime above, which would be a behaviour change.
        services.AddScoped<ISubscriptionManager, SubscriptionManager>();
        services.AddScoped<IBrokerStateResetCoordinator, BrokerStateResetCoordinator>();

        services.AddSingleton<ICertificateSessionQuarantine, CertificateSessionQuarantine>();
        services.AddSingleton<IAppHealthMetricsCollector, AppHealthMetricsCollector>();
        services.AddSingleton<ISparkplugNodeFactory, SparkplugNodeFactory>();

        // Facets of the same ISettingsStore singleton, so a consumer can depend on the slice
        // it actually uses. Resolved lazily rather than captured, so a test that registers a
        // substitute ISettingsStore after this call still wins for every facet.
        services.AddSingleton<IConnectionSettings>(sp => sp.GetRequiredService<ISettingsStore>());
        services.AddSingleton<IChartSettings>(sp => sp.GetRequiredService<ISettingsStore>());
        services.AddSingleton<IEmulatorSettings>(sp => sp.GetRequiredService<ISettingsStore>());
        services.AddSingleton<IUiSettings>(sp => sp.GetRequiredService<ISettingsStore>());
        services.AddSingleton<IPerformanceSettings>(sp => sp.GetRequiredService<ISettingsStore>());
        services.AddSingleton<IAuthSettings>(sp => sp.GetRequiredService<ISettingsStore>());
        return services;
    }

    public static IServiceCollection AddMqttProbeCharts(this IServiceCollection services)
    {
        services.AddSingleton<IJsonFieldExtractor, JsonFieldExtractor>();
        services.AddSingleton<IChartFieldRegistry, ChartFieldRegistry>();
        services.AddScoped<IChartDataService, ChartDataService>();
        services.AddScoped<IThemes, Themes>();
        return services;
    }

    public static IServiceCollection AddMqttProbePlugins(this IServiceCollection services)
    {
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<PluginConfig>>().Value);
        services.AddSingleton<PluginArchiveLimits>();
        services.AddSingleton<PluginAssemblyCache>();
        services.AddSingleton<PluginInstallSession>();
        services.AddSingleton<PluginPackageInstaller>();
        services.AddSingleton<PluginInventoryService>();
        services.AddSingleton<PluginReloadService>();
        services.AddSingleton(sp =>
        {
            var config = sp.GetRequiredService<PluginConfig>();
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();

            // Must run before BuildPluginRegistry: it deletes/moves plugin directories that
            // PluginLoader or ProtobufSchemaFolderLoader would otherwise hold open once loaded.
            PluginPendingOperations.Apply(
                config.PluginFolders, loggerFactory.CreateLogger(typeof(PluginPendingOperations).FullName!));

            return MqttProbePluginStartup.BuildPluginRegistry(
                config, loggerFactory, sp.GetRequiredService<PluginAssemblyCache>());
        });
        services.AddSingleton<PayloadPipeline>();
        return services;
    }

    public static IServiceCollection AddMqttProbeSparkplugTopology(
        this IServiceCollection services, HostSessionModel sessionModel)
    {
        var session = sessionModel == HostSessionModel.PerCircuit
            ? ServiceLifetime.Scoped
            : ServiceLifetime.Singleton;

        services.Add(new ServiceDescriptor(typeof(ISparkplugTopologyService),
            sp => new SparkplugTopologyService(
                sp.GetRequiredService<IMqttManagedClient>(),
                sp.GetRequiredService<ILogger<SparkplugTopologyService>>(),
                sp.GetRequiredService<IUiSettings>()), session));
        return services;
    }
}
