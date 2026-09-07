-- ============================================
-- Script: 002_Create_SyncTasks_Views_And_Procedures.sql
-- Descripción: Vistas y Stored Procedures para SyncTasks
-- Autor: PeopleWorks
-- Fecha: 2025-12-12
-- Base de Datos: SyncJobCentralDB
-- CORREGIDO: Usa ClientName en lugar de CompanyName
-- ============================================

USE SyncJobCentralDB;
GO

-- ============================================
-- 1. VISTA: Tareas Pendientes con Info del Proyecto
-- ============================================
IF EXISTS (SELECT * FROM sys.views WHERE name = 'vw_PendingSyncTasks')
BEGIN
    DROP VIEW [dbo].[vw_PendingSyncTasks];
    PRINT 'Vista vw_PendingSyncTasks eliminada para recrear';
END
GO

CREATE VIEW [dbo].[vw_PendingSyncTasks]
AS
SELECT
    t.[TaskId],
    t.[ProjectId],
    p.[ProjectName],
    p.[ClientName],
    t.[ServerId],
    t.[ConfigId],
    t.[Status],
    t.[Priority],
    t.[TaskType],
    t.[RequestedBy],
    t.[RequestedAt],
    t.[RequestReason],
    t.[NotificationEmail],
    DATEDIFF(MINUTE, t.[RequestedAt], GETDATE()) AS [MinutesWaiting],
    DATEDIFF(SECOND, t.[RequestedAt], GETDATE()) AS [SecondsWaiting]
FROM [dbo].[SyncTasks] t
INNER JOIN [dbo].[Projects] p ON t.[ProjectId] = p.[ProjectId]
WHERE t.[Status] = 'Pending';
GO

PRINT 'Vista vw_PendingSyncTasks creada';
GO

-- ============================================
-- 2. VISTA: Tareas Recientes (últimas 24 horas)
-- ============================================
IF EXISTS (SELECT * FROM sys.views WHERE name = 'vw_RecentSyncTasks')
BEGIN
    DROP VIEW [dbo].[vw_RecentSyncTasks];
    PRINT 'Vista vw_RecentSyncTasks eliminada para recrear';
END
GO

CREATE VIEW [dbo].[vw_RecentSyncTasks]
AS
SELECT
    t.[TaskId],
    t.[ProjectId],
    p.[ProjectName],
    t.[ServerId],
    t.[ConfigId],
    t.[Status],
    t.[Priority],
    t.[TaskType],
    t.[RequestedBy],
    t.[RequestedAt],
    t.[StartedAt],
    t.[CompletedAt],
    t.[DurationMs],
    CASE
        WHEN t.[DurationMs] IS NOT NULL THEN CAST(t.[DurationMs] / 1000.0 AS DECIMAL(10,2))
        ELSE NULL
    END AS [DurationSeconds],
    t.[RowsProcessed],
    t.[RowsInserted],
    t.[RowsUpdated],
    t.[RowsDeleted],
    t.[RowsFailed],
    t.[ErrorMessage],
    t.[NotificationSent],
    CASE t.[Status]
        WHEN 'Completed' THEN '✓ Completado'
        WHEN 'Failed' THEN '✗ Fallido'
        WHEN 'Running' THEN '▶ En Ejecución'
        WHEN 'Pending' THEN '⏳ Pendiente'
        WHEN 'Cancelled' THEN '⊗ Cancelado'
        ELSE t.[Status]
    END AS [StatusDisplay]
FROM [dbo].[SyncTasks] t
INNER JOIN [dbo].[Projects] p ON t.[ProjectId] = p.[ProjectId]
WHERE t.[RequestedAt] >= DATEADD(HOUR, -24, GETDATE());
GO

PRINT 'Vista vw_RecentSyncTasks creada';
GO

-- ============================================
-- 3. VISTA: Estadísticas por Proyecto
-- ============================================
IF EXISTS (SELECT * FROM sys.views WHERE name = 'vw_SyncTasksStatsByProject')
BEGIN
    DROP VIEW [dbo].[vw_SyncTasksStatsByProject];
    PRINT 'Vista vw_SyncTasksStatsByProject eliminada para recrear';
