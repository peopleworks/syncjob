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

        /// <summary>
        /// True when the run deliberately did not write: a guard that refused, or a lease
        /// another host holds.
        /// <para>
        /// It is a kind of success and not a kind of failure - a night of skips is a
        /// report and a night of failures is a phone call - but it is not the same thing
        /// as a load that happened, and a caller that logs "completed successfully" for
        /// one is telling the operator the table was refreshed when it was not.
        /// </para>
        /// </summary>
        public bool Skipped { get; set; }

        /// <summary>
        /// What the run's steps had to say: why the guard refused, which host holds the
        /// lease, what could not be carried across. Null when everything published
        /// quietly.
        /// </summary>
        public string? Notes { get; set; }

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
