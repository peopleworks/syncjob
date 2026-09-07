-- ============================================
-- Script: 999_Rollback_SyncTasks.sql
-- Descripción: Elimina todos los objetos de SyncTasks (ROLLBACK)
-- Autor: PeopleWorks
-- Fecha: 2025-12-12
-- Base de Datos: SyncJobCentralDB
-- ============================================
-- ⚠️ ADVERTENCIA: Este script elimina PERMANENTEMENTE la tabla SyncTasks
--    y todos sus datos. Solo ejecutar si desea deshacer la instalación.
-- ============================================

USE SyncJobCentralDB;
GO

PRINT '============================================';
PRINT 'INICIANDO ROLLBACK DE SYNCTASKS';
PRINT '⚠️  ADVERTENCIA: Se eliminarán todos los objetos';
PRINT '============================================';
GO

-- Solicitar confirmación (comentar estas líneas para ejecutar)
-- RAISERROR('ROLLBACK CANCELADO: Quite este RAISERROR del script para confirmar la eliminación', 16, 1);
-- RETURN;
-- GO

-- ============================================
-- 1. ELIMINAR STORED PROCEDURES
-- ============================================
PRINT '';
PRINT 'Eliminando Stored Procedures...';
GO

IF EXISTS (SELECT * FROM sys.procedures WHERE name = 'sp_GetPendingSyncTasks')
BEGIN
    DROP PROCEDURE [dbo].[sp_GetPendingSyncTasks];
    PRINT '✓ sp_GetPendingSyncTasks eliminado';
END
GO

IF EXISTS (SELECT * FROM sys.procedures WHERE name = 'sp_UpdateSyncTaskStatus')
BEGIN
    DROP PROCEDURE [dbo].[sp_UpdateSyncTaskStatus];
    PRINT '✓ sp_UpdateSyncTaskStatus eliminado';
END
GO

IF EXISTS (SELECT * FROM sys.procedures WHERE name = 'sp_MarkNotificationSent')
BEGIN
    DROP PROCEDURE [dbo].[sp_MarkNotificationSent];
    PRINT '✓ sp_MarkNotificationSent eliminado';
END
GO

IF EXISTS (SELECT * FROM sys.procedures WHERE name = 'sp_GetTasksToNotify')
BEGIN
    DROP PROCEDURE [dbo].[sp_GetTasksToNotify];
    PRINT '✓ sp_GetTasksToNotify eliminado';
END
GO

IF EXISTS (SELECT * FROM sys.procedures WHERE name = 'sp_CancelOldPendingTasks')
BEGIN
    DROP PROCEDURE [dbo].[sp_CancelOldPendingTasks];
    PRINT '✓ sp_CancelOldPendingTasks eliminado';
END
GO

IF EXISTS (SELECT * FROM sys.procedures WHERE name = 'sp_GetSyncTasksStatistics')
BEGIN
    DROP PROCEDURE [dbo].[sp_GetSyncTasksStatistics];
    PRINT '✓ sp_GetSyncTasksStatistics eliminado';
END
GO

-- ============================================
-- 2. ELIMINAR VISTAS
-- ============================================
PRINT '';
PRINT 'Eliminando Vistas...';
GO

IF EXISTS (SELECT * FROM sys.views WHERE name = 'vw_PendingSyncTasks')
BEGIN
    DROP VIEW [dbo].[vw_PendingSyncTasks];
    PRINT '✓ vw_PendingSyncTasks eliminada';
END
GO

IF EXISTS (SELECT * FROM sys.views WHERE name = 'vw_RecentSyncTasks')
BEGIN
    DROP VIEW [dbo].[vw_RecentSyncTasks];
    PRINT '✓ vw_RecentSyncTasks eliminada';
END
GO

IF EXISTS (SELECT * FROM sys.views WHERE name = 'vw_SyncTasksStatsByProject')
BEGIN
    DROP VIEW [dbo].[vw_SyncTasksStatsByProject];
    PRINT '✓ vw_SyncTasksStatsByProject eliminada';
END
GO

-- ============================================
-- 3. ELIMINAR TRIGGER
-- ============================================
PRINT '';
PRINT 'Eliminando Triggers...';
GO

IF EXISTS (SELECT * FROM sys.triggers WHERE name = 'TR_SyncTasks_UpdateTimestamp')
BEGIN
    DROP TRIGGER [dbo].[TR_SyncTasks_UpdateTimestamp];
    PRINT '✓ TR_SyncTasks_UpdateTimestamp eliminado';
END
GO

-- ============================================
-- 4. HACER BACKUP DE DATOS (OPCIONAL)
-- ============================================
PRINT '';
PRINT 'Verificando si hay datos en SyncTasks...';
GO

DECLARE @RowCount INT;

