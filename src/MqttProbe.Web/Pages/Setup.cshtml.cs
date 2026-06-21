using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MqttProbe.Services.Configuration;
using MqttProbe.Services.Security;

namespace MqttProbe.Pages;

[AllowAnonymous]
public class SetupModel : PageModel
{
    private readonly ISettingsStore _settingsStore;
    private readonly IUserAuthService _userAuthService;

    public SetupModel(ISettingsStore settingsStore, IUserAuthService userAuthService)
    {
        _settingsStore = settingsStore;
        _userAuthService = userAuthService;
    }

    public string? ErrorMessage { get; private set; }

    public IActionResult OnGet()
    {
        if (!string.IsNullOrEmpty(_settingsStore.Config.Auth.PasswordHash))
            return RedirectToPage("/Login");

        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string username, string password, string confirmPassword)
    {
        if (!string.IsNullOrEmpty(_settingsStore.Config.Auth.PasswordHash))
            return RedirectToPage("/Login");

        if (string.IsNullOrWhiteSpace(username))
        {
            ErrorMessage = "Username is required.";
            return Page();
        }

        if (string.IsNullOrWhiteSpace(password))
        {
            ErrorMessage = "Password is required.";
            return Page();
        }

        if (password != confirmPassword)
        {
            ErrorMessage = "Passwords do not match.";
            return Page();
        }

        var result = await _userAuthService.CreateUserAsync(username, password, AppRoles.Admin);
        if (!result.Succeeded)
        {
            ErrorMessage = result.Error;
            return Page();
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, username),
            new(ClaimTypes.Role, AppRoles.Admin)
        };

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);

        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal,
            new AuthenticationProperties { IsPersistent = false });

        return LocalRedirect("/");
    }
}
