-- ============================================
-- Script: 001_Create_SyncTasks_Table.sql
-- Descripción: Crea la tabla SyncTasks para manejar sincronizaciones on-demand
-- Autor: PeopleWorks
-- Fecha: 2025-12-12
-- Base de Datos: SyncJobCentralDB
-- ============================================

USE SyncJobCentralDB;
GO

-- ============================================
-- 1. TABLA PRINCIPAL: SyncTasks
-- ============================================
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[SyncTasks]') AND type in (N'U'))
BEGIN
    CREATE TABLE [dbo].[SyncTasks] (
        -- Identificadores
        [TaskId] UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
        [ProjectId] NVARCHAR(100) NOT NULL,
        [ServerId] NVARCHAR(100) NULL,
        [ConfigId] NVARCHAR(200) NULL,  -- NULL = todas las configs del proyecto

        -- Estado de la tarea
        [Status] NVARCHAR(50) NOT NULL DEFAULT 'Pending',
        -- Valores: Pending, Running, Completed, Failed, Cancelled

        -- Prioridad
        [Priority] INT NOT NULL DEFAULT 5, -- 1=Baja, 5=Normal, 10=Alta

        -- Tipo de sincronización
        [TaskType] NVARCHAR(50) NOT NULL DEFAULT 'DataSync',
        -- Valores: DataSync, FullRefresh, IncrementalOnly

        -- Información de la solicitud
        [RequestedBy] NVARCHAR(200) NULL, -- Usuario que solicitó
        [RequestedAt] DATETIME NOT NULL DEFAULT GETDATE(),
        [RequestReason] NVARCHAR(500) NULL, -- "Dashboard on-demand", "Manual sync", etc.

        -- Ejecución
        [StartedAt] DATETIME NULL,
        [CompletedAt] DATETIME NULL,
        [DurationMs] BIGINT NULL,

        -- Resultados
        [RowsProcessed] BIGINT NULL,
        [RowsInserted] BIGINT NULL,
        [RowsUpdated] BIGINT NULL,
        [RowsDeleted] BIGINT NULL,
        [RowsFailed] BIGINT NULL,
        [ExecutionId] UNIQUEIDENTIFIER NULL, -- FK a ExecutionHistory_Central
        [ErrorMessage] NVARCHAR(MAX) NULL,
        [ErrorStackTrace] NVARCHAR(MAX) NULL,

        -- Notificaciones
        [NotifyOnComplete] BIT NOT NULL DEFAULT 1,
        [NotificationEmail] NVARCHAR(500) NULL, -- Separados por ;
        [NotificationSent] BIT NOT NULL DEFAULT 0,
        [NotificationSentAt] DATETIME NULL,

        -- Metadata
        [CreatedAt] DATETIME NOT NULL DEFAULT GETDATE(),
        [UpdatedAt] DATETIME NOT NULL DEFAULT GETDATE(),

        -- Constraints
        CONSTRAINT [PK_SyncTasks] PRIMARY KEY CLUSTERED ([TaskId] ASC),
        CONSTRAINT [FK_SyncTasks_Projects] FOREIGN KEY ([ProjectId]) REFERENCES [dbo].[Projects]([ProjectId]),
        CONSTRAINT [CK_SyncTasks_Status] CHECK ([Status] IN ('Pending', 'Running', 'Completed', 'Failed', 'Cancelled')),
        CONSTRAINT [CK_SyncTasks_TaskType] CHECK ([TaskType] IN ('DataSync', 'FullRefresh', 'IncrementalOnly')),
        CONSTRAINT [CK_SyncTasks_Priority] CHECK ([Priority] BETWEEN 1 AND 10)
    );

    PRINT 'Tabla SyncTasks creada exitosamente';
END
ELSE
BEGIN
    PRINT 'Tabla SyncTasks ya existe, omitiendo creación';
END
GO

-- ============================================
-- 2. ÍNDICES PARA PERFORMANCE
-- ============================================

