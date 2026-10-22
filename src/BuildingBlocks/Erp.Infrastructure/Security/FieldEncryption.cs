using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Erp.Infrastructure.Security;

public sealed class SecurityOptions
{
    public const string Section = "Security";

    /// <summary>Base64, 32 bytes. Encrypts personal identifiers (QID, passport, IBAN) at rest.</summary>
    public string FieldEncryptionKey { get; set; } = "";

    /// <summary>Base64, 32 bytes. Keyed hash used to search/uniquely index encrypted values.</summary>
    public string BlindIndexKey { get; set; } = "";
}

/// <summary>
/// AES-256-GCM encryption for sensitive columns. Stored format: <c>v1:base64(nonce | ciphertext | tag)</c>.
/// The version prefix allows key rotation later (per-tenant data keys wrapped by Key Vault in production).
/// </summary>
public static class FieldProtector
{
    private const string Prefix = "v1:";
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private static byte[]? _encryptionKey;
    private static byte[]? _indexKey;

    public static void Configure(SecurityOptions options)
    {
        _encryptionKey = DecodeKey(options.FieldEncryptionKey, nameof(options.FieldEncryptionKey));
        _indexKey = DecodeKey(options.BlindIndexKey, nameof(options.BlindIndexKey));
    }

    public static string Encrypt(string plaintext)
    {
        var key = _encryptionKey ?? throw new InvalidOperationException("Field encryption is not configured.");
        var data = Encoding.UTF8.GetBytes(plaintext);
        var output = new byte[NonceSize + data.Length + TagSize];
        var nonce = output.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, data, output.AsSpan(NonceSize, data.Length), output.AsSpan(NonceSize + data.Length, TagSize));
        return Prefix + Convert.ToBase64String(output);
    }

    public static string Decrypt(string stored)
    {
        var key = _encryptionKey ?? throw new InvalidOperationException("Field encryption is not configured.");
        if (!stored.StartsWith(Prefix, StringComparison.Ordinal))
            throw new CryptographicException("Unknown encrypted value format.");
        var input = Convert.FromBase64String(stored[Prefix.Length..]);
        var length = input.Length - NonceSize - TagSize;
        var plain = new byte[length];
        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(input.AsSpan(0, NonceSize), input.AsSpan(NonceSize, length), input.AsSpan(NonceSize + length, TagSize), plain);
        return Encoding.UTF8.GetString(plain);
    }

    public static bool IsEncrypted(string stored) => stored.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>Deterministic keyed hash of a normalised value, for equality search and uniqueness.</summary>
    public static string BlindIndex(string value)
    {
        var key = _indexKey ?? throw new InvalidOperationException("Blind index key is not configured.");
        var normalised = value.Trim().Replace(" ", "").ToUpperInvariant();
        return Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(normalised)));
    }

    private static byte[] DecodeKey(string base64, string name)
    {
        var key = string.IsNullOrWhiteSpace(base64) ? [] : Convert.FromBase64String(base64);
        if (key.Length != 32) throw new InvalidOperationException($"Security:{name} must be a base64-encoded 32-byte key.");
        return key;
    }
}

/// <summary>
/// For a column that held plain text before it was encrypted: values written earlier are read as they are, and encrypted the next time
/// the row is saved (or by a backfill).
/// </summary>
public sealed class GraduallyEncryptedStringConverter() : ValueConverter<string?, string?>(
    v => v == null ? null : FieldProtector.Encrypt(v),
    v => v == null ? null : FieldProtector.IsEncrypted(v) ? FieldProtector.Decrypt(v) : v);

/// <summary>Encrypts optional string columns; EF never passes nulls to converters, so nulls stay NULL.</summary>
public sealed class EncryptedStringConverter() : ValueConverter<string?, string?>(
    v => v == null ? null : FieldProtector.Encrypt(v),
    v => v == null ? null : FieldProtector.Decrypt(v));
