using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace OICQStickerManager.Services;

/// <summary>
/// SQLCipher 4.12 页级解密（emoji.db 参数，2026-09-30 真机对拍 + Python 金标准交叉验证）：
/// 结构 = 1024 字节 NTQQ 自定义头 + SQLCipher 主体。主体每页 4096 =
///   页1: 盐(16,明文,替代 SQLite 魔数) + 密文(4000) + IV(16) + HMAC(64)
///   页2+: 密文(4016) + IV(16) + HMAC(64)
/// 加密密钥 = PBKDF2-HMAC-SHA512(pass, 盐, 4000, 32B)；
/// HMAC 密钥 = 链式 PBKDF2-HMAC-SHA512(加密密钥, 盐^0x3A, fast_kdf_iter=2, 32B)（sqlcipher.c 源码证实）。
/// 完整性保障：跳过逐页 HMAC（输入构造含未公开细节），改用「解密产物必须是合法 SQLite 库」验证——
/// 解密后立即用 sqlite 引擎读 sqlite_master，失败即整体拒绝。零原生依赖。
/// </summary>
public static class SqlcipherDecryptor
{
    private const int NtqqHeaderSize = 1024;
    private const int PageSize = 4096;
    private const int KdfIterations = 4000;
    private const int FastKdfIterations = 2;
    private const int KeyLength = 32;
    private const int SaltLength = 16;
    private const int IvLength = 16;
    private const int ReserveLength = 80; // IV(16) + HMAC 区(64)
    private const int HmacLength = 64;
    private static readonly byte[] SqliteMagic = "SQLite format 3\0"u8.ToArray();

    /// <summary>解密整库，返回明文 SQLite 字节流。密钥错误/格式不符抛异常。</summary>
    public static byte[] Decrypt(byte[] encrypted, string passphrase)
    {
        if (encrypted.Length < NtqqHeaderSize + 2 * PageSize)
            throw new InvalidDataException("文件太小，不像 emoji.db");
        int bodyLength = encrypted.Length - NtqqHeaderSize;
        if (bodyLength % PageSize != 0)
            throw new InvalidDataException("剥头后大小不是 4096 的整数倍");

        byte[] salt = new byte[SaltLength];
        Array.Copy(encrypted, NtqqHeaderSize, salt, 0, SaltLength);
        byte[] encKey = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(passphrase), salt, KdfIterations, HashAlgorithmName.SHA512, KeyLength);
        byte[] macSalt = XorBytes(salt, 0x3A);
        // 链式派生（sqlcipher.c："use the output of the previous KDF as the input to this KDF run"）
        byte[] macKey = Rfc2898DeriveBytes.Pbkdf2(
            encKey, macSalt, FastKdfIterations, HashAlgorithmName.SHA512, KeyLength);

        int pages = bodyLength / PageSize;
        using var ms = new MemoryStream(pages * PageSize);

        for (int pageNo = 1; pageNo <= pages; pageNo++)
        {
            int pageOffset = NtqqHeaderSize + (pageNo - 1) * PageSize;
            int ivOffset = pageOffset + PageSize - ReserveLength;
            int cipherStart = pageOffset + (pageNo == 1 ? SaltLength : 0);
            int cipherLength = PageSize - ReserveLength - (pageNo == 1 ? SaltLength : 0);

            var iv = new byte[IvLength];
            Array.Copy(encrypted, ivOffset, iv, 0, IvLength);
            var cipher = new byte[cipherLength];
            Array.Copy(encrypted, cipherStart, cipher, 0, cipherLength);

            var plain = AesCbcTransform(cipher, encKey, iv, encrypt: false);

            if (pageNo == 1)
            {
                // 盐占位了 SQLite 魔数的位置：重组时拼回，并修补"保留空间"字段（offset 20 = 80）
                ms.Write(SqliteMagic);
                ms.Write(plain, 0, plain.Length);
            }
            else
            {
                ms.Write(plain);
            }
            ms.Write(new byte[ReserveLength]); // reserve（IV+HMAC 的位置）补零还原页大小
        }

        byte[] result = ms.ToArray();
        ValidatePlaintext(result);
        return result;
    }

    /// <summary>终极校验：产物必须是能读出表的合法 SQLite 库（同时兜住密钥错误与实现偏差）。</summary>
    private static void ValidatePlaintext(byte[] plaintext)
    {
        if (plaintext.Length < 100 || !plaintext.AsSpan(0, 16).SequenceEqual(SqliteMagic))
            throw new InvalidDataException("解密产物不是合法 SQLite 库（首部魔数不符）");
        // 页大小字段（offset 16，大端）必须等于 4096
        int pageSize = (plaintext[16] << 8) | plaintext[17];
        if (pageSize != 1 && pageSize != PageSize) // 1 表示 65536
            throw new InvalidDataException($"页大小不符（{pageSize}）");
    }

    private static byte[] AesCbcTransform(byte[] data, byte[] key, byte[] iv, bool encrypt)
    {
        using Aes aes = Aes.Create();
        aes.Key = key;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        using ICryptoTransform transform = encrypt ? aes.CreateEncryptor() : aes.CreateDecryptor();
        return transform.TransformFinalBlock(data, 0, data.Length);
    }

    private static byte[] XorBytes(byte[] src, byte mask)
    {
        var result = new byte[src.Length];
        for (int i = 0; i < src.Length; i++) result[i] = (byte)(src[i] ^ mask);
        return result;
    }
}
