using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using SyncJob.Services.Models;
using SyncJob.Database;

namespace SyncJob.Services
{
    /// <summary>
    /// Worker Service que monitorea y ejecuta tareas de sincronización en demanda
    /// </summary>
    public class SyncJobWorkerService : BackgroundService
    {
        private readonly ILogger<SyncJobWorkerService> _logger;
        private readonly IConfiguration _configuration;
        private readonly string _centralConnectionString;
        private readonly TimeSpan _pollingInterval = TimeSpan.FromSeconds(30);

        public SyncJobWorkerService(
            ILogger<SyncJobWorkerService> logger,
            IConfiguration configuration)
        {
            _logger = logger;
            _configuration = configuration;

            // Obtener connection string del central
            _centralConnectionString = configuration.GetConnectionString("SyncJobCentral")
                ?? throw new InvalidOperationException("SyncJobCentral connection string not configured");
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("SyncJob Worker Service starting...");
            _logger.LogInformation("Polling interval: {PollingInterval} seconds", _pollingInterval.TotalSeconds);

            // Esperar un poco antes de comenzar
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await ProcessPendingTasksAsync(stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing pending tasks: {ErrorMessage}", ex.Message);
                }

                // Esperar antes del siguiente ciclo
                try
                {
                    await Task.Delay(_pollingInterval, stoppingToken);
                }
                catch (TaskCanceledException)
                {
                    // Servicio detenido
                    break;
                }
            }

            _logger.LogInformation("SyncJob Worker Service stopping...");
        }

        /// <summary>
        /// Procesa todas las tareas pendientes
        /// </summary>
        private async Task ProcessPendingTasksAsync(CancellationToken stoppingToken)
        {
            // Crear monitor
            var monitor = new SyncTaskMonitor(
                _centralConnectionString,
                _logger as ILogger<SyncTaskMonitor> ??
                    LoggerFactory.Create(builder => builder.AddConsole())
                        .CreateLogger<SyncTaskMonitor>());

            // Obtener tareas pendientes (máximo 5 a la vez)
            var pendingTasks = await monitor.GetPendingTasksAsync(maxTasks: 5);

            if (pendingTasks.Count == 0)
            {
                _logger.LogDebug("No pending tasks found");
                return;
            }

            _logger.LogInformation("Found {TaskCount} pending tasks to process", pendingTasks.Count);

            // Procesar cada tarea (secuencialmente para evitar sobrecarga)
            foreach (var task in pendingTasks)
            {
                if (stoppingToken.IsCancellationRequested)
                    break;

                await ProcessSingleTaskAsync(task, monitor, stoppingToken);
            }
        }

        /// <summary>
        /// Procesa una tarea individual
        /// </summary>
        private async Task ProcessSingleTaskAsync(
            SyncTaskEntity task,
            SyncTaskMonitor monitor,
            CancellationToken stoppingToken)
        {
            var executionId = Guid.NewGuid();
            var startTime = DateTime.Now;

            try
            {
                _logger.LogInformation(
                    "Starting task {TaskId} for project {ProjectId} (Priority: {Priority}, Type: {TaskType})",
                    task.TaskId, task.ProjectId, task.Priority, task.TaskType);

                // Marcar tarea como Running
                await monitor.MarkTaskAsRunningAsync(task.TaskId, executionId);

                // Inicializar DbManager con la base de datos local del proyecto
                // Asumimos que cada proyecto tiene su syncjob.db en su directorio
                var dbPath = GetProjectDbPath(task.ProjectId);
                DbManager.DbPath = dbPath;
                DbManager.Initialize();

                _logger.LogInformation("Using local database: {DbPath}", dbPath);

                // Crear executor
                var executor = new SyncTaskExecutor(
                    _logger as ILogger<SyncTaskExecutor> ??
                        LoggerFactory.Create(builder => builder.AddConsole())
                            .CreateLogger<SyncTaskExecutor>());

                // Ejecutar tarea
                var result = await executor.ExecuteTaskAsync(task);

                // Actualizar estado según resultado
                if (result.Success)
                {
                    await monitor.MarkTaskAsCompletedAsync(task.TaskId, result);

                    _logger.LogInformation(
                        "Task {TaskId} completed successfully. Duration: {Duration}ms, Rows: {Rows}",
                        task.TaskId, result.DurationMs, result.RowsProcessed);

                    // Enviar email de notificación si está configurado
                    if (task.NotifyOnComplete && !string.IsNullOrWhiteSpace(task.NotificationEmail))
                    {
                        await SendCompletionEmailAsync(task, result);
                    }
                }
                else
                {
                    var duration = (long)(DateTime.Now - startTime).TotalMilliseconds;
                    await monitor.MarkTaskAsFailedAsync(
                        task.TaskId,
                        duration,
                        result.ErrorMessage ?? "Unknown error",
                        result.ErrorStackTrace);

                    _logger.LogError(
                        "Task {TaskId} failed. Duration: {Duration}ms, Error: {ErrorMessage}",
                        task.TaskId, duration, result.ErrorMessage);

                    // Enviar email de error si está configurado
                    if (task.NotifyOnComplete && !string.IsNullOrWhiteSpace(task.NotificationEmail))
                    {
                        await SendFailureEmailAsync(task, result);
                    }
                }
            }
            catch (Exception ex)
            {
                var duration = (long)(DateTime.Now - startTime).TotalMilliseconds;

                await monitor.MarkTaskAsFailedAsync(
                    task.TaskId,
                    duration,
                    ex.Message,
                    ex.StackTrace);

                _logger.LogError(ex,
                    "Unexpected error processing task {TaskId}: {ErrorMessage}",
                    task.TaskId, ex.Message);

                // Enviar email de error si está configurado
                if (task.NotifyOnComplete && !string.IsNullOrWhiteSpace(task.NotificationEmail))
                {
                    var errorResult = new SyncTaskExecutionResult
                    {
                        Success = false,
                        ErrorMessage = ex.Message,
                        ErrorStackTrace = ex.StackTrace,
                        DurationMs = duration
                    };
                    await SendFailureEmailAsync(task, errorResult);
                }
            }
        }

