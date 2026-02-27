using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using SyncJob.Services.Models;
using System.Data;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

namespace SyncJob.Services
{
    /// <summary>
    /// Servicio para envío de notificaciones por email usando el API centralizado de PeopleWorks
    /// </summary>
    public class EmailNotificationService
    {
        private readonly string _centralConnectionString;
        private readonly ILogger<EmailNotificationService> _logger;
        private readonly HttpClient _httpClient;

        public EmailNotificationService(
            string centralConnectionString,
            ILogger<EmailNotificationService> logger,
            HttpClient? httpClient = null)
        {
            _centralConnectionString = centralConnectionString;
            _logger = logger;
            _httpClient = httpClient ?? new HttpClient();
        }

        /// <summary>
        /// Obtiene la configuración activa de emails desde la base de datos
        /// </summary>
        public async Task<EmailConfigurationEntity?> GetEmailConfigurationAsync()
        {
            try
            {
                using var connection = new SqlConnection(_centralConnectionString);
                await connection.OpenAsync();

                using var cmd = new SqlCommand("sp_GetEmailConfiguration", connection);
                cmd.CommandType = CommandType.StoredProcedure;

                using var reader = await cmd.ExecuteReaderAsync();

                if (await reader.ReadAsync())
                {
                    return new EmailConfigurationEntity
                    {
                        ConfigId = reader.GetInt32(reader.GetOrdinal("ConfigId")),
                        ApiUrl = reader.GetString(reader.GetOrdinal("ApiUrl")),
                        ApiKey = reader.IsDBNull(reader.GetOrdinal("ApiKey")) ? null : reader.GetString(reader.GetOrdinal("ApiKey")),
                        RNC = reader.GetString(reader.GetOrdinal("RNC")),
                        IsEnabled = reader.GetBoolean(reader.GetOrdinal("IsEnabled")),
                        TemplateDirectory = reader.IsDBNull(reader.GetOrdinal("TemplateDirectory")) ? null : reader.GetString(reader.GetOrdinal("TemplateDirectory")),
                        DefaultTemplate = reader.GetString(reader.GetOrdinal("DefaultTemplate")),
                        FromName = reader.GetString(reader.GetOrdinal("FromName")),
                        ReplyToEmail = reader.IsDBNull(reader.GetOrdinal("ReplyToEmail")) ? null : reader.GetString(reader.GetOrdinal("ReplyToEmail")),
                        DefaultRecipients = reader.IsDBNull(reader.GetOrdinal("DefaultRecipients")) ? null : reader.GetString(reader.GetOrdinal("DefaultRecipients")),
                        AlwaysCopyTo = reader.IsDBNull(reader.GetOrdinal("AlwaysCopyTo")) ? null : reader.GetString(reader.GetOrdinal("AlwaysCopyTo")),
                        MaxRetries = reader.GetInt32(reader.GetOrdinal("MaxRetries")),
                        RetryDelaySeconds = reader.GetInt32(reader.GetOrdinal("RetryDelaySeconds")),
                        LogEmailsSent = reader.GetBoolean(reader.GetOrdinal("LogEmailsSent")),
                        LogEmailErrors = reader.GetBoolean(reader.GetOrdinal("LogEmailErrors")),
                        IncludeExecutionLogs = reader.GetBoolean(reader.GetOrdinal("IncludeExecutionLogs")),
                        MaxLogSizeKB = reader.GetInt32(reader.GetOrdinal("MaxLogSizeKB"))
                    };
                }

                _logger.LogWarning("No se encontró configuración de email habilitada");
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error obteniendo configuración de email");
                throw;
            }
        }

