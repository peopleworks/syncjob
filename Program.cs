using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using Spectre.Console.Cli;
using SyncJob.Commands;
using SyncJob.Services;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace SyncJob
{
    // Config models
    public class SyncConfig
    {
        public SourceConfig? Source { get; set; }

        public DestConfig? Destination { get; set; }

        public List<ColumnMap>? ColumnMappings { get; set; }

        public SyncOptions? Options { get; set; }

        public IncrementalConfig? Incremental { get; set; }
    }

    public class SourceConfig
    {
        public string? ConnectionString { get; set; }

        public string? Query { get; set; }

        // Stored Procedure como alternativa al Query
        public string? StoredProcedure { get; set; }

        // Parámetros opcionales para el Stored Procedure (clave=valor)
        public Dictionary<string, string>? Parameters { get; set; }
    }

    public class DestConfig
    {
        public string? ConnectionString { get; set; }

        public string? StageTable { get; set; }

        public string? FinalTable { get; set; }
    }

    public class ColumnMap
    {
        public string? Source { get; set; }

        public string Dest { get; set; } = string.Empty;
    }

    public class SyncOptions
    {
        public int BatchSize { get; set; }

        public int MaxDegreeOfParallelism { get; set; }

        public int BulkCopyTimeoutSeconds { get; set; }

        public bool KeepIdentity { get; set; }

        public int MinRowThresholdToCommit { get; set; }
    }

    public class SourceDataPackage
    {
        public string[] SourceColumnNames { get; set; } = Array.Empty<string>();

        public List<object[]> Rows { get; set; } = new List<object[]>();
    }

    // CLI
    public class ConfigSettings : CommandSettings
    {
        [Description("Ruta del archivo de configuración (JSON). Default: appsettings.json")]
        [CommandOption("-c|--config <PATH>")]
        public string ConfigPath { get; set; } = "appsettings.json";

        [Description("Nombre de la sección dentro del JSON. Default: SyncJob")]
        [CommandOption("-s|--section <NAME>")]
        public string Section { get; set; } = "SyncJob";

        [Description("Override de MaxDegreeOfParallelism para esta ejecución.")]
        [CommandOption("--maxdop <N>")]
        public int? MaxDop { get; set; }

        [Description("Override de BatchSize para esta ejecución.")]
        [CommandOption("--batch-size <N>")]
        public int? BatchSize { get; set; }

        [Description("Muestra ejemplos de uso y termina.")]
        [CommandOption("--examples")]
        public bool Examples { get; set; }

        [Description("Confía en el certificado del servidor SQL (no valida la CA). Uso temporal/Dev")]
        [CommandOption("--trust-server-cert")]
        public bool TrustServerCertificate { get; set; }

        [Description("Desactiva cifrado TLS en la conexión SQL (no recomendado)")]
        [CommandOption("--no-encrypt")]
        public bool NoEncrypt { get; set; }

        [Description("Nombre esperado en el certificado del servidor (CN/FQDN) para validación")]
        [CommandOption("--host-name-in-cert <NAME>")]
        public string? HostNameInCertificate { get; set; }

        [Description("Limita la cantidad de filas leídas del origen (solo pruebas)")]
        [CommandOption("--top <N>")]
        public int? Top { get; set; }

        [Description("Cantidad de filas a probar en el origen antes de iniciar (default validate: 1)")]
        [CommandOption("--probe-top <N>")]
        public int? ProbeTop { get; set; }

        [Description("Nivel de log (Trace, Debug, Info, Warn, Error, Fatal). Default: Info")]
        [CommandOption("--log-level <LEVEL>")]
        public string? LogLevel { get; set; }

        [Description("Ruta de archivo de log. Si no se indica, se usa logs/SyncJob_yyyyMMdd.log")]
        [CommandOption("--log-file <PATH>")]
        public string? LogFile { get; set; }

        [Description("Directorio de logs (usado si no se especifica --log-file). Default: logs")]
        [CommandOption("--log-dir <PATH>")]
        public string? LogDir { get; set; }

        [Description("Salida de logs en formato JSON (JSONL)")]
        [CommandOption("--json-log")]
        public bool JsonLog { get; set; }

        [Description("No imprimir logs a consola (solo archivo)")]
        [CommandOption("--quiet")]
        public bool Quiet { get; set; }

        [Description("Umbral mínimo de filas para permitir commit (override de config)")]
        [CommandOption("--min-commit <N>")]
        public int? MinCommit { get; set; }

        [Description("No hacer commit Stage -> Final (pruebas)")]
        [CommandOption("--skip-commit")]
        public bool SkipCommit { get; set; }

        [Description("Forzar commit aunque filas < MinRowThresholdToCommit (no recomendado)")]
        [CommandOption("--force-commit")]
        public bool ForceCommit { get; set; }

        [Description("Ejecutar un Stored Procedure como origen (override de Source.StoredProcedure)")]
        [CommandOption("--sp <NAME>")]
        public string? StoredProcedure { get; set; }

        [Description("Parámetro para Stored Procedure (repetible): NOMBRE=VALOR")]
        [CommandOption("--sp-param <NAME=VALUE>")]
        public string[] SpParams { get; set; } = Array.Empty<string>();

        public override ValidationResult Validate()
        {
            if(string.IsNullOrWhiteSpace(ConfigPath))
                return ValidationResult.Error("--config es requerido");
            return ValidationResult.Success();
        }
    }

    public sealed class RunSettings : ConfigSettings
    {
        [Description("No escribe en SQL. Verifica conexiones y mapeos.")]
        [CommandOption("--dry-run")]
        public bool DryRun { get; set; }

        [Description("Escribe directo en tabla Final (sin Stage)")]
        [CommandOption("--direct")]
        public bool Direct { get; set; }

        [Description("Insertar sin truncar tabla Final (append)")]
        [CommandOption("--append")]
        public bool Append { get; set; }

        [Description("Forzar full refresh ignorando tracking incremental")]
        [CommandOption("--full-refresh")]
        public bool FullRefresh { get; set; }

        [Description("Inicializar tabla de tracking incremental")]
        [CommandOption("--init-tracking")]
        public bool InitTracking { get; set; }
    }

    public sealed class ValidateSettings : ConfigSettings
    {
        [Description("Valida contra tabla Final (modo directo)")]
        [CommandOption("--direct")]
        public bool Direct { get; set; }
    }

    public sealed class InitSettings : CommandSettings
    {
        [Description("Archivo de texto con nombres de campos (uno por línea)")]
        [CommandOption("-f|--fields <PATH>")]
        public string FieldsPath { get; set; } = string.Empty;

        [Description("Ruta del appsettings.json a generar/actualizar (default: appsettings.json)")]
        [CommandOption("-o|--out <PATH>")]
        public string OutputPath { get; set; } = "appsettings.json";

        [Description("Nombre de la sección a crear/actualizar (default: SyncJob)")]
        [CommandOption("-s|--section <NAME>")]
        public string Section { get; set; } = "SyncJob";

        [Description("Nombre de tabla Stage (default: dbo.Stage)")]
        [CommandOption("--stage <NAME>")]
        public string Stage { get; set; } = "dbo.Stage";

        [Description("Nombre de tabla Final (default: dbo.Final)")]
        [CommandOption("--final <NAME>")]
        public string Final { get; set; } = "dbo.Final";

        [Description("Sobrescribir la sección si ya existe")]
        [CommandOption("--overwrite")]
        public bool Overwrite { get; set; }

        [Description("Opcional: MaxDegreeOfParallelism para el archivo")]
        [CommandOption("--maxdop <N>")]
        public int? MaxDop { get; set; }

        [Description("Opcional: BatchSize para el archivo")]
        [CommandOption("--batch-size <N>")]
        public int? BatchSize { get; set; }

        [Description("Opcional: KeepIdentity (true/false)")]
        [CommandOption("--keep-identity <BOOL>")]
        public bool? KeepIdentity { get; set; }

        [Description("Opcional: MinRowThresholdToCommit")]
        [CommandOption("--min-commit <N>")]
        public int? MinCommit { get; set; }

        public override ValidationResult Validate()
        {
            if(string.IsNullOrWhiteSpace(FieldsPath))
                return ValidationResult.Error("--fields es requerido");
            if(!File.Exists(FieldsPath))
                return ValidationResult.Error($"No existe el archivo de campos: {FieldsPath}");
            if(string.IsNullOrWhiteSpace(OutputPath))
                return ValidationResult.Error("--out es requerido");
            if(string.IsNullOrWhiteSpace(Section))
                return ValidationResult.Error("--section es requerido");
            return ValidationResult.Success();
        }
    }

    class Program
    {
        static int Main(string[] args)
        {
            // ============================================================
            // MODO DUAL: CLI o Windows Service
            // ============================================================

            // Si no hay argumentos → Modo Windows Service
            if (args.Length == 0)
            {
                RunAsWindowsService();
                return 0;
            }

            // Si hay argumentos → Modo CLI (comportamiento actual)
            return RunAsCli(args);
        }

        /// <summary>
        /// Ejecuta como Windows Service
        /// </summary>
        static void RunAsWindowsService()
        {
            var host = Host.CreateDefaultBuilder()
                .UseWindowsService(options =>
                {
                    options.ServiceName = "PeopleWorks SyncJob Service";
                })
                .ConfigureAppConfiguration((context, config) =>
                {
                    config.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);
                })
                .ConfigureServices((context, services) =>
                {
                    services.AddHostedService<SyncJobWorkerService>();
                })
                .ConfigureLogging((context, logging) =>
                {
                    logging.ClearProviders();
                    logging.AddConsole();
                    logging.AddEventLog(settings =>
                    {
                        settings.SourceName = "PeopleWorks SyncJob";
                    });
                })
                .Build();

            host.Run();
        }

        /// <summary>
        /// Ejecuta como CLI (comportamiento actual)
        /// </summary>
        static int RunAsCli(string[] args)
        {
            var app = new CommandApp();
            app.Configure(
                config =>
                {
                    config.SetApplicationName("SyncJob.exe");
                    var version = System.Reflection.Assembly.GetExecutingAssembly()
                        .GetName().Version?.ToString(3) ?? "2.2.0";
                    config.SetApplicationVersion(version);

                    // ============================================
                    // LEGACY COMMANDS (JSON-based)
                    // ============================================
                    config.AddCommand<RunCommand>("run").WithDescription("Ejecuta sincronización (JSON config)");
                    config.AddCommand<ValidateCommand>("validate")
                        .WithDescription("Valida configuración y conectividad (JSON config)");
                    config.AddCommand<InitCommand>("config-init")
                        .WithDescription("Genera appsettings desde lista de campos");
                    config.AddCommand<ExamplesCommand>("examples").WithDescription("Muestra ejemplos de uso");

                    // ============================================
                    // NEW COMMANDS (SQLite-based)
                    // ============================================

                    // CONFIG commands
                    config.AddBranch("config", cfg =>
                    {
                        cfg.SetDescription("Gestión de configuraciones (SQLite)");
                        cfg.AddCommand<ConfigCreateCommand>("create")
                            .WithDescription("Crear nueva configuración");
                        cfg.AddCommand<ConfigListCommand>("list")
                            .WithDescription("Listar configuraciones");
                        cfg.AddCommand<ConfigShowCommand>("show")
                            .WithDescription("Mostrar detalles de una configuración");
                        cfg.AddCommand<ConfigDeleteCommand>("delete")
                            .WithDescription("Eliminar configuración");
                    });

                    // CONNECTION commands
                    config.AddBranch("connection", conn =>
                    {
                        conn.SetDescription("Gestión de conexiones a SQL Server");
                        conn.AddCommand<ConnectionAddCommand>("add")
                            .WithDescription("Agregar nueva conexión");
                        conn.AddCommand<ConnectionListCommand>("list")
                            .WithDescription("Listar conexiones");
                        conn.AddCommand<ConnectionTestCommand>("test")
                            .WithDescription("Probar conexión");
                        conn.AddCommand<ConnectionDeleteCommand>("delete")
                            .WithDescription("Eliminar conexión");
                    });

                    // DB commands
                    config.AddBranch("db", db =>
                    {
                        db.SetDescription("Gestión de la base de datos SQLite");
                        db.AddCommand<DbInfoCommand>("info")
                            .WithDescription("Información de la base de datos");
                        db.AddCommand<DbBackupCommand>("backup")
                            .WithDescription("Crear backup de la base de datos");
                        db.AddCommand<DbRestoreCommand>("restore")
                            .WithDescription("Restaurar backup");
                        db.AddCommand<DbCleanupCommand>("cleanup")
                            .WithDescription("Limpiar registros antiguos");
                        db.AddCommand<DbVacuumCommand>("vacuum")
                            .WithDescription("Compactar base de datos");
                    });

                    // MAPPING commands
                    config.AddBranch("mapping", map =>
                    {
                        map.SetDescription("Gestión de column mappings");
                        map.AddCommand<MappingAddCommand>("add")
                            .WithDescription("Agregar column mapping");
                        map.AddCommand<MappingListCommand>("list")
                            .WithDescription("Listar column mappings");
                        map.AddCommand<MappingRemoveCommand>("remove")
                            .WithDescription("Eliminar column mapping");
                        map.AddCommand<MappingClearCommand>("clear")
                            .WithDescription("Eliminar todos los mappings");
                    });

                    // HISTORY commands
                    config.AddBranch("history", hist =>
                    {
                        hist.SetDescription("Historial de ejecuciones");
                        hist.AddCommand<HistoryListCommand>("list")
                            .WithDescription("Listar ejecuciones");
                        hist.AddCommand<HistoryShowCommand>("show")
                            .WithDescription("Ver detalles de ejecución");
                        hist.AddCommand<HistoryStatsCommand>("stats")
                            .WithDescription("Estadísticas de ejecuciones");
                        hist.AddCommand<HistoryClearCommand>("clear")
                            .WithDescription("Limpiar historial antiguo");
                    });

                    // CENTRAL SYNC commands
                    config.AddBranch("central", central =>
                    {
                        central.SetDescription("Sincronización central al servidor PeopleWorks");
                        central.AddCommand<CentralSetupCommand>("setup")
                            .WithDescription("Configurar sincronización central (interactivo)");
                        central.AddCommand<CentralTestCommand>("test")
                            .WithDescription("Probar conexión al servidor central");
                        central.AddCommand<CentralStatusCommand>("status")
                            .WithDescription("Ver estado de la configuración central");
                        central.AddCommand<CentralSyncCommand>("sync")
                            .WithDescription("Sincronizar historial manualmente");
                        central.AddCommand<CentralEnableCommand>("enable")
                            .WithDescription("Habilitar sincronización automática");
                        central.AddCommand<CentralDisableCommand>("disable")
                            .WithDescription("Deshabilitar sincronización automática");
                        central.AddCommand<CentralResetCommand>("reset")
                            .WithDescription("Limpiar toda la configuración central");
                    });

                    // RUN command (SQLite-based)
                    config.AddCommand<RunFromDbCommand>("run-db")
                        .WithDescription("Ejecutar sincronización desde configuración SQLite")
                        .WithExample(new[] { "run-db", "my-config-001" })
                        .WithExample(new[] { "run-db", "my-config-001", "--dry-run" })
                        .WithExample(new[] { "run-db", "my-config-001", "--direct", "--append" });
                });

            return app.Run(args);
        }

        // Commands
        public sealed class RunCommand : Command<RunSettings>
        {
            public override int Execute(CommandContext context, RunSettings settings)
            {
                ShowHeader();
                InitLogging(settings);
                try
                {
                    if(settings.Examples)
                    {
                        PrintExamples();
                        return 0;
                    }
                    var cfg = LoadConfig(settings.ConfigPath, settings.Section);
                    ApplyOverrides(cfg, settings);
                    ValidateConfig(cfg, settings.Direct);

                    // Inicializar tabla de tracking si se solicita
                    if(settings.InitTracking && cfg.Incremental?.Enabled == true)
                    {
                        AnsiConsole.Status().Start("Inicializando tabla de tracking...", ctx =>
                        {
                            IncrementalSyncEngine.EnsureTrackingTable(
                                cfg.Destination!.ConnectionString!,
                                cfg.Incremental.TrackingTable ?? "dbo.SyncJobTracking");
                        });
                        AnsiConsole.MarkupLine("[green]Tabla de tracking inicializada correctamente[/]");
                        return 0;
                    }

                    // Override de full refresh
                    if(settings.FullRefresh && cfg.Incremental != null)
                    {
                        cfg.Incremental.ForceFullRefresh = true;
                        AnsiConsole.MarkupLine("[yellow]Modo Full Refresh activado[/] (ignorando tracking incremental)");
                    }

                    ShowConfigSummary(cfg);

                    // Probe TOP N del origen para validar permisos y tiempo
                    int probeN = Math.Max(1, settings.ProbeTop ?? 1);
                    var probeRes = ProbeSourceTopN(cfg, probeN, 30);
                    AnsiConsole.MarkupLine(
                        $"Probe origen TOP {probeN}: [bold]{probeRes.rows}[/] filas en [bold]{probeRes.elapsedMs} ms[/]");
                    if(probeRes.elapsedMs > 2000)
                    {
                        AnsiConsole.MarkupLine(
                            "[yellow]Aviso:[/] El SELECT TOP es más lento de lo esperado (>2s). Considera:");
                        AnsiConsole.MarkupLine(
                            "- Revisar índices en columnas de filtros/joins de la vista/consulta origen.");
                        AnsiConsole.MarkupLine(
                            "- Probar con --top para pruebas y/o particionar por rangos de fecha/ID.");
                    }

                    if(settings.DryRun)
                    {
                        AnsiConsole.Status()
                            .Start(
                                "Verificando conexiones y esquema...",
                                ctx =>
                                {
                                    TestConnectivity(cfg);
                                    var sourceCols = ReadSourceSchema(cfg);
                                    _ = BuildDestToSourceIndex(cfg, sourceCols);
                                    if(settings.Direct)
                                        _ = GetFinalSchema(cfg);
                                    else
                                        _ = GetStageSchema(cfg);
                                });

                        AnsiConsole.MarkupLine("[green]Dry-run OK[/]: Conexiones y mapeos válidos.");
                        Log.Info("Dry-run OK", evt: "run.dryrun.ok");
                        return 0;
                    }

                    // Ejecución real
                    SyncTrackingState? lastState = null;
                    string jobId = string.Empty;

                    // Si el modo incremental está habilitado, obtener estado previo
                    if(cfg.Incremental?.Enabled == true)
                    {
                        jobId = cfg.Incremental.JobIdentifier ?? IncrementalSyncEngine.GenerateJobIdentifier(cfg);
                        AnsiConsole.MarkupLine($"[cyan]Modo Incremental:[/] Job ID = [bold]{jobId}[/]");

                        IncrementalSyncEngine.EnsureTrackingTable(
                            cfg.Destination!.ConnectionString!,
                            cfg.Incremental.TrackingTable ?? "dbo.SyncJobTracking");

                        lastState = IncrementalSyncEngine.GetLastSyncState(
                            cfg.Destination.ConnectionString!,
                            cfg.Incremental.TrackingTable ?? "dbo.SyncJobTracking",
                            jobId);

                        if(lastState != null)
                        {
                            AnsiConsole.MarkupLine($"[cyan]Última sincronización:[/] {lastState.LastSyncTime:yyyy-MM-dd HH:mm:ss}");
                            AnsiConsole.MarkupLine($"[cyan]Filas anteriores:[/] {lastState.RowsProcessed:N0} ({lastState.RowsInserted:N0} ins, {lastState.RowsUpdated:N0} upd, {lastState.RowsDeleted:N0} del)");
                        }
                        else
                        {
                            AnsiConsole.MarkupLine("[yellow]Primera sincronización[/] (no hay tracking previo)");
                        }

                        // Modificar query origen para filtro incremental
                        if(!string.IsNullOrWhiteSpace(cfg.Source!.Query))
                        {
                            cfg.Source.Query = IncrementalSyncEngine.BuildIncrementalQuery(
                                cfg.Source.Query,
                                cfg.Incremental,
                                lastState);
                        }
                    }

                    SourceDataPackage data = new();
                    AnsiConsole.Status()
                        .Start(
                            cfg.Incremental?.Enabled == true ? "Leyendo cambios del origen (incremental)..." : "Leyendo datos del origen...",
                            ctx =>
                            {
                                data = ReadSourceData(cfg);
                            });
                    AnsiConsole.MarkupLine($"Total filas {(cfg.Incremental?.Enabled == true ? "modificadas" : "origen")}: [bold]{data.Rows.Count}[/]");
                    Log.Info($"Source rows={data.Rows.Count}", evt: "run.source.rows");

                    bool doCommit = true;
                    if(data.Rows.Count < cfg.Options!.MinRowThresholdToCommit)
                    {
                        if(settings.ForceCommit)
                        {
                            AnsiConsole.MarkupLine(
                                $"[yellow]Aviso:[/] Forzando commit con {data.Rows.Count} < MinRowThresholdToCommit={cfg.Options.MinRowThresholdToCommit}");
                            Log.Warn($"Forcing commit with rows={data.Rows.Count} < min={cfg.Options.MinRowThresholdToCommit}", evt: "run.commit.force");
                        } else if(settings.SkipCommit)
                        {
                            doCommit = false;
                            AnsiConsole.MarkupLine(
                                $"[yellow]Aviso:[/] {data.Rows.Count} < MinRowThresholdToCommit={cfg.Options.MinRowThresholdToCommit}. Se omitirá commit.");
                            Log.Warn($"Skipping commit with rows={data.Rows.Count} < min={cfg.Options.MinRowThresholdToCommit}", evt: "run.commit.skip");
                        } else
                        {
                            Log.Warn($"Abort: rows={data.Rows.Count} < min={cfg.Options.MinRowThresholdToCommit}", evt: "run.abort.min");
                            throw new Exception(
                                $"Filas ({data.Rows.Count}) < MinRowThresholdToCommit ({cfg.Options.MinRowThresholdToCommit}). Abortado.");
                        }
                    }

                    if(settings.Direct)
                    {
                        if(doCommit)
                        {
                            AnsiConsole.Status().Start("Cargando Final (directo)...", ctx => LoadFinalDirect(cfg, data, settings.Append));
                            Log.Info("Direct load completed", evt: "run.direct.done");
                        }
                        else
                        {
                            AnsiConsole.MarkupLine("[yellow]Aviso:[/] Modo directo y doCommit=false. No se escribi3 en Final.");
                        }
                    }
                    else
                    {
                        AnsiConsole.Status().Start("Cargando Stage en paralelo...", ctx => LoadStageInParallel(cfg, data));
                        if(doCommit)
                        {
                            AnsiConsole.Status()
                                .Start("Commit Stage -> Final...", ctx => CommitStageToFinal(cfg, data.Rows.Count, settings.Append));
                            Log.Info("Commit completed", evt: "run.commit.done");
                        }
                    }

                    // Guardar estado de tracking incremental
                    if(cfg.Incremental?.Enabled == true && doCommit)
                    {
                        var newState = new SyncTrackingState
                        {
                            JobIdentifier = jobId,
                            LastSyncTime = DateTime.UtcNow,
                            RowsProcessed = data.Rows.Count,
                            RowsInserted = data.Rows.Count, // TODO: Obtener metrics reales del MERGE
                            RowsUpdated = 0,
                            RowsDeleted = 0,
                            Success = true
                        };

                        // Extraer valor máximo de tracking column
                        if(!string.IsNullOrWhiteSpace(cfg.Incremental.TrackingColumn))
                        {
                            var maxValue = IncrementalSyncEngine.ExtractMaxTrackingValue(
                                data,
                                cfg.Incremental.TrackingColumn,
                                cfg.Incremental.Mode);

                            if(cfg.Incremental.Mode == TrackingMode.Timestamp && maxValue is DateTime dt)
                            {
                                newState.LastSyncTime = dt;
                            }
                            else if(cfg.Incremental.Mode == TrackingMode.RowVersion && maxValue is byte[] rv)
                            {
                                newState.LastRowVersion = rv;
                            }
                        }

                        IncrementalSyncEngine.SaveSyncState(
                            cfg.Destination!.ConnectionString!,
                            cfg.Incremental.TrackingTable ?? "dbo.SyncJobTracking",
                            newState);

                        AnsiConsole.MarkupLine($"[cyan]Estado de tracking guardado:[/] {newState.LastSyncTime:yyyy-MM-dd HH:mm:ss}");
                    }

                    AnsiConsole.MarkupLine("[green]=== Sync OK ===[/]");
                    Log.Info("Run OK", evt: "run.ok");
                    return 0;
                } catch(Exception ex)
                {
                    AnsiConsole.WriteException(
                        ex,
                        ExceptionFormats.ShortenPaths | ExceptionFormats.ShortenTypes | ExceptionFormats.ShortenMethods);
                    Log.Error("Run failed", ex, evt: "run.error");
                    PrintHelpfulHints(ex, settings);
                    return 1;
                }
            }
        }

        public sealed class ValidateCommand : Command<ValidateSettings>
        {
            public override int Execute(CommandContext context, ValidateSettings settings)
            {
                ShowHeader();
                InitLogging(settings);
                try
                {
                    // Mostrar ejemplos si lo piden
                    if(settings is ConfigSettings cs && cs.Examples)
                    {
                        PrintExamples();
                        return 0;
                    }
                    var cfg = LoadConfig(settings.ConfigPath, settings.Section);
                    ApplyOverrides(cfg, settings);
                    ValidateConfig(cfg, settings.Direct);
                    ShowConfigSummary(cfg);

                    AnsiConsole.Status()
                        .Start(
                            "Probando conexiones y esquema...",
                            ctx =>
                            {
                                TestConnectivity(cfg);
                                var sourceCols = ReadSourceSchema(cfg);
                                _ = BuildDestToSourceIndex(cfg, sourceCols);
                                if(settings.Direct)
                                    _ = GetFinalSchema(cfg);
                                else
                                    _ = GetStageSchema(cfg);
                            });

                    // Probe TOP N del origen para validar permisos y tiempo
                    int probeN = Math.Max(1, settings.ProbeTop ?? 1);
                    var probeRes = ProbeSourceTopN(cfg, probeN, 30);
                    AnsiConsole.MarkupLine($"Probe origen TOP {probeN}: [bold]{probeRes.rows}[/] filas en [bold]{probeRes.elapsedMs} ms[/]");
                    if (probeRes.elapsedMs > 2000)
                    {
                        AnsiConsole.MarkupLine("[yellow]Aviso:[/] El SELECT TOP es más lento de lo esperado (>2s). Considera:");
                        AnsiConsole.MarkupLine("- Revisar índices en columnas de filtros/joins de la vista/consulta origen.");
                        AnsiConsole.MarkupLine("- Probar con --top para pruebas y/o particionar por rangos de fecha/ID.");
                    }

                    AnsiConsole.MarkupLine("[green]Validación OK[/]");
                    Log.Info("Validate OK", evt: "validate.ok");
                    return 0;
                } catch(Exception ex)
                {
                    AnsiConsole.WriteException(
                        ex,
                        ExceptionFormats.ShortenPaths | ExceptionFormats.ShortenTypes | ExceptionFormats.ShortenMethods);
                    Log.Error("Validate failed", ex, evt: "validate.error");
                    PrintHelpfulHints(ex, settings);
                    return 1;
                } finally {
                    Log.Shutdown();
                }
            }
        }

        public sealed class InitCommand : Command<InitSettings>
        {
            public override int Execute(CommandContext context, InitSettings s)
            {
                ShowHeader();
                try
                {
                    var fields = ReadFieldsFile(s.FieldsPath);
                    if(fields.Count == 0)
                        throw new Exception("El archivo de campos está vacío.");

                    var cfg = CreateConfigFromFields(fields, s);
                    WriteSectionToAppSettings(s.OutputPath, s.Section, cfg, s.Overwrite);

                    AnsiConsole.MarkupLine(
                        $"[green]OK[/] Sección '[bold]{s.Section}[/]' escrita en [bold]{s.OutputPath}[/]");
                    return 0;
                } catch(Exception ex)
                {
                    AnsiConsole.WriteException(
                        ex,
                        ExceptionFormats.ShortenPaths | ExceptionFormats.ShortenTypes | ExceptionFormats.ShortenMethods);
                    return 1;
                }
            }
        }

        public sealed class ExamplesCommand : Command
        {
            public override int Execute(CommandContext context)
            {
                ShowHeader();
                PrintExamples();
                return 0;
            }
        }

        static void ShowHeader()
        {
            var banner = new Panel(new FigletText("PeopleWorks SyncJob For SQL Server").Color(Color.Aqua))
                .Border(BoxBorder.Rounded)
                .BorderStyle(new Style(Color.Yellow))
                .Padding(1, 0)
                .Header("PeopleWorks", Justify.Left);
            AnsiConsole.Write(banner);
            AnsiConsole.Write(new Spectre.Console.Rule());
        }

        static void ShowConfigSummary(SyncConfig cfg)
        {
            var src = new SqlConnectionStringBuilder(cfg.Source!.ConnectionString);
            var dst = new SqlConnectionStringBuilder(cfg.Destination!.ConnectionString);

            var table = new Table().Border(TableBorder.Rounded).Title("[bold]Resumen[/]");
            table.AddColumn("Clave");
            table.AddColumn("Valor");
            table.AddRow("Origen", $"{src.DataSource} / {src.InitialCatalog}");
            table.AddRow("Destino", $"{dst.DataSource} / {dst.InitialCatalog}");
            table.AddRow("Stage", cfg.Destination.StageTable ?? string.Empty);
            table.AddRow("Final", cfg.Destination.FinalTable ?? string.Empty);
            table.AddRow("BatchSize", cfg.Options!.BatchSize.ToString());
            table.AddRow("MaxDOP", cfg.Options.MaxDegreeOfParallelism.ToString());
            table.AddRow("BulkCopyTimeout", cfg.Options.BulkCopyTimeoutSeconds.ToString());
            table.AddRow("KeepIdentity", cfg.Options.KeepIdentity ? "true" : "false");
            table.AddRow("MinRowThresholdToCommit", cfg.Options.MinRowThresholdToCommit.ToString());

            if(cfg.Incremental?.Enabled == true)
            {
                table.AddRow("[cyan]Incremental[/]", "[green]Enabled[/]");
                table.AddRow("[cyan]Tracking Mode[/]", cfg.Incremental.Mode.ToString());
                table.AddRow("[cyan]Tracking Column[/]", cfg.Incremental.TrackingColumn ?? "-");
                table.AddRow("[cyan]Merge Strategy[/]", cfg.Incremental.MergeStrategy.ToString());
            }

            AnsiConsole.Write(table);

            if(cfg.Options.MaxDegreeOfParallelism > 4)
                AnsiConsole.MarkupLine("[yellow]Aviso:[/] MaxDOP > 4 puede saturar el destino.");
        }

        // Heurística simple para detectar el primer objeto tras FROM en el SELECT origen
        static string? TryDetectBaseObjectName(string? sql)
        {
            if (string.IsNullOrWhiteSpace(sql)) return null;
            var text = System.Text.RegularExpressions.Regex.Replace(sql, @"\s+", " ").Trim();
            var m = System.Text.RegularExpressions.Regex.Match(text, @"\bFROM\s+([\[\]A-Za-z0-9_\.]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!m.Success || m.Groups.Count < 2) return null;
            var obj = m.Groups[1].Value;
            var parts = obj.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length > 0 ? parts[0] : null;
        }

        // Obtiene tipo del objeto (tabla/vista/sinónimo) si existe en la BD actual
        static string? ResolveObjectType(SqlConnection conn, string twoPartName)
        {
            using var cmd = new SqlCommand(@"
                DECLARE @obj sysname = @p;
                DECLARE @schema sysname = PARSENAME(@obj, 2);
                DECLARE @name   sysname = PARSENAME(@obj, 1);
                IF OBJECT_ID(@obj, 'U') IS NOT NULL SELECT 'USER_TABLE' AS t;
                ELSE IF OBJECT_ID(@obj, 'V') IS NOT NULL SELECT 'VIEW' AS t;
                ELSE IF EXISTS (
                    SELECT 1 FROM sys.synonyms s
                    WHERE s.name = ISNULL(@name, @obj)
                      AND SCHEMA_NAME(s.schema_id) = ISNULL(@schema, SCHEMA_NAME())
                ) SELECT 'SYNONYM' AS t;
                ELSE SELECT NULL AS t;", conn);
            cmd.Parameters.AddWithValue("@p", twoPartName);
            var res = cmd.ExecuteScalar();
            return res == DBNull.Value || res == null ? null : Convert.ToString(res);
        }

        // Cuenta filas de una tabla/vista (opcionalmente dentro de una transacción)
        static int GetRowCount(SqlConnection conn, string tableOrView, SqlTransaction? tran = null)
        {
            using var cmd = new SqlCommand($"SELECT COUNT(*) FROM {tableOrView};", conn, tran);
            var o = cmd.ExecuteScalar();
            return (o == null || o == DBNull.Value) ? 0 : Convert.ToInt32(o);
        }

        static void PrintExamples()
        {
            var eg = new Grid();
            eg.AddColumn();
            eg.AddColumn();
            eg.AddRow("Ayuda general:", "[grey]SyncJob.exe --help[/]");
            eg.AddRow("Ayuda de un comando (ver -s|--section):", "[grey]SyncJob.exe run --help[/]");
            eg.AddRow(
                "Ejecutar con config/section específicos:",
                "[grey]SyncJob.exe run -c appsettings.cliente.json -s ClienteSync[/]");
            eg.AddRow(
                "Dry-run (sin escribir en SQL):",
                "[grey]SyncJob.exe run --dry-run -c appsettings.cliente.json -s ClienteSync[/]");
            // Origen por Stored Procedure
            eg.AddRow("Usar Stored Procedure como origen:", "[grey]SyncJob.exe run -c appsettings.json -s ClienteSync --sp dbo.SP_ObtenerClientes[/]");
            eg.AddRow("SP con parámetros:", "[grey]SyncJob.exe run -c appsettings.json -s ClienteSync --sp dbo.SP_ObtenerClientes --sp-param FechaDesde=2025-01-01 --sp-param Top=1000[/]");
            eg.AddRow("Validar con SP:", "[grey]SyncJob.exe validate -c appsettings.json -s ClienteSync --sp dbo.SP_ObtenerClientes --sp-param FechaDesde=2025-01-01[/]");
            // Append / Direct
            eg.AddRow("Append a Final (sin truncar):", "[grey]SyncJob.exe run -c appsettings.json -s ClienteSync --append[/]");
            eg.AddRow("Directo a Final + append (SP):", "[grey]SyncJob.exe run -c appsettings.json -s ClienteSync --sp dbo.SP_ObtenerClientes --direct --append --min-commit 0[/]");
            eg.AddRow("Stage + sin commit (SP):", "[grey]SyncJob.exe run -c appsettings.json -s ClienteSync --sp dbo.SP_ObtenerClientes --skip-commit[/]");
            eg.AddRow(
                "Confiar en certificado del servidor (dev):",
                "[grey]SyncJob.exe run -c appsettings.json --trust-server-cert[/]");
            eg.AddRow("Forzar no cifrar (no recomendado):", "[grey]SyncJob.exe run -c appsettings.json --no-encrypt[/]");
            eg.AddRow(
                "Ajustar nombre esperado en certificado:",
                "[grey]SyncJob.exe run -c appsettings.json --host-name-in-cert server.dom.local[/]");
            eg.AddRow(
                "Limitar filas leídas (p.ej. 500):",
                "[grey]SyncJob.exe run -c appsettings.cliente.json -s ClienteSync --top 500[/]");
            eg.AddRow(
                "Top 500 y sin commit:",
                "[grey]SyncJob.exe run -c appsettings.cliente.json -s ClienteSync --top 500 --skip-commit[/]");
            eg.AddRow("Nota sobre TOP con SP:", "[grey]--top no aplica a SP; pase un parámetro --sp-param Top=N en su SP[/]");
            eg.AddRow(
                "Ajustar mínimo para commit a 0:",
                "[grey]SyncJob.exe run -c appsettings.cliente.json -s ClienteSync --min-commit 0[/]");
            eg.AddRow(
                "Validar configuración y conectividad:",
                "[grey]SyncJob.exe validate -c appsettings.cliente.json -s ClienteSync[/]");
            eg.AddRow(
                "Validate con TOP 10 en origen:",
                "[grey]SyncJob.exe validate -c appsettings.cliente.json -s ClienteSync --probe-top 10[/]");
            eg.AddRow(
                "Generar appsettings desde campos:",
                "[grey]SyncJob.exe config init -f campos.txt -o appsettings.cliente.json -s ClienteSync[/]");
            eg.AddRow(
                "Alias de init:",
                "[grey]SyncJob.exe config-init -f campos.txt -o appsettings.cliente.json -s ClienteSync[/]");

            // Logging examples
            eg.AddRow("Logs JSON con nivel Debug:",
                "[grey]SyncJob.exe run -c appsettings.json --json-log --log-level Debug[/]");
            eg.AddRow("Log a archivo específico y sin consola:",
                "[grey]SyncJob.exe validate -c appsettings.json --log-file C:\\Logs\\syncjob.log --quiet[/]");
            eg.AddRow("Log a un directorio custom:",
                "[grey]SyncJob.exe run -c appsettings.json --log-dir C:\\Logs[/]");

            // INCREMENTAL SYNC examples
            eg.AddRow("[bold cyan]--- SINCRONIZACIÓN INCREMENTAL ---[/]", "");
            eg.AddRow("Inicializar tabla de tracking:",
                "[grey]SyncJob.exe run -c appsettings.json -s IncrementalSync --init-tracking[/]");
            eg.AddRow("Primera ejecución (full):",
                "[grey]SyncJob.exe run -c appsettings.json -s IncrementalSync[/]");
            eg.AddRow("Siguiente ejecución (solo cambios):",
                "[grey]SyncJob.exe run -c appsettings.json -s IncrementalSync[/]");
            eg.AddRow("Forzar full refresh:",
                "[grey]SyncJob.exe run -c appsettings.json -s IncrementalSync --full-refresh[/]");
            eg.AddRow("Incremental con Timestamp:",
                "[grey]Ver appsettings: Incremental.Mode = Timestamp, TrackingColumn = FechaModificacion[/]");
            eg.AddRow("Incremental con RowVersion:",
                "[grey]Ver appsettings: Incremental.Mode = RowVersion, TrackingColumn = RowVer[/]");
            eg.AddRow("Incremental con MERGE (Upsert):",
                "[grey]Ver appsettings: Incremental.MergeStrategy = Upsert, PrimaryKeyColumns = [IdCliente][/]");

            AnsiConsole.MarkupLine("[bold underline]Ejemplos[/]");
            AnsiConsole.Write(eg);
            AnsiConsole.Write(new Spectre.Console.Rule());
        }

        static void PrintHelpfulHints(Exception ex, ConfigSettings? settings)
        {
            string all = ex.ToString();
            bool isUntrustedCert = all.IndexOf(
                    "certificate chain was issued by an authority that is not trusted",
                    StringComparison.OrdinalIgnoreCase) >=
                0 ||
                all.IndexOf("SSL Provider", StringComparison.OrdinalIgnoreCase) >= 0 &&
                all.IndexOf("not trusted", StringComparison.OrdinalIgnoreCase) >= 0;

            if(isUntrustedCert)
            {
                AnsiConsole.MarkupLine("[yellow]Sugerencias para error de certificado no confiable[/]:");
                AnsiConsole.MarkupLine("- Instalar la CA emisora en ‘Trusted Root Certification Authorities’. ");
                AnsiConsole.MarkupLine(
                    "- Usar [grey]--trust-server-cert[/] (desarrollo) para omitir validación de cadena.");
                AnsiConsole.MarkupLine(
                    "- Si te conectas por IP/alias, especifica el FQDN con [grey]--host-name-in-cert <FQDN>[/].");
                AnsiConsole.MarkupLine("- Como último recurso, [grey]--no-encrypt[/] (no recomendado).");

                string exe = "SyncJob.exe";
                string cfg = settings?.ConfigPath ?? "appsettings.json";
                AnsiConsole.MarkupLine("Ejemplos:");
                AnsiConsole.MarkupLine($"[grey]{exe} validate -c {cfg} --trust-server-cert[/]");
                AnsiConsole.MarkupLine($"[grey]{exe} run -c {cfg} --trust-server-cert[/]");
                AnsiConsole.Write(new Spectre.Console.Rule());
            }
        }

        static void InitLogging(ConfigSettings s)
        {
            var min = ParseLevel(s.LogLevel) ?? Log.Level.Info;
            var opts = new Log.Options
            {
                EnableConsole = !s.Quiet,
                JsonFormat = s.JsonLog,
                FilePath = s.LogFile,
                DirectoryPath = s.LogDir ?? "logs",
                MinLevel = min,
                AppName = "SyncJob"
            };
            Log.Init(opts);
            Log.Info("Logger initialized", evt: "log.init");
        }

        static Log.Level? ParseLevel(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var t = text.Trim();
            return t.Equals("trace", StringComparison.OrdinalIgnoreCase) ? Log.Level.Trace
                : t.Equals("debug", StringComparison.OrdinalIgnoreCase) ? Log.Level.Debug
                : t.Equals("info", StringComparison.OrdinalIgnoreCase) || t.Equals("information", StringComparison.OrdinalIgnoreCase) ? Log.Level.Info
                : t.Equals("warn", StringComparison.OrdinalIgnoreCase) || t.Equals("warning", StringComparison.OrdinalIgnoreCase) ? Log.Level.Warn
                : t.Equals("error", StringComparison.OrdinalIgnoreCase) ? Log.Level.Error
                : t.Equals("fatal", StringComparison.OrdinalIgnoreCase) || t.Equals("critical", StringComparison.OrdinalIgnoreCase) ? Log.Level.Fatal
                : (Log.Level?)null;
        }

        // Ejecuta un SELECT TOP N del origen y mide el tiempo
        static (long elapsedMs, int rows) ProbeSourceTopN(SyncConfig cfg, int topN, int commandTimeoutSeconds)
        {
            if(cfg.Source == null || string.IsNullOrWhiteSpace(cfg.Source.ConnectionString))
                throw new ArgumentException("Config de Source inválida.");

            using var conn = new SqlConnection(cfg.Source.ConnectionString);
            conn.Open();

            // Caso Query: envolver con TOP N
            if(!string.IsNullOrWhiteSpace(cfg.Source.Query))
            {
                string probeSql = $"SELECT TOP {topN} * FROM ( {cfg.Source.Query} ) AS _probe_";
                using var cmd = new SqlCommand(probeSql, conn) { CommandTimeout = commandTimeoutSeconds };
                var sw = System.Diagnostics.Stopwatch.StartNew();
                int count = 0;
                using(var reader = cmd.ExecuteReader(CommandBehavior.SequentialAccess | CommandBehavior.SingleResult))
                {
                    while(reader.Read())
                    {
                        count++;
                    }
                }
                sw.Stop();
                return (sw.ElapsedMilliseconds, count);
            }

            // Caso Stored Procedure: ejecutar y contar hasta topN
            if(!string.IsNullOrWhiteSpace(cfg.Source.StoredProcedure))
            {
                using var cmd = new SqlCommand(cfg.Source.StoredProcedure, conn)
                {
                    CommandType = CommandType.StoredProcedure,
                    CommandTimeout = commandTimeoutSeconds
                };
                AddStoredProcParams(cmd, cfg.Source.Parameters);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                int count = 0;
                using(var reader = cmd.ExecuteReader(CommandBehavior.SequentialAccess | CommandBehavior.SingleResult))
                {
                    while(reader.Read())
                    {
                        count++;
                        if(count >= topN) break;
                    }
                }
                sw.Stop();
                return (sw.ElapsedMilliseconds, count);
            }

            throw new ArgumentException("Source.Query o Source.StoredProcedure debe estar definido.");
        }

        static void ApplyOverrides(SyncConfig cfg, ConfigSettings s)
        {
            if(cfg.Options == null)
                cfg.Options = new SyncOptions();
            if(s.MaxDop.HasValue)
                cfg.Options.MaxDegreeOfParallelism = Math.Max(1, s.MaxDop.Value);
            if(s.BatchSize.HasValue)
                cfg.Options.BatchSize = Math.Max(1, s.BatchSize.Value);
            if(s.MinCommit.HasValue)
                cfg.Options.MinRowThresholdToCommit = Math.Max(0, s.MinCommit.Value);

            // Connection string overrides
            if(cfg.Source?.ConnectionString != null)
            {
                var csb = new SqlConnectionStringBuilder(cfg.Source.ConnectionString);
                if(s.TrustServerCertificate)
                    csb.TrustServerCertificate = true;
                if(s.NoEncrypt)
                    csb.Encrypt = false;
                if(!s.NoEncrypt && s.TrustServerCertificate)
                    csb.Encrypt = true;
                if(!string.IsNullOrWhiteSpace(s.HostNameInCertificate))
                    csb["HostNameInCertificate"] = s.HostNameInCertificate!;
                if(s.Top.HasValue && s.Top.Value > 0 && !string.IsNullOrWhiteSpace(cfg.Source.Query))
                    cfg.Source.Query = $"SELECT TOP {s.Top.Value} * FROM ( {cfg.Source.Query} ) AS _src_";
                cfg.Source.ConnectionString = csb.ToString();
            }
            if(cfg.Destination?.ConnectionString != null)
            {
                var cdb = new SqlConnectionStringBuilder(cfg.Destination.ConnectionString);
                if(s.TrustServerCertificate)
                    cdb.TrustServerCertificate = true;
                if(s.NoEncrypt)
                    cdb.Encrypt = false;
                if(!s.NoEncrypt && s.TrustServerCertificate)
                    cdb.Encrypt = true;
                if(!string.IsNullOrWhiteSpace(s.HostNameInCertificate))
                    cdb["HostNameInCertificate"] = s.HostNameInCertificate!;
                cfg.Destination.ConnectionString = cdb.ToString();
            }

            // Overrides de Stored Procedure y parámetros
            if(cfg.Source != null)
            {
                if(!string.IsNullOrWhiteSpace(s.StoredProcedure))
                {
                    cfg.Source.StoredProcedure = s.StoredProcedure;
                    cfg.Source.Query = null; // evitar ambigüedad
                }
                if(s.SpParams != null && s.SpParams.Length > 0)
                {
                    cfg.Source.Parameters = ParseNameValuePairs(s.SpParams);
                }
                if(cfg.Source.StoredProcedure != null && s.Top.HasValue && s.Top.Value > 0)
                {
                    Log.Warn("--top no aplica automáticamente a Stored Procedures. Considere un parámetro @Top en su SP y páselo con --sp-param.", evt: "override.sp.top.ignore");
                }
            }
        }

        static SyncConfig LoadConfig(string path, string section)
        {
            var json = File.ReadAllText(path);
            var root = JsonSerializer.Deserialize<Dictionary<string, SyncConfig>>(
                json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if(root == null || !root.TryGetValue(section, out var cfg) || cfg == null)
                throw new Exception($"No se encontró la sección '{section}' en {path}");
            return cfg;
        }

        static List<string> ReadFieldsFile(string path)
        {
            var lines = File.ReadAllLines(path);
            return lines
                .Select(l => l.Trim())
                .Where(l => !string.IsNullOrWhiteSpace(l) && !l.StartsWith("#") && !l.StartsWith("//"))
                .ToList();
        }

        static SyncConfig CreateConfigFromFields(List<string> fields, InitSettings s)
        {
            var mappings = fields.Select(f => new ColumnMap { Source = f, Dest = f }).ToList();
            string selectList = string.Join(", ", fields);

            var cfg = new SyncConfig
            {
                Source =
                    new SourceConfig
                    {
                        ConnectionString = "Server=SERVIDOR_ORIGEN;Database=DB;User Id=...;Password=...;",
                        Query = $"SELECT {selectList} FROM dbo.TuFuente"
                    },
                Destination =
                    new DestConfig
                    {
                        ConnectionString = "Server=SERVIDOR_DESTINO;Database=DB;User Id=...;Password=...;",
                        StageTable = s.Stage,
                        FinalTable = s.Final
                    },
                ColumnMappings = mappings,
                Options =
                    new SyncOptions
                    {
                        BatchSize = s.BatchSize ?? 10000,
                        MaxDegreeOfParallelism = s.MaxDop ?? 4,
                        BulkCopyTimeoutSeconds = 0,
                        KeepIdentity = s.KeepIdentity ?? true,
                        MinRowThresholdToCommit = s.MinCommit ?? 1000
                    }
            };
            return cfg;
        }

        static void WriteSectionToAppSettings(string outputPath, string section, SyncConfig cfg, bool overwrite)
        {
            Dictionary<string, SyncConfig> root;
            if(File.Exists(outputPath))
            {
                var json = File.ReadAllText(outputPath);
                root = JsonSerializer.Deserialize<Dictionary<string, SyncConfig>>(
                        json,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ??
                    new Dictionary<string, SyncConfig>(StringComparer.OrdinalIgnoreCase);
            } else
            {
                root = new Dictionary<string, SyncConfig>(StringComparer.OrdinalIgnoreCase);
            }

            if(root.ContainsKey(section) && !overwrite)
                throw new Exception($"La sección '{section}' ya existe. Usa --overwrite para reemplazarla.");

            root[section] = cfg;

            var options = new JsonSerializerOptions { WriteIndented = true };
            var jsonOut = JsonSerializer.Serialize(root, options);
            var dir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
            if(!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(outputPath, jsonOut);
        }

        static void ValidateConfig(SyncConfig cfg, bool directMode = false)
        {
            if(cfg.Source == null)
                throw new ArgumentException("Missing Source config.");
            if(cfg.Destination == null)
                throw new ArgumentException("Missing Destination config.");
            if(cfg.Options == null)
                throw new ArgumentException("Missing Options config.");
            if(cfg.ColumnMappings == null || cfg.ColumnMappings.Count == 0)
                throw new ArgumentException("ColumnMappings es obligatorio y no puede estar vacío.");

            if(string.IsNullOrWhiteSpace(cfg.Source.ConnectionString))
                throw new ArgumentException("Source.ConnectionString vacío.");
            if(string.IsNullOrWhiteSpace(cfg.Source.Query) && string.IsNullOrWhiteSpace(cfg.Source.StoredProcedure))
                throw new ArgumentException("Source.Query o Source.StoredProcedure vacío. Debes especificar uno.");

            if(string.IsNullOrWhiteSpace(cfg.Destination.ConnectionString))
                throw new ArgumentException("Destination.ConnectionString vacío.");
            if(string.IsNullOrWhiteSpace(cfg.Destination.FinalTable))
                throw new ArgumentException("Destination.FinalTable vacío.");
            if(!directMode)
            {
                if(string.IsNullOrWhiteSpace(cfg.Destination.StageTable))
                    throw new ArgumentException("Destination.StageTable vacío.");
                if(string.Equals(
                    cfg.Destination.StageTable,
                    cfg.Destination.FinalTable,
                    StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("Destination.StageTable y Destination.FinalTable no pueden ser iguales.");
            }

            if(cfg.Options.BatchSize <= 0)
                throw new ArgumentException("Options.BatchSize debe ser > 0.");
            if(cfg.Options.MaxDegreeOfParallelism <= 0)
                throw new ArgumentException("Options.MaxDegreeOfParallelism debe ser > 0.");
            if(cfg.Options.MinRowThresholdToCommit < 0)
                throw new ArgumentException("Options.MinRowThresholdToCommit no puede ser negativo.");
        }

        static SourceDataPackage ReadSourceData(SyncConfig cfg)
        {
            if(cfg.Source == null)
                throw new ArgumentNullException(nameof(cfg.Source), "Falta la configuración de Source.");
            if(string.IsNullOrWhiteSpace(cfg.Source.ConnectionString))
                throw new ArgumentException("Source.ConnectionString vacío.");
            if(string.IsNullOrWhiteSpace(cfg.Source.Query) && string.IsNullOrWhiteSpace(cfg.Source.StoredProcedure))
                throw new ArgumentException("Source.Query o Source.StoredProcedure vacío.");

            var package = new SourceDataPackage { Rows = new List<object[]>(capacity: 200000) };

            using var sourceConn = new SqlConnection(cfg.Source.ConnectionString);
            sourceConn.Open();
            try
            {
                var csb = new SqlConnectionStringBuilder(cfg.Source.ConnectionString);
                Log.Info($"Conexion origen OK: {csb.DataSource} / {csb.InitialCatalog}", evt: "source.connect.ok");
            }
            catch { }

            // Reporte de origen: Query (intenta detectar objeto base) o SP
            if(!string.IsNullOrWhiteSpace(cfg.Source.Query))
            {
                var baseObj = TryDetectBaseObjectName(cfg.Source.Query);
                if (!string.IsNullOrWhiteSpace(baseObj))
                {
                    try
                    {
                        var t = ResolveObjectType(sourceConn, baseObj!);
                        if (!string.IsNullOrWhiteSpace(t))
                            Log.Info($"Objeto origen: {baseObj} ({t})", evt: "source.baseobject");
                        else
                            Log.Warn($"Objeto origen no encontrado: {baseObj}", evt: "source.baseobject.missing");
                    }
                    catch { }
                }
                else
                {
                    Log.Debug("No se pudo detectar objeto base del SELECT", evt: "source.baseobject.unknown");
                }
            }
            else if(!string.IsNullOrWhiteSpace(cfg.Source.StoredProcedure))
            {
                Log.Info($"Stored Procedure origen: {cfg.Source.StoredProcedure}", evt: "source.sp");
            }

            using var cmd = !string.IsNullOrWhiteSpace(cfg.Source.Query)
                ? new SqlCommand(cfg.Source.Query, sourceConn) { CommandType = CommandType.Text }
                : new SqlCommand(cfg.Source.StoredProcedure!, sourceConn) { CommandType = CommandType.StoredProcedure };
            if(cmd.CommandType == CommandType.StoredProcedure)
                AddStoredProcParams(cmd, cfg.Source.Parameters);
            using var reader = cmd.ExecuteReader(CommandBehavior.SequentialAccess);

            int fieldCount = reader.FieldCount;
            var colNames = new string[fieldCount];
            for(int i = 0; i < fieldCount; i++)
                colNames[i] = reader.GetName(i);
            package.SourceColumnNames = colNames;
            Log.Info($"Esquema origen OK: {fieldCount} columnas", evt: "source.schema.ok");

            while(reader.Read())
            {
                var values = new object[fieldCount];
                reader.GetValues(values);
                package.Rows.Add(values);
            }

            return package;
        }

        // Solo nombres de columnas del origen sin cargar filas
        static string[] ReadSourceSchema(SyncConfig cfg)
        {
            if(cfg.Source == null ||
                string.IsNullOrWhiteSpace(cfg.Source.ConnectionString) ||
                (string.IsNullOrWhiteSpace(cfg.Source.Query) && string.IsNullOrWhiteSpace(cfg.Source.StoredProcedure)))
                throw new ArgumentException("Config de Source inválida.");

            using var sourceConn = new SqlConnection(cfg.Source.ConnectionString);
            sourceConn.Open();
            try
            {
                var csb = new SqlConnectionStringBuilder(cfg.Source.ConnectionString);
                Log.Info($"Conexion origen OK: {csb.DataSource} / {csb.InitialCatalog}", evt: "source.connect.ok");
            }
            catch { }

            // Obtener esquema: Query (TOP 0 wrapper) o SP (SchemaOnly)
            SqlCommand cmd;
            if(!string.IsNullOrWhiteSpace(cfg.Source.Query))
            {
                string wrapped = $"SELECT TOP 0 * FROM ( {cfg.Source.Query} ) AS _src_";
                cmd = new SqlCommand(wrapped, sourceConn) { CommandType = CommandType.Text };
            }
            else
            {
                cmd = new SqlCommand(cfg.Source.StoredProcedure!, sourceConn) { CommandType = CommandType.StoredProcedure };
                AddStoredProcParams(cmd, cfg.Source.Parameters);
            }
            using var reader = cmd.ExecuteReader(CommandBehavior.SchemaOnly);
            int fieldCount = reader.FieldCount;
            var cols = new string[fieldCount];
            for(int i = 0; i < fieldCount; i++)
                cols[i] = reader.GetName(i);
            Log.Info($"Esquema origen OK: {fieldCount} columnas", evt: "source.schema.ok");
            return cols;
        }

        // Helper: agrega parámetros del SP desde diccionario (clave=valor)
        static void AddStoredProcParams(SqlCommand cmd, Dictionary<string, string>? parameters)
        {
            if (parameters == null) return;
            foreach (var kvp in parameters)
            {
                var name = kvp.Key?.Trim();
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (!name!.StartsWith("@")) name = "@" + name;
                object value = kvp.Value ?? string.Empty;
                cmd.Parameters.AddWithValue(name, value);
            }
        }

        // Helper: parsea lista NAME=VALUE a diccionario
        static Dictionary<string, string> ParseNameValuePairs(IEnumerable<string> pairs)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (pairs == null) return dict;
            foreach (var p in pairs)
            {
                if (string.IsNullOrWhiteSpace(p)) continue;
                var idx = p.IndexOf('=');
                if (idx <= 0)
                {
                    dict[p.Trim()] = string.Empty;
                }
                else
                {
                    var key = p.Substring(0, idx).Trim();
                    var val = p.Substring(idx + 1).Trim();
                    dict[key] = val;
                }
            }
            return dict;
        }

        static void LoadStageInParallel(SyncConfig cfg, SourceDataPackage data)
        {
            if(cfg.Destination == null)
                throw new ArgumentNullException(nameof(cfg.Destination));
            if(cfg.Options == null)
                throw new ArgumentNullException(nameof(cfg.Options));
            if(cfg.ColumnMappings == null || cfg.ColumnMappings.Count == 0)
                throw new ArgumentException("ColumnMappings vacío. Es obligatorio.");

            using(var destConn = new SqlConnection(cfg.Destination.ConnectionString))
            {
                destConn.Open();
                try
                {
                    var cdb = new SqlConnectionStringBuilder(cfg.Destination.ConnectionString);
                    Log.Info($"Conexion destino OK: {cdb.DataSource} / {cdb.InitialCatalog}", evt: "dest.connect.ok");
                }
                catch { }

                // Verificar Stage existe
                try
                {
                    var t = ResolveObjectType(destConn, cfg.Destination.StageTable!);
                    if (!string.IsNullOrWhiteSpace(t))
                        Log.Info($"Stage encontrado: {cfg.Destination.StageTable} ({t})", evt: "dest.stage.exists");
                    else
                        Log.Warn($"Stage no encontrado: {cfg.Destination.StageTable}", evt: "dest.stage.missing");
                }
                catch { }

                // Conteo antes de TRUNCATE Stage
                try
                {
                    int stageBefore = GetRowCount(destConn, cfg.Destination.StageTable!);
                    Log.Info($"Stage antes de TRUNCATE: {stageBefore}", evt: "dest.stage.count.before_truncate");
                }
                catch { }

                using var cmdTrunc = new SqlCommand($"TRUNCATE TABLE {cfg.Destination.StageTable};", destConn);
                cmdTrunc.ExecuteNonQuery();
                Log.Info("TRUNCATE Stage completado", evt: "dest.stage.truncate.done");
            }

            var stageSchema = GetStageSchema(cfg);
            var destToSourceIndex = BuildDestToSourceIndex(cfg, data.SourceColumnNames);

            var batches = SplitIntoBatches(data.Rows, cfg.Options.BatchSize);

            var bulkOptions = cfg.Options.KeepIdentity
                ? SqlBulkCopyOptions.KeepIdentity | SqlBulkCopyOptions.TableLock
                : SqlBulkCopyOptions.TableLock;

            Parallel.ForEach(
                batches,
                new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, cfg.Options.MaxDegreeOfParallelism) },
                batch =>
                {
                    using var destConn = new SqlConnection(cfg.Destination.ConnectionString);
                    destConn.Open();

                    var dt = BuildDataTableForBatch(stageSchema, destToSourceIndex, batch);

                    using var bulk = new SqlBulkCopy(destConn, bulkOptions, null)
                    {
                        DestinationTableName = cfg.Destination.StageTable,
                        BulkCopyTimeout = cfg.Options.BulkCopyTimeoutSeconds,
                        BatchSize = cfg.Options.BatchSize
                    };

                    foreach(var map in cfg.ColumnMappings)
                    {
                        if(!string.IsNullOrWhiteSpace(map.Dest))
                            bulk.ColumnMappings.Add(map.Dest, map.Dest);
                    }

                    bulk.WriteToServer(dt);
                });
        }

        static IEnumerable<List<object[]>> SplitIntoBatches(List<object[]> allRows, int batchSize)
        {
            if(batchSize <= 0)
                throw new ArgumentOutOfRangeException(nameof(batchSize));
            for(int i = 0; i < allRows.Count; i += batchSize)
                yield return allRows.GetRange(i, Math.Min(batchSize, allRows.Count - i));
        }

        static DataTable GetStageSchema(SyncConfig cfg)
        {
            using var destConn = new SqlConnection(cfg.Destination!.ConnectionString);
            destConn.Open();
            using var schemaCmd = new SqlCommand($"SELECT TOP 0 * FROM {cfg.Destination.StageTable};", destConn);
            using var schemaAdapter = new SqlDataAdapter(schemaCmd);
            var dt = new DataTable();
            schemaAdapter.Fill(dt);
            Log.Info($"Esquema Stage OK: {dt.Columns.Count} columnas", evt: "dest.stage.schema.ok");
            return dt;
        }

        static void LoadFinalDirect(SyncConfig cfg, SourceDataPackage data, bool append)
        {
            if(cfg.Destination == null)
                throw new ArgumentNullException(nameof(cfg.Destination));
            if(cfg.Options == null)
                throw new ArgumentNullException(nameof(cfg.Options));
            if(cfg.ColumnMappings == null || cfg.ColumnMappings.Count == 0)
                throw new ArgumentException("ColumnMappings vacío. Es obligatorio.");

            using(var destConn = new SqlConnection(cfg.Destination.ConnectionString))
            {
                destConn.Open();
                try
                {
                    var cdb = new SqlConnectionStringBuilder(cfg.Destination.ConnectionString);
                    Log.Info($"Conexion destino OK: {cdb.DataSource} / {cdb.InitialCatalog}", evt: "dest.connect.ok");
                }
                catch { }

                // Verificar Final existe
                try
                {
                    var t = ResolveObjectType(destConn, cfg.Destination.FinalTable!);
                    if (!string.IsNullOrWhiteSpace(t))
                        Log.Info($"Final encontrado: {cfg.Destination.FinalTable} ({t})", evt: "dest.final.exists");
                    else
                        Log.Warn($"Final no encontrado: {cfg.Destination.FinalTable}", evt: "dest.final.missing");
                }
                catch { }

                // Conteo antes de operación en Final
                try
                {
                    int finalBefore = GetRowCount(destConn, cfg.Destination.FinalTable!);
                    if(append)
                        Log.Info($"Final antes de APPEND: {finalBefore}", evt: "dest.final.count.before_append");
                    else
                        Log.Info($"Final antes de TRUNCATE: {finalBefore}", evt: "dest.final.count.before_truncate");
                }
                catch { }

                if(!append)
                {
                    using var cmdTrunc = new SqlCommand($"TRUNCATE TABLE {cfg.Destination.FinalTable};", destConn);
                    cmdTrunc.ExecuteNonQuery();
                    Log.Info("TRUNCATE Final completado", evt: "dest.final.truncate.done");
                }
            }

            var finalSchema = GetFinalSchema(cfg);
            var destToSourceIndex = BuildDestToSourceIndex(cfg, data.SourceColumnNames);

            var batches = SplitIntoBatches(data.Rows, cfg.Options.BatchSize);

            var bulkOptions = cfg.Options.KeepIdentity
                ? SqlBulkCopyOptions.KeepIdentity | SqlBulkCopyOptions.TableLock
                : SqlBulkCopyOptions.TableLock;

            Parallel.ForEach(
                batches,
                new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, cfg.Options.MaxDegreeOfParallelism) },
                batch =>
                {
                    using var destConn = new SqlConnection(cfg.Destination.ConnectionString);
                    destConn.Open();

                    var dt = BuildDataTableForBatch(finalSchema, destToSourceIndex, batch);

                    using var bulk = new SqlBulkCopy(destConn, bulkOptions, null)
                    {
                        DestinationTableName = cfg.Destination.FinalTable,
                        BulkCopyTimeout = cfg.Options.BulkCopyTimeoutSeconds,
                        BatchSize = cfg.Options.BatchSize
                    };

                    foreach(var map in cfg.ColumnMappings)
                    {
                        if(!string.IsNullOrWhiteSpace(map.Dest))
                            bulk.ColumnMappings.Add(map.Dest, map.Dest);
                    }

                    bulk.WriteToServer(dt);
                });

            // Conteo post-carga
            try
            {
                using var destConn = new SqlConnection(cfg.Destination.ConnectionString);
                destConn.Open();
                int finalAfter = GetRowCount(destConn, cfg.Destination.FinalTable!);
                Log.Info($"Final despues de carga directa{(append ? " (append)" : string.Empty)}: {finalAfter}", evt: "dest.final.count.after_direct");
            }
            catch { }
        }

        static DataTable GetFinalSchema(SyncConfig cfg)
        {
            using var destConn = new SqlConnection(cfg.Destination!.ConnectionString);
            destConn.Open();
            using var schemaCmd = new SqlCommand($"SELECT TOP 0 * FROM {cfg.Destination.FinalTable};", destConn);
            using var schemaAdapter = new SqlDataAdapter(schemaCmd);
            var dt = new DataTable();
            schemaAdapter.Fill(dt);
            Log.Info($"Esquema Final OK: {dt.Columns.Count} columnas", evt: "dest.final.schema.ok");
            return dt;
        }

        static void TestConnectivity(SyncConfig cfg)
        {
            using(var c1 = new SqlConnection(cfg.Source!.ConnectionString))
            {
                c1.Open();
                try
                {
                    var csb = new SqlConnectionStringBuilder(cfg.Source.ConnectionString);
                    Log.Info($"Conexion origen OK: {csb.DataSource} / {csb.InitialCatalog}", evt: "source.connect.ok");
                }
                catch { }
            }
            using(var c2 = new SqlConnection(cfg.Destination!.ConnectionString))
            {
                c2.Open();
                try
                {
                    var cdb = new SqlConnectionStringBuilder(cfg.Destination.ConnectionString);
                    Log.Info($"Conexion destino OK: {cdb.DataSource} / {cdb.InitialCatalog}", evt: "dest.connect.ok");
                }
                catch { }
            }
        }

        static Dictionary<string, int> BuildDestToSourceIndex(SyncConfig cfg, string[] sourceColumnNames)
        {
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach(var m in cfg.ColumnMappings!)
            {
                if(string.IsNullOrWhiteSpace(m.Source) || string.IsNullOrWhiteSpace(m.Dest))
                    throw new ArgumentException("ColumnMappings contiene entradas vacías.");

                int idx = Array.FindIndex(
                    sourceColumnNames,
                    c => c.Equals(m.Source, StringComparison.OrdinalIgnoreCase));
                if(idx == -1)
                    throw new Exception($"Columna origen '{m.Source}' no existe en el SELECT.");

                map[m.Dest] = idx;
            }
            return map;
        }

        static DataTable BuildDataTableForBatch(
            DataTable stageSchema,
            Dictionary<string, int> destColToSourceIndex,
            List<object[]> batchRows)
        {
            var dt = stageSchema.Clone();
            dt.BeginLoadData();
            foreach(var rowValues in batchRows)
            {
                var newRow = dt.NewRow();
                foreach(DataColumn destCol in dt.Columns)
                {
                    if(destColToSourceIndex.TryGetValue(destCol.ColumnName, out int srcIdx))
                    {
                        var val = rowValues[srcIdx];
                        newRow[destCol.ColumnName] = val ?? DBNull.Value;
                    }
                }
                dt.Rows.Add(newRow);
            }
            dt.EndLoadData();
            return dt;
        }

        static void CommitStageToFinal(SyncConfig cfg, int rowCount, bool append)
        {
            using(var destConn = new SqlConnection(cfg.Destination!.ConnectionString))
            {
                destConn.Open();
                using(var tran = destConn.BeginTransaction())
                {
                    try
                    {
                        // Final: existencia y conteo previo
                        try
                        {
                            var t = ResolveObjectType(destConn, cfg.Destination.FinalTable!);
                            if (!string.IsNullOrWhiteSpace(t))
                                Log.Info($"Final encontrado: {cfg.Destination.FinalTable} ({t})", evt: "dest.final.exists");
                            else
                                Log.Warn($"Final no encontrado: {cfg.Destination.FinalTable}", evt: "dest.final.missing");
                        }
                        catch { }

                        try
                        {
                            int finalBefore = GetRowCount(destConn, cfg.Destination.FinalTable!, tran);
                            if(append)
                                Log.Info($"Final antes de APPEND: {finalBefore}", evt: "dest.final.count.before_append");
                            else
                                Log.Info($"Final antes de TRUNCATE: {finalBefore}", evt: "dest.final.count.before_truncate");
                        }
                        catch { }

                        int stageCount;
                        using(var cmdCount = new SqlCommand(
                            $"SELECT COUNT(*) FROM {cfg.Destination.StageTable};",
                            destConn,
                            tran))
                        {
                            stageCount = (int)cmdCount.ExecuteScalar();
                        }

                        if(stageCount != rowCount)
                            throw new Exception(
                                $"Rowcount mismatch: stage={stageCount} vs leído={rowCount}. Abortando swap.");

                        string swapSql = append
                            ? $@"INSERT INTO {cfg.Destination.FinalTable}
                                SELECT * FROM {cfg.Destination.StageTable};"
                            : $@"TRUNCATE TABLE {cfg.Destination.FinalTable};
                                INSERT INTO {cfg.Destination.FinalTable}
                                SELECT * FROM {cfg.Destination.StageTable};";

                        using(var cmdSwap = new SqlCommand(swapSql, destConn, tran))
                        {
                            cmdSwap.ExecuteNonQuery();
                        }

                        tran.Commit();

                        try
                        {
                            int finalAfter = GetRowCount(destConn, cfg.Destination.FinalTable!);
                            Log.Info($"Final despues de commit{(append ? " (append)" : string.Empty)}: {finalAfter}", evt: "dest.final.count.after_commit");
                        }
                        catch { }
                    } catch
                    {
                        try
                        {
                            tran.Rollback();
                        } catch
                        {
                        }
                        throw;
                    }
                }
            }
        }
    }
}
