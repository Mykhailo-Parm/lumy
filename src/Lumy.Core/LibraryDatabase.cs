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
}

/// <summary>
/// The local library: ratings and settings in one SQLite file under %LOCALAPPDATA%\Lumy.
/// Photos themselves are never copied or changed; the database only points at them.
/// All members are thread-safe.
/// </summary>
public sealed class LibraryDatabase : IDisposable
{
    private const int SchemaVersion = 1;

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

    public void Dispose() => _connection.Dispose();

    private void Migrate()
    {
        var version = ExecuteScalar<long>("PRAGMA user_version");
        if (version >= SchemaVersion)
            return;

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
        Execute($"PRAGMA user_version = {SchemaVersion}");
    }

    private void Update(PhotoRecord r) => Execute(
        """
        UPDATE photos SET path = $p, raw_path = $r, fingerprint = $f, file_size = $s, modified_utc = $m, captured_at = $c, updated_utc = $u
        WHERE id = $id
        """,
        ("$p", r.Path), ("$r", r.RawPath), ("$f", r.Fingerprint), ("$s", r.FileSize), ("$m", r.ModifiedUtc.Ticks),
        ("$c", r.CapturedAt?.Ticks), ("$u", DateTime.UtcNow.Ticks), ("$id", r.Id));

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
                (Tier)reader.GetInt32(reader.GetOrdinal("tier"))));
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