END
GO

CREATE VIEW [dbo].[vw_SyncTasksStatsByProject]
AS
SELECT
    t.[ProjectId],
    p.[ProjectName],
    p.[ClientName],
    COUNT(*) AS [TotalTasks],
    SUM(CASE WHEN t.[Status] = 'Pending' THEN 1 ELSE 0 END) AS [PendingTasks],
    SUM(CASE WHEN t.[Status] = 'Running' THEN 1 ELSE 0 END) AS [RunningTasks],
    SUM(CASE WHEN t.[Status] = 'Completed' THEN 1 ELSE 0 END) AS [CompletedTasks],
    SUM(CASE WHEN t.[Status] = 'Failed' THEN 1 ELSE 0 END) AS [FailedTasks],
    SUM(CASE WHEN t.[Status] = 'Cancelled' THEN 1 ELSE 0 END) AS [CancelledTasks],
    AVG(CAST(t.[DurationMs] AS FLOAT)) AS [AvgDurationMs],
    MAX(t.[RequestedAt]) AS [LastRequestedAt],
    SUM(ISNULL(t.[RowsProcessed], 0)) AS [TotalRowsProcessed]
FROM [dbo].[SyncTasks] t
INNER JOIN [dbo].[Projects] p ON t.[ProjectId] = p.[ProjectId]
GROUP BY t.[ProjectId], p.[ProjectName], p.[ClientName];
GO

PRINT 'Vista vw_SyncTasksStatsByProject creada';
GO

-- ============================================
-- 4. STORED PROCEDURE: Obtener Tareas Pendientes (para Worker Service)
-- ============================================
IF EXISTS (SELECT * FROM sys.procedures WHERE name = 'sp_GetPendingSyncTasks')
BEGIN
    DROP PROCEDURE [dbo].[sp_GetPendingSyncTasks];
    PRINT 'Procedimiento sp_GetPendingSyncTasks eliminado para recrear';
END
GO

CREATE PROCEDURE [dbo].[sp_GetPendingSyncTasks]
    @MaxTasks INT = 10,
    @ProjectId NVARCHAR(100) = NULL,
    @ServerId NVARCHAR(100) = NULL
AS
BEGIN
    SET NOCOUNT ON;

    SELECT TOP (@MaxTasks)
        [TaskId],
        [ProjectId],
        [ServerId],
        [ConfigId],
        [Priority],
        [TaskType],
        [RequestedBy],
        [RequestedAt],
        [RequestReason],
        [NotificationEmail],
        [NotifyOnComplete]
    FROM [dbo].[SyncTasks]
    WHERE [Status] = 'Pending'
        AND (@ProjectId IS NULL OR [ProjectId] = @ProjectId)
        AND (@ServerId IS NULL OR [ServerId] = @ServerId)
    ORDER BY [Priority] DESC, [RequestedAt] ASC;
END
GO

PRINT 'Procedimiento sp_GetPendingSyncTasks creado';
GO

-- ============================================
-- 5. STORED PROCEDURE: Actualizar Estado de Tarea
-- ============================================
IF EXISTS (SELECT * FROM sys.procedures WHERE name = 'sp_UpdateSyncTaskStatus')
BEGIN
    DROP PROCEDURE [dbo].[sp_UpdateSyncTaskStatus];
    PRINT 'Procedimiento sp_UpdateSyncTaskStatus eliminado para recrear';
END
GO

CREATE PROCEDURE [dbo].[sp_UpdateSyncTaskStatus]
    @TaskId UNIQUEIDENTIFIER,
    @Status NVARCHAR(50),
    @StartedAt DATETIME = NULL,
    @CompletedAt DATETIME = NULL,
    @DurationMs BIGINT = NULL,
    @RowsProcessed BIGINT = NULL,
    @RowsInserted BIGINT = NULL,
    @RowsUpdated BIGINT = NULL,
    @RowsDeleted BIGINT = NULL,
    @RowsFailed BIGINT = NULL,
    @ExecutionId UNIQUEIDENTIFIER = NULL,
    @ErrorMessage NVARCHAR(MAX) = NULL,
    @ErrorStackTrace NVARCHAR(MAX) = NULL
