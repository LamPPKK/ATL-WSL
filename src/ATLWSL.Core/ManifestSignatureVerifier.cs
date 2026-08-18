using System.Text.Json;
using NSec.Cryptography;

namespace AtlWsl.Core;

public sealed class ManifestSignatureVerifier(IReadOnlyDictionary<string, byte[]> keys)
{
    public static ManifestSignatureVerifier Load(Stream stream)
    {
        var ring = JsonSerializer.Deserialize<KeyRing>(stream, ReleaseManifest.JsonOptions)
            ?? throw new InvalidDataException("Public-key ring is empty.");
        if (ring.SchemaVersion != 1) throw new InvalidDataException("Unsupported public-key ring schema.");
        var parsed = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var entry in ring.Keys)
        {
            byte[] raw;
            try { raw = Convert.FromBase64String(entry.PublicKeyBase64); }
            catch (FormatException exception) { throw new InvalidDataException("Release public key is not valid Base64.", exception); }
            if (entry.Algorithm != "Ed25519" || string.IsNullOrWhiteSpace(entry.KeyId) || raw.Length != 32 ||
                !parsed.TryAdd(entry.KeyId, raw))
                throw new InvalidDataException("Release key ring contains an invalid or duplicate Ed25519 key.");
        }
        if (parsed.Count == 0) throw new InvalidDataException("Release key ring contains no keys.");
        return new ManifestSignatureVerifier(parsed);
    }

    public bool Verify(ReadOnlySpan<byte> data, string signatureBase64, string keyId)
    {
        if (!keys.TryGetValue(keyId, out var raw) || raw.Length != 32 || raw.All(value => value == 0)) return false;
        byte[] signature;
        try { signature = Convert.FromBase64String(signatureBase64.Trim()); }
        catch (FormatException) { return false; }
        if (signature.Length != 64) return false;
        var algorithm = SignatureAlgorithm.Ed25519;
        var publicKey = PublicKey.Import(algorithm, raw, KeyBlobFormat.RawPublicKey);
        return algorithm.Verify(publicKey, data, signature);
    }

    public void RequireValid(byte[] data, string? signature, ReleaseManifest manifest)
    {
        if (manifest.IsDevelopment && string.IsNullOrWhiteSpace(signature)) return;
        if (string.IsNullOrWhiteSpace(signature) || !Verify(data, signature, manifest.SigningKeyId))
            throw new InvalidDataException("Release manifest signature verification failed.");
    }

    private sealed class KeyRing { public int SchemaVersion { get; init; } public IReadOnlyList<Key> Keys { get; init; } = []; }
    private sealed class Key { public string KeyId { get; init; } = string.Empty; public string Algorithm { get; init; } = string.Empty; public string PublicKeyBase64 { get; init; } = string.Empty; }
}
