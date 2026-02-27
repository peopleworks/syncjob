# 🚀 Sincronización Incremental - Guía Completa

## 📋 Índice
- [¿Qué es Sincronización Incremental?](#qué-es-sincronización-incremental)
- [Beneficios](#beneficios)
- [Modos de Tracking](#modos-de-tracking)
- [Estrategias de Merge](#estrategias-de-merge)
- [Configuración](#configuración)
- [Ejemplos de Uso](#ejemplos-de-uso)
- [Detección de Eliminados](#detección-de-eliminados)
- [Mejores Prácticas](#mejores-prácticas)
- [Troubleshooting](#troubleshooting)

---

## ¿Qué es Sincronización Incremental?

La **Sincronización Incremental** permite transferir **solo los registros que han cambiado** desde la última ejecución, en lugar de copiar toda la tabla cada vez.

### Comparación: Full Refresh vs Incremental

| Aspecto | Full Refresh | Incremental Sync |
|---------|--------------|------------------|
| **Datos transferidos** | 100% de la tabla | Solo cambios (5-10% típico) |
| **Tiempo de ejecución** | 30 minutos | 2-3 minutos |
| **Carga en origen** | Alta | Mínima |
| **Carga en red** | Alta | Mínima |
| **Uso de CPU/IO** | Alto | Bajo |
| **Primera ejecución** | Full | Full (crea baseline) |
| **Siguientes ejecuciones** | Full (siempre) | Solo cambios |

**Ejemplo real:**
- Tabla con 10 millones de registros
- Solo 50,000 registros cambian por día (0.5%)
- **Full Refresh**: Transfiere 10M registros cada vez (20 GB, 30 min)
- **Incremental**: Transfiere 50K registros (100 MB, 2 min)
- **Ahorro**: 93% menos tiempo, 99.5% menos datos

---

## 🎯 Beneficios

1. ✅ **90%+ reducción** en tiempo de ejecución
2. ✅ **Menor carga** en servidor origen (crítico para SQL 2008 R2)
3. ✅ **Menor tráfico de red** (importante para conexiones remotas)
4. ✅ **Permite ejecuciones más frecuentes** (cada hora vs cada día)
5. ✅ **Detecta y procesa eliminados** (opcional)
6. ✅ **Merge inteligente** (Insert + Update automático)
7. ✅ **Auditoría completa** (tabla de tracking con historial)

---

## 🔍 Modos de Tracking

### 1. Timestamp (Más común)

**Usa una columna DateTime** para rastrear cambios.

```sql
-- Ejemplo de tabla origen
CREATE TABLE Clientes (
    IdCliente INT PRIMARY KEY,
    NombreCompleto NVARCHAR(200),
    SaldoActual DECIMAL(18,2),
    FechaModificacion DATETIME2 DEFAULT GETUTCDATE()  -- ⬅️ Columna de tracking
);

-- Trigger para actualizar FechaModificacion automáticamente
CREATE TRIGGER trg_Clientes_Update
ON Clientes AFTER UPDATE
AS
BEGIN
    UPDATE Clientes
    SET FechaModificacion = GETUTCDATE()
    FROM Clientes c
    INNER JOIN inserted i ON c.IdCliente = i.IdCliente;
END;
```

**Configuración:**
```json
"Incremental": {
  "Enabled": true,
  "Mode": "Timestamp",
  "TrackingColumn": "FechaModificacion",
  "PrimaryKeyColumns": [ "IdCliente" ],
  "MergeStrategy": "Upsert"
}
```

**Query generado automáticamente:**
```sql
-- Primera ejecución (no hay tracking previo)
SELECT * FROM (
    SELECT IdCliente, NombreCompleto, SaldoActual, FechaModificacion
    FROM dbo.VistaCliente
) AS _base_

-- Siguientes ejecuciones (solo cambios desde última sync)
SELECT * FROM (
    SELECT IdCliente, NombreCompleto, SaldoActual, FechaModificacion
    FROM dbo.VistaCliente
) AS _base_
WHERE FechaModificacion > '2025-12-05 10:30:45.1234567'
```

**Ventajas:**
- ✅ Fácil de implementar
- ✅ Funciona en todas las versiones de SQL Server
- ✅ Intuitivo y fácil de debuggear

**Desventajas:**
- ⚠️ Requiere columna DateTime en tabla origen
- ⚠️ Requiere trigger para actualizar la columna
- ⚠️ Puede tener problemas si el reloj del servidor cambia

---

### 2. RowVersion (Más confiable)

**Usa una columna ROWVERSION** (binario monotónico).

```sql
CREATE TABLE Productos (
    IdProducto INT PRIMARY KEY,
    Nombre NVARCHAR(200),
    Precio DECIMAL(18,2),
    Stock INT,
    RowVer ROWVERSION  -- ⬅️ Se actualiza automáticamente en cada INSERT/UPDATE
);
```

**Configuración:**
```json
"Incremental": {
  "Enabled": true,
  "Mode": "RowVersion",
  "TrackingColumn": "RowVer",
  "PrimaryKeyColumns": [ "IdProducto" ],
  "MergeStrategy": "Upsert"
}
```

**Ventajas:**
- ✅ Actualización automática (no requiere triggers)
- ✅ No depende del reloj del servidor
- ✅ Más confiable que Timestamp
- ✅ Muy eficiente (comparación binaria)

**Desventajas:**
- ⚠️ Requiere agregar columna RowVersion a tabla origen

---

### 3. Change Tracking (Avanzado)

**Usa la funcionalidad nativa de SQL Server.**

```sql
-- Habilitar Change Tracking en base de datos
ALTER DATABASE DBCliente
SET CHANGE_TRACKING = ON
(CHANGE_RETENTION = 2 DAYS, AUTO_CLEANUP = ON);

-- Habilitar en tabla específica
ALTER TABLE Clientes
ENABLE CHANGE_TRACKING
WITH (TRACK_COLUMNS_UPDATED = ON);
```

**Configuración:**
```json
"Incremental": {
  "Enabled": true,
  "Mode": "ChangeTracking",
  "PrimaryKeyColumns": [ "IdCliente" ],
  "MergeStrategy": "Full"
}
```

**Ventajas:**
- ✅ No requiere modificar tablas existentes
- ✅ Detecta INSERT, UPDATE, DELETE automáticamente
- ✅ Muy eficiente

**Desventajas:**
- ⚠️ Requiere SQL Server 2008+ (Standard o Enterprise)
- ⚠️ No disponible actualmente (próxima versión de SyncJob)

---

### 4. Change Data Capture (CDC) (Enterprise)

**Captura completa de cambios con valores antes/después.**

```sql
-- Habilitar CDC en base de datos
EXEC sys.sp_cdc_enable_db;

-- Habilitar CDC en tabla
EXEC sys.sp_cdc_enable_table
    @source_schema = 'dbo',
    @source_name = 'Clientes',
    @role_name = NULL;
```

**Ventajas:**
- ✅ Captura valores antiguos y nuevos
- ✅ Historial completo de cambios
- ✅ No impacta performance de aplicaciones

**Desventajas:**
- ⚠️ Requiere Enterprise Edition (o Standard 2016 SP1+)
- ⚠️ Mayor uso de espacio en disco
- ⚠️ No disponible actualmente (próxima versión de SyncJob)

---

## 🔄 Estrategias de Merge

### Insert (Solo nuevos)

Solo inserta registros nuevos. **No actualiza** registros existentes.

```json
"Incremental": {
  "MergeStrategy": "Insert"
}
```

**Uso:** Tablas de eventos, logs, transacciones (immutable data).

---

### Upsert (Insert + Update)

Inserta nuevos + actualiza existentes (basado en Primary Key).

```json
"Incremental": {
  "MergeStrategy": "Upsert",
  "PrimaryKeyColumns": [ "IdCliente" ]
}
```

**SQL generado:**
```sql
MERGE dbo.Cliente_Final AS target
USING dbo.Cliente_Stage AS source
ON target.IdCliente = source.IdCliente
WHEN MATCHED THEN
    UPDATE SET
        NombreCompleto = source.NombreCompleto,
        SaldoActual = source.SaldoActual,
        FechaModificacion = source.FechaModificacion
WHEN NOT MATCHED BY TARGET THEN
    INSERT (IdCliente, NombreCompleto, SaldoActual, FechaModificacion)
    VALUES (source.IdCliente, source.NombreCompleto, source.SaldoActual, source.FechaModificacion);
```

**Uso:** Tablas maestras (clientes, productos, empleados).

---

### Full (Insert + Update + Delete)

Sincronización completa: inserta, actualiza **y elimina**.

```json
"Incremental": {
  "MergeStrategy": "Full",
  "PrimaryKeyColumns": [ "IdVenta" ],
  "DeleteDetection": {
    "Enabled": true,
    "Mode": "SoftDelete",
    "SoftDeleteColumn": "Estado",
    "SoftDeleteValue": "CANCELADO"
  }
}
```

**Uso:** Replica exacta de tabla origen.

---

## ⚙️ Configuración

### Configuración Mínima (Timestamp)

```json
{
  "IncrementalSync": {
    "Source": {
      "ConnectionString": "Server=SQL2008;Database=DB;User Id=...;Password=...;",
      "Query": "SELECT Id, Nombre, FechaMod FROM Tabla"
    },
    "Destination": {
      "ConnectionString": "Server=SQL2022;Database=DB;User Id=...;Password=...;",
      "StageTable": "dbo.Tabla_Stage",
      "FinalTable": "dbo.Tabla_Final"
    },
    "ColumnMappings": [
      { "Source": "Id", "Dest": "Id" },
      { "Source": "Nombre", "Dest": "Nombre" },
      { "Source": "FechaMod", "Dest": "FechaMod" }
    ],
    "Options": {
      "BatchSize": 10000,
      "MaxDegreeOfParallelism": 4,
      "BulkCopyTimeoutSeconds": 0,
      "KeepIdentity": true,
      "MinRowThresholdToCommit": 0
    },
    "Incremental": {
      "Enabled": true,
      "Mode": "Timestamp",
      "TrackingColumn": "FechaMod",
      "PrimaryKeyColumns": [ "Id" ],
      "MergeStrategy": "Upsert"
    }
  }
}
```

### Configuración Completa (con Delete Detection)

```json
{
  "IncrementalSync_Full": {
    "Source": {
      "ConnectionString": "Server=SQL2008;Database=DBVentas;User Id=...;Password=...;",
      "Query": "SELECT IdVenta, Monto, Estado, FechaMod FROM Ventas"
    },
    "Destination": {
      "ConnectionString": "Server=SQL2022;Database=DBAnalytics;User Id=...;Password=...;",
      "StageTable": "dbo.Ventas_Stage",
      "FinalTable": "dbo.Ventas_Final"
    },
    "ColumnMappings": [
      { "Source": "IdVenta", "Dest": "IdVenta" },
      { "Source": "Monto", "Dest": "Monto" },
      { "Source": "Estado", "Dest": "Estado" },
      { "Source": "FechaMod", "Dest": "FechaMod" }
    ],
    "Options": {
      "BatchSize": 20000,
      "MaxDegreeOfParallelism": 4,
      "BulkCopyTimeoutSeconds": 0,
      "KeepIdentity": true,
      "MinRowThresholdToCommit": 0
    },
    "Incremental": {
      "Enabled": true,
      "Mode": "Timestamp",
      "TrackingColumn": "FechaMod",
      "TrackingTable": "dbo.SyncJobTracking",
      "JobIdentifier": "Ventas_SQL2008_to_SQL2022",
      "ForceFullRefresh": false,
      "PrimaryKeyColumns": [ "IdVenta" ],
      "MergeStrategy": "Full",
      "DeleteDetection": {
        "Enabled": true,
        "Mode": "SoftDelete",
        "SoftDeleteColumn": "Estado",
        "SoftDeleteValue": "CANCELADO"
      }
    }
  }
}
```

---

## 💻 Ejemplos de Uso

### 1. Inicializar Tracking Table

**Primera vez que usas incremental sync:**

```bash
SyncJob.exe run -c appsettings.json -s IncrementalSync --init-tracking
```

Esto crea la tabla `dbo.SyncJobTracking` en el destino:

```sql
CREATE TABLE dbo.SyncJobTracking (
    JobIdentifier NVARCHAR(255) NOT NULL PRIMARY KEY,
    LastSyncTime DATETIME2 NOT NULL,
    LastRowVersion VARBINARY(8) NULL,
    LastChangeTrackingVersion BIGINT NULL,
    RowsProcessed BIGINT NOT NULL,
    RowsInserted BIGINT NOT NULL,
    RowsUpdated BIGINT NOT NULL,
    RowsDeleted BIGINT NOT NULL,
    Success BIT NOT NULL,
    ErrorMessage NVARCHAR(MAX) NULL,
    CreatedAt DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
    UpdatedAt DATETIME2 NOT NULL DEFAULT GETUTCDATE()
);
```

---

### 2. Primera Ejecución (Full Refresh)

```bash
SyncJob.exe run -c appsettings.json -s IncrementalSync
```

**Output:**
```
Modo Incremental: Job ID = SQL2008_DBCliente_VistaCliente_TO_SQL2022_DBPropia_dbo_Cliente_Final
Primera sincronización (no hay tracking previo)
Leyendo datos del origen...
Total filas origen: 1,000,000
Cargando Stage en paralelo...
Commit Stage -> Final...
Estado de tracking guardado: 2025-12-05 11:00:00
=== Sync OK ===
```

---

### 3. Segunda Ejecución (Incremental)

```bash
SyncJob.exe run -c appsettings.json -s IncrementalSync
```

**Output:**
```
Modo Incremental: Job ID = SQL2008_DBCliente_VistaCliente_TO_SQL2022_DBPropia_dbo_Cliente_Final
Última sincronización: 2025-12-05 11:00:00
Filas anteriores: 1,000,000 (1,000,000 ins, 0 upd, 0 del)
Leyendo cambios del origen (incremental)...
Total filas modificadas: 5,432
Cargando Stage en paralelo...
Commit Stage -> Final...
MERGE completed: 234 inserted, 5,198 updated
Estado de tracking guardado: 2025-12-05 12:15:30
=== Sync OK ===
```

**Query ejecutado automáticamente:**
```sql
SELECT * FROM (
    SELECT IdCliente, NombreCompleto, SaldoActual, FechaModificacion
    FROM dbo.VistaCliente
) AS _base_
WHERE FechaModificacion > '2025-12-05 11:00:00.0000000'
```

---

### 4. Forzar Full Refresh

**Útil cuando quieres resetear y sincronizar todo:**

```bash
SyncJob.exe run -c appsettings.json -s IncrementalSync --full-refresh
```

**Output:**
```
Modo Full Refresh activado (ignorando tracking incremental)
Leyendo datos del origen...
Total filas origen: 1,005,432
...
```

---

### 5. Validar Configuración

```bash
SyncJob.exe validate -c appsettings.json -s IncrementalSync
```

Verifica:
- ✅ Conectividad a origen y destino
- ✅ Existencia de tracking table
- ✅ Columnas de mapeo
- ✅ Tracking column existe
- ✅ Primary keys válidos

---

## 🗑️ Detección de Eliminados

### Soft Delete

**Escenario:** Registros marcados como eliminados (columna `IsDeleted`, `Estado`, etc.)

```json
"DeleteDetection": {
  "Enabled": true,
  "Mode": "SoftDelete",
  "SoftDeleteColumn": "Estado",
  "SoftDeleteValue": "CANCELADO"
}
```

**Lógica:**
1. Lee registros del origen donde `Estado = 'CANCELADO'`
2. Elimina esos registros del destino (basado en PK)

**Query origen:**
```sql
SELECT IdVenta, Monto, Estado, FechaMod
FROM Ventas
WHERE Estado = 'CANCELADO'
  AND FechaMod > @LastSyncTime
```

---

### Comparison Delete

**Escenario:** Detectar registros que ya no existen en origen.

```json
"DeleteDetection": {
  "Enabled": true,
  "Mode": "Comparison"
}
```

**Lógica:**
1. Crea tabla temporal con PKs del origen
2. Elimina del destino los registros cuyo PK no existe en temp table

**SQL generado:**
```sql
CREATE TABLE #SourcePKs (IdVenta NVARCHAR(255));

INSERT INTO #SourcePKs
SELECT CAST(IdVenta AS NVARCHAR(255)) FROM Ventas;

DELETE dest
FROM dbo.Ventas_Final dest
LEFT JOIN #SourcePKs tmp ON dest.IdVenta = tmp.IdVenta
WHERE tmp.IdVenta IS NULL;
```

⚠️ **CUIDADO:** Costoso para tablas grandes (> 10M registros).

---

## 🎯 Mejores Prácticas

### 1. Agregar Índice en Tracking Column

```sql
-- Mejora drásticamente el performance del filtro incremental
CREATE INDEX IX_Clientes_FechaModificacion
ON Clientes (FechaModificacion);
```

**Impacto:**
- Sin índice: 15 segundos
- Con índice: 0.5 segundos

---

### 2. Ajustar MinRowThresholdToCommit

```json
"Options": {
  "MinRowThresholdToCommit": 0  // ⬅️ Importante para incremental
}
```

**Razón:** En modo incremental, es normal tener pocas filas (incluso 0 si no hay cambios).

---

### 3. Usar RowVersion en lugar de Timestamp

```sql
-- Agregar columna RowVersion
ALTER TABLE Productos
ADD RowVer ROWVERSION;
```

**Ventajas:**
- No requiere triggers
- Más confiable
- Más rápido

---

### 4. Monitorear Tabla de Tracking

```sql
-- Ver historial de ejecuciones
SELECT
    JobIdentifier,
    LastSyncTime,
    RowsProcessed,
    RowsInserted,
    RowsUpdated,
    RowsDeleted,
    Success,
    UpdatedAt
FROM dbo.SyncJobTracking
ORDER BY UpdatedAt DESC;
```

---

### 5. Estrategia de Pruebas

```bash
# 1. Dry-run primero
SyncJob.exe run -c config.json -s Job --dry-run

# 2. Probar con TOP
SyncJob.exe run -c config.json -s Job --top 1000

# 3. Full refresh inicial
SyncJob.exe run -c config.json -s Job --full-refresh

# 4. Incremental normal
SyncJob.exe run -c config.json -s Job
```

---

## 🔧 Troubleshooting

### Problema: "Tracking column not found in source data"

**Causa:** La columna especificada en `TrackingColumn` no existe en el query origen.

**Solución:**
```json
// Asegúrate de incluir la columna en el SELECT
"Query": "SELECT Id, Nombre, FechaModificacion FROM Tabla"
"TrackingColumn": "FechaModificacion"  // ⬅️ Debe coincidir
```

---

### Problema: "PrimaryKeyColumns is required for MERGE"

**Causa:** No especificaste Primary Keys y usas MergeStrategy Upsert/Full.

**Solución:**
```json
"Incremental": {
  "MergeStrategy": "Upsert",
  "PrimaryKeyColumns": [ "IdCliente" ]  // ⬅️ Requerido
}
```

---

### Problema: Siempre hace full refresh

**Causa:** No se guardó el estado anterior o `ForceFullRefresh = true`.

**Verificar:**
```sql
SELECT * FROM dbo.SyncJobTracking WHERE JobIdentifier = 'tu_job_id';
```

**Solución:**
- Verifica que `Success = 1` en la última ejecución
- Verifica que `ForceFullRefresh = false` en config
- Ejecuta `--init-tracking` si la tabla no existe

---

### Problema: Performance lento en filtro incremental

**Causa:** Falta índice en tracking column.

**Solución:**
```sql
CREATE INDEX IX_Tabla_TrackingColumn ON Tabla (TrackingColumn);
```

---

## 📊 Métricas y Monitoreo

### Ver Estado del Job

```sql
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
WHERE JobIdentifier LIKE '%Cliente%'
ORDER BY LastSyncTime DESC;
```

### Resetear Tracking (forzar full refresh en próxima ejecución)

```sql
DELETE FROM dbo.SyncJobTracking
WHERE JobIdentifier = 'SQL2008_DBCliente_VistaCliente_TO_SQL2022_DBPropia_dbo_Cliente_Final';
```

---

## 🚀 Próximas Mejoras

- [ ] **Change Tracking** mode implementation
- [ ] **CDC** mode implementation
- [ ] **Automatic schema sync** (detectar cambios de columnas)
- [ ] **Partitioned sync** (por rangos de fechas/IDs)
- [ ] **Parallel incremental sync** (múltiples jobs concurrentes)
- [ ] **Metrics export** (Prometheus, Application Insights)
- [ ] **Web UI** para configuración y monitoreo

---

## 📞 Soporte

Para preguntas o issues:
1. Revisar esta documentación
2. Ejecutar con `--dry-run` para validar
3. Verificar logs en `logs/SyncJob_yyyyMMdd.log`
4. Consultar tabla `dbo.SyncJobTracking`

---

**¡Disfruta de sincronizaciones ultra-rápidas! ⚡**
