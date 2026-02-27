# 📧 Configuración del Sistema de Emails Centralizado

## 📋 Descripción General

SyncJob utiliza el **servicio centralizado de emails de PeopleWorks** (DGIIFacturaElectronicaAPI) para enviar notificaciones cuando las tareas de sincronización se completan o fallan.

**Beneficios:**
- ✅ Sin configuración SMTP en cada cliente
- ✅ Auditoría centralizada de emails
- ✅ Control de permisos por RNC
- ✅ Plantillas HTML profesionales
- ✅ Reintentos automáticos
- ✅ Logging completo

---

## 🔧 Pasos de Configuración

### 1️⃣ Ejecutar Scripts SQL

Ejecutar en orden en la base de datos **SyncJobCentralDB**:

```sql
-- FASE 1, 2, 3 (ya ejecutados)
001_Create_SyncTasks_Table.sql
002_Create_SyncTasks_Views_And_Procedures.sql
003_Create_Sample_Data.sql

-- FASE 4 - Configuración de Emails
004_Create_EmailConfiguration_Table.sql
```

Esto creará:
- Tabla `EmailConfiguration` (configuración del servicio)
- Tabla `EmailLog` (auditoría de emails enviados)
- Vista `vw_EmailLogRecent` (últimos 30 días)
- SP `sp_GetEmailConfiguration`
- SP `sp_LogEmailSent`

---

### 2️⃣ Registrar RNC en DGIIFacturaElectronicaAdmin

El servicio de emails requiere un **RNC** válido para funcionar. Opciones:

#### **Opción A: RNC Genérico para SyncJob (Recomendado)**

1. Abrir el BackOffice de DGIIFacturaElectronicaAdmin
2. Ir a **Clientes**
3. Crear nuevo cliente:
   - **RNC:** `SYNCJOB-INTERNAL`
   - **Nombre:** `SyncJob - Servicio Interno`
   - **PuedeEnviarEmail:** ✅ **TRUE** (MUY IMPORTANTE)
   - **IsActive:** ✅ TRUE
4. Guardar

#### **Opción B: Usar RNC de Cliente Existente**

Si prefieres usar el RNC de un cliente existente (ej: Iberofarmacos):

1. Verificar que el cliente tenga `PuedeEnviarEmail = 1`
2. Actualizar la tabla `EmailConfiguration` con ese RNC:

```sql
UPDATE EmailConfiguration
SET RNC = '1-31-12345-6' -- RNC del cliente
WHERE ConfigId = 1;
```

---

### 3️⃣ Actualizar Configuración de EmailConfiguration

```sql
USE SyncJobCentralDB;
GO

UPDATE EmailConfiguration
SET
    -- URL del servicio de emails (PRODUCCIÓN)
    ApiUrl = 'https://api.peopleworksservices.com/ecf/email/send',

    -- RNC configurado en el paso anterior
    RNC = 'SYNCJOB-INTERNAL',

    -- Habilitar servicio
    IsEnabled = 1,

    -- Configurar copia oculta al equipo de soporte
    AlwaysCopyTo = '["admin@peopleworksservices.com", "soporte@peopleworksservices.com"]',

    -- Configurar reintentos
    MaxRetries = 3,
    RetryDelaySeconds = 60,

    -- Notas
    UpdatedBy = 'Admin',
    UpdatedAt = GETDATE(),
    Notes = 'Configuración actualizada para producción'
WHERE ConfigId = 1;
GO

-- Verificar configuración
SELECT * FROM EmailConfiguration;
```

---

### 4️⃣ Verificar Servicio de Emails Esté Corriendo

El API de emails debe estar corriendo en:

```
http://localhost:5000/ecf/email/send  (Desarrollo)
https://api.peopleworksservices.com/ecf/email/send  (Producción)
```

**Verificar:**

```bash
# Verificar que el servicio esté corriendo
curl -X POST http://localhost:5000/ecf/email/send \
  -H "Content-Type: application/json" \
  -H "RNC: SYNCJOB-INTERNAL" \
  -d '{
    "UserName": "Test",
    "ResourceName": "SyncJob",
    "DestinatariosTo": ["test@example.com"],
    "Asunto": "Test Email",
    "CuerpoHtml": "<h1>Test</h1>"
  }'
```

---

### 5️⃣ Configurar appsettings.json de SyncJob

Asegurarse de que el connection string esté correcto:

```json
{
  "ConnectionStrings": {
    "SyncJobCentral": "Server=PEOPLEWORKSSERV;Database=SyncJobCentralDB;Integrated Security=True;TrustServerCertificate=True;Encrypt=True;"
  }
}
```

---

## 🧪 Probar el Sistema de Emails

### Desde Dashboard BackOffice

1. Ir a **Dashboard Generator**
2. Hacer clic en **"Solicitar Sincronización"**
3. Marcar **"Notificar al completar"**
4. Ingresar email de prueba: `tu.email@example.com`
5. Enviar solicitud
6. Esperar hasta 30 segundos (polling interval del Worker Service)
7. Revisar tu bandeja de entrada

### Consultar Log de Emails

