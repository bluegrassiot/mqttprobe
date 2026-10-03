using System.Net;
using System.Text;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MqttProbe.IntegrationTests.Authentication;

internal sealed record CapturedBackchannelLogoutRequest(
    string Method,
    string PathAndQuery,
    string? ContentType,
    string Body,
    IPAddress? RemoteIpAddress,
    int AppStatusCode,
    string? AppError,
    string AppBody)
{
    internal bool AppAccepted => AppStatusCode is >= 200 and <= 299;
}

// Real TCP listener: the Keycloak container POSTs here and the request is
// forwarded into the in-process TestServer, so the provider-to-app hop is a
// genuine network call rather than a fabricated in-process one.
internal sealed class BackchannelLogoutBridge : IAsyncDisposable
{
    private const string AppOrigin = "https://mqttprobe.test";

    // Resolves to the host from inside a container.
    private const string ContainerHostName = "host.docker.internal";

    private static readonly TimeSpan _forwardTimeout = TimeSpan.FromSeconds(30);

    private readonly IHost _host;
    private readonly Channel<CapturedBackchannelLogoutRequest> _arrived =
        Channel.CreateUnbounded<CapturedBackchannelLogoutRequest>();

    private HttpClient? _appClient;
    private bool _disposed;

    private BackchannelLogoutBridge(IHost host)
    {
        _host = host;
    }

    internal int Port { get; private set; }

    internal string ContainerHost => ContainerHostName;

    internal string BackchannelLogoutUrl =>
        $"http://{ContainerHostName}:{Port}/oidc/backchannel-logout"; // DevSkim: ignore DS137138 test-only bridge: provider container must reach a plain HTTP host listener

    internal static async Task<BackchannelLogoutBridge> StartAsync()
    {
        BackchannelLogoutBridge? bridge = null;

        var host = new HostBuilder()
            .ConfigureLogging(logging => logging.ClearProviders())
            .ConfigureWebHost(web =>
            {
                web.UseKestrel();
                web.UseUrls("http://0.0.0.0:0"); // DevSkim: ignore DS137138 test-only bridge: Kestrel binds any interface for a temporary listener
                web.Configure(app => app.Run(context =>
                    bridge is null
                        ? throw new InvalidOperationException("Bridge is not initialised.")
                        : bridge.ForwardAsync(context)));
            })
            .Build();

        bridge = new BackchannelLogoutBridge(host);

        try
        {
            await host.StartAsync();
        }
        catch
        {
            host.Dispose();
            throw;
        }

        var address = host.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()
            ?.Addresses.FirstOrDefault();

        var port = TryReadPort(address);
        if (port is null or 0)
        {
            await host.StopAsync(TimeSpan.FromSeconds(5));
            host.Dispose();
            throw new InvalidOperationException(
                $"Bridge listener did not report a bound port (address: '{address ?? "<none>"}').");
        }

        bridge.Port = port.Value;
        return bridge;
    }


    internal void ForwardToApp(HttpMessageHandler appHandler)
    {
        ArgumentNullException.ThrowIfNull(appHandler);

        var previous = Interlocked.Exchange(
            ref _appClient,
            new HttpClient(appHandler, disposeHandler: false)
            {
                BaseAddress = new Uri(AppOrigin),
                Timeout = _forwardTimeout
            });

        previous?.Dispose();
    }

    internal async Task<CapturedBackchannelLogoutRequest> WaitForRequestAsync(
        TimeSpan timeout, CancellationToken cancellationToken = default)
        => await TryWaitForRequestAsync(timeout, cancellationToken)
           ?? throw new TimeoutException(
               $"No provider-initiated POST reached {BackchannelLogoutUrl} within " +
               $"{timeout.TotalSeconds:0}s. Keycloak either never sent the back-channel " +
               "logout or could not reach the host listener.");

    internal async Task<CapturedBackchannelLogoutRequest?> TryWaitForRequestAsync(
        TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        try
        {
            if (await _arrived.Reader.WaitToReadAsync(timeoutSource.Token) &&
                _arrived.Reader.TryRead(out var captured))
            {
                return captured;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }

        return null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;

        _arrived.Writer.TryComplete();

        try
        {
            await _host.StopAsync(TimeSpan.FromSeconds(5));
        }
        catch (ObjectDisposedException)
        {
        }

        _host.Dispose();
        _appClient?.Dispose();
    }

    private async Task ForwardAsync(HttpContext context)
    {
        var request = context.Request;
        var pathAndQuery = request.Path + request.QueryString;
        if (string.IsNullOrEmpty(pathAndQuery))
            pathAndQuery = "/";

        byte[] requestBody;
        using (var buffer = new MemoryStream())
        {
            await request.Body.CopyToAsync(buffer, context.RequestAborted);
            requestBody = buffer.ToArray();
        }

        var appStatusCode = 0;
        string? appError = null;
        byte[] appBody = [];

        var appClient = _appClient;
        if (appClient is null)
        {
            appStatusCode = StatusCodes.Status503ServiceUnavailable;
            appError = "No app server is attached to the bridge.";
        }
        else
        {
            try
            {
                using var forwarded = new HttpRequestMessage(new HttpMethod(request.Method), pathAndQuery);
                if (request.Method != HttpMethods.Get && request.Method != HttpMethods.Head)
                {
                    forwarded.Content = new ByteArrayContent(requestBody);
                    if (request.ContentType is not null)
                    {
                        forwarded.Content.Headers.Remove("Content-Type");
                        forwarded.Content.Headers.TryAddWithoutValidation("Content-Type", request.ContentType);
                    }
                }

                using var appResponse = await appClient.SendAsync(
                    forwarded, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);

                appStatusCode = (int)appResponse.StatusCode;
                appBody = await appResponse.Content.ReadAsByteArrayAsync(context.RequestAborted);

                context.Response.StatusCode = appStatusCode;
                context.Response.ContentLength = appBody.Length;
                if (appResponse.Content.Headers.ContentType is not null)
                    context.Response.ContentType = appResponse.Content.Headers.ContentType.ToString();

                await context.Response.Body.WriteAsync(appBody, context.RequestAborted);
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
            {
                appError = ex.Message;
                appStatusCode = StatusCodes.Status502BadGateway;
                context.Response.StatusCode = appStatusCode;
            }
        }

        var captured = new CapturedBackchannelLogoutRequest(
            request.Method,
            pathAndQuery,
            request.ContentType,
            Encoding.UTF8.GetString(requestBody),
            context.Connection.RemoteIpAddress,
            appStatusCode,
            appError,
            Encoding.UTF8.GetString(appBody));

        await _arrived.Writer.WriteAsync(captured);
    }

    private static int? TryReadPort(string? address)
    {
        return Uri.TryCreate(address, UriKind.Absolute, out var uri) ? uri.Port : null;
    }
}