-- Índice para tareas pendientes ordenadas por prioridad
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_SyncTasks_Status_Priority' AND object_id = OBJECT_ID('SyncTasks'))
BEGIN
    CREATE NONCLUSTERED INDEX [IX_SyncTasks_Status_Priority]
    ON [dbo].[SyncTasks]([Status], [Priority] DESC, [RequestedAt] ASC)
    WHERE [Status] = 'Pending';

    PRINT 'Índice IX_SyncTasks_Status_Priority creado';
END
GO

-- Índice para consultas por proyecto
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_SyncTasks_ProjectId_Status' AND object_id = OBJECT_ID('SyncTasks'))
BEGIN
    CREATE NONCLUSTERED INDEX [IX_SyncTasks_ProjectId_Status]
    ON [dbo].[SyncTasks]([ProjectId], [Status], [RequestedAt] DESC)
    INCLUDE ([TaskType], [Priority], [RequestedBy]);

    PRINT 'Índice IX_SyncTasks_ProjectId_Status creado';
END
GO

-- Índice para consultas por servidor
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_SyncTasks_ServerId_Status' AND object_id = OBJECT_ID('SyncTasks'))
BEGIN
    CREATE NONCLUSTERED INDEX [IX_SyncTasks_ServerId_Status]
    ON [dbo].[SyncTasks]([ServerId], [Status], [RequestedAt] DESC)
    WHERE [ServerId] IS NOT NULL;

    PRINT 'Índice IX_SyncTasks_ServerId_Status creado';
END
GO

-- Índice para notificaciones pendientes
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_SyncTasks_Notifications' AND object_id = OBJECT_ID('SyncTasks'))
BEGIN
    CREATE NONCLUSTERED INDEX [IX_SyncTasks_Notifications]
    ON [dbo].[SyncTasks]([NotificationSent], [Status])
    WHERE [NotifyOnComplete] = 1 AND [NotificationSent] = 0 AND [Status] IN ('Completed', 'Failed');

    PRINT 'Índice IX_SyncTasks_Notifications creado';
END
GO

-- ============================================
-- 3. TRIGGER PARA ACTUALIZAR UpdatedAt
-- ============================================
IF EXISTS (SELECT * FROM sys.triggers WHERE name = 'TR_SyncTasks_UpdateTimestamp')
BEGIN
    DROP TRIGGER [dbo].[TR_SyncTasks_UpdateTimestamp];
    PRINT 'Trigger TR_SyncTasks_UpdateTimestamp eliminado para recrear';
END
GO

CREATE TRIGGER [dbo].[TR_SyncTasks_UpdateTimestamp]
ON [dbo].[SyncTasks]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;

    UPDATE [dbo].[SyncTasks]
    SET [UpdatedAt] = GETDATE()
    FROM [dbo].[SyncTasks] t
    INNER JOIN inserted i ON t.[TaskId] = i.[TaskId];
END
GO

PRINT 'Trigger TR_SyncTasks_UpdateTimestamp creado';
GO

-- ============================================
-- 4. VALORES POR DEFECTO Y VERIFICACIÓN
-- ============================================

-- Verificar que la tabla Projects existe
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[Projects]') AND type in (N'U'))
BEGIN
    PRINT 'ADVERTENCIA: La tabla Projects no existe. La FK no se podrá validar.';
END
ELSE
BEGIN
    PRINT 'Tabla Projects existe - FK validada correctamente';
END
GO

-- ============================================
-- 5. GRANTS (Permisos)
-- ============================================

-- Permisos para el usuario de la aplicación (ajustar según necesidad)
-- GRANT SELECT, INSERT, UPDATE ON [dbo].[SyncTasks] TO [SyncJobUser];
-- GO

PRINT '============================================';
PRINT 'Script 001_Create_SyncTasks_Table.sql ejecutado exitosamente';
PRINT 'Tabla SyncTasks lista para usar';
PRINT '============================================';
GO
