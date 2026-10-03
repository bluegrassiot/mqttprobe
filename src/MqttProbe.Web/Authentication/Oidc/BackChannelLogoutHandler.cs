using System.Buffers;
using System.Text;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace MqttProbe.Web.Authentication;

// Anonymous server-to-server traffic: no cookie and no antiforgery token, so
// every decision here comes from the signed logout token.
public sealed class BackChannelLogoutHandler
{
    public const string LogoutTokenFormField = "logout_token";

    public const int MaxRequestBodyBytes = 64 * 1024;

    private static readonly TimeSpan _cleanupGrace = TimeSpan.FromMilliseconds(250);

    private readonly BackChannelLogoutValidator _validator;
    private readonly BackChannelLogoutReplayCache _replayCache;
    private readonly AppSessionCoordinator _coordinator;
    private readonly TimeSpan _teardownTimeout;
    private readonly ILogger<BackChannelLogoutHandler> _logger;

    public BackChannelLogoutHandler(
        BackChannelLogoutValidator validator,
        BackChannelLogoutReplayCache replayCache,
        AppSessionCoordinator coordinator,
        IOptions<BackChannelLogoutHandlerOptions> options,
        ILogger<BackChannelLogoutHandler> logger)
    {
        _validator = validator;
        _replayCache = replayCache;
        _coordinator = coordinator;
        _teardownTimeout = options.Value.TeardownTimeout;
        _logger = logger;
    }

    public async Task HandleAsync(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";

        var status = ValidateRequestShape(context);
        string? logoutToken = null;

        if (status is null)
        {
            (status, logoutToken) = await ReadLogoutTokenAsync(context).ConfigureAwait(false);
        }

        if (status is null && logoutToken is not null)
        {
            status = await ValidateAndRevokeAsync(context, logoutToken).ConfigureAwait(false);
        }

        context.Response.StatusCode = status ?? StatusCodes.Status200OK;
    }

    private static int? ValidateRequestShape(HttpContext context)
    {
        var request = context.Request;

        if (!HttpMethods.IsPost(request.Method))
        {
            context.Response.Headers.Allow = HttpMethods.Post;
            return StatusCodes.Status405MethodNotAllowed;
        }

        if (!request.HasFormContentType)
        {
            return StatusCodes.Status415UnsupportedMediaType;
        }

        if (request.ContentLength is { } length && length > MaxRequestBodyBytes)
        {
            return StatusCodes.Status413PayloadTooLarge;
        }

        // Best effort: chunked requests carry no Content-Length, so let the
        // server enforce the bound as well. Reading the body below enforces it
        // either way, because the feature may be absent or read-only.
        var bodySize = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (bodySize is { IsReadOnly: false })
        {
            bodySize.MaxRequestBodySize = MaxRequestBodyBytes;
        }

        return null;
    }

    private async Task<(int? Status, string? LogoutToken)> ReadLogoutTokenAsync(HttpContext context)
    {
        var cancellationToken = context.RequestAborted;

        try
        {
            var form = await ReadBoundedFormAsync(context.Request, cancellationToken).ConfigureAwait(false);
            var values = form?[LogoutTokenFormField] ?? StringValues.Empty;
            var logoutToken = values.Count == 1 ? values[0] : null;

            if (string.IsNullOrWhiteSpace(logoutToken) || logoutToken.Length > MaxRequestBodyBytes)
            {
                _logger.LogDebug(
                    "Back-channel logout rejected: {Reason} ({FieldCount} value(s))",
                    "malformed_form",
                    values.Count);
                return (StatusCodes.Status400BadRequest, null);
            }

            return (null, logoutToken);
        }
        catch (BodyTooLargeException ex)
        {
            _logger.LogDebug(ex, "Back-channel logout rejected: {Reason}", "body_too_large");
            return (StatusCodes.Status413PayloadTooLarge, null);
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            _logger.LogDebug(ex, "Back-channel logout rejected: {Reason}", "body_too_large");
            return (StatusCodes.Status413PayloadTooLarge, null);
        }
        catch (Exception ex) when (ex is BadHttpRequestException or InvalidDataException or IOException)
        {
            _logger.LogDebug(ex, "Back-channel logout rejected: {Reason}", "unreadable_form");
            return (StatusCodes.Status400BadRequest, null);
        }
    }

