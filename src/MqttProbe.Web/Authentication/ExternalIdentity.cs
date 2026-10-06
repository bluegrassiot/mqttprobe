namespace MqttProbe.Web.Authentication;

// Typed external identity with validated nonblank issuer+subject.
public sealed record ExternalIdentity(string Issuer, string Subject, string DisplayName)
{
    public static ExternalIdentity Create(string issuer, string subject, string displayName)
    {
        if (string.IsNullOrWhiteSpace(issuer))
            throw new ArgumentException("Issuer must not be blank.", nameof(issuer));
        if (string.IsNullOrWhiteSpace(subject))
            throw new ArgumentException("Subject must not be blank.", nameof(subject));

        return new ExternalIdentity(issuer, subject, displayName);
    }
}
