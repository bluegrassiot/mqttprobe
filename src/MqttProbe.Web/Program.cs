using System.Net;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.AspNetCore.Hosting.StaticWebAssets;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.FileProviders;
using MQTTnet;
using MQTTnet.Extensions.ManagedClient;
using MqttProbe.Components.Layout;
using MqttProbe.Services.Chart;
using MqttProbe.Services.Configuration;
using MqttProbe.Services.Emulation;
using MqttProbe.Services.Mqtt;
using MqttProbe.Services.Platform;
using MqttProbe.Services.Security;
using MqttProbe.Services.Sparkplug;
using MqttProbe.Services.Telemetry;
using MqttProbe.Web;
using MqttProbe.Web.Services;
using MudBlazor;
using MudBlazor.Services;

var builder = WebApplication.CreateBuilder(args);

StaticWebAssetsLoader.UseStaticWebAssets(builder.Environment, builder.Configuration);

builder.Services.AddRazorPages();
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddMudServices(config =>
{
    config.SnackbarConfiguration.PositionClass = Defaults.Classes.Position.TopCenter;
    config.SnackbarConfiguration.RequireInteraction = false;
    config.SnackbarConfiguration.PreventDuplicates = true;
    config.SnackbarConfiguration.NewestOnTop = false;
    config.SnackbarConfiguration.ShowCloseIcon = true;
    config.SnackbarConfiguration.VisibleStateDuration = 3000;
    config.SnackbarConfiguration.HideTransitionDuration = 500;
    config.SnackbarConfiguration.ShowTransitionDuration = 500;
    config.SnackbarConfiguration.SnackbarVariant = Variant.Filled;
});

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/Login";
        options.LogoutPath = "/Logout";
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
    });
builder.Services.AddAuthorization();
builder.Services.AddRateLimiter(options => options.AddLoginRateLimitPolicy());

var forwardedHeadersSection = builder.Configuration.GetSection("ForwardedHeaders");
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.ForwardLimit = forwardedHeadersSection.GetValue<int?>("ForwardLimit") ?? 1;

    var knownProxies = forwardedHeadersSection.GetSection("KnownProxies").Get<string[]>() ?? [];
    var knownNetworks = forwardedHeadersSection.GetSection("KnownNetworks").Get<string[]>() ?? [];
    if (knownProxies.Length == 0 && knownNetworks.Length == 0)
        return;

    options.KnownProxies.Clear();
    options.KnownIPNetworks.Clear();

    foreach (var knownProxy in knownProxies)
        options.KnownProxies.Add(IPAddress.Parse(knownProxy));

    foreach (var knownNetwork in knownNetworks)
    {
        options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(knownNetwork));
    }
});

// Web circuits are independent user sessions, so MQTT and UI state services stay scoped per circuit.
builder.Services.AddScoped<IManagedMqttClient>(_ => new MqttFactory().CreateManagedMqttClient());
builder.Services.AddScoped<ISessionState, SessionState>();
builder.Services.AddScoped<IEmulationService, EmulationService>();
builder.Services.AddScoped<IMessageStoreManager, MessageStoreManager>();
builder.Services.AddScoped<ISubscriptionManager, SubscriptionManager>();
builder.Services.AddScoped<IMqttOptionsBuilder, MqttOptionsBuilder>();
builder.Services.AddScoped<IUxTelemetryService, UxTelemetryService>();
builder.Services.AddSingleton<ISparkplugNodeFactory, SparkplugNodeFactory>();
builder.Services.AddScoped<IClipboardService, WebClipboardService>();

var configDir = Path.Combine(builder.Environment.ContentRootPath, "config");

var dpBuilder = builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(configDir, "dp-keys")))
    .SetApplicationName("MqttProbe");

if (OperatingSystem.IsWindows())
    dpBuilder.ProtectKeysWithDpapi(protectToLocalMachine: true);
else
{
    var kekBase64 = Environment.GetEnvironmentVariable("MQTTPROBE_KEK");
    if (!string.IsNullOrEmpty(kekBase64))
    {
        var kek = Convert.FromBase64String(kekBase64);
        var decryptor = new AesKeyDecryptor(kek);
        dpBuilder.Services.AddSingleton<IXmlEncryptor>(new AesKeyEncryptor(kek));
        dpBuilder.Services.AddSingleton<AesKeyDecryptor>(decryptor);
    }
}
builder.Services.AddSingleton<ISecretStorage>(sp =>
    new DataProtectionSecretStorage(
        sp.GetRequiredService<IDataProtectionProvider>(),
        Path.Combine(configDir, "secrets.dat")));

var settingsStore = new SettingsStore(Path.Combine(configDir, "appsettings.json"));
builder.Services.AddSingleton<ISettingsStore>(settingsStore);
builder.Services.AddSingleton<IAppInfoService, AppInfoService>();
builder.Services.AddSingleton<IUserAuthService, SingleAdminUserAuthService>();
builder.Services.AddSingleton<IJsonFieldExtractor, JsonFieldExtractor>();
builder.Services.AddSingleton<IChartFieldRegistry, ChartFieldRegistry>();
builder.Services.AddScoped<IChartDataService, ChartDataService>();
builder.Services.AddScoped<ISparkplugTopologyService, SparkplugTopologyService>();
builder.Services.AddScoped<IThemes, Themes>();

var app = builder.Build();

var secretStorage = app.Services.GetRequiredService<ISecretStorage>();
await settingsStore.LoadAsync(secretStorage);

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseForwardedHeaders();
app.UseHttpsRedirection();

app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new CompositeFileProvider(
        app.Environment.WebRootFileProvider,
        new EmbeddedFileProvider(typeof(Program).Assembly, "MqttProbe.wwwroot"))
});
app.UseRouting();

app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapGet("/health", () => Results.Text("OK", "text/plain")).AllowAnonymous();
app.MapRazorPages().RequireAuthorization();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddAdditionalAssemblies(typeof(MqttProbe.Components.Pages.Index).Assembly)
    .RequireAuthorization();

await app.RunAsync();

// Required for WebApplicationFactory<Program> in E2E tests.
public partial class Program
{
    protected Program() { }
}
