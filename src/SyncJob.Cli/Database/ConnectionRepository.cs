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
        // Solo para leer lo que la version anterior escribio. Ver DecryptString.
        private static readonly byte[] EncryptionKey = Encoding.UTF8.GetBytes("SyncJob2025Key16");

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

        /// <summary>
        /// Protege un secreto con DPAPI, en ambito de maquina.
        /// </summary>
        /// <remarks>
        /// Esto era un XOR contra <c>EncryptionKey</c>, una constante de este mismo
        /// archivo, en un repositorio publico. Cualquiera con el <c>syncjob.db</c>
        /// recuperaba todas las contrasenas en cuatro lineas. El comentario original lo
        /// decia -- "replace with proper AES in production" -- y se quedo.
        ///
        /// El ambito es LocalMachine y no Usuario a proposito: quien escribe estas filas
        /// es una persona en una consola, y quien las lee de noche es el servicio de
        /// Windows bajo otra cuenta. Con ambito de usuario el servicio no podria leer
        /// nada de lo que el administrador guardo. A cambio, cualquiera que pueda
        /// ejecutar codigo en esa maquina puede descifrarlas: DPAPI protege el archivo
        /// si se lo llevan, no la maquina.
        /// </remarks>
        public static byte[] EncryptString(string plainText)
        {
            if (string.IsNullOrEmpty(plainText)) return Array.Empty<byte>();

            return ProtectedData.Protect(
                Encoding.UTF8.GetBytes(plainText), null, DataProtectionScope.LocalMachine);
        }

        /// <summary>
        /// Abre un secreto guardado por <see cref="EncryptString"/>, y tambien uno
        /// guardado por la version anterior.
        /// </summary>
        /// <remarks>
        /// Lee las dos formas porque el arreglo escrito ayer sigue en la base de datos de
        /// alguien, y negarse a leerlo dejaria ese trabajo sin poder correr. Una fila
        /// vieja se queda en la forma vieja hasta que se vuelva a guardar
        /// (<c>syncjob connection update --password ...</c>), asi que conviene rehacerlas.
        ///
        /// No modifica el arreglo que recibe. La version anterior hacia el XOR sobre el
        /// arreglo del llamador, de modo que la segunda lectura de la misma entidad
        /// devolvia basura.
        /// </remarks>
        public static string DecryptString(byte[] encryptedBytes)
        {
            if (encryptedBytes == null || encryptedBytes.Length == 0) return string.Empty;

            foreach (var scope in new[] { DataProtectionScope.LocalMachine, DataProtectionScope.CurrentUser })
            {
                try
                {
                    return Encoding.UTF8.GetString(ProtectedData.Unprotect(encryptedBytes, null, scope));
                }
                catch (CryptographicException)
                {
                    // No lo escribio DPAPI en este ambito. Se prueba el siguiente y, al
                    // final, la forma anterior.
                }
            }

            var legacy = new byte[encryptedBytes.Length];
            for (int i = 0; i < legacy.Length; i++)
                legacy[i] = (byte)(encryptedBytes[i] ^ EncryptionKey[i % EncryptionKey.Length]);

            return Encoding.UTF8.GetString(legacy);
        }

        /// <summary>
        /// Si el secreto sigue guardado en la forma anterior, que es debil y conviene
        /// rehacer. Lo usa <c>connection list</c> para avisar sin mostrar el secreto.
        /// </summary>
        public static bool IsLegacyFormat(byte[] encryptedBytes)
        {
            if (encryptedBytes == null || encryptedBytes.Length == 0) return false;

            foreach (var scope in new[] { DataProtectionScope.LocalMachine, DataProtectionScope.CurrentUser })
            {
                try
                {
                    ProtectedData.Unprotect(encryptedBytes, null, scope);
                    return false;
                }
                catch (CryptographicException)
                {
                }
            }

            return true;
        }
    }
}