        /// <summary>
        /// Envía notificación de tarea completada exitosamente
        /// </summary>
        public async Task<bool> SendTaskCompletedNotificationAsync(
            SyncTaskEntity task,
            SyncTaskExecutionResult result,
            EmailConfigurationEntity? config = null)
        {
            config ??= await GetEmailConfigurationAsync();

            if (config == null || !config.IsEnabled)
            {
                _logger.LogWarning("Email deshabilitado o sin configuración. No se enviará notificación.");
                return false;
            }

            try
            {
                // Generar análisis con IA
                AIAnalysisResult? aiAnalysis = null;
                try
                {
                    var aiService = new AIAnalysisService(
                        _centralConnectionString,
                        _logger as ILogger<AIAnalysisService> ??
                            LoggerFactory.Create(builder => builder.AddConsole())
                                .CreateLogger<AIAnalysisService>());

                    aiAnalysis = await aiService.AnalyzeTaskExecutionAsync(task, result);
                }
                catch (Exception exAI)
                {
                    _logger.LogWarning(exAI, "Error generando análisis IA, continuando sin insights");
                }

                // Renderizar HTML desde template
                var htmlBody = await RenderTaskCompletedTemplateAsync(task, result, aiAnalysis);

                // Preparar lista de destinatarios
                var destinatarios = ParseEmailList(task.NotificationEmail);

                if (destinatarios.Count == 0)
                {
                    _logger.LogWarning("No hay destinatarios configurados para la tarea {TaskId}", task.TaskId);
                    return false;
                }

                // Agregar BCC si está configurado
                List<string>? bccList = null;
                if (!string.IsNullOrWhiteSpace(config.AlwaysCopyTo))
                {
                    bccList = ParseEmailListFromJson(config.AlwaysCopyTo);
                }

                // Crear request
                var request = new EnviarEmailRequest
                {
                    UserName = task.RequestedBy ?? "SyncJob",
                    ResourceName = $"SyncJob - {task.ProjectId}",
                    DestinatariosTo = destinatarios,
                    DestinatariosBcc = bccList,
                    Asunto = $"✅ Sincronización completada: {task.ProjectId}",
                    CuerpoHtml = htmlBody
                };

                // Enviar email
                var response = await SendEmailViaCentralApiAsync(request, config);

                // Loguear si está configurado
                if (config.LogEmailsSent && response != null)
                {
                    await LogEmailSentAsync(
                        task.TaskId,
                        task.ProjectId,
                        task.ServerId,
                        JsonSerializer.Serialize(destinatarios),
                        request.Asunto,
                        response.Success ? "Success" : "Failed",
                        response.EmailId,
                        response.DestinatariosCount,
                        response.Success ? DateTime.Now : null,
                        response.Message);
                }

                return response?.Success ?? false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error enviando notificación de tarea completada {TaskId}", task.TaskId);

                if (config.LogEmailErrors)
                {
                    await LogEmailSentAsync(
                        task.TaskId,
                        task.ProjectId,
                        task.ServerId,
                        task.NotificationEmail ?? "",
                        $"Sincronización completada: {task.ProjectId}",
                        "Failed",
                        null,
                        0,
                        null,
                        ex.Message);
                }

                return false;
            }
        }

        /// <summary>
        /// Envía notificación de tarea fallida
        /// </summary>
        public async Task<bool> SendTaskFailedNotificationAsync(
            SyncTaskEntity task,
            SyncTaskExecutionResult result,
            EmailConfigurationEntity? config = null)
        {
            config ??= await GetEmailConfigurationAsync();

            if (config == null || !config.IsEnabled)
            {
                _logger.LogWarning("Email deshabilitado o sin configuración. No se enviará notificación.");
                return false;
            }

            try
            {
                var htmlBody = await RenderTaskFailedTemplateAsync(task, result);

                var destinatarios = ParseEmailList(task.NotificationEmail);
                if (destinatarios.Count == 0)
                    return false;

                List<string>? bccList = null;
                if (!string.IsNullOrWhiteSpace(config.AlwaysCopyTo))
                {
                    bccList = ParseEmailListFromJson(config.AlwaysCopyTo);
                }

                var request = new EnviarEmailRequest
                {
                    UserName = task.RequestedBy ?? "SyncJob",
                    ResourceName = $"SyncJob - {task.ProjectId}",
                    DestinatariosTo = destinatarios,
                    DestinatariosBcc = bccList,
                    Asunto = $"❌ Error en sincronización: {task.ProjectId}",
                    CuerpoHtml = htmlBody
                };

                var response = await SendEmailViaCentralApiAsync(request, config);

                if (config.LogEmailsSent && response != null)
                {
                    await LogEmailSentAsync(
                        task.TaskId,
                        task.ProjectId,
                        task.ServerId,
                        JsonSerializer.Serialize(destinatarios),
                        request.Asunto,
                        response.Success ? "Success" : "Failed",
                        response.EmailId,
                        response.DestinatariosCount,
                        response.Success ? DateTime.Now : null,
                        response.Message);
                }

                return response?.Success ?? false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error enviando notificación de tarea fallida {TaskId}", task.TaskId);
                return false;
            }
        }

