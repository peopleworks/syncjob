using Microsoft.Data.SqlClient;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;

namespace SyncJob
{
    // ============================================================================
    // CONFIGURACIÓN DE SINCRONIZACIÓN INCREMENTAL
    // ============================================================================

    public class IncrementalConfig
    {
        /// <summary>
        /// Habilita sincronización incremental (solo cambios desde última ejecución)
        /// </summary>
        public bool Enabled { get; set; } = false;

        /// <summary>
        /// Estrategia de tracking de cambios
        /// </summary>
        public TrackingMode Mode { get; set; } = TrackingMode.Timestamp;

        /// <summary>
        /// Columna usada para tracking (ej: "FechaModificacion", "RowVersion")
        /// </summary>
        public string? TrackingColumn { get; set; }

        /// <summary>
        /// Tabla donde se guarda el estado de última sincronización
        /// Default: dbo.SyncJobTracking
        /// </summary>
        public string? TrackingTable { get; set; } = "dbo.SyncJobTracking";

        /// <summary>
        /// Nombre único del job para identificar el tracking (auto: Source.Table -> Dest.Table)
        /// </summary>
        public string? JobIdentifier { get; set; }

        /// <summary>
        /// Configuración para detección de deletes
        /// </summary>
        public DeleteDetectionConfig? DeleteDetection { get; set; }

        /// <summary>
        /// Forzar full refresh en la próxima ejecución (ignora tracking)
        /// </summary>
        public bool ForceFullRefresh { get; set; } = false;

        /// <summary>
        /// Columnas de clave primaria para merge (requerido para updates)
        /// </summary>
        public List<string>? PrimaryKeyColumns { get; set; }

        /// <summary>
        /// Estrategia de merge: Insert (solo nuevos), Upsert (insert + update), Full (insert + update + delete)
        /// </summary>
        public MergeStrategy MergeStrategy { get; set; } = MergeStrategy.Upsert;
    }

    public enum TrackingMode
    {
        /// <summary>
        /// Columna de tipo DateTime/DateTime2 (ej: FechaModificacion)
        /// WHERE FechaModificacion > @LastSyncTime
        /// </summary>
        Timestamp,

        /// <summary>
        /// Columna de tipo ROWVERSION/TIMESTAMP (binario monotónico)
        /// WHERE RowVersion > @LastRowVersion
        /// Más eficiente y confiable que Timestamp
        /// </summary>
        RowVersion,

        /// <summary>
        /// SQL Server Change Tracking (sys.change_tracking_*)
        /// Requiere: ALTER DATABASE SET CHANGE_TRACKING = ON
        /// Detecta INSERT, UPDATE, DELETE automáticamente
        /// </summary>
        ChangeTracking,

        /// <summary>
        /// SQL Server Change Data Capture (CDC)
        /// Más robusto que Change Tracking, guarda valores antiguos
        /// Requiere: Enterprise Edition o Standard 2016 SP1+
        /// </summary>
        ChangeDataCapture
    }

    public enum MergeStrategy
    {
        /// <summary>
        /// Solo INSERT de nuevos registros (ignora updates)
        /// </summary>
        Insert,

        /// <summary>
        /// INSERT nuevos + UPDATE existentes (basado en PK)
        /// </summary>
        Upsert,

        /// <summary>
        /// INSERT + UPDATE + DELETE (sincronización completa)
        /// </summary>
        Full
    }

    public class DeleteDetectionConfig
    {
        /// <summary>
        /// Habilita detección de registros eliminados
        /// </summary>
        public bool Enabled { get; set; } = false;

        /// <summary>
        /// Modo de detección
        /// </summary>
        public DeleteDetectionMode Mode { get; set; } = DeleteDetectionMode.SoftDelete;

        /// <summary>
        /// Para Soft Delete: columna que indica registro eliminado (ej: "IsDeleted", "Estado")
        /// </summary>
        public string? SoftDeleteColumn { get; set; }

        /// <summary>
        /// Para Soft Delete: valor que indica eliminación (ej: "1", "true", "INACTIVO")
        /// </summary>
        public string? SoftDeleteValue { get; set; } = "1";

