using Microsoft.Data.Sqlite;

namespace Lumy.Core;

/// <summary>A photo as Lumy knows it: where it is, how to recognise it after a move, and how it was rated.</summary>
public sealed record PhotoRecord(
    long Id,
    string Path,
    string? RawPath,
    string Fingerprint,
    long FileSize,
    DateTime ModifiedUtc,
    DateTime? CapturedAt,
    Tier Tier)
{
    public string FileName => System.IO.Path.GetFileName(Path);

    /// <summary>The <see cref="PerceptualHash"/> of the thumbnail, once the app has computed it.</summary>
    public ulong? Hash { get; init; }
}

/// <summary>A named, ordered set of photos, such as a carousel. Photos are referenced, never copied.</summary>
public sealed record Album(long Id, string Name, int Count, long? CoverPhotoId);

/// <summary>
/// The local library: ratings and settings in one SQLite file under %LOCALAPPDATA%\Lumy.
/// Photos themselves are never copied or changed; the database only points at them.
/// All members are thread-safe.
/// </summary>
public sealed class LibraryDatabase : IDisposable
{
    private const int SchemaVersion = 3;

    private readonly SqliteConnection _connection;
    private readonly Lock _lock = new();

    public LibraryDatabase(string databasePath)
    {
        if (databasePath != ":memory:")
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(databasePath))!);

        _connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString());
        _connection.Open();
        Execute("PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL; PRAGMA foreign_keys = ON;");
        Migrate();
    }

    public static string DefaultPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lumy", "library.db");

    /// <summary>
    /// Brings the library in line with the files found in a folder and returns their records in the same order.
    /// A file whose path is new but whose fingerprint matches a photo that no longer exists at its old path
    /// is treated as moved or renamed, and keeps its rating.
    /// </summary>
    public IReadOnlyList<PhotoRecord> Sync(
        IReadOnlyList<PhotoFile> files,
        Func<string, string> fingerprint,
        Func<string, DateTime?> captureTime,
        Func<string, bool> fileExists,
        CancellationToken cancellationToken = default)
    {
        var result = new List<PhotoRecord>(files.Count);
        lock (_lock)
        {
            using var transaction = _connection.BeginTransaction();
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                result.Add(SyncOne(file, fingerprint, captureTime, fileExists));
            }

            transaction.Commit();
        }

        return result;
    }

    private PhotoRecord SyncOne(PhotoFile file, Func<string, string> fingerprint, Func<string, DateTime?> captureTime, Func<string, bool> fileExists)
    {
        var existing = QuerySingle("SELECT * FROM photos WHERE path = $p", ("$p", file.Path));
        if (existing is not null)
        {
            if (existing.FileSize == file.FileSize && existing.ModifiedUtc == file.ModifiedUtc)
            {
                if (existing.RawPath == file.RawPath)
                    return existing;

                Execute("UPDATE photos SET raw_path = $r WHERE id = $id", ("$r", file.RawPath), ("$id", existing.Id));
                return existing with { RawPath = file.RawPath };
            }

            // Same path, changed content (edited in place): refresh what we know, keep the rating.
            var updated = existing with
            {
                RawPath = file.RawPath,
                Fingerprint = fingerprint(file.Path),
                FileSize = file.FileSize,
                ModifiedUtc = file.ModifiedUtc,
                CapturedAt = captureTime(file.Path) ?? existing.CapturedAt,
                Hash = null,
            };
            Update(updated);
            return updated;
        }

        var print = fingerprint(file.Path);
        var moved = QueryMany("SELECT * FROM photos WHERE fingerprint = $f", ("$f", print))
            .FirstOrDefault(r => !fileExists(r.Path));
        if (moved is not null)
        {
            var relocated = moved with { Path = file.Path, RawPath = file.RawPath, FileSize = file.FileSize, ModifiedUtc = file.ModifiedUtc };
            Update(relocated);
            return relocated;
        }

        var captured = captureTime(file.Path);
        var id = ExecuteScalar<long>(
            """
            INSERT INTO photos (path, raw_path, fingerprint, file_size, modified_utc, captured_at, tier, updated_utc)
            VALUES ($p, $r, $f, $s, $m, $c, 0, $u) RETURNING id
            """,
            ("$p", file.Path), ("$r", file.RawPath), ("$f", print), ("$s", file.FileSize),
            ("$m", file.ModifiedUtc.Ticks), ("$c", captured?.Ticks), ("$u", DateTime.UtcNow.Ticks));
        return new PhotoRecord(id, file.Path, file.RawPath, print, file.FileSize, file.ModifiedUtc, captured, Tier.None);
    }

    public PhotoRecord? Get(long id)
    {
        lock (_lock)
            return QuerySingle("SELECT * FROM photos WHERE id = $id", ("$id", id));
    }

    public void SetTier(IEnumerable<long> ids, Tier tier) => SetTiers(ids.Select(id => (id, tier)));

    public void SetTiers(IEnumerable<(long Id, Tier Tier)> changes)
    {
        lock (_lock)
        {
            using var transaction = _connection.BeginTransaction();
            var now = DateTime.UtcNow.Ticks;
            foreach (var (id, tier) in changes)
                Execute("UPDATE photos SET tier = $t, updated_utc = $u WHERE id = $id", ("$t", (int)tier), ("$u", now), ("$id", id));
            transaction.Commit();
        }
    }

    /// <summary>Saves thumbnail hashes computed by the app, so they are only computed once per photo.</summary>
    public void SetHashes(IEnumerable<(long Id, ulong Hash)> hashes)
    {
        lock (_lock)
        {
            using var transaction = _connection.BeginTransaction();
            foreach (var (id, hash) in hashes)
                Execute("UPDATE photos SET phash = $h WHERE id = $id", ("$h", unchecked((long)hash)), ("$id", id));
            transaction.Commit();
        }
    }

    public string? GetSetting(string key)
    {
        lock (_lock)
            return ExecuteScalar<string?>("SELECT value FROM settings WHERE key = $k", ("$k", key));
    }

    public void SetSetting(string key, string value)
    {
        lock (_lock)
            Execute("INSERT INTO settings (key, value) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value", ("$k", key), ("$v", value));
    }

    // ---------- Edits ----------

    /// <summary>The saved edits of these photos; photos without one are left out.</summary>
    public IReadOnlyDictionary<long, PhotoEdit> GetEdits(IEnumerable<long> ids)
    {
        var wanted = ids.ToHashSet();
        var result = new Dictionary<long, PhotoEdit>();
        if (wanted.Count == 0)
            return result;

        lock (_lock)
        {
            using var command = Command("SELECT photo_id, data FROM edits", []);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetInt64(0);
                if (wanted.Contains(id) && PhotoEdit.FromJson(reader.GetString(1)) is { } edit)
                    result[id] = edit;
            }
        }

        return result;
    }

    /// <summary>Saves an edit; null or an empty one deletes it.</summary>
    public void SetEdit(long photoId, PhotoEdit? edit)
    {
        lock (_lock)
        {
            if (edit is null || edit.IsEmpty)
                Execute("DELETE FROM edits WHERE photo_id = $id", ("$id", photoId));
            else
                Execute(
                    "INSERT INTO edits (photo_id, data, updated_utc) VALUES ($id, $d, $u) ON CONFLICT(photo_id) DO UPDATE SET data = excluded.data, updated_utc = excluded.updated_utc",
                    ("$id", photoId), ("$d", edit.Normalized().ToJson()), ("$u", DateTime.UtcNow.Ticks));
        }
    }

    // ---------- Albums ----------

    public Album CreateAlbum(string name)
    {
        lock (_lock)
        {
            var clean = CleanName(name);
            var id = ExecuteScalar<long>("INSERT INTO albums (name, created_utc) VALUES ($n, $u) RETURNING id", ("$n", clean), ("$u", DateTime.UtcNow.Ticks));
            return new Album(id, clean, 0, null);
        }
    }

    /// <summary>Every album, newest first, with its size and first photo.</summary>
    public IReadOnlyList<Album> Albums()
    {
        lock (_lock)
        {
            using var command = Command(
                """
                SELECT a.id, a.name,
                       (SELECT COUNT(*) FROM album_photos p WHERE p.album_id = a.id),
                       (SELECT p.photo_id FROM album_photos p WHERE p.album_id = a.id ORDER BY p.position LIMIT 1)
                FROM albums a ORDER BY a.created_utc DESC, a.id DESC
                """, []);
            using var reader = command.ExecuteReader();
            var albums = new List<Album>();
            while (reader.Read())
                albums.Add(new Album(reader.GetInt64(0), reader.GetString(1), reader.GetInt32(2), reader.IsDBNull(3) ? null : reader.GetInt64(3)));
            return albums;
        }
    }

    public void RenameAlbum(long albumId, string name)
    {
        lock (_lock)
            Execute("UPDATE albums SET name = $n WHERE id = $id", ("$n", CleanName(name)), ("$id", albumId));
    }

    public void DeleteAlbum(long albumId)
    {
        lock (_lock)
            Execute("DELETE FROM albums WHERE id = $id", ("$id", albumId));
    }

    /// <summary>Appends photos to the end of an album, skipping ones already in it; returns how many were added.</summary>
    public int AddToAlbum(long albumId, IEnumerable<long> photoIds)
    {
        lock (_lock)
        {
            using var transaction = _connection.BeginTransaction();
            var next = ExecuteScalar<long>("SELECT COALESCE(MAX(position), -1) + 1 FROM album_photos WHERE album_id = $a", ("$a", albumId));
            var added = 0;
            foreach (var id in photoIds.Distinct())
            {
                using var command = Command(
                    "INSERT OR IGNORE INTO album_photos (album_id, photo_id, position) VALUES ($a, $p, $n)",
                    [("$a", albumId), ("$p", id), ("$n", next)]);
                if (command.ExecuteNonQuery() > 0)
                {
                    added++;
                    next++;
                }
            }

            transaction.Commit();
            return added;
        }
    }

    public void RemoveFromAlbum(long albumId, IEnumerable<long> photoIds)
    {
        lock (_lock)
        {
            using var transaction = _connection.BeginTransaction();
            foreach (var id in photoIds)
                Execute("DELETE FROM album_photos WHERE album_id = $a AND photo_id = $p", ("$a", albumId), ("$p", id));
            transaction.Commit();
        }
    }

    /// <summary>Puts the album in this order; photos not listed keep their relative order after the listed ones.</summary>
    public void SetAlbumOrder(long albumId, IReadOnlyList<long> photoIds)
    {
        lock (_lock)
        {
            var current = AlbumIds(albumId);
            var order = photoIds.Where(current.Contains).Distinct().Concat(current.Except(photoIds)).ToList();
            using var transaction = _connection.BeginTransaction();
            for (var i = 0; i < order.Count; i++)
                Execute("UPDATE album_photos SET position = $n WHERE album_id = $a AND photo_id = $p", ("$n", i), ("$a", albumId), ("$p", order[i]));
            transaction.Commit();
        }
    }

    /// <summary>The album's photos in order. They can come from different folders.</summary>
    public IReadOnlyList<PhotoRecord> AlbumPhotos(long albumId)
    {
        lock (_lock)
            return QueryMany(
                "SELECT ph.* FROM album_photos ap JOIN photos ph ON ph.id = ap.photo_id WHERE ap.album_id = $a ORDER BY ap.position",
                ("$a", albumId));
    }

    private List<long> AlbumIds(long albumId)
    {
        using var command = Command("SELECT photo_id FROM album_photos WHERE album_id = $a ORDER BY position", [("$a", albumId)]);
        using var reader = command.ExecuteReader();
        var ids = new List<long>();
        while (reader.Read())
            ids.Add(reader.GetInt64(0));
        return ids;
    }

    private static string CleanName(string name) => string.IsNullOrWhiteSpace(name) ? "Без названия" : name.Trim();

    public void Dispose() => _connection.Dispose();

    private void Migrate()
    {
        var version = ExecuteScalar<long>("PRAGMA user_version");
        if (version >= SchemaVersion)
            return;

        if (version < 1)
            CreateSchemaV1();
        if (version < 2)
            Execute("ALTER TABLE photos ADD COLUMN phash INTEGER");
        if (version < 3)
            CreateSchemaV3();

        Execute($"PRAGMA user_version = {SchemaVersion}");
    }

    private void CreateSchemaV1()
    {
        Execute(
            """
            CREATE TABLE IF NOT EXISTS photos (
                id           INTEGER PRIMARY KEY,
                path         TEXT    NOT NULL UNIQUE COLLATE NOCASE,
                raw_path     TEXT,
                fingerprint  TEXT    NOT NULL,
                file_size    INTEGER NOT NULL,
                modified_utc INTEGER NOT NULL,
                captured_at  INTEGER,
                tier         INTEGER NOT NULL DEFAULT 0,
                updated_utc  INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_photos_fingerprint ON photos (fingerprint);
            CREATE TABLE IF NOT EXISTS settings (
                key   TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            """);
    }

    /// <summary>Edits and albums. Albums only point at photos; deleting a photo's row drops it from its albums.</summary>
    private void CreateSchemaV3() => Execute(
        """
        CREATE TABLE IF NOT EXISTS edits (
            photo_id    INTEGER PRIMARY KEY REFERENCES photos (id) ON DELETE CASCADE,
            data        TEXT    NOT NULL,
            updated_utc INTEGER NOT NULL
        );
        CREATE TABLE IF NOT EXISTS albums (
            id          INTEGER PRIMARY KEY,
            name        TEXT    NOT NULL,
            created_utc INTEGER NOT NULL
        );
        CREATE TABLE IF NOT EXISTS album_photos (
            album_id INTEGER NOT NULL REFERENCES albums (id) ON DELETE CASCADE,
            photo_id INTEGER NOT NULL REFERENCES photos (id) ON DELETE CASCADE,
            position INTEGER NOT NULL,
            PRIMARY KEY (album_id, photo_id)
        );
        CREATE INDEX IF NOT EXISTS ix_album_photos_order ON album_photos (album_id, position);
        """);

    private void Update(PhotoRecord r) => Execute(
        """
        UPDATE photos SET path = $p, raw_path = $r, fingerprint = $f, file_size = $s, modified_utc = $m, captured_at = $c, phash = $h, updated_utc = $u
        WHERE id = $id
        """,
        ("$p", r.Path), ("$r", r.RawPath), ("$f", r.Fingerprint), ("$s", r.FileSize), ("$m", r.ModifiedUtc.Ticks),
        ("$c", r.CapturedAt?.Ticks), ("$h", r.Hash is { } h ? unchecked((long)h) : null), ("$u", DateTime.UtcNow.Ticks), ("$id", r.Id));

    private PhotoRecord? QuerySingle(string sql, params (string Name, object? Value)[] parameters) =>
        QueryMany(sql, parameters).FirstOrDefault();

    private List<PhotoRecord> QueryMany(string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(sql, parameters);
        using var reader = command.ExecuteReader();
        var list = new List<PhotoRecord>();
        while (reader.Read())
        {
            list.Add(new PhotoRecord(
                reader.GetInt64(reader.GetOrdinal("id")),
                reader.GetString(reader.GetOrdinal("path")),
                reader.IsDBNull(reader.GetOrdinal("raw_path")) ? null : reader.GetString(reader.GetOrdinal("raw_path")),
                reader.GetString(reader.GetOrdinal("fingerprint")),
                reader.GetInt64(reader.GetOrdinal("file_size")),
                new DateTime(reader.GetInt64(reader.GetOrdinal("modified_utc")), DateTimeKind.Utc),
                reader.IsDBNull(reader.GetOrdinal("captured_at")) ? null : new DateTime(reader.GetInt64(reader.GetOrdinal("captured_at"))),
                (Tier)reader.GetInt32(reader.GetOrdinal("tier")))
            {
                Hash = reader.IsDBNull(reader.GetOrdinal("phash")) ? null : unchecked((ulong)reader.GetInt64(reader.GetOrdinal("phash"))),
            });
        }

        return list;
    }

    private void Execute(string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(sql, parameters);
        command.ExecuteNonQuery();
    }

    private T ExecuteScalar<T>(string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(sql, parameters);
        var value = command.ExecuteScalar();
        return value is null or DBNull ? default! : (T)Convert.ChangeType(value, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T));
    }

    private SqliteCommand Command(string sql, (string Name, object? Value)[] parameters)
    {
        var command = _connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }
}
