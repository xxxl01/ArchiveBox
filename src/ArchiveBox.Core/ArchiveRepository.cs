using System.Globalization;
using Microsoft.Data.Sqlite;

namespace ArchiveBox.Core;

public sealed class ArchiveRepository
{
    private readonly string _connectionString;
    public ArchiveRepository(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, DefaultTimeout = 10 }.ToString();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS settings (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS recent_folders (path TEXT PRIMARY KEY, opened_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS archive_items (
              id TEXT PRIMARY KEY, root_path TEXT NOT NULL, relative_path TEXT NOT NULL,
              source_path TEXT NOT NULL, mode TEXT NOT NULL, archived_at TEXT NOT NULL,
              expires_at TEXT, state TEXT NOT NULL, size INTEGER NOT NULL, sha256 TEXT, error TEXT);
            CREATE INDEX IF NOT EXISTS ix_items_expiry ON archive_items(state, expires_at);
            CREATE TABLE IF NOT EXISTS favorite_folders (path TEXT PRIMARY KEY COLLATE NOCASE, added_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS expiry_rules (
              root_path TEXT NOT NULL COLLATE NOCASE, relative_path TEXT NOT NULL COLLATE NOCASE,
              is_directory INTEGER NOT NULL, expires_at TEXT,
              PRIMARY KEY(root_path,relative_path,is_directory));
            INSERT OR IGNORE INTO expiry_rules(root_path,relative_path,is_directory,expires_at)
              SELECT root_path,relative_path,0,expires_at FROM archive_items
              WHERE expires_at IS NOT NULL AND state IN ('Ready','SourceRetained')
                AND NOT EXISTS(SELECT 1 FROM settings WHERE key='expiryRulesMigrated') ORDER BY archived_at DESC;
            INSERT OR IGNORE INTO settings VALUES('expiryRulesMigrated','1');
            PRAGMA user_version=2;
            """;
        command.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    public string? GetSetting(string key)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM settings WHERE key=$key";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
    }

    public void SetSetting(string key, string value)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO settings VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    public void RememberFolder(string path)
    {
        path = PathSafety.Normalize(path);
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR REPLACE INTO recent_folders(path,opened_at) VALUES($path,$time);
            DELETE FROM recent_folders WHERE path NOT IN (SELECT path FROM recent_folders ORDER BY opened_at DESC, rowid DESC LIMIT 15);
            """;
        command.Parameters.AddWithValue("$path", path);
        command.Parameters.AddWithValue("$time", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
        transaction.Commit();
        SetSetting("lastFolder", path);
    }

    public IReadOnlyList<RecentFolder> GetRecentFolders()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT path,opened_at FROM recent_folders ORDER BY opened_at DESC, rowid DESC";
        using var reader = command.ExecuteReader();
        var result = new List<RecentFolder>();
        while (reader.Read()) result.Add(new(reader.GetString(0), Parse(reader.GetString(1))));
        return result;
    }

    public void Save(ArchiveItem item)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO archive_items VALUES($id,$root,$relative,$source,$mode,$at,$expiry,$state,$size,$hash,$error)
            ON CONFLICT(id) DO UPDATE SET relative_path=excluded.relative_path, expires_at=excluded.expires_at,
              state=excluded.state, size=excluded.size, sha256=excluded.sha256, error=excluded.error;
            """;
        command.Parameters.AddWithValue("$id", item.Id);
        command.Parameters.AddWithValue("$root", item.RootPath);
        command.Parameters.AddWithValue("$relative", item.RelativePath);
        command.Parameters.AddWithValue("$source", item.SourcePath);
        command.Parameters.AddWithValue("$mode", item.Mode.ToString());
        command.Parameters.AddWithValue("$at", item.ArchivedAt.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$expiry", (object?)item.ExpiresAt?.ToUniversalTime().ToString("O") ?? DBNull.Value);
        command.Parameters.AddWithValue("$state", item.State.ToString());
        command.Parameters.AddWithValue("$size", item.Size);
        command.Parameters.AddWithValue("$hash", (object?)item.Sha256 ?? DBNull.Value);
        command.Parameters.AddWithValue("$error", (object?)item.Error ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<ArchiveItem> GetItems()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,root_path,relative_path,source_path,mode,archived_at,expires_at,state,size,sha256,error FROM archive_items ORDER BY archived_at DESC";
        using var reader = command.ExecuteReader();
        var items = new List<ArchiveItem>();
        while (reader.Read()) items.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
            reader.GetString(3), (ArchiveMode)Enum.Parse(typeof(ArchiveMode), reader.GetString(4)), Parse(reader.GetString(5)),
            reader.IsDBNull(6) ? null : Parse(reader.GetString(6)), (ItemState)Enum.Parse(typeof(ItemState), reader.GetString(7)),
            reader.GetInt64(8), reader.IsDBNull(9) ? null : reader.GetString(9), reader.IsDBNull(10) ? null : reader.GetString(10)));
        return items;
    }

    public ArchiveItem Get(string id) => GetItems().FirstOrDefault(i => i.Id == id) ?? throw new IOException("归档记录不存在。");

    public void SetExpiry(IEnumerable<string> ids, DateTimeOffset? expiresAt)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        foreach (var id in ids)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE archive_items SET expires_at=$at WHERE id=$id AND state IN ('Ready','SourceRetained')";
            command.Parameters.AddWithValue("$at", (object?)expiresAt?.ToUniversalTime().ToString("O") ?? DBNull.Value);
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
        }
        transaction.Commit();
        foreach (var id in ids)
        {
            var item = Get(id);
            SetRule(item.RootPath, item.RelativePath, false, expiresAt);
        }
    }

    public IReadOnlyList<FavoriteFolder> GetFavorites()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT path FROM favorite_folders ORDER BY added_at";
        using var reader = command.ExecuteReader();
        var result = new List<FavoriteFolder>();
        while (reader.Read())
        {
            var path = reader.GetString(0);
            var name = Path.GetFileName(PathSafety.Normalize(path));
            result.Add(new(path, string.IsNullOrEmpty(name) ? path : name));
        }
        return result;
    }

    public void SetFavorite(string path, bool favorite)
    {
        path = PathSafety.Normalize(path);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = favorite ? "INSERT OR IGNORE INTO favorite_folders VALUES($path,$time)" : "DELETE FROM favorite_folders WHERE path=$path";
        command.Parameters.AddWithValue("$path", path);
        if (favorite) command.Parameters.AddWithValue("$time", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    public ExpiryRules GetRules()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT root_path,relative_path,is_directory,expires_at FROM expiry_rules";
        using var reader = command.ExecuteReader();
        var rules = new List<ExpiryRule>();
        while (reader.Read()) rules.Add(new(reader.GetString(0), reader.GetString(1), reader.GetInt32(2) != 0,
            reader.IsDBNull(3) ? null : Parse(reader.GetString(3))));
        return new(rules);
    }

    public void SetRule(string root, string relative, bool directory, DateTimeOffset? expiry)
    {
        root = PathSafety.Normalize(root);
        relative = PathSafety.RelativePath(root, PathSafety.Resolve(root, relative));
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO expiry_rules VALUES($root,$relative,$directory,$expiry)
            ON CONFLICT(root_path,relative_path,is_directory) DO UPDATE SET expires_at=excluded.expires_at;
            """;
        command.Parameters.AddWithValue("$root", root);
        command.Parameters.AddWithValue("$relative", relative);
        command.Parameters.AddWithValue("$directory", directory ? 1 : 0);
        command.Parameters.AddWithValue("$expiry", (object?)expiry?.ToUniversalTime().ToString("O") ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    public void RemoveRule(string root, string relative, bool directory)
    {
        root = PathSafety.Normalize(root);
        relative = PathSafety.RelativePath(root, PathSafety.Resolve(root, relative));
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM expiry_rules WHERE root_path=$root AND relative_path=$relative AND is_directory=$directory";
        command.Parameters.AddWithValue("$root", root);
        command.Parameters.AddWithValue("$relative", relative);
        command.Parameters.AddWithValue("$directory", directory ? 1 : 0);
        command.ExecuteNonQuery();
    }

    private static DateTimeOffset Parse(string text) => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
