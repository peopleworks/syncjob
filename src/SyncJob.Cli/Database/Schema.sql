-- ============================================================================
-- SyncJob Metadata Store - SQLite Schema
-- Version: 1.0.0
-- ============================================================================

-- ============================================================================
-- CONFIGURATIONS
-- ============================================================================
CREATE TABLE IF NOT EXISTS Configurations (
    ConfigId TEXT PRIMARY KEY,
    DisplayName TEXT NOT NULL,
    Description TEXT,
    SourceConnectionId TEXT NOT NULL,
    SourceQuery TEXT,
    SourceStoredProc TEXT,
    SourceParameters TEXT,           -- JSON
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

-- ============================================================================
-- CONNECTIONS
-- ============================================================================
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

-- ============================================================================
-- COLUMN MAPPINGS
-- ============================================================================
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

-- ============================================================================
-- EXECUTION HISTORY
-- ============================================================================
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

CREATE INDEX IF NOT EXISTS IX_ExecutionHistory_ConfigId ON ExecutionHistory(ConfigId);
CREATE INDEX IF NOT EXISTS IX_ExecutionHistory_StartTime ON ExecutionHistory(StartTime);
CREATE INDEX IF NOT EXISTS IX_ExecutionHistory_Status ON ExecutionHistory(Status);

-- ============================================================================
-- SNAPSHOT TRACKING
-- ============================================================================
CREATE TABLE IF NOT EXISTS SnapshotData (
    JobId TEXT NOT NULL,
    PrimaryKeyValue TEXT NOT NULL,
    RowHash TEXT NOT NULL,
    LastSeen TEXT NOT NULL,
    FirstSeen TEXT NOT NULL,
    LastSyncedAt TEXT,
    ChangeType TEXT,
    PRIMARY KEY (JobId, PrimaryKeyValue)
);

CREATE INDEX IF NOT EXISTS IX_SnapshotData_JobId ON SnapshotData(JobId);
CREATE INDEX IF NOT EXISTS IX_SnapshotData_LastSeen ON SnapshotData(LastSeen);

CREATE TABLE IF NOT EXISTS SnapshotMetadata (
    JobId TEXT PRIMARY KEY,
    LastSnapshotDate TEXT,
    TotalRows INTEGER,
    SnapshotHash TEXT,
    CompressionEnabled INTEGER DEFAULT 0,
    PartitionStrategy TEXT,
    PartitionValue TEXT,
    CreatedAt TEXT DEFAULT (datetime('now')),
    UpdatedAt TEXT DEFAULT (datetime('now'))
);

-- ============================================================================
-- SCHEDULES
-- ============================================================================
CREATE TABLE IF NOT EXISTS Schedules (
    ScheduleId INTEGER PRIMARY KEY AUTOINCREMENT,
    ConfigId TEXT NOT NULL,
    CronExpression TEXT NOT NULL,
    Timezone TEXT DEFAULT 'UTC',
    IsEnabled INTEGER DEFAULT 1,
    NextRunTime TEXT,
    LastRunTime TEXT,
    LastRunStatus TEXT,
    CreatedAt TEXT DEFAULT (datetime('now')),
    UpdatedAt TEXT DEFAULT (datetime('now')),
    FOREIGN KEY (ConfigId) REFERENCES Configurations(ConfigId) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS IX_Schedules_ConfigId ON Schedules(ConfigId);
CREATE INDEX IF NOT EXISTS IX_Schedules_NextRunTime ON Schedules(NextRunTime);

-- ============================================================================
-- ALERTS
-- ============================================================================
CREATE TABLE IF NOT EXISTS Alerts (
    AlertId INTEGER PRIMARY KEY AUTOINCREMENT,
    ConfigId TEXT,
    AlertType TEXT NOT NULL,
    IsEnabled INTEGER DEFAULT 1,
    Threshold TEXT,
    NotificationChannels TEXT,
    LastTriggered TEXT,
    CreatedAt TEXT DEFAULT (datetime('now'))
);

-- ============================================================================
-- PERFORMANCE METRICS
-- ============================================================================
CREATE TABLE IF NOT EXISTS PerformanceMetrics (
    MetricId INTEGER PRIMARY KEY AUTOINCREMENT,
    ExecutionId TEXT NOT NULL,
    MetricName TEXT NOT NULL,
    MetricValue REAL,
    Unit TEXT,
    RecordedAt TEXT DEFAULT (datetime('now')),
    FOREIGN KEY (ExecutionId) REFERENCES ExecutionHistory(ExecutionId)
);

CREATE INDEX IF NOT EXISTS IX_PerformanceMetrics_ExecutionId ON PerformanceMetrics(ExecutionId);

-- ============================================================================
-- TAGS
-- ============================================================================
CREATE TABLE IF NOT EXISTS Tags (
    TagId INTEGER PRIMARY KEY AUTOINCREMENT,
    TagName TEXT UNIQUE NOT NULL,
    Color TEXT,
    CreatedAt TEXT DEFAULT (datetime('now'))
);

CREATE TABLE IF NOT EXISTS ConfigTags (
    ConfigId TEXT NOT NULL,
    TagId INTEGER NOT NULL,
    PRIMARY KEY (ConfigId, TagId),
    FOREIGN KEY (ConfigId) REFERENCES Configurations(ConfigId) ON DELETE CASCADE,
    FOREIGN KEY (TagId) REFERENCES Tags(TagId) ON DELETE CASCADE
);

-- ============================================================================
-- AUDIT LOG
-- ============================================================================
CREATE TABLE IF NOT EXISTS AuditLog (
    AuditId INTEGER PRIMARY KEY AUTOINCREMENT,
    Timestamp TEXT DEFAULT (datetime('now')),
    Action TEXT NOT NULL,
    EntityType TEXT,
    EntityId TEXT,
    OldValue TEXT,
    NewValue TEXT,
    PerformedBy TEXT,
    HostMachine TEXT
);

CREATE INDEX IF NOT EXISTS IX_AuditLog_Timestamp ON AuditLog(Timestamp);
CREATE INDEX IF NOT EXISTS IX_AuditLog_EntityType ON AuditLog(EntityType);

-- ============================================================================
-- CENTRAL SYNC CONFIGURATION
-- ============================================================================
CREATE TABLE IF NOT EXISTS CentralSyncConfig (
    ConfigKey TEXT PRIMARY KEY,
    ConfigValue TEXT,
    IsEncrypted INTEGER DEFAULT 0,
    UpdatedAt TEXT DEFAULT (datetime('now')),
    Description TEXT
);

CREATE INDEX IF NOT EXISTS IX_CentralSyncConfig_IsEncrypted ON CentralSyncConfig(IsEncrypted);

-- Configuración por defecto (deshabilitado)
INSERT OR IGNORE INTO CentralSyncConfig (ConfigKey, ConfigValue, IsEncrypted, Description)
VALUES ('Enabled', 'false', 0, 'Enable/disable central synchronization');

INSERT OR IGNORE INTO CentralSyncConfig (ConfigKey, ConfigValue, IsEncrypted, Description)
VALUES ('ProjectId', '', 0, 'Project identifier in central database');

INSERT OR IGNORE INTO CentralSyncConfig (ConfigKey, ConfigValue, IsEncrypted, Description)
VALUES ('ServerId', '', 0, 'Server instance identifier');

INSERT OR IGNORE INTO CentralSyncConfig (ConfigKey, ConfigValue, IsEncrypted, Description)
VALUES ('SyncMode', 'AfterEveryExecution', 0, 'When to sync: AfterEveryExecution, Manual, Scheduled');

INSERT OR IGNORE INTO CentralSyncConfig (ConfigKey, ConfigValue, IsEncrypted, Description)
VALUES ('SyncConfigurations', 'true', 0, 'Sync local configurations to central');

INSERT OR IGNORE INTO CentralSyncConfig (ConfigKey, ConfigValue, IsEncrypted, Description)
VALUES ('SyncConnections', 'false', 0, 'Sync connection strings to central (security sensitive)');

-- ============================================================================
-- SCHEMA VERSION
-- ============================================================================
CREATE TABLE IF NOT EXISTS SchemaVersion (
    Version TEXT PRIMARY KEY,
    AppliedAt TEXT DEFAULT (datetime('now')),
    Description TEXT
);

INSERT OR IGNORE INTO SchemaVersion (Version, Description)
VALUES ('1.0.0', 'Initial schema');

INSERT OR IGNORE INTO SchemaVersion (Version, Description)
VALUES ('1.1.0', 'Added CentralSyncConfig table for central synchronization');