```sql
USE SyncJobCentralDB;
GO

-- Ver últimos 10 emails enviados
SELECT TOP 10
    EmailLogId,
    TaskId,
    ProjectId,
    Recipients,
    Subject,
    Status,
    SentAt,
    ErrorMessage,
    CreatedAt
FROM EmailLog
ORDER BY CreatedAt DESC;

-- Ver emails fallidos
SELECT *
FROM EmailLog
WHERE Status = 'Failed'
ORDER BY CreatedAt DESC;

-- Estadísticas de emails
SELECT
    Status,
    COUNT(*) AS Total,
    MIN(CreatedAt) AS PrimerEmail,
    MAX(CreatedAt) AS UltimoEmail
FROM EmailLog
GROUP BY Status;
```

---

## 📧 Formato de los Emails

### Email de Completación ✅

```
Asunto: ✅ Sincronización completada: IBEROFARMACOS

Contenido:
- Proyecto sincronizado
- Tipo de tarea
- Solicitado por
- Duración
- Filas procesadas
- Detalles de operaciones (INSERT, UPDATE, DELETE)
- ExecutionId para rastreo
```

### Email de Error ❌

```
Asunto: ❌ Error en sincronización: IBEROFARMACOS

Contenido:
- Proyecto afectado
- Error detallado
- Stack trace (collapsible)
- ExecutionId
- Recomendaciones de acción
```

---

## 🔐 Seguridad y Permisos

### Control de Acceso

El API de emails valida:

1. **RNC válido** en la tabla `Clientes`
2. **PuedeEnviarEmail = 1** para ese RNC
3. **Header RNC** en el request HTTP
4. **Validación de emails** (formato correcto)
5. **Límite de tamaño** de adjuntos (25 MB)

### Auditoría

Todos los emails enviados se registran en:
- Tabla `EmailLog` en SyncJobCentralDB
- Logs del servicio de emails (DGIIFacturaElectronicaAPI)
- Event Log de Windows (si el Worker Service está instalado como servicio)

---

## 🐛 Troubleshooting

### Email no se envía

1. **Verificar configuración:**
   ```sql
   SELECT * FROM EmailConfiguration WHERE IsEnabled = 1;
   ```

2. **Verificar RNC tiene permisos:**
   ```sql
   USE DGIIFacturaElectronicaAdmin;
   GO

   SELECT RNC, Nombre, PuedeEnviarEmail, IsActive
   FROM Clientes
   WHERE RNC = 'SYNCJOB-INTERNAL';
   ```

3. **Revisar logs de email:**
   ```sql
   SELECT TOP 20 *
   FROM EmailLog
   WHERE Status = 'Failed'
   ORDER BY CreatedAt DESC;
   ```

4. **Verificar servicio de emails está corriendo:**
   - Revisar IIS o proceso del API
   - Verificar URL es accesible

### Error: "RNC no encontrado"

```sql
-- Crear el cliente con el RNC
INSERT INTO DGIIFacturaElectronicaAdmin.dbo.Clientes
(RNC, Nombre, PuedeEnviarEmail, IsActive)
VALUES
('SYNCJOB-INTERNAL', 'SyncJob - Servicio Interno', 1, 1);
```

### Error: "Cliente no tiene permiso para enviar emails"

```sql
-- Habilitar permiso
UPDATE DGIIFacturaElectronicaAdmin.dbo.Clientes
SET PuedeEnviarEmail = 1
WHERE RNC = 'SYNCJOB-INTERNAL';
```

### Error de conexión al API

```sql
-- Verificar URL en configuración
UPDATE EmailConfiguration
SET ApiUrl = 'http://localhost:5000/ecf/email/send' -- URL correcta
WHERE ConfigId = 1;
```

---

## 📊 Monitoreo

### Vista de emails recientes

```sql
SELECT * FROM vw_EmailLogRecent
ORDER BY CreatedAt DESC;
```

### Estadísticas por proyecto

```sql
SELECT
    ProjectId,
    COUNT(*) AS TotalEmails,
    SUM(CASE WHEN Status = 'Success' THEN 1 ELSE 0 END) AS Exitosos,
    SUM(CASE WHEN Status = 'Failed' THEN 1 ELSE 0 END) AS Fallidos,
    MAX(SentAt) AS UltimoEnvio
FROM EmailLog
GROUP BY ProjectId
ORDER BY TotalEmails DESC;
```

---

## 🎯 Próximos Pasos (Opcional)

1. **Templates HTML Personalizados:**
   - Copiar template de Node.js a directorio de templates
   - Actualizar `TemplateDirectory` en `EmailConfiguration`
   - Modificar `EmailNotificationService` para usar templates externos

2. **Adjuntos de Logs:**
   - Habilitar `IncludeExecutionLogs = 1`
   - Configurar `MaxLogSizeKB`
   - Adjuntar logs de ejecución en emails de error

3. **Dashboard de Emails:**
   - Agregar vista en BackOffice para ver emails enviados
   - Estadísticas y gráficos
   - Filtros por proyecto, estado, fecha

---

## ✅ Checklist de Configuración

- [ ] Scripts SQL ejecutados (001, 002, 003, 004)
- [ ] RNC creado en DGIIFacturaElectronicaAdmin
- [ ] Campo `PuedeEnviarEmail = 1` configurado
- [ ] `EmailConfiguration` actualizada con RNC y URL correctos
- [ ] Servicio de emails (DGIIFacturaElectronicaAPI) corriendo
- [ ] Connection string configurado en appsettings.json
- [ ] Prueba de envío exitosa desde Dashboard
- [ ] Verificado log de emails en `EmailLog`

---

**🤖 Sistema listo para producción!**

Para soporte, contactar al equipo de PeopleWorks.
