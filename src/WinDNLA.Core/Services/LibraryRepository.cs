using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using WinDNLA.Core.Models;

namespace WinDNLA.Core.Services;

public sealed class LibraryRepository : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly object _lock = new();

    public LibraryRepository()
    {
        AppPaths.EnsureCreated();
        _connection = new SqliteConnection($"Data Source={AppPaths.LibraryDb}");
        _connection.Open();
        using (var pragma = _connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
            pragma.ExecuteNonQuery();
        }
        Initialize();
    }

    private void Initialize()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            """
            CREATE TABLE IF NOT EXISTS meta (
              key TEXT PRIMARY KEY,
              value TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS folders (
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              parent_id INTEGER NULL,
              root_path TEXT NOT NULL,
              relative_path TEXT NOT NULL,
              name TEXT NOT NULL,
              object_id TEXT NOT NULL UNIQUE
            );
            CREATE TABLE IF NOT EXISTS videos (
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              folder_id INTEGER NOT NULL,
              path TEXT NOT NULL UNIQUE,
              title TEXT NOT NULL,
              size INTEGER NOT NULL,
              mtime_utc_ticks INTEGER NOT NULL,
              duration REAL NOT NULL,
              container TEXT NOT NULL,
              video_codec TEXT NOT NULL,
              audio_codec TEXT NOT NULL,
              width INTEGER NOT NULL,
              height INTEGER NOT NULL,
              thumb_path TEXT NULL,
              object_id TEXT NOT NULL UNIQUE,
              FOREIGN KEY(folder_id) REFERENCES folders(id) ON DELETE CASCADE
            );
            INSERT OR IGNORE INTO meta(key, value) VALUES('system_update_id', '1');
            """;
        cmd.ExecuteNonQuery();
        DropLegacyNeedsTranscodeColumn();
    }

    private void DropLegacyNeedsTranscodeColumn()
    {
        using var check = _connection.CreateCommand();
        check.CommandText = "PRAGMA table_info(videos)";
        using var reader = check.ExecuteReader();
        var hasColumn = false;
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), "needs_transcode", StringComparison.OrdinalIgnoreCase))
            {
                hasColumn = true;
                break;
            }
        }
        reader.Close();
        if (!hasColumn) return;

        using var drop = _connection.CreateCommand();
        drop.CommandText = "ALTER TABLE videos DROP COLUMN needs_transcode";
        drop.ExecuteNonQuery();
    }

    public long GetSystemUpdateId()
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT value FROM meta WHERE key='system_update_id'";
            return long.Parse((string)cmd.ExecuteScalar()!);
        }
    }

    public void BumpSystemUpdateId()
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                "UPDATE meta SET value = CAST(CAST(value AS INTEGER) + 1 AS TEXT) WHERE key='system_update_id'";
            cmd.ExecuteNonQuery();
        }
    }

    public LibraryStats GetStats()
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                """
                SELECT
                  (SELECT COUNT(*) FROM folders),
                  (SELECT COUNT(*) FROM videos),
                  (SELECT CAST(value AS INTEGER) FROM meta WHERE key='system_update_id')
                """;
            using var reader = cmd.ExecuteReader();
            reader.Read();
            return new LibraryStats
            {
                FolderCount = reader.GetInt32(0),
                VideoCount = reader.GetInt32(1),
                SystemUpdateId = reader.GetInt64(2)
            };
        }
    }

    public Dictionary<string, VideoRecord> GetAllVideosByPath()
    {
        lock (_lock)
        {
            var map = new Dictionary<string, VideoRecord>(StringComparer.OrdinalIgnoreCase);
            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                """
                SELECT id, folder_id, path, title, size, mtime_utc_ticks, duration, container,
                       video_codec, audio_codec, width, height, thumb_path, object_id
                FROM videos
                """;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var v = ReadVideo(reader);
                map[v.Path] = v;
                try
                {
                    var full = Path.GetFullPath(v.Path);
                    if (!string.Equals(full, v.Path, StringComparison.Ordinal))
                        map[full] = v;
                }
                catch
                {
                    // keep original key only
                }
            }
            return map;
        }
    }

    public MediaFolderRecord? GetFolderByObjectId(string objectId)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                """
                SELECT id, parent_id, root_path, relative_path, name, object_id
                FROM folders WHERE object_id = $oid
                """;
            cmd.Parameters.AddWithValue("$oid", objectId);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? ReadFolder(reader) : null;
        }
    }

    public VideoRecord? GetVideoByObjectId(string objectId)
    {
        lock (_lock)
            return GetVideoByObjectId_NoLock(objectId);
    }

    public VideoRecord? GetVideoByPath(string path)
    {
        lock (_lock)
        {
            try { path = Path.GetFullPath(path); }
            catch { /* keep as-is */ }

            var byOid = GetVideoByObjectId_NoLock(MakeObjectId("V", path));
            if (byOid is not null) return byOid;

            var id = FindVideoIdByPathIgnoreCase_NoLock(path);
            return id is null ? null : GetVideoById_NoLock(id.Value);
        }
    }

    private VideoRecord? GetVideoByObjectId_NoLock(string objectId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT id, folder_id, path, title, size, mtime_utc_ticks, duration, container,
                   video_codec, audio_codec, width, height, thumb_path, object_id
            FROM videos WHERE object_id = $oid
            """;
        cmd.Parameters.AddWithValue("$oid", objectId);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadVideo(reader) : null;
    }

    private VideoRecord? GetVideoById_NoLock(long id)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT id, folder_id, path, title, size, mtime_utc_ticks, duration, container,
                   video_codec, audio_codec, width, height, thumb_path, object_id
            FROM videos WHERE id = $id
            """;
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadVideo(reader) : null;
    }

    public List<MediaFolderRecord> GetChildFolders(long? parentId)
    {
        lock (_lock)
        {
            var list = new List<MediaFolderRecord>();
            using var cmd = _connection.CreateCommand();
            if (parentId is null)
            {
                cmd.CommandText =
                    """
                    SELECT id, parent_id, root_path, relative_path, name, object_id
                    FROM folders WHERE parent_id IS NULL ORDER BY name COLLATE NOCASE
                    """;
            }
            else
            {
                cmd.CommandText =
                    """
                    SELECT id, parent_id, root_path, relative_path, name, object_id
                    FROM folders WHERE parent_id = $pid ORDER BY name COLLATE NOCASE
                    """;
                cmd.Parameters.AddWithValue("$pid", parentId.Value);
            }

            using var reader = cmd.ExecuteReader();
            while (reader.Read()) list.Add(ReadFolder(reader));
            return list;
        }
    }

    public List<VideoRecord> GetVideosInFolder(long folderId)
    {
        lock (_lock)
        {
            var list = new List<VideoRecord>();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                """
                SELECT id, folder_id, path, title, size, mtime_utc_ticks, duration, container,
                       video_codec, audio_codec, width, height, thumb_path, object_id
                FROM videos WHERE folder_id = $fid ORDER BY title COLLATE NOCASE
                """;
            cmd.Parameters.AddWithValue("$fid", folderId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) list.Add(ReadVideo(reader));
            return list;
        }
    }

    public int CountChildFolders(long folderId)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM folders WHERE parent_id = $id";
            cmd.Parameters.AddWithValue("$id", folderId);
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    public int CountVideosInFolder(long folderId)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM videos WHERE folder_id = $id";
            cmd.Parameters.AddWithValue("$id", folderId);
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    public long UpsertFolder(string rootPath, string relativePath, string name, long? parentId)
    {
        lock (_lock)
        {
            var objectId = MakeObjectId("F", Path.Combine(rootPath, relativePath));
            using var find = _connection.CreateCommand();
            find.CommandText = "SELECT id FROM folders WHERE object_id = $oid";
            find.Parameters.AddWithValue("$oid", objectId);
            var existing = find.ExecuteScalar();
            if (existing is not null and not DBNull)
            {
                var id = (long)existing;
                using var upd = _connection.CreateCommand();
                upd.CommandText =
                    """
                    UPDATE folders SET parent_id=$pid, root_path=$root, relative_path=$rel, name=$name
                    WHERE id=$id
                    """;
                upd.Parameters.AddWithValue("$pid", (object?)parentId ?? DBNull.Value);
                upd.Parameters.AddWithValue("$root", rootPath);
                upd.Parameters.AddWithValue("$rel", relativePath);
                upd.Parameters.AddWithValue("$name", name);
                upd.Parameters.AddWithValue("$id", id);
                upd.ExecuteNonQuery();
                return id;
            }

            using var ins = _connection.CreateCommand();
            ins.CommandText =
                """
                INSERT INTO folders(parent_id, root_path, relative_path, name, object_id)
                VALUES($pid, $root, $rel, $name, $oid);
                SELECT last_insert_rowid();
                """;
            ins.Parameters.AddWithValue("$pid", (object?)parentId ?? DBNull.Value);
            ins.Parameters.AddWithValue("$root", rootPath);
            ins.Parameters.AddWithValue("$rel", relativePath);
            ins.Parameters.AddWithValue("$name", name);
            ins.Parameters.AddWithValue("$oid", objectId);
            return (long)ins.ExecuteScalar()!;
        }
    }

    public void UpsertVideo(VideoRecord video)
    {
        lock (_lock)
        {
            try { video.Path = Path.GetFullPath(video.Path); }
            catch { /* keep as-is */ }

            video.ObjectId = MakeObjectId("V", video.Path);

            // Prefer stable identity (object_id) so the same file never becomes a second row
            // when path casing / normalization differs between scans.
            long? id = FindVideoIdByObjectId_NoLock(video.ObjectId);
            id ??= FindVideoIdByPathIgnoreCase_NoLock(video.Path);

            if (id is not null)
            {
                using var upd = _connection.CreateCommand();
                upd.CommandText =
                    """
                    UPDATE videos SET
                      folder_id=$folder_id,
                      path=$path,
                      title=$title,
                      size=$size,
                      mtime_utc_ticks=$mtime,
                      duration=$duration,
                      container=$container,
                      video_codec=$vcodec,
                      audio_codec=$acodec,
                      width=$width,
                      height=$height,
                      thumb_path=$thumb,
                      object_id=$oid
                    WHERE id=$id
                    """;
                BindVideoParams(upd, video);
                upd.Parameters.AddWithValue("$id", id.Value);
                upd.ExecuteNonQuery();

                // Drop legacy duplicates with the same path or object_id.
                using var del = _connection.CreateCommand();
                del.CommandText =
                    """
                    DELETE FROM videos
                    WHERE id <> $id
                      AND (object_id = $oid OR path = $path)
                    """;
                del.Parameters.AddWithValue("$id", id.Value);
                del.Parameters.AddWithValue("$oid", video.ObjectId);
                del.Parameters.AddWithValue("$path", video.Path);
                del.ExecuteNonQuery();
                return;
            }

            using var ins = _connection.CreateCommand();
            ins.CommandText =
                """
                INSERT INTO videos(folder_id, path, title, size, mtime_utc_ticks, duration, container,
                  video_codec, audio_codec, width, height, thumb_path, object_id)
                VALUES($folder_id, $path, $title, $size, $mtime, $duration, $container,
                  $vcodec, $acodec, $width, $height, $thumb, $oid)
                """;
            BindVideoParams(ins, video);
            ins.ExecuteNonQuery();
        }
    }

    private static void BindVideoParams(SqliteCommand cmd, VideoRecord video)
    {
        cmd.Parameters.AddWithValue("$folder_id", video.FolderId);
        cmd.Parameters.AddWithValue("$path", video.Path);
        cmd.Parameters.AddWithValue("$title", video.Title);
        cmd.Parameters.AddWithValue("$size", video.Size);
        cmd.Parameters.AddWithValue("$mtime", video.MtimeUtcTicks);
        cmd.Parameters.AddWithValue("$duration", video.DurationSeconds);
        cmd.Parameters.AddWithValue("$container", video.Container);
        cmd.Parameters.AddWithValue("$vcodec", video.VideoCodec);
        cmd.Parameters.AddWithValue("$acodec", video.AudioCodec);
        cmd.Parameters.AddWithValue("$width", video.Width);
        cmd.Parameters.AddWithValue("$height", video.Height);
        cmd.Parameters.AddWithValue("$thumb", (object?)video.ThumbPath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$oid", video.ObjectId);
    }

    private long? FindVideoIdByObjectId_NoLock(string objectId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT id FROM videos WHERE object_id = $oid LIMIT 1";
        cmd.Parameters.AddWithValue("$oid", objectId);
        var o = cmd.ExecuteScalar();
        return o is null or DBNull ? null : Convert.ToInt64(o);
    }

    private long? FindVideoIdByPathIgnoreCase_NoLock(string path)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT id, path FROM videos";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), path, StringComparison.OrdinalIgnoreCase))
                return reader.GetInt64(0);
        }
        return null;
    }

    /// <summary>Remove rows whose files are gone from disk (prevents count inflation across rescans).</summary>
    public int PruneMissingVideos()
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT id, path, thumb_path FROM videos";
            using var reader = cmd.ExecuteReader();
            var toDelete = new List<(long Id, string? Thumb)>();
            while (reader.Read())
            {
                var path = reader.GetString(1);
                if (!File.Exists(path))
                    toDelete.Add((reader.GetInt64(0), reader.IsDBNull(2) ? null : reader.GetString(2)));
            }

            reader.Close();
            foreach (var (id, thumb) in toDelete)
            {
                using var del = _connection.CreateCommand();
                del.CommandText = "DELETE FROM videos WHERE id=$id";
                del.Parameters.AddWithValue("$id", id);
                del.ExecuteNonQuery();
                TryDeleteThumbFiles(thumb);
            }

            return toDelete.Count;
        }
    }

    public void DeleteVideosNotIn(HashSet<string> keepPaths)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT id, path, thumb_path FROM videos";
            using var reader = cmd.ExecuteReader();
            var toDelete = new List<(long Id, string? Thumb)>();
            while (reader.Read())
            {
                var path = reader.GetString(1);
                if (!keepPaths.Contains(path))
                    toDelete.Add((reader.GetInt64(0), reader.IsDBNull(2) ? null : reader.GetString(2)));
            }

            reader.Close();
            foreach (var (id, thumb) in toDelete)
            {
                using var del = _connection.CreateCommand();
                del.CommandText = "DELETE FROM videos WHERE id=$id";
                del.Parameters.AddWithValue("$id", id);
                del.ExecuteNonQuery();
                TryDeleteThumbFiles(thumb);
            }
        }
    }

    public void DeleteOrphanFolders()
    {
        lock (_lock)
        {
            // Remove folders that have no videos in subtree and no child folders — iterative cleanup
            bool removed;
            do
            {
                removed = false;
                using var cmd = _connection.CreateCommand();
                cmd.CommandText =
                    """
                    DELETE FROM folders
                    WHERE id NOT IN (SELECT DISTINCT folder_id FROM videos)
                      AND id NOT IN (SELECT DISTINCT parent_id FROM folders WHERE parent_id IS NOT NULL)
                    """;
                removed = cmd.ExecuteNonQuery() > 0;
            } while (removed);
        }
    }

    public static string MakeObjectId(string prefix, string path)
    {
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(path.ToLowerInvariant())))
            .ToLowerInvariant();
        return $"{prefix}{hash[..16]}";
    }

    private static MediaFolderRecord ReadFolder(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        ParentId = reader.IsDBNull(1) ? null : reader.GetInt64(1),
        RootPath = reader.GetString(2),
        RelativePath = reader.GetString(3),
        Name = reader.GetString(4),
        ObjectId = reader.GetString(5)
    };

    private static VideoRecord ReadVideo(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        FolderId = reader.GetInt64(1),
        Path = reader.GetString(2),
        Title = reader.GetString(3),
        Size = reader.GetInt64(4),
        MtimeUtcTicks = reader.GetInt64(5),
        DurationSeconds = reader.GetDouble(6),
        Container = reader.GetString(7),
        VideoCodec = reader.GetString(8),
        AudioCodec = reader.GetString(9),
        Width = reader.GetInt32(10),
        Height = reader.GetInt32(11),
        ThumbPath = reader.IsDBNull(12) ? null : reader.GetString(12),
        ObjectId = reader.GetString(13)
    };

    public void Dispose()
    {
        lock (_lock)
        {
            try
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                cmd.ExecuteNonQuery();
            }
            catch
            {
                // ignore
            }

            try
            {
                _connection.Close();
            }
            catch
            {
                // ignore
            }

            _connection.Dispose();
        }
    }

    private static void TryDeleteThumbFiles(string? thumbPath)
    {
        static void TryDelete(string? path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
            try { File.Delete(path); } catch { /* ignore */ }
        }

        TryDelete(thumbPath);
        var tn = ThumbnailCache.CompanionTnPath(thumbPath);
        if (!string.Equals(tn, thumbPath, StringComparison.OrdinalIgnoreCase))
            TryDelete(tn);
    }
}
