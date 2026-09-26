using Microsoft.Data.Sqlite;

namespace WClop.Core.Storage;

/// <summary>
/// Local SQLite store for:
/// <list type="bullet">
/// <item>File markers keyed by (path, size, mtime), used when the alternate data stream is missing.</item>
/// <item>The hash cache: input content hash + variant → output file (project.md §5 <c>optimisedFilesByHash</c>),
/// shared by the clipboard engine and folder watchers so the same screenshot isn't processed twice.</item>
/// </list>
/// Each call opens its own (pooled) connection, so the class is safe to use from any thread.
/// </summary>
public sealed class OptimisationDatabase
{
    private readonly string _connectionString;

    public OptimisationDatabase(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
        }.ToString();

        using var connection = Open();
        Execute(connection, """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS markers (
                path     TEXT    NOT NULL PRIMARY KEY COLLATE NOCASE,
                size     INTEGER NOT NULL,
                mtime    INTEGER NOT NULL,
                status   TEXT    NOT NULL,
                updated  INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS hash_cache (
                input_hash  TEXT    NOT NULL,
                variant     TEXT    NOT NULL,
                output_path TEXT    NOT NULL,
                output_size INTEGER NOT NULL,
                updated     INTEGER NOT NULL,
                PRIMARY KEY (input_hash, variant)
            );
            """);
    }

    public static string DefaultPath => Path.Combine(AppPaths.LocalAppDataDir, "wclop.db");

    /// <summary>The recorded status, but only if the file still has the size and mtime it had when recorded.</summary>
    public MarkerStatus GetMarker(string path, long size, DateTime lastWriteUtc)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT status FROM markers WHERE path = $path AND size = $size AND mtime = $mtime";
        command.Parameters.AddWithValue("$path", Path.GetFullPath(path));
        command.Parameters.AddWithValue("$size", size);
        command.Parameters.AddWithValue("$mtime", lastWriteUtc.Ticks);
        return MarkerStatusText.Parse(command.ExecuteScalar() as string);
    }

    public void SetMarker(string path, long size, DateTime lastWriteUtc, MarkerStatus status)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        if (status == MarkerStatus.None)
        {
            command.CommandText = "DELETE FROM markers WHERE path = $path";
        }
        else
        {
            command.CommandText = """
                INSERT INTO markers (path, size, mtime, status, updated) VALUES ($path, $size, $mtime, $status, $now)
                ON CONFLICT(path) DO UPDATE SET size = $size, mtime = $mtime, status = $status, updated = $now
                """;
            command.Parameters.AddWithValue("$size", size);
            command.Parameters.AddWithValue("$mtime", lastWriteUtc.Ticks);
            command.Parameters.AddWithValue("$status", MarkerStatusText.ToText(status));
            command.Parameters.AddWithValue("$now", DateTime.UtcNow.Ticks);
        }

        command.Parameters.AddWithValue("$path", Path.GetFullPath(path));
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// A previous output for the same input content and settings, if it still exists with the recorded size.
    /// </summary>
    public string? GetCachedOutput(string inputHash, string variant)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT output_path, output_size FROM hash_cache WHERE input_hash = $hash AND variant = $variant";
        command.Parameters.AddWithValue("$hash", inputHash);
        command.Parameters.AddWithValue("$variant", variant);

        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;

        var outputPath = reader.GetString(0);
        var outputSize = reader.GetInt64(1);
        var info = new FileInfo(outputPath);
        return info.Exists && info.Length == outputSize ? outputPath : null;
    }

    public void PutCachedOutput(string inputHash, string variant, string outputPath)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO hash_cache (input_hash, variant, output_path, output_size, updated)
            VALUES ($hash, $variant, $path, $size, $now)
            ON CONFLICT(input_hash, variant) DO UPDATE SET output_path = $path, output_size = $size, updated = $now
            """;
        command.Parameters.AddWithValue("$hash", inputHash);
        command.Parameters.AddWithValue("$variant", variant);
        command.Parameters.AddWithValue("$path", Path.GetFullPath(outputPath));
        command.Parameters.AddWithValue("$size", new FileInfo(outputPath).Length);
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.Ticks);
        command.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
