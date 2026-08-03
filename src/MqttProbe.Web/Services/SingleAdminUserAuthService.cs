using MqttProbe.Services.Configuration;
using MqttProbe.Services.Security;

namespace MqttProbe.Web.Services;

public class SingleAdminUserAuthService(IAuthSettings authSettings)
    : MqttProbe.Services.Authentication.SingleAdminUserAuthService(authSettings);
