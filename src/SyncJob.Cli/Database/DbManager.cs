using Microsoft.Data.Sqlite;
using System;
using System.IO;

namespace SyncJob.Database
{
    /// <summary>
    /// Gestor de la base de datos SQLite local
    /// </summary>
    public static class DbManager
    {
        private static string? _dbPath;
        private static readonly object _lock = new object();

        /// <summary>
        /// Ruta de la base de datos SQLite
        /// </summary>
        public static string DbPath
        {
            get
            {
                if (_dbPath == null)
                {
                    var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    var syncJobDir = Path.Combine(appData, "SyncJob");
                    Directory.CreateDirectory(syncJobDir);
                    _dbPath = Path.Combine(syncJobDir, "SyncJob.db");
                }
                return _dbPath;
            }
            set => _dbPath = value;
        }

        /// <summary>
        /// Connection string para SQLite
        /// </summary>
        public static string ConnectionString => $"Data Source={DbPath};";

        /// <summary>
        /// Inicializa la base de datos (crea schema si no existe)
        /// </summary>
        public static void Initialize()
        {
            lock (_lock)
            {
                var exists = File.Exists(DbPath);

                using var conn = GetConnection();
                conn.Open();

                if (!exists)
                {
                    Log.Info($"Creating SQLite database at: {DbPath}", evt: "db.create");
                    ExecuteSchema(conn);
                } else
                {
                    Log.Debug($"Using existing database at: {DbPath}", evt: "db.exists");
                }

                // Verificar schema version
                VerifySchema(conn);
            }
        }

        /// <summary>
        /// Obtiene una nueva conexión a SQLite
        /// </summary>
        public static SqliteConnection GetConnection()
        {
            return new SqliteConnection(ConnectionString);
        }

        /// <summary>
        /// Ejecuta el script de schema
        /// </summary>
        private static void ExecuteSchema(SqliteConnection conn)
        {
            var schemaPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Database", "Schema.sql");

            string schemaSql;
            if (File.Exists(schemaPath))
            {
                schemaSql = File.ReadAllText(schemaPath);
            }
            else
            {
                // Schema embebido como fallback
                schemaSql = GetEmbeddedSchema();
            }

            using var cmd = new SqliteCommand(schemaSql, conn);
            cmd.ExecuteNonQuery();

            Log.Info("Database schema created successfully", evt: "db.schema.created");
        }

        /// <summary>
        /// Verifica la versión del schema
        /// </summary>
        private static void VerifySchema(SqliteConnection conn)
        {
            const string checkTableSql = "SELECT name FROM sqlite_master WHERE type='table' AND name='SchemaVersion'";
            using var checkCmd = new SqliteCommand(checkTableSql, conn);
            var exists = checkCmd.ExecuteScalar();

            if (exists == null)
            {
                Log.Warn("Schema tables not found, recreating...", evt: "db.schema.missing");
                ExecuteSchema(conn);
                return;
            }

            // Verificar versión
            const string versionSql = "SELECT Version FROM SchemaVersion ORDER BY AppliedAt DESC LIMIT 1";
            using var versionCmd = new SqliteCommand(versionSql, conn);
            var version = versionCmd.ExecuteScalar() as string;

            Log.Debug($"Database schema version: {version ?? "unknown"}", evt: "db.schema.version");
        }

