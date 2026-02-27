-- ============================================================================
-- Script de Setup para Sincronización Incremental
-- ============================================================================
-- Este script te ayuda a preparar tus tablas para usar sincronización incremental
-- Ejecuta las secciones relevantes según tu escenario
-- ============================================================================

USE [TuBaseDeDatos]; -- ⬅️ CAMBIAR por tu base de datos
GO

PRINT '======================================';
PRINT 'Setup de Sincronización Incremental';
PRINT '======================================';
PRINT '';

-- ============================================================================
-- OPCIÓN 1: Agregar columna Timestamp (DateTime) con Trigger
-- ============================================================================
-- Usa esta opción si quieres rastrear cambios con una columna DateTime
-- Ventajas: Fácil de implementar, funciona en todas las versiones de SQL Server
-- Desventajas: Requiere trigger para actualizar la columna

PRINT 'OPCIÓN 1: Agregar columna Timestamp (DateTime)';
PRINT '--------------------------------------------';
GO

-- Paso 1: Agregar columna FechaModificacion
/*
ALTER TABLE dbo.TuTabla
ADD FechaModificacion DATETIME2 NOT NULL DEFAULT GETUTCDATE();
GO

PRINT 'Columna FechaModificacion agregada';
*/

-- Paso 2: Crear trigger para actualizar automáticamente
/*
CREATE OR ALTER TRIGGER trg_TuTabla_UpdateFechaModificacion
ON dbo.TuTabla
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;

    UPDATE t
    SET FechaModificacion = GETUTCDATE()
    FROM dbo.TuTabla t
    INNER JOIN inserted i ON t.IdColumna = i.IdColumna; -- ⬅️ CAMBIAR por tu PK
END;
GO

PRINT 'Trigger de actualización creado';
*/

-- Paso 3: Crear índice para mejorar performance del filtro incremental
/*
CREATE INDEX IX_TuTabla_FechaModificacion
ON dbo.TuTabla (FechaModificacion);
GO

PRINT 'Índice creado en FechaModificacion';
*/

-- Paso 4: Poblar valores históricos (opcional)
/*
UPDATE dbo.TuTabla
SET FechaModificacion = GETUTCDATE()
WHERE FechaModificacion IS NULL;
GO

PRINT 'Valores históricos poblados';
*/

PRINT '';
GO

-- ============================================================================
-- OPCIÓN 2: Agregar columna RowVersion (Binario)
-- ============================================================================
-- Usa esta opción para tracking más confiable y eficiente
-- Ventajas: Actualización automática (no requiere trigger), más confiable
-- Desventajas: Requiere agregar columna a tabla origen

PRINT 'OPCIÓN 2: Agregar columna RowVersion';
PRINT '--------------------------------------------';
GO

-- Paso 1: Agregar columna RowVersion
/*
ALTER TABLE dbo.TuTabla
ADD RowVer ROWVERSION;
GO

PRINT 'Columna RowVersion agregada (se actualiza automáticamente)';
*/

-- Paso 2: Verificar que funciona
/*
-- Prueba: actualiza un registro y verifica que RowVer cambió
SELECT TOP 1 IdColumna, RowVer FROM dbo.TuTabla;

UPDATE dbo.TuTabla
SET AlgunaColumna = AlgunaColumna
WHERE IdColumna = 1; -- ⬅️ CAMBIAR por un ID válido

SELECT TOP 1 IdColumna, RowVer FROM dbo.TuTabla WHERE IdColumna = 1;
-- El valor de RowVer debe haber cambiado
*/

PRINT '';
GO

-- ============================================================================
-- OPCIÓN 3: Habilitar SQL Server Change Tracking (Avanzado)
-- ============================================================================
-- Usa esta opción si quieres detección automática de INSERT/UPDATE/DELETE
-- Ventajas: No requiere modificar tablas, detecta deletes automáticamente
-- Desventajas: Requiere SQL Server 2008+ Standard/Enterprise

PRINT 'OPCIÓN 3: Habilitar Change Tracking';
PRINT '--------------------------------------------';
GO

-- Paso 1: Habilitar Change Tracking en la base de datos
/*
ALTER DATABASE [TuBaseDeDatos] -- ⬅️ CAMBIAR por tu base de datos
SET CHANGE_TRACKING = ON
(
    CHANGE_RETENTION = 2 DAYS,      -- Retener cambios por 2 días
    AUTO_CLEANUP = ON               -- Limpiar automáticamente
);
GO

PRINT 'Change Tracking habilitado en base de datos';
*/

