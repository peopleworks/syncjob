using Microsoft.Data.SqlClient;
using Spectre.Console;
using Spectre.Console.Cli;
using SyncJob.Database;
using SyncJob.Services;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;

namespace SyncJob.Commands
{
    // ============================================================================
    // RUN COMMAND - Ejecutar sincronización desde configuración SQLite
    // ============================================================================

    public class RunFromDbSettings : CommandSettings
    {
        [Description("ID de la configuración a ejecutar")]
        [CommandArgument(0, "<CONFIG_ID>")]
        public string ConfigId { get; set; } = string.Empty;

        [Description("Base de datos SQLite (default: syncjob.db)")]
        [CommandOption("--db <PATH>")]
        public string DbPath { get; set; } = "syncjob.db";

        [Description("Modo dry-run (no escribe en destino)")]
        [CommandOption("--dry-run")]
        public bool DryRun { get; set; }

        [Description("Modo directo (sin stage, directo a Final)")]
        [CommandOption("--direct")]
        public bool Direct { get; set; }

        [Description("Append mode (no trunca tabla final)")]
        [CommandOption("--append")]
        public bool Append { get; set; }

        [Description("Forzar full refresh (ignorar tracking incremental)")]
        [CommandOption("--full-refresh")]
        public bool FullRefresh { get; set; }

        [Description("Limitar filas a leer del origen (testing)")]
        [CommandOption("--top <N>")]
        public int? Top { get; set; }

        [Description("Override de BatchSize")]
        [CommandOption("--batch-size <N>")]
        public int? BatchSize { get; set; }

        [Description("Override de MaxDOP")]
        [CommandOption("--maxdop <N>")]
        public int? MaxDop { get; set; }

        [Description("Nivel de log (Trace, Debug, Info, Warn, Error, Fatal)")]
        [CommandOption("--log-level <LEVEL>")]
        public string? LogLevel { get; set; }

        [Description("Ruta de archivo de log")]
        [CommandOption("--log-file <PATH>")]
        public string? LogFile { get; set; }

        [Description("Salida de logs en formato JSON")]
        [CommandOption("--json-log")]
        public bool JsonLog { get; set; }

        [Description("No imprimir logs a consola")]
        [CommandOption("--quiet")]
        public bool Quiet { get; set; }

        public override ValidationResult Validate()
        {
            if (string.IsNullOrWhiteSpace(ConfigId))
                return ValidationResult.Error("CONFIG_ID es requerido");
            return ValidationResult.Success();
        }
    }

