using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;
using OICQStickerManager.Services;

internal static class WalTests
{
    internal static (string Name, Action Run)[] Cases =>
    [
        ("Encrypted WAL applies only the last committed transaction", LastCommit),
        ("Encrypted WAL accepts both checksum byte orders", BigEndianChecksum),
        ("Encrypted WAL rejects an invalid header checksum", InvalidHeader),
        ("SQLite queries include a favorite committed only in encrypted WAL", QueryCommittedFavorite),
        ("Online database snapshot includes its stable encrypted WAL", () => Program.RunAsync(SnapshotIncludesWal())),
        ("Legacy SHA1 WAL applies only the last committed transaction", LegacyLastCommit),
        ("Legacy SHA1 WAL rejects a corrupt page even with valid WAL checksums", LegacyCorruptFrame),
        ("SQLite queries include a favorite committed only in legacy SHA1 WAL", () => QueryCommittedFavorite(true)),
    ];
    internal static byte[] Wal(params (int Page, int Size, byte[] Data)[] frames) => Wal(false, frames);
    private static byte[] Wal(bool bigEndian, params (int Page, int Size, byte[] Data)[] frames)
    {
        var result = new byte[32 + 4120 * frames.Length];
        Put(result, 0, bigEndian ? 0x377f0683u : 0x377f0682u);
        Put(result, 4, 3007000); Put(result, 8, 4096); Put(result, 16, 123); Put(result, 20, 456);
        uint a = 0, b = 0;
        Checksum(result.AsSpan(0, 24), bigEndian, ref a, ref b);
        Put(result, 24, a); Put(result, 28, b);
        for (int i = 0; i < frames.Length; i++)
        {
            int offset = 32 + i * 4120;
            Put(result, offset, (uint)frames[i].Page); Put(result, offset + 4, (uint)frames[i].Size);
            result.AsSpan(16, 8).CopyTo(result.AsSpan(offset + 8));
            frames[i].Data.CopyTo(result, offset + 24);
            Checksum(result.AsSpan(offset, 8), bigEndian, ref a, ref b);
            Checksum(result.AsSpan(offset + 24, 4096), bigEndian, ref a, ref b);
            Put(result, offset + 16, a); Put(result, offset + 20, b);
        }
        return result;
    }
    private static void Checksum(ReadOnlySpan<byte> data, bool bigEndian, ref uint a, ref uint b)
    {
        for (int i = 0; i < data.Length; i += 8)
        {
            uint x = bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(data.Slice(i)) : BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(i));
            uint y = bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(data.Slice(i + 4)) : BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(i + 4));
            a = unchecked(a + x + b); b = unchecked(b + y + a);
        }
    }
    private static void Put(byte[] data, int offset, uint value) => BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(offset), value);
    private static byte[] Page(byte value)
    {
        var page = CipherTests.Plain().AsSpan(4096).ToArray(); page[100] = value;
        return CipherTests.EncryptPage(page, 2);
    }
    private static void LastCommit()
    {
        var wal = Wal((2, 2, Page(10)), (2, 2, Page(20)), (2, 0, Page(30)));
        var plain = SqlcipherDecryptor.Decrypt(CipherTests.Encrypt(CipherTests.Plain()), CipherTests.Password, wal);
        Program.Require(plain[4096 + 100] == 20, "committed WAL page was ignored or replaced by an uncommitted frame");
    }
    private static void BigEndianChecksum()
    {
        var plain = SqlcipherDecryptor.Decrypt(CipherTests.Encrypt(CipherTests.Plain()), CipherTests.Password, Wal(true, (2, 2, Page(20))));
        Program.Require(plain[4196] == 20, "big-endian checksum WAL was not applied");
    }
    private static void InvalidHeader()
    {
        var wal = Wal((2, 2, Page(20))); wal[24] ^= 1;
        bool rejected = false;
        try { SqlcipherDecryptor.Decrypt(CipherTests.Encrypt(CipherTests.Plain()), CipherTests.Password, wal); }
        catch (InvalidDataException) { rejected = true; }
        Program.Require(rejected, "invalid WAL was silently ignored");
    }
    private static void LegacyLastCommit()
    {
        var committed = CipherTests.Plain().AsSpan(4096).ToArray(); committed[100] = 21;
        var pending = committed.ToArray(); pending[100] = 31;
        var wal = Wal((2, 2, CipherTests.EncryptPage(committed, 2, true)), (2, 0, CipherTests.EncryptPage(pending, 2, true)));
        var plain = SqlcipherDecryptor.Decrypt(CipherTests.Encrypt(CipherTests.Plain(), true), CipherTests.Password, wal);
        Program.Require(plain[4196] == 21, "legacy committed WAL content was lost or replaced by a pending frame");
    }
    private static void LegacyCorruptFrame()
    {
        var page = CipherTests.EncryptPage(CipherTests.Plain().AsSpan(4096), 2, true);
        page[100] ^= 1;
        bool rejected = false;
        try { SqlcipherDecryptor.Decrypt(CipherTests.Encrypt(CipherTests.Plain(), true), CipherTests.Password, Wal((2, 2, page))); }
        catch (System.Security.Cryptography.CryptographicException ex) { rejected = ex.Message.Contains("第 2 页", StringComparison.Ordinal); }
        Program.Require(rejected, "valid WAL rolling checksum bypassed legacy page authentication");
    }
    private static void QueryCommittedFavorite() => QueryCommittedFavorite(false);
    private static void QueryCommittedFavorite(bool legacy)
    {
        var root = Program.NewDirectory();
        var path = Path.Combine(root, "source.db");
        using var source = new SqliteConnection($"Data Source={path};Pooling=False"); source.Open();
        int reserve = 80;
        Program.Require(FileControl(source.Handle!.DangerousGetHandle(), "main", 38, ref reserve) == 0, "fixture reserve setup failed");
        using var command = source.CreateCommand();
        command.CommandText = "PRAGMA page_size=4096; VACUUM; CREATE TABLE fav_emoji_info_storage_table ([80011] TEXT); INSERT INTO fav_emoji_info_storage_table VALUES ('AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA'); PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0;";
        command.ExecuteNonQuery();
        var main = ReadShared(path);
        Program.Require(main[20] == 80, "fixture did not reserve SQLCipher space");
        command.CommandText = "INSERT INTO fav_emoji_info_storage_table VALUES ('BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB');"; command.ExecuteNonQuery();
        var plainWal = ReadShared(path + "-wal");
        var frames = new List<(int Page, int Size, byte[] Data)>();
        for (int offset = 32; offset + 4120 <= plainWal.Length; offset += 4120)
        {
            int page = (int)BinaryPrimitives.ReadUInt32BigEndian(plainWal.AsSpan(offset));
            int size = (int)BinaryPrimitives.ReadUInt32BigEndian(plainWal.AsSpan(offset + 4));
            frames.Add((page, size, CipherTests.EncryptPage(plainWal.AsSpan(offset + 24, 4096), page, legacy)));
        }
        var plain = SqlcipherDecryptor.Decrypt(CipherTests.Encrypt(main, legacy), CipherTests.Password, Wal(frames.ToArray()));
        var output = Path.Combine(root, "output.db"); File.WriteAllBytes(output, plain);
        using var connection = new SqliteConnection($"Data Source={output};Mode=ReadOnly;Pooling=False"); connection.Open();
        using var query = connection.CreateCommand(); query.CommandText = "SELECT COUNT(*) FROM fav_emoji_info_storage_table";
        Program.Require(Convert.ToInt32(query.ExecuteScalar()) == 2, "WAL-only favorite was missing from the readable SQLite snapshot");
    }
    private static async Task SnapshotIncludesWal()
    {
        var path = Path.Combine(Program.NewDirectory(), "emoji.db");
        var database = CipherTests.Encrypt(CipherTests.Plain());
        var wal = Wal((2, 2, Page(20)));
        await File.WriteAllBytesAsync(path, database);
        await File.WriteAllBytesAsync(path + "-wal", wal);
        using var shared = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        var snapshot = await QqDatabaseSnapshot.ReadAsync(path);
        Program.Require(snapshot.Wal != null && snapshot.Wal.SequenceEqual(wal) && snapshot.Database.SequenceEqual(database), "online snapshot omitted WAL or failed on a writer's shared handle");
    }
    internal static byte[] ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var bytes = new byte[stream.Length]; stream.ReadExactly(bytes); return bytes;
    }
    [DllImport("e_sqlite3", EntryPoint = "sqlite3_file_control", CallingConvention = CallingConvention.Cdecl)]
    private static extern int FileControl(IntPtr database, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int operation, ref int value);
}
