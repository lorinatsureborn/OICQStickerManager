using System.IO;

namespace OICQStickerManager.Services;

internal sealed record QqDatabaseSnapshot(byte[] Database, byte[]? Wal)
{
    internal static async Task<QqDatabaseSnapshot> ReadAsync(string databasePath)
    {
        var main = await ReadSharedAsync(databasePath).ConfigureAwait(false);
        var wal = await ReadWalAsync(databasePath + "-wal").ConfigureAwait(false);
        var mainAfter = await ReadSharedAsync(databasePath).ConfigureAwait(false);
        var walAfter = await ReadWalAsync(databasePath + "-wal").ConfigureAwait(false);
        if (!main.AsSpan().SequenceEqual(mainAfter)
            || (wal == null) != (walAfter == null)
            || (wal != null && (walAfter!.Length < wal.Length || !wal.AsSpan().SequenceEqual(walAfter.AsSpan(0, wal.Length)))))
            throw new QqSnapshotChangedException();
        return new(main, wal);
    }

    private static async Task<byte[]?> ReadWalAsync(string path)
    {
        try { return await ReadSharedAsync(path).ConfigureAwait(false); }
        catch (FileNotFoundException) { return null; }
    }

    private static async Task<byte[]> ReadSharedAsync(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 81920, useAsync: true);
        if (stream.Length > 256L * 1024 * 1024) throw new IOException("QQ 索引快照超过读取上限");
        var bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes).ConfigureAwait(false);
        return bytes;
    }
}

internal sealed class QqSnapshotChangedException : IOException
{
    internal QqSnapshotChangedException() : base("QQ 数据库快照正在变化") { }
}
