using Microsoft.Extensions.DependencyInjection;
using MqttProbe.Core;
using MqttProbe.Core.Services.Platform;
using MqttProbe.UI.Components.Layout;
using MudBlazor;
using MudBlazor.Services;

namespace MqttProbe.UI.Services.Platform;

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

    public static IServiceCollection AddMqttProbeCharts(this IServiceCollection services)
    {
        services.AddMqttProbeChartData();
        services.AddScoped<IThemes, Themes>();
        return services;
    }

    public static IServiceCollection AddMqttProbeUiNotifications(this IServiceCollection services)
    {
        services.AddScoped<IUserNotifier, MudBlazorUserNotifier>();
        return services;
    }

    public static IServiceCollection AddMqttProbeUi(this IServiceCollection services)
    {
        services.AddMqttProbeMud();
        services.AddMqttProbeCharts();
        services.AddMqttProbeUiNotifications();
        return services;
    }
}
