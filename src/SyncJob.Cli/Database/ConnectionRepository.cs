using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace SyncJob.Database
{
    /// <summary>
    /// Repositorio para gestionar conexiones
    /// </summary>
    public class ConnectionRepository
    {
        private static readonly byte[] EncryptionKey = Encoding.UTF8.GetBytes("SyncJob2025Key16"); // 16 bytes for AES128

        public static void Create(ConnectionEntity conn)
        {
            using var connection = DbManager.GetConnection();
            connection.Open();

            const string sql = @"
INSERT INTO Connections (
    ConnectionId, DisplayName, ServerType, ServerName, DatabaseName, Username,
    PasswordEncrypted, ConnectionStringEncrypted, TrustServerCertificate, Encrypt, IsActive
) VALUES (
    @ConnectionId, @DisplayName, @ServerType, @ServerName, @DatabaseName, @Username,
    @PasswordEncrypted, @ConnectionStringEncrypted, @TrustServerCertificate, @Encrypt, @IsActive
)";

            using var cmd = new SqliteCommand(sql, connection);
            cmd.Parameters.AddWithValue("@ConnectionId", conn.ConnectionId);
            cmd.Parameters.AddWithValue("@DisplayName", conn.DisplayName);
            cmd.Parameters.AddWithValue("@ServerType", conn.ServerType);
            cmd.Parameters.AddWithValue("@ServerName", conn.ServerName);
            cmd.Parameters.AddWithValue("@DatabaseName", conn.DatabaseName);
            cmd.Parameters.AddWithValue("@Username", (object?)conn.Username ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@PasswordEncrypted", (object?)conn.PasswordEncrypted ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@ConnectionStringEncrypted", (object?)conn.ConnectionStringEncrypted ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@TrustServerCertificate", conn.TrustServerCertificate ? 1 : 0);
            cmd.Parameters.AddWithValue("@Encrypt", conn.Encrypt ? 1 : 0);
            cmd.Parameters.AddWithValue("@IsActive", conn.IsActive ? 1 : 0);

            cmd.ExecuteNonQuery();

            Log.Info($"Connection created: {conn.ConnectionId}", evt: "connection.created");
        }

        public static ConnectionEntity? GetById(string connectionId)
        {
            using var connection = DbManager.GetConnection();
            connection.Open();

            const string sql = "SELECT * FROM Connections WHERE ConnectionId = @ConnectionId";
            using var cmd = new SqliteCommand(sql, connection);
            cmd.Parameters.AddWithValue("@ConnectionId", connectionId);

            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return null;

            return MapFromReader(reader);
        }

        public static List<ConnectionListItem> ListAll(bool? activeOnly = null)
        {
            using var connection = DbManager.GetConnection();
            connection.Open();

            var sql = "SELECT * FROM Connections";
            if (activeOnly.HasValue)
                sql += activeOnly.Value ? " WHERE IsActive = 1" : " WHERE IsActive = 0";
            sql += " ORDER BY DisplayName";

            using var cmd = new SqliteCommand(sql, connection);
            var list = new List<ConnectionListItem>();

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(new ConnectionListItem
                {
                    ConnectionId = reader.GetString(0),
                    DisplayName = reader.GetString(1),
                    ServerName = reader.GetString(3),
                    DatabaseName = reader.GetString(4),
                    IsActive = reader.GetInt32(10) == 1,
                    LastTestDate = reader.IsDBNull(11) ? null : DateTime.Parse(reader.GetString(11)),
                    LastTestSuccess = reader.IsDBNull(12) ? null : reader.GetInt32(12) == 1
                });
            }

            return list;
        }

        public static void Delete(string connectionId)
        {
            using var connection = DbManager.GetConnection();
            connection.Open();

            const string sql = "DELETE FROM Connections WHERE ConnectionId = @ConnectionId";
            using var cmd = new SqliteCommand(sql, connection);
            cmd.Parameters.AddWithValue("@ConnectionId", connectionId);
            cmd.ExecuteNonQuery();

            Log.Info($"Connection deleted: {connectionId}", evt: "connection.deleted");
        }

        public static bool Exists(string connectionId)
        {
            using var connection = DbManager.GetConnection();
            connection.Open();

            const string sql = "SELECT COUNT(*) FROM Connections WHERE ConnectionId = @ConnectionId";
            using var cmd = new SqliteCommand(sql, connection);
            cmd.Parameters.AddWithValue("@ConnectionId", connectionId);

            return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
        }

        private static ConnectionEntity MapFromReader(SqliteDataReader reader)
        {
            return new ConnectionEntity
            {
                ConnectionId = reader.GetString(0),
                DisplayName = reader.GetString(1),
                ServerType = reader.GetString(2),
                ServerName = reader.GetString(3),
                DatabaseName = reader.GetString(4),
                Username = reader.IsDBNull(5) ? null : reader.GetString(5),
                PasswordEncrypted = reader.IsDBNull(6) ? null : (byte[])reader.GetValue(6),
                ConnectionStringEncrypted = reader.IsDBNull(7) ? null : (byte[])reader.GetValue(7),
                TrustServerCertificate = reader.GetInt32(8) == 1,
                Encrypt = reader.GetInt32(9) == 1,
                IsActive = reader.GetInt32(10) == 1,
                LastTestDate = reader.IsDBNull(11) ? null : DateTime.Parse(reader.GetString(11)),
                LastTestSuccess = reader.IsDBNull(12) ? null : reader.GetInt32(12) == 1,
                CreatedAt = DateTime.Parse(reader.GetString(13)),
                UpdatedAt = DateTime.Parse(reader.GetString(14))
            };
        }

        // Simple encryption helpers (for demonstration - use proper encryption in production)
        public static byte[] EncryptString(string plainText)
        {
            if (string.IsNullOrEmpty(plainText)) return Array.Empty<byte>();
            var bytes = Encoding.UTF8.GetBytes(plainText);
            // Simple XOR encryption (replace with proper AES in production)
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] ^= EncryptionKey[i % EncryptionKey.Length];
            return bytes;
        }

        public static string DecryptString(byte[] encryptedBytes)
        {
            if (encryptedBytes == null || encryptedBytes.Length == 0) return string.Empty;
            // Simple XOR decryption
            for (int i = 0; i < encryptedBytes.Length; i++)
                encryptedBytes[i] ^= EncryptionKey[i % EncryptionKey.Length];
            return Encoding.UTF8.GetString(encryptedBytes);
        }
    }
}