-- Paso 2: Habilitar Change Tracking en la tabla
/*
ALTER TABLE dbo.TuTabla
ENABLE CHANGE_TRACKING
WITH (TRACK_COLUMNS_UPDATED = ON);
GO

PRINT 'Change Tracking habilitado en tabla';
*/

-- Paso 3: Verificar estado
/*
SELECT
    DB_NAME(database_id) AS DatabaseName,
    is_auto_cleanup_on,
    retention_period,
    retention_period_units_desc
FROM sys.change_tracking_databases
WHERE database_id = DB_ID();

SELECT
    OBJECT_NAME(object_id) AS TableName,
    is_track_columns_updated_on
FROM sys.change_tracking_tables
WHERE object_id = OBJECT_ID('dbo.TuTabla');
*/

PRINT '';
GO

-- ============================================================================
-- OPCIÓN 4: Habilitar Change Data Capture (CDC) - Enterprise
-- ============================================================================
-- Usa esta opción para auditoría completa (valores antes/después)
-- Ventajas: Historial completo, no impacta performance
-- Desventajas: Requiere Enterprise Edition (o Standard 2016 SP1+)

PRINT 'OPCIÓN 4: Habilitar CDC (Enterprise Edition)';
PRINT '--------------------------------------------';
GO

-- Paso 1: Habilitar CDC en la base de datos
/*
EXEC sys.sp_cdc_enable_db;
GO

PRINT 'CDC habilitado en base de datos';
*/

-- Paso 2: Habilitar CDC en la tabla
/*
EXEC sys.sp_cdc_enable_table
    @source_schema = N'dbo',
    @source_name = N'TuTabla',
    @role_name = NULL,
    @supports_net_changes = 1;
GO

PRINT 'CDC habilitado en tabla';
*/

-- Paso 3: Verificar estado
/*
SELECT
    DB_NAME(database_id) AS DatabaseName,
    is_cdc_enabled
FROM sys.databases
WHERE database_id = DB_ID();

SELECT
    OBJECT_NAME(object_id) AS TableName,
    is_tracked_by_cdc
FROM sys.tables
WHERE object_id = OBJECT_ID('dbo.TuTabla');
*/

PRINT '';
GO

-- ============================================================================
-- CREAR TABLA DE TRACKING EN DESTINO (Automático en SyncJob)
-- ============================================================================
-- SyncJob crea esta tabla automáticamente con --init-tracking
-- Aquí está el script por si quieres crearla manualmente

PRINT 'Tabla de Tracking (dbo.SyncJobTracking)';
PRINT '--------------------------------------------';
GO

/*
USE [TuBaseDeDatosDestino]; -- ⬅️ CAMBIAR por tu base de datos DESTINO
GO

IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'dbo.SyncJobTracking') AND type = 'U')
BEGIN
    CREATE TABLE dbo.SyncJobTracking (
        JobIdentifier NVARCHAR(255) NOT NULL PRIMARY KEY,
        LastSyncTime DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
        LastRowVersion VARBINARY(8) NULL,
        LastChangeTrackingVersion BIGINT NULL,
        RowsProcessed BIGINT NOT NULL DEFAULT 0,
        RowsInserted BIGINT NOT NULL DEFAULT 0,
        RowsUpdated BIGINT NOT NULL DEFAULT 0,
        RowsDeleted BIGINT NOT NULL DEFAULT 0,
        Success BIT NOT NULL DEFAULT 1,
        ErrorMessage NVARCHAR(MAX) NULL,
        CreatedAt DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
        UpdatedAt DATETIME2 NOT NULL DEFAULT GETUTCDATE()
    );

    CREATE INDEX IX_SyncJobTracking_LastSyncTime ON dbo.SyncJobTracking (LastSyncTime);

    PRINT 'Tabla dbo.SyncJobTracking creada';
END
ELSE
BEGIN
    PRINT 'Tabla dbo.SyncJobTracking ya existe';
END
GO
*/

-- ============================================================================
-- QUERIES ÚTILES PARA MONITOREO
-- ============================================================================

PRINT '';
PRINT 'Queries Útiles para Monitoreo';
PRINT '--------------------------------------------';
PRINT '';
GO

-- Ver historial de sincronizaciones
/*
SELECT
    JobIdentifier,
    FORMAT(LastSyncTime, 'yyyy-MM-dd HH:mm:ss') AS LastSync,
    FORMAT(RowsProcessed, 'N0') AS TotalRows,
    FORMAT(RowsInserted, 'N0') AS Inserted,
    FORMAT(RowsUpdated, 'N0') AS Updated,
    FORMAT(RowsDeleted, 'N0') AS Deleted,
    CASE WHEN Success = 1 THEN 'OK' ELSE 'ERROR' END AS Status,
    ErrorMessage
FROM dbo.SyncJobTracking
ORDER BY LastSyncTime DESC;
*/

