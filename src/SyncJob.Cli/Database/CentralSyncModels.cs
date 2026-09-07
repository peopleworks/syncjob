using System;
using System.Collections.Generic;

namespace SyncJob.Database
{
    /// <summary>
    /// Configuración de sincronización central almacenada en SQLite local
    /// </summary>
    public class CentralSyncConfigEntity
    {
        public string ConfigKey { get; set; } = string.Empty;
        public string ConfigValue { get; set; } = string.Empty;
        public bool IsEncrypted { get; set; }
        public DateTime UpdatedAt { get; set; }
        public string? Description { get; set; }
    }

    /// <summary>
    /// Configuración completa para sincronización central
    /// </summary>
    public class CentralSyncSettings
    {
        public bool Enabled { get; set; }
        public string ProjectId { get; set; } = string.Empty;
        public string ServerId { get; set; } = string.Empty;
        public string? ConnectionString { get; set; }
        public string? ApiKey { get; set; }
        public string? ServerUrl { get; set; }
        public string SyncMode { get; set; } = "AfterEveryExecution"; // AfterEveryExecution, Manual, Scheduled
        public bool SyncConfigurations { get; set; } = true;
        public bool SyncConnections { get; set; } = false;
        public DateTime? LastSyncAt { get; set; }
        public int BatchSize { get; set; } = 100;
        public int MaxRetries { get; set; } = 3;
        public int RetryDelaySeconds { get; set; } = 5;
    }

    /// <summary>
    /// Resultado de una operación de sincronización
    /// </summary>
    public class SyncResult
    {
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }
        public int RecordsProcessed { get; set; }
        public int RecordsFailed { get; set; }
        public long DurationMs { get; set; }
        public DateTime SyncedAt { get; set; } = DateTime.Now;
        public SyncType Type { get; set; }
    }

    /// <summary>
    /// Tipos de sincronización
    /// </summary>
    public enum SyncType
    {
        Execution,
        Configuration,
        Connection,
        Heartbeat,
        FullSync
    }

    /// <summary>
    /// Estado de la conexión central
    /// </summary>
    public class CentralConnectionStatus
    {
        public bool IsConfigured { get; set; }
        public bool IsConnected { get; set; }
        public bool IsAuthenticated { get; set; }
        public string? ProjectId { get; set; }
        public string? ServerId { get; set; }
        public string? ServerUrl { get; set; }
        public DateTime? LastHeartbeat { get; set; }
        public DateTime? LastSyncAt { get; set; }
        public string? ErrorMessage { get; set; }
        public string Version { get; set; } = "1.0.0";
    }

    /// <summary>
    /// DTO para enviar ejecución al central
    /// </summary>
    public class ExecutionSyncDto
    {
        public string ExecutionId { get; set; } = string.Empty;
        public string ProjectId { get; set; } = string.Empty;
        public string ServerId { get; set; } = string.Empty;
        public string ConfigId { get; set; } = string.Empty;
        public string? ConfigDisplayName { get; set; }
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

    /// <summary>
    /// DTO para enviar configuración al central
    /// </summary>
    public class ConfigurationSyncDto
    {
        public string ConfigId { get; set; } = string.Empty;
        public string ProjectId { get; set; } = string.Empty;
        public string ServerId { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string? Description { get; set; }
        public string? SourceConnectionId { get; set; }
        public string? SourceQuery { get; set; }
        public string? SourceStoredProc { get; set; }
        public string? SourceParameters { get; set; }
        public string? DestConnectionId { get; set; }
        public string? DestStageTable { get; set; }
        public string? DestFinalTable { get; set; }
        public int BatchSize { get; set; }
        public int MaxDOP { get; set; }
        public int BulkCopyTimeout { get; set; }
        public bool KeepIdentity { get; set; }
        public int MinRowThreshold { get; set; }
        public bool IsActive { get; set; }
        public string? TrackingMode { get; set; }
        public string? TrackingColumn { get; set; }
        public string? MergeStrategy { get; set; }
        public string? CreatedBy { get; set; }
        public string? Tags { get; set; }
    }

    /// <summary>
    /// DTO para heartbeat (ping) al central
    /// </summary>
    public class HeartbeatDto
    {
        public string ProjectId { get; set; } = string.Empty;
        public string ServerId { get; set; } = string.Empty;
        public string ServerName { get; set; } = string.Empty;
        public string HostMachine { get; set; } = string.Empty;
        public string? IpAddress { get; set; }
        public string? OperatingSystem { get; set; }
        public string SyncJobVersion { get; set; } = "1.0.0";
        public bool IsOnline { get; set; } = true;
        public DateTime Timestamp { get; set; } = DateTime.Now;
    }

    /// <summary>
    /// Batch de ejecuciones para sincronización
    /// </summary>
    public class ExecutionBatchDto
    {
        public string ProjectId { get; set; } = string.Empty;
        public string ServerId { get; set; } = string.Empty;
        public List<ExecutionSyncDto> Executions { get; set; } = new();
    }

    /// <summary>
    /// Respuesta del servidor central
    /// </summary>
    public class CentralApiResponse<T>
    {
        public bool Success { get; set; }
        public string? Message { get; set; }
        public T? Data { get; set; }
        public int RecordsProcessed { get; set; }
        public int RecordsFailed { get; set; }
        public List<string>? Errors { get; set; }
    }
}