        /// <summary>
        /// Schema SQL embebido (fallback)
        /// </summary>
        private static string GetEmbeddedSchema()
        {
            return @"
-- Minimal embedded schema
CREATE TABLE IF NOT EXISTS Configurations (
    ConfigId TEXT PRIMARY KEY,
    DisplayName TEXT NOT NULL,
    Description TEXT,
    SourceConnectionId TEXT NOT NULL,
    SourceQuery TEXT,
    SourceStoredProc TEXT,
    SourceParameters TEXT,
    DestConnectionId TEXT NOT NULL,
    DestStageTable TEXT,
    DestFinalTable TEXT NOT NULL,
    BatchSize INTEGER DEFAULT 10000,
    MaxDOP INTEGER DEFAULT 4,
    BulkCopyTimeout INTEGER DEFAULT 0,
    KeepIdentity INTEGER DEFAULT 1,
    MinRowThreshold INTEGER DEFAULT 0,
    IsActive INTEGER DEFAULT 1,
    TrackingMode TEXT DEFAULT 'Snapshot',
    TrackingColumn TEXT,
    MergeStrategy TEXT DEFAULT 'Upsert',
    CreatedAt TEXT DEFAULT (datetime('now')),
    UpdatedAt TEXT DEFAULT (datetime('now')),
    CreatedBy TEXT,
    Tags TEXT
);

CREATE TABLE IF NOT EXISTS Connections (
    ConnectionId TEXT PRIMARY KEY,
    DisplayName TEXT NOT NULL,
    ServerType TEXT NOT NULL DEFAULT 'SqlServer',
    ServerName TEXT NOT NULL,
    DatabaseName TEXT NOT NULL,
    Username TEXT,
    PasswordEncrypted BLOB,
    ConnectionStringEncrypted BLOB,
    TrustServerCertificate INTEGER DEFAULT 0,
    Encrypt INTEGER DEFAULT 1,
    IsActive INTEGER DEFAULT 1,
    LastTestDate TEXT,
    LastTestSuccess INTEGER,
    CreatedAt TEXT DEFAULT (datetime('now')),
    UpdatedAt TEXT DEFAULT (datetime('now'))
);

CREATE TABLE IF NOT EXISTS ColumnMappings (
    MappingId INTEGER PRIMARY KEY AUTOINCREMENT,
    ConfigId TEXT NOT NULL,
    SourceColumn TEXT NOT NULL,
    DestColumn TEXT NOT NULL,
    IsPrimaryKey INTEGER DEFAULT 0,
    TransformType TEXT,
    TransformExpression TEXT,
    Ordinal INTEGER,
    CreatedAt TEXT DEFAULT (datetime('now')),
    FOREIGN KEY (ConfigId) REFERENCES Configurations(ConfigId) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS IX_ColumnMappings_ConfigId ON ColumnMappings(ConfigId);

CREATE TABLE IF NOT EXISTS ExecutionHistory (
    ExecutionId TEXT PRIMARY KEY,
    ConfigId TEXT NOT NULL,
    StartTime TEXT NOT NULL,
    EndTime TEXT,
    DurationMs INTEGER,
    Status TEXT NOT NULL,
    ExecutionMode TEXT,
    RowsRead INTEGER DEFAULT 0,
    RowsInserted INTEGER DEFAULT 0,
    RowsUpdated INTEGER DEFAULT 0,
    RowsDeleted INTEGER DEFAULT 0,
    RowsSkipped INTEGER DEFAULT 0,
    RowsFailed INTEGER DEFAULT 0,
    ErrorMessage TEXT,
    ErrorStackTrace TEXT,
    HostMachine TEXT,
    TriggeredBy TEXT,
    LogFilePath TEXT,
    FOREIGN KEY (ConfigId) REFERENCES Configurations(ConfigId)
);

CREATE TABLE IF NOT EXISTS SchemaVersion (
    Version TEXT PRIMARY KEY,
    AppliedAt TEXT DEFAULT (datetime('now')),
    Description TEXT
);

INSERT OR IGNORE INTO SchemaVersion (Version, Description)
VALUES ('1.0.0', 'Initial embedded schema');
";
        }

        /// <summary>
        /// Ejecuta vacuum para compactar la base de datos
        /// </summary>
        public static void Vacuum()
        {
            using var conn = GetConnection();
            conn.Open();
            using var cmd = new SqliteCommand("VACUUM;", conn);
            cmd.ExecuteNonQuery();
            Log.Info("Database vacuumed successfully", evt: "db.vacuum");
        }

        /// <summary>
        /// Obtiene información de la base de datos
        /// </summary>
        public static DatabaseInfo GetInfo()
        {
            using var conn = GetConnection();
            conn.Open();

            var info = new DatabaseInfo { Path = DbPath };

            // Tamaño del archivo
            if (File.Exists(DbPath))
            {
                var fileInfo = new FileInfo(DbPath);
                info.SizeBytes = fileInfo.Length;
            }

            // Contadores de tablas
            using (var cmd = new SqliteCommand("SELECT COUNT(*) FROM Configurations", conn))
                info.ConfigCount = Convert.ToInt32(cmd.ExecuteScalar());

            using (var cmd = new SqliteCommand("SELECT COUNT(*) FROM Connections", conn))
                info.ConnectionCount = Convert.ToInt32(cmd.ExecuteScalar());

            using (var cmd = new SqliteCommand("SELECT COUNT(*) FROM ExecutionHistory", conn))
                info.ExecutionCount = Convert.ToInt32(cmd.ExecuteScalar());

            // Schema version
            try
            {
                using var cmd = new SqliteCommand(
                    "SELECT Version FROM SchemaVersion ORDER BY AppliedAt DESC LIMIT 1", conn);
                info.SchemaVersion = cmd.ExecuteScalar() as string ?? "unknown";
            }
            catch
            {
                info.SchemaVersion = "unknown";
            }

            return info;
        }

        /// <summary>
        /// Backup de la base de datos
        /// </summary>
        public static void Backup(string destinationPath)
        {
            if (File.Exists(DbPath))
            {
                File.Copy(DbPath, destinationPath, overwrite: true);
                Log.Info($"Database backed up to: {destinationPath}", evt: "db.backup");
            }
            else
            {
                throw new FileNotFoundException("Database file not found", DbPath);
            }
        }

        /// <summary>
        /// Restaurar backup
        /// </summary>
        public static void Restore(string backupPath)
        {
            if (!File.Exists(backupPath))
                throw new FileNotFoundException("Backup file not found", backupPath);

            // Cerrar conexiones activas
            SqliteConnection.ClearAllPools();

            File.Copy(backupPath, DbPath, overwrite: true);
            Log.Info($"Database restored from: {backupPath}", evt: "db.restore");

            // Re-inicializar
            Initialize();
        }

        /// <summary>
        /// Limpia registros antiguos de ejecuciones
        /// </summary>
        public static int CleanupOldExecutions(int daysToKeep)
        {
            using var conn = GetConnection();
            conn.Open();

            var cutoffDate = DateTime.UtcNow.AddDays(-daysToKeep).ToString("yyyy-MM-dd HH:mm:ss");
            const string sql = "DELETE FROM ExecutionHistory WHERE StartTime < @cutoff";

            using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.AddWithValue("@cutoff", cutoffDate);
            var deleted = cmd.ExecuteNonQuery();

            Log.Info($"Cleaned up {deleted} old execution records", evt: "db.cleanup");
            return deleted;
        }
    }

    /// <summary>
    /// Información de la base de datos
    /// </summary>
    public class DatabaseInfo
    {
        public string Path { get; set; } = string.Empty;
        public long SizeBytes { get; set; }
        public string SizeMB => (SizeBytes / 1024.0 / 1024.0).ToString("F2");
        public int ConfigCount { get; set; }
        public int ConnectionCount { get; set; }
        public int ExecutionCount { get; set; }
        public string SchemaVersion { get; set; } = string.Empty;
    }
}
