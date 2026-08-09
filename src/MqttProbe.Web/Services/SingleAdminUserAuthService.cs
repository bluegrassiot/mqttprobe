using MqttProbe.Core.Services.Configuration;
using MqttProbe.Core.Services.Security;

namespace MqttProbe.Web.Services;

public class SingleAdminUserAuthService(IAuthSettings authSettings)
    : MqttProbe.Core.Services.Authentication.SingleAdminUserAuthService(authSettings);
