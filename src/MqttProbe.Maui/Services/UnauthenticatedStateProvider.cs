using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using MqttProbe.Services.Security;

namespace MqttProbe.Services;

public class UnauthenticatedStateProvider : AuthenticationStateProvider
{
    private static readonly AuthenticationState _operatorState = new(
        new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "local"), new Claim(ClaimTypes.Role, AppRoles.Operator)],
            authenticationType: "Maui")));

    public override Task<AuthenticationState> GetAuthenticationStateAsync()
        => Task.FromResult(_operatorState);
}
