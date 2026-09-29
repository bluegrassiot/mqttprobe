using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MqttProbe.Core.Models.Configuration;
using MqttProbe.Core.Services.Configuration;
using MqttProbe.Core.Services.Security;
using MqttProbe.Web.Authentication;
using AuthOptions = MqttProbe.Web.Authentication.AuthenticationOptions;

namespace MqttProbe.Pages;

[AllowAnonymous]
[EnableRateLimiting("login")]
public class LoginModel : PageModel
{
    public const string RateLimitPolicyName = "login";

    private readonly IAuthSettings _authSettings;
    private readonly IUserAuthService _userAuthService;
    private readonly IUiSettings _uiSettings;
    private readonly AuthOptions _authOptions;
    private readonly AppSessionCoordinator? _coordinator;
    private readonly IOptionsMonitor<OpenIdConnectOptions>? _oidcOptionsMonitor;
    private readonly ILogger<LoginModel> _logger;

    public LoginModel(
        IAuthSettings authSettings,
        IUserAuthService userAuthService,
        IUiSettings uiSettings,
        IOptions<AuthOptions> authOptions,
        AppSessionCoordinator? coordinator = null,
        IOptionsMonitor<OpenIdConnectOptions>? oidcOptionsMonitor = null,
        ILogger<LoginModel>? logger = null)
    {
        _authSettings = authSettings;
        _userAuthService = userAuthService;
        _uiSettings = uiSettings;
        _authOptions = authOptions.Value;
        _coordinator = coordinator;
        _oidcOptionsMonitor = oidcOptionsMonitor;
        _logger = logger ?? NullLogger<LoginModel>.Instance;
    }

    public bool IsAccessibleFontProfile =>
        FontProfiles.IsAccessible(_uiSettings.Ui.FontProfile);

    public bool IsOidcMode =>
        _authOptions.Mode.Equals("OIDC", StringComparison.OrdinalIgnoreCase);

    public string? ProviderDisplayName => _authOptions.Oidc.ProviderDisplayName;

    [BindProperty]
    public bool RememberMe { get; set; }

    public string? ReturnUrl { get; private set; }
    public string? ErrorMessage { get; private set; }

    public IActionResult OnGet(string? returnUrl = null, string? error = null)
    {
        ReturnUrl = IsLocalUrl(returnUrl) ? returnUrl : "/";

        if (!string.IsNullOrEmpty(error))
        {
            ErrorMessage = error switch
            {
                "provider_unavailable" => "The identity provider is temporarily unavailable. Please try again.",
                "timeout" => "The request timed out. Please try again.",
                "authentication_failed" => "Authentication failed. Please try again.",
                LogoutModel.SignOutIncompleteError =>
                    "You are signed out of mqttprobe, but the identity provider session could not be ended. " +
                    "You may still be signed in at the identity provider.",
                _ => "An error occurred during authentication. Please try again."
            };
        }

        if (IsOidcMode)
        {
            // OIDC mode: never redirect to Setup
            return Page();
        }

        // Local mode: check if setup is needed
        if (string.IsNullOrEmpty(_authSettings.Auth.PasswordHash))
            return RedirectToPage("/Setup");

        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string? username, string? password, string? returnUrl = null)
    {
        ReturnUrl = IsLocalUrl(returnUrl) ? returnUrl : "/";

        if (IsOidcMode)
        {
            // OIDC mode: never validate local password
            return Page();
        }

        // Local mode
        if (!await _userAuthService.ValidateCredentialsAsync(username ?? "", password ?? ""))
        {
            ErrorMessage = "Invalid username or password.";
            return Page();
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, username ?? ""),
            new(ClaimTypes.Role, AppRoles.Admin)
        };

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);

        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal,
            new AuthenticationProperties { IsPersistent = RememberMe, RedirectUri = ReturnUrl });

        return LocalRedirect(ReturnUrl!);
    }

    public async Task<IActionResult> OnPostChallengeAsync(string? returnUrl = null)
    {
        ReturnUrl = IsLocalUrl(returnUrl) ? returnUrl : "/";

        if (!IsOidcMode)
        {
            return Page();
        }

        var configManager = _oidcOptionsMonitor
            ?.Get(OpenIdConnectDefaults.AuthenticationScheme)
            ?.ConfigurationManager;

        if (configManager is null)
        {
            _logger.LogWarning("OIDC metadata preflight failed");
            return RedirectToPage("/Login", new { error = "provider_unavailable", returnUrl = ReturnUrl });
        }

        try
        {
            await configManager.GetConfigurationAsync(HttpContext.RequestAborted);
        }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return MetadataPreflightFailed(ReturnUrl ?? "/");
        }

        if (_coordinator is not null)
        {
            var existingSessionId = HttpContext.User.FindFirst(AuthClaimTypes.AppSessionId)?.Value;
            if (!string.IsNullOrEmpty(existingSessionId))
            {
                await _coordinator.RevokeSessionAsync(existingSessionId);
            }
        }

        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

        return Challenge(
            new AuthenticationProperties { RedirectUri = ReturnUrl },
            OpenIdConnectDefaults.AuthenticationScheme);
    }

    private RedirectToPageResult MetadataPreflightFailed(string returnUrl)
    {
        _logger.LogWarning(new InvalidOperationException("OIDC metadata unavailable."), "OIDC metadata preflight failed");
        return RedirectToPage("/Login", new { error = "provider_unavailable", returnUrl });
    }

    private static bool IsLocalUrl(string? url)
    {
        if (string.IsNullOrEmpty(url))
            return false;

        // Allows relative paths like "/" or "/page" but rejects protocol-relative or absolute URLs
        return url[0] == '/' && (url.Length == 1 || url[1] != '/' && url[1] != '\\');
    }
}
