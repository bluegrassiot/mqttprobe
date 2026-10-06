using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using MqttProbe.Core.Models.Configuration;
using MqttProbe.Core.Services.Configuration;
using MqttProbe.Web.Authentication;

namespace MqttProbe.Pages;

[AllowAnonymous]
public class AccessDeniedModel : PageModel
{
    private readonly DenialStateStore _denialStore;
    private readonly AuthenticationOptions _authOptions;
    private readonly IUiSettings _uiSettings;

    public AccessDeniedModel(
        DenialStateStore denialStore,
        IOptions<AuthenticationOptions> authOptions,
        IUiSettings uiSettings)
    {
        _denialStore = denialStore;
        _authOptions = authOptions.Value;
        _uiSettings = uiSettings;
    }

    public bool IsAccessibleFontProfile =>
        FontProfiles.IsAccessible(_uiSettings.Ui.FontProfile);

    public string? DisplayName { get; private set; }
    public string? Category { get; private set; }
    public string? ProviderDisplayName { get; private set; }

    public IActionResult OnGet(string? state)
    {
        if (string.IsNullOrEmpty(state))
        {
            return RedirectToPage("/Login");
        }

        var result = _denialStore.Retrieve(state);
        if (result is null)
        {
            return RedirectToPage("/Login");
        }

        DisplayName = result.Value.DisplayName;
        Category = result.Value.Category;
        ProviderDisplayName = _authOptions.Oidc.ProviderDisplayName;

        return Page();
    }
}
