-- =====================================================================
-- Script: 004_Create_EmailConfiguration_Table.sql
-- Descripción: Configuración centralizada de emails para SyncJob
-- Base de Datos: SyncJobCentralDB
-- Fecha: 2025-12-12
-- =====================================================================

USE SyncJobCentralDB;
GO

-- =====================================================================
-- TABLA: EmailConfiguration
-- Configuración del servicio de emails centralizado de PeopleWorks
-- =====================================================================

IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[EmailConfiguration]') AND type = 'U')
BEGIN
    CREATE TABLE [dbo].[EmailConfiguration]
    (
        [ConfigId] INT IDENTITY(1,1) NOT NULL PRIMARY KEY,

        -- Configuración del API de Emails
        [ApiUrl] NVARCHAR(500) NOT NULL DEFAULT 'http://localhost:5000/ecf/email/send',
        [ApiKey] NVARCHAR(500) NULL, -- API Key si el servicio lo requiere
        [RNC] NVARCHAR(50) NOT NULL DEFAULT 'SYNCJOB-INTERNAL', -- RNC para autenticación
        [IsEnabled] BIT NOT NULL DEFAULT 1,

        -- Configuración de Templates
        [TemplateDirectory] NVARCHAR(500) NULL, -- Directorio de templates (opcional)
        [DefaultTemplate] NVARCHAR(100) DEFAULT 'sync-notification.html',

        -- Configuración de Envío
        [FromName] NVARCHAR(200) NOT NULL DEFAULT 'SyncJob Notifications',
        [ReplyToEmail] NVARCHAR(200) NULL,
        [DefaultRecipients] NVARCHAR(MAX) NULL, -- JSON array de emails por defecto
        [AlwaysCopyTo] NVARCHAR(MAX) NULL, -- Emails que siempre reciben copia (BCC)

        -- Configuración de Reintentos
        [MaxRetries] INT NOT NULL DEFAULT 3,
        [RetryDelaySeconds] INT NOT NULL DEFAULT 60,

        -- Configuración de Logging
        [LogEmailsSent] BIT NOT NULL DEFAULT 1,
        [LogEmailErrors] BIT NOT NULL DEFAULT 1,

        -- Configuración de Contenido
        [IncludeExecutionLogs] BIT NOT NULL DEFAULT 0, -- Incluir logs de ejecución como adjunto
        [MaxLogSizeKB] INT NOT NULL DEFAULT 500, -- Tamaño máximo de logs adjuntos

        -- Metadatos
        [CreatedAt] DATETIME2 NOT NULL DEFAULT GETDATE(),
        [UpdatedAt] DATETIME2 NOT NULL DEFAULT GETDATE(),
        [UpdatedBy] NVARCHAR(200) NULL,
        [Notes] NVARCHAR(MAX) NULL
    );

    PRINT '✓ Tabla EmailConfiguration creada exitosamente';
END
ELSE
BEGIN
    PRINT '⚠ Tabla EmailConfiguration ya existe';
END
GO

-- =====================================================================
-- ÍNDICES
-- =====================================================================

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_EmailConfiguration_IsEnabled')
BEGIN
    CREATE NONCLUSTERED INDEX [IX_EmailConfiguration_IsEnabled]
    ON [dbo].[EmailConfiguration]([IsEnabled])
    WHERE [IsEnabled] = 1;

    PRINT '✓ Índice IX_EmailConfiguration_IsEnabled creado';
END
GO

-- =====================================================================
-- TRIGGER: UpdatedAt automático
-- =====================================================================

IF EXISTS (SELECT * FROM sys.triggers WHERE name = 'trg_EmailConfiguration_UpdatedAt')
    DROP TRIGGER [dbo].[trg_EmailConfiguration_UpdatedAt];
GO

CREATE TRIGGER [dbo].[trg_EmailConfiguration_UpdatedAt]
ON [dbo].[EmailConfiguration]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;

    UPDATE [dbo].[EmailConfiguration]
    SET [UpdatedAt] = GETDATE()
    WHERE [ConfigId] IN (SELECT [ConfigId] FROM inserted);
END
GO

PRINT '✓ Trigger trg_EmailConfiguration_UpdatedAt creado';
GO

-- =====================================================================
-- DATOS INICIALES
-- Configuración por defecto apuntando al servicio centralizado de PeopleWorks
-- =====================================================================

