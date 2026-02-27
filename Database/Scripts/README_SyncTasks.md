# 📋 SyncTasks - Sistema de Sincronizaciones On-Demand

## 🎯 Descripción

**SyncTasks** es un sistema de cola de tareas para manejar sincronizaciones on-demand en SyncJobCentralDB. Permite a los clientes solicitar actualizaciones de datos fuera del horario programado, con notificaciones por email cuando la tarea finaliza.

---

## 📦 Componentes

### Scripts SQL

| Script | Descripción | Obligatorio |
|--------|-------------|-------------|
| `001_Create_SyncTasks_Table.sql` | Tabla principal, índices y triggers | ✅ Sí |
| `002_Create_SyncTasks_Views_And_Procedures.sql` | Vistas y Stored Procedures | ✅ Sí |
| `003_SyncTasks_Sample_Data.sql` | Datos de ejemplo para pruebas | ⚠️ Opcional |

---

## 🚀 Instalación

### Pre-requisitos

1. SQL Server 2017 o superior
2. Base de datos `SyncJobCentralDB` ya creada
3. Tabla `Projects` existente (requerida por FK)
4. Permisos de `CREATE TABLE`, `CREATE VIEW`, `CREATE PROCEDURE`

### Pasos de Instalación

#### 1. Ejecutar Scripts en Orden

```sql
-- 1. Crear tabla SyncTasks
:r 001_Create_SyncTasks_Table.sql

-- 2. Crear vistas y stored procedures
:r 002_Create_SyncTasks_Views_And_Procedures.sql

-- 3. (OPCIONAL) Insertar datos de ejemplo
:r 003_SyncTasks_Sample_Data.sql
```

#### 2. Verificar Instalación

```sql
USE SyncJobCentralDB;
GO

-- Verificar tabla existe
SELECT * FROM INFORMATION_SCHEMA.TABLES
WHERE TABLE_NAME = 'SyncTasks';

-- Verificar vistas
SELECT * FROM INFORMATION_SCHEMA.VIEWS
WHERE TABLE_NAME LIKE '%SyncTasks%';

-- Verificar stored procedures
SELECT * FROM INFORMATION_SCHEMA.ROUTINES
WHERE ROUTINE_NAME LIKE 'sp_%SyncTasks%';
```

---

## 📊 Estructura de la Tabla SyncTasks

### Campos Principales

```sql
TaskId              UNIQUEIDENTIFIER PRIMARY KEY
ProjectId           NVARCHAR(100)  -- FK a Projects
ServerId            NVARCHAR(100)  -- NULL = cualquier servidor
ConfigId            NVARCHAR(200)  -- NULL = todas las configs
Status              NVARCHAR(50)   -- Pending, Running, Completed, Failed, Cancelled
Priority            INT            -- 1-10 (1=Baja, 5=Normal, 10=Alta)
TaskType            NVARCHAR(50)   -- DataSync, FullRefresh, IncrementalOnly
RequestedBy         NVARCHAR(200)  -- Usuario/sistema que solicitó
RequestedAt         DATETIME
NotificationEmail   NVARCHAR(500)  -- Emails separados por ;
```

### Estados Posibles

| Estado | Descripción |
|--------|-------------|
| `Pending` | Tarea en cola, esperando ejecución |
| `Running` | Tarea en ejecución actualmente |
| `Completed` | Tarea completada exitosamente |
| `Failed` | Tarea falló con error |
| `Cancelled` | Tarea cancelada manualmente o por timeout |

---

## 🔍 Vistas Disponibles

### 1. `vw_PendingSyncTasks`

Tareas pendientes con información del proyecto, ordenadas por prioridad.

```sql
SELECT * FROM vw_PendingSyncTasks;
```

**Columnas:**
- `TaskId`, `ProjectId`, `ProjectName`, `CompanyName`
- `Status`, `Priority`, `TaskType`
- `MinutesWaiting` - Minutos desde que se solicitó

### 2. `vw_RecentSyncTasks`

Tareas de las últimas 24 horas con métricas completas.

```sql
SELECT * FROM vw_RecentSyncTasks
ORDER BY RequestedAt DESC;
```

### 3. `vw_SyncTasksStatsByProject`

Estadísticas agregadas por proyecto.

```sql
SELECT * FROM vw_SyncTasksStatsByProject
WHERE ProjectId = 'IBEROFARMACOS';
```

---

## ⚙️ Stored Procedures

### 1. `sp_GetPendingSyncTasks`

