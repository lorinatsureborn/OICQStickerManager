using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace OICQStickerManager.Services;

/// <summary>
/// NTQQ SQLCipher: 1024-byte custom header, 4096-byte pages, PBKDF2-SHA512
/// (4000 iterations), AES-256-CBC and per-page HMAC-SHA512 or legacy HMAC-SHA1.
/// WAL salts, checksums and commit boundaries are validated before applying frames.
/// </summary>
public static class SqlcipherDecryptor
{
    private const int NtqqHeaderSize = 1024;
    private const int PageSize = 4096;
    private readonly record struct CipherFormat(int ReserveLength, int MacLength, bool Legacy);
    private static readonly CipherFormat Modern = new(80, 64, false);
    private static readonly CipherFormat Legacy = new(48, 20, true);
    private static readonly byte[] SqliteMagic = "SQLite format 3\0"u8.ToArray();

    public static byte[] Decrypt(byte[] encrypted, string passphrase, byte[]? wal = null)
    {
        int bodyLength = encrypted.Length - NtqqHeaderSize;
        if (bodyLength < PageSize || bodyLength % PageSize != 0)
            throw new InvalidDataException("不支持的 NTQQ 数据库页格式");
        var salt = encrypted.AsSpan(NtqqHeaderSize, 16).ToArray();
        var password = Encoding.UTF8.GetBytes(passphrase);
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, 4000, HashAlgorithmName.SHA512, 32);
        var macKey = Rfc2898DeriveBytes.Pbkdf2(key, salt.Select(b => (byte)(b ^ 0x3a)).ToArray(), 2, HashAlgorithmName.SHA512, 32);
        try
        {
            var (frames, pages) = ReadCommittedFrames(wal, bodyLength / PageSize);
            var firstPage = frames.TryGetValue(1, out int firstOffset)
                ? wal!.AsSpan(firstOffset, PageSize) : encrypted.AsSpan(NtqqHeaderSize, PageSize);
            // Select once using authenticated bytes, never by version or unauthenticated plaintext.
            var format = AuthenticatePage(firstPage, 1, macKey, Modern) ? Modern
                : AuthenticatePage(firstPage, 1, macKey, Legacy) ? Legacy
                : throw new CryptographicException("SQLCipher 第 1 页认证失败");
            var result = new byte[checked(pages * PageSize)];
            for (int page = 1; page <= pages; page++)
            {
                ReadOnlySpan<byte> source;
                if (frames.TryGetValue(page, out int offset)) source = wal!.AsSpan(offset, PageSize);
                else if (page <= bodyLength / PageSize) source = encrypted.AsSpan(NtqqHeaderSize + (page - 1) * PageSize, PageSize);
                else throw new InvalidDataException("WAL 提交缺少数据库页");
                DecryptPage(source, result.AsSpan((page - 1) * PageSize, PageSize), page, key, macKey, format);
            }
            ValidatePlaintext(result, format);
            return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(password);
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(macKey);
        }
    }

    private static bool AuthenticatePage(ReadOnlySpan<byte> encrypted, int page, byte[] macKey, CipherFormat format)
    {
        int start = page == 1 ? 16 : 0;
        int macOffset = PageSize - format.ReserveLength + 16;
        var payload = new byte[macOffset - start + 4];
        encrypted.Slice(start, macOffset - start).CopyTo(payload);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(payload.Length - 4), page);
        var mac = format.Legacy ? HMACSHA1.HashData(macKey, payload) : HMACSHA512.HashData(macKey, payload);
        return CryptographicOperations.FixedTimeEquals(mac, encrypted.Slice(macOffset, format.MacLength));
    }

    private static void DecryptPage(ReadOnlySpan<byte> encrypted, Span<byte> output, int page, byte[] key, byte[] macKey, CipherFormat format)
    {
        if (!AuthenticatePage(encrypted, page, macKey, format))
            throw new CryptographicException($"SQLCipher 第 {page} 页认证失败");
        int start = page == 1 ? 16 : 0;
        int cipherEnd = PageSize - format.ReserveLength;
        using var aes = Aes.Create();
        aes.Key = key;
        aes.DecryptCbc(encrypted.Slice(start, cipherEnd - start), encrypted.Slice(cipherEnd, 16), output.Slice(start), PaddingMode.None);
        if (page == 1) SqliteMagic.CopyTo(output);
        output.Slice(cipherEnd, format.ReserveLength).Clear();
    }

    private static (Dictionary<int, int> Frames, int Pages) ReadCommittedFrames(byte[]? wal, int mainPages)
    {
        var result = new Dictionary<int, int>();
        if (wal == null || wal.Length == 0) return (result, mainPages);
        if (wal.Length < 32) throw new InvalidDataException("WAL 首部尚未写完");
        uint magic = Big(wal);
        if (magic is not (0x377f0682 or 0x377f0683) || Big(wal.AsSpan(4)) != 3007000 || Big(wal.AsSpan(8)) != PageSize)
            throw new InvalidDataException("不支持的 WAL 格式");
        bool bigEndian = magic == 0x377f0683;
        uint a = 0, b = 0;
        Checksum(wal.AsSpan(0, 24), bigEndian, ref a, ref b);
        if (a != Big(wal.AsSpan(24)) || b != Big(wal.AsSpan(28))) throw new InvalidDataException("WAL 首部校验失败");
        var validFrames = new List<(int Page, int Offset)>();
        int lastCommit = 0, committedPages = mainPages;
        for (int offset = 32; offset <= wal.Length - PageSize - 24; offset += PageSize + 24)
        {
            if (!wal.AsSpan(offset + 8, 8).SequenceEqual(wal.AsSpan(16, 8))) break;
            uint page = Big(wal.AsSpan(offset)), size = Big(wal.AsSpan(offset + 4));
            if (page == 0 || page > int.MaxValue / PageSize || size > int.MaxValue / PageSize)
                throw new InvalidDataException("WAL 页号越界");
            Checksum(wal.AsSpan(offset, 8), bigEndian, ref a, ref b);
            Checksum(wal.AsSpan(offset + 24, PageSize), bigEndian, ref a, ref b);
            if (a != Big(wal.AsSpan(offset + 16)) || b != Big(wal.AsSpan(offset + 20)))
                throw new InvalidDataException("WAL 帧校验失败");
            validFrames.Add(((int)page, offset + 24));
            if (size != 0) { lastCommit = validFrames.Count; committedPages = (int)size; }
        }
        for (int i = 0; i < lastCommit; i++)
            if (validFrames[i].Page <= committedPages) result[validFrames[i].Page] = validFrames[i].Offset;
        return (result, committedPages);
    }

    private static uint Big(ReadOnlySpan<byte> data) => BinaryPrimitives.ReadUInt32BigEndian(data);
    private static void Checksum(ReadOnlySpan<byte> data, bool bigEndian, ref uint a, ref uint b)
    {
        for (int offset = 0; offset < data.Length; offset += 8)
        {
            uint x = bigEndian ? Big(data.Slice(offset)) : BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset));
            uint y = bigEndian ? Big(data.Slice(offset + 4)) : BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset + 4));
            a = unchecked(a + x + b);
            b = unchecked(b + y + a);
        }
    }

    private static void ValidatePlaintext(byte[] plain, CipherFormat format)
    {
        // Older QQ can retain SQLite's 80-byte reserve while using a 48-byte cipher trailer.
        bool supportedReserve = plain.Length >= 100 && (plain[20] == 80 || (format.Legacy && plain[20] == 48));
        if (plain.Length < 100 || !plain.AsSpan(0, 16).SequenceEqual(SqliteMagic)
            || BinaryPrimitives.ReadUInt16BigEndian(plain.AsSpan(16)) != PageSize || !supportedReserve)
            throw new InvalidDataException("解密产物不是受支持的 SQLite 页格式");
    }
}
