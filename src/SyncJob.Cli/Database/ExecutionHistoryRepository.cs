using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;

namespace SyncJob.Database
{
    /// <summary>
    /// Repositorio para CRUD de Execution History
    /// </summary>
    public static class ExecutionHistoryRepository
    {
        /// <summary>
        /// Crear un nuevo registro de ejecución
        /// </summary>
        public static string Create(ExecutionHistoryEntity execution)
        {
            using var conn = DbManager.GetConnection();
            conn.Open();

            // Generar ID si no existe
            if (string.IsNullOrEmpty(execution.ExecutionId))
            {
                execution.ExecutionId = Guid.NewGuid().ToString();
            }

            const string sql = @"
                INSERT INTO ExecutionHistory
                (ExecutionId, ConfigId, StartTime, EndTime, DurationMs, Status, ExecutionMode,
                 RowsRead, RowsInserted, RowsUpdated, RowsDeleted, RowsSkipped, RowsFailed,
                 ErrorMessage, ErrorStackTrace, HostMachine, TriggeredBy, LogFilePath)
                VALUES
                (@ExecutionId, @ConfigId, @StartTime, @EndTime, @DurationMs, @Status, @ExecutionMode,
                 @RowsRead, @RowsInserted, @RowsUpdated, @RowsDeleted, @RowsSkipped, @RowsFailed,
                 @ErrorMessage, @ErrorStackTrace, @HostMachine, @TriggeredBy, @LogFilePath)";

            using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.AddWithValue("@ExecutionId", execution.ExecutionId);
            cmd.Parameters.AddWithValue("@ConfigId", execution.ConfigId);
            cmd.Parameters.AddWithValue("@StartTime", execution.StartTime.ToString("yyyy-MM-dd HH:mm:ss"));
            cmd.Parameters.AddWithValue("@EndTime", execution.EndTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@DurationMs", (object?)execution.DurationMs ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Status", execution.Status);
            cmd.Parameters.AddWithValue("@ExecutionMode", (object?)execution.ExecutionMode ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@RowsRead", execution.RowsRead);
            cmd.Parameters.AddWithValue("@RowsInserted", execution.RowsInserted);
            cmd.Parameters.AddWithValue("@RowsUpdated", execution.RowsUpdated);
            cmd.Parameters.AddWithValue("@RowsDeleted", execution.RowsDeleted);
            cmd.Parameters.AddWithValue("@RowsSkipped", execution.RowsSkipped);
            cmd.Parameters.AddWithValue("@RowsFailed", execution.RowsFailed);
            cmd.Parameters.AddWithValue("@ErrorMessage", (object?)execution.ErrorMessage ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@ErrorStackTrace", (object?)execution.ErrorStackTrace ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@HostMachine", (object?)execution.HostMachine ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@TriggeredBy", (object?)execution.TriggeredBy ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@LogFilePath", (object?)execution.LogFilePath ?? DBNull.Value);

            cmd.ExecuteNonQuery();

            return execution.ExecutionId;
        }

        /// <summary>
        /// Actualizar un registro de ejecución existente (usado para marcar como finalizado)
        /// </summary>
        public static void Update(ExecutionHistoryEntity execution)
        {
            using var conn = DbManager.GetConnection();
            conn.Open();

            const string sql = @"
                UPDATE ExecutionHistory
                SET EndTime = @EndTime,
                    DurationMs = @DurationMs,
                    Status = @Status,
                    RowsRead = @RowsRead,
                    RowsInserted = @RowsInserted,
                    RowsUpdated = @RowsUpdated,
                    RowsDeleted = @RowsDeleted,
                    RowsSkipped = @RowsSkipped,
                    RowsFailed = @RowsFailed,
                    ErrorMessage = @ErrorMessage,
                    ErrorStackTrace = @ErrorStackTrace
                WHERE ExecutionId = @ExecutionId";

            using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.AddWithValue("@ExecutionId", execution.ExecutionId);
            cmd.Parameters.AddWithValue("@EndTime", execution.EndTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@DurationMs", (object?)execution.DurationMs ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Status", execution.Status);
            cmd.Parameters.AddWithValue("@RowsRead", execution.RowsRead);
            cmd.Parameters.AddWithValue("@RowsInserted", execution.RowsInserted);
            cmd.Parameters.AddWithValue("@RowsUpdated", execution.RowsUpdated);
            cmd.Parameters.AddWithValue("@RowsDeleted", execution.RowsDeleted);
            cmd.Parameters.AddWithValue("@RowsSkipped", execution.RowsSkipped);
            cmd.Parameters.AddWithValue("@RowsFailed", execution.RowsFailed);
            cmd.Parameters.AddWithValue("@ErrorMessage", (object?)execution.ErrorMessage ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@ErrorStackTrace", (object?)execution.ErrorStackTrace ?? DBNull.Value);

            cmd.ExecuteNonQuery();
        }

        /// <summary>
        /// Obtener una ejecución específica por ID
        /// </summary>
        public static ExecutionHistoryEntity? GetById(string executionId)
        {
            using var conn = DbManager.GetConnection();
            conn.Open();

            const string sql = @"
                SELECT ExecutionId, ConfigId, StartTime, EndTime, DurationMs, Status, ExecutionMode,
                       RowsRead, RowsInserted, RowsUpdated, RowsDeleted, RowsSkipped, RowsFailed,
                       ErrorMessage, ErrorStackTrace, HostMachine, TriggeredBy, LogFilePath
                FROM ExecutionHistory
                WHERE ExecutionId = @ExecutionId";

            using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.AddWithValue("@ExecutionId", executionId);

            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                return MapFromReader(reader);
            }

            return null;
        }

        /// <summary>
        /// Obtener todas las ejecuciones de una configuración
        /// </summary>
        public static List<ExecutionHistoryEntity> GetByConfigId(string configId, int limit = 100)
        {
            var executions = new List<ExecutionHistoryEntity>();

            using var conn = DbManager.GetConnection();
            conn.Open();

            const string sql = @"
                SELECT ExecutionId, ConfigId, StartTime, EndTime, DurationMs, Status, ExecutionMode,
                       RowsRead, RowsInserted, RowsUpdated, RowsDeleted, RowsSkipped, RowsFailed,
                       ErrorMessage, ErrorStackTrace, HostMachine, TriggeredBy, LogFilePath
                FROM ExecutionHistory
                WHERE ConfigId = @ConfigId
                ORDER BY StartTime DESC
                LIMIT @Limit";

            using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.AddWithValue("@ConfigId", configId);
            cmd.Parameters.AddWithValue("@Limit", limit);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                executions.Add(MapFromReader(reader));
            }

            return executions;
        }