IF EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[SyncTasks]') AND type in (N'U'))
BEGIN
    SELECT @RowCount = COUNT(*) FROM [dbo].[SyncTasks];

    IF @RowCount > 0
    BEGIN
        PRINT '';
        PRINT '⚠️  ADVERTENCIA: La tabla SyncTasks contiene ' + CAST(@RowCount AS NVARCHAR) + ' registros';
        PRINT '   Considere hacer un backup antes de continuar:';
        PRINT '';
        PRINT '   SELECT * INTO SyncTasks_Backup_' + CONVERT(VARCHAR, GETDATE(), 112) + ' FROM SyncTasks;';
        PRINT '';

        -- Descomentar la siguiente línea para crear backup automático
        -- EXEC('SELECT * INTO SyncTasks_Backup_' + CONVERT(VARCHAR, GETDATE(), 112) + ' FROM SyncTasks');
        -- PRINT '✓ Backup creado como SyncTasks_Backup_' + CONVERT(VARCHAR, GETDATE(), 112);
    END
    ELSE
    BEGIN
        PRINT '✓ Tabla SyncTasks está vacía, no se requiere backup';
    END
END
GO

-- ============================================
-- 5. ELIMINAR TABLA SYNCTASKS
-- ============================================
PRINT '';
PRINT 'Eliminando tabla SyncTasks...';
GO

IF EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[SyncTasks]') AND type in (N'U'))
BEGIN
    -- Primero eliminar FK constraint si existe
    IF EXISTS (
        SELECT * FROM sys.foreign_keys
        WHERE name = 'FK_SyncTasks_Projects'
            AND parent_object_id = OBJECT_ID('SyncTasks')
    )
    BEGIN
        ALTER TABLE [dbo].[SyncTasks] DROP CONSTRAINT [FK_SyncTasks_Projects];
        PRINT '✓ Foreign Key FK_SyncTasks_Projects eliminada';
    END

    -- Eliminar tabla
    DROP TABLE [dbo].[SyncTasks];
    PRINT '✓ Tabla SyncTasks eliminada';
END
ELSE
BEGIN
    PRINT '⚠️  Tabla SyncTasks no existe, omitiendo eliminación';
END
GO

-- ============================================
-- 6. VERIFICACIÓN FINAL
-- ============================================
PRINT '';
PRINT 'Verificando que todos los objetos fueron eliminados...';
GO

-- Verificar tabla
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[SyncTasks]') AND type in (N'U'))
BEGIN
    PRINT '✓ Tabla SyncTasks: NO EXISTE (correcto)';
END
ELSE
BEGIN
    PRINT '✗ ERROR: Tabla SyncTasks aún existe';
END
GO

-- Verificar vistas
DECLARE @ViewCount INT;
SELECT @ViewCount = COUNT(*)
FROM sys.views
WHERE name IN ('vw_PendingSyncTasks', 'vw_RecentSyncTasks', 'vw_SyncTasksStatsByProject');

IF @ViewCount = 0
BEGIN
    PRINT '✓ Vistas: NINGUNA (correcto)';
END
ELSE
BEGIN
    PRINT '✗ ERROR: Quedan ' + CAST(@ViewCount AS NVARCHAR) + ' vista(s)';
END
GO

-- Verificar stored procedures
DECLARE @ProcCount INT;
SELECT @ProcCount = COUNT(*)
FROM sys.procedures
WHERE name LIKE 'sp_%SyncTasks%'
    OR name IN ('sp_GetPendingSyncTasks', 'sp_UpdateSyncTaskStatus', 'sp_MarkNotificationSent',
                'sp_GetTasksToNotify', 'sp_CancelOldPendingTasks', 'sp_GetSyncTasksStatistics');

IF @ProcCount = 0
BEGIN
    PRINT '✓ Stored Procedures: NINGUNO (correcto)';
END
ELSE
BEGIN
    PRINT '✗ ERROR: Quedan ' + CAST(@ProcCount AS NVARCHAR) + ' procedimiento(s)';
END
GO

-- Verificar triggers
IF NOT EXISTS (SELECT * FROM sys.triggers WHERE name = 'TR_SyncTasks_UpdateTimestamp')
BEGIN
    PRINT '✓ Triggers: NINGUNO (correcto)';
END
ELSE
BEGIN
    PRINT '✗ ERROR: Trigger TR_SyncTasks_UpdateTimestamp aún existe';
END
GO

PRINT '';
PRINT '============================================';
PRINT 'ROLLBACK DE SYNCTASKS COMPLETADO';
PRINT 'Todos los objetos fueron eliminados exitosamente';
PRINT '============================================';
GO

-- ============================================
-- 7. NOTAS FINALES
-- ============================================
PRINT '';
PRINT 'NOTAS IMPORTANTES:';
PRINT '- Los datos de SyncTasks fueron eliminados permanentemente';
PRINT '- Para reinstalar, ejecute los scripts 001 y 002 nuevamente';
PRINT '- Si necesita recuperar datos, restaure desde backup';
PRINT '';
GO