        /// <summary>
        /// Para Comparison: compara PKs de origen vs destino
        /// Los registros en destino que no existen en origen se eliminan
        /// </summary>
        public bool UseComparison { get; set; } = false;
    }

    public enum DeleteDetectionMode
    {
        /// <summary>
        /// Columna que marca registros como eliminados (ej: IsDeleted = 1)
        /// </summary>
        SoftDelete,

        /// <summary>
        /// SQL Server Change Tracking/CDC detecta DELETEs automáticamente
        /// </summary>
        AutoDetect,

        /// <summary>
        /// Comparación de PKs: registros en destino que no existen en origen
        /// CUIDADO: Puede ser costoso para tablas grandes
        /// </summary>
        Comparison
    }

    // ============================================================================
    // SISTEMA DE TRACKING
    // ============================================================================

    public class SyncTrackingState
    {
        public string JobIdentifier { get; set; } = string.Empty;
        public DateTime LastSyncTime { get; set; }
        public byte[]? LastRowVersion { get; set; }
        public long? LastChangeTrackingVersion { get; set; }
        public long RowsProcessed { get; set; }
        public long RowsInserted { get; set; }
        public long RowsUpdated { get; set; }
        public long RowsDeleted { get; set; }
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }
    }

    public static class IncrementalSyncEngine
    {
        // ========================================================================
        // TABLA DE TRACKING (crear en destino)
        // ========================================================================

        private const string CREATE_TRACKING_TABLE_SQL = @"
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'{0}') AND type = 'U')
BEGIN
    CREATE TABLE {0} (
        JobIdentifier NVARCHAR(255) NOT NULL PRIMARY KEY,
        LastSyncTime DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
        LastRowVersion VARBINARY(8) NULL,
        LastChangeTrackingVersion BIGINT NULL,
        RowsProcessed BIGINT NOT NULL DEFAULT 0,
        RowsInserted BIGINT NOT NULL DEFAULT 0,
        RowsUpdated BIGINT NOT NULL DEFAULT 0,
        RowsDeleted BIGINT NOT NULL DEFAULT 0,
        Success BIT NOT NULL DEFAULT 1,
        ErrorMessage NVARCHAR(MAX) NULL,
        CreatedAt DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
        UpdatedAt DATETIME2 NOT NULL DEFAULT GETUTCDATE()
    );

    CREATE INDEX IX_SyncJobTracking_LastSyncTime ON {0} (LastSyncTime);
