using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using SyncJob.Services.Models;
using System.Data;

namespace SyncJob.Services
{
    /// <summary>
    /// Monitor de tareas de sincronización desde SyncJobCentralDB
    /// </summary>
    public class SyncTaskMonitor
    {
        private readonly string _centralConnectionString;
        private readonly ILogger<SyncTaskMonitor> _logger;

        public SyncTaskMonitor(string centralConnectionString, ILogger<SyncTaskMonitor> logger)
        {
            _centralConnectionString = centralConnectionString;
            _logger = logger;
        }

        /// <summary>
        /// Obtiene las tareas pendientes de la cola
        /// </summary>
        public async Task<List<SyncTaskEntity>> GetPendingTasksAsync(int maxTasks = 10, string? projectId = null)
        {
            try
            {
                using var connection = new SqlConnection(_centralConnectionString);
                await connection.OpenAsync();

                using var cmd = new SqlCommand("sp_GetPendingSyncTasks", connection);
                cmd.CommandType = CommandType.StoredProcedure;
                cmd.Parameters.AddWithValue("@MaxTasks", maxTasks);
                cmd.Parameters.AddWithValue("@ProjectId", (object?)projectId ?? DBNull.Value);

                var tasks = new List<SyncTaskEntity>();
                using var reader = await cmd.ExecuteReaderAsync();

                while (await reader.ReadAsync())
                {
                    tasks.Add(MapTaskFromReader(reader));
                }

                _logger.LogInformation("Found {Count} pending sync tasks", tasks.Count);
                return tasks;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting pending sync tasks");
                throw;
            }
        }

        /// <summary>
        /// Actualiza el estado de una tarea
        /// </summary>
        public async Task UpdateTaskStatusAsync(
            Guid taskId,
            string status,
            DateTime? startedAt = null,
            DateTime? completedAt = null,
            long? durationMs = null,
            long? rowsProcessed = null,
            long? rowsInserted = null,
            long? rowsUpdated = null,
            long? rowsDeleted = null,
            long? rowsFailed = null,
            Guid? executionId = null,
            string? errorMessage = null,
            string? errorStackTrace = null)
        {
            try
            {
                using var connection = new SqlConnection(_centralConnectionString);
                await connection.OpenAsync();

                using var cmd = new SqlCommand("sp_UpdateSyncTaskStatus", connection);
                cmd.CommandType = CommandType.StoredProcedure;

                cmd.Parameters.AddWithValue("@TaskId", taskId);
                cmd.Parameters.AddWithValue("@Status", status);
                cmd.Parameters.AddWithValue("@StartedAt", (object?)startedAt ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@CompletedAt", (object?)completedAt ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@DurationMs", (object?)durationMs ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@RowsProcessed", (object?)rowsProcessed ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@RowsInserted", (object?)rowsInserted ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@RowsUpdated", (object?)rowsUpdated ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@RowsDeleted", (object?)rowsDeleted ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@RowsFailed", (object?)rowsFailed ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@ExecutionId", (object?)executionId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@ErrorMessage", (object?)errorMessage ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@ErrorStackTrace", (object?)errorStackTrace ?? DBNull.Value);

                await cmd.ExecuteNonQueryAsync();

                _logger.LogInformation("Updated task {TaskId} to status {Status}", taskId, status);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating task {TaskId} status to {Status}", taskId, status);
                throw;
            }
        }

        /// <summary>
        /// Marca una tarea como iniciada (Running)
        /// </summary>
        public async Task MarkTaskAsRunningAsync(Guid taskId, Guid executionId)
        {
            await UpdateTaskStatusAsync(
                taskId: taskId,
                status: "Running",
                startedAt: DateTime.Now,
                executionId: executionId
            );
        }

        /// <summary>
        /// Marca una tarea como completada exitosamente
        /// </summary>
        public async Task MarkTaskAsCompletedAsync(Guid taskId, SyncTaskExecutionResult result)
        {
            await UpdateTaskStatusAsync(
                taskId: taskId,
                status: "Completed",
                completedAt: DateTime.Now,
                durationMs: result.DurationMs,
                rowsProcessed: result.RowsProcessed,
                rowsInserted: result.RowsInserted,
                rowsUpdated: result.RowsUpdated,
                rowsDeleted: result.RowsDeleted,
                rowsFailed: result.RowsFailed,
                executionId: result.ExecutionId
            );
        }

        /// <summary>
        /// Marca una tarea como fallida
        /// </summary>
        public async Task MarkTaskAsFailedAsync(Guid taskId, long durationMs, string errorMessage, string? stackTrace = null)
        {
            await UpdateTaskStatusAsync(
                taskId: taskId,
                status: "Failed",
                completedAt: DateTime.Now,
                durationMs: durationMs,
                errorMessage: errorMessage,
                errorStackTrace: stackTrace
            );
        }

