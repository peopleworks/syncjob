using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace SyncJob.Database
{
    /// <summary>
    /// Repositorio para gestionar snapshots (tracking sin modificar origen)
    /// </summary>
    public static class SnapshotRepository
    {
        /// <summary>
        /// Guardar un snapshot completo
        /// </summary>
        public static void SaveSnapshot(string configId, Dictionary<string, string> snapshot)
        {
            using var conn = DbManager.GetConnection();
            conn.Open();

            using var transaction = conn.BeginTransaction();
            try
            {
                // Primero, eliminar snapshot anterior
                DeleteSnapshot(configId, conn, transaction);

                // Insertar nuevo snapshot
                const string sql = @"
                    INSERT INTO SnapshotData (ConfigId, RowKey, RowHash)
                    VALUES (@ConfigId, @RowKey, @RowHash)";

                using var cmd = new SqliteCommand(sql, conn, transaction);
                cmd.Parameters.Add("@ConfigId", SqliteType.Text);
                cmd.Parameters.Add("@RowKey", SqliteType.Text);
                cmd.Parameters.Add("@RowHash", SqliteType.Text);

                foreach (var kvp in snapshot)
                {
                    cmd.Parameters["@ConfigId"].Value = configId;
                    cmd.Parameters["@RowKey"].Value = kvp.Key;
                    cmd.Parameters["@RowHash"].Value = kvp.Value;
                    cmd.ExecuteNonQuery();
                }

                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }

        /// <summary>
        /// Obtener el snapshot actual de una configuración
        /// </summary>
        public static Dictionary<string, string> GetSnapshot(string configId)
        {
            var snapshot = new Dictionary<string, string>();

            using var conn = DbManager.GetConnection();
            conn.Open();

            const string sql = @"
                SELECT RowKey, RowHash
                FROM SnapshotData
                WHERE ConfigId = @ConfigId";

            using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.AddWithValue("@ConfigId", configId);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                snapshot[reader.GetString(0)] = reader.GetString(1);
            }

            return snapshot;
        }

        /// <summary>
        /// Eliminar snapshot de una configuración
        /// </summary>
        public static void DeleteSnapshot(string configId)
        {
            using var conn = DbManager.GetConnection();
            conn.Open();
            DeleteSnapshot(configId, conn, null);
        }

        private static void DeleteSnapshot(string configId, SqliteConnection conn, SqliteTransaction? transaction)
        {
            const string sql = "DELETE FROM SnapshotData WHERE ConfigId = @ConfigId";

            using var cmd = new SqliteCommand(sql, conn, transaction);
            cmd.Parameters.AddWithValue("@ConfigId", configId);
            cmd.ExecuteNonQuery();
        }

        /// <summary>
        /// Comparar snapshot actual con uno nuevo para detectar cambios
        /// </summary>
        public static SnapshotDiff CompareSnapshots(
            Dictionary<string, string> oldSnapshot,
            Dictionary<string, string> newSnapshot)
        {
            var diff = new SnapshotDiff();

            // Detectar inserts y updates
            foreach (var newRow in newSnapshot)
            {
                if (!oldSnapshot.ContainsKey(newRow.Key))
                {
                    // Nuevo registro
                    diff.Inserts.Add(newRow.Key);
                }
                else if (oldSnapshot[newRow.Key] != newRow.Value)
                {
                    // Registro modificado
                    diff.Updates.Add(newRow.Key);
                }
            }

            // Detectar deletes
            foreach (var oldRow in oldSnapshot)
            {
                if (!newSnapshot.ContainsKey(oldRow.Key))
                {
                    diff.Deletes.Add(oldRow.Key);
                }
            }

            return diff;
        }

        /// <summary>
        /// Generar hash de una fila para snapshot tracking
        /// </summary>
        public static string GenerateRowHash(params object?[] values)
        {
            var sb = new StringBuilder();
            foreach (var value in values)
            {
                sb.Append(value?.ToString() ?? "NULL");
                sb.Append('|');
            }

            var bytes = Encoding.UTF8.GetBytes(sb.ToString());
            var hash = SHA256.HashData(bytes);
            return Convert.ToBase64String(hash);
        }

        /// <summary>
        /// Generar clave de fila basada en PKs
        /// </summary>
        public static string GenerateRowKey(params object?[] pkValues)
        {
            return string.Join("||", pkValues.Select(v => v?.ToString() ?? "NULL"));
        }

        /// <summary>
        /// Obtener estadísticas del snapshot
        /// </summary>
        public static SnapshotStats GetStats(string configId)
        {
            using var conn = DbManager.GetConnection();
            conn.Open();

            const string sql = @"
                SELECT
                    COUNT(*) as RowCount,
                    MAX(CreatedAt) as LastSnapshotTime
                FROM SnapshotData
                WHERE ConfigId = @ConfigId";

            using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.AddWithValue("@ConfigId", configId);

            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                return new SnapshotStats
                {
                    RowCount = reader.IsDBNull(0) ? 0 : reader.GetInt32(0),
                    LastSnapshotTime = reader.IsDBNull(1) ? null : DateTime.Parse(reader.GetString(1))
                };
            }

            return new SnapshotStats();
        }

        /// <summary>
        /// Verificar si existe snapshot para una configuración
        /// </summary>
        public static bool HasSnapshot(string configId)
        {
            using var conn = DbManager.GetConnection();
            conn.Open();

            const string sql = "SELECT COUNT(*) FROM SnapshotData WHERE ConfigId = @ConfigId";

            using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.AddWithValue("@ConfigId", configId);

            return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
        }

        /// <summary>
        /// Limpiar snapshots antiguos (solo mantiene el más reciente)
        /// </summary>
        public static int CleanupOldSnapshots(string configId)
        {
            // En esta implementación solo guardamos un snapshot por config,
            // pero esta función podría extenderse para mantener histórico
            // Por ahora, no hace nada ya que SaveSnapshot ya reemplaza el anterior
            return 0;
        }
    }

    /// <summary>
    /// Diferencia entre dos snapshots
    /// </summary>
    public class SnapshotDiff
    {
        public List<string> Inserts { get; set; } = new();
        public List<string> Updates { get; set; } = new();
        public List<string> Deletes { get; set; } = new();

        public int TotalChanges => Inserts.Count + Updates.Count + Deletes.Count;
        public bool HasChanges => TotalChanges > 0;
    }

    /// <summary>
    /// Estadísticas de snapshot
    /// </summary>
    public class SnapshotStats
    {
        public int RowCount { get; set; }
        public DateTime? LastSnapshotTime { get; set; }
    }
}