AS
BEGIN
    SET NOCOUNT ON;

    UPDATE [dbo].[SyncTasks]
    SET
        [Status] = @Status,
        [StartedAt] = COALESCE(@StartedAt, [StartedAt]),
        [CompletedAt] = COALESCE(@CompletedAt, [CompletedAt]),
        [DurationMs] = COALESCE(@DurationMs, [DurationMs]),
        [RowsProcessed] = COALESCE(@RowsProcessed, [RowsProcessed]),
        [RowsInserted] = COALESCE(@RowsInserted, [RowsInserted]),
        [RowsUpdated] = COALESCE(@RowsUpdated, [RowsUpdated]),
        [RowsDeleted] = COALESCE(@RowsDeleted, [RowsDeleted]),
        [RowsFailed] = COALESCE(@RowsFailed, [RowsFailed]),
        [ExecutionId] = COALESCE(@ExecutionId, [ExecutionId]),
        [ErrorMessage] = COALESCE(@ErrorMessage, [ErrorMessage]),
        [ErrorStackTrace] = COALESCE(@ErrorStackTrace, [ErrorStackTrace]),
        [UpdatedAt] = GETDATE()
    WHERE [TaskId] = @TaskId;

    -- Retornar el registro actualizado
    SELECT
        [TaskId],
        [ProjectId],
        [Status],
        [StartedAt],
        [CompletedAt],
        [DurationMs],
        [RowsProcessed],
        [ErrorMessage]
    FROM [dbo].[SyncTasks]
    WHERE [TaskId] = @TaskId;
END
GO

PRINT 'Procedimiento sp_UpdateSyncTaskStatus creado';
GO

-- ============================================
-- 6. STORED PROCEDURE: Marcar Notificación Enviada
-- ============================================
IF EXISTS (SELECT * FROM sys.procedures WHERE name = 'sp_MarkNotificationSent')
BEGIN
    DROP PROCEDURE [dbo].[sp_MarkNotificationSent];
    PRINT 'Procedimiento sp_MarkNotificationSent eliminado para recrear';
END
GO

CREATE PROCEDURE [dbo].[sp_MarkNotificationSent]
    @TaskId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;

    UPDATE [dbo].[SyncTasks]
    SET
        [NotificationSent] = 1,
        [NotificationSentAt] = GETDATE(),
        [UpdatedAt] = GETDATE()
    WHERE [TaskId] = @TaskId;
END
GO

PRINT 'Procedimiento sp_MarkNotificationSent creado';
GO

-- ============================================
-- 7. STORED PROCEDURE: Obtener Tareas para Notificar
-- ============================================
IF EXISTS (SELECT * FROM sys.procedures WHERE name = 'sp_GetTasksToNotify')
BEGIN
    DROP PROCEDURE [dbo].[sp_GetTasksToNotify];
    PRINT 'Procedimiento sp_GetTasksToNotify eliminado para recrear';
END
GO

CREATE PROCEDURE [dbo].[sp_GetTasksToNotify]
    @MaxTasks INT = 10
AS
BEGIN
    SET NOCOUNT ON;

    SELECT TOP (@MaxTasks)
        t.[TaskId],
        t.[ProjectId],
        p.[ProjectName],
        t.[Status],
        t.[TaskType],
        t.[RequestedBy],
        t.[RequestedAt],
        t.[StartedAt],
        t.[CompletedAt],
        t.[DurationMs],
        t.[RowsProcessed],
        t.[RowsInserted],
        t.[RowsUpdated],
        t.[RowsFailed],
        t.[ErrorMessage],
        t.[NotificationEmail]
    FROM [dbo].[SyncTasks] t
    INNER JOIN [dbo].[Projects] p ON t.[ProjectId] = p.[ProjectId]
    WHERE t.[NotifyOnComplete] = 1
        AND t.[NotificationSent] = 0
        AND t.[Status] IN ('Completed', 'Failed')
        AND t.[NotificationEmail] IS NOT NULL
    ORDER BY t.[CompletedAt] ASC;
END
GO

PRINT 'Procedimiento sp_GetTasksToNotify creado';
GO

