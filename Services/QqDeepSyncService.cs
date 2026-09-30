using System.IO;
using OICQStickerManager.Models;

namespace OICQStickerManager.Services;

/// <summary>QQ 收藏深度同步策略（设计 §4.8，用户定案三档）。</summary>
public enum QqSyncStrategy
{
    /// <summary>孤儿留在镜像里，打「已移除」淡色角标，悬浮有解释。</summary>
    Mark = 0,

    /// <summary>孤儿直接从镜像移除（QQ 缓存文件永远不动）。</summary>
    Remove = 1,

    /// <summary>孤儿复制进图库（无默认标签）后从镜像移除；已在图库则仅移除。</summary>
    Adopt = 2,
}

/// <summary>
/// QQ 收藏深度同步（Phase B）：解密各绑定账号的 emoji.db，读出权威收藏 MD5 集合，
/// 与 Ori 目录对账 → 按策略处理孤儿。失败一律良性：返回 false 让上层降级回目录镜像模式。
/// </summary>
public class QqDeepSyncService
{
    private readonly string _dbKey;
    private readonly Func<string, QqEmojiService?> _serviceOf;
    private readonly Func<QqStickerModel, Task<bool>> _adoptToLibrary;

    /// <param name="adoptToLibrary">宿主提供的收编实现（复制进图库，无 QQ 标签，MD5 去重）。返回 false=重复/失败。</param>
    public QqDeepSyncService(string dbKey, Func<string, QqEmojiService?> serviceOf,
        Func<QqStickerModel, Task<bool>> adoptToLibrary)
    {
        _dbKey = dbKey;
        _serviceOf = serviceOf;
        _adoptToLibrary = adoptToLibrary;
    }

    /// <summary>对全部绑定账号执行对账。返回处理的孤儿数；-1 = 失败（不抛出，调用方降级/引导重读）。</summary>
    public async Task<int> ReconcileAllAsync(List<string> uins, QqSyncStrategy strategy)
    {
        try
        {
            int orphanCount = 0;
            foreach (var uin in uins)
            {
                var service = _serviceOf(uin);
                if (service == null) continue;

                var indexedMd5 = await ReadAuthoritativeMd5sAsync(uin);
                if (indexedMd5 == null) return -1; // 解密失败 → 整体降级

                // 目录有、索引无 = 孤儿（QQ 已取消收藏但缓存残留）；Md5 为空的残留也按孤儿处理
                var orphans = service.Mirror.Where(m => !indexedMd5.Contains(m.Md5 ?? "")).ToList();
                orphanCount += orphans.Count;
                foreach (var orphan in orphans)
                {
                    switch (strategy)
                    {
                        case QqSyncStrategy.Remove:
                            service.Mirror.Remove(orphan);
                            break;
                        case QqSyncStrategy.Adopt:
                            if (await _adoptToLibrary(orphan)) { /* 收编成功 */ }
                            service.Mirror.Remove(orphan); // 重复也已入图库，同样出镜像
                            break;
                        default:
                            orphan.IsOrphaned = true;
                            break;
                    }
                }
            }
            return orphanCount;
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>解密某账号的 emoji.db（只读快照到临时目录），返回权威收藏 MD5 集合；失败返回 null。</summary>
    private async Task<HashSet<string>?> ReadAuthoritativeMd5sAsync(string uin)
    {
        string? tempDir = null;
        try
        {
            var dbPath = Path.Combine(QqEmojiService.TencentFilesRoot, uin, "nt_qq", "nt_db", "emoji.db");
            if (!File.Exists(dbPath)) return new HashSet<string>(StringComparer.Ordinal); // 无库 = 无收藏记录

            tempDir = Path.Combine(Path.GetTempPath(), "asuka-deepsync", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            var snapshot = Path.Combine(tempDir, "emoji.db");
            await Task.Run(() => File.Copy(dbPath, snapshot, overwrite: true));
            var wal = dbPath + "-wal";
            if (File.Exists(wal))
            {
                try { File.Copy(wal, snapshot + "-wal", true); } catch { /* 快照尽力 */ }
            }

            var encrypted = await File.ReadAllBytesAsync(snapshot);
            var plain = await Task.Run(() => SqlcipherDecryptor.Decrypt(encrypted, _dbKey));
            var plainPath = Path.Combine(tempDir, "plain.db");
            await File.WriteAllBytesAsync(plainPath, plain);
            return await Task.Run(() => ReadFavMd5s(plainPath));
        }
        catch
        {
            return null; // 密钥失效/QQ 更新改结构 → 降级
        }
        finally
        {
            if (tempDir != null)
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }

    private static HashSet<string> ReadFavMd5s(string plainDbPath)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            {
                DataSource = plainDbPath,
                Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly,
            }.ToString());
        conn.Open();
        using var cmd = conn.CreateCommand();
        // 列名是混淆的纯数字（MD5 在 "80011"），必须双引号引用
        cmd.CommandText = "SELECT \"80011\" FROM fav_emoji_info_storage_table WHERE \"80011\" IS NOT NULL AND \"80011\" != ''";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var md5 = reader.GetString(0).ToUpperInvariant();
            if (md5.Length == 32) result.Add(md5);
        }
        return result;
    }
}