    public class RunFromDbCommand : Command<RunFromDbSettings>
    {
        public override int Execute(CommandContext context, RunFromDbSettings settings)
        {
            try
            {
                // Inicializar logging
                InitLogging(settings);

                AnsiConsole.MarkupLine($"[cyan]═══════════════════════════════════════════════════[/]");
                AnsiConsole.MarkupLine($"[cyan bold]  Ejecutando sincronización: {settings.ConfigId}[/]");
                AnsiConsole.MarkupLine($"[cyan]═══════════════════════════════════════════════════[/]");

                // Obtener configuración desde SQLite
                ConfigurationEntity? config = null;
                List<ColumnMappingEntity> mappings = new();
                ConnectionEntity? sourceConn = null;
                ConnectionEntity? destConn = null;

                AnsiConsole.Status().Start("Cargando configuración desde SQLite...", ctx =>
                {
                    DbManager.DbPath = settings.DbPath;
                    DbManager.Initialize();

                    config = ConfigRepository.GetById(settings.ConfigId);
                    if (config == null)
                        throw new Exception($"Configuración '{settings.ConfigId}' no encontrada");

                    mappings = ColumnMappingRepository.GetByConfigId(settings.ConfigId);
                    if (mappings.Count == 0)
                        throw new Exception($"No hay column mappings para '{settings.ConfigId}'");

                    sourceConn = ConnectionRepository.GetById(config.SourceConnectionId);
                    if (sourceConn == null)
                        throw new Exception($"Conexión origen '{config.SourceConnectionId}' no encontrada");

                    destConn = ConnectionRepository.GetById(config.DestConnectionId);
                    if (destConn == null)
                        throw new Exception($"Conexión destino '{config.DestConnectionId}' no encontrada");
                });

                if (config == null || mappings == null || sourceConn == null || destConn == null)
                    return 1;

                // Mostrar resumen
                ShowConfigSummary(config, mappings, sourceConn, destConn, settings);

                // Convertir a SyncConfig (formato legacy)
                var syncConfig = ConvertToSyncConfig(config, mappings, sourceConn, destConn, settings);

                // Validar configuración
                ValidateConfig(syncConfig, settings.Direct);

                // Crear registro de ejecución
                string executionId = Guid.NewGuid().ToString("N");
                DateTime startTime = DateTime.UtcNow;
                var stopwatch = Stopwatch.StartNew();

                var execution = new ExecutionHistoryEntity
                {
                    ExecutionId = executionId,
                    ConfigId = settings.ConfigId,
                    StartTime = startTime,
                    Status = "Running",
                    ExecutionMode = settings.DryRun ? "DryRun" : (settings.FullRefresh ? "Full" : "Incremental"),
                    HostMachine = Environment.MachineName,
                    TriggeredBy = Environment.UserName
                };
                ExecutionHistoryRepository.Create(execution);

                try
                {
                    // Ejecutar sincronización
                    var result = ExecuteSync(syncConfig, settings);

                    stopwatch.Stop();

                    // Actualizar ejecución con resultados
                    execution.EndTime = DateTime.UtcNow;
                    execution.DurationMs = stopwatch.ElapsedMilliseconds;
                    execution.Status = "Success";
                    execution.RowsRead = result.RowsRead;
                    execution.RowsInserted = result.RowsInserted;
                    execution.RowsUpdated = result.RowsUpdated;
                    execution.RowsDeleted = result.RowsDeleted;
                    ExecutionHistoryRepository.Update(execution);

                    // Sincronizar automáticamente al central si está habilitado
                    TrySyncToCentral(execution);

                    // Mostrar resumen final
                    ShowExecutionSummary(execution, result);

                    AnsiConsole.MarkupLine($"[green]✓ Sincronización completada exitosamente[/]");
                    Log.Info($"Execution completed: {executionId}", evt: "run.success");
                    return 0;
                }
                catch (Exception ex)
                {
                    stopwatch.Stop();

                    // Actualizar ejecución con error
                    execution.EndTime = DateTime.UtcNow;
                    execution.DurationMs = stopwatch.ElapsedMilliseconds;
                    execution.Status = "Failed";
                    execution.ErrorMessage = ex.Message;
                    execution.ErrorStackTrace = ex.StackTrace;
                    ExecutionHistoryRepository.Update(execution);

                    // Sincronizar automáticamente al central (incluso si falló)
                    TrySyncToCentral(execution);

                    AnsiConsole.WriteException(ex, ExceptionFormats.ShortenPaths | ExceptionFormats.ShortenTypes);
                    Log.Error($"Execution failed: {executionId}", ex, evt: "run.error");
                    return 1;
                }
            }
            catch (Exception ex)
            {
                AnsiConsole.WriteException(ex, ExceptionFormats.ShortenPaths | ExceptionFormats.ShortenTypes);
                Log.Error("Run command failed", ex, evt: "run.fatal");
                return 1;
            }
            finally
            {
                Log.Shutdown();
            }
        }

        private static void InitLogging(RunFromDbSettings settings)
        {
            var minLevel = ParseLogLevel(settings.LogLevel) ?? Log.Level.Info;
            var opts = new Log.Options
            {
                EnableConsole = !settings.Quiet,
                JsonFormat = settings.JsonLog,
                FilePath = settings.LogFile,
                DirectoryPath = "logs",
                MinLevel = minLevel,
                AppName = "SyncJob"
            };
            Log.Init(opts);
        }