        /// <summary>
        /// Obtiene la ruta del archivo de base de datos local del proyecto
        /// </summary>
        private string GetProjectDbPath(string projectId)
        {
            // Por defecto, buscar en el directorio actual
            // TODO: Configurar rutas específicas por proyecto desde configuración
            var basePath = Directory.GetCurrentDirectory();

            // Si hay configuración específica del proyecto
            var projectDbPath = _configuration.GetValue<string>($"Projects:{projectId}:DbPath");
            if (!string.IsNullOrWhiteSpace(projectDbPath))
            {
                return projectDbPath;
            }

            // Ruta por defecto
            return Path.Combine(basePath, $"{projectId.ToLowerInvariant()}_syncjob.db");
        }

        /// <summary>
        /// Envía email de notificación de completación
        /// </summary>
        private async Task SendCompletionEmailAsync(SyncTaskEntity task, SyncTaskExecutionResult result)
        {
            try
            {
                _logger.LogInformation("Enviando email de completación a {Email}", task.NotificationEmail);

                // Crear servicio de email con HttpClient compartido
                var emailService = new EmailNotificationService(
                    _centralConnectionString,
                    _logger as ILogger<EmailNotificationService> ??
                        LoggerFactory.Create(builder => builder.AddConsole())
                            .CreateLogger<EmailNotificationService>());

                // Enviar notificación
                bool sent = await emailService.SendTaskCompletedNotificationAsync(task, result);

                if (sent)
                {
                    _logger.LogInformation("✅ Email de completación enviado exitosamente a {Email}", task.NotificationEmail);
                }
                else
                {
                    _logger.LogWarning("⚠️ No se pudo enviar email de completación a {Email}", task.NotificationEmail);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error enviando email de completación a {Email}", task.NotificationEmail);
            }
        }

        /// <summary>
        /// Envía email de notificación de falla
        /// </summary>
        private async Task SendFailureEmailAsync(SyncTaskEntity task, SyncTaskExecutionResult result)
        {
            try
            {
                _logger.LogInformation("Enviando email de falla a {Email}", task.NotificationEmail);

                // Crear servicio de email
                var emailService = new EmailNotificationService(
                    _centralConnectionString,
                    _logger as ILogger<EmailNotificationService> ??
                        LoggerFactory.Create(builder => builder.AddConsole())
                            .CreateLogger<EmailNotificationService>());

                // Enviar notificación
                bool sent = await emailService.SendTaskFailedNotificationAsync(task, result);

                if (sent)
                {
                    _logger.LogInformation("✅ Email de falla enviado exitosamente a {Email}", task.NotificationEmail);
                }
                else
                {
                    _logger.LogWarning("⚠️ No se pudo enviar email de falla a {Email}", task.NotificationEmail);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error enviando email de falla a {Email}", task.NotificationEmail);
            }
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("SyncJob Worker Service is stopping gracefully...");
            await base.StopAsync(cancellationToken);
        }
    }
}