        /// <summary>
        /// Llama al API centralizado de emails de PeopleWorks
        /// </summary>
        private async Task<EnviarEmailResponse?> SendEmailViaCentralApiAsync(
            EnviarEmailRequest request,
            EmailConfigurationEntity config)
        {
            try
            {
                _logger.LogInformation("Enviando email via API centralizado: {ApiUrl}", config.ApiUrl);

                // Agregar header RNC (requerido por el middleware)
                _httpClient.DefaultRequestHeaders.Remove("RNC");
                _httpClient.DefaultRequestHeaders.Add("RNC", config.RNC);

                // Agregar API Key si está configurada
                if (!string.IsNullOrWhiteSpace(config.ApiKey))
                {
                    _httpClient.DefaultRequestHeaders.Remove("X-API-Key");
                    _httpClient.DefaultRequestHeaders.Add("X-API-Key", config.ApiKey);
                }

                // Enviar request
                var httpResponse = await _httpClient.PostAsJsonAsync(config.ApiUrl, request);

                if (httpResponse.IsSuccessStatusCode)
                {
                    var response = await httpResponse.Content.ReadFromJsonAsync<EnviarEmailResponse>();

                    if (response != null && response.Success)
                    {
                        _logger.LogInformation("✅ Email enviado exitosamente. EmailId: {EmailId}, Destinatarios: {Count}",
                            response.EmailId, response.DestinatariosCount);
                    }
                    else
                    {
                        _logger.LogWarning("⚠️ API devolvió Success=false: {Message}", response?.Message);
                    }

                    return response;
                }
                else
                {
                    var errorContent = await httpResponse.Content.ReadAsStringAsync();
                    _logger.LogError("❌ Error en API de emails. Status: {Status}, Error: {Error}",
                        httpResponse.StatusCode, errorContent);

                    return new EnviarEmailResponse
                    {
                        Success = false,
                        Message = $"HTTP {httpResponse.StatusCode}: {errorContent}"
                    };
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Excepción llamando al API de emails");
                return new EnviarEmailResponse
                {
                    Success = false,
                    Message = ex.Message
                };
            }
        }

        /// <summary>
        /// Renderiza template de tarea completada
        /// </summary>
        private async Task<string> RenderTaskCompletedTemplateAsync(
            SyncTaskEntity task,
            SyncTaskExecutionResult result,
            AIAnalysisResult? aiAnalysis = null)
        {
            // Preparar sección de insights IA
            var aiInsightsHtml = GenerateAIInsightsHtml(aiAnalysis);

            // Template HTML con análisis IA
            return $@"
<!DOCTYPE html>
<html lang=""es"">
<head>
    <meta charset=""UTF-8"">
    <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
    <style>
        body {{
            font-family: 'Segoe UI', Tahoma, Geneva, Verdana, sans-serif;
            line-height: 1.6;
            color: #333;
            max-width: 650px;
            margin: 0 auto;
            background-color: #f8f9fa;
            padding: 20px;
        }}
        .email-container {{
            background: white;
            border-radius: 15px;
            box-shadow: 0 10px 30px rgba(0,0,0,0.1);
            overflow: hidden;
        }}
        .header {{
            background: linear-gradient(135deg, #059669 0%, #10b981 100%);
            color: white;
            text-align: center;
            padding: 30px 20px;
        }}
        .content {{
            padding: 30px;
        }}
        .metric {{
            background: #f0fdf4;
            border-left: 4px solid #10b981;
            padding: 15px;
            margin: 15px 0;
            border-radius: 4px;
        }}
        .footer {{
            font-size: 12px;
            color: #6c757d;
            margin-top: 30px;
            border-top: 1px solid #e9ecef;
            padding: 20px;
            background: #f8f9fa;
        }}
    </style>
</head>
<body>
    <div class=""email-container"">
        <div class=""header"">
            <h1>✅ Sincronización Completada</h1>
            <p style=""margin: 10px 0 0 0; font-size: 1.2em;"">{DateTime.Now:dd/MM/yyyy HH:mm:ss}</p>
        </div>
        <div class=""content"">
            <p>La tarea de sincronización ha sido completada exitosamente.</p>

            <div class=""metric"">
                <strong>📋 Proyecto:</strong> {task.ProjectId}<br>
                <strong>🔧 Tipo de Tarea:</strong> {task.TaskType}<br>
                <strong>👤 Solicitado por:</strong> {task.RequestedBy ?? "Sistema"}<br>
                <strong>📅 Solicitado:</strong> {task.RequestedAt:dd/MM/yyyy HH:mm:ss}<br>
                <strong>⏱️ Duración:</strong> {result.DurationMs / 1000.0:N2} segundos<br>
                <strong>📊 Filas Procesadas:</strong> {result.RowsProcessed:N0}<br>
                <strong>➕ Filas Insertadas:</strong> {result.RowsInserted:N0}<br>
                <strong>🔄 Filas Actualizadas:</strong> {result.RowsUpdated:N0}<br>
                <strong>➖ Filas Eliminadas:</strong> {result.RowsDeleted:N0}<br>
                <strong>🆔 ExecutionId:</strong> {result.ExecutionId}<br>
            </div>

            {(string.IsNullOrWhiteSpace(task.RequestReason) ? "" : $@"
            <div class=""metric"">
                <strong>📝 Razón de la solicitud:</strong><br>
                {task.RequestReason}
            </div>
            ")}

            {aiInsightsHtml}

            <p style=""color: #059669; font-weight: bold; text-align: center; font-size: 1.1em; margin-top: 30px;"">
                ✓ Datos sincronizados correctamente
            </p>
        </div>
        <div class=""footer"">
            <p><strong>🤖 PeopleWorks SyncJob</strong></p>
            <p>Este es un mensaje automático del sistema de sincronización.</p>
            <p>Generado: {DateTime.Now:yyyy-MM-dd HH:mm:ss}</p>
        </div>
    </div>
</body>
</html>";
        }

        /// <summary>
        /// Renderiza template de tarea fallida
        /// </summary>
        private async Task<string> RenderTaskFailedTemplateAsync(
            SyncTaskEntity task,
            SyncTaskExecutionResult result)
        {
            return $@"
<!DOCTYPE html>
<html lang=""es"">
<head>
    <meta charset=""UTF-8"">
    <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
    <style>
        body {{
            font-family: 'Segoe UI', Tahoma, Geneva, Verdana, sans-serif;
            line-height: 1.6;
            color: #333;
            max-width: 650px;
            margin: 0 auto;
            background-color: #f8f9fa;
            padding: 20px;
        }}
        .email-container {{
            background: white;
            border-radius: 15px;
            box-shadow: 0 10px 30px rgba(0,0,0,0.1);
            overflow: hidden;
        }}
        .header {{
            background: linear-gradient(135deg, #dc2626 0%, #ef4444 100%);
            color: white;
            text-align: center;
            padding: 30px 20px;
        }}
        .content {{
            padding: 30px;
        }}
        .metric {{
            background: #fef2f2;
            border-left: 4px solid #ef4444;
            padding: 15px;
            margin: 15px 0;
            border-radius: 4px;
        }}
        .error-box {{
            background: #fee2e2;
            border: 2px solid #ef4444;
            padding: 20px;
            margin: 25px 0;
            border-radius: 10px;
            font-family: monospace;
            font-size: 13px;
        }}
        .footer {{
            font-size: 12px;
            color: #6c757d;
            margin-top: 30px;
            border-top: 1px solid #e9ecef;
            padding: 20px;
            background: #f8f9fa;
        }}
    </style>
</head>
<body>
    <div class=""email-container"">
        <div class=""header"">
            <h1>❌ Error en Sincronización</h1>
            <p style=""margin: 10px 0 0 0; font-size: 1.2em;"">{DateTime.Now:dd/MM/yyyy HH:mm:ss}</p>
        </div>
        <div class=""content"">
            <p><strong>La tarea de sincronización ha fallado y requiere atención.</strong></p>

            <div class=""metric"">
                <strong>📋 Proyecto:</strong> {task.ProjectId}<br>
                <strong>🔧 Tipo de Tarea:</strong> {task.TaskType}<br>
                <strong>👤 Solicitado por:</strong> {task.RequestedBy ?? "Sistema"}<br>
                <strong>📅 Solicitado:</strong> {task.RequestedAt:dd/MM/yyyy HH:mm:ss}<br>
                <strong>⏱️ Duración:</strong> {result.DurationMs / 1000.0:N2} segundos<br>
                <strong>🆔 ExecutionId:</strong> {result.ExecutionId}<br>
            </div>

            <div class=""error-box"">
                <strong style=""color: #dc2626;"">⚠️ Error:</strong><br>
                {result.ErrorMessage ?? "Error desconocido"}
            </div>

            {(!string.IsNullOrWhiteSpace(result.ErrorStackTrace) ? $@"
            <details>
                <summary style=""cursor: pointer; color: #dc2626; font-weight: bold;"">Ver Stack Trace</summary>
                <pre style=""background: #fef2f2; padding: 15px; border-radius: 4px; overflow-x: auto; font-size: 11px;"">{result.ErrorStackTrace}</pre>
            </details>
            " : "")}

            <p style=""color: #dc2626; font-weight: bold; text-align: center; font-size: 1.1em; margin-top: 30px;"">
                ⚠️ Por favor, revise el error y reintente la sincronización
            </p>
        </div>
        <div class=""footer"">
            <p><strong>🤖 PeopleWorks SyncJob</strong></p>
            <p>Este es un mensaje automático del sistema de sincronización.</p>
            <p>Generado: {DateTime.Now:yyyy-MM-dd HH:mm:ss}</p>
        </div>
    </div>
</body>
</html>";
        }

        /// <summary>
        /// Genera HTML con insights de los modelos de IA
        /// </summary>
        private string GenerateAIInsightsHtml(AIAnalysisResult? aiAnalysis)
        {
            if (aiAnalysis == null || aiAnalysis.HasError)
                return "";

            var html = new System.Text.StringBuilder();

            html.AppendLine(@"
            <div style=""background: linear-gradient(135deg, #667eea 0%, #764ba2 100%);
                        border-radius: 15px;
                        padding: 25px;
                        margin: 25px 0;
                        color: white;
                        box-shadow: 0 4px 15px rgba(102,126,234,0.3);"">
                <h3 style=""margin: 0 0 20px 0; font-size: 1.3em; text-align: center;"">
                    🤖 Análisis Multi-IA
                </h3>");

            // Claude Insights
            if (!string.IsNullOrWhiteSpace(aiAnalysis.ClaudeInsights))
            {
                html.AppendLine($@"
                <div style=""background: rgba(255,255,255,0.15);
                            border-radius: 10px;
                            padding: 15px;
                            margin-bottom: 15px;"">
                    <div style=""font-weight: bold; margin-bottom: 8px; font-size: 1.05em;"">
                        🧠 Claude (Análisis Estratégico)
                    </div>
                    <div style=""line-height: 1.6; font-size: 0.95em;"">
                        {System.Web.HttpUtility.HtmlEncode(aiAnalysis.ClaudeInsights).Replace("\n", "<br>")}
                    </div>
                </div>");
            }

            // Codex Patterns
            if (!string.IsNullOrWhiteSpace(aiAnalysis.CodexPatterns))
            {
                html.AppendLine($@"
                <div style=""background: rgba(255,255,255,0.15);
                            border-radius: 10px;
                            padding: 15px;
                            margin-bottom: 15px;"">
                    <div style=""font-weight: bold; margin-bottom: 8px; font-size: 1.05em;"">
                        💻 Codex (Detección de Patrones)
                    </div>
                    <div style=""line-height: 1.6; font-size: 0.95em;"">
                        {System.Web.HttpUtility.HtmlEncode(aiAnalysis.CodexPatterns).Replace("\n", "<br>")}
                    </div>
                </div>");
            }

            // Gemini Alerts
            if (!string.IsNullOrWhiteSpace(aiAnalysis.GeminiAlerts))
            {
                html.AppendLine($@"
                <div style=""background: rgba(255,255,255,0.15);
                            border-radius: 10px;
                            padding: 15px;
                            margin-bottom: 10px;"">
                    <div style=""font-weight: bold; margin-bottom: 8px; font-size: 1.05em;"">
                        ⚡ Gemini (Alertas Rápidas)
                    </div>
                    <div style=""line-height: 1.6; font-size: 0.95em;"">
                        {System.Web.HttpUtility.HtmlEncode(aiAnalysis.GeminiAlerts).Replace("\n", "<br>")}
                    </div>
                </div>");
            }

            html.AppendLine(@"
                <div style=""text-align: center; margin-top: 15px; font-size: 0.85em; opacity: 0.9;"">
                    <span style=""background: rgba(255,255,255,0.2);
                                 padding: 6px 12px;
                                 border-radius: 15px;
                                 font-weight: 600;"">
                        Powered by Claude • Codex • Gemini
                    </span>
                </div>
            </div>");

            return html.ToString();
        }

        /// <summary>
        /// Parsea lista de emails separados por coma
        /// </summary>
        private List<string> ParseEmailList(string? emailList)
        {
            if (string.IsNullOrWhiteSpace(emailList))
                return new List<string>();

            return emailList
                .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(e => e.Trim())
                .Where(e => !string.IsNullOrWhiteSpace(e))
                .ToList();
        }

        /// <summary>
        /// Parsea JSON array de emails
        /// </summary>
        private List<string> ParseEmailListFromJson(string? json)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(json))
                    return new List<string>();

                var emails = JsonSerializer.Deserialize<List<string>>(json);
                return emails ?? new List<string>();
            }
            catch
            {
                return new List<string>();
            }
        }

        /// <summary>
        /// Registra email enviado en el log
        /// </summary>
        private async Task LogEmailSentAsync(
            Guid? taskId,
            string? projectId,
            string? serverId,
            string recipients,
            string subject,
            string status,
            string? emailId,
            int destinatariosCount,
            DateTime? sentAt,
            string? errorMessage)
        {
            try
            {
                using var connection = new SqlConnection(_centralConnectionString);
                await connection.OpenAsync();

                using var cmd = new SqlCommand("sp_LogEmailSent", connection);
                cmd.CommandType = CommandType.StoredProcedure;

                cmd.Parameters.AddWithValue("@TaskId", (object?)taskId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@ProjectId", (object?)projectId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@ServerId", (object?)serverId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Recipients", recipients);
                cmd.Parameters.AddWithValue("@Subject", subject);
                cmd.Parameters.AddWithValue("@BodyHtml", DBNull.Value);
                cmd.Parameters.AddWithValue("@Attachments", DBNull.Value);
                cmd.Parameters.AddWithValue("@Status", status);
                cmd.Parameters.AddWithValue("@EmailId", (object?)emailId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@DestinatariosCount", destinatariosCount);
                cmd.Parameters.AddWithValue("@SentAt", (object?)sentAt ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@ErrorMessage", (object?)errorMessage ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@RetryCount", 0);

                await cmd.ExecuteNonQueryAsync();

                _logger.LogDebug("Email log registrado para tarea {TaskId}", taskId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error registrando email log");
            }
        }
    }
}
