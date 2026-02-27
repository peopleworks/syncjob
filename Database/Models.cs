using System;
using System.Collections.Generic;

namespace SyncJob.Database
{
    // ============================================================================
    // DOMAIN MODELS
    // ============================================================================

    public class ConfigurationEntity
    {
        public string ConfigId { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string? Description { get; set; }
        public string SourceConnectionId { get; set; } = string.Empty;
        public string? SourceQuery { get; set; }
        public string? SourceStoredProc { get; set; }
        public string? SourceParameters { get; set; }  // JSON
        public string DestConnectionId { get; set; } = string.Empty;
        public string? DestStageTable { get; set; }
        public string DestFinalTable { get; set; } = string.Empty;
        public int BatchSize { get; set; } = 10000;
        public int MaxDOP { get; set; } = 4;
        public int BulkCopyTimeout { get; set; } = 0;
        public bool KeepIdentity { get; set; } = true;
        public int MinRowThreshold { get; set; } = 0;
        public bool IsActive { get; set; } = true;
        public string TrackingMode { get; set; } = "Snapshot";
        public string? TrackingColumn { get; set; }
        public string MergeStrategy { get; set; } = "Upsert";
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public string? CreatedBy { get; set; }
        public string? Tags { get; set; }  // JSON

        // Navigation properties
        public List<ColumnMappingEntity> ColumnMappings { get; set; } = new();
    }

    public class ConnectionEntity
    {
        public string ConnectionId { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string ServerType { get; set; } = "SqlServer";
        public string ServerName { get; set; } = string.Empty;
        public string DatabaseName { get; set; } = string.Empty;
        public string? Username { get; set; }
        public byte[]? PasswordEncrypted { get; set; }
        public byte[]? ConnectionStringEncrypted { get; set; }
        public bool TrustServerCertificate { get; set; } = false;
        public bool Encrypt { get; set; } = true;
        public bool IsActive { get; set; } = true;
        public DateTime? LastTestDate { get; set; }
        public bool? LastTestSuccess { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    public class ColumnMappingEntity
    {
        public int MappingId { get; set; }
        public string ConfigId { get; set; } = string.Empty;
        public string SourceColumn { get; set; } = string.Empty;
        public string DestColumn { get; set; } = string.Empty;
        public bool IsPrimaryKey { get; set; } = false;
        public string? TransformType { get; set; }
        public string? TransformExpression { get; set; }
        public int? Ordinal { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    public class ExecutionHistoryEntity
    {
        public string ExecutionId { get; set; } = string.Empty;
        public string ConfigId { get; set; } = string.Empty;
        public DateTime StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public long? DurationMs { get; set; }
        public string Status { get; set; } = string.Empty;
        public string? ExecutionMode { get; set; }
        public long RowsRead { get; set; }
        public long RowsInserted { get; set; }
        public long RowsUpdated { get; set; }
        public long RowsDeleted { get; set; }
        public long RowsSkipped { get; set; }
        public long RowsFailed { get; set; }
        public string? ErrorMessage { get; set; }
        public string? ErrorStackTrace { get; set; }
        public string? HostMachine { get; set; }
        public string? TriggeredBy { get; set; }
        public string? LogFilePath { get; set; }
    }

    public class SnapshotDataEntity
    {
        public string JobId { get; set; } = string.Empty;
        public string PrimaryKeyValue { get; set; } = string.Empty;
        public string RowHash { get; set; } = string.Empty;
        public DateTime LastSeen { get; set; }
        public DateTime FirstSeen { get; set; }
        public DateTime? LastSyncedAt { get; set; }
        public string? ChangeType { get; set; }
    }

    public class SnapshotMetadataEntity
    {
        public string JobId { get; set; } = string.Empty;
        public DateTime? LastSnapshotDate { get; set; }
        public long TotalRows { get; set; }
        public string? SnapshotHash { get; set; }
        public bool CompressionEnabled { get; set; } = false;
        public string? PartitionStrategy { get; set; }
        public string? PartitionValue { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    // ============================================================================
    // ENUMS
    // ============================================================================

    public enum ExecutionStatus
    {
        Running,
        Success,
        Failed,
        Cancelled
    }

    public enum ExecutionMode
    {
        Full,
        Incremental,
        DryRun
    }

    public enum TrackingModeEnum
    {
        None,
        Snapshot,
        Timestamp,
        RowVersion,
        ChangeTracking,
        ChangeDataCapture
    }

    public enum MergeStrategyEnum
    {
        Insert,
        Upsert,
        Full
    }

    // ============================================================================
    // DTOs
    // ============================================================================

    public class ConfigurationListItem
    {
        public string ConfigId { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string? Description { get; set; }
        public bool IsActive { get; set; }
        public string TrackingMode { get; set; } = string.Empty;
        public DateTime UpdatedAt { get; set; }
        public int MappingsCount { get; set; }
        public DateTime? LastExecution { get; set; }
        public string? LastExecutionStatus { get; set; }
    }

    public class ConnectionListItem
    {
        public string ConnectionId { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string ServerName { get; set; } = string.Empty;
        public string DatabaseName { get; set; } = string.Empty;
        public bool IsActive { get; set; }
        public DateTime? LastTestDate { get; set; }
        public bool? LastTestSuccess { get; set; }
    }

    public class ExecutionSummary
    {
        public string ExecutionId { get; set; } = string.Empty;
        public string ConfigId { get; set; } = string.Empty;
        public DateTime StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public long? DurationMs { get; set; }
        public string DurationFormatted
        {
            get
            {
                if (!DurationMs.HasValue) return "-";
                var ts = TimeSpan.FromMilliseconds(DurationMs.Value);
                if (ts.TotalHours >= 1)
                    return $"{ts.Hours}h {ts.Minutes}m {ts.Seconds}s";
                if (ts.TotalMinutes >= 1)
                    return $"{ts.Minutes}m {ts.Seconds}s";
                return $"{ts.Seconds}s";
            }
        }
        public string Status { get; set; } = string.Empty;
        public long RowsProcessed => RowsInserted + RowsUpdated + RowsDeleted;
        public long RowsInserted { get; set; }
        public long RowsUpdated { get; set; }
        public long RowsDeleted { get; set; }
    }
}
