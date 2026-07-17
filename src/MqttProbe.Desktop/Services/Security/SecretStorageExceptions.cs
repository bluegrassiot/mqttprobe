namespace MqttProbe.Desktop.Services.Security;

public class SecretStorageException : Exception
{
    public SecretStorageException(string message) : base(message) { }
    public SecretStorageException(string message, Exception inner) : base(message, inner) { }
}

public sealed class SecretKeyFacilityException : SecretStorageException
{
    public SecretKeyFacilityException(string message) : base(message) { }
    public SecretKeyFacilityException(string message, Exception inner) : base(message, inner) { }
}

public sealed class AmbiguousSecretKeyException : SecretStorageException
{
    public AmbiguousSecretKeyException(string message) : base(message) { }
}

public sealed class PartialSecretKeyMigrationException : SecretStorageException
{
    public PartialSecretKeyMigrationException(string message) : base(message) { }
    public PartialSecretKeyMigrationException(string message, Exception inner) : base(message, inner) { }
}

public sealed class OrphanedSecretStoreException : SecretStorageException
{
    public OrphanedSecretStoreException(string message) : base(message) { }
}
