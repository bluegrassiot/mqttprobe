namespace MqttProbe.Web.Authentication;

// Rejections carry only a reason code; token material never enters a result, a
// log line or a response body.
public sealed class BackChannelLogoutValidationResult
{
    private BackChannelLogoutValidationResult(
        bool isValid,
        string reason,
        string issuer,
        string? subject,
        string? sid,
        string tokenId,
        DateTimeOffset expiresAt)
    {
        IsValid = isValid;
        Reason = reason;
        Issuer = issuer;
        Subject = subject;
        Sid = sid;
        TokenId = tokenId;
        ExpiresAt = expiresAt;
    }

    public bool IsValid { get; }

    public string Reason { get; }

    public string Issuer { get; }

    public string? Subject { get; }

    // sid claim; null when the provider omitted it, and only then does the
    // subject identify the session set.
    public string? Sid { get; }

    public string TokenId { get; }

    public DateTimeOffset ExpiresAt { get; }

    public static BackChannelLogoutValidationResult Invalid(string reason)
        => new(false, reason, "", null, null, "", default);

    public static BackChannelLogoutValidationResult Valid(
        string issuer,
        string? subject,
        string? sid,
        string tokenId,
        DateTimeOffset expiresAt)
        => new(true, "", issuer, subject, sid, tokenId, expiresAt);
}
