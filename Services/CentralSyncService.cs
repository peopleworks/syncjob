using Microsoft.Data.SqlClient;
using SyncJob.Database;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace SyncJob.Services
{
    /// <summary>
    /// Servicio para sincronizar datos locales con el servidor central
    /// </summary>
    public class CentralSyncService
    {
        private readonly CentralSyncSettings _settings;

        public CentralSyncService()
        {
            _settings = CentralSyncRepository.GetSettings();
        }

        public CentralSyncService(CentralSyncSettings settings)
        {
            _settings = settings;
        }

        /// <summary>
        /// Verifica si la sincronización está habilitada y configurada
        /// </summary>
        public bool IsEnabled()
        {
            return _settings.Enabled && CentralSyncRepository.IsConfigured();
        }

        /// <summary>
        /// Prueba la conexión al servidor central
        /// </summary>
        public async Task<CentralConnectionStatus> TestConnectionAsync()
        {
            var status = new CentralConnectionStatus
            {
                IsConfigured = CentralSyncRepository.IsConfigured(),
                ProjectId = _settings.ProjectId,
                ServerId = _settings.ServerId,
                ServerUrl = _settings.ServerUrl
            };

            if (!status.IsConfigured)
            {
                status.ErrorMessage = "Central sync not configured. Run 'syncjob central setup' first.";
                return status;
            }

            try
            {
                using var connection = new SqlConnection(_settings.ConnectionString);
                await connection.OpenAsync();

                // Verificar que la base de datos existe
                using var cmd = new SqlCommand("SELECT DB_NAME()", connection);
                var dbName = await cmd.ExecuteScalarAsync() as string;

                if (dbName != "SyncJobCentralDB")
                {
                    status.ErrorMessage = $"Connected to wrong database: {dbName}. Expected: SyncJobCentralDB";
                    return status;
                }

                status.IsConnected = true;

                // Verificar que el proyecto existe
                cmd.CommandText = "SELECT COUNT(*) FROM Projects WHERE ProjectId = @ProjectId";
                cmd.Parameters.Clear();
                cmd.Parameters.AddWithValue("@ProjectId", _settings.ProjectId);

                var projectExists = Convert.ToInt32(await cmd.ExecuteScalarAsync()) > 0;

                if (!projectExists)
                {
                    status.ErrorMessage = $"Project '{_settings.ProjectId}' not found in central database";
                    return status;
                }

                status.IsAuthenticated = true;
                status.LastHeartbeat = DateTime.Now;

                Log.Info("Connection to central server successful", evt: "central.test.success");
            }
            catch (Exception ex)
            {
                status.ErrorMessage = $"Connection failed: {ex.Message}";
                Log.Error($"Failed to connect to central server: {ex.Message}", ex, "central.test.failed");
            }

            return status;
        }

        /// <summary>
        /// Sincroniza una ejecución al servidor central
        /// </summary>
        public async Task<SyncResult> SyncExecutionAsync(ExecutionHistoryEntity execution)
        {
            var sw = Stopwatch.StartNew();
            var result = new SyncResult
            {
                Type = SyncType.Execution,
                SyncedAt = DateTime.Now
            };

            try
            {
                if (!IsEnabled())
                {
                    result.Success = true; // No es error, simplemente no está habilitado
                    result.ErrorMessage = "Central sync is disabled";
                    return result;
                }

                using var connection = new SqlConnection(_settings.ConnectionString);
                await connection.OpenAsync();

                const string sql = @"
                    INSERT INTO ExecutionHistory_Central
                    (ExecutionId, ProjectId, ServerId, ConfigId, ConfigDisplayName, StartTime, EndTime, DurationMs,
                     Status, ExecutionMode, RowsRead, RowsInserted, RowsUpdated, RowsDeleted, RowsSkipped, RowsFailed,
                     ErrorMessage, ErrorStackTrace, HostMachine, TriggeredBy, LogFilePath, SyncedAt)
                    VALUES
                    (@ExecutionId, @ProjectId, @ServerId, @ConfigId, @ConfigDisplayName, @StartTime, @EndTime, @DurationMs,
                     @Status, @ExecutionMode, @RowsRead, @RowsInserted, @RowsUpdated, @RowsDeleted, @RowsSkipped, @RowsFailed,
                     @ErrorMessage, @ErrorStackTrace, @HostMachine, @TriggeredBy, @LogFilePath, GETDATE())";

                using var cmd = new SqlCommand(sql, connection);
                cmd.Parameters.AddWithValue("@ExecutionId", execution.ExecutionId);
                cmd.Parameters.AddWithValue("@ProjectId", _settings.ProjectId);
                cmd.Parameters.AddWithValue("@ServerId", _settings.ServerId);
                cmd.Parameters.AddWithValue("@ConfigId", execution.ConfigId);
                cmd.Parameters.AddWithValue("@ConfigDisplayName", (object?)GetConfigDisplayName(execution.ConfigId) ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@StartTime", execution.StartTime);
                cmd.Parameters.AddWithValue("@EndTime", (object?)execution.EndTime ?? DBNull.Value);
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

                await cmd.ExecuteNonQueryAsync();

                result.Success = true;
                result.RecordsProcessed = 1;

                // Registrar el sync en la tabla de logs del central
                await LogSyncToCentral(connection, SyncType.Execution, true, 1, 0);

                // Actualizar timestamp local
                CentralSyncRepository.UpdateLastSyncTime();

                Log.Info($"Execution {execution.ExecutionId} synced to central", evt: "central.sync.execution.success");
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = ex.Message;
                result.RecordsFailed = 1;

                Log.Error($"Failed to sync execution to central: {ex.Message}", ex, "central.sync.execution.failed");
            }
            finally
            {
                sw.Stop();
                result.DurationMs = sw.ElapsedMilliseconds;
            }

            return result;
        }

        /// <summary>
        /// Sincroniza múltiples ejecuciones (batch)
        /// </summary>
        public async Task<SyncResult> SyncExecutionBatchAsync(List<ExecutionHistoryEntity> executions, int days = 7)
        {
            var sw = Stopwatch.StartNew();
            var result = new SyncResult
            {
                Type = SyncType.Execution,
                SyncedAt = DateTime.Now
            };

            try
            {
                if (!IsEnabled())
                {
                    result.Success = true;
                    result.ErrorMessage = "Central sync is disabled";
                    return result;
                }

                if (executions == null || executions.Count == 0)
                {
                    result.Success = true;
                    result.ErrorMessage = "No executions to sync";
                    return result;
                }

                using var connection = new SqlConnection(_settings.ConnectionString);
                await connection.OpenAsync();

                int processed = 0;
                int failed = 0;

                foreach (var execution in executions)
                {
                    try
                    {
                        const string sql = @"
                            IF NOT EXISTS (SELECT 1 FROM ExecutionHistory_Central WHERE ExecutionId = @ExecutionId)
                            BEGIN
                                INSERT INTO ExecutionHistory_Central
                                (ExecutionId, ProjectId, ServerId, ConfigId, ConfigDisplayName, StartTime, EndTime, DurationMs,
                                 Status, ExecutionMode, RowsRead, RowsInserted, RowsUpdated, RowsDeleted, RowsSkipped, RowsFailed,
                                 ErrorMessage, ErrorStackTrace, HostMachine, TriggeredBy, LogFilePath, SyncedAt)
                                VALUES
                                (@ExecutionId, @ProjectId, @ServerId, @ConfigId, @ConfigDisplayName, @StartTime, @EndTime, @DurationMs,
                                 @Status, @ExecutionMode, @RowsRead, @RowsInserted, @RowsUpdated, @RowsDeleted, @RowsSkipped, @RowsFailed,
                                 @ErrorMessage, @ErrorStackTrace, @HostMachine, @TriggeredBy, @LogFilePath, GETDATE())
                            END";

                        using var cmd = new SqlCommand(sql, connection);
                        cmd.Parameters.AddWithValue("@ExecutionId", execution.ExecutionId);
                        cmd.Parameters.AddWithValue("@ProjectId", _settings.ProjectId);
                        cmd.Parameters.AddWithValue("@ServerId", _settings.ServerId);
                        cmd.Parameters.AddWithValue("@ConfigId", execution.ConfigId);
                        cmd.Parameters.AddWithValue("@ConfigDisplayName", (object?)GetConfigDisplayName(execution.ConfigId) ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@StartTime", execution.StartTime);
                        cmd.Parameters.AddWithValue("@EndTime", (object?)execution.EndTime ?? DBNull.Value);
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

                        await cmd.ExecuteNonQueryAsync();
                        processed++;
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        Log.Warn($"Failed to sync execution {execution.ExecutionId}: {ex.Message}", evt: "central.sync.execution.item.failed");
                    }
                }

                result.Success = failed == 0;
                result.RecordsProcessed = processed;
                result.RecordsFailed = failed;

                // Registrar el sync en la tabla de logs del central
                await LogSyncToCentral(connection, SyncType.Execution, result.Success, processed, failed);

                // Actualizar timestamp local
                CentralSyncRepository.UpdateLastSyncTime();

                Log.Info($"Batch sync completed: {processed} processed, {failed} failed", evt: "central.sync.batch.completed");
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = ex.Message;

                Log.Error($"Failed to sync execution batch: {ex.Message}", ex, "central.sync.batch.failed");
            }
            finally
            {
                sw.Stop();
                result.DurationMs = sw.ElapsedMilliseconds;
            }

            return result;
        }

        /// <summary>
        /// Envía un heartbeat al servidor central
        /// </summary>
        public async Task<SyncResult> SendHeartbeatAsync()
        {
            var sw = Stopwatch.StartNew();
            var result = new SyncResult
            {
                Type = SyncType.Heartbeat,
                SyncedAt = DateTime.Now
            };

            try
            {
                if (!IsEnabled())
                {
                    result.Success = true;
                    result.ErrorMessage = "Central sync is disabled";
                    return result;
                }

                using var connection = new SqlConnection(_settings.ConnectionString);
                await connection.OpenAsync();

                const string sql = @"
                    UPDATE ServerInstances
                    SET LastHeartbeat = GETDATE(),
                        IsOnline = 1,
                        UpdatedAt = GETDATE()
                    WHERE ServerId = @ServerId AND ProjectId = @ProjectId";

                using var cmd = new SqlCommand(sql, connection);
                cmd.Parameters.AddWithValue("@ServerId", _settings.ServerId);
                cmd.Parameters.AddWithValue("@ProjectId", _settings.ProjectId);

                var rowsAffected = await cmd.ExecuteNonQueryAsync();

                if (rowsAffected == 0)
                {
                    // Server instance no existe, crearlo
                    await RegisterServerInstanceAsync(connection);
                }

                result.Success = true;
                result.RecordsProcessed = 1;

                Log.Debug("Heartbeat sent to central", evt: "central.heartbeat.success");
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = ex.Message;

                Log.Warn($"Failed to send heartbeat: {ex.Message}", evt: "central.heartbeat.failed");
            }
            finally
            {
                sw.Stop();
                result.DurationMs = sw.ElapsedMilliseconds;
            }

            return result;
        }

        /// <summary>
        /// Registra la instancia del servidor en el central (primera vez)
        /// </summary>
        private async Task RegisterServerInstanceAsync(SqlConnection connection)
        {
            const string sql = @"
                INSERT INTO ServerInstances
                (ServerId, ProjectId, ServerName, HostMachine, IpAddress, OperatingSystem, SyncJobVersion,
                 IsOnline, LastHeartbeat, RegisteredAt, UpdatedAt)
                VALUES
                (@ServerId, @ProjectId, @ServerName, @HostMachine, @IpAddress, @OperatingSystem, @SyncJobVersion,
                 1, GETDATE(), GETDATE(), GETDATE())";

            using var cmd = new SqlCommand(sql, connection);
            cmd.Parameters.AddWithValue("@ServerId", _settings.ServerId);
            cmd.Parameters.AddWithValue("@ProjectId", _settings.ProjectId);
            cmd.Parameters.AddWithValue("@ServerName", Environment.MachineName);
            cmd.Parameters.AddWithValue("@HostMachine", Environment.MachineName);
            cmd.Parameters.AddWithValue("@IpAddress", GetLocalIpAddress());
            cmd.Parameters.AddWithValue("@OperatingSystem", Environment.OSVersion.ToString());
            cmd.Parameters.AddWithValue("@SyncJobVersion", "1.1.0");

            await cmd.ExecuteNonQueryAsync();

            Log.Info($"Server instance {_settings.ServerId} registered in central", evt: "central.server.registered");
        }

        /// <summary>
        /// Registra un sync en la tabla de logs del central
        /// </summary>
        private async Task LogSyncToCentral(SqlConnection connection, SyncType syncType, bool success, int processed, int failed)
        {
            try
            {
                const string sql = @"
                    INSERT INTO SyncLogs
                    (ProjectId, ServerId, SyncType, Status, RecordsProcessed, RecordsFailed, Timestamp)
                    VALUES
                    (@ProjectId, @ServerId, @SyncType, @Status, @RecordsProcessed, @RecordsFailed, GETDATE())";

                using var cmd = new SqlCommand(sql, connection);
                cmd.Parameters.AddWithValue("@ProjectId", _settings.ProjectId);
                cmd.Parameters.AddWithValue("@ServerId", _settings.ServerId);
                cmd.Parameters.AddWithValue("@SyncType", syncType.ToString());
                cmd.Parameters.AddWithValue("@Status", success ? "Success" : (failed < processed ? "PartialSuccess" : "Failed"));
                cmd.Parameters.AddWithValue("@RecordsProcessed", processed);
                cmd.Parameters.AddWithValue("@RecordsFailed", failed);

                await cmd.ExecuteNonQueryAsync();
            }
            catch (Exception ex)
            {
                Log.Warn($"Failed to log sync to central: {ex.Message}", evt: "central.log.failed");
            }
        }

        /// <summary>
        /// Obtiene el display name de una configuración
        /// </summary>
        private string? GetConfigDisplayName(string configId)
        {
            try
            {
                var config = ConfigRepository.GetById(configId);
                return config?.DisplayName;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Obtiene la IP local del servidor
        /// </summary>
        private string GetLocalIpAddress()
        {
            try
            {
                var host = System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName());
                foreach (var ip in host.AddressList)
                {
                    if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    {
                        return ip.ToString();
                    }
                }
                return "127.0.0.1";
            }
            catch
            {
                return "127.0.0.1";
            }
        }
    }
}
