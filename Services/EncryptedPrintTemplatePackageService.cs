using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SpeedEmulator.Models;

namespace SpeedEmulator.Services;

public static class EncryptedPrintTemplatePackageService
{
    private const string PackageMagic = "SpeedEmulator.PrintTemplate";
    private const int PackageVersion = 1;
    private const string AlgorithmName = "AES-256-GCM";
    private const string KdfName = "PBKDF2-SHA256-180000";
    private const string CompressionName = "gzip";
    private const int SaltSize = 16;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;
    private const int KdfIterations = 180_000;

    public static async Task ExportAsync(PrintTemplate template, string fileName, JsonSerializerOptions jsonOptions)
    {
        var json = JsonSerializer.Serialize(template, jsonOptions);
        var compressed = Compress(Encoding.UTF8.GetBytes(json));

        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var tag = new byte[TagSize];
        var cipherText = new byte[compressed.Length];
        var key = DeriveKey(salt);

        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Encrypt(nonce, compressed, cipherText, tag, CreateAssociatedData());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(compressed);
        }

        var package = new EncryptedTemplatePackage
        {
            Salt = Convert.ToBase64String(salt),
            Nonce = Convert.ToBase64String(nonce),
            Tag = Convert.ToBase64String(tag),
            CipherText = Convert.ToBase64String(cipherText)
        };

        var packageJson = JsonSerializer.Serialize(package, jsonOptions);
        await File.WriteAllTextAsync(fileName, packageJson, Encoding.UTF8);
    }

    public static async Task<PrintTemplate?> ImportAsync(string fileName, JsonSerializerOptions jsonOptions)
    {
        var text = await File.ReadAllTextAsync(fileName, Encoding.UTF8);
        var package = TryReadPackage(text, jsonOptions);
        if (package is null)
        {
            return JsonSerializer.Deserialize<PrintTemplate>(text, jsonOptions);
        }

        ValidatePackage(package);

        var salt = DecodeRequired(package.Salt, "salt");
        var nonce = DecodeRequired(package.Nonce, "nonce");
        var tag = DecodeRequired(package.Tag, "tag");
        var cipherText = DecodeRequired(package.CipherText, "cipherText");
        var plainText = new byte[cipherText.Length];
        var key = DeriveKey(salt);

        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, cipherText, tag, plainText, CreateAssociatedData());
            var decompressed = Decompress(plainText);
            var json = Encoding.UTF8.GetString(decompressed);
            return JsonSerializer.Deserialize<PrintTemplate>(json, jsonOptions);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidDataException("模板文件不是本系统导出的加密模板，或文件已损坏。", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plainText);
        }
    }

    private static EncryptedTemplatePackage? TryReadPackage(string text, JsonSerializerOptions jsonOptions)
    {
        try
        {
            var package = JsonSerializer.Deserialize<EncryptedTemplatePackage>(text, jsonOptions);
            return string.Equals(package?.Magic, PackageMagic, StringComparison.Ordinal)
                ? package
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void ValidatePackage(EncryptedTemplatePackage package)
    {
        if (package.Version != PackageVersion
            || !string.Equals(package.Algorithm, AlgorithmName, StringComparison.Ordinal)
            || !string.Equals(package.Kdf, KdfName, StringComparison.Ordinal)
            || !string.Equals(package.Compression, CompressionName, StringComparison.Ordinal))
        {
            throw new InvalidDataException("模板文件版本或加密格式不受支持。");
        }
    }

    private static byte[] DecodeRequired(string value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException($"模板文件缺少 {fieldName} 字段。");
        }

        try
        {
            return Convert.FromBase64String(value);
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException($"模板文件 {fieldName} 字段格式不正确。", ex);
        }
    }

    private static byte[] DeriveKey(byte[] salt)
    {
        return Rfc2898DeriveBytes.Pbkdf2(
            GetApplicationSecret(),
            salt,
            KdfIterations,
            HashAlgorithmName.SHA256,
            KeySize);
    }

    private static byte[] GetApplicationSecret()
    {
        var secret = new byte[KeySize];
        Mix(secret, Convert.FromHexString("B6D21E7C3F8849AA61E00D8BC472F2E114C0E44FA917703A0D89E2D7171F6AA2"), 0);
        Mix(secret, Convert.FromHexString("2A49A6EF8C1703D4D5AF7339E20C9AB03F7B624C1185E9D8A4C631D02E55B097"), 7);
        Mix(secret, Convert.FromHexString("7F1C90B27A496142DAE63821BA0298DD4F6EE903A15C26497AE4D5B236EF7C03"), 13);

        var context = Encoding.UTF8.GetBytes("SpeedEmulator.Template.Package.V1");
        var combined = new byte[secret.Length + context.Length];
        Buffer.BlockCopy(secret, 0, combined, 0, secret.Length);
        Buffer.BlockCopy(context, 0, combined, secret.Length, context.Length);
        return SHA256.HashData(combined);
    }

    private static void Mix(byte[] target, byte[] source, int offset)
    {
        for (var index = 0; index < source.Length; index++)
        {
            target[(index + offset) % target.Length] ^= source[index];
        }
    }

    private static byte[] CreateAssociatedData()
    {
        return Encoding.UTF8.GetBytes($"{PackageMagic}|{PackageVersion}|{AlgorithmName}|{KdfName}|{CompressionName}");
    }

    private static byte[] Compress(byte[] source)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            gzip.Write(source, 0, source.Length);
        }

        return output.ToArray();
    }

    private static byte[] Decompress(byte[] source)
    {
        using var input = new MemoryStream(source);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return output.ToArray();
    }

    private sealed class EncryptedTemplatePackage
    {
        public string Magic { get; set; } = PackageMagic;

        public int Version { get; set; } = PackageVersion;

        public string Algorithm { get; set; } = AlgorithmName;

        public string Kdf { get; set; } = KdfName;

        public string Compression { get; set; } = CompressionName;

        public string Salt { get; set; } = string.Empty;

        public string Nonce { get; set; } = string.Empty;

        public string Tag { get; set; } = string.Empty;

        public string CipherText { get; set; } = string.Empty;
    }
}
