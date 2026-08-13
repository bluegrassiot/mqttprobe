using System.Net;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.AspNetCore.Hosting.StaticWebAssets;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.FileProviders;
using MqttProbe.Core.Models.Plugins;
using MqttProbe.Core.Services.Configuration;
using MqttProbe.Core.Services.Platform;
using MqttProbe.Core.Services.Plugins;
using MqttProbe.Core.Services.Plugins.Packaging;
using MqttProbe.Core.Services.Security;
using MqttProbe.UI.Services.Platform;
using MqttProbe.Web;
using MqttProbe.Web.Services;

var builder = WebApplication.CreateBuilder(args);

StaticWebAssetsLoader.UseStaticWebAssets(builder.Environment, builder.Configuration);

builder.Services.AddRazorPages();
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddMqttProbeUi();

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/Login";
        options.LogoutPath = "/Logout";
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
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
        dpBuilder.Services.AddSingleton(decryptor);
    }
}
builder.Services.AddSingleton<ISecretStorage>(sp =>
    new DataProtectionSecretStorage(
        sp.GetRequiredService<IDataProtectionProvider>(),
        Path.Combine(configDir, "secrets.dat")));

builder.Services.AddMqttProbeCore(HostSessionModel.PerCircuit);
builder.Services.AddMqttProbeSettings(Path.Combine(configDir, "appsettings.json"));
builder.Services.AddSingleton<ICertificateEnvelopeKeyStore>(sp =>
    new WebCertificateEnvelopeKeyStore(sp.GetRequiredService<ISecretStorage>()));
builder.Services.AddSingleton<IFileProtector, DefaultFileProtector>();
builder.Services.AddSingleton<ICertificateAssetStore>(sp =>
{
    var store = new CertificateAssetStore(
        sp.GetRequiredService<ICertificateEnvelopeKeyStore>(),
        configDir,
        sp.GetRequiredService<ILogger<CertificateAssetStore>>());
    return store;
});
builder.Services.AddSingleton<ICertificateFilePicker, WebCertificateFilePicker>();
builder.Services.AddSingleton<ICertificateInputCapability, WebCertificateInputCapability>();

builder.Services.AddScoped<IClipboardService, WebClipboardService>();
builder.Services.AddSingleton<IAppInfoService, AppInfoService>();
builder.Services.AddSingleton<IUpdateService, NoOpUpdateService>();
builder.Services.AddSingleton<IUserAuthService, SingleAdminUserAuthService>();

builder.Services.Configure<PluginConfig>(builder.Configuration.GetSection("Plugins"));
builder.Services.PostConfigure<PluginConfig>(cfg =>
{
    var contentPlugins = Path.Combine(builder.Environment.ContentRootPath, "Plugins");
    PluginFolderDefaults.Apply(cfg, builder.Environment.ContentRootPath, contentPlugins);
});
builder.Services.AddSingleton<IPluginPackagePicker, WebPluginPackagePicker>();
builder.Services.AddSingleton<IPluginInputCapability, WebPluginInputCapability>();
builder.Services.AddMqttProbePlugins();

builder.Services.AddMqttProbeSparkplugTopology(HostSessionModel.PerCircuit);

var app = builder.Build();

var configLoaded = await app.Services.GetRequiredService<ISettingsLoader>().LoadAsync();

var certCleanup = new CertificateStoreCleanup(
    app.Services.GetRequiredService<ICertificateAssetStore>(),
    app.Services.GetRequiredService<ICertificateEnvelopeKeyStore>(),
    app.Services.GetRequiredService<ILogger<CertificateStoreCleanup>>());
await certCleanup.RunAsync(
    app.Services.GetRequiredService<IConnectionSettings>().Connections, configLoaded);

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
    .AddAdditionalAssemblies(typeof(MqttProbe.UI.Components.Pages.Index).Assembly)
    .RequireAuthorization();

await app.RunAsync();

public partial class Program
{
    protected Program() { }
}
