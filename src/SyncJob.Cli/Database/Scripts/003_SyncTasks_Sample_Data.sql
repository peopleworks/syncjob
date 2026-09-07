-- ============================================
-- Script: 003_SyncTasks_Sample_Data.sql
-- Descripción: Datos de ejemplo para probar SyncTasks (OPCIONAL)
-- Autor: PeopleWorks
-- Fecha: 2025-12-12
-- Base de Datos: SyncJobCentralDB
-- ============================================

USE SyncJobCentralDB;
GO

PRINT 'Insertando datos de ejemplo para SyncTasks...';
GO

-- ============================================
-- 1. INSERTAR TAREAS DE EJEMPLO
-- ============================================

-- Tarea Pendiente - Iberofarmacos
INSERT INTO [dbo].[SyncTasks] (
    [TaskId],
    [ProjectId],
    [ServerId],
    [Status],
    [Priority],
    [TaskType],
    [RequestedBy],
    [RequestReason],
    [NotifyOnComplete],
    [NotificationEmail]
)
VALUES (
    NEWID(),
    'IBEROFARMACOS',
    'SERVER-IBERO-01',
    'Pending',
    10, -- Alta prioridad
    'DataSync',
    'admin@iberofarmacos.com',
    'Dashboard on-demand refresh',
    1,
    'admin@iberofarmacos.com;dashboard@iberofarmacos.com'
);

-- Tarea Completada - Iberofarmacos
DECLARE @CompletedTaskId UNIQUEIDENTIFIER = NEWID();
INSERT INTO [dbo].[SyncTasks] (
    [TaskId],
    [ProjectId],
    [ServerId],
    [Status],
    [Priority],
    [TaskType],
    [RequestedBy],
    [RequestedAt],
    [StartedAt],
    [CompletedAt],
    [DurationMs],
    [RowsProcessed],
    [RowsInserted],
    [RowsUpdated],
    [RowsDeleted],
    [RowsFailed],
    [NotifyOnComplete],
    [NotificationEmail],
    [NotificationSent]
)
VALUES (
    @CompletedTaskId,
    'IBEROFARMACOS',
    'SERVER-IBERO-01',
    'Completed',
    5,
    'DataSync',
    'system',
    DATEADD(HOUR, -2, GETDATE()),
    DATEADD(HOUR, -2, GETDATE()),
    DATEADD(MINUTE, -115, GETDATE()),
    300000, -- 5 minutos
    15000,
    12000,
    2500,
    500,
    0,
    1,
    'system@peopleworks.com',
    1
);

-- Tarea Fallida - Ejemplo
DECLARE @FailedTaskId UNIQUEIDENTIFIER = NEWID();
INSERT INTO [dbo].[SyncTasks] (
    [TaskId],
    [ProjectId],
    [ServerId],
    [Status],
    [Priority],
    [TaskType],
    [RequestedBy],
    [RequestedAt],
    [StartedAt],
    [CompletedAt],
    [DurationMs],
    [ErrorMessage],
    [ErrorStackTrace],
    [NotifyOnComplete],
    [NotificationEmail],
    [NotificationSent]
)
VALUES (
    @FailedTaskId,
    'IBEROFARMACOS',
    'SERVER-IBERO-01',
    'Failed',
    8,
    'FullRefresh',
    'admin@iberofarmacos.com',
    DATEADD(HOUR, -3, GETDATE()),
    DATEADD(HOUR, -3, GETDATE()),
    DATEADD(MINUTE, -175, GETDATE()),
    12000,
    'Connection timeout: Unable to connect to source database',
    'System.Data.SqlClient.SqlException: Timeout expired...',
    1,
    'admin@iberofarmacos.com',
    1
);

-- Tarea Pendiente con baja prioridad
INSERT INTO [dbo].[SyncTasks] (
    [TaskId],
    [ProjectId],
    [Status],
    [Priority],
    [TaskType],
    [RequestedBy],
    [RequestReason],
    [NotifyOnComplete],
    [NotificationEmail]
)
VALUES (
    NEWID(),
    'IBEROFARMACOS',
    'Pending',
    2, -- Baja prioridad
    'IncrementalOnly',
    'scheduled-job',
    'Scheduled incremental sync',
    0,
    NULL
);

-- Tarea Running (simulación)
INSERT INTO [dbo].[SyncTasks] (
    [TaskId],
    [ProjectId],
    [ServerId],
    [Status],
    [Priority],
    [TaskType],
    [RequestedBy],
    [RequestedAt],
    [StartedAt],
    [NotifyOnComplete],
    [NotificationEmail]
)
VALUES (
    NEWID(),
    'IBEROFARMACOS',
    'SERVER-IBERO-02',
    'Running',
    7,
    'DataSync',
    'api-request',
    DATEADD(MINUTE, -5, GETDATE()),
    DATEADD(MINUTE, -3, GETDATE()),
    1,
    'notifications@iberofarmacos.com'
);

PRINT 'Datos de ejemplo insertados exitosamente';
GO

-- ============================================
-- 2. VERIFICAR DATOS INSERTADOS
-- ============================================
PRINT '';
PRINT 'Verificando datos insertados:';
GO

SELECT
    [Status],
    COUNT(*) AS [Count]
FROM [dbo].[SyncTasks]
GROUP BY [Status]
ORDER BY [Status];
GO

PRINT '';
PRINT '============================================';
PRINT 'Tareas de ejemplo por status:';
SELECT * FROM [dbo].[vw_RecentSyncTasks]
ORDER BY [RequestedAt] DESC;
GO

PRINT '============================================';
PRINT 'Script 003_SyncTasks_Sample_Data.sql ejecutado exitosamente';
PRINT 'Datos de ejemplo insertados en SyncTasks';
PRINT '============================================';
GO
