-- ============================================
-- Script: 000_Create_Database_And_Projects.sql
-- Descripción: Crea la base de datos SyncJobCentralDB y tabla Projects
-- Autor: PeopleWorks
-- Fecha: 2025-01-16
-- EJECUTAR PRIMERO antes de los demás scripts
-- ============================================

-- ============================================
-- 1. CREAR BASE DE DATOS
-- ============================================
IF NOT EXISTS (SELECT name FROM sys.databases WHERE name = 'SyncJobCentralDB')
BEGIN
    CREATE DATABASE [SyncJobCentralDB];
    PRINT 'Base de datos SyncJobCentralDB creada exitosamente';
END
ELSE
BEGIN
    PRINT 'Base de datos SyncJobCentralDB ya existe';
END
GO

USE SyncJobCentralDB;
GO

-- ============================================
-- 2. TABLA PRINCIPAL: Projects
-- ============================================
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[Projects]') AND type in (N'U'))
BEGIN
    CREATE TABLE [dbo].[Projects] (
        [ProjectId] NVARCHAR(100) NOT NULL,
        [ProjectName] NVARCHAR(200) NOT NULL,
        [ClientName] NVARCHAR(200) NOT NULL,
        [Description] NVARCHAR(500) NULL,
        [ContactEmail] NVARCHAR(200) NULL,
        [IsActive] BIT NOT NULL DEFAULT 1,
        [CreatedAt] DATETIME NOT NULL DEFAULT GETDATE(),
        [UpdatedAt] DATETIME NOT NULL DEFAULT GETDATE(),

        CONSTRAINT [PK_Projects] PRIMARY KEY CLUSTERED ([ProjectId] ASC)
    );

    PRINT 'Tabla Projects creada exitosamente';
END
ELSE
BEGIN
    PRINT 'Tabla Projects ya existe, omitiendo creación';
END
GO

-- ============================================
-- 3. ÍNDICES PARA PROJECTS
-- ============================================
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_Projects_ClientName' AND object_id = OBJECT_ID('Projects'))
BEGIN
    CREATE NONCLUSTERED INDEX [IX_Projects_ClientName]
    ON [dbo].[Projects]([ClientName]);

    PRINT 'Índice IX_Projects_ClientName creado';
END
GO

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_Projects_IsActive' AND object_id = OBJECT_ID('Projects'))
BEGIN
    CREATE NONCLUSTERED INDEX [IX_Projects_IsActive]
    ON [dbo].[Projects]([IsActive])
    WHERE [IsActive] = 1;

    PRINT 'Índice IX_Projects_IsActive creado';
END
GO

-- ============================================
-- 4. TRIGGER PARA ACTUALIZAR UpdatedAt
-- ============================================
IF EXISTS (SELECT * FROM sys.triggers WHERE name = 'TR_Projects_UpdateTimestamp')
BEGIN
    DROP TRIGGER [dbo].[TR_Projects_UpdateTimestamp];
END
GO

CREATE TRIGGER [dbo].[TR_Projects_UpdateTimestamp]
ON [dbo].[Projects]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;

    UPDATE [dbo].[Projects]
    SET [UpdatedAt] = GETDATE()
    FROM [dbo].[Projects] p
    INNER JOIN inserted i ON p.[ProjectId] = i.[ProjectId];
END
GO

PRINT 'Trigger TR_Projects_UpdateTimestamp creado';
GO

-- ============================================
-- 5. TABLA: Servers (registro de servidores)
-- ============================================
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[Servers]') AND type in (N'U'))
BEGIN
    CREATE TABLE [dbo].[Servers] (
        [ServerId] NVARCHAR(100) NOT NULL,
        [ProjectId] NVARCHAR(100) NOT NULL,
        [ServerName] NVARCHAR(200) NOT NULL,
        [HostMachine] NVARCHAR(200) NULL,
        [IpAddress] NVARCHAR(50) NULL,
        [OperatingSystem] NVARCHAR(100) NULL,
        [SyncJobVersion] NVARCHAR(50) NULL,
        [LastHeartbeat] DATETIME NULL,
        [IsOnline] BIT NOT NULL DEFAULT 0,
        [ConfigurationsCount] INT NOT NULL DEFAULT 0,
        [CreatedAt] DATETIME NOT NULL DEFAULT GETDATE(),
        [UpdatedAt] DATETIME NOT NULL DEFAULT GETDATE(),

        CONSTRAINT [PK_Servers] PRIMARY KEY CLUSTERED ([ServerId] ASC),
        CONSTRAINT [FK_Servers_Projects] FOREIGN KEY ([ProjectId]) REFERENCES [dbo].[Projects]([ProjectId])
    );

    PRINT 'Tabla Servers creada exitosamente';
END
ELSE
BEGIN
    PRINT 'Tabla Servers ya existe, omitiendo creación';