IF NOT EXISTS (SELECT * FROM [dbo].[EmailConfiguration])
BEGIN
    INSERT INTO [dbo].[EmailConfiguration]
    (
        [ApiUrl],
        [RNC],
        [IsEnabled],
        [DefaultTemplate],
        [FromName],
        [ReplyToEmail],
        [DefaultRecipients],
        [AlwaysCopyTo],
        [MaxRetries],
        [RetryDelaySeconds],
        [LogEmailsSent],
        [LogEmailErrors],
        [IncludeExecutionLogs],
        [MaxLogSizeKB],
        [Notes]
    )
    VALUES
    (
        'http://localhost:5000/ecf/email/send', -- URL del servicio centralizado
        'SYNCJOB-INTERNAL', -- RNC genérico para SyncJob
        1, -- Habilitado
        'sync-notification.html', -- Template por defecto
        'PeopleWorks SyncJob', -- Nombre del remitente
        'soporte@peopleworksservices.com', -- Reply-To
        NULL, -- Sin destinatarios por defecto (se especifican en cada tarea)
        '["admin@peopleworksservices.com"]', -- Copia oculta al admin
        3, -- 3 reintentos
        60, -- 60 segundos entre reintentos
        1, -- Log de emails enviados
        1, -- Log de errores
        0, -- No incluir logs de ejecución por ahora
        500, -- Máximo 500 KB de logs
        'Configuración inicial del servicio de emails centralizado. Apunta al API de DGIIFacturaElectronicaAPI.'
    );

    PRINT '✓ Configuración inicial de emails insertada';
    PRINT '';
    PRINT '⚠ IMPORTANTE: Actualizar los siguientes campos según el entorno:';
    PRINT '   - ApiUrl: URL real del servicio de emails (ej: https://api.peopleworksservices.com/ecf/email/send)';
    PRINT '   - RNC: Verificar que el RNC esté registrado en DGIIFacturaElectronicaAdmin y tenga permiso PuedeEnviarEmail=1';
    PRINT '   - AlwaysCopyTo: Emails que siempre recibirán copia de las notificaciones';
END
ELSE
BEGIN
    PRINT '⚠ Ya existe configuración de emails';
END
GO

-- =====================================================================
-- TABLA: EmailLog
-- Registro de todos los emails enviados para auditoría
-- =====================================================================

IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[EmailLog]') AND type = 'U')
BEGIN
    CREATE TABLE [dbo].[EmailLog]
    (
        [EmailLogId] BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,

        -- Referencia
        [TaskId] UNIQUEIDENTIFIER NULL, -- Referencia a SyncTasks (opcional)
        [ProjectId] NVARCHAR(100) NULL,
        [ServerId] NVARCHAR(100) NULL,

        -- Datos del Email
        [Recipients] NVARCHAR(MAX) NOT NULL, -- JSON array de destinatarios
        [Subject] NVARCHAR(500) NOT NULL,
        [BodyHtml] NVARCHAR(MAX) NULL,
        [Attachments] NVARCHAR(MAX) NULL, -- JSON array de nombres de archivos adjuntos

        -- Resultado del Envío
        [Status] NVARCHAR(50) NOT NULL, -- Success, Failed, Retrying
        [EmailId] NVARCHAR(100) NULL, -- ID del email del servicio externo
        [DestinatariosCount] INT NULL,
        [SentAt] DATETIME2 NULL,
        [ErrorMessage] NVARCHAR(MAX) NULL,
        [RetryCount] INT NOT NULL DEFAULT 0,

        -- Metadatos
        [CreatedAt] DATETIME2 NOT NULL DEFAULT GETDATE(),
        [UpdatedAt] DATETIME2 NOT NULL DEFAULT GETDATE()
    );

    CREATE NONCLUSTERED INDEX [IX_EmailLog_TaskId] ON [dbo].[EmailLog]([TaskId]);
    CREATE NONCLUSTERED INDEX [IX_EmailLog_Status] ON [dbo].[EmailLog]([Status]);
    CREATE NONCLUSTERED INDEX [IX_EmailLog_CreatedAt] ON [dbo].[EmailLog]([CreatedAt] DESC);
    CREATE NONCLUSTERED INDEX [IX_EmailLog_ProjectId] ON [dbo].[EmailLog]([ProjectId]);

    PRINT '✓ Tabla EmailLog creada exitosamente';
END
ELSE
BEGIN
    PRINT '⚠ Tabla EmailLog ya existe';
END
GO

-- =====================================================================
-- STORED PROCEDURE: sp_GetEmailConfiguration
-- Obtiene la configuración activa de emails
-- =====================================================================

IF EXISTS (SELECT * FROM sys.procedures WHERE name = 'sp_GetEmailConfiguration')
    DROP PROCEDURE [dbo].[sp_GetEmailConfiguration];
GO

CREATE PROCEDURE [dbo].[sp_GetEmailConfiguration]
AS
BEGIN
    SET NOCOUNT ON;

    SELECT TOP 1
        [ConfigId],
        [ApiUrl],
        [ApiKey],
        [RNC],
        [IsEnabled],
        [TemplateDirectory],
        [DefaultTemplate],
        [FromName],
        [ReplyToEmail],
        [DefaultRecipients],
        [AlwaysCopyTo],
        [MaxRetries],
        [RetryDelaySeconds],
        [LogEmailsSent],
        [LogEmailErrors],
        [IncludeExecutionLogs],
        [MaxLogSizeKB],
        [CreatedAt],
        [UpdatedAt],
        [UpdatedBy],
        [Notes]
    FROM [dbo].[EmailConfiguration]
    WHERE [IsEnabled] = 1
    ORDER BY [ConfigId] DESC; -- Obtener la configuración más reciente
