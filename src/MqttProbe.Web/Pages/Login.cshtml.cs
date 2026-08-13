using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using MqttProbe.Core.Models.Configuration;
using MqttProbe.Core.Services.Configuration;
using MqttProbe.Core.Services.Security;

namespace MqttProbe.Pages;

[AllowAnonymous]
[EnableRateLimiting("login")]
public class LoginModel : PageModel
{
    public const string RateLimitPolicyName = "login";

    private readonly IAuthSettings _authSettings;
    private readonly IUserAuthService _userAuthService;
    private readonly IUiSettings _uiSettings;

    public LoginModel(IAuthSettings authSettings, IUserAuthService userAuthService, IUiSettings uiSettings)
    {
        _authSettings = authSettings;
        _userAuthService = userAuthService;
        _uiSettings = uiSettings;
    }

    public bool IsAccessibleFontProfile =>
        FontProfiles.IsAccessible(_uiSettings.Ui.FontProfile);

    [BindProperty]
    public bool RememberMe { get; set; }

    public string? ReturnUrl { get; private set; }
    public string? ErrorMessage { get; private set; }

    public IActionResult OnGet(string? returnUrl = null)
    {
        if (string.IsNullOrEmpty(_authSettings.Auth.PasswordHash))
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
