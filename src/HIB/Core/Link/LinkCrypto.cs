using System;
using System.Security.Cryptography;
using System.Text;

namespace HIB.Core.Link;

/// <summary>
/// AES-256-GCM framing shared with the phone: base64( nonce(12) ‖ ciphertext ‖ tag(16) ), no AAD.
/// </summary>
public static class LinkCrypto
{
    public const int KeySize = 32;
    public const int NonceSize = 12;
    public const int TagSize = 16;

    public static byte[] GenerateKey() => RandomNumberGenerator.GetBytes(KeySize);

    public static string Encrypt(string plaintext, byte[] key) => Encrypt(Encoding.UTF8.GetBytes(plaintext), key);

    public static string Encrypt(byte[] plaintext, byte[] key)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var output = new byte[NonceSize + plaintext.Length + TagSize];
        using var gcm = new AesGcm(key, TagSize);
        gcm.Encrypt(nonce, plaintext,
            output.AsSpan(NonceSize, plaintext.Length),
            output.AsSpan(NonceSize + plaintext.Length, TagSize));
        nonce.CopyTo(output, 0);
        return Convert.ToBase64String(output);
    }

    /// <summary>Returns the UTF-8 plaintext, or null if the frame is not valid for this key.</summary>
    public static string? TryDecrypt(string frame, byte[] key)
    {
        byte[] combined;
        try { combined = Convert.FromBase64String(frame.Trim()); }
        catch (FormatException) { return null; }
        if (combined.Length < NonceSize + TagSize) return null;
        var ctLen = combined.Length - NonceSize - TagSize;
        var pt = new byte[ctLen];
        try
        {
            using var gcm = new AesGcm(key, TagSize);
            gcm.Decrypt(combined.AsSpan(0, NonceSize),
                combined.AsSpan(NonceSize, ctLen),
                combined.AsSpan(NonceSize + ctLen, TagSize),
                pt);
        }
        catch (CryptographicException) { return null; }
        return Encoding.UTF8.GetString(pt);
    }
}
