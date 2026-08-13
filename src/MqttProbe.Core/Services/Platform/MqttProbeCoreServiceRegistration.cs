using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MqttProbe.Core.Models.Plugins;
using MqttProbe.Core.Services.Chart;
using MqttProbe.Core.Services.Configuration;
using MqttProbe.Core.Services.Emulation;
using MqttProbe.Core.Services.Metrics;
using MqttProbe.Core.Services.Mqtt;
using MqttProbe.Core.Services.Plugins;
using MqttProbe.Core.Services.Plugins.Loading;
using MqttProbe.Core.Services.Plugins.Packaging;
using MqttProbe.Core.Services.Plugins.Pipeline;
using MqttProbe.Core.Services.Plugins.Registry;
using MqttProbe.Core.Services.Security;
using MqttProbe.Core.Services.Sparkplug;

namespace MqttProbe.Core.Services.Platform;

public enum HostSessionModel
{
    PerCircuit,
    SingleSession
}

public static class MqttProbeCoreServiceRegistration
{
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

        return services;
    }

    // One instance per facet, shared through every interface it implements: the facets own
    // change events, so a second instance would silently drop subscribers.
    public static IServiceCollection AddMqttProbeSettings(
        this IServiceCollection services, string configPath, bool isMobile = false)
    {
        services.AddSingleton(_ => new SettingsDocument(configPath));
        services.AddSingleton<ISettingsDocument>(sp => sp.GetRequiredService<SettingsDocument>());

        // GetService, not GetRequiredService: not every host registers ISecretStorage or
        // ICertificateAssetStore, and ConnectionSecrets treats either as optional.
        services.AddSingleton(sp => new ConnectionSecrets(
            sp.GetService<ISecretStorage>(), sp.GetService<ILogger<ConnectionSecrets>>()));

        services.AddSingleton<ISettingsLoader>(sp => new SettingsLoader(
            sp.GetRequiredService<SettingsDocument>(),
            sp.GetRequiredService<ConnectionSecrets>(),
            isMobile,
            sp.GetService<ILogger<SettingsLoader>>()));

        services.AddSingleton<IConnectionSettings>(sp => new ConnectionSettings(
            sp.GetRequiredService<ISettingsDocument>(),
            sp.GetRequiredService<ConnectionSecrets>(),
            sp.GetService<ICertificateAssetStore>()));

        services.AddSingleton<IChartSettings>(sp =>
            new ChartSettings(sp.GetRequiredService<ISettingsDocument>()));
        services.AddSingleton<IEmulatorSettings>(sp =>
            new EmulatorSettings(sp.GetRequiredService<ISettingsDocument>()));

        services.AddSingleton(sp => new PreferenceSettings(sp.GetRequiredService<ISettingsDocument>()));
        services.AddSingleton<IUiSettings>(sp => sp.GetRequiredService<PreferenceSettings>());
        services.AddSingleton<IPerformanceSettings>(sp => sp.GetRequiredService<PreferenceSettings>());
        services.AddSingleton<IAuthSettings>(sp => sp.GetRequiredService<PreferenceSettings>());
        services.AddSingleton<ISparkplugSettings>(sp => sp.GetRequiredService<PreferenceSettings>());

        return services;
    }

    public static IServiceCollection AddMqttProbeChartData(this IServiceCollection services)
    {
        services.AddSingleton<IJsonFieldExtractor, JsonFieldExtractor>();
        services.AddSingleton<IChartFieldRegistry, ChartFieldRegistry>();
        services.AddScoped<IChartDataService, ChartDataService>();
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
        services.AddSingleton<IFormatDisplayNames, FormatDisplayNames>();
        return services;
    }

    public static IServiceCollection AddMqttProbeSparkplugTopology(
        this IServiceCollection services, HostSessionModel sessionModel)
    {
        var session = sessionModel == HostSessionModel.PerCircuit
            ? ServiceLifetime.Scoped
            : ServiceLifetime.Singleton;

        services.Add(new ServiceDescriptor(typeof(ISparkplugTopologyService),
            _ => new SparkplugTopologyService(), session));
        services.Add(new ServiceDescriptor(typeof(ISparkplugCommandService),
            sp => new SparkplugCommandService(
                sp.GetRequiredService<IMqttManagedClient>(),
                sp.GetRequiredService<ILogger<SparkplugCommandService>>(),
                sp.GetRequiredService<ISparkplugSettings>(),
                sp.GetRequiredService<ISparkplugTopologyService>()), session));
        return services;
    }
}
