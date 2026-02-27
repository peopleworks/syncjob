using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Text.Json;

namespace SyncJob.Database
{
    /// <summary>
    /// Repositorio para gestionar configuraciones
    /// </summary>
    public class ConfigRepository
    {
        /// <summary>
        /// Crea una nueva configuración
        /// </summary>
        public static void Create(ConfigurationEntity config)
        {
            using var conn = DbManager.GetConnection();
            conn.Open();

            const string sql = @"
INSERT INTO Configurations (
    ConfigId, DisplayName, Description, SourceConnectionId, SourceQuery, SourceStoredProc,
    SourceParameters, DestConnectionId, DestStageTable, DestFinalTable,
    BatchSize, MaxDOP, BulkCopyTimeout, KeepIdentity, MinRowThreshold,
    IsActive, TrackingMode, TrackingColumn, MergeStrategy, CreatedBy, Tags
) VALUES (
    @ConfigId, @DisplayName, @Description, @SourceConnectionId, @SourceQuery, @SourceStoredProc,
    @SourceParameters, @DestConnectionId, @DestStageTable, @DestFinalTable,
    @BatchSize, @MaxDOP, @BulkCopyTimeout, @KeepIdentity, @MinRowThreshold,
    @IsActive, @TrackingMode, @TrackingColumn, @MergeStrategy, @CreatedBy, @Tags
)";

            using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.AddWithValue("@ConfigId", config.ConfigId);
            cmd.Parameters.AddWithValue("@DisplayName", config.DisplayName);
            cmd.Parameters.AddWithValue("@Description", (object?)config.Description ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@SourceConnectionId", config.SourceConnectionId);
            cmd.Parameters.AddWithValue("@SourceQuery", (object?)config.SourceQuery ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@SourceStoredProc", (object?)config.SourceStoredProc ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@SourceParameters", (object?)config.SourceParameters ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@DestConnectionId", config.DestConnectionId);
            cmd.Parameters.AddWithValue("@DestStageTable", (object?)config.DestStageTable ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@DestFinalTable", config.DestFinalTable);
            cmd.Parameters.AddWithValue("@BatchSize", config.BatchSize);
            cmd.Parameters.AddWithValue("@MaxDOP", config.MaxDOP);
            cmd.Parameters.AddWithValue("@BulkCopyTimeout", config.BulkCopyTimeout);
            cmd.Parameters.AddWithValue("@KeepIdentity", config.KeepIdentity ? 1 : 0);
            cmd.Parameters.AddWithValue("@MinRowThreshold", config.MinRowThreshold);
            cmd.Parameters.AddWithValue("@IsActive", config.IsActive ? 1 : 0);
            cmd.Parameters.AddWithValue("@TrackingMode", config.TrackingMode);
            cmd.Parameters.AddWithValue("@TrackingColumn", (object?)config.TrackingColumn ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@MergeStrategy", config.MergeStrategy);
            cmd.Parameters.AddWithValue("@CreatedBy", (object?)config.CreatedBy ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Tags", (object?)config.Tags ?? DBNull.Value);

            cmd.ExecuteNonQuery();

            // Insertar column mappings
            foreach (var mapping in config.ColumnMappings)
            {
                mapping.ConfigId = config.ConfigId;
                ColumnMappingRepository.Create(mapping, conn);
            }

            Log.Info($"Configuration created: {config.ConfigId}", evt: "config.created");
        }

        /// <summary>
        /// Obtiene una configuración por ID
        /// </summary>
        public static ConfigurationEntity? GetById(string configId)
        {
            using var conn = DbManager.GetConnection();
            conn.Open();

            const string sql = "SELECT * FROM Configurations WHERE ConfigId = @ConfigId";
            using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.AddWithValue("@ConfigId", configId);

            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return null;

            var config = MapFromReader(reader);

            // Cargar mappings
            config.ColumnMappings = ColumnMappingRepository.GetByConfigId(configId, conn);

            return config;
        }

        /// <summary>
        /// Lista todas las configuraciones
        /// </summary>
        public static List<ConfigurationListItem> ListAll(bool? activeOnly = null)
        {
            using var conn = DbManager.GetConnection();
            conn.Open();

            var sql = @"
SELECT
    c.*,
    (SELECT COUNT(*) FROM ColumnMappings WHERE ConfigId = c.ConfigId) AS MappingsCount,
    (SELECT StartTime FROM ExecutionHistory WHERE ConfigId = c.ConfigId ORDER BY StartTime DESC LIMIT 1) AS LastExecution,
    (SELECT Status FROM ExecutionHistory WHERE ConfigId = c.ConfigId ORDER BY StartTime DESC LIMIT 1) AS LastExecutionStatus
FROM Configurations c";

            if (activeOnly.HasValue)
                sql += activeOnly.Value ? " WHERE c.IsActive = 1" : " WHERE c.IsActive = 0";

            sql += " ORDER BY c.UpdatedAt DESC";

            using var cmd = new SqliteCommand(sql, conn);
            var list = new List<ConfigurationListItem>();

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(new ConfigurationListItem
                {
                    ConfigId = reader.GetString(0),
                    DisplayName = reader.GetString(1),
                    Description = reader.IsDBNull(2) ? null : reader.GetString(2),
                    IsActive = reader.GetInt32(15) == 1,
                    TrackingMode = reader.GetString(16),
                    UpdatedAt = DateTime.Parse(reader.GetString(20)),
                    MappingsCount = reader.GetInt32(reader.FieldCount - 3),
                    LastExecution = reader.IsDBNull(reader.FieldCount - 2) ? null : DateTime.Parse(reader.GetString(reader.FieldCount - 2)),
                    LastExecutionStatus = reader.IsDBNull(reader.FieldCount - 1) ? null : reader.GetString(reader.FieldCount - 1)
                });
            }

            return list;
        }

        /// <summary>
        /// Actualiza una configuración
        /// </summary>
        public static void Update(ConfigurationEntity config)
        {
            using var conn = DbManager.GetConnection();
            conn.Open();

            const string sql = @"
UPDATE Configurations SET
    DisplayName = @DisplayName,
    Description = @Description,
    SourceConnectionId = @SourceConnectionId,
    SourceQuery = @SourceQuery,
    SourceStoredProc = @SourceStoredProc,
    SourceParameters = @SourceParameters,
    DestConnectionId = @DestConnectionId,
    DestStageTable = @DestStageTable,
    DestFinalTable = @DestFinalTable,
    BatchSize = @BatchSize,
    MaxDOP = @MaxDOP,
    BulkCopyTimeout = @BulkCopyTimeout,
    KeepIdentity = @KeepIdentity,
    MinRowThreshold = @MinRowThreshold,
    IsActive = @IsActive,
    TrackingMode = @TrackingMode,
    TrackingColumn = @TrackingColumn,
    MergeStrategy = @MergeStrategy,
    Tags = @Tags,
    UpdatedAt = datetime('now')
WHERE ConfigId = @ConfigId";

            using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.AddWithValue("@ConfigId", config.ConfigId);
            cmd.Parameters.AddWithValue("@DisplayName", config.DisplayName);
            cmd.Parameters.AddWithValue("@Description", (object?)config.Description ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@SourceConnectionId", config.SourceConnectionId);
            cmd.Parameters.AddWithValue("@SourceQuery", (object?)config.SourceQuery ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@SourceStoredProc", (object?)config.SourceStoredProc ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@SourceParameters", (object?)config.SourceParameters ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@DestConnectionId", config.DestConnectionId);
            cmd.Parameters.AddWithValue("@DestStageTable", (object?)config.DestStageTable ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@DestFinalTable", config.DestFinalTable);
            cmd.Parameters.AddWithValue("@BatchSize", config.BatchSize);
            cmd.Parameters.AddWithValue("@MaxDOP", config.MaxDOP);
            cmd.Parameters.AddWithValue("@BulkCopyTimeout", config.BulkCopyTimeout);
            cmd.Parameters.AddWithValue("@KeepIdentity", config.KeepIdentity ? 1 : 0);
            cmd.Parameters.AddWithValue("@MinRowThreshold", config.MinRowThreshold);
            cmd.Parameters.AddWithValue("@IsActive", config.IsActive ? 1 : 0);
            cmd.Parameters.AddWithValue("@TrackingMode", config.TrackingMode);
            cmd.Parameters.AddWithValue("@TrackingColumn", (object?)config.TrackingColumn ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@MergeStrategy", config.MergeStrategy);
            cmd.Parameters.AddWithValue("@Tags", (object?)config.Tags ?? DBNull.Value);

            cmd.ExecuteNonQuery();

            Log.Info($"Configuration updated: {config.ConfigId}", evt: "config.updated");
        }

        /// <summary>
        /// Elimina una configuración
        /// </summary>
        public static void Delete(string configId)
        {
            using var conn = DbManager.GetConnection();
            conn.Open();

            // Los mappings se eliminan automáticamente por FK CASCADE
            const string sql = "DELETE FROM Configurations WHERE ConfigId = @ConfigId";
            using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.AddWithValue("@ConfigId", configId);
            cmd.ExecuteNonQuery();

            Log.Info($"Configuration deleted: {configId}", evt: "config.deleted");
        }

        /// <summary>
        /// Verifica si existe una configuración
        /// </summary>
        public static bool Exists(string configId)
        {
            using var conn = DbManager.GetConnection();
            conn.Open();

            const string sql = "SELECT COUNT(*) FROM Configurations WHERE ConfigId = @ConfigId";
            using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.AddWithValue("@ConfigId", configId);

            return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
        }

        // Helper: Mapear desde SqliteDataReader
        private static ConfigurationEntity MapFromReader(SqliteDataReader reader)
        {
            return new ConfigurationEntity
            {
                ConfigId = reader.GetString(0),
                DisplayName = reader.GetString(1),
                Description = reader.IsDBNull(2) ? null : reader.GetString(2),
                SourceConnectionId = reader.GetString(3),
                SourceQuery = reader.IsDBNull(4) ? null : reader.GetString(4),
                SourceStoredProc = reader.IsDBNull(5) ? null : reader.GetString(5),
                SourceParameters = reader.IsDBNull(6) ? null : reader.GetString(6),
                DestConnectionId = reader.GetString(7),
                DestStageTable = reader.IsDBNull(8) ? null : reader.GetString(8),
                DestFinalTable = reader.GetString(9),
                BatchSize = reader.GetInt32(10),
                MaxDOP = reader.GetInt32(11),
                BulkCopyTimeout = reader.GetInt32(12),
                KeepIdentity = reader.GetInt32(13) == 1,
                MinRowThreshold = reader.GetInt32(14),
                IsActive = reader.GetInt32(15) == 1,
                TrackingMode = reader.GetString(16),
                TrackingColumn = reader.IsDBNull(17) ? null : reader.GetString(17),
                MergeStrategy = reader.GetString(18),
                CreatedAt = DateTime.Parse(reader.GetString(19)),
                UpdatedAt = DateTime.Parse(reader.GetString(20)),
                CreatedBy = reader.IsDBNull(21) ? null : reader.GetString(21),
                Tags = reader.IsDBNull(22) ? null : reader.GetString(22)
            };
        }
    }

    /// <summary>
    /// Repositorio para Column Mappings
    /// </summary>
    public class ColumnMappingRepository
    {
        public static void Create(ColumnMappingEntity mapping, SqliteConnection? conn = null)
        {
            var ownConnection = conn == null;
            if (ownConnection)
            {
                conn = DbManager.GetConnection();
                conn.Open();
            }

            try
            {
                const string sql = @"
INSERT INTO ColumnMappings (ConfigId, SourceColumn, DestColumn, IsPrimaryKey, TransformType, TransformExpression, Ordinal)
VALUES (@ConfigId, @SourceColumn, @DestColumn, @IsPrimaryKey, @TransformType, @TransformExpression, @Ordinal)";

                using var cmd = new SqliteCommand(sql, conn);
                cmd.Parameters.AddWithValue("@ConfigId", mapping.ConfigId);
                cmd.Parameters.AddWithValue("@SourceColumn", mapping.SourceColumn);
                cmd.Parameters.AddWithValue("@DestColumn", mapping.DestColumn);
                cmd.Parameters.AddWithValue("@IsPrimaryKey", mapping.IsPrimaryKey ? 1 : 0);
                cmd.Parameters.AddWithValue("@TransformType", (object?)mapping.TransformType ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@TransformExpression", (object?)mapping.TransformExpression ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Ordinal", (object?)mapping.Ordinal ?? DBNull.Value);

                cmd.ExecuteNonQuery();
            }
            finally
            {
                if (ownConnection)
                    conn?.Dispose();
            }
        }

        public static List<ColumnMappingEntity> GetByConfigId(string configId, SqliteConnection? conn = null)
        {
            var ownConnection = conn == null;
            if (ownConnection)
            {
                conn = DbManager.GetConnection();
                conn.Open();
            }

            try
            {
                const string sql = "SELECT * FROM ColumnMappings WHERE ConfigId = @ConfigId ORDER BY Ordinal, MappingId";
                using var cmd = new SqliteCommand(sql, conn);
                cmd.Parameters.AddWithValue("@ConfigId", configId);

                var list = new List<ColumnMappingEntity>();
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    list.Add(new ColumnMappingEntity
                    {
                        MappingId = reader.GetInt32(0),
                        ConfigId = reader.GetString(1),
                        SourceColumn = reader.GetString(2),
                        DestColumn = reader.GetString(3),
                        IsPrimaryKey = reader.GetInt32(4) == 1,
                        TransformType = reader.IsDBNull(5) ? null : reader.GetString(5),
                        TransformExpression = reader.IsDBNull(6) ? null : reader.GetString(6),
                        Ordinal = reader.IsDBNull(7) ? null : reader.GetInt32(7),
                        CreatedAt = DateTime.Parse(reader.GetString(8))
                    });
                }

                return list;
            }
            finally
            {
                if (ownConnection)
                    conn?.Dispose();
            }
        }

        public static void DeleteByConfigId(string configId)
        {
            using var conn = DbManager.GetConnection();
            conn.Open();

            const string sql = "DELETE FROM ColumnMappings WHERE ConfigId = @ConfigId";
            using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.AddWithValue("@ConfigId", configId);
            cmd.ExecuteNonQuery();
        }

        public static void Delete(int mappingId)
        {
            using var conn = DbManager.GetConnection();
            conn.Open();

            const string sql = "DELETE FROM ColumnMappings WHERE MappingId = @MappingId";
            using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.AddWithValue("@MappingId", mappingId);
            cmd.ExecuteNonQuery();
        }

        public static int CountByConfigId(string configId)
        {
            using var conn = DbManager.GetConnection();
            conn.Open();

            const string sql = "SELECT COUNT(*) FROM ColumnMappings WHERE ConfigId = @ConfigId";
            using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.AddWithValue("@ConfigId", configId);

            return Convert.ToInt32(cmd.ExecuteScalar());
        }

        public static List<ColumnMappingEntity> GetPrimaryKeys(string configId)
        {
            using var conn = DbManager.GetConnection();
            conn.Open();

            const string sql = "SELECT * FROM ColumnMappings WHERE ConfigId = @ConfigId AND IsPrimaryKey = 1 ORDER BY Ordinal, MappingId";
            using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.AddWithValue("@ConfigId", configId);

            var list = new List<ColumnMappingEntity>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(new ColumnMappingEntity
                {
                    MappingId = reader.GetInt32(0),
                    ConfigId = reader.GetString(1),
                    SourceColumn = reader.GetString(2),
                    DestColumn = reader.GetString(3),
                    IsPrimaryKey = true,
                    TransformType = reader.IsDBNull(5) ? null : reader.GetString(5),
                    TransformExpression = reader.IsDBNull(6) ? null : reader.GetString(6),
                    Ordinal = reader.IsDBNull(7) ? null : reader.GetInt32(7),
                    CreatedAt = DateTime.Parse(reader.GetString(8))
                });
            }

            return list;
        }

        public static bool ExistsBySourceColumn(string configId, string sourceColumn)
        {
            using var conn = DbManager.GetConnection();
            conn.Open();

            const string sql = "SELECT COUNT(*) FROM ColumnMappings WHERE ConfigId = @ConfigId AND SourceColumn = @SourceColumn";
            using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.AddWithValue("@ConfigId", configId);
            cmd.Parameters.AddWithValue("@SourceColumn", sourceColumn);

            return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
        }
    }
}