END";

        // ========================================================================
        // INICIALIZACIÓN
        // ========================================================================

        /// <summary>
        /// Crea la tabla de tracking si no existe
        /// </summary>
        public static void EnsureTrackingTable(string connectionString, string trackingTable)
        {
            using var conn = new SqlConnection(connectionString);
            conn.Open();

            var sql = string.Format(CREATE_TRACKING_TABLE_SQL, trackingTable);
            using var cmd = new SqlCommand(sql, conn);
            cmd.ExecuteNonQuery();

            Log.Info($"Tracking table ensured: {trackingTable}", evt: "incremental.tracking.ensured");
        }

        // ========================================================================
        // OBTENER ÚLTIMO ESTADO
        // ========================================================================

        /// <summary>
        /// Obtiene el estado de la última sincronización exitosa
        /// </summary>
        public static SyncTrackingState? GetLastSyncState(
            string connectionString,
            string trackingTable,
            string jobIdentifier)
        {
            using var conn = new SqlConnection(connectionString);
            conn.Open();

            var sql = $@"
SELECT TOP 1
    JobIdentifier,
    LastSyncTime,
    LastRowVersion,
    LastChangeTrackingVersion,
    RowsProcessed,
    RowsInserted,
    RowsUpdated,
    RowsDeleted,
    Success,
    ErrorMessage
FROM {trackingTable}
WHERE JobIdentifier = @JobIdentifier
  AND Success = 1
ORDER BY LastSyncTime DESC";

            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@JobIdentifier", jobIdentifier);

            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
            {
                Log.Info($"No previous sync found for job: {jobIdentifier}", evt: "incremental.tracking.notfound");
                return null;
            }

            var state = new SyncTrackingState
            {
                JobIdentifier = reader.GetString(0),
                LastSyncTime = reader.GetDateTime(1),
                LastRowVersion = reader.IsDBNull(2) ? null : (byte[])reader.GetValue(2),
                LastChangeTrackingVersion = reader.IsDBNull(3) ? null : reader.GetInt64(3),
                RowsProcessed = reader.GetInt64(4),
                RowsInserted = reader.GetInt64(5),
                RowsUpdated = reader.GetInt64(6),
                RowsDeleted = reader.GetInt64(7),
                Success = reader.GetBoolean(8),
                ErrorMessage = reader.IsDBNull(9) ? null : reader.GetString(9)
            };

            Log.Info($"Last sync: {state.LastSyncTime:O}, Rows: {state.RowsProcessed}", evt: "incremental.tracking.loaded");
            return state;
        }

        // ========================================================================
        // GUARDAR ESTADO
        // ========================================================================

        /// <summary>
        /// Guarda el estado de la sincronización actual
        /// </summary>
        public static void SaveSyncState(
            string connectionString,
            string trackingTable,
            SyncTrackingState state)
        {
            using var conn = new SqlConnection(connectionString);
            conn.Open();

            var sql = $@"
MERGE {trackingTable} AS target
USING (SELECT @JobIdentifier AS JobIdentifier) AS source
ON target.JobIdentifier = source.JobIdentifier
WHEN MATCHED THEN
    UPDATE SET
        LastSyncTime = @LastSyncTime,
        LastRowVersion = @LastRowVersion,
        LastChangeTrackingVersion = @LastChangeTrackingVersion,
        RowsProcessed = @RowsProcessed,
        RowsInserted = @RowsInserted,
        RowsUpdated = @RowsUpdated,
        RowsDeleted = @RowsDeleted,
        Success = @Success,
        ErrorMessage = @ErrorMessage,
        UpdatedAt = GETUTCDATE()
WHEN NOT MATCHED THEN
    INSERT (JobIdentifier, LastSyncTime, LastRowVersion, LastChangeTrackingVersion,
            RowsProcessed, RowsInserted, RowsUpdated, RowsDeleted, Success, ErrorMessage)
    VALUES (@JobIdentifier, @LastSyncTime, @LastRowVersion, @LastChangeTrackingVersion,
            @RowsProcessed, @RowsInserted, @RowsUpdated, @RowsDeleted, @Success, @ErrorMessage);";

            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@JobIdentifier", state.JobIdentifier);
            cmd.Parameters.AddWithValue("@LastSyncTime", state.LastSyncTime);
            cmd.Parameters.AddWithValue("@LastRowVersion", (object?)state.LastRowVersion ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@LastChangeTrackingVersion", (object?)state.LastChangeTrackingVersion ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@RowsProcessed", state.RowsProcessed);
            cmd.Parameters.AddWithValue("@RowsInserted", state.RowsInserted);
            cmd.Parameters.AddWithValue("@RowsUpdated", state.RowsUpdated);
            cmd.Parameters.AddWithValue("@RowsDeleted", state.RowsDeleted);
            cmd.Parameters.AddWithValue("@Success", state.Success);
            cmd.Parameters.AddWithValue("@ErrorMessage", (object?)state.ErrorMessage ?? DBNull.Value);

            cmd.ExecuteNonQuery();
            Log.Info($"Sync state saved: {state.JobIdentifier}", evt: "incremental.tracking.saved");
        }

        // ========================================================================
        // CONSTRUCCIÓN DE QUERY INCREMENTAL
        // ========================================================================

        /// <summary>
        /// Modifica el query origen para incluir filtro incremental
        /// </summary>
        public static string BuildIncrementalQuery(
            string originalQuery,
            IncrementalConfig config,
            SyncTrackingState? lastState)
        {
            if (!config.Enabled || config.ForceFullRefresh || lastState == null)
            {
                Log.Info("Using full refresh query", evt: "incremental.query.full");
                return originalQuery;
            }

            string whereClause;

            switch (config.Mode)
            {
                case TrackingMode.Timestamp:
                    if (string.IsNullOrWhiteSpace(config.TrackingColumn))
                        throw new ArgumentException("TrackingColumn is required for Timestamp mode");

                    whereClause = $"{config.TrackingColumn} > '{lastState.LastSyncTime:yyyy-MM-dd HH:mm:ss.fffffff}'";
                    Log.Info($"Incremental filter (Timestamp): {whereClause}", evt: "incremental.query.timestamp");
                    break;

                case TrackingMode.RowVersion:
                    if (string.IsNullOrWhiteSpace(config.TrackingColumn))
                        throw new ArgumentException("TrackingColumn is required for RowVersion mode");
                    if (lastState.LastRowVersion == null)
                        throw new InvalidOperationException("LastRowVersion is null, cannot do incremental sync");

                    var hexVersion = BitConverter.ToString(lastState.LastRowVersion).Replace("-", "");
                    whereClause = $"{config.TrackingColumn} > 0x{hexVersion}";
                    Log.Info($"Incremental filter (RowVersion): {whereClause}", evt: "incremental.query.rowversion");
                    break;

                case TrackingMode.ChangeTracking:
                    // SQL Server Change Tracking usa sintaxis especial
                    throw new NotImplementedException("Change Tracking mode requires special query builder (coming soon)");

                case TrackingMode.ChangeDataCapture:
                    // CDC usa funciones especiales
                    throw new NotImplementedException("CDC mode requires special query builder (coming soon)");

                default:
                    throw new ArgumentException($"Unknown tracking mode: {config.Mode}");
            }

            // Detectar si el query original tiene WHERE
            var upperQuery = originalQuery.ToUpperInvariant();
            bool hasWhere = upperQuery.Contains("WHERE");

            string incrementalQuery;
            if (hasWhere)
            {
                // Inyectar condición en WHERE existente
                incrementalQuery = $@"
SELECT * FROM (
    {originalQuery}
) AS _base_
WHERE {whereClause}";
            }
            else
            {
                // Agregar WHERE
                incrementalQuery = $@"
SELECT * FROM (
    {originalQuery}
) AS _base_
WHERE {whereClause}";
            }

            return incrementalQuery;
        }

        // ========================================================================
        // MERGE (UPSERT)
        // ========================================================================

        /// <summary>
        /// Ejecuta MERGE (INSERT + UPDATE) desde Stage a Final
        /// </summary>
        public static (long inserted, long updated) ExecuteMerge(
            SqlConnection conn,
            SqlTransaction tran,
            string stageTable,
            string finalTable,
            List<string> primaryKeys,
            List<string> allColumns)
        {
            if (primaryKeys == null || primaryKeys.Count == 0)
                throw new ArgumentException("PrimaryKeyColumns is required for MERGE operation");

            // Construir ON clause (join por PKs)
            var onClause = string.Join(" AND ",
                primaryKeys.Select(pk => $"target.[{pk}] = source.[{pk}]"));

            // Construir UPDATE SET clause (todas las columnas excepto PKs)
            var updateColumns = allColumns.Except(primaryKeys, StringComparer.OrdinalIgnoreCase).ToList();
            var updateSet = string.Join(", ",
                updateColumns.Select(col => $"target.[{col}] = source.[{col}]"));

            // Construir INSERT columns y values
            var insertColumns = string.Join(", ", allColumns.Select(c => $"[{c}]"));
            var insertValues = string.Join(", ", allColumns.Select(c => $"source.[{c}]"));

            var mergeSql = $@"
MERGE {finalTable} AS target
USING {stageTable} AS source
ON {onClause}
WHEN MATCHED THEN
    UPDATE SET {updateSet}
WHEN NOT MATCHED BY TARGET THEN
    INSERT ({insertColumns})
    VALUES ({insertValues})
OUTPUT $action;";

            using var cmd = new SqlCommand(mergeSql, conn, tran);
            cmd.CommandTimeout = 0;

            long inserted = 0, updated = 0;

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var action = reader.GetString(0);
                if (action == "INSERT") inserted++;
                else if (action == "UPDATE") updated++;
            }

            Log.Info($"MERGE completed: {inserted} inserted, {updated} updated", evt: "incremental.merge.done");
            return (inserted, updated);
        }

        // ========================================================================
        // DELETE DETECTION
        // ========================================================================

        /// <summary>
        /// Detecta y elimina registros del destino según configuración
        /// </summary>
        public static long ExecuteDeleteDetection(
            SqlConnection sourceConn,
            SqlConnection destConn,
            SqlTransaction destTran,
            string sourceQuery,
            string finalTable,
            List<string> primaryKeys,
            DeleteDetectionConfig deleteConfig)
        {
            if (!deleteConfig.Enabled)
                return 0;

            long deletedCount = 0;

            switch (deleteConfig.Mode)
            {
                case DeleteDetectionMode.SoftDelete:
                    deletedCount = ExecuteSoftDeleteDetection(
                        sourceConn, destConn, destTran, sourceQuery, finalTable,
                        primaryKeys, deleteConfig);
                    break;

                case DeleteDetectionMode.Comparison:
                    deletedCount = ExecuteComparisonDeleteDetection(
                        sourceConn, destConn, destTran, sourceQuery, finalTable, primaryKeys);
                    break;

                case DeleteDetectionMode.AutoDetect:
                    throw new NotImplementedException("AutoDetect mode requires Change Tracking/CDC");

                default:
                    throw new ArgumentException($"Unknown delete detection mode: {deleteConfig.Mode}");
            }

            Log.Info($"Delete detection: {deletedCount} rows deleted", evt: "incremental.delete.done");
            return deletedCount;
        }

        private static long ExecuteSoftDeleteDetection(
            SqlConnection sourceConn,
            SqlConnection destConn,
            SqlTransaction destTran,
            string sourceQuery,
            string finalTable,
            List<string> primaryKeys,
            DeleteDetectionConfig deleteConfig)
        {
            if (string.IsNullOrWhiteSpace(deleteConfig.SoftDeleteColumn))
                throw new ArgumentException("SoftDeleteColumn is required for SoftDelete mode");

            // Leer PKs de registros eliminados del origen
            var deletedPKs = new List<Dictionary<string, object>>();

            var deletedQuery = $@"
SELECT {string.Join(", ", primaryKeys.Select(pk => $"[{pk}]"))}
FROM ({sourceQuery}) AS _src_
WHERE [{deleteConfig.SoftDeleteColumn}] = {deleteConfig.SoftDeleteValue}";

            using (var cmd = new SqlCommand(deletedQuery, sourceConn))
            {
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var pkValues = new Dictionary<string, object>();
                    for (int i = 0; i < primaryKeys.Count; i++)
                    {
                        pkValues[primaryKeys[i]] = reader.GetValue(i);
                    }
                    deletedPKs.Add(pkValues);
                }
            }

            if (deletedPKs.Count == 0)
                return 0;

            // Eliminar del destino
            // TODO: Optimizar con tabla temporal para grandes volúmenes
            long deleted = 0;
            foreach (var pkDict in deletedPKs)
            {
                var whereClause = string.Join(" AND ",
                    pkDict.Select(kv => $"[{kv.Key}] = @{kv.Key}"));

                var deleteSql = $"DELETE FROM {finalTable} WHERE {whereClause}";
                using var cmd = new SqlCommand(deleteSql, destConn, destTran);
                foreach (var kv in pkDict)
                {
                    cmd.Parameters.AddWithValue($"@{kv.Key}", kv.Value);
                }
                deleted += cmd.ExecuteNonQuery();
            }

            return deleted;
        }

        private static long ExecuteComparisonDeleteDetection(
            SqlConnection sourceConn,
            SqlConnection destConn,
            SqlTransaction destTran,
            string sourceQuery,
            string finalTable,
            List<string> primaryKeys)
        {
            // Crear tabla temporal con PKs del origen
            var tempTable = $"#SourcePKs_{Guid.NewGuid():N}";
            var pkColumns = string.Join(", ", primaryKeys.Select(pk => $"[{pk}] NVARCHAR(255)"));

            using (var cmd = new SqlCommand($"CREATE TABLE {tempTable} ({pkColumns})", destConn, destTran))
            {
                cmd.ExecuteNonQuery();
            }

            // Insertar PKs del origen en temp table
            var pkSelectList = string.Join(", ", primaryKeys.Select(pk => $"CAST([{pk}] AS NVARCHAR(255))"));
            var pkQuery = $"SELECT {pkSelectList} FROM ({sourceQuery}) AS _src_";

            using (var sourceCmd = new SqlCommand(pkQuery, sourceConn))
            using (var reader = sourceCmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    var values = new object[primaryKeys.Count];
                    reader.GetValues(values);

                    var insertSql = $"INSERT INTO {tempTable} VALUES ({string.Join(",", Enumerable.Range(0, primaryKeys.Count).Select(i => $"@p{i}"))})";
                    using var insertCmd = new SqlCommand(insertSql, destConn, destTran);
                    for (int i = 0; i < primaryKeys.Count; i++)
                    {
                        insertCmd.Parameters.AddWithValue($"@p{i}", values[i]);
                    }
                    insertCmd.ExecuteNonQuery();
                }
            }

            // Eliminar registros que no existen en origen
            var joinClause = string.Join(" AND ",
                primaryKeys.Select(pk => $"dest.[{pk}] = tmp.[{pk}]"));

            var deleteSql = $@"