        private static Log.Level? ParseLogLevel(string? level)
        {
            if (string.IsNullOrWhiteSpace(level)) return null;
            return level.ToLowerInvariant() switch
            {
                "trace" => Log.Level.Trace,
                "debug" => Log.Level.Debug,
                "info" => Log.Level.Info,
                "warn" or "warning" => Log.Level.Warn,
                "error" => Log.Level.Error,
                "fatal" or "critical" => Log.Level.Fatal,
                _ => null
            };
        }

        private static void ShowConfigSummary(
            ConfigurationEntity config,
            List<ColumnMappingEntity> mappings,
            ConnectionEntity sourceConn,
            ConnectionEntity destConn,
            RunFromDbSettings settings)
        {
            var table = new Table().Border(TableBorder.Rounded).Title("[bold]Configuración[/]");
            table.AddColumn("Propiedad");
            table.AddColumn("Valor");

            table.AddRow("Config ID", config.ConfigId);
            table.AddRow("Nombre", config.DisplayName);
            if (!string.IsNullOrWhiteSpace(config.Description))
                table.AddRow("Descripción", config.Description);

            table.AddRow("[cyan]Origen[/]", $"{sourceConn.ServerName} / {sourceConn.DatabaseName}");
            table.AddRow("[cyan]Destino[/]", $"{destConn.ServerName} / {destConn.DatabaseName}");

            if (!string.IsNullOrWhiteSpace(config.DestStageTable))
                table.AddRow("Stage Table", config.DestStageTable);
            table.AddRow("Final Table", config.DestFinalTable);

            table.AddRow("Column Mappings", mappings.Count.ToString());
            table.AddRow("Batch Size", (settings.BatchSize ?? config.BatchSize).ToString());
            table.AddRow("Max DOP", (settings.MaxDop ?? config.MaxDOP).ToString());
            table.AddRow("Tracking Mode", config.TrackingMode);
            table.AddRow("Merge Strategy", config.MergeStrategy);

            if (settings.DryRun)
                table.AddRow("[yellow]Modo[/]", "[yellow]DRY RUN[/]");
            if (settings.Direct)
                table.AddRow("[yellow]Modo[/]", "[yellow]DIRECTO (sin stage)[/]");
            if (settings.Append)
                table.AddRow("[yellow]Modo[/]", "[yellow]APPEND (no trunca)[/]");
            if (settings.FullRefresh)
                table.AddRow("[yellow]Modo[/]", "[yellow]FULL REFRESH[/]");

            AnsiConsole.Write(table);
        }

        private static SyncConfig ConvertToSyncConfig(
            ConfigurationEntity config,
            List<ColumnMappingEntity> mappings,
            ConnectionEntity sourceConn,
            ConnectionEntity destConn,
            RunFromDbSettings settings)
        {
            // Construir connection strings
            var sourceCs = BuildConnectionString(sourceConn);
            var destCs = BuildConnectionString(destConn);

            // Parsear parámetros de source
            Dictionary<string, string>? sourceParams = null;
            if (!string.IsNullOrWhiteSpace(config.SourceParameters))
            {
                try
                {
                    sourceParams = JsonSerializer.Deserialize<Dictionary<string, string>>(config.SourceParameters);
                }
                catch { }
            }

            var syncConfig = new SyncConfig
            {
                Source = new SourceConfig
                {
                    ConnectionString = sourceCs,
                    Query = config.SourceQuery,
                    StoredProcedure = config.SourceStoredProc,
                    Parameters = sourceParams
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
                    BatchSize = settings.BatchSize ?? config.BatchSize,
                    MaxDegreeOfParallelism = settings.MaxDop ?? config.MaxDOP,
                    BulkCopyTimeoutSeconds = config.BulkCopyTimeout,
                    KeepIdentity = config.KeepIdentity,
                    MinRowThresholdToCommit = config.MinRowThreshold
                }
            };

            // Configuración incremental (si aplica)
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
                    ForceFullRefresh = settings.FullRefresh,
                    PrimaryKeyColumns = mappings
                        .Where(m => m.IsPrimaryKey)
                        .Select(m => m.DestColumn)
                        .ToList()
                };
            }

