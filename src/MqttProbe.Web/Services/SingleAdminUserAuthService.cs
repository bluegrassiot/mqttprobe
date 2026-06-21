using MqttProbe.Services.Configuration;
using MqttProbe.Services.Security;

namespace MqttProbe.Web.Services;

public class SingleAdminUserAuthService(ISettingsStore settingsStore)
    : MqttProbe.Services.Authentication.SingleAdminUserAuthService(settingsStore);