-- ============================================
-- 8. STORED PROCEDURE: Cancelar Tareas Pendientes Antiguas
-- ============================================
IF EXISTS (SELECT * FROM sys.procedures WHERE name = 'sp_CancelOldPendingTasks')
BEGIN
    DROP PROCEDURE [dbo].[sp_CancelOldPendingTasks];
    PRINT 'Procedimiento sp_CancelOldPendingTasks eliminado para recrear';
END
GO

CREATE PROCEDURE [dbo].[sp_CancelOldPendingTasks]
    @AgeHours INT = 24
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @CutoffDate DATETIME = DATEADD(HOUR, -@AgeHours, GETDATE());

    UPDATE [dbo].[SyncTasks]
    SET
        [Status] = 'Cancelled',
        [ErrorMessage] = 'Task cancelled automatically due to age (>' + CAST(@AgeHours AS NVARCHAR) + ' hours)',
        [UpdatedAt] = GETDATE()
    WHERE [Status] = 'Pending'
        AND [RequestedAt] < @CutoffDate;

    SELECT @@ROWCOUNT AS [TasksCancelled];
END
GO

PRINT 'Procedimiento sp_CancelOldPendingTasks creado';
GO

-- ============================================
-- 9. STORED PROCEDURE: Obtener Estadísticas Generales
-- ============================================
IF EXISTS (SELECT * FROM sys.procedures WHERE name = 'sp_GetSyncTasksStatistics')
BEGIN
    DROP PROCEDURE [dbo].[sp_GetSyncTasksStatistics];
    PRINT 'Procedimiento sp_GetSyncTasksStatistics eliminado para recrear';
END
GO

CREATE PROCEDURE [dbo].[sp_GetSyncTasksStatistics]
    @ProjectId NVARCHAR(100) = NULL,
    @DaysBack INT = 30
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @StartDate DATETIME = DATEADD(DAY, -@DaysBack, GETDATE());

    SELECT
        COUNT(*) AS [TotalTasks],
        SUM(CASE WHEN [Status] = 'Pending' THEN 1 ELSE 0 END) AS [PendingTasks],
        SUM(CASE WHEN [Status] = 'Running' THEN 1 ELSE 0 END) AS [RunningTasks],
        SUM(CASE WHEN [Status] = 'Completed' THEN 1 ELSE 0 END) AS [CompletedTasks],
        SUM(CASE WHEN [Status] = 'Failed' THEN 1 ELSE 0 END) AS [FailedTasks],
        SUM(CASE WHEN [Status] = 'Cancelled' THEN 1 ELSE 0 END) AS [CancelledTasks],

        -- Métricas de tiempo
        AVG(CAST([DurationMs] AS FLOAT)) AS [AvgDurationMs],
        MIN([DurationMs]) AS [MinDurationMs],
        MAX([DurationMs]) AS [MaxDurationMs],

        -- Métricas de datos
        SUM(ISNULL([RowsProcessed], 0)) AS [TotalRowsProcessed],
        SUM(ISNULL([RowsInserted], 0)) AS [TotalRowsInserted],
        SUM(ISNULL([RowsUpdated], 0)) AS [TotalRowsUpdated],
        SUM(ISNULL([RowsFailed], 0)) AS [TotalRowsFailed],

        -- Tasa de éxito
        CASE
            WHEN COUNT(*) > 0
            THEN CAST(SUM(CASE WHEN [Status] = 'Completed' THEN 1 ELSE 0 END) AS FLOAT) / COUNT(*) * 100
            ELSE 0
        END AS [SuccessRate],

        -- Notificaciones
        SUM(CASE WHEN [NotificationSent] = 1 THEN 1 ELSE 0 END) AS [NotificationsSent]
    FROM [dbo].[SyncTasks]
    WHERE [RequestedAt] >= @StartDate
        AND (@ProjectId IS NULL OR [ProjectId] = @ProjectId);
END
GO

PRINT 'Procedimiento sp_GetSyncTasksStatistics creado';
GO

PRINT '============================================';
PRINT 'Script 002_Create_SyncTasks_Views_And_Procedures.sql ejecutado exitosamente';
PRINT 'Vistas y Stored Procedures creados';
PRINT '============================================';
GO