            // Override de TOP
            if (settings.Top.HasValue && settings.Top.Value > 0 && !string.IsNullOrWhiteSpace(syncConfig.Source.Query))
            {
                syncConfig.Source.Query = $"SELECT TOP {settings.Top.Value} * FROM ({syncConfig.Source.Query}) AS _src_";
            }

            return syncConfig;
        }

        private static string BuildConnectionString(ConnectionEntity conn)
        {
            // Desencriptar connection string si está guardado completo
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
                catch
                {
                    // Fallback a construir manualmente
                }
            }

            // Construir connection string manualmente
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

                // Desencriptar password
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
                        throw new Exception($"No se pudo desencriptar la contraseña de la conexión '{conn.ConnectionId}'");
                    }
                }
            }
            else
            {
                builder.IntegratedSecurity = true;
            }

            return builder.ToString();
        }

        private static void ValidateConfig(SyncConfig cfg, bool directMode)
        {
            if (cfg.Source == null || string.IsNullOrWhiteSpace(cfg.Source.ConnectionString))
                throw new ArgumentException("Source config inválido");
            if (cfg.Destination == null || string.IsNullOrWhiteSpace(cfg.Destination.ConnectionString))
                throw new ArgumentException("Destination config inválido");
            if (cfg.Options == null)
                throw new ArgumentException("Options config inválido");
            if (cfg.ColumnMappings == null || cfg.ColumnMappings.Count == 0)
                throw new ArgumentException("ColumnMappings vacío");

            if (string.IsNullOrWhiteSpace(cfg.Source.Query) && string.IsNullOrWhiteSpace(cfg.Source.StoredProcedure))
                throw new ArgumentException("Source.Query o Source.StoredProcedure requerido");

            if (!directMode && string.IsNullOrWhiteSpace(cfg.Destination.StageTable))
                throw new ArgumentException("Destination.StageTable requerido (o use --direct)");
        }

        private static ExecutionResult ExecuteSync(SyncConfig config, RunFromDbSettings settings)
        {
            var result = new ExecutionResult();

            // Test de conectividad
            AnsiConsole.Status().Start("Probando conexiones...", ctx =>
            {
                TestConnectivity(config);
            });
            AnsiConsole.MarkupLine("[green]✓[/] Conexiones OK");

            // Leer datos del origen
            SourceDataPackage data = new();
            AnsiConsole.Status().Start("Leyendo datos del origen...", ctx =>
            {
                data = ReadSourceData(config);
            });
            result.RowsRead = data.Rows.Count;
            AnsiConsole.MarkupLine($"[cyan]→[/] Leídas [bold]{data.Rows.Count:N0}[/] filas del origen");
            Log.Info($"Source rows read: {data.Rows.Count}", evt: "run.source.read");

            if (data.Rows.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]⚠[/] No hay datos para sincronizar");
                return result;
            }

            if (settings.DryRun)
            {
                AnsiConsole.MarkupLine("[yellow]ℹ[/] Modo DRY RUN - no se escribirá en destino");
                return result;
            }

            // Escribir a destino
            if (settings.Direct)
            {
                AnsiConsole.Status().Start("Escribiendo directamente a tabla Final...", ctx =>
                {
                    LoadFinalDirect(config, data, settings.Append);
                });
                result.RowsInserted = data.Rows.Count;
                AnsiConsole.MarkupLine($"[green]✓[/] Escritas [bold]{data.Rows.Count:N0}[/] filas a tabla Final");
            }
            else
            {
                AnsiConsole.Status().Start("Cargando Stage en paralelo...", ctx =>
                {
                    LoadStageInParallel(config, data);
                });
                AnsiConsole.MarkupLine($"[green]✓[/] Stage cargado con [bold]{data.Rows.Count:N0}[/] filas");

                AnsiConsole.Status().Start("Commit Stage → Final...", ctx =>
                {
                    CommitStageToFinal(config, data.Rows.Count, settings.Append);
                });
                result.RowsInserted = data.Rows.Count;
                AnsiConsole.MarkupLine($"[green]✓[/] Commit completado");
            }

            return result;
        }

        private static void ShowExecutionSummary(ExecutionHistoryEntity execution, ExecutionResult result)
        {
            var panel = new Panel(
                new Table()
                    .Border(TableBorder.None)
                    .AddColumn("Métrica")
                    .AddColumn("Valor")
                    .AddRow("Duración", $"{execution.DurationMs:N0} ms")
                    .AddRow("Filas leídas", $"{result.RowsRead:N0}")
                    .AddRow("Filas insertadas", $"{result.RowsInserted:N0}")
                    .AddRow("Filas actualizadas", $"{result.RowsUpdated:N0}")
                    .AddRow("Filas eliminadas", $"{result.RowsDeleted:N0}")
            )
            .Header("[bold green]Resumen de Ejecución[/]")
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Green);

            AnsiConsole.Write(panel);
        }

        // ========================================================================
        // HELPERS (copiados de Program.cs - idealmente refactorizar a clase común)
        // ========================================================================

        private static void TestConnectivity(SyncConfig cfg)
        {
            using (var c1 = new SqlConnection(cfg.Source!.ConnectionString))
            {
                c1.Open();
                Log.Info($"Source connection OK", evt: "source.connect.ok");
            }
            using (var c2 = new SqlConnection(cfg.Destination!.ConnectionString))
            {
                c2.Open();
                Log.Info($"Dest connection OK", evt: "dest.connect.ok");
            }
        }

        private static SourceDataPackage ReadSourceData(SyncConfig cfg)
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

        private static void LoadStageInParallel(SyncConfig cfg, SourceDataPackage data)
        {
            using (var conn = new SqlConnection(cfg.Destination!.ConnectionString))
            {
                conn.Open();
                using var cmd = new SqlCommand($"TRUNCATE TABLE {cfg.Destination.StageTable};", conn);
                cmd.ExecuteNonQuery();
            }

            var stageSchema = GetStageSchema(cfg);
            var destToSourceIndex = BuildDestToSourceIndex(cfg, data.SourceColumnNames);
            var batches = SplitIntoBatches(data.Rows, cfg.Options!.BatchSize);

            var bulkOptions = cfg.Options.KeepIdentity
                ? SqlBulkCopyOptions.KeepIdentity | SqlBulkCopyOptions.TableLock
                : SqlBulkCopyOptions.TableLock;

            System.Threading.Tasks.Parallel.ForEach(
                batches,
                new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = cfg.Options.MaxDegreeOfParallelism },
                batch =>
                {
                    using var conn = new SqlConnection(cfg.Destination.ConnectionString);
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

        private static void LoadFinalDirect(SyncConfig cfg, SourceDataPackage data, bool append)
        {
            using (var conn = new SqlConnection(cfg.Destination!.ConnectionString))
            {
                conn.Open();
                if (!append)
                {
                    using var cmd = new SqlCommand($"TRUNCATE TABLE {cfg.Destination.FinalTable};", conn);
                    cmd.ExecuteNonQuery();
                }
            }

            var finalSchema = GetFinalSchema(cfg);
            var destToSourceIndex = BuildDestToSourceIndex(cfg, data.SourceColumnNames);
            var batches = SplitIntoBatches(data.Rows, cfg.Options!.BatchSize);

            var bulkOptions = cfg.Options.KeepIdentity
                ? SqlBulkCopyOptions.KeepIdentity | SqlBulkCopyOptions.TableLock
                : SqlBulkCopyOptions.TableLock;

            System.Threading.Tasks.Parallel.ForEach(
                batches,
                new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = cfg.Options.MaxDegreeOfParallelism },
                batch =>
                {
                    using var conn = new SqlConnection(cfg.Destination.ConnectionString);
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

        private static void CommitStageToFinal(SyncConfig cfg, int rowCount, bool append)
        {
            using var conn = new SqlConnection(cfg.Destination!.ConnectionString);
            conn.Open();
            using var tran = conn.BeginTransaction();

            try
            {
                int stageCount;
                using (var cmd = new SqlCommand($"SELECT COUNT(*) FROM {cfg.Destination.StageTable};", conn, tran))
                {
                    stageCount = (int)cmd.ExecuteScalar();
                }

                if (stageCount != rowCount)
                    throw new Exception($"Rowcount mismatch: stage={stageCount} vs read={rowCount}");

                string swapSql = append
                    ? $"INSERT INTO {cfg.Destination.FinalTable} SELECT * FROM {cfg.Destination.StageTable};"
                    : $"TRUNCATE TABLE {cfg.Destination.FinalTable}; INSERT INTO {cfg.Destination.FinalTable} SELECT * FROM {cfg.Destination.StageTable};";

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

        private static DataTable GetStageSchema(SyncConfig cfg)
        {
            using var conn = new SqlConnection(cfg.Destination!.ConnectionString);
            conn.Open();
            using var cmd = new SqlCommand($"SELECT TOP 0 * FROM {cfg.Destination.StageTable};", conn);
            using var adapter = new SqlDataAdapter(cmd);
            var dt = new DataTable();
            adapter.Fill(dt);
            return dt;
        }

        private static DataTable GetFinalSchema(SyncConfig cfg)
        {
            using var conn = new SqlConnection(cfg.Destination!.ConnectionString);
            conn.Open();
            using var cmd = new SqlCommand($"SELECT TOP 0 * FROM {cfg.Destination.FinalTable};", conn);
            using var adapter = new SqlDataAdapter(cmd);
            var dt = new DataTable();
            adapter.Fill(dt);
            return dt;
        }

        private static Dictionary<string, int> BuildDestToSourceIndex(SyncConfig cfg, string[] sourceColumns)
        {
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in cfg.ColumnMappings!)
            {
                int idx = Array.FindIndex(sourceColumns, c => c.Equals(m.Source, StringComparison.OrdinalIgnoreCase));
                if (idx == -1)
                    throw new Exception($"Columna origen '{m.Source}' no encontrada");
                map[m.Dest] = idx;
            }
            return map;
        }

        private static IEnumerable<List<object[]>> SplitIntoBatches(List<object[]> rows, int batchSize)
        {
            for (int i = 0; i < rows.Count; i += batchSize)
                yield return rows.GetRange(i, Math.Min(batchSize, rows.Count - i));
        }

        private static DataTable BuildDataTableForBatch(
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

        /// <summary>
        /// Intenta sincronizar la ejecución al servidor central (sin fallar si hay error)
        /// </summary>
        private static void TrySyncToCentral(ExecutionHistoryEntity execution)
        {
            try
            {
                var settings = CentralSyncRepository.GetSettings();

                // Verificar si el sync central está habilitado y configurado
                if (!settings.Enabled || !CentralSyncRepository.IsConfigured())
                {
                    Log.Debug("Central sync is disabled or not configured", evt: "central.sync.skipped");
                    return;
                }

                // Verificar el modo de sincronización
                if (settings.SyncMode != "AfterEveryExecution")
                {
                    Log.Debug($"Central sync mode is '{settings.SyncMode}', skipping auto-sync", evt: "central.sync.skipped");
                    return;
                }

                // Realizar la sincronización
                Log.Info("Syncing execution to central server...", evt: "central.sync.start");

                var service = new CentralSyncService(settings);
                var syncTask = service.SyncExecutionAsync(execution);
                syncTask.Wait(); // Sincronización sincrónica para simplicidad
                var result = syncTask.Result;

                if (result.Success)
                {
                    Log.Info($"Execution synced to central successfully (Duration: {result.DurationMs}ms)", evt: "central.sync.success");
                }
                else
                {
                    Log.Warn($"Failed to sync to central: {result.ErrorMessage}", evt: "central.sync.failed");
                }
            }
            catch (Exception ex)
            {
                // No fallar la ejecución principal si el sync central falla
                Log.Warn($"Error during central sync (non-critical): {ex.Message}", evt: "central.sync.error");
            }
        }
    }

    // ============================================================================
    // HELPER CLASSES
    // ============================================================================

    internal class ExecutionResult
    {
        public long RowsRead { get; set; }
        public long RowsInserted { get; set; }
        public long RowsUpdated { get; set; }
        public long RowsDeleted { get; set; }
    }
}
