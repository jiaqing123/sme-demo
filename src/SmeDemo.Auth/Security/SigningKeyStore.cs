using Microsoft.IdentityModel.Tokens;
using System.Security.Cryptography;
using System.Text.Json;

namespace SmeDemo.Auth.Security;

/// <summary>
/// Owns the RS256 signing keys. The current key is generated on first start and
/// written to the key volume, so a restart keeps existing tokens valid. A
/// previous key file is loaded when present, so a rotation keeps tokens signed by
/// the old key verifiable (used by <c>/auth/userinfo</c>, which validates tokens
/// itself). The private key never leaves the issuer or reaches source control.
/// </summary>
public sealed class SigningKeyStore : IDisposable
{
    private const string KeyFileName = "signing-key.json";
    private const string PreviousKeyFileName = "signing-key.previous.json";
    private const int RequiredKeySizeBits = 2048;

    private readonly List<RSA> _rsaKeys = [];
    private readonly List<SecurityKey> _validationKeys = [];
    private readonly List<IReadOnlyDictionary<string, object>> _publicJwks = [];

    public SigningKeyStore(string keysDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keysDirectory);

        Directory.CreateDirectory(keysDirectory);

        AddKey(LoadOrCreate(Path.Combine(keysDirectory, KeyFileName)));

        var previousKeyPath = Path.Combine(keysDirectory, PreviousKeyFileName);
        if (File.Exists(previousKeyPath))
        {
            var previous = LoadExisting(previousKeyPath);
            if (!string.Equals(ComputeKeyId(previous), KeyId, StringComparison.Ordinal))
            {
                AddKey(previous);
            }
            else
            {
                previous.Dispose();
            }
        }
    }

    public string KeyId => _validationKeys[0].KeyId!;

    public SecurityKey SigningKey => _validationKeys[0];

    /// <summary>
    /// Current key first, then the optional previous key. Validation must accept
    /// both so a token minted just before a rotation is not rejected.
    /// </summary>
    public IReadOnlyList<SecurityKey> ValidationKeys => _validationKeys;

    public IReadOnlyList<IReadOnlyDictionary<string, object>> PublicJwks => _publicJwks;

    public void Dispose()
    {
        foreach (var rsa in _rsaKeys)
        {
            rsa.Dispose();
        }
    }

    private void AddKey(RSA rsa)
    {
        if (rsa.KeySize != RequiredKeySizeBits)
        {
            rsa.Dispose();
            throw new InvalidOperationException(
                $"The signing key must be {RequiredKeySizeBits}-bit RSA; found {rsa.KeySize} bits.");
        }

        var keyId = ComputeKeyId(rsa);
        _rsaKeys.Add(rsa);
        _validationKeys.Add(new RsaSecurityKey(rsa) { KeyId = keyId });
        _publicJwks.Add(CreatePublicJwk(rsa, keyId));
    }

    private static RSA LoadOrCreate(string keyPath)
    {
        if (File.Exists(keyPath))
        {
            return LoadExisting(keyPath);
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

    private static RSA LoadExisting(string keyPath)
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
