using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using MqttProbe.Services.Configuration;
using MqttProbe.Services.Security;

namespace MqttProbe.Pages;

[AllowAnonymous]
[EnableRateLimiting("login")]
public class LoginModel : PageModel
{
    public const string RateLimitPolicyName = "login";

    private readonly ISettingsStore _settingsStore;
    private readonly IUserAuthService _userAuthService;

    public LoginModel(ISettingsStore settingsStore, IUserAuthService userAuthService)
    {
        _settingsStore = settingsStore;
        _userAuthService = userAuthService;
    }

    [BindProperty]
    public bool RememberMe { get; set; }

    public string? ReturnUrl { get; private set; }
    public string? ErrorMessage { get; private set; }

    public IActionResult OnGet(string? returnUrl = null)
    {
        if (string.IsNullOrEmpty(_settingsStore.Config.Auth.PasswordHash))
            return RedirectToPage("/Setup");

        ReturnUrl = returnUrl ?? "/";
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string username, string password, string? returnUrl = null)
    {
        ReturnUrl = returnUrl ?? "/";

        if (!await _userAuthService.ValidateCredentialsAsync(username, password))
        {
            ErrorMessage = "Invalid username or password.";
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
            new AuthenticationProperties { IsPersistent = RememberMe, RedirectUri = ReturnUrl });

        return LocalRedirect(ReturnUrl);
    }
}
