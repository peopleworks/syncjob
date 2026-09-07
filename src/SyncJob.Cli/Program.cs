using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using Spectre.Console.Cli;
using SyncJob.Commands;
using SyncJob.Core;
using SyncJob.Core.Incremental;
using SyncJob.Core.Model;
using SyncJob.Core.Run;
using SyncJob.Engine;
using SyncJob.Security;
using SyncJob.Services;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

// La ruta de run se prueba contra dos bases de verdad, y para eso hay que poder
// llamar al mismo punto de entrada que escribe el operador. Una prueba que llama a
// otra cosa prueba otra cosa.
[assembly: InternalsVisibleTo("SyncJob.IntegrationTests")]

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

        [Description("Si el origen queda por debajo de --min-commit, no publicar y seguir (en vez de abortar)")]
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
        [Description("No escribe en el destino. Copia el origen a una stage propia y dice qué habría pasado.")]
        [CommandOption("--dry-run")]
        public bool DryRun { get; set; }

        [Description("En desuso: el motor siempre pasa por una stage que crea él mismo. Se acepta y se explica en la corrida.")]
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

        [Description("Ejecutar TODAS las secciones del archivo, en orden")]
        [CommandOption("--all")]
        public bool All { get; set; }

        [Description("Con --all: seguir con las demás secciones aunque una falle")]
        [CommandOption("--continue-on-error")]
        public bool ContinueOnError { get; set; }
    }

    public sealed class ValidateSettings : ConfigSettings
    {
        [Description("En desuso: la validación siempre comprueba la tabla Final.")]
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
        /// Ejecuta como CLI (comportamiento actual).
        ///
        /// Internal y no privado para que las pruebas en vivo entren por aqui, que es
        /// exactamente por donde entra el operador: con el mismo parser, las mismas
        /// banderas y los mismos codigos de salida.
        /// </summary>
        internal static int RunAsCli(string[] args)
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

                    config.AddBranch("secrets", sec =>
                    {
                        sec.SetDescription("Cifrado de contraseñas del appsettings.json (DPAPI)");
                        sec.AddCommand<SecretsProtectCommand>("protect")
                           .WithDescription("Cifra las contraseñas del archivo")
                           .WithExample(new[] { "secrets", "protect", "-c", "appsettings.json" })
                           .WithExample(new[] { "secrets", "protect", "-c", "appsettings.json", "--scope", "machine" });
                        sec.AddCommand<SecretsStatusCommand>("status")
                           .WithDescription("Muestra qué contraseñas están cifradas y cuáles no")
                           .WithExample(new[] { "secrets", "status", "-c", "appsettings.json" });
                    });

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
        public sealed class RunCommand : AsyncCommand<RunSettings>
        {
            /// <summary>
            /// Carga, adapta, ejecuta e informa. Nada mas.
            ///
            /// La tuberia entera - leer el origen, armar el stage, decidir si se
            /// publica y publicar - vive en SyncJob.Core y ya no esta escrita aqui.
            /// Estaba escrita aqui, en el servicio de Windows y en el comando central,
            /// tres veces, y las tres se separaron: esta tenia la lista explicita de
            /// columnas, el intercambio por nombres y el piso de filas, y las otras dos
            /// no. Un arreglo que hay que aplicar tres veces se aplica una.
            /// </summary>
            public override async Task<int> ExecuteAsync(CommandContext context, RunSettings settings)
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

                    return settings.All
                        ? await EjecutarTodasLasSecciones(settings)
                        : await EjecutarSeccion(settings, settings.Section, CancellationToken.None);
                } catch(Exception ex)
                {
                    AnsiConsole.WriteException(
                        ex,
                        ExceptionFormats.ShortenPaths | ExceptionFormats.ShortenTypes | ExceptionFormats.ShortenMethods);
                    Log.Error("Run failed", ex, evt: "run.error");
                    PrintHelpfulHints(ex, settings);
                    return 1;
                } finally
                {
                    Log.Shutdown();
                }
            }
        }

        /// <summary>
        /// <c>--all</c>: una seccion por vez, por la MISMA ruta que una sola.
        ///
        /// Lo unico que cambia entre una vuelta y otra es el nombre de la seccion. Antes
        /// se clonaba el RunSettings campo por campo, y la copia se olvidaba de
        /// --min-commit, --top, --batch-size, --maxdop, --sp, --probe-top y de las
        /// opciones de certificado: quien escribia <c>run --all --min-commit 0</c>
        /// obtenia un piso que no habia pedido y no se enteraba. Pasar el nombre y no
        /// una copia hace que esa clase de olvido no se pueda escribir.
        /// </summary>
        private static async Task<int> EjecutarTodasLasSecciones(RunSettings settings)
        {
            var secciones = ListarSecciones(settings.ConfigPath);
            AnsiConsole.MarkupLine(
                $"[cyan]→[/] {secciones.Count} secciones: [bold]{Markup.Escape(string.Join(", ", secciones))}[/]");

            var fallidas = new List<string>();
            foreach(var s in secciones)
            {
                AnsiConsole.MarkupLine($"\n[cyan]═══ {Markup.Escape(s)} ═══[/]");

                int codigo;
                try
                {
                    codigo = await EjecutarSeccion(settings, s, CancellationToken.None);
                } catch(Exception ex)
                {
                    Log.Error($"Section '{s}' failed: {ex.Message}", evt: "run.all.section.error");
                    AnsiConsole.MarkupLine($"[red]✗[/] {Markup.Escape(s)}: {Markup.Escape(ex.Message)}");
                    codigo = 1;
                }

                if(codigo == 0)
                    continue;

                fallidas.Add(s);
                if(!settings.ContinueOnError)
                {
                    AnsiConsole.MarkupLine(
                        $"[red]✗[/] Se detuvo en '{Markup.Escape(s)}'. Use --continue-on-error para seguir con las demás.");
                    return 1;
                }
            }

            if(fallidas.Count > 0)
            {
                AnsiConsole.MarkupLine($"\n[red]✗[/] Fallaron: [bold]{Markup.Escape(string.Join(", ", fallidas))}[/]");
                return 1;
            }

            AnsiConsole.MarkupLine($"\n[green]✓[/] {secciones.Count} secciones sincronizadas");
            return 0;
        }

        /// <summary>
        /// Una seccion, de principio a fin: se carga el archivo, se traduce al modelo
        /// del motor, se corre y se cuenta que paso.
        ///
        /// El codigo de salida sale de <see cref="JobRun.Status"/> y no de un catch. El
        /// motor no lanza por nada que sea un resultado de la corrida - una corrida de
        /// treinta pasos cuyo septimo falla no puede perder los otros veintinueve
        /// resultados - asi que el fallo vuelve como estado y con un mensaje que dice
        /// que hacer.
        /// </summary>
        private static async Task<int> EjecutarSeccion(RunSettings settings, string seccion, CancellationToken ct)
        {
            var cfg = LoadConfig(settings.ConfigPath, seccion);
            ApplyOverrides(cfg, settings);
            ValidateConfig(cfg);

            var (job, perdidas) = await CoreAdapter.JobAsync(
                seccion, LeerSeccionCruda(settings.ConfigPath, seccion), cfg, settings, ct);

            if(settings.InitTracking)
                return await InicializarTracking(job, cfg, perdidas, ct);

            ShowConfigSummary(cfg);
            AvisarDeFullRefresh(job, settings);
            Probar(cfg, settings);

            var run = await Correr(job, cfg, settings, seccion, ct);

            return Informar(run, perdidas, seccion);
        }

        /// <summary>
        /// El JSON crudo de una seccion, que es lo que lee el importador del motor.
        ///
        /// El importador y no un mapeo escrito a mano aqui: es el mismo lector que
        /// importa un archivo para inspeccionarlo, ya sabe cosas que este tendria que
        /// aprender - cual origen gana cuando la seccion trae consulta y procedimiento a
        /// la vez - y un segundo mapeo seria una segunda cosa que mantener al dia.
        /// </summary>
        private static string LeerSeccionCruda(string path, string seccion)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));

            if(!doc.RootElement.TryGetProperty(seccion, out var elemento))
                throw new Exception($"No se encontró la sección '{seccion}' en {path}");

            return elemento.GetRawText();
        }

        /// <summary>
        /// La corrida, con el renglon de estado de Spectre siguiendola.
        ///
        /// Las filas copiadas se mueven mientras se copian, que es lo nuevo y no es
        /// decoracion: es la diferencia entre un trabajo lento y un trabajo colgado, y
        /// hasta ahora la pantalla decia "Leyendo datos del origen..." y no cambiaba
        /// mas hasta que terminaba.
        /// </summary>
        private static Task<JobRun> Correr(
            SyncJobDefinition job, SyncConfig cfg, RunSettings settings, string seccion, CancellationToken ct)
        {
            string titulo = settings.DryRun
                ? $"Ensayo de '{seccion}': se prepara todo y no se escribe nada..."
                : $"Ejecutando '{seccion}'...";

            return AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync(
                    titulo,
                    ctx => new JobRunner().RunAsync(
                        job,
                        CoreAdapter.Options(cfg, settings, triggeredBy: "cli", new ProgresoEnPantalla(ctx)),
                        ct));
        }

        /// <summary>
        /// Lleva el progreso del motor al renglon de estado.
        ///
        /// El copiador llama a esto desde su propia devolucion de llamada, cada diez mil
        /// filas y desde el hilo de SqlBulkCopy; por eso no hace nada que pueda tardar y
        /// por eso el motor traga lo que lance. Escribir el estado alcanza: el display en
        /// vivo de Spectre se refresca solo.
        /// </summary>
        private sealed class ProgresoEnPantalla : IProgress<RunProgress>
        {
            private readonly StatusContext _ctx;

            public ProgresoEnPantalla(StatusContext ctx) => _ctx = ctx;

            public void Report(RunProgress value)
            {
                _ctx.Status(Markup.Escape(value.Message));
                Log.Debug(value.Message, evt: "run.progress");
            }
        }

        /// <summary>
        /// <c>--full-refresh</c> le dice al paso que ignore su marca de agua por esta
        /// corrida. Se dice en voz alta cuando la seccion no lleva marca de agua: la
        /// bandera se sigue escribiendo y no hace nada, y eso es justo lo que no debe
        /// pasar en silencio.
        /// </summary>
        private static void AvisarDeFullRefresh(SyncJobDefinition job, RunSettings settings)
        {
            if(!settings.FullRefresh)
                return;

            bool incremental = job.Steps.Count > 0 && job.Steps[0].Incremental is not null;

            AnsiConsole.MarkupLine(incremental
                ? "[yellow]Modo Full Refresh activado[/] (se ignora la marca de agua por esta corrida)"
                : "[yellow]--full-refresh no aplica:[/] la sección no lleva marca de agua, ya lee todo cada vez.");
        }

        /// <summary>
        /// Probe TOP N del origen: mide permisos y tiempo antes de mover nada. Lo usan
        /// <c>run</c> y <c>validate</c>, que es como estaba escrito dos veces.
        /// </summary>
        private static void Probar(SyncConfig cfg, ConfigSettings settings)
        {
            int probeN = Math.Max(1, settings.ProbeTop ?? 1);
            var probeRes = ProbeSourceTopN(cfg, probeN, 30);

            AnsiConsole.MarkupLine(
                $"Probe origen TOP {probeN}: [bold]{probeRes.rows}[/] filas en [bold]{probeRes.elapsedMs} ms[/]");

            if(probeRes.elapsedMs <= 2000)
                return;

            AnsiConsole.MarkupLine("[yellow]Aviso:[/] El SELECT TOP es más lento de lo esperado (>2s). Considera:");
            AnsiConsole.MarkupLine("- Revisar índices en columnas de filtros/joins de la vista/consulta origen.");
            AnsiConsole.MarkupLine("- Probar con --top para pruebas y/o particionar por rangos de fecha/ID.");
        }

        /// <summary>
        /// <c>--init-tracking</c>: deja lista la tabla de marcas de agua del destino y
        /// dice que hay en ella para este trabajo.
        ///
        /// La crea leyendola, porque leer es lo que hace la corrida y el mismo camino no
        /// puede dejar una tabla con otra forma. Antes esto llamaba a
        /// IncrementalSyncEngine.EnsureTrackingTable, que creaba una tabla que el motor
        /// no usa; y cuando la seccion no era incremental la bandera se ignoraba y el
        /// comando seguia de largo hasta hacer una carga completa de produccion, que es
        /// lo ultimo que espera quien escribio --init-tracking.
        /// </summary>
        private static async Task<int> InicializarTracking(
            SyncJobDefinition job, SyncConfig cfg, IReadOnlyList<string> perdidas, CancellationToken ct)
        {
            var paso = job.Steps.Count > 0 ? job.Steps[0] : null;
            if(paso is null)
            {
                AnsiConsole.MarkupLine("[red]✗[/] La sección no describe ningún paso, así que no hay nada que inicializar.");
                return 1;
            }

            string tabla = paso.Incremental?.Watermark?.StateTable ?? SqlWatermarkStore.DefaultTable;
            var almacen = new SqlWatermarkStore(tabla);
            string destino = cfg.Destination!.ConnectionString!;

            Watermark? actual = null;
            await AnsiConsole.Status()
                .StartAsync(
                    $"Preparando {tabla} en el destino...",
                    async _ => actual = await almacen.ReadAsync(destino, job.Id, paso.Id, ct));

            AnsiConsole.MarkupLine($"[green]✓[/] Tabla de marcas de agua lista: [bold]{Markup.Escape(tabla)}[/]");

            AnsiConsole.MarkupLine(actual is null
                ? $"[cyan]Sin marca previa[/] para {Markup.Escape(job.Id)} / {Markup.Escape(paso.Id)}: la próxima corrida leerá todo."
                : $"[cyan]Marca actual[/] {Markup.Escape(job.Id)} / {Markup.Escape(paso.Id)}: " +
                  $"[bold]{Markup.Escape(actual.Value)}[/] (anterior: {Markup.Escape(actual.PreviousValue ?? "-")}, " +
                  $"movida {actual.UpdatedAt:yyyy-MM-dd HH:mm:ss}Z)");

            if(paso.Incremental?.Watermark is null)
            {
                AnsiConsole.MarkupLine(
                    "[yellow]Aviso:[/] la sección no define Incremental.TrackingColumn, así que nada escribirá en " +
                    "esta tabla: cada corrida seguirá leyendo todo el origen.");
            }

            ImprimirPerdidas(perdidas);
            Log.Info($"Tracking table ready: {tabla}", evt: "run.tracking.init");
            return 0;
        }

        /// <summary>
        /// El resumen del final: los numeros reales de cada paso, lo que el archivo
        /// decia y no cruzo, y el codigo de salida.
        ///
        /// Los numeros salen del trabajo y no de la intencion. Antes esta ruta guardaba
        /// RowsInserted = filas del origen con un <c>// TODO: Obtener metrics reales del
        /// MERGE</c> al lado; ahora los pone el publicador, que es quien sabe cuantas
        /// filas entraron, cuantas cambiaron y cuantas salieron.
        /// </summary>
        private static int Informar(JobRun run, IReadOnlyList<string> perdidas, string seccion)
        {
            // Un StepResult con el id reservado no habla de un paso sino de la corrida
            // - una concesion que se quedo tomada en otro lado, un trabajo que no
            // valido - y no es un renglon de la tabla llamado "(run)".
            var pasos = run.Steps.Where(x => x.StepId != JobRunner.RunLevelStepId).ToList();
            var deLaCorrida = run.Steps.Where(x => x.StepId == JobRunner.RunLevelStepId).ToList();

            if(pasos.Count > 0)
            {
                var tabla = new Table().Border(TableBorder.Rounded).Title("[bold]Resultado[/]");
                tabla.AddColumn("Paso");
                tabla.AddColumn("Estado");
                tabla.AddColumn(new TableColumn("Leídas").RightAligned());
                tabla.AddColumn(new TableColumn("Insertadas").RightAligned());
                tabla.AddColumn(new TableColumn("Actualizadas").RightAligned());
                tabla.AddColumn(new TableColumn("Borradas").RightAligned());
                tabla.AddColumn(new TableColumn("Duración").RightAligned());

                foreach(var paso in pasos)
                {
                    tabla.AddRow(
                        Markup.Escape(paso.StepName),
                        Pintar(paso.Status),
                        Numero(paso.RowsRead),
                        Numero(paso.RowsInserted),
                        Numero(paso.RowsUpdated),
                        Numero(paso.RowsDeleted),
                        paso.Duration is { } d ? d.TotalSeconds.ToString("N1", CultureInfo.InvariantCulture) + " s" : "-");
                }

                AnsiConsole.Write(tabla);
            }

            foreach(var paso in pasos.Where(x => x.WatermarkValue is not null))
            {
                AnsiConsole.MarkupLine(
                    $"[cyan]Marca de agua[/] {Markup.Escape(paso.StepName)}: " +
                    $"{Markup.Escape(paso.PreviousWatermarkValue ?? "(ninguna)")} → [bold]{Markup.Escape(paso.WatermarkValue!)}[/]");
            }

            // El mensaje es lo que se lee a las tres de la mañana para decidir si se
            // fuerza; se imprime entero y sin recortar.
            foreach(var paso in pasos.Where(x => !string.IsNullOrWhiteSpace(x.Message)))
                AnsiConsole.MarkupLine($"[grey]{Markup.Escape(paso.StepName)}:[/] {Markup.Escape(paso.Message!)}");

            foreach(var nota in deLaCorrida)
                AnsiConsole.MarkupLine($"[yellow]La corrida:[/] {Markup.Escape(nota.Message ?? nota.Status.ToString())}");

            ImprimirPerdidas(perdidas);

            string totales =
                $"{Numero(run.RowsRead)} leídas, {Numero(run.RowsInserted)} insertadas, " +
                $"{Numero(run.RowsUpdated)} actualizadas, {Numero(run.RowsDeleted)} borradas";

            switch(run.Status)
            {
                case RunStatus.Succeeded:
                    AnsiConsole.MarkupLine($"[green]=== Sync OK ===[/] {Markup.Escape(seccion)}: {totales}");
                    Log.Info($"Run OK: {totales}", evt: "run.ok");
                    return 0;

                // Un salto es una tabla que alguien pidio cargar y que a proposito no se
                // cargo: es un informe y no una llamada telefonica, asi que sale con 0 -
                // pero se dice cual y por que, que es la mitad que importa.
                case RunStatus.Skipped:
                    var omitidos = pasos.Where(x => x.Status == RunStatus.Skipped).Select(x => x.StepName).ToList();
                    AnsiConsole.MarkupLine(
                        $"[yellow]=== Sin publicar ===[/] {Markup.Escape(seccion)}: no se publicó " +
                        $"[bold]{Markup.Escape(string.Join(", ", omitidos))}[/]. El destino quedó como estaba, " +
                        "una carga más viejo. El motivo está arriba.");
                    Log.Warn($"Run skipped: {string.Join(", ", omitidos)}", evt: "run.skipped");
                    return 0;

                default:
                    AnsiConsole.MarkupLine($"[red]=== Sync falló ===[/] {Markup.Escape(seccion)}: {totales}");
                    Log.Error($"Run failed: {totales}", evt: "run.failed");
                    return 1;
            }
        }

        /// <summary>
        /// Lo que el archivo decia y no llego al motor.
        ///
        /// Se imprime siempre. Un operador al que no se le avisa es un operador que cree
        /// que funciono, y la lista es justamente la parte honesta de la traduccion: una
        /// estrategia de merge que este modelo no tiene, una lista de columnas que ya no
        /// hace falta, una bandera cuyo significado cambio.
        /// </summary>
        private static void ImprimirPerdidas(IReadOnlyList<string> perdidas)
        {
            if(perdidas.Count == 0)
                return;

            AnsiConsole.MarkupLine($"[yellow]Lo que el archivo decía y no cruzó ({perdidas.Count}):[/]");

            foreach(var perdida in perdidas)
            {
                AnsiConsole.MarkupLine($"  [yellow]•[/] {Markup.Escape(perdida)}");
                Log.Warn(perdida, evt: "run.adapt.loss");
            }
        }

        private static string Pintar(RunStatus estado) => estado switch
        {
            RunStatus.Succeeded => "[green]OK[/]",
            RunStatus.Skipped => "[yellow]Omitido[/]",
            RunStatus.Failed => "[red]Falló[/]",
            _ => Markup.Escape(estado.ToString())
        };

        /// <summary>
        /// Invariante y no la del equipo: el mismo numero tiene que leerse igual en el
        /// log de un servidor en español y en el de uno en inglés.
        /// </summary>
        private static string Numero(long valor) => valor.ToString("N0", CultureInfo.InvariantCulture);

        public sealed class ValidateCommand : AsyncCommand<ValidateSettings>
        {
            public override async Task<int> ExecuteAsync(CommandContext context, ValidateSettings settings)
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
                    ValidateConfig(cfg);
                    ShowConfigSummary(cfg);

                    AnsiConsole.Status()
                        .Start(
                            "Probando conexiones y esquema...",
                            ctx =>
                            {
                                TestConnectivity(cfg);
                                _ = ReadSourceSchema(cfg);

                                // Siempre el destino y nunca la stage. El motor crea la
                                // stage con la forma de esta tabla en cada corrida, asi
                                // que leer una stage hecha a mano no comprobaba lo que
                                // se va a usar y rechazaba secciones que corren.
                                _ = GetFinalSchema(cfg);
                            });

                    Probar(cfg, settings);

                    return await ValidarComoLoVeElMotor(cfg, settings);
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

        /// <summary>
        /// La seccion tal como la va a ver el motor: se traduce al modelo y se le pasa
        /// el validador del Core, que es el mismo que va a correr antes de abrir una
        /// sola conexion cuando llegue el run.
        ///
        /// Reemplaza a la comprobacion que hacia BuildDestToSourceIndex, que verificaba
        /// que cada ColumnMapping nombrara una columna existente en el SELECT. Esa
        /// comprobacion ya no describe lo que pasa: el motor descubre las columnas del
        /// destino y las empareja por nombre, y lo que hay que validar ahora es el
        /// trabajo - una seccion con consulta y procedimiento a la vez, un merge sin
        /// clave, dos columnas de sincronizacion - que es justo lo que este validador
        /// mira. Y de paso se ven las perdidas de la traduccion antes de correr nada,
        /// que es donde sirven.
        /// </summary>
        private static async Task<int> ValidarComoLoVeElMotor(SyncConfig cfg, ValidateSettings settings)
        {
            var (job, perdidas) = await CoreAdapter.JobAsync(
                settings.Section,
                LeerSeccionCruda(settings.ConfigPath, settings.Section),
                cfg,
                ComoRunSettings(settings),
                CancellationToken.None);

            ImprimirPerdidas(perdidas);

            var problemas = JobValidator.Validate(job);
            foreach(var problema in problemas)
            {
                string color = problema.Severity == ValidationSeverity.Error ? "red" : "yellow";
                AnsiConsole.MarkupLine($"[{color}]{problema.Severity}[/] {Markup.Escape(problema.Message)}");
            }

            int errores = problemas.Count(x => x.Severity == ValidationSeverity.Error);
            if(errores > 0)
            {
                AnsiConsole.MarkupLine(
                    $"[red]✗[/] La sección no correría: {errores} error(es). El run se detiene en el mismo punto.");
                Log.Error($"Validate failed: {errores} model error(s)", evt: "validate.model.error");
                return 1;
            }

            AnsiConsole.MarkupLine("[green]Validación OK[/]");
            Log.Info("Validate OK", evt: "validate.ok");
            return 0;
        }

        /// <summary>
        /// <c>validate</c> no tiene las banderas de <c>run</c>, pero el adaptador pide un
        /// RunSettings porque son esas banderas las que cambian el trabajo. Se traducen
        /// las que <c>validate</c> si acepta - <c>--sp</c>, <c>--sp-param</c>,
        /// <c>--top</c>, <c>--min-commit</c>, <c>--batch-size</c>, <c>--maxdop</c>,
        /// <c>--direct</c> - para que valide exactamente el trabajo que se va a correr
        /// con esa misma linea de comandos.
        /// </summary>
        private static RunSettings ComoRunSettings(ValidateSettings settings) => new()
        {
            ConfigPath = settings.ConfigPath,
            Section = settings.Section,
            MaxDop = settings.MaxDop,
            BatchSize = settings.BatchSize,
            Top = settings.Top,
            ProbeTop = settings.ProbeTop,
            MinCommit = settings.MinCommit,
            StoredProcedure = settings.StoredProcedure,
            SpParams = settings.SpParams,
            TrustServerCertificate = settings.TrustServerCertificate,
            NoEncrypt = settings.NoEncrypt,
            HostNameInCertificate = settings.HostNameInCertificate,
            Direct = settings.Direct
        };

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
            eg.AddRow("Append a Final desde un SP:", "[grey]SyncJob.exe run -c appsettings.json -s ClienteSync --sp dbo.SP_ObtenerClientes --append --min-commit 0[/]");
            eg.AddRow("No publicar si el origen viene corto:", "[grey]SyncJob.exe run -c appsettings.json -s ClienteSync --min-commit 1000 --skip-commit[/]");
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
                // Los corchetes van duplicados a proposito: Spectre.Console lee
                // "[IdCliente]" como una etiqueta de estilo y revienta con
                // "Could not find color or style 'IdCliente'". El comando
                // examples fallaba SIEMPRE por esta linea.
                "[grey]Ver appsettings: Incremental.MergeStrategy = Upsert, PrimaryKeyColumns = [[IdCliente]][/]");

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

            // El convertidor de enums por nombre no es un lujo: Incremental.Mode,
            // Incremental.MergeStrategy y DeleteDetection.Mode son enums, y sin el una
            // seccion escrita como la documenta INCREMENTAL_SYNC.md - "Mode":
            // "Timestamp" - no se podia cargar. Reventaba con una JsonException cruda
            // antes de llegar a validar nada, asi que el bloque incremental que el
            // archivo describe no habia forma de correrlo desde el JSON. Los numeros
            // siguen entrando igual, de modo que ningun archivo que hoy funcione deja
            // de hacerlo.
            var root = JsonSerializer.Deserialize<Dictionary<string, SyncConfig>>(
                json,
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
                });
            if(root == null || !root.TryGetValue(section, out var cfg) || cfg == null)
                throw new Exception($"No se encontró la sección '{section}' en {path}");

            // Descifrado transparente: si las claves vienen marcadas con enc:,
            // se abren aqui y el resto del programa nunca se entera. Un archivo
            // en texto plano pasa sin cambios, asi que adoptar el cifrado no
            // obliga a migrar nada de golpe.
            if(cfg.Source != null)
                cfg.Source.ConnectionString =
                    SecretProtector.DescifrarClaveDeConexion(cfg.Source.ConnectionString ?? string.Empty);
            if(cfg.Destination != null)
                cfg.Destination.ConnectionString =
                    SecretProtector.DescifrarClaveDeConexion(cfg.Destination.ConnectionString ?? string.Empty);

            return cfg;
        }

        /// <summary>
        /// Todas las secciones sincronizables del archivo, en el orden en que
        /// estan escritas. Una seccion cuenta si tiene Source y Destination:
        /// asi se ignoran bloques de configuracion que no son sincronizaciones
        /// (ConnectionStrings, AIProxySettings, Logging y demas).
        ///
        /// Existe para poder correr un archivo completo de una vez. Con seis
        /// secciones, encadenarlas a mano en un script es justo el tipo de cosa
        /// donde se olvida una y nadie se entera.
        /// </summary>
        static List<string> ListarSecciones(string path)
        {
            var json = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(json);
            var secciones = new List<string>();

            foreach(var prop in doc.RootElement.EnumerateObject())
            {
                if(prop.Value.ValueKind != JsonValueKind.Object) continue;

                bool tieneOrigen = prop.Value.TryGetProperty("Source", out var s)
                                   && s.ValueKind == JsonValueKind.Object;
                bool tieneDestino = prop.Value.TryGetProperty("Destination", out var d)
                                    && d.ValueKind == JsonValueKind.Object;

                if(tieneOrigen && tieneDestino) secciones.Add(prop.Name);
            }

            if(secciones.Count == 0)
                throw new Exception($"No se encontró ninguna sección con Source y Destination en {path}");

            return secciones;
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

        /// <summary>
        /// Deduce los ColumnMappings 1:1 leyendo el esquema del origen.
        ///
        /// Se usa CommandBehavior.SchemaOnly: SQL Server devuelve los metadatos
        /// del resultado SIN ejecutar la consulta, asi que no cuesta nada aunque
        /// el origen sean millones de filas.
        ///
        /// Se llama solo cuando la lista viene vacia. Declararla a mano sigue
        /// siendo valido y necesario cuando los nombres difieren entre origen y
        /// destino, o cuando se quiere mover un subconjunto de columnas.
        /// </summary>
        internal static void AutoMapearColumnas(SyncConfig cfg)
        {
            if(cfg.Source == null || string.IsNullOrWhiteSpace(cfg.Source.ConnectionString))
                throw new ArgumentException("No se pueden deducir las columnas: falta Source.ConnectionString.");

            using var conn = new SqlConnection(cfg.Source.ConnectionString);
            conn.Open();

            using var cmd = conn.CreateCommand();
            if(!string.IsNullOrWhiteSpace(cfg.Source.StoredProcedure))
            {
                cmd.CommandText = cfg.Source.StoredProcedure;
                cmd.CommandType = CommandType.StoredProcedure;
            }
            else
            {
                cmd.CommandText = cfg.Source.Query;
                cmd.CommandType = CommandType.Text;
            }

            var columnas = new List<string>();
            using(var rd = cmd.ExecuteReader(CommandBehavior.SchemaOnly))
            {
                for(int i = 0; i < rd.FieldCount; i++)
                {
                    string nombre = rd.GetName(i);
                    if(string.IsNullOrWhiteSpace(nombre))
                        throw new ArgumentException(
                            $"La columna {i + 1} del origen no tiene nombre. Póngale un alias o " +
                            "declare los ColumnMappings a mano.");
                    columnas.Add(nombre);
                }
            }

            if(columnas.Count == 0)
                throw new ArgumentException("El origen no devolvió columnas; no hay nada que mapear.");

            cfg.ColumnMappings = columnas
                .Select(c => new ColumnMap { Source = c, Dest = c })
                .ToList();

            Log.Info($"ColumnMappings deducidos del origen: {columnas.Count} columnas", evt: "config.automap");
            AnsiConsole.MarkupLine(
                $"[cyan]→[/] ColumnMappings deducidos del origen: [bold]{columnas.Count}[/] columnas");
        }

        /// <summary>
        /// Lo que el archivo tiene que decir para que la seccion pueda correr.
        ///
        /// Ya no recibe el modo directo: <c>--direct</c> no cambia nada de esto desde
        /// que el motor siempre pasa por una tabla de stage. Lo que el modo directo
        /// evitaba - tener que declarar la stage - ahora vale para todos.
        /// </summary>
        static void ValidateConfig(SyncConfig cfg)
        {
            if(cfg.Source == null)
                throw new ArgumentException("Missing Source config.");
            if(cfg.Destination == null)
                throw new ArgumentException("Missing Destination config.");
            if(cfg.Options == null)
                throw new ArgumentException("Missing Options config.");
            // ColumnMappings vacio ya no es un error: se deducen del origen.
            // Escribir a mano una lista de 30 columnas es justo donde se cuela
            // una errata que nadie ve hasta que los datos salen corridos.
            // Se sigue pudiendo declarar la lista cuando los nombres difieren
            // entre origen y destino, o cuando se quiere mover solo algunas.
            if(cfg.ColumnMappings == null || cfg.ColumnMappings.Count == 0)
                AutoMapearColumnas(cfg);

            if(string.IsNullOrWhiteSpace(cfg.Source.ConnectionString))
                throw new ArgumentException("Source.ConnectionString vacío.");
            if(string.IsNullOrWhiteSpace(cfg.Source.Query) && string.IsNullOrWhiteSpace(cfg.Source.StoredProcedure))
                throw new ArgumentException("Source.Query o Source.StoredProcedure vacío. Debes especificar uno.");

            if(string.IsNullOrWhiteSpace(cfg.Destination.ConnectionString))
                throw new ArgumentException("Destination.ConnectionString vacío.");
            if(string.IsNullOrWhiteSpace(cfg.Destination.FinalTable))
                throw new ArgumentException("Destination.FinalTable vacío.");
            // Destination.StageTable dejo de ser obligatoria: el motor crea la tabla de
            // stage con la forma del destino en cada corrida y la borra despues, asi que
            // una seccion que no la nombra corre igual. Exigirla era exigir el problema:
            // una stage mantenida a mano se separa del destino el dia que alguien agrega
            // una columna de un solo lado, y la publicacion entra corrida.
            //
            // Lo que si sigue siendo un error es que sea la MISMA que Final. El motor
            // borra y vuelve a crear la tabla de stage antes de copiar, de modo que
            // apuntarla al destino es borrar el destino.
            if(!string.IsNullOrWhiteSpace(cfg.Destination.StageTable) &&
                string.Equals(
                    cfg.Destination.StageTable,
                    cfg.Destination.FinalTable,
                    StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Destination.StageTable y Destination.FinalTable no pueden ser iguales.");

            if(cfg.Options.BatchSize <= 0)
                throw new ArgumentException("Options.BatchSize debe ser > 0.");
            if(cfg.Options.MaxDegreeOfParallelism <= 0)
                throw new ArgumentException("Options.MaxDegreeOfParallelism debe ser > 0.");
            if(cfg.Options.MinRowThresholdToCommit < 0)
                throw new ArgumentException("Options.MinRowThresholdToCommit no puede ser negativo.");
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
        internal static Dictionary<string, string> ParseNameValuePairs(IEnumerable<string> pairs)
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
    }
}
