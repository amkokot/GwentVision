using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GwentCompanion.Platform.Windows.Security;

/// <summary>
/// Per-installation ECDSA identity. The private PKCS#8 bytes are protected with
/// Windows DPAPI for the current user and are decrypted only while signing.
/// </summary>
public sealed class InstallationSigningIdentity
{
    private const int CurrentSchema = 1;
    public const string Algorithm = "ecdsa-p256-sha256";
    private readonly string _path;
    private readonly Guid _installationId;

    private InstallationSigningIdentity(string path, Guid installationId, byte[] publicKeySpki, byte[] fingerprint)
    {
        _path = path;
        _installationId = installationId;
        PublicKeySpki = publicKeySpki;
        Fingerprint = fingerprint;
    }

    public byte[] PublicKeySpki { get; }
    public byte[] Fingerprint { get; }
    public string FingerprintHex => Convert.ToHexString(Fingerprint).ToLowerInvariant();

    public static InstallationSigningIdentity LoadOrCreate(string path, Guid installationId)
    {
        if (installationId == Guid.Empty) throw new ArgumentException("Installation identity is required.", nameof(installationId));
        if (File.Exists(path)) return Load(path, installationId);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = key.ExportSubjectPublicKeyInfo();
        var privateKey = key.ExportPkcs8PrivateKey();
        byte[]? encrypted = null;
        try
        {
            encrypted = ProtectedData.Protect(privateKey, Entropy(installationId), DataProtectionScope.CurrentUser);
            var file = new StoredSigningIdentity(CurrentSchema, Algorithm, installationId,
                Convert.ToBase64String(publicKey), Convert.ToBase64String(encrypted), DateTimeOffset.UtcNow);
            var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(output, file, new JsonSerializerOptions { WriteIndented = true });
                    output.Flush(flushToDisk: true);
                }
                try { File.Move(temporary, path); }
                catch (IOException) when (File.Exists(path)) { return Load(path, installationId); }
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return new(path, installationId, publicKey, SHA256.HashData(publicKey));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateKey);
            if (encrypted is not null) CryptographicOperations.ZeroMemory(encrypted);
        }
    }

    public byte[] Sign(byte[] canonicalPayload)
    {
        ArgumentNullException.ThrowIfNull(canonicalPayload);
        var stored = Read(_path, _installationId);
        var encrypted = Convert.FromBase64String(stored.ProtectedPrivateKey);
        byte[]? privateKey = null;
        try
        {
            privateKey = ProtectedData.Unprotect(encrypted, Entropy(_installationId), DataProtectionScope.CurrentUser);
            using var key = ECDsa.Create();
            key.ImportPkcs8PrivateKey(privateKey, out var bytesRead);
            if (bytesRead != privateKey.Length) throw new InvalidDataException("Unexpected signing-key bytes.");
            return key.SignData(canonicalPayload, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (CryptographicException error)
        {
            throw new InvalidDataException("The installation signing key cannot be unlocked for this Windows user.", error);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encrypted);
            if (privateKey is not null) CryptographicOperations.ZeroMemory(privateKey);
        }
    }

    public bool Verify(byte[] canonicalPayload, byte[] signature)
    {
        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(PublicKeySpki, out var bytesRead);
        return bytesRead == PublicKeySpki.Length && key.VerifyData(canonicalPayload, signature,
            HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
    }

    private static InstallationSigningIdentity Load(string path, Guid installationId)
    {
        var stored = Read(path, installationId);
        byte[]? publicKey = null; byte[]? encrypted = null; byte[]? privateKey = null;
        try
        {
            publicKey = Convert.FromBase64String(stored.PublicKeySpki);
            encrypted = Convert.FromBase64String(stored.ProtectedPrivateKey);
            privateKey = ProtectedData.Unprotect(encrypted, Entropy(installationId), DataProtectionScope.CurrentUser);
            using var key = ECDsa.Create();
            key.ImportPkcs8PrivateKey(privateKey, out var bytesRead);
            if (bytesRead != privateKey.Length || !CryptographicOperations.FixedTimeEquals(publicKey, key.ExportSubjectPublicKeyInfo()))
                throw new InvalidDataException("Installation signing key and public key disagree.");
            return new(path, installationId, publicKey.ToArray(), SHA256.HashData(publicKey));
        }
        catch (FormatException error) { throw new InvalidDataException("Installation signing identity is damaged.", error); }
        catch (CryptographicException error) { throw new InvalidDataException("Installation signing identity cannot be unlocked for this Windows user.", error); }
        finally
        {
            if (publicKey is not null) CryptographicOperations.ZeroMemory(publicKey);
            if (encrypted is not null) CryptographicOperations.ZeroMemory(encrypted);
            if (privateKey is not null) CryptographicOperations.ZeroMemory(privateKey);
        }
    }

    private static StoredSigningIdentity Read(string path, Guid installationId)
    {
        StoredSigningIdentity? stored;
        try { stored = JsonSerializer.Deserialize<StoredSigningIdentity>(File.ReadAllText(path)); }
        catch (JsonException error) { throw new InvalidDataException("Installation signing identity is damaged.", error); }
        if (stored is not { Schema: CurrentSchema, Algorithm: Algorithm } || stored.InstallationId != installationId ||
            string.IsNullOrWhiteSpace(stored.PublicKeySpki) || string.IsNullOrWhiteSpace(stored.ProtectedPrivateKey) ||
            stored.CreatedAtUtc == default)
            throw new InvalidDataException("Unknown or damaged installation signing identity; restore it rather than replacing it.");
        return stored;
    }

    private static byte[] Entropy(Guid installationId) => SHA256.HashData(
        Encoding.UTF8.GetBytes("GwentVision/upload-identity/v1/" + installationId.ToString("D")));

    private sealed record StoredSigningIdentity(int Schema, string Algorithm, Guid InstallationId,
        string PublicKeySpki, string ProtectedPrivateKey, DateTimeOffset CreatedAtUtc);
}
