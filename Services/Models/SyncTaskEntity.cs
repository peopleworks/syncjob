using System;

namespace SyncJob.Services.Models
{
    /// <summary>
    /// Entidad de tarea de sincronización desde SyncJobCentralDB
    /// </summary>
    public class SyncTaskEntity
    {
        public Guid TaskId { get; set; }
        public string ProjectId { get; set; } = string.Empty;
        public string? ServerId { get; set; }
        public string? ConfigId { get; set; }
        public string Status { get; set; } = string.Empty;
        public int Priority { get; set; }
        public string TaskType { get; set; } = string.Empty;
        public string? RequestedBy { get; set; }
        public DateTime RequestedAt { get; set; }
        public string? RequestReason { get; set; }
        public string? NotificationEmail { get; set; }
        public bool NotifyOnComplete { get; set; }

        // Campos de ejecución
        public DateTime? StartedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public long? DurationMs { get; set; }
        public long? RowsProcessed { get; set; }
        public long? RowsInserted { get; set; }
        public long? RowsUpdated { get; set; }
        public long? RowsDeleted { get; set; }
        public long? RowsFailed { get; set; }
        public Guid? ExecutionId { get; set; }
        public string? ErrorMessage { get; set; }
        public string? ErrorStackTrace { get; set; }
    }

    /// <summary>
    /// Resultado de ejecución de una tarea
    /// </summary>
    public class SyncTaskExecutionResult
    {
        public bool Success { get; set; }
        public long DurationMs { get; set; }
        public long RowsProcessed { get; set; }
        public long RowsInserted { get; set; }
        public long RowsUpdated { get; set; }
        public long RowsDeleted { get; set; }
        public long RowsFailed { get; set; }
        public Guid? ExecutionId { get; set; }
        public string? ErrorMessage { get; set; }
        public string? ErrorStackTrace { get; set; }
    }
}
