namespace MqttProbe.Desktop.Services.Security;

public sealed class RawSecretKeyFile : IRawSecretKeyFile
{
    private readonly string _path;

    public RawSecretKeyFile(string secretsDir)
        => _path = Path.Combine(secretsDir, MasterKeyConstants.RawKeyFileName);

    public bool Exists => File.Exists(_path);

    public byte[]? TryRead()
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        var bytes = File.ReadAllBytes(_path);
        if (bytes.Length != MasterKeyConstants.KeySize)
        {
            throw new SecretStorageException(
                $"Raw key file '{_path}' has length {bytes.Length}; expected {MasterKeyConstants.KeySize}.");
        }

        return bytes;
    }

    public void Write(ReadOnlySpan<byte> key)
    {
        if (key.Length != MasterKeyConstants.KeySize)
        {
            throw new SecretStorageException("Raw key must be 32 bytes.");
        }

        var temp = _path + ".tmp";
        File.WriteAllBytes(temp, key.ToArray());
        RestrictToOwner(temp);
        File.Move(temp, _path, overwrite: true);
        RestrictToOwner(_path);
    }

    public bool TryDelete()
    {
        if (!File.Exists(_path))
        {
            return true;
        }

        try
        {
            File.Delete(_path);
            return !File.Exists(_path);
        }
        catch
        {
            return false;
        }
    }

    private static void RestrictToOwner(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}