        /// <summary>
        /// Obtener todas las ejecuciones (con filtros opcionales)
        /// </summary>
        public static List<ExecutionHistoryEntity> GetAll(
            string? status = null,
            DateTime? startDate = null,
            DateTime? endDate = null,
            int limit = 100)
        {
            var executions = new List<ExecutionHistoryEntity>();

            using var conn = DbManager.GetConnection();
            conn.Open();

            var sql = @"
                SELECT ExecutionId, ConfigId, StartTime, EndTime, DurationMs, Status, ExecutionMode,
                       RowsRead, RowsInserted, RowsUpdated, RowsDeleted, RowsSkipped, RowsFailed,
                       ErrorMessage, ErrorStackTrace, HostMachine, TriggeredBy, LogFilePath
                FROM ExecutionHistory
                WHERE 1=1";

            if (!string.IsNullOrWhiteSpace(status))
                sql += " AND Status = @Status";

            if (startDate.HasValue)
                sql += " AND StartTime >= @StartDate";

            if (endDate.HasValue)
                sql += " AND StartTime <= @EndDate";

            sql += " ORDER BY StartTime DESC LIMIT @Limit";

            using var cmd = new SqliteCommand(sql, conn);

            if (!string.IsNullOrWhiteSpace(status))
                cmd.Parameters.AddWithValue("@Status", status);

            if (startDate.HasValue)
                cmd.Parameters.AddWithValue("@StartDate", startDate.Value.ToString("yyyy-MM-dd HH:mm:ss"));

            if (endDate.HasValue)
                cmd.Parameters.AddWithValue("@EndDate", endDate.Value.ToString("yyyy-MM-dd HH:mm:ss"));

            cmd.Parameters.AddWithValue("@Limit", limit);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                executions.Add(MapFromReader(reader));
            }

            return executions;
        }

        /// <summary>
        /// Obtener la última ejecución de una configuración
        /// </summary>
        public static ExecutionHistoryEntity? GetLastExecution(string configId)
        {
            using var conn = DbManager.GetConnection();
            conn.Open();

            const string sql = @"
                SELECT ExecutionId, ConfigId, StartTime, EndTime, DurationMs, Status, ExecutionMode,
                       RowsRead, RowsInserted, RowsUpdated, RowsDeleted, RowsSkipped, RowsFailed,
                       ErrorMessage, ErrorStackTrace, HostMachine, TriggeredBy, LogFilePath
                FROM ExecutionHistory
                WHERE ConfigId = @ConfigId
                ORDER BY StartTime DESC
                LIMIT 1";

            using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.AddWithValue("@ConfigId", configId);

            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                return MapFromReader(reader);
            }

