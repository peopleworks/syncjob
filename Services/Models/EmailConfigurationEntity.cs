using System;

namespace SyncJob.Services.Models
{
    /// <summary>
    /// Configuración del servicio de emails centralizado
    /// </summary>
    public class EmailConfigurationEntity
    {
        public int ConfigId { get; set; }
        public string ApiUrl { get; set; } = string.Empty;
        public string? ApiKey { get; set; }
        public string RNC { get; set; } = string.Empty;
        public bool IsEnabled { get; set; }
        public string? TemplateDirectory { get; set; }
        public string DefaultTemplate { get; set; } = "sync-notification.html";
        public string FromName { get; set; } = string.Empty;
        public string? ReplyToEmail { get; set; }
        public string? DefaultRecipients { get; set; }
        public string? AlwaysCopyTo { get; set; }
        public int MaxRetries { get; set; }
        public int RetryDelaySeconds { get; set; }
        public bool LogEmailsSent { get; set; }
        public bool LogEmailErrors { get; set; }
        public bool IncludeExecutionLogs { get; set; }
        public int MaxLogSizeKB { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public string? UpdatedBy { get; set; }
        public string? Notes { get; set; }
    }

    /// <summary>
    /// Request para el API de emails centralizado de PeopleWorks
    /// </summary>
    public class EnviarEmailRequest
    {
        public string UserName { get; set; } = "SyncJob";
        public string ResourceName { get; set; } = "SyncJob Worker Service";
        public List<string> DestinatariosTo { get; set; } = new();
        public List<string>? DestinatariosCc { get; set; }
        public List<string>? DestinatariosBcc { get; set; }
        public string Asunto { get; set; } = string.Empty;
        public string CuerpoHtml { get; set; } = string.Empty;
        public List<EmailAttachmentDto>? Adjuntos { get; set; }
    }

    public class EmailAttachmentDto
    {
        public string FileName { get; set; } = string.Empty;
        public string ContentBase64 { get; set; } = string.Empty;
        public string ContentType { get; set; } = "application/octet-stream";
    }

    /// <summary>
    /// Response del API de emails
    /// </summary>
    public class EnviarEmailResponse
    {
        public bool Success { get; set; }
        public string? Message { get; set; }
        public string? EmailId { get; set; }
        public int DestinatariosCount { get; set; }
    }
}
