using System.Security.Cryptography;
using System.Text;

namespace DysonHarness;

public sealed class DysonPluginVariableProtector
{
    private const byte EnvelopeVersion = 1;
    private const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private readonly string _keyPath;
    private readonly object _keyGate = new();
    private byte[]? _key;

    public DysonPluginVariableProtector(string keyPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyPath);
        _keyPath = Path.GetFullPath(keyPath);
    }

    public static DysonPluginVariableProtector ForMode(DysonAppMode mode) =>
        new(DysonAppPaths.GetPluginVariableProtectionKeyPath(mode));

    public Result<byte[], string> Protect(string subjectId, Guid installationId, string variableName, string plaintext)
    {
        if (string.IsNullOrWhiteSpace(subjectId) || subjectId == DysonSubjects.Shared || installationId == Guid.Empty || string.IsNullOrWhiteSpace(variableName))
            return Result<byte[], string>.AsError("Protected plugin variable scope is invalid.");
        if (plaintext is null)
            return Result<byte[], string>.AsError("Plugin variable value is required.");
        byte[]? clear = null;
        try
        {
            var key = GetOrCreateKey();
            clear = Encoding.UTF8.GetBytes(plaintext);
            var nonce = RandomNumberGenerator.GetBytes(NonceSize);
            var tag = new byte[TagSize];
            var cipher = new byte[clear.Length];
            using (var aes = new AesGcm(key, TagSize))
                aes.Encrypt(nonce, clear, cipher, tag, BuildAssociatedData(subjectId, installationId, variableName));
            var envelope = new byte[1 + NonceSize + TagSize + cipher.Length];
            envelope[0] = EnvelopeVersion;
            nonce.CopyTo(envelope, 1);
            tag.CopyTo(envelope, 1 + NonceSize);
            cipher.CopyTo(envelope, 1 + NonceSize + TagSize);
            return Result<byte[], string>.AsValue(envelope);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            return Result<byte[], string>.AsError("Failed to protect plugin variable value.");
        }
        finally
        {
            if (clear is not null) CryptographicOperations.ZeroMemory(clear);
        }
    }

    public Result<DysonPluginSecretValue, string> Unprotect(string subjectId, Guid installationId, string variableName, byte[] envelope)
    {
        if (string.IsNullOrWhiteSpace(subjectId) || installationId == Guid.Empty || string.IsNullOrWhiteSpace(variableName))
            return Result<DysonPluginSecretValue, string>.AsError("Protected plugin variable scope is invalid.");
        if (envelope is null || envelope.Length < 1 + NonceSize + TagSize || envelope[0] != EnvelopeVersion)
            return Result<DysonPluginSecretValue, string>.AsError("Protected plugin variable authentication failed.");
        byte[]? clear = null;
        try
        {
            var key = GetOrCreateKey();
            var nonce = envelope.AsSpan(1, NonceSize);
            var tag = envelope.AsSpan(1 + NonceSize, TagSize);
            var cipher = envelope.AsSpan(1 + NonceSize + TagSize);
            clear = new byte[cipher.Length];
            using (var aes = new AesGcm(key, TagSize))
                aes.Decrypt(nonce, cipher, tag, clear, BuildAssociatedData(subjectId, installationId, variableName));
            var secret = new DysonPluginSecretValue(clear);
            clear = null;
            return Result<DysonPluginSecretValue, string>.AsValue(secret);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or DecoderFallbackException)
        {
            return Result<DysonPluginSecretValue, string>.AsError("Protected plugin variable authentication failed.");
        }
        finally
        {
            if (clear is not null) CryptographicOperations.ZeroMemory(clear);
        }
    }

    private byte[] GetOrCreateKey()
    {
        lock (_keyGate)
        {
            if (_key is not null) return _key;
            var directory = Path.GetDirectoryName(_keyPath)!;
            Directory.CreateDirectory(directory);
            if (File.Exists(_keyPath))
            {
                var existing = File.ReadAllBytes(_keyPath);
                if (existing.Length != KeySize) throw new CryptographicException("Invalid key length.");
                return _key = existing;
            }
            var created = RandomNumberGenerator.GetBytes(KeySize);
            var temp = _keyPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(temp, created);
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                try { File.Move(temp, _keyPath); }
                catch (IOException) when (File.Exists(_keyPath)) { File.Delete(temp); }
                var persisted = File.ReadAllBytes(_keyPath);
                if (persisted.Length != KeySize) throw new CryptographicException("Invalid key length.");
                CryptographicOperations.ZeroMemory(created);
                return _key = persisted;
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            }
        }
    }

    private static byte[] BuildAssociatedData(string subjectId, Guid installationId, string variableName) =>
        Encoding.UTF8.GetBytes($"DysonHarness.PluginVariable.v1\n{subjectId}\n{installationId:D}\n{variableName}");
}

public sealed class DysonPluginSecretValue : IDisposable
{
    private byte[]? _utf8;

    internal DysonPluginSecretValue(byte[] utf8) => _utf8 = utf8;

    public int Length => _utf8 is null ? 0 : Encoding.UTF8.GetCharCount(_utf8);
    public bool IsDisposed => _utf8 is null;

    public void CopyTo(Span<char> destination)
    {
        var value = _utf8 ?? throw new ObjectDisposedException(nameof(DysonPluginSecretValue));
        Encoding.UTF8.GetChars(value, destination);
    }

    internal string RevealForRuntime() =>
        _utf8 is null ? throw new ObjectDisposedException(nameof(DysonPluginSecretValue)) : Encoding.UTF8.GetString(_utf8);

    public override string ToString() => "[REDACTED]";

    public void Dispose()
    {
        var value = Interlocked.Exchange(ref _utf8, null);
        if (value is not null) CryptographicOperations.ZeroMemory(value);
    }
}
