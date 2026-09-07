using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using SyncJob.Database;
using SyncJob.Services.Models;
using System.Data;
using System.Diagnostics;

namespace SyncJob.Services
{
    /// <summary>
    /// Ejecutor de tareas de sincronización
    /// </summary>
    public class SyncTaskExecutor
    {
        private readonly ILogger<SyncTaskExecutor> _logger;

        public SyncTaskExecutor(ILogger<SyncTaskExecutor> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Ejecuta una tarea de sincronización
        /// </summary>
        public async Task<SyncTaskExecutionResult> ExecuteTaskAsync(SyncTaskEntity task)
        {
            var executionId = Guid.NewGuid();
            var stopwatch = Stopwatch.StartNew();

            var result = new SyncTaskExecutionResult
            {
                ExecutionId = executionId,
                Success = false
            };

            try
            {
                _logger.LogInformation("Starting execution of task {TaskId} (Type: {TaskType})",
                    task.TaskId, task.TaskType);

                // Obtener configuración desde SQLite
                var config = ConfigRepository.GetById(task.ConfigId ?? task.ProjectId);
                if (config == null)
                {
                    throw new Exception($"Configuration '{task.ConfigId ?? task.ProjectId}' not found");
                }

                var mappings = ColumnMappingRepository.GetByConfigId(config.ConfigId);
                if (mappings.Count == 0)
                {
                    throw new Exception($"No column mappings found for '{config.ConfigId}'");
                }

                var sourceConn = ConnectionRepository.GetById(config.SourceConnectionId);
                if (sourceConn == null)
                {
                    throw new Exception($"Source connection '{config.SourceConnectionId}' not found");
                }

                var destConn = ConnectionRepository.GetById(config.DestConnectionId);
                if (destConn == null)
                {
                    throw new Exception($"Destination connection '{config.DestConnectionId}' not found");
                }

                // Convertir a SyncConfig
                var syncConfig = ConvertToSyncConfig(config, mappings, sourceConn, destConn);

                // Ejecutar sincronización
                var execResult = await ExecuteSyncAsync(syncConfig);

                // Guardar en historial de ejecución local
                var execution = new ExecutionHistoryEntity
                {
                    ExecutionId = executionId.ToString("N"),
                    ConfigId = config.ConfigId,
                    StartTime = DateTime.UtcNow.AddMilliseconds(-stopwatch.ElapsedMilliseconds),
                    EndTime = DateTime.UtcNow,
                    DurationMs = stopwatch.ElapsedMilliseconds,
                    Status = "Success",
                    ExecutionMode = "OnDemand",
                    RowsRead = execResult.RowsRead,
                    RowsInserted = execResult.RowsInserted,
                    RowsUpdated = execResult.RowsUpdated,
                    RowsDeleted = execResult.RowsDeleted,
                    HostMachine = Environment.MachineName,
                    TriggeredBy = task.RequestedBy ?? "SyncJobWorker"
                };
                ExecutionHistoryRepository.Create(execution);

                // Preparar resultado
                result.Success = true;
                result.DurationMs = stopwatch.ElapsedMilliseconds;
                result.RowsProcessed = execResult.RowsRead;
                result.RowsInserted = execResult.RowsInserted;
                result.RowsUpdated = execResult.RowsUpdated;
                result.RowsDeleted = execResult.RowsDeleted;
                result.RowsFailed = 0;

                _logger.LogInformation(
                    "Task {TaskId} completed successfully in {DurationMs}ms. Rows: {RowsProcessed}",
                    task.TaskId, result.DurationMs, result.RowsProcessed);
            }
            catch (Exception ex)
            {
                stopwatch.Stop();

                result.Success = false;
                result.DurationMs = stopwatch.ElapsedMilliseconds;
                result.ErrorMessage = ex.Message;
                result.ErrorStackTrace = ex.StackTrace;

                _logger.LogError(ex, "Task {TaskId} failed: {ErrorMessage}", task.TaskId, ex.Message);
            }

            return result;
        }

        /// <summary>
        /// Convierte ConfigurationEntity a SyncConfig (formato legacy)
        /// </summary>
        private SyncConfig ConvertToSyncConfig(
            ConfigurationEntity config,
            List<ColumnMappingEntity> mappings,
            ConnectionEntity sourceConn,
            ConnectionEntity destConn)
        {
            var sourceCs = BuildConnectionString(sourceConn);
            var destCs = BuildConnectionString(destConn);

            var syncConfig = new SyncConfig
            {
                Source = new SourceConfig
                {
                    ConnectionString = sourceCs,
                    Query = config.SourceQuery,
                    StoredProcedure = config.SourceStoredProc,
                    Parameters = null // TODO: Parse from config.SourceParameters if needed
                },
                Destination = new DestConfig
                {
                    ConnectionString = destCs,
                    StageTable = config.DestStageTable,
                    FinalTable = config.DestFinalTable
                },
                ColumnMappings = mappings.Select(m => new ColumnMap
                {
                    Source = m.SourceColumn,
                    Dest = m.DestColumn
                }).ToList(),
                Options = new SyncOptions
                {
                    BatchSize = config.BatchSize,
                    MaxDegreeOfParallelism = config.MaxDOP,
                    BulkCopyTimeoutSeconds = config.BulkCopyTimeout,
                    KeepIdentity = config.KeepIdentity,
                    MinRowThresholdToCommit = config.MinRowThreshold
                }
            };

            // Configuración incremental si aplica
            if (config.TrackingMode != "None" && !string.IsNullOrWhiteSpace(config.TrackingColumn))
            {
                var trackingMode = Enum.Parse<TrackingMode>(config.TrackingMode, ignoreCase: true);
                var mergeStrategy = Enum.Parse<MergeStrategy>(config.MergeStrategy, ignoreCase: true);

                syncConfig.Incremental = new IncrementalConfig
                {
                    Enabled = true,
                    Mode = trackingMode,
                    TrackingColumn = config.TrackingColumn,
                    TrackingTable = "dbo.SyncJobTracking",
                    JobIdentifier = config.ConfigId,
                    MergeStrategy = mergeStrategy,
                    ForceFullRefresh = false,
                    PrimaryKeyColumns = mappings
                        .Where(m => m.IsPrimaryKey)
                        .Select(m => m.DestColumn)
                        .ToList()
                };
            }

            return syncConfig;
        }

        /// <summary>
        /// Construye connection string desde ConnectionEntity
        /// </summary>
        private string BuildConnectionString(ConnectionEntity conn)
        {
            // Intentar desencriptar connection string completo
            if (conn.ConnectionStringEncrypted != null && conn.ConnectionStringEncrypted.Length > 0)
            {
                try
                {
                    return System.Text.Encoding.UTF8.GetString(
                        System.Security.Cryptography.ProtectedData.Unprotect(
                            conn.ConnectionStringEncrypted,
                            null,
                            System.Security.Cryptography.DataProtectionScope.CurrentUser));
                }
                catch { }
            }

            // Construir manualmente
            var builder = new SqlConnectionStringBuilder
            {
                DataSource = conn.ServerName,
                InitialCatalog = conn.DatabaseName,
                TrustServerCertificate = conn.TrustServerCertificate,
                Encrypt = conn.Encrypt
            };

            if (!string.IsNullOrWhiteSpace(conn.Username))
            {
                builder.UserID = conn.Username;

                if (conn.PasswordEncrypted != null && conn.PasswordEncrypted.Length > 0)
                {
                    try
                    {
                        var password = System.Text.Encoding.UTF8.GetString(
                            System.Security.Cryptography.ProtectedData.Unprotect(
                                conn.PasswordEncrypted,
                                null,
                                System.Security.Cryptography.DataProtectionScope.CurrentUser));
                        builder.Password = password;
                    }
                    catch
                    {
                        throw new Exception($"Failed to decrypt password for connection '{conn.ConnectionId}'");
                    }
                }
            }
            else
            {
                builder.IntegratedSecurity = true;
            }

            return builder.ToString();
        }

        /// <summary>
        /// Ejecuta la sincronización (lógica simplificada de RunCommands)
        /// </summary>
        private async Task<InternalExecutionResult> ExecuteSyncAsync(SyncConfig config)
        {
            var result = new InternalExecutionResult();

            // Test de conectividad
            await Task.Run(() => TestConnectivity(config));
            _logger.LogDebug("Connectivity test passed");

            // Leer datos del origen
            var data = await Task.Run(() => ReadSourceData(config));
            result.RowsRead = data.Rows.Count;
            _logger.LogInformation("Read {RowCount} rows from source", data.Rows.Count);

            if (data.Rows.Count == 0)
            {
                _logger.LogWarning("No data to sync");
                return result;
            }

            // Escribir a destino (modo stage)
            if (!string.IsNullOrWhiteSpace(config.Destination?.StageTable))
            {
                await Task.Run(() => LoadStageInParallel(config, data));
                _logger.LogInformation("Stage loaded with {RowCount} rows", data.Rows.Count);

                await Task.Run(() => CommitStageToFinal(config, data.Rows.Count));
                result.RowsInserted = data.Rows.Count;
                _logger.LogInformation("Stage committed to final table");
            }
            else
            {
                // Modo directo
                await Task.Run(() => LoadFinalDirect(config, data));
                result.RowsInserted = data.Rows.Count;
                _logger.LogInformation("Loaded {RowCount} rows directly to final table", data.Rows.Count);
            }

            return result;
        }

        // ========================================================================
        // HELPERS (simplificados de RunCommands.cs)
        // ========================================================================

        private void TestConnectivity(SyncConfig cfg)
        {
            using (var c1 = new SqlConnection(cfg.Source!.ConnectionString))
            {
                c1.Open();
            }
            using (var c2 = new SqlConnection(cfg.Destination!.ConnectionString))
            {
                c2.Open();
            }
        }

        private SourceDataPackage ReadSourceData(SyncConfig cfg)
        {
            var package = new SourceDataPackage { Rows = new List<object[]>(200000) };

            using var conn = new SqlConnection(cfg.Source!.ConnectionString);
            conn.Open();

            using var cmd = !string.IsNullOrWhiteSpace(cfg.Source.Query)
                ? new SqlCommand(cfg.Source.Query, conn) { CommandType = CommandType.Text }
                : new SqlCommand(cfg.Source.StoredProcedure!, conn) { CommandType = CommandType.StoredProcedure };

            if (cmd.CommandType == CommandType.StoredProcedure && cfg.Source.Parameters != null)
            {
                foreach (var kvp in cfg.Source.Parameters)
                {
                    var paramName = kvp.Key.StartsWith("@") ? kvp.Key : "@" + kvp.Key;
                    cmd.Parameters.AddWithValue(paramName, kvp.Value ?? string.Empty);
                }
            }

            using var reader = cmd.ExecuteReader(CommandBehavior.SequentialAccess);

            int fieldCount = reader.FieldCount;
            var colNames = new string[fieldCount];
            for (int i = 0; i < fieldCount; i++)
                colNames[i] = reader.GetName(i);
            package.SourceColumnNames = colNames;

            while (reader.Read())
            {
                var values = new object[fieldCount];
                reader.GetValues(values);
                package.Rows.Add(values);
            }

            return package;
        }

        private void LoadStageInParallel(SyncConfig cfg, SourceDataPackage data)
        {
            // Truncar stage
            using (var conn = new SqlConnection(cfg.Destination!.ConnectionString))
            {
                conn.Open();
                using var cmd = new SqlCommand($"TRUNCATE TABLE {cfg.Destination.StageTable};", conn);
                cmd.ExecuteNonQuery();
            }

            var stageSchema = GetTableSchema(cfg.Destination!.ConnectionString!, cfg.Destination.StageTable!);
            var destToSourceIndex = BuildDestToSourceIndex(cfg, data.SourceColumnNames);
            var batches = SplitIntoBatches(data.Rows, cfg.Options!.BatchSize);

            var bulkOptions = cfg.Options.KeepIdentity
                ? SqlBulkCopyOptions.KeepIdentity | SqlBulkCopyOptions.TableLock
                : SqlBulkCopyOptions.TableLock;

            Parallel.ForEach(
                batches,
                new ParallelOptions { MaxDegreeOfParallelism = cfg.Options.MaxDegreeOfParallelism },
                batch =>
                {
                    using var conn = new SqlConnection(cfg.Destination!.ConnectionString);
                    conn.Open();

                    var dt = BuildDataTableForBatch(stageSchema, destToSourceIndex, batch);

                    using var bulk = new SqlBulkCopy(conn, bulkOptions, null)
                    {
                        DestinationTableName = cfg.Destination.StageTable,
                        BulkCopyTimeout = cfg.Options.BulkCopyTimeoutSeconds,
                        BatchSize = cfg.Options.BatchSize
                    };

                    foreach (var map in cfg.ColumnMappings!)
                    {
                        if (!string.IsNullOrWhiteSpace(map.Dest))
                            bulk.ColumnMappings.Add(map.Dest, map.Dest);
                    }

                    bulk.WriteToServer(dt);
                });
        }

        private void LoadFinalDirect(SyncConfig cfg, SourceDataPackage data)
        {
            // Truncar final
            using (var conn = new SqlConnection(cfg.Destination!.ConnectionString))
            {
                conn.Open();
                using var cmd = new SqlCommand($"TRUNCATE TABLE {cfg.Destination.FinalTable};", conn);
                cmd.ExecuteNonQuery();
            }

            var finalSchema = GetTableSchema(cfg.Destination!.ConnectionString!, cfg.Destination.FinalTable!);
            var destToSourceIndex = BuildDestToSourceIndex(cfg, data.SourceColumnNames);
            var batches = SplitIntoBatches(data.Rows, cfg.Options!.BatchSize);

            var bulkOptions = cfg.Options.KeepIdentity
                ? SqlBulkCopyOptions.KeepIdentity | SqlBulkCopyOptions.TableLock
                : SqlBulkCopyOptions.TableLock;

            Parallel.ForEach(
                batches,
                new ParallelOptions { MaxDegreeOfParallelism = cfg.Options.MaxDegreeOfParallelism },
                batch =>
                {
                    using var conn = new SqlConnection(cfg.Destination!.ConnectionString);
                    conn.Open();

                    var dt = BuildDataTableForBatch(finalSchema, destToSourceIndex, batch);

                    using var bulk = new SqlBulkCopy(conn, bulkOptions, null)
                    {
                        DestinationTableName = cfg.Destination.FinalTable,
                        BulkCopyTimeout = cfg.Options.BulkCopyTimeoutSeconds,
                        BatchSize = cfg.Options.BatchSize
                    };

                    foreach (var map in cfg.ColumnMappings!)
                    {
                        if (!string.IsNullOrWhiteSpace(map.Dest))
                            bulk.ColumnMappings.Add(map.Dest, map.Dest);
                    }

                    bulk.WriteToServer(dt);
                });
        }

        private void CommitStageToFinal(SyncConfig cfg, int rowCount)
        {
            using var conn = new SqlConnection(cfg.Destination!.ConnectionString);
            conn.Open();
            using var tran = conn.BeginTransaction();

            try
            {
                int stageCount;
                using (var cmd = new SqlCommand($"SELECT COUNT(*) FROM {cfg.Destination.StageTable};", conn, tran))
                {
                    stageCount = (int)cmd.ExecuteScalar()!;
                }

                if (stageCount != rowCount)
                    throw new Exception($"Rowcount mismatch: stage={stageCount} vs read={rowCount}");

                var swapSql = $"TRUNCATE TABLE {cfg.Destination.FinalTable}; INSERT INTO {cfg.Destination.FinalTable} SELECT * FROM {cfg.Destination.StageTable};";

                using (var cmd = new SqlCommand(swapSql, conn, tran))
                {
                    cmd.ExecuteNonQuery();
                }

                tran.Commit();
            }
            catch
            {
                try { tran.Rollback(); } catch { }
                throw;
            }
        }

        private DataTable GetTableSchema(string connectionString, string tableName)
        {
            using var conn = new SqlConnection(connectionString);
            conn.Open();
            using var cmd = new SqlCommand($"SELECT TOP 0 * FROM {tableName};", conn);
            using var adapter = new SqlDataAdapter(cmd);
            var dt = new DataTable();
            adapter.Fill(dt);
            return dt;
        }

        private Dictionary<string, int> BuildDestToSourceIndex(SyncConfig cfg, string[] sourceColumns)
        {
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in cfg.ColumnMappings!)
            {
                int idx = Array.FindIndex(sourceColumns, c => c.Equals(m.Source, StringComparison.OrdinalIgnoreCase));
                if (idx == -1)
                    throw new Exception($"Source column '{m.Source}' not found");
                map[m.Dest] = idx;
            }
            return map;
        }

        private IEnumerable<List<object[]>> SplitIntoBatches(List<object[]> rows, int batchSize)
        {
            for (int i = 0; i < rows.Count; i += batchSize)
                yield return rows.GetRange(i, Math.Min(batchSize, rows.Count - i));
        }

        private DataTable BuildDataTableForBatch(
            DataTable schema,
            Dictionary<string, int> destToSourceIndex,
            List<object[]> rows)
        {
            var dt = schema.Clone();
            dt.BeginLoadData();
            foreach (var rowValues in rows)
            {
                var newRow = dt.NewRow();
                foreach (DataColumn col in dt.Columns)
                {
                    if (destToSourceIndex.TryGetValue(col.ColumnName, out int srcIdx))
                    {
                        newRow[col.ColumnName] = rowValues[srcIdx] ?? DBNull.Value;
                    }
                }
                dt.Rows.Add(newRow);
            }
            dt.EndLoadData();
            return dt;
        }

        private class InternalExecutionResult
        {
            public long RowsRead { get; set; }
            public long RowsInserted { get; set; }
            public long RowsUpdated { get; set; }
            public long RowsDeleted { get; set; }
        }
    }
}
