using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace SyncJob.Database
{
    /// <summary>
    /// Repositorio para configuración de sincronización central
    /// Maneja encriptación/desencriptación de valores sensibles usando DPAPI
    /// </summary>
    public static class CentralSyncRepository
    {
        /// <summary>
        /// Obtiene un valor de configuración (desencripta si es necesario)
        /// </summary>
        public static string? GetConfigValue(string key)
        {
            using var conn = DbManager.GetConnection();
            conn.Open();

            const string sql = "SELECT ConfigValue, IsEncrypted FROM CentralSyncConfig WHERE ConfigKey = @key";
            using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.AddWithValue("@key", key);

            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                var value = reader.GetString(0);
                var isEncrypted = reader.GetInt32(1) == 1;

                if (isEncrypted && !string.IsNullOrEmpty(value))
                {
                    return DecryptString(value);
                }

                return value;
            }

            return null;
        }

        /// <summary>
        /// Guarda un valor de configuración (encripta si es sensible)
        /// </summary>
        public static void SetConfigValue(string key, string? value, bool encrypt = false)
        {
            if (value == null)
            {
                DeleteConfigValue(key);
                return;
            }

            using var conn = DbManager.GetConnection();
            conn.Open();

            var valueToStore = encrypt ? EncryptString(value) : value;

            const string sql = @"
                INSERT INTO CentralSyncConfig (ConfigKey, ConfigValue, IsEncrypted, UpdatedAt)
                VALUES (@key, @value, @isEncrypted, @updatedAt)
                ON CONFLICT(ConfigKey) DO UPDATE SET
                    ConfigValue = @value,
                    IsEncrypted = @isEncrypted,
                    UpdatedAt = @updatedAt";

            using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.AddWithValue("@key", key);
            cmd.Parameters.AddWithValue("@value", valueToStore);
            cmd.Parameters.AddWithValue("@isEncrypted", encrypt ? 1 : 0);
            cmd.Parameters.AddWithValue("@updatedAt", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

            cmd.ExecuteNonQuery();
        }

        /// <summary>
        /// Elimina un valor de configuración
        /// </summary>
        public static void DeleteConfigValue(string key)
        {
            using var conn = DbManager.GetConnection();
            conn.Open();

            const string sql = "DELETE FROM CentralSyncConfig WHERE ConfigKey = @key";
            using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.AddWithValue("@key", key);
            cmd.ExecuteNonQuery();
        }

        /// <summary>
        /// Obtiene toda la configuración de sincronización central
        /// </summary>
        public static CentralSyncSettings GetSettings()
        {
            var settings = new CentralSyncSettings
            {
                Enabled = GetConfigValue("Enabled") == "true",
                ProjectId = GetConfigValue("ProjectId") ?? string.Empty,
                ServerId = GetConfigValue("ServerId") ?? string.Empty,
                ConnectionString = GetConfigValue("ConnectionString"), // Encriptado
                ApiKey = GetConfigValue("ApiKey"), // Encriptado
                ServerUrl = GetConfigValue("ServerUrl"),
                SyncMode = GetConfigValue("SyncMode") ?? "AfterEveryExecution",
                SyncConfigurations = GetConfigValue("SyncConfigurations") == "true",
                SyncConnections = GetConfigValue("SyncConnections") == "true"
            };

            // Parse LastSyncAt
            var lastSyncStr = GetConfigValue("LastSyncAt");
            if (!string.IsNullOrEmpty(lastSyncStr) && DateTime.TryParse(lastSyncStr, out var lastSync))
            {
                settings.LastSyncAt = lastSync;
            }

            // Parse BatchSize
            var batchSizeStr = GetConfigValue("BatchSize");
            if (!string.IsNullOrEmpty(batchSizeStr) && int.TryParse(batchSizeStr, out var batchSize))
            {
                settings.BatchSize = batchSize;
            }

            return settings;
        }

        /// <summary>
        /// Guarda toda la configuración de sincronización central
        /// </summary>
        public static void SaveSettings(CentralSyncSettings settings)
        {
            SetConfigValue("Enabled", settings.Enabled.ToString().ToLower());
            SetConfigValue("ProjectId", settings.ProjectId);
            SetConfigValue("ServerId", settings.ServerId);
            SetConfigValue("SyncMode", settings.SyncMode);
            SetConfigValue("SyncConfigurations", settings.SyncConfigurations.ToString().ToLower());
            SetConfigValue("SyncConnections", settings.SyncConnections.ToString().ToLower());
            SetConfigValue("BatchSize", settings.BatchSize.ToString());

            // Valores sensibles (encriptados)
            if (!string.IsNullOrEmpty(settings.ConnectionString))
            {
                SetConfigValue("ConnectionString", settings.ConnectionString, encrypt: true);
            }

            if (!string.IsNullOrEmpty(settings.ApiKey))
            {
                SetConfigValue("ApiKey", settings.ApiKey, encrypt: true);
            }

            if (!string.IsNullOrEmpty(settings.ServerUrl))
            {
                SetConfigValue("ServerUrl", settings.ServerUrl);
            }

            Log.Info("Central sync settings saved", evt: "central.config.saved");
        }

        /// <summary>
        /// Actualiza el timestamp de última sincronización
        /// </summary>
        public static void UpdateLastSyncTime()
        {
            SetConfigValue("LastSyncAt", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        }

        /// <summary>
        /// Verifica si la sincronización central está configurada
        /// </summary>
        public static bool IsConfigured()
        {
            var settings = GetSettings();
            return !string.IsNullOrEmpty(settings.ProjectId)
                && !string.IsNullOrEmpty(settings.ServerId)
                && !string.IsNullOrEmpty(settings.ConnectionString);
        }

        /// <summary>
        /// Obtiene todas las entradas de configuración (para debugging)
        /// </summary>
        public static List<CentralSyncConfigEntity> GetAllConfigs(bool includeEncrypted = false)
        {
            var configs = new List<CentralSyncConfigEntity>();

            using var conn = DbManager.GetConnection();
            conn.Open();

            const string sql = "SELECT ConfigKey, ConfigValue, IsEncrypted, UpdatedAt, Description FROM CentralSyncConfig";
            using var cmd = new SqliteCommand(sql, conn);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var isEncrypted = reader.GetInt32(2) == 1;

                configs.Add(new CentralSyncConfigEntity
                {
                    ConfigKey = reader.GetString(0),
                    ConfigValue = isEncrypted && !includeEncrypted ? "***ENCRYPTED***" : reader.GetString(1),
                    IsEncrypted = isEncrypted,
                    UpdatedAt = DateTime.Parse(reader.GetString(3)),
                    Description = reader.IsDBNull(4) ? null : reader.GetString(4)
                });
            }

            return configs;
        }

        /// <summary>
        /// Limpia toda la configuración central
        /// </summary>
        public static void ClearAllConfig()
        {
            using var conn = DbManager.GetConnection();
            conn.Open();

            const string sql = "DELETE FROM CentralSyncConfig WHERE ConfigKey NOT IN ('SyncMode', 'SyncConfigurations', 'SyncConnections')";
            using var cmd = new SqliteCommand(sql, conn);
            cmd.ExecuteNonQuery();

            // Deshabilitar sync
            SetConfigValue("Enabled", "false");

            Log.Info("Central sync configuration cleared", evt: "central.config.cleared");
        }

        // ============================================================================
        // ENCRIPTACIÓN CON DPAPI (Windows Data Protection API)
        // ============================================================================

        /// <summary>
        /// Encripta un string usando DPAPI (Windows)
        /// </summary>
        private static string EncryptString(string plainText)
        {
            if (string.IsNullOrEmpty(plainText))
                return plainText;

            try
            {
                byte[] plainBytes = Encoding.UTF8.GetBytes(plainText);
                byte[] encryptedBytes = ProtectedData.Protect(
                    plainBytes,
                    null, // Sin entropía adicional
                    DataProtectionScope.CurrentUser // Solo el usuario actual puede desencriptar
                );

                // Convertir a Base64 para almacenar en SQLite
                return Convert.ToBase64String(encryptedBytes);
            }
            catch (Exception ex)
            {
                Log.Error($"Error encrypting value: {ex.Message}", ex, "central.encrypt.error");
                throw;
            }
        }

        /// <summary>
        /// Desencripta un string usando DPAPI (Windows)
        /// </summary>
        private static string DecryptString(string encryptedText)
        {
            if (string.IsNullOrEmpty(encryptedText))
                return encryptedText;

            try
            {
                byte[] encryptedBytes = Convert.FromBase64String(encryptedText);
                byte[] plainBytes = ProtectedData.Unprotect(
                    encryptedBytes,
                    null,
                    DataProtectionScope.CurrentUser
                );

                return Encoding.UTF8.GetString(plainBytes);
            }
            catch (Exception ex)
            {
                Log.Error($"Error decrypting value: {ex.Message}", ex, "central.decrypt.error");
                throw;
            }
        }

        /// <summary>
        /// Genera un API Key aleatorio
        /// </summary>
        public static string GenerateApiKey()
        {
            const int keyLength = 32;
            byte[] randomBytes = new byte[keyLength];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(randomBytes);
            }

            return Convert.ToBase64String(randomBytes)
                .Replace("+", "")
                .Replace("/", "")
                .Replace("=", "")
                .Substring(0, keyLength);
        }
    }
}