DELETE dest
FROM {finalTable} dest
LEFT JOIN {tempTable} tmp ON {joinClause}
WHERE tmp.[{primaryKeys[0]}] IS NULL";

            using var deleteCmd = new SqlCommand(deleteSql, destConn, destTran);
            return deleteCmd.ExecuteNonQuery();
        }

        // ========================================================================
        // HELPERS
        // ========================================================================

        /// <summary>
        /// Genera JobIdentifier automático basado en config
        /// </summary>
        public static string GenerateJobIdentifier(SyncConfig cfg)
        {
            var srcBuilder = new SqlConnectionStringBuilder(cfg.Source!.ConnectionString!);
            var dstBuilder = new SqlConnectionStringBuilder(cfg.Destination!.ConnectionString!);

            var srcObject = TryGetSourceObjectName(cfg);
            var dstTable = cfg.Destination.FinalTable ?? "Unknown";

            return $"{srcBuilder.DataSource}_{srcBuilder.InitialCatalog}_{srcObject}_TO_{dstBuilder.DataSource}_{dstBuilder.InitialCatalog}_{dstTable}"
                .Replace("[", "").Replace("]", "").Replace(".", "_");
        }

        private static string TryGetSourceObjectName(SyncConfig cfg)
        {
            if (!string.IsNullOrWhiteSpace(cfg.Source?.StoredProcedure))
                return cfg.Source.StoredProcedure!.Replace("[", "").Replace("]", "");

            if (!string.IsNullOrWhiteSpace(cfg.Source?.Query))
            {
                // Heurística simple para detectar tabla/vista base
                var match = System.Text.RegularExpressions.Regex.Match(
                    cfg.Source.Query,
                    @"\bFROM\s+([\[\]A-Za-z0-9_\.]+)",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                if (match.Success)
                    return match.Groups[1].Value.Replace("[", "").Replace("]", "");
            }

            return "UnknownSource";
        }

        /// <summary>
        /// Extrae el máximo valor de tracking column del dataset
        /// </summary>
        public static object? ExtractMaxTrackingValue(
            SourceDataPackage data,
            string trackingColumn,
            TrackingMode mode)
        {
            var colIndex = Array.FindIndex(data.SourceColumnNames,
                c => c.Equals(trackingColumn, StringComparison.OrdinalIgnoreCase));

            if (colIndex == -1)
                throw new ArgumentException($"Tracking column '{trackingColumn}' not found in source data");

            if (data.Rows.Count == 0)
                return null;

            switch (mode)
            {
                case TrackingMode.Timestamp:
                    return data.Rows
                        .Select(r => r[colIndex])
                        .Where(v => v != null && v != DBNull.Value)
                        .Cast<DateTime>()
                        .DefaultIfEmpty(DateTime.MinValue)
                        .Max();

                case TrackingMode.RowVersion:
                    return data.Rows
                        .Select(r => r[colIndex])
                        .Where(v => v != null && v != DBNull.Value)
                        .Cast<byte[]>()
                        .OrderByDescending(b => BitConverter.ToUInt64(b.Reverse().ToArray(), 0))
                        .FirstOrDefault();

                default:
                    return null;
            }
        }
    }
}
