using MqttProbe.Core.Services.Configuration;

namespace MqttProbe.Web.Services;

public class SingleAdminUserAuthService(IAuthSettings authSettings)
    : MqttProbe.Core.Services.Authentication.SingleAdminUserAuthService(authSettings);