Obtener tareas pendientes para procesar (usado por Worker Service).

```sql
EXEC sp_GetPendingSyncTasks
    @MaxTasks = 10,
    @ProjectId = 'IBEROFARMACOS',  -- NULL para todos
    @ServerId = NULL;              -- NULL para todos
```

### 2. `sp_UpdateSyncTaskStatus`

Actualizar estado y resultados de una tarea.

```sql
EXEC sp_UpdateSyncTaskStatus
    @TaskId = '12345678-1234-1234-1234-123456789012',
    @Status = 'Completed',
    @CompletedAt = GETDATE(),
    @DurationMs = 120000,
    @RowsProcessed = 5000,
    @RowsInserted = 4500,
    @RowsUpdated = 500;
```

### 3. `sp_MarkNotificationSent`

Marcar que la notificación fue enviada.

```sql
EXEC sp_MarkNotificationSent
    @TaskId = '12345678-1234-1234-1234-123456789012';
```

### 4. `sp_GetTasksToNotify`

Obtener tareas completadas que requieren notificación.

```sql
EXEC sp_GetTasksToNotify @MaxTasks = 10;
```

### 5. `sp_CancelOldPendingTasks`

Cancelar tareas pendientes muy antiguas (limpieza).

```sql
-- Cancelar tareas pendientes de más de 24 horas
EXEC sp_CancelOldPendingTasks @AgeHours = 24;
```

### 6. `sp_GetSyncTasksStatistics`

Obtener estadísticas de tareas.

```sql
-- Estadísticas de los últimos 30 días para Iberofarmacos
EXEC sp_GetSyncTasksStatistics
    @ProjectId = 'IBEROFARMACOS',
    @DaysBack = 30;
```

---

## 💼 Casos de Uso

### Caso 1: Solicitar Sincronización desde Dashboard

```sql
INSERT INTO SyncTasks (
    TaskId,
    ProjectId,
    ServerId,
    Status,
    Priority,
    TaskType,
    RequestedBy,
    RequestReason,
    NotifyOnComplete,
    NotificationEmail
)
VALUES (
    NEWID(),
    'IBEROFARMACOS',
    NULL,  -- Cualquier servidor disponible
    'Pending',
    10,  -- Alta prioridad
    'DataSync',
    'admin@iberofarmacos.com',
    'Dashboard on-demand refresh',
    1,
    'admin@iberofarmacos.com;dashboard@iberofarmacos.com'
);
```

### Caso 2: Worker Service Procesando Tareas

```sql
-- 1. Obtener tarea pendiente
DECLARE @TaskId UNIQUEIDENTIFIER;

SELECT TOP 1 @TaskId = TaskId
FROM vw_PendingSyncTasks
ORDER BY Priority DESC, RequestedAt ASC;

-- 2. Marcar como Running
EXEC sp_UpdateSyncTaskStatus
    @TaskId = @TaskId,
    @Status = 'Running',
    @StartedAt = GETDATE();

-- 3. [Ejecutar sincronización aquí...]

-- 4. Marcar como Completed
EXEC sp_UpdateSyncTaskStatus
    @TaskId = @TaskId,
    @Status = 'Completed',
    @CompletedAt = GETDATE(),
    @DurationMs = 180000,
    @RowsProcessed = 10000,
    @RowsInserted = 9500,
    @RowsUpdated = 500;
```

### Caso 3: Monitoreo de Tareas

```sql
-- Ver tareas activas
SELECT
    TaskId,
    ProjectId,
    Status,
    Priority,
    RequestedBy,
    DATEDIFF(MINUTE, RequestedAt, GETDATE()) AS MinutesWaiting
FROM SyncTasks
WHERE Status IN ('Pending', 'Running')
ORDER BY Priority DESC, RequestedAt ASC;

-- Tasa de éxito del día
SELECT
    COUNT(*) AS TotalTasks,
    SUM(CASE WHEN Status = 'Completed' THEN 1 ELSE 0 END) AS Completed,
    SUM(CASE WHEN Status = 'Failed' THEN 1 ELSE 0 END) AS Failed,
    CAST(SUM(CASE WHEN Status = 'Completed' THEN 1 ELSE 0 END) AS FLOAT) / COUNT(*) * 100 AS SuccessRate
FROM SyncTasks
WHERE CAST(RequestedAt AS DATE) = CAST(GETDATE() AS DATE);
```

---

## 🔒 Seguridad

### Permisos Recomendados

