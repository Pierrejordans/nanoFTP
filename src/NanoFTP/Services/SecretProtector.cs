using System.Security.Cryptography;
using System.Text;

namespace NanoFTP.Services;

/// <summary>
/// Protège les mots de passe au repos avec AES-GCM. La clé est créée une seule fois
/// dans le dossier de données de l'utilisateur.
/// </summary>
public sealed class SecretProtector
{
    private const string Prefix = "v1.";
    private readonly byte[] _key;

    public SecretProtector(string keyPath)
    {
        _key = LoadOrCreateKey(keyPath);
    }

    public string Protect(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var plaintext = Encoding.UTF8.GetBytes(value);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];

        using var aes = new AesGcm(_key, tag.Length);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        var payload = new byte[nonce.Length + tag.Length + ciphertext.Length];
        Buffer.BlockCopy(nonce, 0, payload, 0, nonce.Length);
        Buffer.BlockCopy(tag, 0, payload, nonce.Length, tag.Length);
        Buffer.BlockCopy(ciphertext, 0, payload, nonce.Length + tag.Length, ciphertext.Length);

        return Prefix + Convert.ToBase64String(payload);
    }

    public string Unprotect(string? value)
    {
        if (string.IsNullOrEmpty(value) || !value.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return string.Empty;
        }

        try
        {
            var payload = Convert.FromBase64String(value[Prefix.Length..]);
            if (payload.Length < 29)
            {
                return string.Empty;
            }

            var nonce = payload.AsSpan(0, 12);
            var tag = payload.AsSpan(12, 16);
            var ciphertext = payload.AsSpan(28);
            var plaintext = new byte[ciphertext.Length];

            using var aes = new AesGcm(_key, 16);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
            return Encoding.UTF8.GetString(plaintext);
        }
        catch (CryptographicException)
        {
            return string.Empty;
        }
        catch (FormatException)
        {
            return string.Empty;
        }
    }

    private static byte[] LoadOrCreateKey(string keyPath)
    {
        var directory = Path.GetDirectoryName(keyPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (File.Exists(keyPath))
        {
            var existing = File.ReadAllBytes(keyPath);
            if (existing.Length == 32)
            {
                return existing;
            }

            throw new InvalidDataException("Le fichier de clé local de NanoFTP est invalide.");
        }

        var key = RandomNumberGenerator.GetBytes(32);
        File.WriteAllBytes(keyPath, key);
        TryRestrictPermissions(keyPath);
        return key;
    }

    internal static void TryRestrictPermissions(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (IOException)
        {
            // Le chiffrement reste fonctionnel même si la permission ne peut pas être resserrée.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
