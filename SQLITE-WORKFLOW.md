# SyncJob - SQLite Workflow Guide

Esta guía explica cómo usar SyncJob con la nueva funcionalidad de configuración basada en SQLite.

## Índice

1. [Conceptos](#conceptos)
2. [Workflow Básico](#workflow-básico)
3. [Gestión de Conexiones](#gestión-de-conexiones)
4. [Gestión de Configuraciones](#gestión-de-configuraciones)
5. [Column Mappings](#column-mappings)
6. [Ejecutar Sincronizaciones](#ejecutar-sincronizaciones)
7. [Historial de Ejecuciones](#historial-de-ejecuciones)
8. [Gestión de la Base de Datos](#gestión-de-la-base-de-datos)

---

## Conceptos

### ¿Qué cambió?

**Antes (v1.x - JSON):**
- Configuración en archivos JSON (appsettings.json)
- Difícil de gestionar múltiples configuraciones
- No hay historial de ejecuciones
- Sin versionado de configuraciones

**Ahora (v2.0 - SQLite):**
- ✅ Configuraciones almacenadas en base de datos SQLite local
- ✅ Gestión centralizada de conexiones reutilizables
- ✅ Historial completo de ejecuciones con métricas
- ✅ Versionado y auditoría integrados
- ✅ Backup y restauración de configuraciones

### Modelo de Datos

```
Connections (conexiones reutilizables)
    ├── source-001
    ├── source-002
    └── dest-001

Configurations (sync jobs)
    ├── daily-users-sync
    │   └── ColumnMappings (1..N)
    └── hourly-orders-sync
        └── ColumnMappings (1..N)

ExecutionHistory (logs de ejecuciones)
    ├── exec-001 (daily-users-sync, 2025-01-10 08:00)
    ├── exec-002 (daily-users-sync, 2025-01-11 08:00)
    └── exec-003 (hourly-orders-sync, 2025-01-11 09:00)
```

---

## Workflow Básico

### 1. Crear Conexiones

Primero, crea las conexiones a los servidores SQL que vas a usar:

```powershell
# Conexión ORIGEN
SyncJob.exe connection add `
    --connection-id "prod-source" `
    --display-name "Servidor Producción (Origen)" `
    --server "prod-sql-01.company.local" `
    --database "SalesDB" `
    --username "syncuser" `
    --password "Password123!" `
    --trust-cert

# Conexión DESTINO
SyncJob.exe connection add `
    --connection-id "dwh-dest" `
    --display-name "Data Warehouse (Destino)" `
    --server "dwh-sql-01.company.local" `
    --database "SalesDataWarehouse" `
    --username "dwhuser" `
    --password "Password456!" `
    --trust-cert
```

**Nota:** Las contraseñas se encriptan automáticamente usando DPAPI (Windows Data Protection).

### 2. Crear Configuración

Crea una configuración de sincronización que use esas conexiones:

```powershell
SyncJob.exe config create `
    --config-id "daily-sales-sync" `
    --display-name "Sincronización Diaria de Ventas" `
    --description "Sincroniza ventas del día anterior al DWH" `
    --source-connection "prod-source" `
    --dest-connection "dwh-dest" `
    --source-query "SELECT OrderId, CustomerId, OrderDate, TotalAmount FROM Orders WHERE OrderDate >= DATEADD(day, -1, GETDATE())" `
    --dest-stage "staging.Orders_Stage" `
    --dest-final "dbo.Orders" `
    --batch-size 10000 `
    --maxdop 4
```

### 3. Agregar Column Mappings

Define cómo mapean las columnas origen → destino:

```powershell
# Primary Key
SyncJob.exe mapping add --config-id "daily-sales-sync" --source "OrderId" --dest "OrderId" --is-pk

# Otras columnas
SyncJob.exe mapping add --config-id "daily-sales-sync" --source "CustomerId" --dest "CustomerId"
SyncJob.exe mapping add --config-id "daily-sales-sync" --source "OrderDate" --dest "OrderDate"
SyncJob.exe mapping add --config-id "daily-sales-sync" --source "TotalAmount" --dest "TotalAmount"
```

### 4. Verificar Configuración

Antes de ejecutar, revisa la configuración:

```powershell
SyncJob.exe config show --config-id "daily-sales-sync"
SyncJob.exe mapping list --config-id "daily-sales-sync"
```

### 5. Ejecutar (Dry-Run)

Prueba sin escribir datos:

```powershell
SyncJob.exe run-db daily-sales-sync --dry-run
```

### 6. Ejecutar (Real)

Ejecuta la sincronización real:

```powershell
SyncJob.exe run-db daily-sales-sync
```

### 7. Ver Historial

```powershell
# Listar ejecuciones
SyncJob.exe history list

# Ver detalles de una ejecución
SyncJob.exe history show --execution-id <ID>

# Estadísticas
SyncJob.exe history stats
```

---

## Gestión de Conexiones

### Listar Conexiones

```powershell
SyncJob.exe connection list
```

### Probar Conexión

```powershell
SyncJob.exe connection test --connection-id "prod-source"
```

### Eliminar Conexión

```powershell
SyncJob.exe connection delete --connection-id "prod-source"
```

**Nota:** No se puede eliminar una conexión si está siendo usada por alguna configuración.

---

## Gestión de Configuraciones

### Listar Configuraciones

```powershell
SyncJob.exe config list
```

Salida ejemplo:
```
ConfigId             DisplayName                   Tracking   Status   Updated
daily-sales-sync     Sincronización Diaria         Snapshot   Active   2025-01-10 14:30:00
hourly-orders-sync   Sincronización Horaria        None       Active   2025-01-09 09:15:00
```

### Ver Detalles

```powershell
SyncJob.exe config show --config-id "daily-sales-sync"
```

### Eliminar Configuración

```powershell
SyncJob.exe config delete --config-id "daily-sales-sync"
```

---

## Column Mappings

### Agregar Mapping

```powershell
# Mapping simple
SyncJob.exe mapping add --config-id "my-sync" --source "Id" --dest "Id"

# Mapping con Primary Key
SyncJob.exe mapping add --config-id "my-sync" --source "Id" --dest "Id" --is-pk

# Mapping con orden específico
SyncJob.exe mapping add --config-id "my-sync" --source "Name" --dest "Name" --ordinal 1
```

### Listar Mappings

```powershell
SyncJob.exe mapping list --config-id "my-sync"
```

### Eliminar Mapping

```powershell
SyncJob.exe mapping remove --config-id "my-sync" --mapping-id 1
```

### Limpiar Todos los Mappings

```powershell
SyncJob.exe mapping clear --config-id "my-sync"
```

---

## Ejecutar Sincronizaciones

### Comando Básico

```powershell
SyncJob.exe run-db <CONFIG_ID>
```

### Opciones Comunes

```powershell
# Dry-run (no escribe)
SyncJob.exe run-db my-sync --dry-run

# Modo directo (sin stage, directo a final)
SyncJob.exe run-db my-sync --direct

# Append (no trunca tabla final)
SyncJob.exe run-db my-sync --append

# Full refresh (ignora tracking incremental)
SyncJob.exe run-db my-sync --full-refresh

# Limitar filas (testing)
SyncJob.exe run-db my-sync --top 1000

# Overrides de performance
SyncJob.exe run-db my-sync --batch-size 5000 --maxdop 8

# Base de datos custom
SyncJob.exe run-db my-sync --db "C:\Data\custom-syncjob.db"
```

### Logging

```powershell
# Log nivel Debug
SyncJob.exe run-db my-sync --log-level Debug

# Log a archivo específico
SyncJob.exe run-db my-sync --log-file "C:\Logs\sync-2025-01-10.log"

# Log en formato JSON
SyncJob.exe run-db my-sync --json-log

# Sin salida a consola
SyncJob.exe run-db my-sync --quiet --log-file "C:\Logs\sync.log"
```

### Ejemplos Completos

**Sincronización completa con logging detallado:**
```powershell
SyncJob.exe run-db daily-sales-sync `
    --log-level Debug `
    --log-file "C:\Logs\sales-sync.log"
```

**Sincronización incremental con append:**
```powershell
SyncJob.exe run-db hourly-orders-sync `
    --append `
    --batch-size 5000
```

**Testing con datos limitados:**
```powershell
SyncJob.exe run-db daily-sales-sync `
    --dry-run `
    --top 100 `
    --log-level Trace
```

---

## Historial de Ejecuciones

### Listar Ejecuciones

```powershell
# Últimas 20 ejecuciones
SyncJob.exe history list

# Filtrar por configuración
SyncJob.exe history list --config-id "daily-sales-sync"

# Últimas 50
SyncJob.exe history list --limit 50
```

### Ver Detalles de Ejecución

```powershell
SyncJob.exe history show --execution-id a1b2c3d4e5f6
```

Salida ejemplo:
```
Execution ID: a1b2c3d4e5f6
Config ID: daily-sales-sync
Status: Success
Duration: 45.3 seconds

Started: 2025-01-10 08:00:15
Ended: 2025-01-10 08:01:00

Rows Read: 125,430
Rows Inserted: 125,430
Rows Updated: 0
Rows Deleted: 0

Host: SERVER-01
Triggered By: DOMAIN\user
```

### Estadísticas

```powershell
SyncJob.exe history stats
```

### Limpiar Historial Antiguo

```powershell
# Eliminar registros más antiguos de 30 días
SyncJob.exe history clear --days 30
```

---

## Gestión de la Base de Datos

### Información de la DB

```powershell
SyncJob.exe db info
```

Salida ejemplo:
```
Database Path: C:\Users\user\AppData\Local\SyncJob\SyncJob.db
Database Size: 2.3 MB
Connections: 5
Configurations: 12
Total Executions: 247
```

### Backup

```powershell
# Backup automático
SyncJob.exe db backup

# Backup a ubicación específica
SyncJob.exe db backup --output "C:\Backups\syncjob-2025-01-10.db"
```

### Restaurar

```powershell
SyncJob.exe db restore --backup "C:\Backups\syncjob-2025-01-10.db"
```

### Compactar (Vacuum)

```powershell
SyncJob.exe db vacuum
```

### Limpiar Registros Antiguos

```powershell
# Limpiar ejecuciones + snapshots más antiguos de 90 días
SyncJob.exe db cleanup --days 90
```

---

## Migración desde JSON (v1.x)

Si tienes configuraciones en JSON del sistema antiguo, puedes importarlas:

**PENDIENTE DE IMPLEMENTAR** - Próxima versión incluirá:
```powershell
SyncJob.exe config import --json-file "appsettings.json" --section "MySync"
```

Por ahora, debes crear las configuraciones manualmente usando los comandos de arriba.

---

## Programación de Tareas (Task Scheduler)

Para ejecutar sincronizaciones automáticamente, usa Windows Task Scheduler:

### Ejemplo de Acción

```
Programa: C:\Apps\SyncJob\SyncJob.exe
Argumentos: run-db daily-sales-sync --quiet --log-file "C:\Logs\sales-sync.log"
Iniciar en: C:\Apps\SyncJob
```

### Ejemplo PowerShell Script

```powershell
# sync-all-daily.ps1
$exe = "C:\Apps\SyncJob\SyncJob.exe"
$logDir = "C:\Logs\SyncJob"
$date = Get-Date -Format "yyyy-MM-dd"

# Ejecutar todas las sincronizaciones diarias
@("daily-sales-sync", "daily-customers-sync", "daily-inventory-sync") | ForEach-Object {
    $logFile = Join-Path $logDir "$_-$date.log"
    Write-Host "Ejecutando: $_" -ForegroundColor Cyan

    & $exe run-db $_ --log-file $logFile --quiet

    if ($LASTEXITCODE -eq 0) {
        Write-Host "✓ $_ OK" -ForegroundColor Green
    } else {
        Write-Host "✗ $_ FAILED" -ForegroundColor Red
    }
}
```

---

## Troubleshooting

### La base de datos SQLite no existe

Por defecto, SyncJob crea la DB en:
```
%LOCALAPPDATA%\SyncJob\SyncJob.db
```

Para usar una ubicación custom, siempre especifica `--db`:
```powershell
SyncJob.exe config list --db "C:\MyData\sync.db"
```

### Error "Configuration not found"

```powershell
# Listar todas las configuraciones
SyncJob.exe config list

# Verificar ID exacto
SyncJob.exe config show --config-id "my-sync"
```

### Error "Connection failed"

```powershell
# Probar conexión
SyncJob.exe connection test --connection-id "source-001"

# Ver detalles
SyncJob.exe connection list
```

### Contraseña incorrecta

Las contraseñas están encriptadas con DPAPI y son específicas del usuario de Windows actual. Si cambias de usuario, necesitas recrear las conexiones.

---

## Best Practices

1. **Usa IDs descriptivos**: `prod-sql-sales`, no `conn-001`
2. **Nombra las configuraciones claramente**: `daily-sales-to-dwh`, no `sync-1`
3. **Siempre haz dry-run primero**: `--dry-run` antes de ejecutar en producción
4. **Monitorea el historial**: Revisa `history stats` regularmente
5. **Haz backups periódicos**: `db backup` antes de cambios grandes
6. **Limpia historial antiguo**: `history clear --days 90` mensualmente
7. **Usa logging adecuado**: `--log-level Info` en prod, `Debug` en troubleshooting

---

## Próximas Versiones

Features planeadas para v2.1+:

- [ ] Importar configuraciones desde JSON (legacy)
- [ ] Exportar configuraciones a JSON
- [ ] Templates de configuraciones
- [ ] Validación de schemas automática
- [ ] Notificaciones (email, Slack, Teams)
- [ ] Dashboard web para monitoreo
- [ ] Soporte para múltiples bases de datos (PostgreSQL, MySQL)

---

## Soporte

Para reportar bugs o solicitar features:
- GitHub Issues: (URL del repo)
- Email: support@company.com

---

**Versión:** 2.0.0
**Última Actualización:** 2025-01-10