            return null;
        }

        /// <summary>
        /// Eliminar ejecuciones más antiguas que una fecha
        /// </summary>
        public static int DeleteOlderThan(DateTime cutoffDate)
        {
            using var conn = DbManager.GetConnection();
            conn.Open();

            const string sql = "DELETE FROM ExecutionHistory WHERE StartTime < @CutoffDate";

            using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.AddWithValue("@CutoffDate", cutoffDate.ToString("yyyy-MM-dd HH:mm:ss"));

            return cmd.ExecuteNonQuery();
        }

        /// <summary>
        /// Obtener estadísticas de ejecuciones
        /// </summary>
        public static ExecutionStats GetStats(string? configId = null, int days = 30)
        {
            using var conn = DbManager.GetConnection();
            conn.Open();

            var cutoffDate = DateTime.Now.AddDays(-days);
            var sql = @"
                SELECT
                    COUNT(*) as TotalExecutions,
                    SUM(CASE WHEN Status = 'Success' THEN 1 ELSE 0 END) as SuccessCount,
                    SUM(CASE WHEN Status = 'Failed' THEN 1 ELSE 0 END) as FailedCount,
                    SUM(CASE WHEN Status = 'Running' THEN 1 ELSE 0 END) as RunningCount,
                    AVG(DurationMs) as AvgDurationMs,
                    SUM(RowsRead) as TotalRowsRead,
                    SUM(RowsInserted) as TotalRowsInserted,
                    SUM(RowsUpdated) as TotalRowsUpdated,
                    SUM(RowsDeleted) as TotalRowsDeleted
                FROM ExecutionHistory
                WHERE StartTime >= @CutoffDate";

            if (!string.IsNullOrWhiteSpace(configId))
                sql += " AND ConfigId = @ConfigId";

            using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.AddWithValue("@CutoffDate", cutoffDate.ToString("yyyy-MM-dd HH:mm:ss"));

            if (!string.IsNullOrWhiteSpace(configId))
                cmd.Parameters.AddWithValue("@ConfigId", configId);

            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                return new ExecutionStats
                {
                    TotalExecutions = reader.IsDBNull(0) ? 0 : reader.GetInt32(0),
                    SuccessCount = reader.IsDBNull(1) ? 0 : reader.GetInt32(1),
                    FailedCount = reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
                    RunningCount = reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
                    AvgDurationMs = reader.IsDBNull(4) ? 0 : reader.GetDouble(4),
                    TotalRowsRead = reader.IsDBNull(5) ? 0 : reader.GetInt64(5),
                    TotalRowsInserted = reader.IsDBNull(6) ? 0 : reader.GetInt64(6),
                    TotalRowsUpdated = reader.IsDBNull(7) ? 0 : reader.GetInt64(7),
                    TotalRowsDeleted = reader.IsDBNull(8) ? 0 : reader.GetInt64(8)
                };
            }

            return new ExecutionStats();
        }

        /// <summary>
        /// Mapear un reader a una entidad
        /// </summary>
        private static ExecutionHistoryEntity MapFromReader(SqliteDataReader reader)
        {
            return new ExecutionHistoryEntity
            {
                ExecutionId = reader.GetString(0),
                ConfigId = reader.GetString(1),
                StartTime = DateTime.Parse(reader.GetString(2)),
                EndTime = reader.IsDBNull(3) ? null : DateTime.Parse(reader.GetString(3)),
                DurationMs = reader.IsDBNull(4) ? null : reader.GetInt64(4),
                Status = reader.GetString(5),
                ExecutionMode = reader.IsDBNull(6) ? null : reader.GetString(6),
                RowsRead = reader.GetInt64(7),
                RowsInserted = reader.GetInt64(8),
                RowsUpdated = reader.GetInt64(9),
                RowsDeleted = reader.GetInt64(10),
                RowsSkipped = reader.GetInt64(11),
                RowsFailed = reader.GetInt64(12),
                ErrorMessage = reader.IsDBNull(13) ? null : reader.GetString(13),
                ErrorStackTrace = reader.IsDBNull(14) ? null : reader.GetString(14),
                HostMachine = reader.IsDBNull(15) ? null : reader.GetString(15),
                TriggeredBy = reader.IsDBNull(16) ? null : reader.GetString(16),
                LogFilePath = reader.IsDBNull(17) ? null : reader.GetString(17)
            };
        }
    }

    /// <summary>
    /// Estadísticas de ejecuciones
    /// </summary>
    public class ExecutionStats
    {
        public int TotalExecutions { get; set; }
        public int SuccessCount { get; set; }
        public int FailedCount { get; set; }
        public int RunningCount { get; set; }
        public double AvgDurationMs { get; set; }
        public long TotalRowsRead { get; set; }
        public long TotalRowsInserted { get; set; }
        public long TotalRowsUpdated { get; set; }
        public long TotalRowsDeleted { get; set; }

        public double SuccessRate => TotalExecutions > 0 ? (double)SuccessCount / TotalExecutions * 100 : 0;
    }
}
