using Microsoft.Data.SqlClient;
using Spectre.Console;
using Spectre.Console.Cli;
using SyncJob.Core.Import;
using SyncJob.Core.Model;
using SyncJob.Core.Run;
using SyncJob.Database;
using SyncJob.Engine;
using SyncJob.Services;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SyncJob.Commands
{
    // ============================================================================
    // RUN COMMAND - Ejecutar sincronización desde configuración SQLite
    // ============================================================================

    public class RunFromDbSettings : CommandSettings
    {
        [Description("ID de la configuración a ejecutar")]
        [CommandArgument(0, "<CONFIG_ID>")]
        public string ConfigId { get; set; } = string.Empty;

        [Description("Base de datos SQLite (default: syncjob.db)")]
        [CommandOption("--db <PATH>")]
        public string DbPath { get; set; } = "syncjob.db";

        [Description("Modo dry-run (no escribe en destino)")]
        [CommandOption("--dry-run")]
        public bool DryRun { get; set; }

        [Description("Modo directo (sin stage, directo a Final)")]
        [CommandOption("--direct")]
        public bool Direct { get; set; }

        [Description("Append mode (no trunca tabla final)")]
        [CommandOption("--append")]
        public bool Append { get; set; }

        [Description("Forzar full refresh (ignorar tracking incremental)")]
        [CommandOption("--full-refresh")]
        public bool FullRefresh { get; set; }

        // Mismas banderas que la ruta del JSON. Antes esta ruta no evaluaba el
        // umbral en absoluto, asi que tampoco tenia como reaccionar a el.
        [Description("Forzar commit aunque filas < MinRowThresholdToCommit (no recomendado)")]
        [CommandOption("--force-commit")]
        public bool ForceCommit { get; set; }

        [Description("Omitir el commit si filas < MinRowThresholdToCommit, en vez de abortar")]
        [CommandOption("--skip-commit")]
        public bool SkipCommit { get; set; }

        [Description("Limitar filas a leer del origen (testing)")]
        [CommandOption("--top <N>")]
        public int? Top { get; set; }

        [Description("Override de BatchSize")]
        [CommandOption("--batch-size <N>")]
        public int? BatchSize { get; set; }

        [Description("Override de MaxDOP")]
        [CommandOption("--maxdop <N>")]
        public int? MaxDop { get; set; }

        [Description("Nivel de log (Trace, Debug, Info, Warn, Error, Fatal)")]
        [CommandOption("--log-level <LEVEL>")]
        public string? LogLevel { get; set; }

        [Description("Ruta de archivo de log")]
        [CommandOption("--log-file <PATH>")]
        public string? LogFile { get; set; }

        [Description("Salida de logs en formato JSON")]
        [CommandOption("--json-log")]
        public bool JsonLog { get; set; }

        [Description("No imprimir logs a consola")]
        [CommandOption("--quiet")]
        public bool Quiet { get; set; }

        public override ValidationResult Validate()
        {
            if(string.IsNullOrWhiteSpace(ConfigId))
                return ValidationResult.Error("CONFIG_ID es requerido");
            return ValidationResult.Success();
        }
    }

    /// <summary>
    /// Runs one configuration out of the SQLite catalog through
    /// <see cref="JobRunner"/>.
    /// <para>
    /// This command used to carry its own copy of the pipeline - read the whole source
    /// into a <c>List&lt;object[]&gt;</c>, batch it into <c>DataTable</c>s, bulk copy it,
    /// then commit stage to final - which was one of three copies in this repository that
    /// had drifted apart. All of it is gone. What is left is the part that is genuinely
    /// this surface's: reading the catalog, resolving the credentials the model refuses to
    /// hold, and writing the execution history.
    /// </para>
    /// <para>
    /// The exit code comes from <see cref="JobRun.Status"/> and never from a
    /// <c>catch</c>: the runner reports a failed step as a result rather than throwing,
    /// because the results of the steps that did run are the most valuable thing a
    /// failed run produced.
    /// </para>
    /// </summary>
    public sealed class RunFromDbCommand : AsyncCommand<RunFromDbSettings>
    {
        public override Task<int> ExecuteAsync(CommandContext context, RunFromDbSettings settings) =>
            RunAsync(settings, CancellationToken.None);

        /// <summary>
        /// The command without Spectre's plumbing, so that a test can drive the whole of
        /// it - catalog, importer, overrides, credential resolution, run and history -
        /// rather than the half of it that is easy to reach.
        /// </summary>
        public static async Task<int> RunAsync(RunFromDbSettings settings, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(settings);

            try
            {
                InitLogging(settings);

                AnsiConsole.MarkupLine($"[cyan]═══════════════════════════════════════════════════[/]");
                AnsiConsole.MarkupLine($"[cyan bold]  Ejecutando sincronización: {Markup.Escape(settings.ConfigId)}[/]");
                AnsiConsole.MarkupLine($"[cyan]═══════════════════════════════════════════════════[/]");

                // Obtener configuración desde SQLite
                ConfigurationEntity? config = null;
                List<ColumnMappingEntity> mappings = new();
                ConnectionEntity? sourceConn = null;
                ConnectionEntity? destConn = null;

                AnsiConsole.Status().Start("Cargando configuración desde SQLite...", ctx =>
                {
                    DbManager.DbPath = settings.DbPath;
                    DbManager.Initialize();

                    config = ConfigRepository.GetById(settings.ConfigId);
                    if(config == null)
                        throw new Exception($"Configuración '{settings.ConfigId}' no encontrada");

                    // Un catálogo sin column mappings ya no es un error: el motor
                    // descubre las columnas del destino y empareja por nombre, que es lo
                    // que la lista de identidades estaba diciendo de todos modos.
                    mappings = ColumnMappingRepository.GetByConfigId(settings.ConfigId);

                    sourceConn = ConnectionRepository.GetById(config.SourceConnectionId);
                    if(sourceConn == null)
                        throw new Exception($"Conexión origen '{config.SourceConnectionId}' no encontrada");

                    destConn = ConnectionRepository.GetById(config.DestConnectionId);
                    if(destConn == null)
                        throw new Exception($"Conexión destino '{config.DestConnectionId}' no encontrada");
                });

                if(config == null || sourceConn == null || destConn == null)
                    return 1;

                // Mostrar resumen
                ShowConfigSummary(config, mappings, sourceConn, destConn, settings);

                var (job, losses) = await BuildJobAsync(
                    config, mappings, sourceConn, destConn, settings, cancellationToken).ConfigureAwait(false);

                ShowLosses(losses);

                AnsiConsole.Status().Start("Probando conexiones...", ctx =>
                {
                    TestConnectivity(sourceConn, destConn);
                });
                AnsiConsole.MarkupLine("[green]✓[/] Conexiones OK");

                // Crear registro de ejecución
                string executionId = Guid.NewGuid().ToString("N");
                var stopwatch = Stopwatch.StartNew();

                var execution = new ExecutionHistoryEntity
                {
                    ExecutionId = executionId,
                    ConfigId = settings.ConfigId,
                    StartTime = DateTime.UtcNow,
                    Status = "Running",
                    ExecutionMode = settings.DryRun ? "DryRun" : (settings.FullRefresh ? "Full" : "Incremental"),
                    HostMachine = Environment.MachineName,
                    TriggeredBy = Environment.UserName
                };
                ExecutionHistoryRepository.Create(execution);

                try
                {
                    JobRun run = null!;

                    await AnsiConsole.Status()
                        .StartAsync($"Ejecutando '{Markup.Escape(config.ConfigId)}'...", async ctx =>
                        {
                            var options = RunOptions(settings, sourceConn, destConn, new StatusProgress(ctx));
                            run = await new JobRunner().RunAsync(job, options, cancellationToken).ConfigureAwait(false);
                        })
                        .ConfigureAwait(false);

                    stopwatch.Stop();

                    Record(execution, run, stopwatch.ElapsedMilliseconds);
                    ExecutionHistoryRepository.Update(execution);

                    // Sincronizar automáticamente al central si está habilitado
                    TrySyncToCentral(execution);

                    ShowRun(run);
                    ShowExecutionSummary(execution);

                    return Announce(run, executionId);
                }
                catch(Exception ex)
                {
                    stopwatch.Stop();

                    // Actualizar ejecución con error
                    execution.EndTime = DateTime.UtcNow;
                    execution.DurationMs = stopwatch.ElapsedMilliseconds;
                    execution.Status = "Failed";
                    execution.ErrorMessage = ex.Message;
                    execution.ErrorStackTrace = ex.StackTrace;
                    ExecutionHistoryRepository.Update(execution);

                    // Sincronizar automáticamente al central (incluso si falló)
                    TrySyncToCentral(execution);

                    AnsiConsole.WriteException(ex, ExceptionFormats.ShortenPaths | ExceptionFormats.ShortenTypes);
                    Log.Error($"Execution failed: {executionId}", ex, evt: "run.error");
                    return 1;
                }
            }
            catch(Exception ex)
            {
                AnsiConsole.WriteException(ex, ExceptionFormats.ShortenPaths | ExceptionFormats.ShortenTypes);
                Log.Error("Run command failed", ex, evt: "run.fatal");
                return 1;
            }
            finally
            {
                Log.Shutdown();
            }
        }

        private static void InitLogging(RunFromDbSettings settings)
        {
            var minLevel = ParseLogLevel(settings.LogLevel) ?? Log.Level.Info;
            var opts = new Log.Options
            {
                EnableConsole = !settings.Quiet,
                JsonFormat = settings.JsonLog,
                FilePath = settings.LogFile,
                DirectoryPath = "logs",
                MinLevel = minLevel,
                AppName = "SyncJob"
            };
            Log.Init(opts);
        }

        private static Log.Level? ParseLogLevel(string? level)
        {
            if(string.IsNullOrWhiteSpace(level)) return null;
            return level.ToLowerInvariant() switch
            {
                "trace" => Log.Level.Trace,
                "debug" => Log.Level.Debug,
                "info" => Log.Level.Info,
                "warn" or "warning" => Log.Level.Warn,
                "error" => Log.Level.Error,
                "fatal" or "critical" => Log.Level.Fatal,
                _ => null
            };
        }

        private static void ShowConfigSummary(
            ConfigurationEntity config,
            List<ColumnMappingEntity> mappings,
            ConnectionEntity sourceConn,
            ConnectionEntity destConn,
            RunFromDbSettings settings)
        {
            var table = new Table().Border(TableBorder.Rounded).Title("[bold]Configuración[/]");
            table.AddColumn("Propiedad");
            table.AddColumn("Valor");

            table.AddRow("Config ID", config.ConfigId);
            table.AddRow("Nombre", config.DisplayName);
            if(!string.IsNullOrWhiteSpace(config.Description))
                table.AddRow("Descripción", config.Description);

            table.AddRow("[cyan]Origen[/]", $"{sourceConn.ServerName} / {sourceConn.DatabaseName}");
            table.AddRow("[cyan]Destino[/]", $"{destConn.ServerName} / {destConn.DatabaseName}");

            if(!string.IsNullOrWhiteSpace(config.DestStageTable))
                table.AddRow("Stage Table", config.DestStageTable);
            table.AddRow("Final Table", config.DestFinalTable);

            table.AddRow("Column Mappings", mappings.Count.ToString());
            table.AddRow("Batch Size", (settings.BatchSize ?? config.BatchSize).ToString());
            table.AddRow("Max DOP", (settings.MaxDop ?? config.MaxDOP).ToString());
            table.AddRow("Tracking Mode", config.TrackingMode);
            table.AddRow("Merge Strategy", config.MergeStrategy);

            // El piso se muestra porque siempre se aplica. Un seguro que no se ve es un
            // seguro en el que se confía sin saber en qué está puesto.
            table.AddRow("Piso de filas", MinimumRows(config).ToString());

            if(settings.DryRun)
                table.AddRow("[yellow]Modo[/]", "[yellow]DRY RUN[/]");
            if(settings.Direct)
                table.AddRow("[yellow]Modo[/]", "[yellow]DIRECTO (sin stage)[/]");
            if(settings.Append)
                table.AddRow("[yellow]Modo[/]", "[yellow]APPEND (no trunca)[/]");
            if(settings.FullRefresh)
                table.AddRow("[yellow]Modo[/]", "[yellow]FULL REFRESH[/]");

            AnsiConsole.Write(table);
        }

        // ========================================================================
        // DEL CATÁLOGO AL MODELO
        // ========================================================================

        /// <summary>
        /// The job the engine will run, plus everything the catalog said that did not
        /// carry across.
        /// <para>
        /// <see cref="SqliteJobImporter"/> does the reading, not this command. The Core
        /// may not open a SQLite file - there is a test that fails if it ever references
        /// the provider - so the shape of that API is rows in, jobs out, and this is where
        /// the rows are handed over. Writing the mapping here instead would be a second
        /// reader of the same three tables, disagreeing with the one that <c>config
        /// import</c> already uses.
        /// </para>
        /// </summary>
        public static async Task<(SyncJobDefinition Job, IReadOnlyList<string> Losses)> BuildJobAsync(
            ConfigurationEntity config,
            List<ColumnMappingEntity> mappings,
            ConnectionEntity sourceConn,
            ConnectionEntity destConn,
            RunFromDbSettings settings,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(config);
            ArgumentNullException.ThrowIfNull(sourceConn);
            ArgumentNullException.ThrowIfNull(destConn);
            ArgumentNullException.ThrowIfNull(settings);

            var rows = new SqliteJobRows
            {
                Configuration = Row(config),
                SourceConnection = Row(sourceConn),
                DestinationConnection = Row(destConn),
                ColumnMappings = (mappings ?? new List<ColumnMappingEntity>()).Select(Row).ToList()
            };

            var imported = await new SqliteJobImporter([rows], DbManager.DbPath)
                .ImportAsync(cancellationToken)
                .ConfigureAwait(false);

            if(imported.Jobs.Count == 0)
            {
                throw new InvalidOperationException(
                    $"La configuración '{config.ConfigId}' no describe un job que el motor pueda ejecutar" +
                    (imported.Losses.Count == 0 ? "." : ": " + string.Join(" ", imported.Losses)));
            }

            var job = imported.Jobs[0];
            var losses = new List<string>(imported.Losses);

            ApplyCommandLineOverrides(job, config, settings, losses);

            return (job, losses);
        }

        private static SqliteConfigurationRow Row(ConfigurationEntity config) => new()
        {
            ConfigId = config.ConfigId,
            DisplayName = config.DisplayName,
            Description = config.Description,
            SourceConnectionId = config.SourceConnectionId,
            SourceQuery = config.SourceQuery,
            SourceStoredProc = config.SourceStoredProc,
            SourceParameters = config.SourceParameters,
            DestConnectionId = config.DestConnectionId,
            DestStageTable = config.DestStageTable,
            DestFinalTable = config.DestFinalTable,
            BatchSize = config.BatchSize,
            MaxDOP = config.MaxDOP,
            BulkCopyTimeout = config.BulkCopyTimeout,
            KeepIdentity = config.KeepIdentity,
            MinRowThreshold = config.MinRowThreshold,
            IsActive = config.IsActive,
            TrackingMode = config.TrackingMode,
            TrackingColumn = config.TrackingColumn,
            MergeStrategy = config.MergeStrategy,
            CreatedBy = config.CreatedBy,
            Tags = config.Tags
        };

        /// <summary>
        /// A connection row for the importer, carrying the <b>fact</b> of a stored secret
        /// and never the secret.
        /// <para>
        /// <see cref="SqliteConnectionRow.ConnectionString"/> is deliberately left unset
        /// even though this command can produce one: handing it over would put a password
        /// into a model that is logged, exported and diffed. The importer composes a
        /// credential-free string from the columns instead and points the endpoint at a
        /// <see cref="Endpoint.SecretRef"/>, which
        /// <see cref="JobRunOptions.ResolveConnectionString"/> answers at run time.
        /// </para>
        /// </summary>
        private static SqliteConnectionRow Row(ConnectionEntity conn) => new()
        {
            ConnectionId = conn.ConnectionId,
            DisplayName = conn.DisplayName,
            ServerType = conn.ServerType,
            ServerName = conn.ServerName,
            DatabaseName = conn.DatabaseName,
            Username = conn.Username,
            HasStoredPassword = conn.PasswordEncrypted is { Length: > 0 },
            HasStoredConnectionString = conn.ConnectionStringEncrypted is { Length: > 0 },
            TrustServerCertificate = conn.TrustServerCertificate,
            Encrypt = conn.Encrypt,
            IsActive = conn.IsActive
        };

        private static SqliteColumnMappingRow Row(ColumnMappingEntity mapping) => new()
        {
            MappingId = mapping.MappingId,
            SourceColumn = mapping.SourceColumn,
            DestColumn = mapping.DestColumn,
            IsPrimaryKey = mapping.IsPrimaryKey,
            TransformType = mapping.TransformType,
            TransformExpression = mapping.TransformExpression,
            Ordinal = mapping.Ordinal
        };

        /// <summary>
        /// The command line, applied over what the catalog said.
        /// <para>
        /// <b>This is a second copy of a mapping that already exists.</b>
        /// <c>CoreAdapter.ApplyOverrides</c> does the same job for the two surfaces whose
        /// jobs come out of a JSON section; it is private and it takes a
        /// <c>RunSettings</c>, which this command has never had - its flags live on
        /// <see cref="RunFromDbSettings"/>. Until that method is lifted into one that
        /// takes a <see cref="SyncJobDefinition"/> and a plain set of overrides, this is
        /// the drift WP 1.5e exists to end, in miniature. It is deliberately the smallest
        /// copy that works: none of the file-supplied fallbacks are repeated, because the
        /// importer has already read them off the same rows.
        /// </para>
        /// </summary>
        private static void ApplyCommandLineOverrides(
            SyncJobDefinition job, ConfigurationEntity config, RunFromDbSettings settings, List<string> losses)
        {
            var step = job.Steps.Count > 0 ? job.Steps[0] : null;
            if(step is null)
                return;

            if(settings.Append)
                step.Publication.Mode = PublicationMode.Append;

            ApplyGuard(step, config, settings);

            if(settings.BatchSize is > 0)
                step.BatchSize = settings.BatchSize.Value;

            if(settings.MaxDop is > 0)
                step.Publication.MaxDegreeOfParallelism = Math.Max(1, settings.MaxDop.Value);

            if(settings.FullRefresh)
            {
                if(step.Incremental is not null)
                {
                    step.Incremental.ForceFullRead = true;
                }
                else
                {
                    // Said rather than swallowed. The importer only builds an
                    // IncrementalPlan for a Timestamp or RowVersion tracking mode, so on
                    // a Snapshot or None configuration the flag has nothing to force -
                    // the step reads everything already.
                    losses.Add(
                        $"--full-refresh has nothing to force on '{config.ConfigId}': its tracking mode is " +
                        $"'{config.TrackingMode}', which keeps no watermark, so every run already reads everything.");
                }
            }

            if(settings.Top is > 0)
                ApplyTop(step, settings.Top.Value, losses);

            // --direct cargaba la tabla Final sin pasar por stage. El motor siempre
            // stagea, y esta es la única bandera cuyo significado cambió de verdad, así
            // que se dice en voz alta en vez de ignorarse.
            if(settings.Direct)
            {
                losses.Add(
                    "--direct cargaba el destino sin tabla de stage. El motor siempre stagea, y la razón es " +
                    "justamente la falla que --direct no podía evitar: sin nada en stage no hay nada que el " +
                    "guard pueda comparar, así que un origen que vuelve vacío se descubre después de haber " +
                    "vaciado el destino. Las filas terminan en la misma tabla; sólo cambió el orden.");
            }
        }

        /// <summary>
        /// The row floor, and what to do when a load falls under it.
        /// <para>
        /// The deployed command had two separate protections and the engine has one, so
        /// they are folded together here. It refused to write an empty source at all -
        /// <c>if (data.Rows.Count == 0) return;</c> - <b>before</b> it ever looked at
        /// <c>MinRowThresholdToCommit</c>, and it applied the threshold afterwards. That
        /// is why the floor is never below one row: a source that returns nothing must not
        /// reach a destination whatever the catalog says, and that was true of this
        /// command before the engine.
        /// </para>
        /// <para>
        /// <c>--force-commit</c> and <c>--skip-commit</c> keep exactly the meanings
        /// <c>PuedeCommitear</c> gave them. Where the catalog set no floor of its own, an
        /// empty read is a <b>skip</b> and the run still exits zero, which is the exit code
        /// the deployed command gave it. Where the catalog did set one, breaching it is a
        /// <b>failure</b>, which is what <c>PuedeCommitear</c>'s throw was.
        /// </para>
        /// </summary>
        private static void ApplyGuard(SyncStep step, ConfigurationEntity config, RunFromDbSettings settings)
        {
            step.Publication.Guard.MinimumRows = MinimumRows(config);

            step.Publication.Guard.OnFailure =
                settings.ForceCommit ? GuardFailureAction.Force
                : settings.SkipCommit ? GuardFailureAction.Skip
                : config.MinRowThreshold > 0 ? GuardFailureAction.Abort
                : GuardFailureAction.Skip;
        }

        private static int MinimumRows(ConfigurationEntity config) => Math.Max(1, config.MinRowThreshold);

        /// <summary>
        /// <c>--top</c>, which exists for trying a configuration out against a real source.
        /// </summary>
        private static void ApplyTop(SyncStep step, int top, List<string> losses)
        {
            if(!string.IsNullOrWhiteSpace(step.Source.StoredProcedure))
            {
                // Antes esta bandera se aplicaba sólo si había Query, y con un stored
                // procedure no hacía nada y no lo decía.
                losses.Add(
                    $"--top {top} no aplica a un stored procedure: sólo el procedimiento puede limitar lo que " +
                    "devuelve. Dele un parámetro @Top y páselo en SourceParameters.");

                return;
            }

            if(!string.IsNullOrWhiteSpace(step.Source.Sql))
            {
                // La tabla derivada es como esta ruta lo hizo siempre, y aquí es segura
                // porque la consulta va al origen tal cual está escrita de todos modos.
                step.Source.Sql = $"SELECT TOP {top} * FROM ( {step.Source.Sql} ) AS _src_";
                return;
            }

            if(!string.IsNullOrWhiteSpace(step.Source.Table))
                step.Source.Sql = $"SELECT TOP {top} * FROM {step.Source.Table}";
        }

        // ========================================================================
        // CREDENCIALES
        // ========================================================================

        /// <summary>
        /// How the run behaves, and where its credentials come from.
        /// <para>
        /// The lease is off, as it is for the CLI: this is a one-shot command and the
        /// process itself is the mutual exclusion. The Windows service is the surface
        /// where two hosts can genuinely overlap, and it turns the lease on.
        /// </para>
        /// </summary>
        private static JobRunOptions RunOptions(
            RunFromDbSettings settings,
            ConnectionEntity sourceConn,
            ConnectionEntity destConn,
            IProgress<RunProgress>? progress) => new()
            {
                TriggeredBy = "cli:run-db",
                DryRun = settings.DryRun,
                Progress = progress,
                UseLease = false,

                // El motor nunca lee el almacén de secretos: pide, y esta es la única
                // respuesta. Lo que devuelve tiene contraseña adentro y no entra en un
                // log, en un mensaje de excepción ni en el historial de ejecuciones.
                ResolveConnectionString = (endpoint, _) => Task.FromResult(
                    BuildConnectionString(endpoint.Id == CoreAdapter.DestinationEndpointId ? destConn : sourceConn))
            };

        /// <summary>
        /// The one place this command turns a stored connection into a connection string
        /// with a credential in it.
        /// <para>
        /// It is reached from <see cref="JobRunOptions.ResolveConnectionString"/> and from
        /// the connectivity probe, and from nowhere else, so what it returns is never held
        /// by the model, never printed and never written to the execution history.
        /// </para>
        /// </summary>
        private static string BuildConnectionString(ConnectionEntity conn)
        {
            var stored = Decrypt(conn.ConnectionStringEncrypted, conn.ConnectionId, "cadena de conexión");
            if(stored is { Length: > 0 } && Parses(stored))
                return stored;

            var builder = new SqlConnectionStringBuilder
            {
                DataSource = conn.ServerName,
                InitialCatalog = conn.DatabaseName,
                TrustServerCertificate = conn.TrustServerCertificate,
                Encrypt = conn.Encrypt
            };

            if(string.IsNullOrWhiteSpace(conn.Username))
            {
                builder.IntegratedSecurity = true;
                return builder.ToString();
            }

            builder.UserID = conn.Username;
            builder.Password = Decrypt(conn.PasswordEncrypted, conn.ConnectionId, "contraseña")
                ?? throw new InvalidOperationException(
                    $"La conexión '{conn.ConnectionId}' usa autenticación SQL y su contraseña guardada no se " +
                    "pudo leer. Vuelva a registrarla con 'syncjob connection add'.");

            return builder.ToString();
        }

        /// <summary>
        /// Reads one of the <c>Connections</c> table's blobs, in the two formats this
        /// repository actually contains.
        /// <para>
        /// DPAPI first, because that is what this command has always assumed. Then
        /// <see cref="ConnectionRepository.DecryptString"/>, <b>which is what
        /// <c>syncjob connection add</c> actually writes</b>: it stores with
        /// <c>ConnectionRepository.EncryptString</c> and this path only ever read with
        /// <c>ProtectedData.Unprotect</c>, so until now a connection with a stored
        /// password could not be run from here at all - the DPAPI call throws on bytes it
        /// did not write, and the run died on "No se pudo desencriptar la contraseña".
        /// The fallback is what makes those configurations runnable.
        /// </para>
        /// <para>
        /// It is not a fix for the storage itself. <c>EncryptString</c> is an XOR against
        /// a literal key compiled into the binary and is not encryption; that belongs to
        /// whoever owns <see cref="ConnectionRepository"/>.
        /// </para>
        /// </summary>
        /// <returns>The decrypted value, or null when neither reading produced one.</returns>
        private static string? Decrypt(byte[]? blob, string connectionId, string what)
        {
            if(blob is not { Length: > 0 })
                return null;

            try
            {
                return Encoding.UTF8.GetString(
                    ProtectedData.Unprotect(blob, null, DataProtectionScope.CurrentUser));
            }
            catch(CryptographicException)
            {
                // No es un blob DPAPI, o lo escribió otra cuenta de Windows. Se prueba el
                // otro formato antes de rendirse.
            }
            catch(PlatformNotSupportedException)
            {
                // DPAPI sólo existe en Windows.
            }

            try
            {
                // Una copia: DecryptString hace el XOR sobre el arreglo que recibe, así
                // que pasarle el de la entidad dejaría basura para la segunda lectura de
                // la misma conexión.
                return ConnectionRepository.DecryptString((byte[])blob.Clone());
            }
            catch(Exception ex)
            {
                // Nunca el valor, y tampoco el texto de la excepción: un mensaje de
                // criptografía puede llevar fragmentos de lo que intentó descifrar.
                Log.Warn(
                    $"No se pudo leer la {what} guardada de la conexión '{connectionId}' ({ex.GetType().Name})",
                    evt: "run.secret.unreadable");

                return null;
            }
        }

        /// <summary>
        /// Whether a decrypted blob is a connection string at all.
        /// <para>
        /// A blob written in one format and read in the other comes back as rubbish rather
        /// than as an error, and rubbish handed to <c>SqlConnection</c> fails with a
        /// message about the rubbish. Falling back to the columns the row keeps in clear -
        /// server, database, user - fails with a message about the server.
        /// </para>
        /// </summary>
        private static bool Parses(string connectionString)
        {
            try
            {
                _ = new SqlConnectionStringBuilder(connectionString);
                return true;
            }
            catch(ArgumentException)
            {
                return false;
            }
            catch(FormatException)
            {
                return false;
            }
        }

        /// <summary>
        /// The pre-flight the command has always printed. The engine would report a bad
        /// connection perfectly well on its own, but it would report it after creating a
        /// staging table, and "Conexiones OK" before a long copy is worth the two round
        /// trips.
        /// </summary>
        private static void TestConnectivity(ConnectionEntity sourceConn, ConnectionEntity destConn)
        {
            using(var c1 = new SqlConnection(BuildConnectionString(sourceConn)))
            {
                c1.Open();
                Log.Info($"Source connection OK", evt: "source.connect.ok");
            }
            using(var c2 = new SqlConnection(BuildConnectionString(destConn)))
            {
                c2.Open();
                Log.Info($"Dest connection OK", evt: "dest.connect.ok");
            }
        }

        // ========================================================================
        // LO QUE SE IMPRIME Y LO QUE SE GUARDA
        // ========================================================================

        /// <summary>
        /// What the catalog said that did not carry across. An operator who is not told is
        /// an operator who thinks it worked.
        /// </summary>
        private static void ShowLosses(IReadOnlyList<string> losses)
        {
            if(losses.Count == 0)
                return;

            AnsiConsole.Write(new Rule($"[yellow]{losses.Count} cosa(s) que no viajaron[/]")
            {
                Justification = Justify.Left
            });

            foreach(var loss in losses)
            {
                AnsiConsole.MarkupLine($"[yellow]•[/] {Markup.Escape(loss)}");
                Log.Warn(loss, evt: "run.import.loss");
            }
        }

        /// <summary>
        /// Every step's real numbers, and every message about why a table was or was not
        /// written.
        /// <para>
        /// A result whose id is <see cref="JobRunner.RunLevelStepId"/> is about the run and
        /// not about a step - a lease held elsewhere, a job that did not validate - so it
        /// is not printed as a table row called "(run)".
        /// </para>
        /// </summary>
        private static void ShowRun(JobRun run)
        {
            var steps = run.Steps.Where(x => x.StepId != JobRunner.RunLevelStepId).ToList();

            if(steps.Count > 0)
            {
                var table = new Table().Border(TableBorder.Rounded).Title("[bold]Pasos[/]");
                table.AddColumn("Paso");
                table.AddColumn("Estado");
                table.AddColumn(new TableColumn("Leídas").RightAligned());
                table.AddColumn(new TableColumn("Insertadas").RightAligned());
                table.AddColumn(new TableColumn("Actualizadas").RightAligned());
                table.AddColumn(new TableColumn("Eliminadas").RightAligned());

                foreach(var step in steps)
                {
                    table.AddRow(
                        Markup.Escape(step.StepName),
                        Paint(step.Status),
                        $"{step.RowsRead:N0}",
                        $"{step.RowsInserted:N0}",
                        $"{step.RowsUpdated:N0}",
                        $"{step.RowsDeleted:N0}");
                }

                AnsiConsole.Write(table);
            }

            foreach(var step in run.Steps.Where(x => x.WatermarkValue is { Length: > 0 }))
            {
                AnsiConsole.MarkupLine(
                    $"[cyan]→[/] {Markup.Escape(step.StepName)}: marca de agua " +
                    $"{Markup.Escape(step.PreviousWatermarkValue ?? "—")} → {Markup.Escape(step.WatermarkValue!)}");
            }

            foreach(var step in run.Steps.Where(x => !string.IsNullOrWhiteSpace(x.Message)))
                AnsiConsole.MarkupLine($"{Bullet(step.Status)} {Markup.Escape(step.Message!)}");
        }

        private static string Paint(RunStatus status) => status switch
        {
            RunStatus.Succeeded => "[green]OK[/]",
            RunStatus.Skipped => "[yellow]Omitido[/]",
            RunStatus.Failed => "[red]Falló[/]",
            _ => status.ToString()
        };

        private static string Bullet(RunStatus status) => status switch
        {
            RunStatus.Succeeded => "[green]✓[/]",
            RunStatus.Skipped => "[yellow]⚠[/]",
            RunStatus.Failed => "[red]✗[/]",
            _ => "[grey]•[/]"
        };

        /// <summary>
        /// The run's outcome as this process's exit code.
        /// <para>
        /// A skip is zero and is still said out loud: it means a table someone asked to be
        /// loaded deliberately was not, which is a report rather than a phone call.
        /// </para>
        /// </summary>
        private static int Announce(JobRun run, string executionId)
        {
            switch(run.Status)
            {
                case RunStatus.Succeeded:
                    AnsiConsole.MarkupLine("[green]✓ Sincronización completada exitosamente[/]");
                    Log.Info($"Execution completed: {executionId}", evt: "run.success");
                    return 0;

                case RunStatus.Skipped:
                    AnsiConsole.MarkupLine("[yellow]⚠ Sincronización omitida: no se escribió en el destino[/]");
                    Log.Warn($"Execution skipped: {executionId}", evt: "run.skipped");
                    return 0;

                default:
                    AnsiConsole.MarkupLine("[red]✗ Sincronización fallida[/]");
                    Log.Error($"Execution failed: {executionId}", evt: "run.error");
                    return 1;
            }
        }

        /// <summary>
        /// The execution history row, filled from the run.
        /// <para>
        /// The row counts are the publisher's, not the source's: a replace reports what it
        /// put in and what it took out, an append its insert count, and a merge the rows
        /// the MERGE itself said it changed. The old row recorded the source row count
        /// under <c>RowsInserted</c> and zero for everything else.
        /// </para>
        /// </summary>
        private static void Record(ExecutionHistoryEntity execution, JobRun run, long elapsedMilliseconds)
        {
            execution.EndTime = DateTime.UtcNow;
            execution.DurationMs = elapsedMilliseconds;
            execution.Status = run.Status switch
            {
                RunStatus.Succeeded => "Success",
                RunStatus.Skipped => "Skipped",
                _ => "Failed"
            };

            execution.RowsRead = run.RowsRead;
            execution.RowsInserted = run.RowsInserted;
            execution.RowsUpdated = run.RowsUpdated;
            execution.RowsDeleted = run.RowsDeleted;

            // Por qué una tabla no se escribió pertenece al historial y no sólo a la
            // pantalla: quien lee el historial es justamente el que no estaba mirando.
            var messages = run.Steps
                .Where(x => !string.IsNullOrWhiteSpace(x.Message))
                .Select(x => $"[{x.StepName}] {x.Message}")
                .ToList();

            execution.ErrorMessage = messages.Count == 0 ? null : string.Join(" ", messages);
        }

        private static void ShowExecutionSummary(ExecutionHistoryEntity execution)
        {
            var panel = new Panel(
                new Table()
                    .Border(TableBorder.None)
                    .AddColumn("Métrica")
                    .AddColumn("Valor")
                    .AddRow("Duración", $"{execution.DurationMs:N0} ms")
                    .AddRow("Filas leídas", $"{execution.RowsRead:N0}")
                    .AddRow("Filas insertadas", $"{execution.RowsInserted:N0}")
                    .AddRow("Filas actualizadas", $"{execution.RowsUpdated:N0}")
                    .AddRow("Filas eliminadas", $"{execution.RowsDeleted:N0}")
            )
            .Header("[bold green]Resumen de Ejecución[/]")
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Green);

            AnsiConsole.Write(panel);
        }

        /// <summary>
        /// The engine's progress on the spinner this command already showed.
        /// <para>
        /// Rows copied move while a long copy runs, which is the difference between a slow
        /// job and a stuck one, and it is new here. The text is escaped because a step's
        /// name and a table's name arrive from the catalog and Spectre reads square
        /// brackets as markup.
        /// </para>
        /// </summary>
        private sealed class StatusProgress(StatusContext context) : IProgress<RunProgress>
        {
            public void Report(RunProgress value)
            {
                context.Status(Markup.Escape(value.Message));
                context.Refresh();
            }
        }

        /// <summary>
        /// Intenta sincronizar la ejecución al servidor central (sin fallar si hay error)
        /// </summary>
        private static void TrySyncToCentral(ExecutionHistoryEntity execution)
        {
            try
            {
                var settings = CentralSyncRepository.GetSettings();

                // Verificar si el sync central está habilitado y configurado
                if(!settings.Enabled || !CentralSyncRepository.IsConfigured())
                {
                    Log.Debug("Central sync is disabled or not configured", evt: "central.sync.skipped");
                    return;
                }

                // Verificar el modo de sincronización
                if(settings.SyncMode != "AfterEveryExecution")
                {
                    Log.Debug($"Central sync mode is '{settings.SyncMode}', skipping auto-sync", evt: "central.sync.skipped");
                    return;
                }

                // Realizar la sincronización
                Log.Info("Syncing execution to central server...", evt: "central.sync.start");

                var service = new CentralSyncService(settings);
                var syncTask = service.SyncExecutionAsync(execution);
                syncTask.Wait(); // Sincronización sincrónica para simplicidad
                var result = syncTask.Result;

                if(result.Success)
                {
                    Log.Info($"Execution synced to central successfully (Duration: {result.DurationMs}ms)", evt: "central.sync.success");
                }
                else
                {
                    Log.Warn($"Failed to sync to central: {result.ErrorMessage}", evt: "central.sync.failed");
                }
            }
            catch(Exception ex)
            {
                // No fallar la ejecución principal si el sync central falla
                Log.Warn($"Error during central sync (non-critical): {ex.Message}", evt: "central.sync.error");
            }
        }
    }
}