END
GO

PRINT '✓ Stored Procedure sp_GetEmailConfiguration creado';
GO

-- =====================================================================
-- STORED PROCEDURE: sp_LogEmailSent
-- Registra un email enviado en el log
-- =====================================================================

IF EXISTS (SELECT * FROM sys.procedures WHERE name = 'sp_LogEmailSent')
    DROP PROCEDURE [dbo].[sp_LogEmailSent];
GO

CREATE PROCEDURE [dbo].[sp_LogEmailSent]
    @TaskId UNIQUEIDENTIFIER = NULL,
    @ProjectId NVARCHAR(100) = NULL,
    @ServerId NVARCHAR(100) = NULL,
    @Recipients NVARCHAR(MAX),
    @Subject NVARCHAR(500),
    @BodyHtml NVARCHAR(MAX) = NULL,
    @Attachments NVARCHAR(MAX) = NULL,
    @Status NVARCHAR(50),
    @EmailId NVARCHAR(100) = NULL,
    @DestinatariosCount INT = NULL,
    @SentAt DATETIME2 = NULL,
    @ErrorMessage NVARCHAR(MAX) = NULL,
    @RetryCount INT = 0
AS
BEGIN
    SET NOCOUNT ON;

    INSERT INTO [dbo].[EmailLog]
    (
        [TaskId],
        [ProjectId],
        [ServerId],
        [Recipients],
        [Subject],
        [BodyHtml],
        [Attachments],
        [Status],
        [EmailId],
        [DestinatariosCount],
        [SentAt],
        [ErrorMessage],
        [RetryCount]
    )
    VALUES
    (
        @TaskId,
        @ProjectId,
        @ServerId,
        @Recipients,
        @Subject,
        @BodyHtml,
        @Attachments,
        @Status,
        @EmailId,
        @DestinatariosCount,
        @SentAt,
        @ErrorMessage,
        @RetryCount
    );

    SELECT SCOPE_IDENTITY() AS EmailLogId;
END
GO

PRINT '✓ Stored Procedure sp_LogEmailSent creado';
GO

-- =====================================================================
-- VISTA: vw_EmailLogRecent
-- Emails recientes (últimos 30 días)
-- =====================================================================

IF EXISTS (SELECT * FROM sys.views WHERE name = 'vw_EmailLogRecent')
    DROP VIEW [dbo].[vw_EmailLogRecent];
GO

CREATE VIEW [dbo].[vw_EmailLogRecent]
AS
SELECT
    el.[EmailLogId],
    el.[TaskId],
    el.[ProjectId],
    el.[ServerId],
    el.[Recipients],
    el.[Subject],
    el.[Status],
    el.[EmailId],
    el.[DestinatariosCount],
    el.[SentAt],
    el.[ErrorMessage],
    el.[RetryCount],
    el.[CreatedAt],
    CASE
        WHEN el.[Status] = 'Success' THEN 'Enviado'
        WHEN el.[Status] = 'Failed' THEN 'Fallido'
        WHEN el.[Status] = 'Retrying' THEN 'Reintentando'
        ELSE el.[Status]
    END AS [StatusDisplay],
    DATEDIFF(MINUTE, el.[CreatedAt], GETDATE()) AS [MinutesAgo]
FROM [dbo].[EmailLog] el
WHERE el.[CreatedAt] >= DATEADD(DAY, -30, GETDATE());
GO

PRINT '✓ Vista vw_EmailLogRecent creada';
GO

-- =====================================================================
-- SCRIPT COMPLETADO
-- =====================================================================

PRINT '';
PRINT '====================================================================';
PRINT '✅ Script 004_Create_EmailConfiguration_Table.sql completado';
PRINT '====================================================================';
PRINT '';
PRINT '📋 Tablas creadas:';
PRINT '   - EmailConfiguration (configuración del servicio)';
PRINT '   - EmailLog (auditoría de emails enviados)';
PRINT '';
PRINT '📋 Vistas creadas:';
PRINT '   - vw_EmailLogRecent';
PRINT '';
PRINT '📋 Stored Procedures creados:';
PRINT '   - sp_GetEmailConfiguration';
PRINT '   - sp_LogEmailSent';
PRINT '';
PRINT '⚠ PRÓXIMOS PASOS:';
PRINT '   1. Verificar que el RNC "SYNCJOB-INTERNAL" esté registrado en DGIIFacturaElectronicaAdmin';
PRINT '   2. Asegurarse de que el campo PuedeEnviarEmail = 1 para ese RNC';
PRINT '   3. Actualizar ApiUrl con la URL real del servicio (producción)';
PRINT '   4. Configurar AlwaysCopyTo con emails del equipo de soporte';
PRINT '';
GO
