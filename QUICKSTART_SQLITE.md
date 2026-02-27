# 🚀 SyncJob 2.0 - Guía de Inicio Rápido (SQLite Mode)

## 🎯 ¿Qué es nuevo en SyncJob 2.0?

¡Bienvenido al futuro de SyncJob! Ahora puedes gestionar todas tus sincronizaciones desde una **base de datos SQLite local** con comandos CLI super poderosos.

### **¿Por qué SQLite?**
✅ **No modifica BD origen** - Solo necesitas permisos de SELECT
✅ **Configuración centralizada** - Todo en `SyncJob.db`
✅ **CLI Interactivo** - Crea configs sin editar JSON
✅ **Historial completo** - Tracking de todas las ejecuciones
✅ **Portable** - Un archivo, toda tu configuración
✅ **Snapshot tracking** - Incremental SIN modificar origen

---

## 📋 Tabla de Contenidos
1. [Instalación y Setup](#instalación-y-setup)
2. [Primeros Pasos](#primeros-pasos)
3. [Workflow Completo](#workflow-completo)
4. [Comandos Disponibles](#comandos-disponibles)
5. [Ejemplos Prácticos](#ejemplos-prácticos)

---

## 🔧 Instalación y Setup

### **1. Ubicación de la Base de Datos**

La primera vez que ejecutes cualquier comando, SyncJob creará automáticamente:

```
Windows: C:\Users\<tu-usuario>\AppData\Local\SyncJob\SyncJob.db
Linux:   ~/.local/share/SyncJob/SyncJob.db
Mac:     ~/Library/Application Support/SyncJob/SyncJob.db
```

### **2. Verificar Instalación**

```bash
SyncJob.exe db info
```

**Output esperado:**
```
╔══════════════════════════════╗
║ SyncJob Database Information ║
╚══════════════════════════════╝

Path:            C:\Users\...\SyncJob.db
Size:            0.05 MB
Schema Version:  1.0.0
Configurations:  0
Connections:     0
Executions:      0
```

✅ ¡Si ves esto, todo está funcionando!

---

## 🚀 Primeros Pasos

### **Paso 1: Crear Conexiones**

Primero necesitas definir las conexiones a tus servidores SQL.

#### **Opción A: Modo Interactivo** (Recomendado)

```bash
SyncJob.exe connection add SQL2008_Origen --interactive
```

**Te preguntará:**
```
Nombre descriptivo: [SQL2008_Origen]  → (enter para usar default)
Servidor SQL: SERVIDOR2008
Base de datos: DBCliente
Usuario (opcional, enter para Windows Auth): sa
Password: ********
Confiar en certificado del servidor? (y/n): y
```

✅ Conexión creada!

#### **Opción B: Modo No-Interactivo**

```bash
SyncJob.exe connection add SQL2022_Dest \
  --server SERVIDOR2022 \
  --database DBAnalytics \
  --username sa \
  --password MiPassword123 \
  --trust-cert
```

### **Paso 2: Verificar Conexiones**

```bash
# Listar todas
SyncJob.exe connection list

# Probar una conexión
SyncJob.exe connection test SQL2008_Origen
```

**Output esperado:**
```
✓ Conexión exitosa

Servidor:      SERVIDOR2008
Base de datos: DBCliente
Versión SQL:   Microsoft SQL Server 2008 R2
```

### **Paso 3: Crear una Configuración**

```bash
SyncJob.exe config create ClientesSync --interactive
```

**Te preguntará:**
```
Nombre descriptivo: [ClientesSync] → Sincronización de Clientes
Descripción (opcional): Sync de tabla Clientes desde SQL 2008 a 2022

Conexiones disponibles:
╔════════════════╦══════════╦════════════════╦═══════════╗
║ ID             ║ Nombre   ║ Servidor       ║ Base      ║
╠════════════════╬══════════╬════════════════╬═══════════╣
║ SQL2008_Origen ║ ...      ║ SERVIDOR2008   ║ DBCliente ║
║ SQL2022_Dest   ║ ...      ║ SERVIDOR2022   ║ DBAnalytics║
╚════════════════╩══════════╩════════════════╩═══════════╝

ID de conexión origen: SQL2008_Origen
Query SQL origen: SELECT IdCliente, Nombre, Saldo FROM Clientes
ID de conexión destino: SQL2022_Dest
Tabla Stage (opcional): dbo.Clientes_Stage
Tabla Final: dbo.Clientes_Final

Modo de tracking:
> Snapshot
  Timestamp
  RowVersion
  None

Estrategia de merge:
  Insert
> Upsert
  Full

BatchSize: [10000]
MaxDOP: [4]
```

✅ ¡Configuración creada!

---

## 📖 Workflow Completo

### **Escenario: Sincronizar tabla Clientes**

```bash
# 1. Crear conexiones
SyncJob.exe connection add SQL2008 --interactive
SyncJob.exe connection add SQL2022 --interactive

# 2. Crear configuración
SyncJob.exe config create ClientesSync --interactive

# 3. Agregar mappings de columnas
# (Próximamente: mapping add)
# Por ahora, los mappings se crean automáticamente basados en el query

# 4. Ver la configuración
SyncJob.exe config show ClientesSync

# 5. ¡Ejecutar! (Próximamente)
# SyncJob.exe run ClientesSync

# 6. Ver historial (Próximamente)
# SyncJob.exe history list ClientesSync
```

---

## 📚 Comandos Disponibles

### **🔧 Gestión de Conexiones**

```bash
# Agregar conexión
SyncJob.exe connection add <id> --interactive
SyncJob.exe connection add <id> --server <srv> --database <db> --username <usr> --password <pwd>

# Listar conexiones
SyncJob.exe connection list
SyncJob.exe connection list --active-only

# Probar conexión
SyncJob.exe connection test <id>

# Eliminar conexión
SyncJob.exe connection delete <id>
SyncJob.exe connection delete <id> --force  # Sin confirmación
```

### **⚙️ Gestión de Configuraciones**

```bash
# Crear configuración
SyncJob.exe config create <config-id> --interactive
SyncJob.exe config create <config-id> \
  --source-conn <id> \
  --source-query "SELECT..." \
  --dest-conn <id> \
  --dest-final <tabla> \
  --tracking-mode Snapshot

# Listar configuraciones
SyncJob.exe config list
SyncJob.exe config list --active-only
SyncJob.exe config list --format json
SyncJob.exe config list --format csv

# Ver detalles
SyncJob.exe config show <config-id>

# Eliminar configuración
SyncJob.exe config delete <config-id>
SyncJob.exe config delete <config-id> --force
```

### **💾 Gestión de Base de Datos**

```bash
# Ver información
SyncJob.exe db info

# Crear backup
SyncJob.exe db backup --output backup_2025_12_05.db
SyncJob.exe db backup  # Auto-genera nombre con timestamp

# Restaurar backup
SyncJob.exe db restore --file backup.db
SyncJob.exe db restore --file backup.db --force

# Limpiar registros antiguos
SyncJob.exe db cleanup --older-than 90  # Elimina ejecuciones > 90 días
SyncJob.exe db cleanup --older-than 30 --force

# Compactar base de datos
SyncJob.exe db vacuum
```

---

## 💡 Ejemplos Prácticos

### **Ejemplo 1: Setup Inicial Completo**

```bash
# 1. Ver estado de la BD
SyncJob.exe db info

# 2. Agregar conexión origen (SQL 2008 R2)
SyncJob.exe connection add SQL2008_Prod \
  --server SERVIDOR2008 \
  --database DBCliente \
  --username sa \
  --password MyPassword123 \
  --trust-cert

# 3. Agregar conexión destino (SQL 2022)
SyncJob.exe connection add SQL2022_Analytics \
  --server SERVIDOR2022 \
  --database DBAnalytics \
  --username sa \
  --password MyPassword456 \
  --trust-cert

# 4. Probar conexiones
SyncJob.exe connection test SQL2008_Prod
SyncJob.exe connection test SQL2022_Analytics

# 5. Ver todas las conexiones
SyncJob.exe connection list
```

### **Ejemplo 2: Crear Múltiples Configuraciones**

```bash
# Config 1: Clientes
SyncJob.exe config create ClientesSync \
  --display-name "Sincronización de Clientes" \
  --source-conn SQL2008_Prod \
  --source-query "SELECT IdCliente, Nombre, Email, Saldo, FechaMod FROM Clientes" \
  --dest-conn SQL2022_Analytics \
  --dest-stage dbo.Clientes_Stage \
  --dest-final dbo.Clientes_Final \
  --tracking-mode Snapshot \
  --merge-strategy Upsert

# Config 2: Ventas
SyncJob.exe config create VentasSync \
  --display-name "Sincronización de Ventas" \
  --source-conn SQL2008_Prod \
  --source-query "SELECT IdVenta, IdCliente, Monto, Fecha FROM Ventas WHERE Fecha >= '2024-01-01'" \
  --dest-conn SQL2022_Analytics \
  --dest-final dbo.Ventas_Final \
  --tracking-mode Snapshot \
  --merge-strategy Insert

# Config 3: Productos
SyncJob.exe config create ProductosSync \
  --display-name "Sincronización de Productos" \
  --source-conn SQL2008_Prod \
  --source-query "SELECT IdProducto, Nombre, Precio, Stock, RowVer FROM Productos" \
  --dest-conn SQL2022_Analytics \
  --dest-final dbo.Productos_Final \
  --tracking-mode RowVersion \
  --tracking-column RowVer \
  --merge-strategy Upsert

# Ver todas las configuraciones
SyncJob.exe config list
```

### **Ejemplo 3: Gestión de Backups**

```bash
# Crear backup antes de cambios importantes
SyncJob.exe db backup --output backup_antes_migracion.db

# Hacer cambios...
# ...

# Si algo sale mal, restaurar
SyncJob.exe db restore --file backup_antes_migracion.db --force

# Programar backups regulares (con cron/Task Scheduler)
# Cada día a las 2am
# 0 2 * * * cd /path/to/syncjob && ./SyncJob.exe db backup --output backups/daily_$(date +\%Y\%m\%d).db
```

### **Ejemplo 4: Mantenimiento de la BD**

```bash
# Limpiar ejecuciones de más de 90 días
SyncJob.exe db cleanup --older-than 90

# Compactar después de limpieza
SyncJob.exe db vacuum

# Ver espacio ahorrado
SyncJob.exe db info
```

---

## 🎯 Próximos Comandos (Coming Soon)

Los siguientes comandos están en desarrollo:

```bash
# MAPPING commands (agregar/listar/eliminar mappings de columnas)
SyncJob.exe mapping add <config> --interactive
SyncJob.exe mapping add <config> --source Col1 --dest Col1 --primary-key
SyncJob.exe mapping list <config>
SyncJob.exe mapping remove <config> --column Col1

# RUN command (ejecutar usando config de SQLite)
SyncJob.exe run <config-id>
SyncJob.exe run <config-id> --dry-run
SyncJob.exe run <config-id> --full-refresh

# SNAPSHOT commands (snapshot-based tracking)
SyncJob.exe snapshot create <config>
SyncJob.exe snapshot diff <config>
SyncJob.exe snapshot stats <config>

# HISTORY commands (ver historial de ejecuciones)
SyncJob.exe history list
SyncJob.exe history list <config>
SyncJob.exe history show <execution-id>
SyncJob.exe history list --status failed
SyncJob.exe history list --period 7d

# SCHEDULE commands (programar ejecuciones)
SyncJob.exe schedule add <config> --cron "0 */6 * * *"
SyncJob.exe schedule list
SyncJob.exe schedule enable <config>
SyncJob.exe schedule disable <config>
```

---

## 📊 Visualización de Datos

### **Ver Configuraciones en Formato Tabla**

```bash
SyncJob.exe config list
```

```
╔═══════════════╦══════════════════════════╦════════════╦══════════════╦══════════════╗
║ ConfigId      ║ Nombre                   ║ Estado     ║ Tracking     ║ Última Ejec. ║
╠═══════════════╬══════════════════════════╬════════════╬══════════════╬══════════════╣
║ ClientesSync  ║ Sincronización Clientes  ║ ✓ Activo   ║ Snapshot     ║ 2h ago (OK)  ║
║ VentasSync    ║ Sincronización Ventas    ║ ✓ Activo   ║ Snapshot     ║ Never        ║
║ ProductosSync ║ Sincronización Productos ║ ✓ Activo   ║ RowVersion   ║ 1d ago (OK)  ║
╚═══════════════╩══════════════════════════╩════════════╩══════════════╩══════════════╝
```

### **Ver Configuraciones en JSON**

```bash
SyncJob.exe config list --format json
```

```json
[
  {
    "configId": "ClientesSync",
    "displayName": "Sincronización de Clientes",
    "isActive": true,
    "trackingMode": "Snapshot",
    "mappingsCount": 5,
    "lastExecution": "2025-12-05T10:30:00",
    "lastExecutionStatus": "Success"
  }
]
```

---

## 🔒 Seguridad

### **Contraseñas Encriptadas**

Las contraseñas se almacenan **encriptadas** en SQLite. Nunca se guardan en texto plano.

```bash
# Cuando agregas una conexión con password
SyncJob.exe connection add SQL2008 --interactive
# Password: ********  ← Se encripta automáticamente

# La contraseña se almacena en:
# Connections.PasswordEncrypted (BLOB encriptado)
```

### **Backup de la Base de Datos**

```bash
# Hacer backup regularmente
SyncJob.exe db backup --output backup.db

# ⚠️ IMPORTANTE: El backup contiene las passwords encriptadas
# Guárdalo en un lugar seguro!
```

---

## 💡 Tips y Mejores Prácticas

### **1. Nombrado de IDs**

```bash
# ✅ BUENO
SyncJob.exe connection add SQL2008_Prod_DBCliente
SyncJob.exe config create ClientesSync_Prod_to_Analytics

# ❌ MALO
SyncJob.exe connection add c1
SyncJob.exe config create sync1
```

### **2. Usa Modo Interactivo para Empezar**

```bash
# Primero usa --interactive para familiarizarte
SyncJob.exe connection add MiConexion --interactive
SyncJob.exe config create MiConfig --interactive

# Luego automatiza con scripts
```

### **3. Backups Antes de Cambios Importantes**

```bash
SyncJob.exe db backup --output antes_migracion.db
# ... hacer cambios ...
# Si algo falla:
SyncJob.exe db restore --file antes_migracion.db
```

### **4. Limpieza Regular**

```bash
# Cada mes, limpia ejecuciones antiguas
SyncJob.exe db cleanup --older-than 90
SyncJob.exe db vacuum
```

---

## 🆘 Troubleshooting

### **Problema: "Connection failed"**

```bash
# 1. Verifica la conexión
SyncJob.exe connection test <id>

# 2. Si falla con error de certificado:
SyncJob.exe connection add <id> --trust-cert --interactive

# 3. Verifica firewall/red
ping SERVIDOR_SQL
```

### **Problema: "ConfigId already exists"**

```bash
# Elimina la config existente primero
SyncJob.exe config delete <id> --force

# O usa otro ID
SyncJob.exe config create <nuevo-id> ...
```

### **Problema: "Database is locked"**

```bash
# Otra instancia de SyncJob está corriendo
# Espera a que termine o ciérrala

# Si persiste, restaura un backup
SyncJob.exe db restore --file backup.db
```

---

## 📞 Ayuda Adicional

```bash
# Ayuda general
SyncJob.exe --help

# Ayuda de un comando específico
SyncJob.exe config --help
SyncJob.exe config create --help
SyncJob.exe connection add --help

# Ejemplos de uso
SyncJob.exe examples
```

---

## 🎉 ¡Listo para Empezar!

Ya tienes todo lo necesario para usar SyncJob 2.0 con SQLite.

**Workflow típico:**
1. ✅ `connection add` - Agregar conexiones origen y destino
2. ✅ `connection test` - Probar que funcionan
3. ✅ `config create` - Crear configuración de sync
4. ⏳ `run` - Ejecutar sincronización (próximamente)
5. ⏳ `history list` - Ver resultados (próximamente)

**¡Disfruta de la velocidad y simplicidad de SyncJob 2.0!** 🚀

---

**Versión:** 2.0.0
**Última actualización:** 2025-12-05
**Documentación completa:** INCREMENTAL_SYNC.md