    private static async Task<IFormCollection?> ReadBoundedFormAsync(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        using var body = new MemoryStream();
        var rented = ArrayPool<byte>.Shared.Rent(16 * 1024);

        try
        {
            while (true)
            {
                var read = await request.Body
                    .ReadAsync(rented.AsMemory(), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                if (body.Length + read > MaxRequestBodyBytes)
                {
                    throw new BodyTooLargeException();
                }

                body.Write(rented.AsSpan(0, read));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }

        body.Position = 0;
        using var reader = new FormReader(body, Encoding.UTF8);
        var fields = await reader.ReadFormAsync(cancellationToken).ConfigureAwait(false);

        return fields is null ? null : new FormCollection(fields);
    }

    private async Task<int?> ValidateAndRevokeAsync(HttpContext context, string logoutToken)
    {
        var validation = await _validator
            .ValidateAsync(logoutToken, context.RequestAborted)
            .ConfigureAwait(false);

        if (!validation.IsValid)
        {
            // The reason stays out of the response: the caller is unauthenticated.
            return StatusCodes.Status400BadRequest;
        }

        if (!TryClaimToken(validation))
        {
            return StatusCodes.Status400BadRequest;
        }

        var completed = false;

        try
        {
            var (invalidated, sessionCount) = await InvalidateSessionsAsync(validation).ConfigureAwait(false);

            if (!invalidated)
            {
                _logger.LogWarning("Back-channel logout incomplete: {Reason}", "invalidation_failed");
                return StatusCodes.Status500InternalServerError;
            }

            // Claim consumed once every reachable gate is closed, the no-match
            // case included: a valid token must never be replayable.
            _replayCache.Complete(validation.Issuer, validation.TokenId);
            completed = true;

            _logger.LogInformation(
                "Back-channel logout accepted: {SessionCount} session(s) revoked", sessionCount);

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Back-channel logout incomplete: {Reason}", "revocation_failed");
            return StatusCodes.Status500InternalServerError;
        }
        finally
        {
            if (!completed)
            {
                _replayCache.Release(validation.Issuer, validation.TokenId);
            }
        }
    }

    private async Task<(bool Invalidated, int SessionCount)> InvalidateSessionsAsync(
        BackChannelLogoutValidationResult validation)
    {
        // Captured before revoking: a session that finishes revoking leaves the
        // registry, and only the captured record can still prove its gates shut.
        var records = _coordinator.FindSessionsForLogout(
            validation.Issuer, validation.Sid, validation.Subject);

        using var budget = new CancellationTokenSource(_teardownTimeout);
        var revocations = records
            .Select(record => _coordinator.RevokeSessionAsync(record.SessionId, budget.Token))
            .ToList();

        await WaitForRevocationsAsync(revocations).ConfigureAwait(false);

        return (records.All(record => record.AreGatesRevoked), records.Count);
    }

    // Revokes on the application's bounded clock rather than the caller's: the
    // identity provider's connection must not decide how long cleanup may run.
    // The wait outlives the cancellation budget slightly so cleanup that honours
    // the token can finish instead of being reported as abandoned.
    private async Task WaitForRevocationsAsync(IReadOnlyList<Task<IReadOnlyList<CircuitLease>>> revocations)
    {
        var all = Task.WhenAll(revocations);
        var wait = _teardownTimeout + _cleanupGrace;

        try
        {
            await all.WaitAsync(wait).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            _logger.LogWarning(ex, "Back-channel logout cleanup still running after {Timeout}", wait);

            // The wait gave up on this task: its eventual fault is logged here
            // instead of being dropped with the abandoned task.
            _ = LogLateRevocationFaultAsync(all);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Back-channel logout revocation reported a failure");
        }
    }

    private async Task LogLateRevocationFaultAsync(Task revocation)
    {
        try
        {
            await revocation.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Back-channel logout revocation reported a failure");
        }
    }

    private bool TryClaimToken(BackChannelLogoutValidationResult validation)
    {
        var reservation = _replayCache.TryReserve(
            validation.Issuer, validation.TokenId, validation.ExpiresAt);

        if (reservation == BackChannelLogoutReservation.Reserved)
        {
            return true;
        }

        if (reservation == BackChannelLogoutReservation.Full)
        {
            _logger.LogWarning("Back-channel logout rejected: {Reason}", "claim_cache_full");
        }
        else
        {
            _logger.LogDebug("Back-channel logout rejected: {Reason}", "duplicate_token");
        }

        return false;
    }

    private sealed class BodyTooLargeException : Exception
    {
    }
}

public sealed class BackChannelLogoutHandlerOptions
{
    public TimeSpan TeardownTimeout { get; set; } = TimeSpan.FromSeconds(30);
}
