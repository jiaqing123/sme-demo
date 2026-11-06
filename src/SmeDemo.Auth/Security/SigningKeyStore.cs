using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;

namespace SmeDemo.Auth.Security;

/// <summary>
/// Owns the RS256 signing key. The key is generated on first start and written
/// to the key volume, so a restart keeps existing tokens valid. The private key
/// never leaves the issuer and is never committed to source control.
/// </summary>
public sealed class SigningKeyStore : IDisposable
{
    private const string KeyFileName = "signing-key.json";
    private const int RequiredKeySizeBits = 2048;

    private readonly RSA _rsa;
    private readonly IReadOnlyDictionary<string, object> _publicJwk;

    public SigningKeyStore(string keysDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keysDirectory);

        Directory.CreateDirectory(keysDirectory);
        var keyPath = Path.Combine(keysDirectory, KeyFileName);

        _rsa = LoadOrCreate(keyPath);
        if (_rsa.KeySize != RequiredKeySizeBits)
        {
            _rsa.Dispose();
            throw new InvalidOperationException(
                $"The signing key must be {RequiredKeySizeBits}-bit RSA; found {_rsa.KeySize} bits.");
        }

        KeyId = ComputeKeyId(_rsa);
        SigningKey = new RsaSecurityKey(_rsa) { KeyId = KeyId };
        _publicJwk = CreatePublicJwk(_rsa, KeyId);
    }

    public string KeyId { get; }

    public SecurityKey SigningKey { get; }

    public IReadOnlyDictionary<string, object> PublicJwk => _publicJwk;

    public void Dispose() => _rsa.Dispose();

    private static RSA LoadOrCreate(string keyPath)
    {
        if (File.Exists(keyPath))
        {
            var stored = JsonSerializer.Deserialize<StoredKey>(File.ReadAllText(keyPath))
                ?? throw new InvalidOperationException("The signing key file is empty or invalid.");

            var rsa = RSA.Create();
            try
            {
                rsa.ImportPkcs8PrivateKey(Convert.FromBase64String(stored.PrivateKeyPkcs8), out _);
            }
            catch (Exception exception) when (exception is CryptographicException or FormatException)
            {
                rsa.Dispose();
                throw new InvalidOperationException("The signing key file could not be read.", exception);
            }

            if (!string.Equals(stored.Kid, ComputeKeyId(rsa), StringComparison.Ordinal))
            {
                rsa.Dispose();
                throw new InvalidOperationException("The signing key file does not match its recorded key id.");
            }

            return rsa;
        }

        using var generated = RSA.Create(RequiredKeySizeBits);
        var kid = ComputeKeyId(generated);
        var payload = new StoredKey(kid, Convert.ToBase64String(generated.ExportPkcs8PrivateKey()));

        File.WriteAllText(keyPath, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        RestrictPermissions(keyPath);

        var created = RSA.Create();
        created.ImportPkcs8PrivateKey(Convert.FromBase64String(payload.PrivateKeyPkcs8), out _);
        return created;
    }

    private static string ComputeKeyId(RSA rsa) =>
        Base64UrlEncoder.Encode(SHA256.HashData(rsa.ExportSubjectPublicKeyInfo()));

    private static IReadOnlyDictionary<string, object> CreatePublicJwk(RSA rsa, string keyId)
    {
        var parameters = rsa.ExportParameters(includePrivateParameters: false);

        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["kty"] = "RSA",
            ["use"] = "sig",
            ["alg"] = SecurityAlgorithms.RsaSha256,
            ["kid"] = keyId,
            ["n"] = Base64UrlEncoder.Encode(parameters.Modulus),
            ["e"] = Base64UrlEncoder.Encode(parameters.Exponent),
        };
    }

    private static void RestrictPermissions(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private sealed record StoredKey(string Kid, string PrivateKeyPkcs8);
}
