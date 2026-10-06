using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using OICQStickerManager.Services;

internal static class CipherTests
{
    internal const string Password = "audit-only-key!1x";
    internal static readonly byte[] Salt = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();
    internal static (string Name, Action Run)[] Cases =>
    [
        ("SQLCipher rejects corruption outside the SQLite header", CorruptPageIsRejected),
        ("SQLCipher rejects a 65536 header in a 4096 page stream", UnsupportedPageSize),
        ("SQLCipher authenticates and decrypts all supported pages", SupportedPages),
        ("Legacy SHA1 pages authenticate with the QQ 80-byte SQLite reserve", LegacyPages),
        ("Legacy SHA1 pages retain a 48-byte SQLite reserve", LegacySmallReserve),
        ("Legacy SHA1 rejects corruption after the first page", LegacyCorruption),
        ("Legacy SHA1 rejects the wrong passphrase", LegacyWrongKey),
        ("SQLCipher rejects mixed authentication formats across pages", MixedFormats),
        ("SQLCipher rejects a corrupted legacy first page", LegacyFirstPageCorruption),
    ];
    internal static byte[] Plain(int size = 4096)
    {
        var plain = new byte[8192];
        "SQLite format 3\0"u8.CopyTo(plain);
        BinaryPrimitives.WriteUInt16BigEndian(plain.AsSpan(16), (ushort)(size == 65536 ? 1 : size));
        plain[18] = plain[19] = 1;
        plain[20] = 80;
        plain[4096] = 13;
        plain[4112] = 37;
        return plain;
    }
    internal static byte[] Encrypt(byte[] plain, bool legacy = false)
    {
        var result = new byte[1024 + plain.Length];
        for (int page = 1; page <= plain.Length / 4096; page++)
            EncryptPage(plain.AsSpan((page - 1) * 4096, 4096), page, legacy).CopyTo(result, 1024 + (page - 1) * 4096);
        return result;
    }
    internal static byte[] EncryptPage(ReadOnlySpan<byte> plain, int page, bool legacy = false)
    {
        byte[] key = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(Password), Salt, 4000, HashAlgorithmName.SHA512, 32);
        byte[] macKey = Rfc2898DeriveBytes.Pbkdf2(key, Salt.Select(b => (byte)(b ^ 0x3a)).ToArray(), 2, HashAlgorithmName.SHA512, 32);
        var result = new byte[4096];
        int start = page == 1 ? 16 : 0;
        if (page == 1) Salt.CopyTo(result, 0);
        var iv = Enumerable.Repeat((byte)(page + 10), 16).ToArray();
        using var aes = Aes.Create();
        aes.Key = key;
        int cipherEnd = legacy ? 4048 : 4016;
        aes.EncryptCbc(plain.Slice(start, cipherEnd - start), iv, PaddingMode.None).CopyTo(result, start);
        iv.CopyTo(result, cipherEnd);
        var payload = new byte[cipherEnd + 16 - start + 4];
        result.AsSpan(start, cipherEnd + 16 - start).CopyTo(payload);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(payload.Length - 4), page);
        if (legacy)
        {
            HMACSHA1.HashData(macKey, payload).CopyTo(result, 4064);
            result.AsSpan(4084, 12).Fill(0xc5);
        }
        else HMACSHA512.HashData(macKey, payload).CopyTo(result, 4032);
        return result;
    }
    private static void CorruptPageIsRejected()
    {
        var encrypted = Encrypt(Plain());
        encrypted[1024 + 4096 + 100] ^= 1;
        bool rejected = false;
        try { SqlcipherDecryptor.Decrypt(encrypted, Password); } catch (CryptographicException) { rejected = true; }
        Program.Require(rejected, "unauthenticated damaged page was accepted");
    }
    private static void UnsupportedPageSize()
    {
        bool rejected = false;
        try { SqlcipherDecryptor.Decrypt(Encrypt(Plain(65536)), Password); } catch (InvalidDataException) { rejected = true; }
        Program.Require(rejected, "65536 page header was accepted by the fixed 4096 decoder");
    }
    private static void SupportedPages()
    {
        var plain = Plain();
        Program.Require(SqlcipherDecryptor.Decrypt(Encrypt(plain), Password).SequenceEqual(plain), "supported authenticated pages were not preserved");
    }
    private static void LegacyPages()
    {
        var plain = Plain();
        Program.Require(SqlcipherDecryptor.Decrypt(Encrypt(plain, true), Password).SequenceEqual(plain), "valid old QQ pages were rejected or changed");
    }
    private static void LegacySmallReserve()
    {
        var plain = Plain();
        plain[20] = 48;
        Program.Require(SqlcipherDecryptor.Decrypt(Encrypt(plain, true), Password).SequenceEqual(plain), "legacy cipher reserve was forced to 80 bytes");
    }
    private static void LegacyCorruption()
    {
        var encrypted = Encrypt(Plain(), true);
        encrypted[1024 + 4096 + 100] ^= 1;
        RequireRejected(encrypted, Password, "legacy later-page authentication was skipped", 2);
    }
    private static void LegacyWrongKey() => RequireRejected(Encrypt(Plain(), true), "wrong-only-key!1x", "wrong legacy passphrase was accepted");
    private static void MixedFormats()
    {
        var encrypted = Encrypt(Plain(), true);
        EncryptPage(Plain().AsSpan(4096), 2).CopyTo(encrypted, 5120);
        RequireRejected(encrypted, Password, "page format changed after first-page authentication", 2);
    }
    private static void LegacyFirstPageCorruption()
    {
        var encrypted = Encrypt(Plain(), true);
        encrypted[1024 + 100] ^= 1;
        RequireRejected(encrypted, Password, "legacy format fallback accepted a corrupted first page");
    }
    private static void RequireRejected(byte[] encrypted, string password, string message, int page = 1)
    {
        bool rejected = false;
        try { SqlcipherDecryptor.Decrypt(encrypted, password); }
        catch (CryptographicException ex) { rejected = ex.Message.Contains($"第 {page} 页", StringComparison.Ordinal); }
        Program.Require(rejected, message);
    }
}