-- Ver última sincronización de un job específico
/*
SELECT TOP 1 *
FROM dbo.SyncJobTracking
WHERE JobIdentifier LIKE '%TuTabla%'
ORDER BY LastSyncTime DESC;
*/

-- Resetear tracking (forzar full refresh en próxima ejecución)
/*
DELETE FROM dbo.SyncJobTracking
WHERE JobIdentifier = 'nombre_exacto_del_job';
*/

-- Estadísticas de cambios (con Change Tracking)
/*
SELECT
    CT.object_id,
    OBJECT_NAME(CT.object_id) AS TableName,
    COUNT(*) AS ChangesCount
FROM sys.dm_tran_commit_table CT
GROUP BY CT.object_id
ORDER BY ChangesCount DESC;
*/

-- ============================================================================
-- EJEMPLO COMPLETO: Tabla Clientes con Timestamp
-- ============================================================================

PRINT '';
PRINT 'Ejemplo Completo: Tabla Clientes';
PRINT '--------------------------------------------';
PRINT '';
GO

/*
-- 1. Crear tabla de ejemplo (origen)
CREATE TABLE dbo.Clientes (
    IdCliente INT PRIMARY KEY IDENTITY(1,1),
    NombreCompleto NVARCHAR(200) NOT NULL,
    Email NVARCHAR(100),
    SaldoActual DECIMAL(18,2) DEFAULT 0,
    FechaModificacion DATETIME2 NOT NULL DEFAULT GETUTCDATE()
);
GO

-- 2. Crear trigger de actualización
CREATE TRIGGER trg_Clientes_UpdateFechaModificacion
ON dbo.Clientes
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;

    UPDATE c
    SET FechaModificacion = GETUTCDATE()
    FROM dbo.Clientes c
    INNER JOIN inserted i ON c.IdCliente = i.IdCliente;
END;
GO

-- 3. Crear índice
CREATE INDEX IX_Clientes_FechaModificacion
ON dbo.Clientes (FechaModificacion);
GO

-- 4. Insertar datos de prueba
INSERT INTO dbo.Clientes (NombreCompleto, Email, SaldoActual)
VALUES
    ('Juan Pérez', 'juan@example.com', 1500.00),
    ('María García', 'maria@example.com', 2300.50),
    ('Carlos López', 'carlos@example.com', 890.25);
GO

-- 5. Crear tabla Stage en destino
CREATE TABLE dbo.Clientes_Stage (
    IdCliente INT NOT NULL,
    NombreCompleto NVARCHAR(200) NOT NULL,
    Email NVARCHAR(100),
    SaldoActual DECIMAL(18,2),
    FechaModificacion DATETIME2 NOT NULL
);
GO

-- 6. Crear tabla Final en destino
CREATE TABLE dbo.Clientes_Final (
    IdCliente INT PRIMARY KEY,
    NombreCompleto NVARCHAR(200) NOT NULL,
    Email NVARCHAR(100),
    SaldoActual DECIMAL(18,2),
    FechaModificacion DATETIME2 NOT NULL
);
GO

PRINT 'Ejemplo completo creado';
PRINT '';
PRINT 'Ahora puedes:';
PRINT '1. Configurar appsettings.json con estos nombres de tabla';
PRINT '2. Ejecutar: SyncJob.exe run -c config.json -s Clientes --init-tracking';
PRINT '3. Ejecutar: SyncJob.exe run -c config.json -s Clientes (primera sync - full)';
PRINT '4. Modificar algunos registros en Clientes';
PRINT '5. Ejecutar: SyncJob.exe run -c config.json -s Clientes (segunda sync - INCREMENTAL)';
*/

-- ============================================================================
PRINT '';
PRINT '======================================';
PRINT 'Setup Script Completado';
PRINT '======================================';
PRINT '';
PRINT 'Próximos pasos:';
PRINT '1. Ejecuta las secciones relevantes (descomenta el código)';
PRINT '2. Configura appsettings.incremental.json';
PRINT '3. Ejecuta: SyncJob.exe run -c config.json -s JobName --init-tracking';
PRINT '4. Consulta INCREMENTAL_SYNC.md para documentación completa';
PRINT '';
PRINT '¡Disfruta de sincronizaciones ultra-rápidas! ⚡';
PRINT '';
-- ============================================================================