        /// <summary>
        /// Obtiene el estado de una tarea específica
        /// </summary>
        public async Task<SyncTaskEntity?> GetTaskStatusAsync(Guid taskId)
        {
            try
            {
                using var connection = new SqlConnection(_centralConnectionString);
                await connection.OpenAsync();

                var query = @"
                    SELECT
                        TaskId, ProjectId, ServerId, ConfigId, Status, Priority, TaskType,
                        RequestedBy, RequestedAt, RequestReason, NotificationEmail, NotifyOnComplete,
                        StartedAt, CompletedAt, DurationMs, RowsProcessed, RowsInserted,
                        RowsUpdated, RowsDeleted, RowsFailed, ExecutionId, ErrorMessage, ErrorStackTrace
                    FROM SyncTasks
                    WHERE TaskId = @TaskId";

                using var cmd = new SqlCommand(query, connection);
                cmd.Parameters.AddWithValue("@TaskId", taskId);

                using var reader = await cmd.ExecuteReaderAsync();

                if (await reader.ReadAsync())
                {
                    return MapTaskFromReader(reader);
                }

                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting task status for {TaskId}", taskId);
                throw;
            }
        }

        /// <summary>
        /// Mapea un SqlDataReader a SyncTaskEntity
        /// </summary>
        private SyncTaskEntity MapTaskFromReader(SqlDataReader reader)
        {
            return new SyncTaskEntity
            {
                TaskId = reader.GetGuid(reader.GetOrdinal("TaskId")),
                ProjectId = reader.GetString(reader.GetOrdinal("ProjectId")),
                ServerId = reader.IsDBNull(reader.GetOrdinal("ServerId")) ? null : reader.GetString(reader.GetOrdinal("ServerId")),
                ConfigId = reader.IsDBNull(reader.GetOrdinal("ConfigId")) ? null : reader.GetString(reader.GetOrdinal("ConfigId")),
                Status = reader.GetString(reader.GetOrdinal("Status")),
                Priority = reader.GetInt32(reader.GetOrdinal("Priority")),
                TaskType = reader.GetString(reader.GetOrdinal("TaskType")),
                RequestedBy = reader.IsDBNull(reader.GetOrdinal("RequestedBy")) ? null : reader.GetString(reader.GetOrdinal("RequestedBy")),
                RequestedAt = reader.GetDateTime(reader.GetOrdinal("RequestedAt")),
                RequestReason = reader.IsDBNull(reader.GetOrdinal("RequestReason")) ? null : reader.GetString(reader.GetOrdinal("RequestReason")),
                NotificationEmail = reader.IsDBNull(reader.GetOrdinal("NotificationEmail")) ? null : reader.GetString(reader.GetOrdinal("NotificationEmail")),
                NotifyOnComplete = reader.GetBoolean(reader.GetOrdinal("NotifyOnComplete")),
                StartedAt = reader.IsDBNull(reader.GetOrdinal("StartedAt")) ? null : reader.GetDateTime(reader.GetOrdinal("StartedAt")),
                CompletedAt = reader.IsDBNull(reader.GetOrdinal("CompletedAt")) ? null : reader.GetDateTime(reader.GetOrdinal("CompletedAt")),
                DurationMs = reader.IsDBNull(reader.GetOrdinal("DurationMs")) ? null : reader.GetInt64(reader.GetOrdinal("DurationMs")),
                RowsProcessed = reader.IsDBNull(reader.GetOrdinal("RowsProcessed")) ? null : reader.GetInt64(reader.GetOrdinal("RowsProcessed")),
                RowsInserted = reader.IsDBNull(reader.GetOrdinal("RowsInserted")) ? null : reader.GetInt64(reader.GetOrdinal("RowsInserted")),
                RowsUpdated = reader.IsDBNull(reader.GetOrdinal("RowsUpdated")) ? null : reader.GetInt64(reader.GetOrdinal("RowsUpdated")),
                RowsDeleted = reader.IsDBNull(reader.GetOrdinal("RowsDeleted")) ? null : reader.GetInt64(reader.GetOrdinal("RowsDeleted")),
                RowsFailed = reader.IsDBNull(reader.GetOrdinal("RowsFailed")) ? null : reader.GetInt64(reader.GetOrdinal("RowsFailed")),
                ExecutionId = reader.IsDBNull(reader.GetOrdinal("ExecutionId")) ? null : reader.GetGuid(reader.GetOrdinal("ExecutionId")),
                ErrorMessage = reader.IsDBNull(reader.GetOrdinal("ErrorMessage")) ? null : reader.GetString(reader.GetOrdinal("ErrorMessage")),
                ErrorStackTrace = reader.IsDBNull(reader.GetOrdinal("ErrorStackTrace")) ? null : reader.GetString(reader.GetOrdinal("ErrorStackTrace"))
            };
        }
    }
}