END
GO

-- ============================================
-- 6. TABLA: ExecutionHistory_Central
-- ============================================
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[ExecutionHistory_Central]') AND type in (N'U'))
BEGIN
    CREATE TABLE [dbo].[ExecutionHistory_Central] (
        [ExecutionId] UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
        [ProjectId] NVARCHAR(100) NOT NULL,
        [ServerId] NVARCHAR(100) NULL,
        [ConfigId] NVARCHAR(200) NULL,
        [ConfigDisplayName] NVARCHAR(200) NULL,
        [StartTime] DATETIME NOT NULL,
        [EndTime] DATETIME NULL,
        [DurationMs] BIGINT NULL,
        [Status] NVARCHAR(50) NOT NULL,
        [ExecutionMode] NVARCHAR(50) NULL,
        [RowsRead] BIGINT NULL,
        [RowsInserted] BIGINT NULL,
        [RowsUpdated] BIGINT NULL,
        [RowsDeleted] BIGINT NULL,
        [RowsSkipped] BIGINT NULL,
        [RowsFailed] BIGINT NULL,
        [ErrorMessage] NVARCHAR(MAX) NULL,
        [HostMachine] NVARCHAR(200) NULL,
        [TriggeredBy] NVARCHAR(200) NULL,
        [SyncedAt] DATETIME NOT NULL DEFAULT GETDATE(),

        CONSTRAINT [PK_ExecutionHistory_Central] PRIMARY KEY CLUSTERED ([ExecutionId] ASC),
        CONSTRAINT [FK_ExecutionHistory_Projects] FOREIGN KEY ([ProjectId]) REFERENCES [dbo].[Projects]([ProjectId])
    );

    PRINT 'Tabla ExecutionHistory_Central creada exitosamente';
END
GO

-- Índices para ExecutionHistory_Central
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_ExecutionHistory_Central_ProjectId' AND object_id = OBJECT_ID('ExecutionHistory_Central'))
BEGIN
    CREATE NONCLUSTERED INDEX [IX_ExecutionHistory_Central_ProjectId]
    ON [dbo].[ExecutionHistory_Central]([ProjectId], [StartTime] DESC);

    PRINT 'Índice IX_ExecutionHistory_Central_ProjectId creado';
END
GO

-- ============================================
-- 7. TABLA: SyncLog (log de sincronizaciones)
-- ============================================
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[SyncLog]') AND type in (N'U'))
BEGIN
    CREATE TABLE [dbo].[SyncLog] (
        [LogId] INT IDENTITY(1,1) NOT NULL,
        [ProjectId] NVARCHAR(100) NOT NULL,
        [ServerId] NVARCHAR(100) NULL,
        [SyncType] NVARCHAR(50) NOT NULL,
        [Status] NVARCHAR(50) NOT NULL,
        [RecordsProcessed] INT NULL,
        [RecordsFailed] INT NULL,
        [Timestamp] DATETIME NOT NULL DEFAULT GETDATE(),

        CONSTRAINT [PK_SyncLog] PRIMARY KEY CLUSTERED ([LogId] ASC)
    );

    PRINT 'Tabla SyncLog creada exitosamente';
END
GO

-- ============================================
-- 8. DATOS DE EJEMPLO: Proyecto Iberofarmacos
-- ============================================
IF NOT EXISTS (SELECT 1 FROM [dbo].[Projects] WHERE [ProjectId] = 'iberofarmacos')
BEGIN
    INSERT INTO [dbo].[Projects] ([ProjectId], [ProjectName], [ClientName], [Description], [ContactEmail], [IsActive])
    VALUES (
        'iberofarmacos',
        'Dashboard Iberofarmacos',
        'Iberofarmacos',
        'Sistema de sincronización de datos de ventas para el Dashboard de Iberofarmacos',
        'admin@iberofarmacos.com',
        1
    );

    PRINT 'Proyecto Iberofarmacos insertado como ejemplo';
END
ELSE
BEGIN
    PRINT 'Proyecto Iberofarmacos ya existe';
END
GO

PRINT '============================================';
PRINT 'Script 000_Create_Database_And_Projects.sql ejecutado exitosamente';
PRINT 'Base de datos y tablas base creadas';
PRINT '';
PRINT 'ORDEN DE EJECUCIÓN DE SCRIPTS:';
PRINT '  1. 000_Create_Database_And_Projects.sql (ESTE)';
PRINT '  2. 001_Create_SyncTasks_Table.sql';
PRINT '  3. 002_Create_SyncTasks_Views_And_Procedures.sql';
PRINT '  4. 003_SyncTasks_Sample_Data.sql (opcional)';
PRINT '  5. 004_Create_EmailConfiguration_Table.sql';
PRINT '============================================';
GO