```sql
-- Usuario para la aplicación (SyncJob Worker Service)
CREATE USER [SyncJobApp] WITHOUT LOGIN;

GRANT SELECT, INSERT, UPDATE ON dbo.SyncTasks TO [SyncJobApp];
GRANT EXECUTE ON dbo.sp_GetPendingSyncTasks TO [SyncJobApp];
GRANT EXECUTE ON dbo.sp_UpdateSyncTaskStatus TO [SyncJobApp];
GRANT EXECUTE ON dbo.sp_MarkNotificationSent TO [SyncJobApp];
GRANT EXECUTE ON dbo.sp_GetTasksToNotify TO [SyncJobApp];

-- Usuario para consultas (Dashboard API)
CREATE USER [DashboardAPI] WITHOUT LOGIN;

GRANT SELECT ON dbo.SyncTasks TO [DashboardAPI];
GRANT INSERT ON dbo.SyncTasks TO [DashboardAPI];  -- Solo para crear tareas
GRANT SELECT ON dbo.vw_PendingSyncTasks TO [DashboardAPI];
GRANT SELECT ON dbo.vw_RecentSyncTasks TO [DashboardAPI];
GRANT SELECT ON dbo.vw_SyncTasksStatsByProject TO [DashboardAPI];
```

---

## 📈 Mantenimiento

### Limpieza Periódica (Ejecutar Semanalmente)

```sql
-- 1. Cancelar tareas pendientes de más de 48 horas
EXEC sp_CancelOldPendingTasks @AgeHours = 48;

-- 2. Eliminar tareas completadas de más de 90 días
DELETE FROM SyncTasks
WHERE Status IN ('Completed', 'Cancelled')
    AND CompletedAt < DATEADD(DAY, -90, GETDATE());

-- 3. Reindexar tabla
ALTER INDEX ALL ON SyncTasks REBUILD;

-- 4. Actualizar estadísticas
UPDATE STATISTICS SyncTasks;
```

### Monitoreo de Performance

```sql
-- Índices fragmentados
SELECT
    object_name(i.object_id) AS TableName,
    i.name AS IndexName,
    ips.avg_fragmentation_in_percent
FROM sys.dm_db_index_physical_stats(DB_ID(), OBJECT_ID('SyncTasks'), NULL, NULL, 'LIMITED') ips
INNER JOIN sys.indexes i ON i.object_id = ips.object_id AND i.index_id = ips.index_id
WHERE ips.avg_fragmentation_in_percent > 30;
```

---

## 🐛 Troubleshooting

### Problema: Tareas atascadas en "Running"

```sql
-- Identificar tareas running de más de 30 minutos
SELECT
    TaskId,
    ProjectId,
    StartedAt,
    DATEDIFF(MINUTE, StartedAt, GETDATE()) AS MinutesRunning
FROM SyncTasks
WHERE Status = 'Running'
    AND DATEDIFF(MINUTE, StartedAt, GETDATE()) > 30;

-- Marcar como Failed si el proceso murió
UPDATE SyncTasks
SET Status = 'Failed',
    ErrorMessage = 'Task stuck in Running state - marked as failed',
    UpdatedAt = GETDATE()
WHERE Status = 'Running'
    AND DATEDIFF(MINUTE, StartedAt, GETDATE()) > 60;
```

### Problema: Demasiadas tareas pendientes

```sql
-- Ver cola de tareas por prioridad
SELECT
    Priority,
    COUNT(*) AS PendingCount,
    MIN(RequestedAt) AS OldestRequest
FROM SyncTasks
WHERE Status = 'Pending'
GROUP BY Priority
ORDER BY Priority DESC;

-- Aumentar prioridad de tareas antiguas
UPDATE SyncTasks
SET Priority = CASE
    WHEN Priority < 10 THEN Priority + 2
    ELSE Priority
END
WHERE Status = 'Pending'
    AND RequestedAt < DATEADD(HOUR, -2, GETDATE());
```

---

## 📞 Soporte

Para preguntas o problemas, contactar:
- **Email:** support@peopleworks.com
- **Documentación:** PeopleWorks Internal Wiki

---

## 📝 Changelog

### Versión 1.0 (2025-12-12)
- ✅ Tabla SyncTasks creada
- ✅ 3 vistas de consulta
- ✅ 6 stored procedures
- ✅ Índices optimizados
- ✅ Trigger de UpdatedAt
- ✅ Scripts de ejemplo

---

**Sistema diseñado por PeopleWorks para clientes con necesidades de sincronización on-demand.** 🚀
